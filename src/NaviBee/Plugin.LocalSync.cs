using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MusicBeePlugin
{
    public partial class Plugin
    {
        private static int _isLocalSyncActive;
        private static int _suppressTagChangeNotifications;
        private static readonly SemaphoreSlim _localLinkGate = new SemaphoreSlim(1, 1);

        internal static List<string> QueryLibraryVirtualNavidromeFiles()
        {
            var result = new List<string>();
            if (Api.Library_QueryFilesEx == null) return result;
            if (!Api.Library_QueryFilesEx("domain=Library", out var files) || files == null) return result;
            for (int i = 0; i < files.Length; i++)
            {
                if (LocalLibraryLinker.IsVirtualNavidromeUrl(files[i]))
                    result.Add(files[i]);
            }
            return result;
        }

        internal static int ParseLeadingInt(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return 0;
            string s = raw.Trim();
            int slash = s.IndexOf('/');
            if (slash > 0) s = s.Substring(0, slash).Trim();
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int val) ? val : 0;
        }

        internal static int ParseDurationSeconds(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return 0;
            string s = raw.Trim();
            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long num))
            {
                if (num <= 0) return 0;
                if (num >= 10_000_000L) return (int)(num / 10_000_000L);
                if (num >= 10_000L) return (int)(num / 1000L);
                return (int)num;
            }

            string[] parts = s.Split(':');
            if (parts.Length == 2 &&
                int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int m2) &&
                double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out double s2))
                return Math.Max(0, m2 * 60 + (int)Math.Round(s2));
            if (parts.Length == 3 &&
                int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int h3) &&
                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int m3) &&
                double.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out double s3))
                return Math.Max(0, h3 * 3600 + m3 * 60 + (int)Math.Round(s3));
            return 0;
        }

        internal static DateTime ParseLibraryLastPlayedUtc(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return DateTime.MinValue;
            string s = raw.Trim();
            if (s == "0") return DateTime.MinValue;
            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks) && ticks > 0)
            {
                try { return new DateTime(ticks, DateTimeKind.Local).ToUniversalTime(); }
                catch { return DateTime.MinValue; }
            }
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var inv))
                return inv.ToUniversalTime();
            if (DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var loc))
                return loc.ToUniversalTime();
            return DateTime.MinValue;
        }

        internal static LocalTrackInfo ReadLocalTrackInfo(string localUrl, MetaDataType pidField)
        {
            var info = new LocalTrackInfo { Url = localUrl };
            if (string.IsNullOrEmpty(localUrl)) return info;
            try
            {
                bool readBatch = false;
                if (Api.Library_GetFileTags != null)
                {
                    var fields = new[]
                    {
                        pidField,
                        MetaDataType.Artist,
                        MetaDataType.AlbumArtist,
                        MetaDataType.PrimaryArtist,
                        MetaDataType.Album,
                        MetaDataType.TrackTitle,
                        MetaDataType.DiscNo,
                        MetaDataType.TrackNo
                    };
                    if (Api.Library_GetFileTags(localUrl, fields, out var values) && values != null && values.Length == 8)
                    {
                        info.PidTag = values[0] ?? "";
                        info.Artist = values[1] ?? "";
                        info.AlbumArtist = values[2] ?? "";
                        info.PrimaryArtist = values[3] ?? "";
                        info.Album = values[4] ?? "";
                        info.Title = values[5] ?? "";
                        info.DiscNo = ParseLeadingInt(values[6]);
                        info.TrackNo = ParseLeadingInt(values[7]);
                        readBatch = true;
                    }
                }

                if (!readBatch && Api.Library_GetFileTag != null)
                {
                    info.PidTag = Api.Library_GetFileTag(localUrl, pidField) ?? "";
                    info.Artist = Api.Library_GetFileTag(localUrl, MetaDataType.Artist) ?? "";
                    info.AlbumArtist = Api.Library_GetFileTag(localUrl, MetaDataType.AlbumArtist) ?? "";
                    info.PrimaryArtist = Api.Library_GetFileTag(localUrl, MetaDataType.PrimaryArtist) ?? "";
                    info.Album = Api.Library_GetFileTag(localUrl, MetaDataType.Album) ?? "";
                    info.Title = Api.Library_GetFileTag(localUrl, MetaDataType.TrackTitle) ?? "";
                    info.DiscNo = ParseLeadingInt(Api.Library_GetFileTag(localUrl, MetaDataType.DiscNo));
                    info.TrackNo = ParseLeadingInt(Api.Library_GetFileTag(localUrl, MetaDataType.TrackNo));
                }

                if (Api.Library_GetFileProperty != null)
                    info.DurationSeconds = ParseDurationSeconds(Api.Library_GetFileProperty(localUrl, FilePropertyType.Duration));
            }
            catch { }
            return info;
        }

        internal LocalLinkSummary LinkLocalLibraryInternal(bool allowEmbedPid, CancellationToken ct, Action<string> progressCallback = null)
        {
            var empty = new LocalLinkSummary();
            if (Api.Library_QueryFilesEx == null || !NavidromeCatalog.Instance.HasData) return empty;

            try { _localLinkGate.Wait(ct); }
            catch (OperationCanceledException) { return empty; }

            try
            {
                if (!Api.Library_QueryFilesEx("domain=Library", out var files) || files == null || files.Length == 0)
                    return empty;

                var settings = Settings ?? new NavidromeSettings();
                MetaDataType pidField = settings.GetPidMetaDataType();
                var localInfos = new List<LocalTrackInfo>(files.Length);

                for (int i = 0; i < files.Length; i++)
                {
                    if (ct.IsCancellationRequested) return empty;
                    string f = files[i];
                    if (!string.IsNullOrWhiteSpace(f) && !LocalLibraryLinker.IsVirtualNavidromeUrl(f))
                        localInfos.Add(ReadLocalTrackInfo(f, pidField));
                }

                Func<string, MetaDataType, string, bool> writePid = null;
                if (allowEmbedPid && Api.Library_SetFileTag != null && Api.Library_CommitTagsToFile != null)
                {
                    writePid = (url, field, songId) =>
                    {
                        Interlocked.Increment(ref _suppressTagChangeNotifications);
                        try
                        {
                            return Api.Library_SetFileTag(url, field, songId) &&
                                   Api.Library_CommitTagsToFile(url);
                        }
                        finally
                        {
                            Interlocked.Decrement(ref _suppressTagChangeNotifications);
                        }
                    };
                }

                var effectiveSettings = allowEmbedPid == settings.EmbedNavidromeIdInLocalTag
                    ? settings
                    : new NavidromeSettings
                    {
                        LinkLocalLibraryTracks = settings.LinkLocalLibraryTracks,
                        EmbedNavidromeIdInLocalTag = allowEmbedPid,
                        LocalIdTagField = settings.LocalIdTagField
                    };

                return LocalLibraryLinker.Instance.LinkLocalTracks(
                    localInfos,
                    NavidromeCatalog.Instance,
                    effectiveSettings,
                    writePid,
                    pruneMissingLocalPaths: true,
                    progressCallback: (cur, tot) =>
                    {
                        string msg = $"Navidrome: Linking local tracks ({cur:N0} / {tot:N0})...";
                        progressCallback?.Invoke(msg);
                        Api.MB_SetBackgroundTaskMessage?.Invoke(msg);
                    },
                    ct: ct);
            }
            finally
            {
                _localLinkGate.Release();
            }
        }

        public void LinkAndSyncLocalLibrary(
            bool showDialog = true,
            bool pushLocalRatingsOnLink = true,
            Action<string> statusCallback = null,
            Action onComplete = null,
            bool requireConfirmation = false,
            bool? overrideEmbedPid = null)
        {
            if (requireConfirmation)
            {
                bool willEmbed = overrideEmbedPid ?? (Settings?.EmbedNavidromeIdInLocalTag ?? true);
                string embedNote = willEmbed
                    ? "Matched tracks will also have their Navidrome ID written to the configured tag field."
                    : "Links will be saved to local_links.json without modifying audio file tags.";
                if (!AskUiConfirmation(
                    $"Scan your local MusicBee library, match tracks against Navidrome, and run a 2-way sync of stars, ratings, and playcounts?\n\n{embedNote}",
                    "Link Local Library & Sync State"))
                {
                    onComplete?.Invoke();
                    return;
                }
            }

            if (Interlocked.CompareExchange(ref _isLocalSyncActive, 1, 0) != 0)
            {
                if (showDialog) ShowUiMessage("Local library linking and sync is already in progress.", "Navidrome Local Link", MessageBoxIcon.Information);
                onComplete?.Invoke();
                return;
            }

            var ct = ShutdownToken;
            Task.Run(() =>
            {
                try
                {
                    void Report(string msg) { statusCallback?.Invoke(msg); Api.MB_SetBackgroundTaskMessage?.Invoke(msg); }

                    if (!NavidromeCatalog.Instance.HasData)
                    {
                        string msg = "Please connect and sync the Navidrome catalog before linking local files.";
                        Report(msg);
                        if (showDialog) ShowUiMessage(msg, "Navidrome Local Link", MessageBoxIcon.Information);
                        return;
                    }

                    Report("Navidrome: Refreshing server ratings & playcounts...");
                    NavidromeCatalog.Instance.RefreshUserState(Subsonic, ct);
                    if (ct.IsCancellationRequested) return;

                    Report("Navidrome: Matching local library tracks to server...");
                    bool allowEmbed = overrideEmbedPid ?? (Settings?.EmbedNavidromeIdInLocalTag ?? true);
                    var linkRes = LinkLocalLibraryInternal(allowEmbedPid: allowEmbed, ct, statusCallback);
                    if (ct.IsCancellationRequested) return;

                    Report($"Navidrome: Syncing stars, ratings & playcounts across {linkRes.Linked:N0} linked tracks...");
                    var syncRes = SyncStateToMusicBeeLibraryCore(pushLocalRatingsToServer: pushLocalRatingsOnLink, ct);
                    if (ct.IsCancellationRequested) return;

                    Api.MB_RefreshPanels?.Invoke();

                    int catCount = NavidromeCatalog.Instance.SongCount;
                    int srvCount = NavidromeCatalog.Instance.ServerSongCount;
                    string catInfo = srvCount > 0 ? $"{catCount:N0} / {srvCount:N0} server tracks" : $"{catCount:N0} tracks";
                    string failNote = linkRes.PidEmbedFailed > 0 ? $", {linkRes.PidEmbedFailed:N0} tag writes failed" : "";
                    string shortSummary = $"Navidrome: Linked {linkRes.Linked:N0} of {linkRes.Scanned:N0} local tracks (Catalog: {catInfo}; PID: {linkRes.MatchedByPid:N0}, Path: {linkRes.MatchedByPath:N0}, Tags: {linkRes.MatchedByMetadata:N0}).";
                    string dialogSummary =
                        $"Catalog: {catInfo}\n" +
                        $"Linked: {linkRes.Linked:N0} of {linkRes.Scanned:N0} local tracks ({linkRes.Unmatched:N0} unmatched)\n" +
                        $"Match tiers: PID {linkRes.MatchedByPid:N0} | Path {linkRes.MatchedByPath:N0} | Tags {linkRes.MatchedByMetadata:N0}\n" +
                        $"Embedded IDs: {linkRes.PidEmbedded:N0}{failNote}\n" +
                        $"State sync: updated {syncRes.LibraryTracksUpdated:N0} tracks, pulled {syncRes.PlayCountsUpdated:N0} playcounts, pushed {syncRes.RatingsOrStarsPushed:N0} ratings/stars.";
                    if (linkRes.Unmatched > 0 && !string.IsNullOrEmpty(linkRes.UnmatchedLogPath))
                        dialogSummary += $"\n\nUnmatched track diagnostics saved to:\n{linkRes.UnmatchedLogPath}";

                    Report(shortSummary);
                    if (showDialog) ShowUiMessage(dialogSummary, "Navidrome Local Library Link & Sync", MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    string err = $"Navidrome local link error: {FriendlyErrors.FormatException(ex)}";
                    statusCallback?.Invoke(err);
                    Api.MB_SetBackgroundTaskMessage?.Invoke(err);
                }
                finally
                {
                    Interlocked.Exchange(ref _isLocalSyncActive, 0);
                    onComplete?.Invoke();
                }
            }, ct);
        }
    }
}
