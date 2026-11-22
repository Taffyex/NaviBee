using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MusicBeePlugin
{
    public partial class Plugin
    {
        private static int _isLinkingActive = 0;

        public void ForceCacheAndLinkAllArtwork(bool showDialog = true)
        {
            if (Interlocked.CompareExchange(ref _isLinkingActive, 1, 0) != 0)
            {
                if (showDialog)
                    ShowUiMessage("Artwork caching and linking is already in progress.", "Navidrome Artwork Cache", MessageBoxIcon.Information);
                return;
            }

            var ct = ShutdownToken;
            Task.Run(() =>
            {
                try
                {
                    if (ct.IsCancellationRequested) return;
                    var songs = NavidromeCatalog.Instance.GetAllSongs();
                    if (songs.Count == 0)
                    {
                        if (showDialog)
                            ShowUiMessage("Please ensure the Navidrome catalog is synced before caching artwork.", "Navidrome Artwork Cache", MessageBoxIcon.Information);
                        return;
                    }

                    Api.MB_SetBackgroundTaskMessage?.Invoke("Navidrome: Verifying album artwork cache...");

                    var distinctCovers = NavidromeCatalog.Instance.GetDistinctAlbumCoverIds();
                    int targetSize = Settings != null ? Settings.ArtworkSize : 800;
                    var missing = new List<string>();

                    foreach (var cId in distinctCovers)
                    {
                        if (ArtworkCache.Instance.GetLocalFilePathIfExists(cId, targetSize) == null)
                            missing.Add(cId);
                    }

                    if (missing.Count > 0 && Subsonic != null)
                    {
                        Api.MB_SetBackgroundTaskMessage?.Invoke($"Navidrome: Downloading {missing.Count} missing album covers...");
                        int done = 0;
                        int totalMissing = missing.Count;

                        Parallel.ForEach(missing, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, cId =>
                        {
                            if (ct.IsCancellationRequested) return;
                            ArtworkCache.Instance.GetOrFetchLocalFilePath(cId, Subsonic, targetSize);
                            int c = Interlocked.Increment(ref done);
                            if (c % 10 == 0 || c == totalMissing)
                                Api.MB_SetBackgroundTaskMessage?.Invoke($"Navidrome: Caching missing artwork ({c}/{totalMissing})...");
                        });
                    }

                    if (ct.IsCancellationRequested) return;

                    int linked = 0;
                    if (MusicBeeArtworkBridge.IsAvailable)
                    {
                        var linkList = new List<KeyValuePair<string, string>>(songs.Count);
                        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                        foreach (var s in songs)
                        {
                            string rel = !string.IsNullOrEmpty(s.CachedRelativePath) ? s.CachedRelativePath : SubsonicUrlHelper.BuildRelativePath(s);
                            string url = "Navidrome:\\" + rel;
                            if (!seenUrls.Add(url)) continue;

                            string coverId = !string.IsNullOrEmpty(s.CanonicalCoverId) ? s.CanonicalCoverId : s.CoverArt;
                            if (string.IsNullOrEmpty(coverId))
                                coverId = NavidromeCatalog.Instance.ResolveCoverArtId(url);

                            if (!string.IsNullOrEmpty(coverId))
                            {
                                string localPath = ArtworkCache.Instance.GetLocalFilePathIfExists(coverId, targetSize);
                                if (localPath != null)
                                    linkList.Add(new KeyValuePair<string, string>(url, localPath));
                            }
                        }

                        if (Api.Library_QueryFilesEx != null && Api.Library_QueryFilesEx("Navidrome:", out var libFiles) && libFiles != null)
                        {
                            foreach (var f in libFiles)
                            {
                                if (!string.IsNullOrEmpty(f) && seenUrls.Add(f))
                                {
                                    string cId = NavidromeCatalog.Instance.ResolveCoverArtId(f);
                                    if (!string.IsNullOrEmpty(cId))
                                    {
                                        string localPath = ArtworkCache.Instance.GetLocalFilePathIfExists(cId, targetSize);
                                        if (localPath != null)
                                            linkList.Add(new KeyValuePair<string, string>(f, localPath));
                                    }
                                }
                            }
                        }

                        Api.MB_SetBackgroundTaskMessage?.Invoke($"Navidrome: Linking {linkList.Count} track covers...");
                        linked = MusicBeeArtworkBridge.AttachArtworkBatch(linkList, (cur, tot) =>
                        {
                            Api.MB_SetBackgroundTaskMessage?.Invoke($"Navidrome: Linking artwork ({cur}/{tot})...");
                        });
                    }

                    Api.MB_RefreshPanels?.Invoke();

                    string summary = MusicBeeArtworkBridge.IsAvailable
                        ? $"Navidrome: Cached {distinctCovers.Count} covers and linked {linked} tracks."
                        : $"Navidrome: Cached {distinctCovers.Count} album covers to disk.";
                    Api.MB_SetBackgroundTaskMessage?.Invoke(summary);

                    if (showDialog)
                        ShowUiMessage(summary, "Navidrome Artwork Cache", MessageBoxIcon.Information);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Api.MB_SetBackgroundTaskMessage?.Invoke($"Navidrome artwork cache error: {ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref _isLinkingActive, 0);
                }
            });
        }
    }
}
