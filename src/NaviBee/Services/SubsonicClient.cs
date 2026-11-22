using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace MusicBeePlugin
{
    public partial class SubsonicClient
    {
        private const string ClientName = "MusicBee";
        private const string ApiVersion = "1.16.1";
        private volatile NavidromeSettings _settings;
        public Exception LastError { get; private set; }

        static SubsonicClient()
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                // Enable TLS 1.3 when supported by the host OS / .NET 4.8 runtime
                try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)12288; } catch { }
            }
            catch { }
        }

        public SubsonicClient(NavidromeSettings settings)
        {
            _settings = settings ?? new NavidromeSettings();
        }

        public string ServerUrlNormalized
        {
            get
            {
                var s = _settings;
                return s?.ServerUrl?.Trim().TrimEnd('/') ?? "";
            }
        }

        public void UpdateSettings(NavidromeSettings settings)
        {
            _settings = settings ?? new NavidromeSettings();
            ArtworkCache.Instance.ClearMemoryCache();
            LyricsCache.Instance.ClearMemoryCache();
        }

        internal static void ConfigureRequest(HttpWebRequest req)
        {
            if (req == null) return;
            req.UserAgent = "NaviBee/1.0";
            try
            {
                var sp = ServicePointManager.FindServicePoint(req.RequestUri);
                if (sp != null && sp.ConnectionLimit < 16)
                    sp.ConnectionLimit = 16;
            }
            catch { }

            try
            {
                string proxyVal = Plugin.Api.Setting_GetWebProxy != null ? Plugin.Api.Setting_GetWebProxy() : null;
                if (!string.IsNullOrWhiteSpace(proxyVal))
                {
                    string candidate = proxyVal.Trim();
                    if (!candidate.Contains("://"))
                        candidate = "http://" + candidate;
                    if (Uri.TryCreate(candidate, UriKind.Absolute, out var proxyUri))
                        req.Proxy = new WebProxy(proxyUri);
                }
            }
            catch { }
        }

        internal static bool IsNonAudioErrorContentType(string contentType)
        {
            if (string.IsNullOrWhiteSpace(contentType)) return false;
            string ct = contentType.Trim().ToLowerInvariant();
            return ct.StartsWith("application/json") ||
                   ct.StartsWith("text/xml") ||
                   ct.StartsWith("application/xml") ||
                   ct.StartsWith("text/html") ||
                   ct.StartsWith("text/plain");
        }

        internal static bool IsValidImageBytes(byte[] data)
        {
            if (data == null || data.Length < 12) return false;
            // JPEG: FF D8 FF
            if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return true;
            // PNG: 89 50 4E 47 0D 0A 1A 0A
            if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47) return true;
            // GIF: GIF8
            if (data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x38) return true;
            // WEBP: RIFF....WEBP
            if (data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46 &&
                data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50) return true;
            // BMP: BM
            if (data[0] == 0x42 && data[1] == 0x4D) return true;
            return false;
        }

        private static string GetBaseUrlOrThrow(NavidromeSettings settings)
        {
            string raw = settings?.ServerUrl?.Trim() ?? "";
            if (string.IsNullOrEmpty(raw))
                throw new InvalidOperationException("Navidrome server URL is not configured.");
            if (!raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Server URL must start with http:// or https://.");
            return raw.TrimEnd('/');
        }

        private static string BuildAuthParams(NavidromeSettings settings, bool includeJson = true)
        {
            string username = Uri.EscapeDataString(settings?.Username ?? "");
            string password = settings?.PlainPassword ?? "";
            string fmt = includeJson ? "&f=json" : "";

            if (string.Equals(settings?.AuthMethod, "hexPass", StringComparison.OrdinalIgnoreCase))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(password);
                string hex = BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
                return $"u={username}&p=enc:{hex}&v={ApiVersion}&c={ClientName}{fmt}";
            }

            byte[] saltBytes = new byte[6];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(saltBytes);
            string salt = BitConverter.ToString(saltBytes).Replace("-", "").ToLowerInvariant();

            using (var md5 = MD5.Create())
            {
                byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(password + salt));
                string token = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                return $"u={username}&t={token}&s={salt}&v={ApiVersion}&c={ClientName}{fmt}";
            }
        }

        private static IDictionary<string, object> ParseSubsonicEnvelope(string json)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var root = serializer.Deserialize<Dictionary<string, object>>(json);
            if (root != null && root.TryGetValue("subsonic-response", out var respObj) && respObj is IDictionary<string, object> resp)
            {
                string status = SubsonicJsonParser.GetString(resp, "status");
                if (string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
                    return resp;

                if (resp.TryGetValue("error", out var errObj) && errObj is IDictionary<string, object> err)
                {
                    string msg = SubsonicJsonParser.GetString(err, "message", "Unknown server error");
                    int code = SubsonicJsonParser.GetInt(err, "code");
                    throw new InvalidOperationException($"Subsonic error {code}: {msg}");
                }
            }
            throw new InvalidOperationException("Invalid Subsonic server response format.");
        }

        public IDictionary<string, object> SendRequest(string endpoint, IDictionary<string, string> queryParams = null, int timeoutMs = 15000)
        {
            LastError = null;
            try
            {
                var settings = _settings;
                string baseUrl = GetBaseUrlOrThrow(settings);
                string auth = BuildAuthParams(settings);
                var sb = new StringBuilder($"{baseUrl}/rest/{endpoint}?{auth}");

                if (queryParams != null)
                {
                    foreach (var kvp in queryParams)
                        sb.Append('&').Append(Uri.EscapeDataString(kvp.Key)).Append('=').Append(Uri.EscapeDataString(kvp.Value ?? ""));
                }

                var request = (HttpWebRequest)WebRequest.Create(sb.ToString());
                request.Method = "GET";
                request.Accept = "application/json";
                request.Timeout = timeoutMs;
                request.ReadWriteTimeout = timeoutMs;
                ConfigureRequest(request);

                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    return ParseSubsonicEnvelope(reader.ReadToEnd());
                }
            }
            catch (Exception ex)
            {
                LastError = ex;
                return null;
            }
        }

        public bool Ping()
        {
            var resp = SendRequest("ping");
            return resp != null && string.Equals(SubsonicJsonParser.GetString(resp, "status"), "ok", StringComparison.OrdinalIgnoreCase);
        }

        public Uri GetStreamUri(string songId, int maxBitrate = 0)
        {
            var settings = _settings;
            string baseUrl = GetBaseUrlOrThrow(settings);
            string auth = BuildAuthParams(settings, includeJson: false);
            string url = $"{baseUrl}/rest/stream?{auth}&id={Uri.EscapeDataString(songId)}";
            int br = maxBitrate > 0 ? maxBitrate : (settings?.TranscodeBitrate ?? 0);
            if (br > 0)
                url += $"&maxBitRate={br}";
            return new Uri(url);
        }

        public byte[] GetCoverArt(string coverId, int size = -1)
        {
            var settings = _settings;
            int s = size >= 0 ? size : (settings != null ? settings.ArtworkSize : 800);
            string canonical = NavidromeCatalog.Instance.ResolveCanonicalCoverId(coverId);
            byte[] data = ArtworkCache.Instance.GetArtwork(canonical, s, GetCoverArtDirect);
            if (data != null && !string.IsNullOrEmpty(coverId) && !string.Equals(coverId, canonical, StringComparison.OrdinalIgnoreCase))
                ArtworkCache.Instance.PutMemory(coverId, s, data);
            return data;
        }

        public byte[] GetCoverArtDirect(string coverId, int size = -1)
        {
            if (string.IsNullOrEmpty(coverId))
                return null;

            try
            {
                var settings = _settings;
                int s = size >= 0 ? size : (settings != null ? settings.ArtworkSize : 800);
                string baseUrl = GetBaseUrlOrThrow(settings);
                string auth = BuildAuthParams(settings, includeJson: false);
                string sizeParam = s > 0 ? $"&size={s}" : "";
                string url = $"{baseUrl}/rest/getCoverArt?{auth}&id={Uri.EscapeDataString(coverId)}{sizeParam}";

                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = 15000;
                req.ReadWriteTimeout = 15000;
                ConfigureRequest(req);

                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    if (IsNonAudioErrorContentType(resp.ContentType))
                        return null;
                    using (var ms = new MemoryStream())
                    {
                        resp.GetResponseStream()?.CopyTo(ms);
                        byte[] data = ms.ToArray();
                        return IsValidImageBytes(data) ? data : null;
                    }
                }
            }
            catch (Exception ex)
            {
                LastError = ex;
                return null;
            }
        }
    }
}
