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

        public PluginInfo Initialise(IntPtr apiInterfacePtr)
        {
            Api = default(MusicBeeApiInterface);
            Api.Initialise(apiInterfacePtr);
            Instance = this;
            MusicBeeArtworkBridge.Initialize(Api);
            MusicBeeLyricsBridge.Initialize(Api);

            string persistentPath = Api.Setting_GetPersistentStoragePath != null
                ? Api.Setting_GetPersistentStoragePath()
                : AppDomain.CurrentDomain.BaseDirectory;

            NavidromeCatalog.Instance.ConfigureStorage(persistentPath);
            ArtworkCache.Instance.ConfigureStorage(persistentPath);
            AudioStreamCache.Instance.ConfigureStorage(persistentPath);
            LyricsCache.Instance.ConfigureStorage(persistentPath);

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
                Api.MB_RegisterCommand("Navidrome: Resync Catalog & Artwork", (s, e) => ForceResync());
                Api.MB_RegisterCommand("Navidrome: Force Cache & Link All Artwork", (s, e) => ForceCacheAndLinkAllArtwork(true));
                Api.MB_RegisterCommand("Navidrome: Upgrade Cached Artwork to High-Res", (s, e) => UpgradeArtwork());
                Api.MB_RegisterCommand("Navidrome: Clear Audio & Artwork Cache", (s, e) => ClearAllCache());
                Api.MB_RegisterCommand("Navidrome: Import All Tracks into Music Library", (s, e) => ImportAllToLibrary());
            }

            NavidromeCatalog.Instance.LoadFromDisk();

            if (!string.IsNullOrWhiteSpace(Settings.ServerUrl) && !string.IsNullOrWhiteSpace(Settings.Username))
                StartConnectAndSync(isInitialConfigure: false);
            else
                Api.MB_SetBackgroundTaskMessage?.Invoke("Navidrome: Open Plugin Settings to configure server URL and credentials.");

            return new PluginInfo
            {
                PluginInfoVersion = MusicBeeApiInfo.PluginInfoVersion,
                Type = PluginType.Storage,
                Name = "Navidrome Storage & Sync",
                Description = "Navidrome & Subsonic cloud library streaming, audio/artwork caching, scrobbling, and playlist sync",
                Author = "TaffyEx",
                TargetApplication = "Navidrome",
                VersionMajor = 1,
                VersionMinor = 0,
                Revision = 1,
                MinInterfaceVersion = MusicBeeApiInfo.MinInterfaceVersion,
                MinApiRevision = MusicBeeApiInfo.MinApiRevision,
                ReceiveNotifications =
                    ReceiveNotificationFlags.PlayerEvents |
                    ReceiveNotificationFlags.TagEvents,
                ConfigurationPanelHeight = 0
            };
        }

        private void StartConnectAndSync(bool isInitialConfigure)
        {
            Task.Run(() =>
            {
                bool ready = Subsonic.Ping();
                SetConnectionReady(ready);
                Api.MB_SendNotification?.Invoke(ready ? CallbackType.StorageReady : CallbackType.StorageFailed);
                if (!ready) return;

                Api.MB_SetBackgroundTaskMessage?.Invoke("Navidrome connected: ready for streaming & sync.");
                WarmPlaylistLookup();

                bool forceFull = !NavidromeCatalog.Instance.HasData || NavidromeCatalog.Instance.IsServerChanged(Subsonic);
                bool catalogChanged = false;
                bool synced = forceFull
                    ? (catalogChanged = NavidromeCatalog.Instance.SyncFromServer(Subsonic, msg => Api.MB_SetBackgroundTaskMessage?.Invoke(msg), null, Settings.ArtworkSize))
                    : NavidromeCatalog.Instance.SmartSyncIfNeeded(Subsonic, Settings.ArtworkSize, msg => Api.MB_SetBackgroundTaskMessage?.Invoke(msg), out catalogChanged);

                if (catalogChanged && Settings.SyncServerStarsToLibrary)
                    SyncStarsToMusicBeeLibrary();

                if (isInitialConfigure)
                    Refresh();

                if (synced)
                    ForceCacheAndLinkAllArtwork(false);
                else
                    Api.MB_RefreshPanels?.Invoke();
            });
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
                return form.ShowDialog() == DialogResult.OK;
            }
        }

        public void Close(PluginCloseReason reason)
        {
            ArtworkCache.Instance.StopPrewarm();
            if (NavidromeCatalog.Instance.HasData)
                NavidromeCatalog.Instance.SaveToDisk();
            _storageIcon?.Dispose();
            _storageIcon = null;
            Instance = null;
        }

        internal static void ShowUiMessage(string text, string caption, MessageBoxIcon icon)
        {
            try
            {
                Form owner = Application.OpenForms.Count > 0 ? Application.OpenForms[0] : null;
                if (owner != null && !owner.IsDisposed && owner.IsHandleCreated)
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

        private void ForceResync()
        {
            Task.Run(() =>
            {
                Api.MB_SetBackgroundTaskMessage?.Invoke("Navidrome: Starting full catalog and artwork sync...");
                bool ok = NavidromeCatalog.Instance.SyncFromServer(Subsonic, msg =>
                {
                    Api.MB_SetBackgroundTaskMessage?.Invoke(msg);
                }, null, Settings?.ArtworkSize ?? 800);

                if (ok)
                {
                    SetConnectionReady(true);
                    WarmPlaylistLookup();
                    Refresh();
                    ForceCacheAndLinkAllArtwork(false);
                    ShowUiMessage($"Navidrome catalog resynced successfully: {NavidromeCatalog.Instance.SongCount} tracks available.",
                        "Navidrome Resync", MessageBoxIcon.Information);
                }
                else
                {
                    ShowUiMessage("Could not connect to Navidrome server to resync catalog.",
                        "Navidrome Resync", MessageBoxIcon.Warning);
                }
            });
        }

        private void UpgradeArtwork()
        {
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
            });
        }

        private void ClearAllCache()
        {
            ArtworkCache.Instance.ClearDiskCache();
            AudioStreamCache.Instance.ClearCache();
            LyricsCache.Instance.ClearCache();
            Refresh();
            ShowUiMessage("Audio, artwork, and lyrics caches cleared successfully.", "Navidrome Cache", MessageBoxIcon.Information);
        }

        private void ImportAllToLibrary()
        {
            if (Api.Library_AddFileToLibrary == null) return;

            Task.Run(() =>
            {
                var songs = NavidromeCatalog.Instance.GetAllSongs();
                if (songs.Count == 0)
                {
                    ShowUiMessage("Please ensure the Navidrome catalog is synced before importing.",
                        "Navidrome Import", MessageBoxIcon.Information);
                    return;
                }

                int total = songs.Count;
                int added = 0;
                int targetSize = Settings?.ArtworkSize ?? 800;

                for (int i = 0; i < total; i++)
                {
                    var s = songs[i];
                    string rel = !string.IsNullOrEmpty(s.CachedRelativePath) ? s.CachedRelativePath : SubsonicUrlHelper.BuildRelativePath(s);
                    string url = "Navidrome:\\" + rel;
                    Api.Library_AddFileToLibrary(url, LibraryCategory.Music);
                    added++;

                    if (MusicBeeArtworkBridge.IsAvailable)
                    {
                        string cId = !string.IsNullOrEmpty(s.CanonicalCoverId) ? s.CanonicalCoverId : s.CoverArt;
                        if (!string.IsNullOrEmpty(cId))
                        {
                            string localPath = ArtworkCache.Instance.GetLocalFilePathIfExists(cId, targetSize);
                            if (localPath != null)
                                MusicBeeArtworkBridge.AttachArtwork(url, localPath);
                        }
                    }

                    if (added % 200 == 0 || added == total)
                        Api.MB_SetBackgroundTaskMessage?.Invoke($"Navidrome: Importing to Music Library ({added} / {total})...");
                }

                Api.MB_RefreshPanels?.Invoke();
                ForceCacheAndLinkAllArtwork(false);

                ShowUiMessage($"Successfully added {added} Navidrome tracks to your MusicBee 'MUSIC' Library.",
                    "Navidrome Library Sync", MessageBoxIcon.Information);
            });
        }

        public void Uninstall()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                    File.Delete(SettingsFilePath);
            }
            catch { }
        }

        public void ReceiveNotification(string sourceFileUrl, NotificationType type)
        {
            switch (type)
            {
                case NotificationType.SelectedFileChanged:
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
                    OnTagsChanged(sourceFileUrl);
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
