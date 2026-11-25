using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace MusicBeePlugin
{
    public class LocalTrackLink
    {
        public string LocalPath { get; set; } = "";
        public string SongId { get; set; } = "";
        public string MatchTier { get; set; } = "";
        public string LinkedUtc { get; set; } = "";
    }

    public struct LocalTrackInfo
    {
        public string Url, PidTag, Artist, AlbumArtist, PrimaryArtist, Album, Title;
        public int DiscNo, TrackNo, DurationSeconds;
    }

    public class LocalLinkSummary
    {
        public int Scanned { get; set; }
        public int Linked { get; set; }
        public int NewlyLinked { get; set; }
        public int PidEmbedded { get; set; }
        public int PidEmbedFailed { get; set; }
        public int Unmatched { get; set; }
        public int MatchedByPid { get; set; }
        public int MatchedByPath { get; set; }
        public int MatchedByMetadata { get; set; }
        public string UnmatchedLogPath { get; set; } = "";
        public List<LocalUnmatchedSample> UnmatchedSamples { get; } = new List<LocalUnmatchedSample>();
    }

    public class LocalUnmatchedSample
    {
        public string LocalPath { get; set; } = "";
        public string Artist { get; set; } = "";
        public string AlbumArtist { get; set; } = "";
        public string Album { get; set; } = "";
        public string Title { get; set; } = "";
        public int DiscNo { get; set; }
        public int TrackNo { get; set; }
        public int DurationSeconds { get; set; }
        public string NearestCandidate { get; set; } = "";
    }

    public partial class LocalLibraryLinker
    {
        private static readonly Regex DiscFolderRegex = new Regex(
            @"^(?:cd|disc|disk)\s*0*\d+$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex CueSubTrackRegex = new Regex(
            @"#\d+#$",
            RegexOptions.Compiled);

        public static bool IsCueSubTrackUrl(string url) =>
            !string.IsNullOrWhiteSpace(url) && CueSubTrackRegex.IsMatch(url.Trim());

        private static readonly string[] SafeMultiArtistSeparators = new[]
        {
            ";", " / ", " \u2022 ", " \u00B7 ", "\u2022", "\u00B7",
            " feat. ", " feat ", " ft. ", " ft ", " featuring ", " vs. ", " vs "
        };

        internal static bool IsDiscFolderName(string segment) =>
            !string.IsNullOrWhiteSpace(segment) && DiscFolderRegex.IsMatch(segment.Trim());

        internal static void ExtractPathSuffixes(string rawPath, out string suf3, out string suf2, out string stem3, out string stem2)
        {
            ExtractPathSuffixes(rawPath, out suf3, out suf2, out stem3, out stem2, out _, out _);
        }

        internal static string SafeNormalizeFormKC(string input)
        {
            if (string.IsNullOrEmpty(input)) return input ?? "";
            bool hasSurrogate = false;
            for (int i = 0; i < input.Length; i++)
            {
                if (char.IsSurrogate(input[i])) { hasSurrogate = true; break; }
            }

            string cleaned = input;
            if (hasSurrogate)
            {
                var sb = new StringBuilder(input.Length);
                for (int i = 0; i < input.Length; i++)
                {
                    char c = input[i];
                    if (char.IsHighSurrogate(c))
                    {
                        if (i + 1 < input.Length && char.IsLowSurrogate(input[i + 1]))
                        {
                            sb.Append(c);
                            sb.Append(input[++i]);
                        }
                    }
                    else if (!char.IsLowSurrogate(c))
                    {
                        sb.Append(c);
                    }
                }
                cleaned = sb.ToString();
            }

            try { return cleaned.Normalize(NormalizationForm.FormKC); }
            catch (ArgumentException) { return cleaned; }
        }

        internal static void ExtractPathSuffixes(
            string rawPath,
            out string suf3,
            out string suf2,
            out string stem3,
            out string stem2,
            out string discCollapsed,
            out string discCollapsedStem)
        {
            suf3 = suf2 = stem3 = stem2 = discCollapsed = discCollapsedStem = null;
            if (string.IsNullOrWhiteSpace(rawPath) || IsCueSubTrackUrl(rawPath)) return;

            string normalized = SafeNormalizeFormKC(rawPath.Trim()).Replace('/', '\\');
            string[] parts = normalized.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return;

            string file = parts[parts.Length - 1];
            SubsonicUrlHelper.SplitStemAndExtensionSafe(file, out string stem, out _);
            string parent = parts[parts.Length - 2];

            suf2 = parent + "\\" + file;
            stem2 = parent + "\\" + stem;

            if (parts.Length >= 3)
            {
                string grand = parts[parts.Length - 3];
                if (!grand.EndsWith(":", StringComparison.Ordinal))
                {
                    suf3 = grand + "\\" + suf2;
                    stem3 = grand + "\\" + stem2;

                    if (IsDiscFolderName(parent))
                    {
                        discCollapsed = grand + "\\" + file;
                        discCollapsedStem = grand + "\\" + stem;
                    }
                }
            }
        }

        internal static string NormalizeMetaToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            string norm = SafeNormalizeFormKC(value);
            var sb = new StringBuilder(norm.Length);
            bool lastSpace = false;

            for (int i = 0; i < norm.Length; i++)
            {
                char c = norm[i];
                if (c == '\u2018' || c == '\u2019' || c == '\u02BC') c = '\'';
                else if (c == '\u201C' || c == '\u201D') c = '"';
                else if (c == '\u2013' || c == '\u2014' || c == '\u2212') c = '-';
                else if (c == '\u2026')
                {
                    sb.Append("...");
                    lastSpace = false;
                    continue;
                }

                if (char.IsWhiteSpace(c))
                {
                    if (sb.Length > 0 && !lastSpace)
                    {
                        sb.Append(' ');
                        lastSpace = true;
                    }
                }
                else
                {
                    sb.Append(char.ToLowerInvariant(c));
                    lastSpace = false;
                }
            }

            if (sb.Length > 0 && sb[sb.Length - 1] == ' ')
                sb.Length--;

            string result = sb.ToString();
            if (result == "[unknown album]" || result == "[unknown artist]")
                return "";
            return result;
        }

        internal static string ExtractPrimaryArtist(string value)
        {
            string norm = NormalizeMetaToken(value);
            if (string.IsNullOrEmpty(norm)) return "";

            int splitIdx = -1;
            for (int i = 0; i < SafeMultiArtistSeparators.Length; i++)
            {
                int idx = norm.IndexOf(SafeMultiArtistSeparators[i], StringComparison.Ordinal);
                if (idx > 0 && (splitIdx < 0 || idx < splitIdx))
                    splitIdx = idx;
            }

            string primary = splitIdx > 0 ? norm.Substring(0, splitIdx).Trim() : norm;
            return StripArticleThe(primary);
        }

        internal static string StripArticleThe(string normalizedArtist)
        {
            if (string.IsNullOrEmpty(normalizedArtist)) return "";
            if (normalizedArtist.StartsWith("the ", StringComparison.Ordinal) && normalizedArtist.Length > 4)
                return normalizedArtist.Substring(4).Trim();
            if (normalizedArtist.EndsWith(", the", StringComparison.Ordinal) && normalizedArtist.Length > 5)
                return normalizedArtist.Substring(0, normalizedArtist.Length - 5).Trim();
            return normalizedArtist;
        }

        internal static List<string> ExtractArtistVariants(params string[] rawArtists)
        {
            var variants = new List<string>(6);
            var seen = new HashSet<string>(StringComparer.Ordinal);

            void Add(string token)
            {
                if (!string.IsNullOrEmpty(token) && seen.Add(token))
                    variants.Add(token);
            }

            if (rawArtists == null) return variants;
            for (int i = 0; i < rawArtists.Length; i++)
            {
                string norm = NormalizeMetaToken(rawArtists[i]);
                if (string.IsNullOrEmpty(norm)) continue;

                Add(norm);
                Add(StripArticleThe(norm));

                if (norm.Contains(" & "))
                {
                    string andForm = norm.Replace(" & ", " and ");
                    Add(andForm);
                    Add(StripArticleThe(andForm));
                }

                string primary = ExtractPrimaryArtist(norm);
                if (!string.IsNullOrEmpty(primary))
                {
                    Add(primary);
                    if (primary.Contains(" & "))
                        Add(primary.Replace(" & ", " and "));
                }
            }

            return variants;
        }

        internal static string WriteUnmatchedDiagnosticLog(
            string cacheDir,
            int catalogCount,
            int serverSongCount,
            LocalLinkSummary summary)
        {
            if (string.IsNullOrEmpty(cacheDir) || summary == null || summary.Unmatched <= 0)
                return null;

            string logPath = Path.Combine(cacheDir, "unmatched_tracks.log");
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine($"NaviBee Unmatched Local Tracks Report ({DateTime.UtcNow.ToString("u", CultureInfo.InvariantCulture)})");
                sb.AppendLine($"Catalog: {catalogCount:N0} tracks" + (serverSongCount > 0 ? $" (Server scanStatus: {serverSongCount:N0})" : ""));
                sb.AppendLine($"Scanned: {summary.Scanned:N0} | Linked: {summary.Linked:N0} | Unmatched: {summary.Unmatched:N0} | PID write failures: {summary.PidEmbedFailed:N0}");
                sb.AppendLine($"Breakdown: PID={summary.MatchedByPid:N0}, Path={summary.MatchedByPath:N0}, Metadata={summary.MatchedByMetadata:N0}");
                sb.AppendLine(new string('-', 80));

                int count = summary.UnmatchedSamples != null ? summary.UnmatchedSamples.Count : 0;
                sb.AppendLine($"Showing first {count} unmatched tracks:");
                sb.AppendLine();

                for (int i = 0; i < count; i++)
                {
                    var s = summary.UnmatchedSamples[i];
                    sb.AppendLine($"[{i + 1}] Path: {s.LocalPath}");
                    sb.AppendLine($"    Local Tags: Artist=\"{s.Artist}\" | AlbumArtist=\"{s.AlbumArtist}\" | Album=\"{s.Album}\" | Disc={s.DiscNo} Track={s.TrackNo} | Title=\"{s.Title}\" | Dur={s.DurationSeconds}s");
                    if (!string.IsNullOrEmpty(s.NearestCandidate))
                        sb.AppendLine($"    Nearest Server Track: {s.NearestCandidate}");
                    else
                        sb.AppendLine("    Nearest Server Track: (no close title/album match in synced catalog)");
                }

                NavidromeCatalog.WriteTextAtomic(logPath, sb.ToString());
                return logPath;
            }
            catch
            {
                return null;
            }
        }
    }
}
