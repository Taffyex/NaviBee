using System;
using System.Threading;
using System.Threading.Tasks;

namespace MusicBeePlugin
{
    public partial class Plugin
    {
        private static readonly object _lifecycleLock = new object();
        private static CancellationTokenSource _shutdownCts = new CancellationTokenSource();
        private static volatile bool _storageReadySent;

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

                bool forceFull = !NavidromeCatalog.Instance.HasData || NavidromeCatalog.Instance.IsServerChanged(Subsonic);
                bool catalogChanged = false;
                bool synced = forceFull
                    ? (catalogChanged = NavidromeCatalog.Instance.SyncFromServer(
                        Subsonic,
                        msg => Api.MB_SetBackgroundTaskMessage?.Invoke(msg),
                        null,
                        Settings.ArtworkSize,
                        ct))
                    : NavidromeCatalog.Instance.SmartSyncIfNeeded(
                        Subsonic,
                        Settings.ArtworkSize,
                        msg => Api.MB_SetBackgroundTaskMessage?.Invoke(msg),
                        out catalogChanged,
                        ct);

                if (ct.IsCancellationRequested) return;

                if (!forceFull && synced && !catalogChanged)
                {
                    if (NavidromeCatalog.Instance.RefreshUserState(Subsonic, ct))
                        catalogChanged = true;
                }

                if (ct.IsCancellationRequested) return;

                if (catalogChanged && Settings.SyncServerStarsToLibrary)
                    SyncStarsToMusicBeeLibrary();

                if (isInitialConfigure)
                    Refresh();

                if (synced && (catalogChanged || isInitialConfigure))
                    ForceCacheAndLinkAllArtwork(false);
                else
                    Api.MB_RefreshPanels?.Invoke();
            }, ct);
        }
    }
}
