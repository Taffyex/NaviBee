using System;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace MusicBeePlugin
{
    public static class FriendlyErrors
    {
        private static readonly Regex SubsonicCodeRegex = new Regex(@"Subsonic error (\d+):\s*(.*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static string NormalizeServerUrl(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return "";

            string s = raw.Trim();
            if (!s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                bool preferHttp = LooksLikeLocalOrExplicitPort(s);
                s = (preferHttp ? "http://" : "https://") + s;
            }

            s = s.TrimEnd('/');
            if (s.EndsWith("/rest", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(0, s.Length - 5).TrimEnd('/');

            return s;
        }

        private static bool LooksLikeLocalOrExplicitPort(string hostAndPath)
        {
            string hostPart = hostAndPath;
            int slash = hostPart.IndexOf('/');
            if (slash >= 0) hostPart = hostPart.Substring(0, slash);

            if (hostPart.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) ||
                hostPart.StartsWith("127.", StringComparison.Ordinal) ||
                hostPart.StartsWith("192.168.", StringComparison.Ordinal) ||
                hostPart.StartsWith("10.", StringComparison.Ordinal) ||
                hostPart.StartsWith("172.", StringComparison.Ordinal) ||
                hostPart.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
                hostPart.Contains(":4533"))
            {
                return true;
            }

            return false;
        }

        public static string FormatException(Exception ex)
        {
            if (ex == null)
                return "Check server URL, username and password.";

            if (ex is WebException wex)
                return FormatWebException(wex);

            string msg = ex.Message ?? "";
            var m = SubsonicCodeRegex.Match(msg);
            if (m.Success && int.TryParse(m.Groups[1].Value, out int code))
            {
                string serverMsg = m.Groups[2].Value.Trim();
                switch (code)
                {
                    case 40: return "Wrong username or password (error 40).";
                    case 41: return "Token auth not supported by server; switch Auth Method to hexPass (error 41).";
                    case 42: return "Server rejected authentication method (error 42).";
                    case 43: return "Multiple conflicting auth mechanisms (error 43).";
                    case 44: return "Invalid API key (error 44).";
                    case 50: return "User is not authorized for this operation (error 50).";
                    case 60: return "Server trial period has expired (error 60).";
                    case 70: return "Requested item was not found on the server (error 70).";
                    case 20: return "Incompatible Subsonic client protocol version (error 20).";
                    case 30: return "Incompatible Subsonic server protocol version (error 30).";
                    default: return string.IsNullOrEmpty(serverMsg) ? $"Server error {code}." : $"Server error {code}: {serverMsg}";
                }
            }

            if (ex is UriFormatException)
                return "Server URL format is invalid.";

            if (msg.Length > 140)
                msg = msg.Substring(0, 137) + "...";

            return string.IsNullOrWhiteSpace(msg) ? "Unknown connection error." : msg;
        }

        private static string FormatWebException(WebException wex)
        {
            if (wex.Response is HttpWebResponse httpResp)
            {
                int code = (int)httpResp.StatusCode;
                switch (code)
                {
                    case 401: return "Unauthorized (HTTP 401) — check username and password.";
                    case 403: return "Access forbidden (HTTP 403) — check reverse proxy or credentials.";
                    case 404: return "Server API not found (HTTP 404) — check the server URL.";
                    case 502: return "Bad gateway (HTTP 502) — reverse proxy cannot reach Navidrome.";
                    case 503: return "Server unavailable (HTTP 503) — Navidrome may be restarting.";
                    case 504: return "Gateway timeout (HTTP 504) — server took too long to respond.";
                    default: return $"HTTP {code} ({httpResp.StatusDescription}).";
                }
            }

            switch (wex.Status)
            {
                case WebExceptionStatus.NameResolutionFailure:
                    return "Server host not found (DNS lookup failed).";
                case WebExceptionStatus.ConnectFailure:
                    return "Could not connect — server is unreachable or port is closed.";
                case WebExceptionStatus.Timeout:
                    return "Connection timed out — server did not respond in time.";
                case WebExceptionStatus.TrustFailure:
                    return "SSL/TLS certificate is not trusted by Windows.";
                case WebExceptionStatus.SecureChannelFailure:
                    return "TLS/SSL handshake failed — check HTTPS settings.";
                case WebExceptionStatus.ConnectionClosed:
                case WebExceptionStatus.KeepAliveFailure:
                    return "Connection was closed unexpectedly by the server.";
                default:
                    return wex.Message;
            }
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes < 0) bytes = 0;
            if (bytes >= 1024L * 1024L * 1024L)
                return string.Format(CultureInfo.InvariantCulture, "{0:F2} GB", bytes / (1024.0 * 1024.0 * 1024.0));
            if (bytes >= 1024L * 1024L)
                return string.Format(CultureInfo.InvariantCulture, "{0:F1} MB", bytes / (1024.0 * 1024.0));
            if (bytes >= 1024L)
                return string.Format(CultureInfo.InvariantCulture, "{0:F0} KB", bytes / 1024.0);
            return $"{bytes} B";
        }

        public static string FormatTrackLabel(SubsonicSong song)
        {
            if (song == null) return "Unknown track";
            bool hasArtist = !string.IsNullOrWhiteSpace(song.Artist);
            bool hasTitle = !string.IsNullOrWhiteSpace(song.Title);
            if (hasArtist && hasTitle) return $"{song.Artist} \u2013 {song.Title}";
            if (hasTitle) return song.Title;
            return !string.IsNullOrWhiteSpace(song.Id) ? song.Id : "Unknown track";
        }
    }
}
