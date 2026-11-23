using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MusicBeePlugin
{
    public partial class Plugin
    {
        private static readonly object _lifecycleLock = new object();
        private static CancellationTokenSource _shutdownCts = new CancellationTokenSource();
        private static volatile bool _storageReadySent;
        private static int _isImportActive;

        internal static CancellationToken ShutdownToken
        {
            get
            {
                lock (_lifecycleLock)
                {
                    return _shutdownCts != null ? _shutdownCts.Token : CancellationToken.None;
                }
            }
        }

        internal static void ResetShutdownToken()
        {
            lock (_lifecycleLock)
            {
                try { _shutdownCts?.Dispose(); } catch { }
                _shutdownCts = new CancellationTokenSource();
                _storageReadySent = false;
            }
        }

        internal static void CancelBackgroundWork()
        {
            lock (_lifecycleLock)
            {
                try { _shutdownCts?.Cancel(); } catch { }
            }
            DisposePlaylistDebounceTimers();
            ArtworkCache.Instance.StopPrewarm();
            AudioStreamCache.Instance.StopBackgroundDownloads();
        }

        private void OnPluginStartup()
        {
            if (Settings == null || string.IsNullOrWhiteSpace(Settings.ServerUrl) || string.IsNullOrWhiteSpace(Settings.Username))
            {
                Api.MB_SetBackgroundTaskMessage?.Invoke("Navidrome: Open Plugin Settings to configure server URL and credentials.");
                return;
            }

            // Register the "Navidrome:\" storage device immediately when a local catalog exists so
            // MusicBee does not report "File not found" while the background server ping is in flight.
            if (NavidromeCatalog.Instance.HasData && !_storageReadySent)
            {
                _storageReadySent = true;
                Api.MB_SendNotification?.Invoke(CallbackType.StorageReady);
            }

            StartConnectAndSync(isInitialConfigure: false);
        }

        private void StartConnectAndSync(bool isInitialConfigure)
        {
            CancellationToken ct = ShutdownToken;
            Task.Run(() =>
            {
                if (ct.IsCancellationRequested) return;

                var ping = Subsonic.PingWithInfo();
                if (ct.IsCancellationRequested) return;

                SetConnectionReady(ping.Ok);
                if (!ping.Ok)
                {
                    string reason = FriendlyErrors.FormatException(Subsonic.LastError);
                    if (NavidromeCatalog.Instance.HasData)
                    {
                        if (!_storageReadySent)
                        {
                            _storageReadySent = true;
                            Api.MB_SendNotification?.Invoke(CallbackType.StorageReady);
                        }
                        Api.MB_SetBackgroundTaskMessage?.Invoke($"Navidrome offline ({reason}): playing from cache.");
                    }
                    else
                    {
                        Api.MB_SendNotification?.Invoke(CallbackType.StorageFailed);
                        Api.MB_SetBackgroundTaskMessage?.Invoke($"Navidrome: {reason}");
                    }
                    return;
                }

                if (!_storageReadySent || isInitialConfigure)
                {
                    _storageReadySent = true;
                    Api.MB_SendNotification?.Invoke(CallbackType.StorageReady);
                }

                string ver = !string.IsNullOrWhiteSpace(ping.ServerVersion) ? $" ({ping.ServerVersion})" : "";
                Api.MB_SetBackgroundTaskMessage?.Invoke($"Navidrome connected{ver}: ready for streaming & sync.");
                WarmPlaylistLookup();
                if (ct.IsCancellationRequested) return;

                bool serverChanged = NavidromeCatalog.Instance.IsServerChanged(Subsonic);
                if (serverChanged)
                {
                    ResetSyncTracking();
                    PendingSyncQueue.Instance.Clear();
                    LocalLibraryLinker.Instance.Clear(persist: true);
                    PlaylistLinkStore.Instance.Clear();
                    ArtworkCache.Instance.ClearDiskCache();
                    AudioStreamCache.Instance.ClearCache();
                    LyricsCache.Instance.ClearCache();
                }
                else
                {
                    PendingSyncQueue.Instance.FlushPending(Subsonic, ct);
                }

                bool forceFull = !NavidromeCatalog.Instance.HasData || serverChanged;
                bool catalogChanged = false;
                bool synced = forceFull
                    ? (catalogChanged = NavidromeCatalog.Instance.SyncFromServer(
                        Subsonic,
                        msg => Api.MB_SetBackgroundTaskMessage?.Invoke(msg),
                        null,
                        Settings.ArtworkSize,
                        ct,
                        startPrewarm: false))
                    : NavidromeCatalog.Instance.SmartSyncIfNeeded(
                        Subsonic,
                        Settings.ArtworkSize,
                        msg => Api.MB_SetBackgroundTaskMessage?.Invoke(msg),
                        out catalogChanged,
                        ct,
                        startPrewarm: !isInitialConfigure);

                if (ct.IsCancellationRequested) return;

                if (!forceFull && synced && !catalogChanged)
                {
                    if (NavidromeCatalog.Instance.RefreshUserState(Subsonic, ct))
                        catalogChanged = true;
                }

                if (ct.IsCancellationRequested) return;

                int newlyLinked = 0;
                if (Settings.LinkLocalLibraryTracks && NavidromeCatalog.Instance.HasData)
                {
                    var linkRes = LinkLocalLibraryInternal(allowEmbedPid: Settings.EmbedNavidromeIdInLocalTag, ct);
                    newlyLinked = linkRes.NewlyLinked;
                }

                if (ct.IsCancellationRequested) return;

                bool shouldSyncState = (catalogChanged || isInitialConfigure || newlyLinked > 0) &&
                                       (Settings.SyncServerStarsToLibrary || Settings.SyncPlayCountsToLibrary);
                if (shouldSyncState)
                    SyncStateToMusicBeeLibrary(pushLocalRatingsToServer: isInitialConfigure);

                if (isInitialConfigure)
                    Refresh();

                if (synced && (catalogChanged || isInitialConfigure))
                    ForceCacheAndLinkAllArtwork(false);
                else
                    Api.MB_RefreshPanels?.Invoke();
            }, ct);
        }

        public static List<SubsonicSong> FilterMissingImportSongs(
            IReadOnlyList<SubsonicSong> catalogSongs,
            LocalLibraryLinker linker,
            ISet<string> existingVirtualUrls,
            bool skipDuplicates,
            out int skippedLocal,
            out int skippedAlreadyImported)
        {
            skippedLocal = 0;
            skippedAlreadyImported = 0;
            var toImport = new List<SubsonicSong>();
            if (catalogSongs == null || catalogSongs.Count == 0)
                return toImport;

            for (int i = 0; i < catalogSongs.Count; i++)
            {
                var s = catalogSongs[i];
                if (s == null) continue;

                if (skipDuplicates)
                {
                    if (linker != null && !string.IsNullOrEmpty(s.Id) && linker.IsSongLinkedToLocal(s.Id))
                    {
                        skippedLocal++;
                        continue;
                    }
                    string rel = !string.IsNullOrEmpty(s.CachedRelativePath) ? s.CachedRelativePath : SubsonicUrlHelper.BuildRelativePath(s);
                    string url = "Navidrome:\\" + rel;
                    if (existingVirtualUrls != null && existingVirtualUrls.Contains(url))
                    {
                        skippedAlreadyImported++;
                        continue;
                    }
                }

                toImport.Add(s);
            }
            return toImport;
        }

        private void ImportAllToLibrary(bool requireConfirmation = false)
        {
            if (Api.Library_AddFileToLibrary == null) return;

            if (requireConfirmation)
            {
                bool skipDupesPrompt = Settings == null || Settings.SkipDuplicatesOnImport;
                string dupeNote = skipDupesPrompt
                    ? "Tracks already linked to local files or already imported will be skipped."
                    : "Duplicate skipping is currently disabled in settings.";
                if (!AskUiConfirmation(
                    $"Import missing Navidrome tracks into your MusicBee Music Library?\n\n{dupeNote}",
                    "Import Missing Navidrome Tracks"))
                {
                    return;
                }
            }

            if (Interlocked.CompareExchange(ref _isImportActive, 1, 0) != 0)
            {
                ShowUiMessage("Importing Navidrome tracks is already in progress.", "Navidrome Import", MessageBoxIcon.Information);
                return;
            }

            var ct = ShutdownToken;
            Task.Run(() =>
            {
                try
                {
                    var songs = NavidromeCatalog.Instance.GetAllSongs();
                    if (songs.Count == 0)
                    {
                        ShowUiMessage("Please ensure the Navidrome catalog is synced before importing.",
                            "Navidrome Import", MessageBoxIcon.Information);
                        return;
                    }

                    bool skipDupes = Settings == null || Settings.SkipDuplicatesOnImport;
                    if (skipDupes && (Settings == null || Settings.LinkLocalLibraryTracks))
                    {
                        Api.MB_SetBackgroundTaskMessage?.Invoke("Navidrome: Matching existing local library tracks to prevent duplicates...");
                        LinkLocalLibraryInternal(allowEmbedPid: Settings?.EmbedNavidromeIdInLocalTag ?? true, ct);
                        if (ct.IsCancellationRequested) return;
                    }

                    var existingVirtual = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (skipDupes)
                    {
                        foreach (var f in QueryLibraryVirtualNavidromeFiles())
                            existingVirtual.Add(f);
                    }

                    var missingSongs = FilterMissingImportSongs(
                        songs,
                        LocalLibraryLinker.Instance,
                        existingVirtual,
                        skipDupes,
                        out int skippedLocal,
                        out int skippedExisting);

                    int total = missingSongs.Count;
                    int added = 0;
                    int failed = 0;

                    for (int i = 0; i < total; i++)
                    {
                        if (ct.IsCancellationRequested) return;
                        var s = missingSongs[i];
                        string rel = !string.IsNullOrEmpty(s.CachedRelativePath) ? s.CachedRelativePath : SubsonicUrlHelper.BuildRelativePath(s);
                        string url = "Navidrome:\\" + rel;
                        string addedUrl = Api.Library_AddFileToLibrary(url, LibraryCategory.Music);
                        if (!string.IsNullOrEmpty(addedUrl))
                            added++;
                        else
                            failed++;

                        int processed = i + 1;
                        if (processed % 200 == 0 || processed == total)
                            Api.MB_SetBackgroundTaskMessage?.Invoke($"Navidrome: Importing missing tracks ({processed:N0} / {total:N0})...");
                    }

                    if (ct.IsCancellationRequested) return;

                    if (Settings == null || Settings.SyncServerStarsToLibrary || Settings.SyncPlayCountsToLibrary)
                        SyncStateToMusicBeeLibraryCore(pushLocalRatingsToServer: false, ct);

                    Api.MB_RefreshPanels?.Invoke();
                    if (added > 0)
                        ForceCacheAndLinkAllArtwork(false);

                    string failedNote = failed > 0 ? $", {failed:N0} failed" : "";
                    string summary = added > 0
                        ? $"Navidrome: Imported {added:N0} missing tracks (skipped {skippedLocal:N0} linked local, {skippedExisting:N0} already imported{failedNote})."
                        : $"Navidrome: No missing tracks to import ({skippedLocal:N0} already linked to local library, {skippedExisting:N0} already imported{failedNote}).";
                    Api.MB_SetBackgroundTaskMessage?.Invoke(summary);
                }
                finally
                {
                    Interlocked.Exchange(ref _isImportActive, 0);
                }
            }, ct);
        }
    }
}
