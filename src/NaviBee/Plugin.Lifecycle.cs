using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MusicBeePlugin
{
    public partial class Plugin
    {
        internal static Plugin Instance;
        internal static MusicBeeApiInterface Api;
        internal static NavidromeSettings Settings;
        internal static SubsonicClient Subsonic;
        private static string SettingsFilePath;
        private static string PersistentCacheRoot;

        public PluginInfo Initialise(IntPtr apiInterfacePtr)
        {
            Api = default(MusicBeeApiInterface);
            Api.Initialise(apiInterfacePtr);
            Instance = this;
            ResetShutdownToken();
            ResetSyncTracking();
            MusicBeeArtworkBridge.Initialize(Api);
            MusicBeeLyricsBridge.Initialize(Api);

            string persistentPath = Api.Setting_GetPersistentStoragePath != null
                ? Api.Setting_GetPersistentStoragePath()
                : AppDomain.CurrentDomain.BaseDirectory;
            PersistentCacheRoot = persistentPath;

            NavidromeCatalog.Instance.ConfigureStorage(persistentPath);
            LocalLibraryLinker.Instance.ConfigureStorage(persistentPath);
            ArtworkCache.Instance.ConfigureStorage(persistentPath);
            AudioStreamCache.Instance.ConfigureStorage(persistentPath);
            LyricsCache.Instance.ConfigureStorage(persistentPath);
            PlaylistLinkStore.Instance.ConfigureStorage(persistentPath);
            PlaylistBackup.Instance.ConfigureStorage(persistentPath);

            SettingsFilePath = Path.Combine(persistentPath, "mb_NavidromePlugin.json");
            if (!File.Exists(SettingsFilePath))
            {
                string globalPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MusicBee", "mb_NavidromePlugin.json");
                if (File.Exists(globalPath))
                    SettingsFilePath = globalPath;
            }

            Settings = NavidromeSettings.Load(SettingsFilePath);
            Subsonic = new SubsonicClient(Settings);

            if (Api.MB_RegisterCommand != null)
            {
                Api.MB_RegisterCommand("Navidrome: Link Local Library Tracks & Sync State", (s, e) => LinkAndSyncLocalLibrary(showDialog: true, pushLocalRatingsOnLink: true));
                Api.MB_RegisterCommand("Navidrome: Resync Catalog & Artwork", (s, e) => ForceResync());
                Api.MB_RegisterCommand("Navidrome: Force Cache & Link All Artwork", (s, e) => ForceCacheAndLinkAllArtwork(true));
                Api.MB_RegisterCommand("Navidrome: Upgrade Cached Artwork to High-Res", (s, e) => UpgradeArtwork());
                Api.MB_RegisterCommand("Navidrome: Clear Audio & Artwork Cache", (s, e) => ClearAllCache());
                Api.MB_RegisterCommand("Navidrome: Import All Tracks into Music Library", (s, e) => ImportAllToLibrary());
            }

            if (Api.MB_AddMenuItem != null)
            {
                Api.MB_AddMenuItem("mnuTools/Navidrome: Link Local Library Tracks & Sync State", "Navidrome: Link Local Library", (s, e) => LinkAndSyncLocalLibrary(showDialog: true, pushLocalRatingsOnLink: true));
                Api.MB_AddMenuItem("mnuTools/Navidrome: Resync Catalog & Artwork", "Navidrome: Resync Catalog", (s, e) => ForceResync());
                Api.MB_AddMenuItem("mnuTools/Navidrome: Import Missing Tracks into Music Library", "Navidrome: Import Missing Tracks", (s, e) => ImportAllToLibrary());
                Api.MB_AddMenuItem("mnuTools/Navidrome: Force Cache & Link All Artwork", "Navidrome: Cache & Link Artwork", (s, e) => ForceCacheAndLinkAllArtwork(true));
                Api.MB_AddMenuItem("mnuTools/Navidrome: Upgrade Cached Artwork to High-Res", "Navidrome: Upgrade Artwork", (s, e) => UpgradeArtwork());
                Api.MB_AddMenuItem("mnuTools/Navidrome: Clear Audio & Artwork Cache", "Navidrome: Clear Cache", (s, e) => ClearAllCache());
            }

            NavidromeCatalog.Instance.LoadFromDisk();

            return new PluginInfo
            {
                PluginInfoVersion = MusicBeeApiInfo.PluginInfoVersion,
                Type = PluginType.Storage,
                Name = "Navidrome Storage & Sync",
                Description = "Navidrome & Subsonic cloud library streaming, audio/artwork caching, scrobbling, and playlist sync",
                Author = "TaffyEx",
                TargetApplication = "Navidrome",
                VersionMajor = 1,
                VersionMinor = 1,
                Revision = 0,
                MinInterfaceVersion = MusicBeeApiInfo.MinInterfaceVersion,
                MinApiRevision = MusicBeeApiInfo.MinApiRevision,
                ReceiveNotifications =
                    ReceiveNotificationFlags.StartupOnly |
                    ReceiveNotificationFlags.PlayerEvents |
                    ReceiveNotificationFlags.TagEvents,
                ConfigurationPanelHeight = 0
            };
        }

        public bool Configure(IntPtr panelHandle)
        {
            using (var form = new SettingsWindow(Settings, savedSettings =>
            {
                Settings = savedSettings;
                Settings.Save(SettingsFilePath);
                Subsonic.UpdateSettings(Settings);
                if (string.IsNullOrWhiteSpace(Settings.ServerUrl) || string.IsNullOrWhiteSpace(Settings.Username))
                {
                    SetConnectionReady(false);
                    return;
                }

                StartConnectAndSync(isInitialConfigure: true);
            }))
            {
                IWin32Window owner = GetMainOwnerWindow();
                return (owner != null ? form.ShowDialog(owner) : form.ShowDialog()) == DialogResult.OK;
            }
        }

        public void Close(PluginCloseReason reason)
        {
            CancelBackgroundWork();
            NavidromeCatalog.Instance.FlushPendingSave();
            _storageIcon?.Dispose();
            _storageIcon = null;
            Instance = null;
        }

        internal static Form GetMainOwnerWindow()
        {
            try
            {
                if (Application.OpenForms.Count > 0)
                {
                    Form f = Application.OpenForms[0];
                    if (f != null && !f.IsDisposed && f.IsHandleCreated)
                        return f;
                }
            }
            catch { }
            return null;
        }

        internal static void ShowUiMessage(string text, string caption, MessageBoxIcon icon)
        {
            try
            {
                Form owner = GetMainOwnerWindow();
                if (owner != null)
                {
                    if (owner.InvokeRequired)
                        owner.BeginInvoke((Action)(() => MessageBox.Show(owner, text, caption, MessageBoxButtons.OK, icon)));
                    else
                        MessageBox.Show(owner, text, caption, MessageBoxButtons.OK, icon);
                    return;
                }
            }
            catch { }
            Api.MB_SetBackgroundTaskMessage?.Invoke($"{caption}: {text}");
        }

        internal static bool AskUiConfirmation(string text, string caption)
        {
            try
            {
                Form owner = GetMainOwnerWindow();
                if (owner != null)
                {
                    if (owner.InvokeRequired)
                    {
                        DialogResult res = DialogResult.No;
                        owner.Invoke((Action)(() =>
                        {
                            res = MessageBox.Show(owner, text, caption, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                        }));
                        return res == DialogResult.Yes;
                    }
                    return MessageBox.Show(owner, text, caption, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
                }
                return MessageBox.Show(text, caption, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
            }
            catch
            {
                return false;
            }
        }

        private void ForceResync()
        {
            var ct = ShutdownToken;
            Task.Run(() =>
            {
                Api.MB_SetBackgroundTaskMessage?.Invoke("Navidrome: Starting full catalog and artwork sync...");
                bool ok = NavidromeCatalog.Instance.SyncFromServer(Subsonic, msg =>
                {
                    Api.MB_SetBackgroundTaskMessage?.Invoke(msg);
                }, null, Settings?.ArtworkSize ?? 800, ct);

                if (ct.IsCancellationRequested) return;

                if (ok)
                {
                    SetConnectionReady(true);
                    WarmPlaylistLookup();
                    if (Settings != null && Settings.LinkLocalLibraryTracks)
                        LinkLocalLibraryInternal(allowEmbedPid: Settings.EmbedNavidromeIdInLocalTag, ct);
                    if (Settings != null && (Settings.SyncServerStarsToLibrary || Settings.SyncPlayCountsToLibrary))
                        SyncStateToMusicBeeLibraryCore(pushLocalRatingsToServer: false, ct);
                    Refresh();
                    ForceCacheAndLinkAllArtwork(false);
                    Api.MB_SetBackgroundTaskMessage?.Invoke($"Navidrome: Resynced {NavidromeCatalog.Instance.SongCount:N0} tracks.");
                }
                else
                {
                    string reason = FriendlyErrors.FormatException(Subsonic?.LastError);
                    ShowUiMessage($"Could not resync catalog: {reason}", "Navidrome Resync", MessageBoxIcon.Warning);
                }
            }, ct);
        }

        private void UpgradeArtwork()
        {
            var ct = ShutdownToken;
            Task.Run(() =>
            {
                var distinct = NavidromeCatalog.Instance.GetDistinctAlbumCoverIds();
                if (distinct.Count == 0)
                {
                    ShowUiMessage("Please ensure the Navidrome catalog is loaded before upgrading artwork.",
                        "Artwork Upgrade", MessageBoxIcon.Information);
                    return;
                }

                ArtworkCache.Instance.UpgradeAndRecacheArtwork(distinct, Subsonic, Settings.ArtworkSize, msg =>
                {
                    Api.MB_SetBackgroundTaskMessage?.Invoke(msg);
                }, () => ForceCacheAndLinkAllArtwork(false));
            }, ct);
        }

        private void ClearAllCache()
        {
            ArtworkCache.Instance.GetCacheStats(out _, out long artBytes);
            AudioStreamCache.Instance.GetCacheStats(out _, out long audioBytes);
            LyricsCache.Instance.GetCacheStats(out _, out long lyricsBytes);
            long totalBytes = artBytes + audioBytes + lyricsBytes;

            if (!AskUiConfirmation(
                $"Delete {FriendlyErrors.FormatBytes(totalBytes)} of cached audio, artwork, and lyrics?\n\nPlaylist backups will not be deleted.",
                "Clear Navidrome Cache"))
            {
                return;
            }

            ArtworkCache.Instance.ClearDiskCache();
            AudioStreamCache.Instance.ClearCache();
            LyricsCache.Instance.ClearCache();
            Refresh();
            Api.MB_SetBackgroundTaskMessage?.Invoke("Navidrome: Audio, artwork, and lyrics caches cleared.");
        }

        public void Uninstall()
        {
            CancelBackgroundWork();
            try
            {
                if (!string.IsNullOrEmpty(SettingsFilePath) && File.Exists(SettingsFilePath))
                    File.Delete(SettingsFilePath);
            }
            catch { }

            try
            {
                if (!string.IsNullOrEmpty(PersistentCacheRoot))
                {
                    string cacheDir = Path.Combine(PersistentCacheRoot, "NavidromeCache");
                    if (Directory.Exists(cacheDir))
                        Directory.Delete(cacheDir, recursive: true);
                }
            }
            catch { }
        }

        public void ReceiveNotification(string sourceFileUrl, NotificationType type)
        {
            switch (type)
            {
                case NotificationType.PluginStartup:
                    OnPluginStartup();
                    break;
                case NotificationType.SelectedFileChanged:
                    OnSelectedFileChanged(sourceFileUrl);
                    break;
                case NotificationType.TrackChanged:
                    OnTrackChanged(sourceFileUrl, isPlayStateEvent: false);
                    break;
                case NotificationType.PlayStateChanged:
                    OnTrackChanged(sourceFileUrl, isPlayStateEvent: true);
                    break;
                case NotificationType.PlayCountersChanged:
                    OnPlayCountersChanged(sourceFileUrl);
                    break;
                case NotificationType.TagsChanged:
                case NotificationType.RatingChanged:
                    OnTagsChanged(sourceFileUrl, type);
                    break;
                case NotificationType.PlaylistCreated:
                case NotificationType.PlaylistUpdated:
                case NotificationType.PlaylistDeleted:
                case NotificationType.PlaylistMoved:
                    OnPlaylistNotification(sourceFileUrl, type);
                    break;
            }
        }
    }
}
