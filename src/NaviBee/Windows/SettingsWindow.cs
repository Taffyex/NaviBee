using System;
using System.Drawing;
using System.Windows.Forms;

namespace MusicBeePlugin
{
    public partial class SettingsWindow : Form
    {
        private readonly NavidromeSettings _settings;
        private readonly Action<NavidromeSettings> _onSave;
        private ToolTip _toolTip;

        private TextBox _txtServerUrl;
        private TextBox _txtUsername;
        private TextBox _txtPassword;
        private CheckBox _chkShowPassword;
        private ComboBox _cmbAuthMethod;
        private ComboBox _cmbTranscode;
        private ComboBox _cmbArtworkSize;
        private ComboBox _cmbAudioCache;
        private CheckBox _chkPrefetch;
        private CheckBox _chkLinkLocal;
        private CheckBox _chkEmbedLocalPid;
        private CheckBox _chkSyncStars;
        private CheckBox _chkPushPlaylists;
        private Label _lblCacheStats;
        private Label _lblBackupStats;
        private LinkLabel _lnkOpenBackups;
        private Button _btnLinkLocal;
        private Button _btnForceLink;
        private Button _btnUpgradeArtwork;
        private Button _btnClearCache;

        private Label _lblStatus;
        private Button _btnTestNavidrome;
        private Button _btnSave;
        private Button _btnCancel;

        public SettingsWindow(NavidromeSettings settings, Action<NavidromeSettings> onSave)
        {
            _settings = settings ?? new NavidromeSettings();
            _onSave = onSave;
            InitializeComponent();
            LoadValues();
            RefreshStats();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _toolTip?.Dispose();
            base.Dispose(disposing);
        }

        private void AddSectionDivider(string title, int top)
        {
            var lbl = new Label
            {
                Text = title,
                AutoSize = true,
                UseMnemonic = false,
                Location = new Point(14, top),
                ForeColor = Color.FromArgb(70, 82, 96),
                Font = new Font("Segoe UI", 8.5F, FontStyle.Bold, GraphicsUnit.Point)
            };
            Controls.Add(lbl);

            int textW = TextRenderer.MeasureText(title, lbl.Font, Size.Empty, TextFormatFlags.NoPrefix).Width;
            int lineX = 14 + textW + 6;
            int lineW = Math.Max(40, 520 - lineX);
            var line = new Panel
            {
                Location = new Point(lineX, top + 8),
                Size = new Size(lineW, 1),
                BackColor = SystemColors.ControlDark
            };
            Controls.Add(line);
        }

        private void InitializeComponent()
        {
            _toolTip = new ToolTip { AutoPopDelay = 12000, InitialDelay = 300, ReshowDelay = 200 };
            Text = "NaviBee — Navidrome Storage & Sync Settings";
            AutoScaleMode = AutoScaleMode.Font;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowIcon = false;
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

            int y = 14;
            const int lblW = 136;
            const int ctrlX = 158;
            const int ctrlW = 362;
            const int rowH = 30;

            Label CreateLabel(string text, int top) =>
                new Label { Text = text, Location = new Point(12, top + 3), Size = new Size(lblW, 20), TextAlign = ContentAlignment.MiddleRight };

            Controls.Add(CreateLabel("Navidrome URL:", y));
            _txtServerUrl = new TextBox { Location = new Point(ctrlX, y), Size = new Size(ctrlW, 23) };
            _txtServerUrl.Leave += (s, e) => NormalizeUrlField();
            _toolTip.SetToolTip(_txtServerUrl, "Your Navidrome or Subsonic server URL (e.g. https://music.example.com or http://localhost:4533).");
            Controls.Add(_txtServerUrl);
            y += rowH;

            Controls.Add(CreateLabel("Username:", y));
            _txtUsername = new TextBox { Location = new Point(ctrlX, y), Size = new Size(ctrlW, 23) };
            Controls.Add(_txtUsername);
            y += rowH;

            Controls.Add(CreateLabel("Password:", y));
            _txtPassword = new TextBox { Location = new Point(ctrlX, y), Size = new Size(ctrlW - 70, 23), PasswordChar = '•' };
            Controls.Add(_txtPassword);

            _chkShowPassword = new CheckBox { Text = "Show", Location = new Point(ctrlX + ctrlW - 62, y + 1), Size = new Size(64, 22) };
            _chkShowPassword.CheckedChanged += (s, e) => _txtPassword.PasswordChar = _chkShowPassword.Checked ? '\0' : '•';
            Controls.Add(_chkShowPassword);
            y += rowH;

            Controls.Add(CreateLabel("Auth Method:", y));
            _cmbAuthMethod = new ComboBox { Location = new Point(ctrlX, y), Size = new Size(ctrlW, 23), DropDownStyle = ComboBoxStyle.DropDownList };
            _cmbAuthMethod.Items.AddRange(new object[] { "token", "hexPass" });
            Controls.Add(_cmbAuthMethod);
            y += rowH;

            Controls.Add(CreateLabel("Transcoding:", y));
            _cmbTranscode = new ComboBox { Location = new Point(ctrlX, y), Size = new Size(ctrlW, 23), DropDownStyle = ComboBoxStyle.DropDownList };
            _cmbTranscode.Items.AddRange(new object[] { "Original (0)", "128 kbps", "192 kbps", "256 kbps", "320 kbps" });
            Controls.Add(_cmbTranscode);
            y += rowH + 6;

            AddSectionDivider("Caching & Sync", y);
            y += 24;

            Controls.Add(CreateLabel("Artwork Quality:", y));
            _cmbArtworkSize = new ComboBox { Location = new Point(ctrlX, y), Size = new Size(ctrlW, 23), DropDownStyle = ComboBoxStyle.DropDownList };
            _cmbArtworkSize.Items.AddRange(new object[] { "Original / Master (Unscaled)", "1200 px (Ultra HD)", "800 px (High Definition - Recommended)", "300 px (Compact)" });
            Controls.Add(_cmbArtworkSize);
            y += rowH;

            Controls.Add(CreateLabel("Audio Cache:", y));
            _cmbAudioCache = new ComboBox { Location = new Point(ctrlX, y), Size = new Size(ctrlW, 23), DropDownStyle = ComboBoxStyle.DropDownList };
            _cmbAudioCache.Items.AddRange(new object[] { "Disabled (0)", "512 MB", "2048 MB (2 GB - Recommended)", "5120 MB (5 GB)", "10240 MB (10 GB)", "Unlimited (-1)" });
            Controls.Add(_cmbAudioCache);
            y += rowH;

            _chkPrefetch = new CheckBox { Text = "Pre-fetch next track in Now Playing queue", Location = new Point(ctrlX, y), Size = new Size(ctrlW, 22) };
            Controls.Add(_chkPrefetch);
            y += 24;

            _chkLinkLocal = new CheckBox { Text = "Link local library tracks (2-way playcount && rating sync)", Location = new Point(ctrlX, y), Size = new Size(ctrlW, 22) };
            _chkLinkLocal.CheckedChanged += (s, e) => _chkEmbedLocalPid.Enabled = _chkLinkLocal.Checked;
            _toolTip.SetToolTip(_chkLinkLocal,
                "Matches local MusicBee files to Navidrome tracks so playing, rating,\n" +
                "loving, or adding local files to playlists syncs two-way with Navidrome.");
            Controls.Add(_chkLinkLocal);
            y += 24;

            _chkEmbedLocalPid = new CheckBox { Text = "Embed Navidrome ID in local tag (Custom16)", Location = new Point(ctrlX + 18, y), Size = new Size(ctrlW - 18, 22) };
            _toolTip.SetToolTip(_chkEmbedLocalPid,
                "Writes the Navidrome song ID into Custom16 on matched local files for instant O(1) lookup.\n" +
                "Uncheck to store links only in NavidromeCache\\local_links.json without modifying audio files.");
            Controls.Add(_chkEmbedLocalPid);
            y += 24;

            _chkSyncStars = new CheckBox { Text = "Sync Navidrome stars, ratings && playcounts to library", Location = new Point(ctrlX, y), Size = new Size(ctrlW, 22) };
            Controls.Add(_chkSyncStars);
            y += 24;

            _chkPushPlaylists = new CheckBox { Text = "Push MusicBee playlist changes to Navidrome (safe mode)", Location = new Point(ctrlX, y), Size = new Size(ctrlW, 22) };
            _toolTip.SetToolTip(_chkPushPlaylists,
                "Opt-in. Never deletes server playlists, never sends empty playlists,\n" +
                "never overwrites playlists not created by NaviBee, skips playlists\n" +
                "owned by other users, and saves a JSON backup before every update.");
            Controls.Add(_chkPushPlaylists);
            y += 28;

            _lblCacheStats = new Label { Location = new Point(14, y), Size = new Size(506, 20), ForeColor = Color.DarkSlateGray, Text = "Cache stats: calculating..." };
            Controls.Add(_lblCacheStats);
            y += 21;

            _lblBackupStats = new Label { Location = new Point(14, y), Size = new Size(340, 20), ForeColor = Color.DarkSlateGray, Text = "Playlist backups: 0 files" };
            Controls.Add(_lblBackupStats);

            _lnkOpenBackups = new LinkLabel { Text = "Open backups folder", Location = new Point(360, y), Size = new Size(160, 20), TextAlign = ContentAlignment.MiddleRight };
            _lnkOpenBackups.LinkClicked += (s, e) => OpenBackupsFolder();
            Controls.Add(_lnkOpenBackups);
            y += 26;

            _btnLinkLocal = new Button { Text = "Link Local Library", Location = new Point(14, y), Size = new Size(132, 26) };
            _btnLinkLocal.Click += (s, e) => LinkLocalLibraryNow();
            _toolTip.SetToolTip(_btnLinkLocal, "Match local MusicBee library tracks to Navidrome and run a 2-way star, rating & playcount sync now.");
            Controls.Add(_btnLinkLocal);

            _btnForceLink = new Button { Text = "Cache && Link Art", Location = new Point(154, y), Size = new Size(132, 26) };
            _btnForceLink.Click += (s, e) => Plugin.Instance?.ForceCacheAndLinkAllArtwork(true);
            Controls.Add(_btnForceLink);

            _btnUpgradeArtwork = new Button { Text = "Re-Cache High-Res", Location = new Point(294, y), Size = new Size(138, 26) };
            _btnUpgradeArtwork.Click += (s, e) => UpgradeArtwork();
            Controls.Add(_btnUpgradeArtwork);

            _btnClearCache = new Button { Text = "Clear Cache", Location = new Point(440, y), Size = new Size(80, 26) };
            _btnClearCache.Click += (s, e) => ClearCaches();
            Controls.Add(_btnClearCache);
            y += rowH + 6;


            _lblStatus = new Label { Location = new Point(14, y), Size = new Size(506, 36), Text = "", TextAlign = ContentAlignment.MiddleLeft };
            Controls.Add(_lblStatus);
            y += 40;

            _btnTestNavidrome = new Button { Text = "Test Connection", Location = new Point(14, y), Size = new Size(132, 28) };
            _btnTestNavidrome.Click += async (s, e) => await TestConnectionAsync();
            Controls.Add(_btnTestNavidrome);

            _btnSave = new Button { Text = "Save", Location = new Point(350, y), Size = new Size(80, 28), DialogResult = DialogResult.OK };
            _btnSave.Click += (s, e) => SaveValues();
            Controls.Add(_btnSave);

            _btnCancel = new Button { Text = "Cancel", Location = new Point(440, y), Size = new Size(80, 28), DialogResult = DialogResult.Cancel };
            Controls.Add(_btnCancel);

            ClientSize = new Size(536, y + 42);
            AcceptButton = _btnSave;
            CancelButton = _btnCancel;
        }
    }
}
