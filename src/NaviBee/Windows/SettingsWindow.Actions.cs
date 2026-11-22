using System;
using System.Drawing;
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


            if (string.IsNullOrWhiteSpace(_txtServerUrl.Text))
            {
                _lblStatus.ForeColor = Color.DimGray;
                _lblStatus.Text = "Enter your Navidrome server URL (e.g. https://music.example.com) and credentials.";
            }
        }

        private void SaveValues()
        {
            _settings.ServerUrl = _txtServerUrl.Text.Trim();
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


            _onSave?.Invoke(_settings);
            Close();
        }

        private void RefreshStats()
        {
            ArtworkCache.Instance.GetCacheStats(out int artCount, out long artBytes);
            AudioStreamCache.Instance.GetCacheStats(out int audioCount, out long audioBytes);
            LyricsCache.Instance.GetCacheStats(out int lyricsCount, out long lyricsBytes);
            double artMb = artBytes / (1024.0 * 1024.0);
            double audioMb = audioBytes / (1024.0 * 1024.0);
            string text = $"Cache: {artCount} art ({artMb:F1} MB) | {audioCount} audio ({audioMb:F1} MB) | {lyricsCount} lyrics";
            if (IsHandleCreated && InvokeRequired) SafeUi(() => _lblCacheStats.Text = text);
            else _lblCacheStats.Text = text;
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
                    ServerUrl = _txtServerUrl.Text.Trim(),
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
            ArtworkCache.Instance.ClearDiskCache();
            AudioStreamCache.Instance.ClearCache();
            LyricsCache.Instance.ClearCache();
            RefreshStats();
            _lblStatus.ForeColor = Color.DarkGreen;
            _lblStatus.Text = "Artwork, audio, and lyrics caches cleared.";
        }

        private async Task TestConnectionAsync()
        {
            _btnTestNavidrome.Enabled = false;
            _lblStatus.ForeColor = Color.Black;
            _lblStatus.Text = "Pinging Navidrome server...";

            try
            {
                var testSettings = new NavidromeSettings
                {
                    ServerUrl = _txtServerUrl.Text.Trim(),
                    Username = _txtUsername.Text.Trim(),
                    PlainPassword = _txtPassword.Text,
                    AuthMethod = _cmbAuthMethod.SelectedItem?.ToString() ?? "token"
                };

                var client = new SubsonicClient(testSettings);
                bool ok = await Task.Run(() => client.Ping());
                if (IsDisposed) return;

                if (ok)
                {
                    _lblStatus.ForeColor = Color.DarkGreen;
                    _lblStatus.Text = "Connection successful! Navidrome is reachable.";
                }
                else
                {
                    string detail = client.LastError?.Message ?? "Check URL, username and password.";
                    if (detail.Length > 110) detail = detail.Substring(0, 107) + "...";
                    _lblStatus.ForeColor = Color.DarkRed;
                    _lblStatus.Text = $"Connection failed: {detail}";
                }
            }
            catch (Exception ex)
            {
                if (!IsDisposed)
                {
                    _lblStatus.ForeColor = Color.DarkRed;
                    _lblStatus.Text = $"Connection failed: {ex.Message}";
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
