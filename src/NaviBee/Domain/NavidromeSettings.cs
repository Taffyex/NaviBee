using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace MusicBeePlugin
{
    public class NavidromeSettings
    {
        internal const string DefaultServerUrl = "";


        public string ServerUrl { get; set; } = DefaultServerUrl;
        public string Username { get; set; } = "";
        public string EncryptedPassword { get; set; } = "";
        public string AuthMethod { get; set; } = "token"; // "token" or "hexPass"
        public string BrowseBy { get; set; } = "tags";     // kept for JSON compatibility
        public int TranscodeBitrate { get; set; } = 0;     // 0 = original
        public int ArtworkSize { get; set; } = 800;        // 0 = Original/Unscaled, 300, 800, 1200
        public int AudioCacheMaxMb { get; set; } = 2048;   // 0 = Disabled, 512, 2048, 5120, 10240, -1 = Unlimited
        public bool PrefetchNextTrack { get; set; } = true;
        public bool SyncServerStarsToLibrary { get; set; } = true;
        public bool PushPlaylistsToServer { get; set; } = false;
        public bool LinkLocalLibraryTracks { get; set; } = true;
        public bool EmbedNavidromeIdInLocalTag { get; set; } = true;
        public string LocalIdTagField { get; set; } = "Custom16"; // "Custom16" or "DatabaseOnly"
        public bool SyncPlayCountsToLibrary { get; set; } = true;
        public bool SkipDuplicatesOnImport { get; set; } = true;

        public bool TryGetPidMetaDataType(out MetaDataType field)
        {
            field = MetaDataType.Custom16;
            if (!EmbedNavidromeIdInLocalTag)
                return false;

            string raw = (LocalIdTagField ?? "Custom16").Trim();
            if (string.IsNullOrEmpty(raw) || string.Equals(raw, "DatabaseOnly", StringComparison.OrdinalIgnoreCase))
                return false;

            if (Enum.TryParse<MetaDataType>(raw, ignoreCase: true, out var parsed))
            {
                field = parsed;
                return true;
            }
            return true;
        }

        public MetaDataType GetPidMetaDataType()
        {
            return TryGetPidMetaDataType(out var field) ? field : MetaDataType.Custom16;
        }

        [ScriptIgnore]
        public string PlainPassword
        {
            get => Decrypt(EncryptedPassword);
            set => EncryptedPassword = Encrypt(value);
        }

        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NavidromePluginEntropy_MusicBee_v1");

        private const string DpapiPrefix = "dpapi:";
        private const string Base64Prefix = "b64:";

        public static string Encrypt(string plainText)
        {
            if (string.IsNullOrEmpty(plainText))
                return "";
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(plainText);
                byte[] encrypted = ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
                return DpapiPrefix + Convert.ToBase64String(encrypted);
            }
            catch
            {
                // Fallback to simple base64 if DPAPI fails
                return Base64Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(plainText));
            }
        }

        public static string Decrypt(string cipherText)
        {
            if (string.IsNullOrEmpty(cipherText))
                return "";

            if (cipherText.StartsWith(Base64Prefix, StringComparison.Ordinal))
            {
                try
                {
                    byte[] raw = Convert.FromBase64String(cipherText.Substring(Base64Prefix.Length));
                    return Encoding.UTF8.GetString(raw);
                }
                catch { return ""; }
            }

            string payload = cipherText.StartsWith(DpapiPrefix, StringComparison.Ordinal)
                ? cipherText.Substring(DpapiPrefix.Length)
                : cipherText;

            try
            {
                byte[] data = Convert.FromBase64String(payload);
                byte[] decrypted = ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(decrypted);
            }
            catch
            {
                // Never decode raw DPAPI ciphertext as UTF-8 on another machine/user.
                return "";
            }
        }

        public static NavidromeSettings Load(string filePath)
        {
            if (!File.Exists(filePath))
                return new NavidromeSettings();

            try
            {
                string json = File.ReadAllText(filePath, Encoding.UTF8);
                var serializer = new JavaScriptSerializer();
                var settings = serializer.Deserialize<NavidromeSettings>(json);
                return settings ?? new NavidromeSettings();
            }
            catch
            {
                return new NavidromeSettings();
            }
        }

        public bool Save(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return false;

            string tmp = null;
            try
            {
                string dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var serializer = new JavaScriptSerializer();
                string json = serializer.Serialize(this);
                tmp = filePath + ".tmp_" + Guid.NewGuid().ToString("N");
                File.WriteAllText(tmp, json, Encoding.UTF8);

                if (File.Exists(filePath))
                    File.Replace(tmp, filePath, null, ignoreMetadataErrors: true);
                else
                    File.Move(tmp, filePath);

                tmp = null;
                return true;
            }
            catch
            {
                try
                {
                    if (!string.IsNullOrEmpty(tmp) && File.Exists(tmp))
                    {
                        if (File.Exists(filePath)) File.Delete(filePath);
                        File.Move(tmp, filePath);
                        return true;
                    }
                }
                catch { }
                try { if (!string.IsNullOrEmpty(tmp) && File.Exists(tmp)) File.Delete(tmp); } catch { }
                return false;
            }
        }
    }
}
