using System;
using System.Drawing;
using System.Windows.Forms;

namespace MusicBeePlugin
{
    public partial class SettingsWindow : Form
    {
        private readonly NavidromeSettings _settings;
        private readonly Action<NavidromeSettings> _onSave;

        private TextBox _txtServerUrl;
        private TextBox _txtUsername;
        private TextBox _txtPassword;
        private ComboBox _cmbAuthMethod;
        private ComboBox _cmbTranscode;
        private ComboBox _cmbArtworkSize;
        private ComboBox _cmbAudioCache;
        private CheckBox _chkPrefetch;
        private CheckBox _chkSyncStars;
        private Label _lblCacheStats;
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

        private void InitializeComponent()
        {
            Text = "Navidrome Storage & Sync Settings";
            const int formHeight = 434;
            ClientSize = new Size(524, formHeight);
            AutoScaleMode = AutoScaleMode.Font;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowIcon = false;
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

            int y = 14;
            const int lblW = 140;
            const int ctrlX = 160;
            const int ctrlW = 344;
            const int rowH = 30;

            Label CreateLabel(string text, int top) =>
                new Label { Text = text, Location = new Point(12, top + 3), Size = new Size(lblW, 20), TextAlign = ContentAlignment.MiddleRight };

            Controls.Add(CreateLabel("Navidrome URL:", y));
            _txtServerUrl = new TextBox { Location = new Point(ctrlX, y), Size = new Size(ctrlW, 23) };
            Controls.Add(_txtServerUrl);
            y += rowH;

            Controls.Add(CreateLabel("Username:", y));
            _txtUsername = new TextBox { Location = new Point(ctrlX, y), Size = new Size(ctrlW, 23) };
            Controls.Add(_txtUsername);
            y += rowH;

            Controls.Add(CreateLabel("Password:", y));
            _txtPassword = new TextBox { Location = new Point(ctrlX, y), Size = new Size(ctrlW, 23), PasswordChar = '•' };
            Controls.Add(_txtPassword);
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

            var cacheSep = new Label { Text = "── Caching && Performance ───────────────────────────────", Location = new Point(14, y), Size = new Size(494, 18), ForeColor = Color.Gray };
            Controls.Add(cacheSep);
            y += 22;

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

            _chkSyncStars = new CheckBox { Text = "Sync Navidrome stars && ratings to local MusicBee library", Location = new Point(ctrlX, y), Size = new Size(ctrlW, 22) };
            Controls.Add(_chkSyncStars);
            y += 26;

            _lblCacheStats = new Label { Location = new Point(14, y), Size = new Size(494, 20), ForeColor = Color.DarkSlateGray, Text = "Cache stats: calculating..." };
            Controls.Add(_lblCacheStats);
            y += 22;

            _btnForceLink = new Button { Text = "Cache && Link Art", Location = new Point(ctrlX, y), Size = new Size(136, 26) };
            _btnForceLink.Click += (s, e) => Plugin.Instance?.ForceCacheAndLinkAllArtwork(true);
            Controls.Add(_btnForceLink);

            _btnUpgradeArtwork = new Button { Text = "Re-Cache High-Res", Location = new Point(ctrlX + 142, y), Size = new Size(134, 26) };
            _btnUpgradeArtwork.Click += (s, e) => UpgradeArtwork();
            Controls.Add(_btnUpgradeArtwork);

            _btnClearCache = new Button { Text = "Clear", Location = new Point(ctrlX + 282, y), Size = new Size(62, 26) };
            _btnClearCache.Click += (s, e) => ClearCaches();
            Controls.Add(_btnClearCache);
            y += rowH + 6;


            _lblStatus = new Label { Location = new Point(14, y), Size = new Size(494, 34), Text = "", TextAlign = ContentAlignment.MiddleLeft };
            Controls.Add(_lblStatus);
            y += 38;

            _btnTestNavidrome = new Button { Text = "Test Connection", Location = new Point(14, y), Size = new Size(130, 28) };
            _btnTestNavidrome.Click += async (s, e) => await TestConnectionAsync();
            Controls.Add(_btnTestNavidrome);

            _btnSave = new Button { Text = "Save", Location = new Point(334, y), Size = new Size(80, 28), DialogResult = DialogResult.OK };
            _btnSave.Click += (s, e) => SaveValues();
            Controls.Add(_btnSave);

            _btnCancel = new Button { Text = "Cancel", Location = new Point(424, y), Size = new Size(80, 28), DialogResult = DialogResult.Cancel };
            Controls.Add(_btnCancel);
            AcceptButton = _btnSave;
            CancelButton = _btnCancel;
        }
    }
}
