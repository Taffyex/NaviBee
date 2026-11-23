using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MusicBeePlugin
{
    /// <summary>Represents parsed lyrics text and whether it contains LRC timestamps.</summary>
    public sealed class SubsonicLyricsResult
    {
        public static readonly SubsonicLyricsResult Empty = new SubsonicLyricsResult("", false);

        public string Text { get; }
        public bool IsSynced { get; }
        public bool HasLyrics => !string.IsNullOrWhiteSpace(Text);

        public SubsonicLyricsResult(string text, bool isSynced)
        {
            Text = text ?? "";
            IsSynced = isSynced && !string.IsNullOrWhiteSpace(Text);
        }
    }

    /// <summary>Parses OpenSubsonic structuredLyrics and raw Subsonic lyrics into MusicBee-compatible LRC or plain text.</summary>
    public static class SubsonicLyricsFormatter
    {
        private static readonly Regex LrcTimestampLineRegex = new Regex(
            @"^\s*\[\d{1,3}:\d{2}(?:\.\d{1,3})?\]",
            RegexOptions.Compiled | RegexOptions.Multiline);

        private static readonly Regex LrcMetadataTagLineRegex = new Regex(
            @"^\s*\[(?:ar|ti|al|by|offset):",
            RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase);

        private sealed class TimedLine
        {
            public long EffectiveMs;
            public int Order;
            public string Text;
        }

        private sealed class CandidateEntry
        {
            public bool Synced;
            public bool IsMain;
            public int Offset;
            public List<TimedLine> TimedLines = new List<TimedLine>();
            public List<string> PlainLines = new List<string>();
        }

        public static SubsonicLyricsResult ParseStructuredLyrics(IDictionary<string, object> lyricsListDict)
        {
            if (lyricsListDict == null)
                return SubsonicLyricsResult.Empty;

            if (!TryGetCaseInsensitive(lyricsListDict, "structuredLyrics", out var rawList) || rawList == null)
                return SubsonicLyricsResult.Empty;

            var entries = ParseCandidateEntries(rawList);
            if (entries.Count == 0)
                return SubsonicLyricsResult.Empty;

            CandidateEntry best = SelectBestEntry(entries);
            if (best == null)
                return SubsonicLyricsResult.Empty;

            if (best.Synced && best.TimedLines.Count > 0)
                return new SubsonicLyricsResult(FormatLrc(best.TimedLines), isSynced: true);

            if (best.PlainLines.Count > 0)
                return NormalizeRawLyrics(string.Join("\r\n", best.PlainLines));

            return SubsonicLyricsResult.Empty;
        }

        public static SubsonicLyricsResult NormalizeRawLyrics(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return SubsonicLyricsResult.Empty;

            string text = raw.Trim('\uFEFF', '\u200B', '\0');
            text = text.Replace("\r\n", "\n").Replace("\r", "\n");
            text = text.Trim();
            if (string.IsNullOrEmpty(text))
                return SubsonicLyricsResult.Empty;

            var tsMatch = LrcTimestampLineRegex.Match(text);
            bool hasTimestamps = tsMatch.Success;
            if (hasTimestamps)
            {
                int trimStart = tsMatch.Index;
                var metaMatch = LrcMetadataTagLineRegex.Match(text);
                if (metaMatch.Success && metaMatch.Index < trimStart)
                    trimStart = metaMatch.Index;
                if (trimStart > 0)
                    text = text.Substring(trimStart).TrimStart();
            }

            text = text.Replace("\n", "\r\n");
            bool isSynced = hasTimestamps && text.StartsWith("[", StringComparison.Ordinal);
            return new SubsonicLyricsResult(text, isSynced);
        }

        private static List<CandidateEntry> ParseCandidateEntries(object rawList)
        {
            var result = new List<CandidateEntry>();
            if (rawList is IDictionary<string, object> singleDict)
            {
                var parsed = ParseEntry(singleDict);
                if (parsed != null) result.Add(parsed);
                return result;
            }

            if (rawList is IEnumerable enumerable && !(rawList is string))
            {
                foreach (var item in enumerable)
                {
                    if (item is IDictionary<string, object> dict)
                    {
                        var parsed = ParseEntry(dict);
                        if (parsed != null) result.Add(parsed);
                    }
                }
            }
            return result;
        }

        private static CandidateEntry ParseEntry(IDictionary<string, object> dict)
        {
            if (!TryGetCaseInsensitive(dict, "line", out var lineObj) || lineObj == null)
                return null;

            bool synced = SubsonicJsonParser.GetBool(dict, "synced");
            int offset = SubsonicJsonParser.GetInt(dict, "offset");
            string kind = (SubsonicJsonParser.GetString(dict, "kind") ?? "").Trim().ToLowerInvariant();
            bool isMain = string.IsNullOrEmpty(kind) || kind == "main";

            var entry = new CandidateEntry
            {
                Synced = synced,
                IsMain = isMain,
                Offset = offset
            };

            int order = 0;
            void AddLineDict(IDictionary<string, object> lineDict)
            {
                string val = SubsonicJsonParser.GetString(lineDict, "value");
                if (val == null) val = "";

                if (TryGetLong(lineDict, "start", out long startMs))
                {
                    long effectiveMs = Math.Max(0L, startMs - offset);
                    string cleanVal = val.Replace("\r", " ").Replace("\n", " ");
                    entry.TimedLines.Add(new TimedLine
                    {
                        EffectiveMs = effectiveMs,
                        Order = order++,
                        Text = cleanVal
                    });
                }

                entry.PlainLines.Add(val);
            }

            if (lineObj is IDictionary<string, object> singleLine)
            {
                AddLineDict(singleLine);
            }
            else if (lineObj is IEnumerable lines && !(lineObj is string))
            {
                foreach (var item in lines)
                {
                    if (item is IDictionary<string, object> lineDict)
                        AddLineDict(lineDict);
                }
            }

            if (entry.TimedLines.Count == 0 && entry.PlainLines.Count == 0)
                return null;

            return entry;
        }

        private static CandidateEntry SelectBestEntry(List<CandidateEntry> entries)
        {
            CandidateEntry best = null;
            int bestScore = int.MinValue;

            foreach (var e in entries)
            {
                bool hasTimed = e.Synced && e.TimedLines.Count > 0;
                bool hasPlainText = HasNonEmptyText(e.PlainLines);
                if (!hasTimed && !hasPlainText)
                    continue;

                int score = 0;
                if (hasTimed) score += 100000;
                if (e.IsMain) score += 10000;
                score += Math.Min(9999, hasTimed ? e.TimedLines.Count : e.PlainLines.Count);

                if (score > bestScore)
                {
                    bestScore = score;
                    best = e;
                }
            }
            return best;
        }

        private static bool HasNonEmptyText(List<string> lines)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                if (!string.IsNullOrWhiteSpace(lines[i]))
                    return true;
            }
            return false;
        }

        private static string FormatLrc(List<TimedLine> lines)
        {
            lines.Sort((a, b) =>
            {
                int cmp = a.EffectiveMs.CompareTo(b.EffectiveMs);
                return cmp != 0 ? cmp : a.Order.CompareTo(b.Order);
            });

            var sb = new StringBuilder(lines.Count * 40);
            for (int i = 0; i < lines.Count; i++)
            {
                if (i > 0) sb.Append("\r\n");
                long ms = lines[i].EffectiveMs;
                long mins = ms / 60000L;
                long secs = (ms / 1000L) % 60L;
                long cs = (ms % 1000L) / 10L;

                sb.Append('[')
                  .Append(mins.ToString("D2", CultureInfo.InvariantCulture))
                  .Append(':')
                  .Append(secs.ToString("D2", CultureInfo.InvariantCulture))
                  .Append('.')
                  .Append(cs.ToString("D2", CultureInfo.InvariantCulture))
                  .Append(']')
                  .Append(lines[i].Text ?? "");
            }
            return sb.ToString();
        }

        private static bool TryGetCaseInsensitive(IDictionary<string, object> dict, string key, out object val)
        {
            if (dict.TryGetValue(key, out val))
                return true;
            foreach (var kvp in dict)
            {
                if (string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    val = kvp.Value;
                    return true;
                }
            }
            val = null;
            return false;
        }

        private static bool TryGetLong(IDictionary<string, object> dict, string key, out long result)
        {
            result = 0;
            if (!TryGetCaseInsensitive(dict, key, out var val) || val == null)
                return false;
            if (val is int iVal) { result = iVal; return true; }
            if (val is long lVal) { result = lVal; return true; }
            if (double.TryParse(val.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double dVal))
            {
                result = (long)Math.Round(dVal);
                return true;
            }
            return false;
        }
    }
}
