using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MusicBeePlugin
{
    public partial class SettingsWindow
    {
        private void SafeUi(Action action)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                if (InvokeRequired) Invoke(action);
                else action();
            }
            catch { }
        }

        private void NormalizeUrlField()
        {
            string raw = _txtServerUrl.Text;
            if (string.IsNullOrWhiteSpace(raw)) return;
            string normalized = FriendlyErrors.NormalizeServerUrl(raw);
            if (!string.Equals(raw, normalized, StringComparison.Ordinal))
                _txtServerUrl.Text = normalized;
        }

        private void LoadValues()
        {
            _txtServerUrl.Text = _settings.ServerUrl ?? NavidromeSettings.DefaultServerUrl;
            _txtUsername.Text = _settings.Username ?? "";
            _txtPassword.Text = _settings.PlainPassword ?? "";
            _cmbAuthMethod.SelectedItem = _settings.AuthMethod ?? "token";

            switch (_settings.TranscodeBitrate)
            {
                case 128: _cmbTranscode.SelectedIndex = 1; break;
                case 192: _cmbTranscode.SelectedIndex = 2; break;
                case 256: _cmbTranscode.SelectedIndex = 3; break;
                case 320: _cmbTranscode.SelectedIndex = 4; break;
                default: _cmbTranscode.SelectedIndex = 0; break;
            }

            switch (_settings.ArtworkSize)
            {
                case 0: _cmbArtworkSize.SelectedIndex = 0; break;
                case 1200: _cmbArtworkSize.SelectedIndex = 1; break;
                case 300: _cmbArtworkSize.SelectedIndex = 3; break;
                default: _cmbArtworkSize.SelectedIndex = 2; break;
            }

            switch (_settings.AudioCacheMaxMb)
            {
                case 0: _cmbAudioCache.SelectedIndex = 0; break;
                case 512: _cmbAudioCache.SelectedIndex = 1; break;
                case 5120: _cmbAudioCache.SelectedIndex = 3; break;
                case 10240: _cmbAudioCache.SelectedIndex = 4; break;
                case -1: _cmbAudioCache.SelectedIndex = 5; break;
                default: _cmbAudioCache.SelectedIndex = 2; break;
            }

            _chkPrefetch.Checked = _settings.PrefetchNextTrack;
            _chkSyncStars.Checked = _settings.SyncServerStarsToLibrary;
            _chkPushPlaylists.Checked = _settings.PushPlaylistsToServer;

            if (string.IsNullOrWhiteSpace(_txtServerUrl.Text))
            {
                _lblStatus.ForeColor = Color.DimGray;
                _lblStatus.Text = "Enter your Navidrome server URL (e.g. https://music.example.com) and credentials.";
            }
        }

        private void SaveValues()
        {
            NormalizeUrlField();
            _settings.ServerUrl = FriendlyErrors.NormalizeServerUrl(_txtServerUrl.Text);
            _settings.Username = _txtUsername.Text.Trim();
            _settings.PlainPassword = _txtPassword.Text;
            _settings.AuthMethod = _cmbAuthMethod.SelectedItem?.ToString() ?? "token";

            switch (_cmbTranscode.SelectedIndex)
            {
                case 1: _settings.TranscodeBitrate = 128; break;
                case 2: _settings.TranscodeBitrate = 192; break;
                case 3: _settings.TranscodeBitrate = 256; break;
                case 4: _settings.TranscodeBitrate = 320; break;
                default: _settings.TranscodeBitrate = 0; break;
            }

            switch (_cmbArtworkSize.SelectedIndex)
            {
                case 0: _settings.ArtworkSize = 0; break;
                case 1: _settings.ArtworkSize = 1200; break;
                case 3: _settings.ArtworkSize = 300; break;
                default: _settings.ArtworkSize = 800; break;
            }

            switch (_cmbAudioCache.SelectedIndex)
            {
                case 0: _settings.AudioCacheMaxMb = 0; break;
                case 1: _settings.AudioCacheMaxMb = 512; break;
                case 3: _settings.AudioCacheMaxMb = 5120; break;
                case 4: _settings.AudioCacheMaxMb = 10240; break;
                case 5: _settings.AudioCacheMaxMb = -1; break;
                default: _settings.AudioCacheMaxMb = 2048; break;
            }

            _settings.PrefetchNextTrack = _chkPrefetch.Checked;
            _settings.SyncServerStarsToLibrary = _chkSyncStars.Checked;
            _settings.PushPlaylistsToServer = _chkPushPlaylists.Checked;

            _onSave?.Invoke(_settings);
            Close();
        }

        private void RefreshStats()
        {
            ArtworkCache.Instance.GetCacheStats(out int artCount, out long artBytes);
            AudioStreamCache.Instance.GetCacheStats(out int audioCount, out long audioBytes);
            LyricsCache.Instance.GetCacheStats(out int lyricsCount, out _);
            PlaylistBackup.Instance.GetBackupStats(out int backupCount, out long backupBytes);

            string cacheText = $"Cache: {artCount} art ({FriendlyErrors.FormatBytes(artBytes)}) | {audioCount} audio ({FriendlyErrors.FormatBytes(audioBytes)}) | {lyricsCount} lyrics";
            string backupText = backupCount > 0
                ? $"Playlist backups: {backupCount} {(backupCount == 1 ? "file" : "files")} ({FriendlyErrors.FormatBytes(backupBytes)})"
                : "Playlist backups: none yet";

            if (IsHandleCreated && InvokeRequired)
            {
                SafeUi(() =>
                {
                    _lblCacheStats.Text = cacheText;
                    _lblBackupStats.Text = backupText;
                });
            }
            else
            {
                _lblCacheStats.Text = cacheText;
                _lblBackupStats.Text = backupText;
            }
        }

        private void OpenBackupsFolder()
        {
            try
            {
                string dir = PlaylistBackup.Instance.BackupDirectory;
                if (string.IsNullOrWhiteSpace(dir)) return;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _lblStatus.ForeColor = Color.DarkRed;
                _lblStatus.Text = $"Could not open backups folder: {ex.Message}";
            }
        }

        private void UpgradeArtwork()
        {
            _btnUpgradeArtwork.Enabled = false;
            _lblStatus.ForeColor = Color.Black;
            _lblStatus.Text = "Starting artwork upgrade to high-res...";

            int selectedSize = 800;
            switch (_cmbArtworkSize.SelectedIndex)
            {
                case 0: selectedSize = 0; break;
                case 1: selectedSize = 1200; break;
                case 3: selectedSize = 300; break;
            }

            var covers = NavidromeCatalog.Instance.GetDistinctAlbumCoverIds();
            if (covers.Count == 0)
            {
                _btnUpgradeArtwork.Enabled = true;
                _lblStatus.ForeColor = Color.DarkOrange;
                _lblStatus.Text = "Catalog is empty. Save and connect first to sync tracks.";
                return;
            }

            var client = Plugin.Subsonic;
            if (client == null || string.IsNullOrWhiteSpace(client.ServerUrlNormalized))
            {
                client = new SubsonicClient(new NavidromeSettings
                {
                    ServerUrl = FriendlyErrors.NormalizeServerUrl(_txtServerUrl.Text),
                    Username = _txtUsername.Text.Trim(),
                    PlainPassword = _txtPassword.Text,
                    AuthMethod = _cmbAuthMethod.SelectedItem?.ToString() ?? "token",
                    ArtworkSize = selectedSize
                });
            }

            ArtworkCache.Instance.UpgradeAndRecacheArtwork(covers, client, selectedSize,
                msg => SafeUi(() => _lblStatus.Text = msg),
                () =>
                {
                    Plugin.Instance?.ForceCacheAndLinkAllArtwork(false);
                    SafeUi(() =>
                    {
                        _btnUpgradeArtwork.Enabled = true;
                        RefreshStats();
                    });
                });
        }

        private void ClearCaches()
        {
            ArtworkCache.Instance.GetCacheStats(out _, out long artBytes);
            AudioStreamCache.Instance.GetCacheStats(out _, out long audioBytes);
            LyricsCache.Instance.GetCacheStats(out _, out long lyricsBytes);
            long totalBytes = artBytes + audioBytes + lyricsBytes;

            var result = MessageBox.Show(
                this,
                $"Delete {FriendlyErrors.FormatBytes(totalBytes)} of cached audio, artwork, and lyrics?\n\nPlaylist backups will not be deleted.",
                "Clear Navidrome Cache",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);

            if (result != DialogResult.Yes)
                return;

            ArtworkCache.Instance.ClearDiskCache();
            AudioStreamCache.Instance.ClearCache();
            LyricsCache.Instance.ClearCache();
            RefreshStats();
            _lblStatus.ForeColor = Color.DarkGreen;
            _lblStatus.Text = "Artwork, audio, and lyrics caches cleared.";
        }

        private async Task TestConnectionAsync()
        {
            NormalizeUrlField();
            _btnTestNavidrome.Enabled = false;
            _lblStatus.ForeColor = Color.Black;
            _lblStatus.Text = "Pinging Navidrome server...";

            try
            {
                var testSettings = new NavidromeSettings
                {
                    ServerUrl = FriendlyErrors.NormalizeServerUrl(_txtServerUrl.Text),
                    Username = _txtUsername.Text.Trim(),
                    PlainPassword = _txtPassword.Text,
                    AuthMethod = _cmbAuthMethod.SelectedItem?.ToString() ?? "token"
                };

                var client = new SubsonicClient(testSettings);
                var result = await Task.Run(() =>
                {
                    var ping = client.PingWithInfo();
                    SubsonicScanStatus scan = ping.Ok ? client.GetScanStatus() : null;
                    return Tuple.Create(ping, scan, client.LastError);
                });
                if (IsDisposed) return;

                var pingInfo = result.Item1;
                var scanInfo = result.Item2;
                var lastErr = result.Item3;

                if (pingInfo != null && pingInfo.Ok)
                {
                    string serverName = !string.IsNullOrWhiteSpace(pingInfo.ServerType)
                        ? char.ToUpperInvariant(pingInfo.ServerType[0]) + pingInfo.ServerType.Substring(1)
                        : "Subsonic";
                    string version = !string.IsNullOrWhiteSpace(pingInfo.ServerVersion)
                        ? " " + pingInfo.ServerVersion
                        : (!string.IsNullOrWhiteSpace(pingInfo.ApiVersion) ? " API " + pingInfo.ApiVersion : "");
                    string trackCount = scanInfo != null && scanInfo.Count > 0
                        ? $" \u00B7 {scanInfo.Count:N0} tracks"
                        : "";

                    _lblStatus.ForeColor = Color.DarkGreen;
                    _lblStatus.Text = $"\u2713 Connected to {serverName}{version}{trackCount}";
                }
                else
                {
                    string detail = FriendlyErrors.FormatException(lastErr);
                    _lblStatus.ForeColor = Color.DarkRed;
                    _lblStatus.Text = $"Connection failed: {detail}";
                }
            }
            catch (Exception ex)
            {
                if (!IsDisposed)
                {
                    _lblStatus.ForeColor = Color.DarkRed;
                    _lblStatus.Text = $"Connection failed: {FriendlyErrors.FormatException(ex)}";
                }
            }
            finally
            {
                if (!IsDisposed)
                    _btnTestNavidrome.Enabled = true;
            }
        }
    }
}
