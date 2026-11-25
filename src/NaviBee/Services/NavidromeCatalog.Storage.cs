using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace MusicBeePlugin
{
    public partial class NavidromeCatalog
    {
        public bool LoadFromDisk()
        {
            lock (_lock)
            {
                var ser = new JavaScriptSerializer { MaxJsonLength = 100 * 1024 * 1024 };
                if (!string.IsNullOrEmpty(_metaFilePath) && File.Exists(_metaFilePath))
                {
                    try { _meta = ser.Deserialize<CatalogMetadata>(File.ReadAllText(_metaFilePath, Encoding.UTF8)) ?? new CatalogMetadata(); }
                    catch { _meta = new CatalogMetadata(); }
                }

                if (string.IsNullOrEmpty(_catalogFilePath) || !File.Exists(_catalogFilePath))
                    return false;

                try
                {
                    if (TryReadCatalogSongsStreaming(_catalogFilePath, out var streamedSongs))
                    {
                        if (streamedSongs == null || streamedSongs.Count == 0) return false;
                        BuildIndexes(streamedSongs);
                        return true;
                    }

                    string json = File.ReadAllText(_catalogFilePath, Encoding.UTF8);
                    if (string.IsNullOrEmpty(json)) return false;
                    var songs = ser.Deserialize<List<SubsonicSong>>(json);
                    if (songs == null || songs.Count == 0) return false;
                    BuildIndexes(songs);
                    return true;
                }
                catch { return false; }
            }
        }

        public void ScheduleSaveToDisk(int delayMs = 3000)
        {
            _hasPendingSave = true;
            lock (_saveTimerLock)
            {
                if (_debouncedSaveTimer == null)
                {
                    _debouncedSaveTimer = new Timer(_ =>
                    {
                        if (!_hasPendingSave) return;
                        _hasPendingSave = false;
                        SaveToDisk();
                    }, null, delayMs, Timeout.Infinite);
                }
                else
                {
                    _debouncedSaveTimer.Change(delayMs, Timeout.Infinite);
                }
            }
        }

        public void FlushPendingSave()
        {
            lock (_saveTimerLock)
            {
                _debouncedSaveTimer?.Dispose();
                _debouncedSaveTimer = null;
            }
            if (_hasPendingSave)
            {
                _hasPendingSave = false;
                SaveToDisk();
            }
        }

        public void SaveToDisk()
        {
            List<SubsonicSong> snapshot;
            CatalogMetadata metaSnapshot;
            string catPath;
            string metaPath;

            lock (_lock)
            {
                snapshot = new List<SubsonicSong>(_allSongs);
                metaSnapshot = new CatalogMetadata
                {
                    LastScan = _meta.LastScan,
                    ServerSongCount = _meta.ServerSongCount,
                    ServerUrl = _meta.ServerUrl,
                    ArtworkSize = _meta.ArtworkSize,
                    LastUserStateSyncUtc = _meta.LastUserStateSyncUtc
                };
                catPath = _catalogFilePath;
                metaPath = _metaFilePath;
            }

            _hasPendingSave = false;
            WriteSnapshotToDisk(snapshot, metaSnapshot, catPath, metaPath);
        }

        private void WriteSnapshotToDisk(List<SubsonicSong> snapshot, CatalogMetadata metaSnapshot, string catPath, string metaPath)
        {
            if (string.IsNullOrEmpty(catPath) || string.IsNullOrEmpty(metaPath)) return;
            lock (_diskWriteLock)
            {
                try
                {
                    WriteCatalogSongsStreaming(catPath, snapshot);

                    var metaSer = new JavaScriptSerializer();
                    string metaJson = metaSer.Serialize(metaSnapshot);
                    WriteTextAtomic(metaPath, metaJson);
                }
                catch { }
            }
        }

        private static bool TryReadCatalogSongsStreaming(string catPath, out List<SubsonicSong> songs)
        {
            songs = null;
            using (var reader = new StreamReader(catPath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 65536))
            {
                string firstLine = null;
                while ((firstLine = reader.ReadLine()) != null)
                {
                    firstLine = firstLine.Trim();
                    if (firstLine.Length > 0) break;
                }

                if (!string.Equals(firstLine, "[", StringComparison.Ordinal))
                    return false;

                var list = new List<SubsonicSong>(1024);
                var lineSer = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 };
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string trimmed = line.Trim();
                    if (trimmed.Length == 0) continue;
                    if (string.Equals(trimmed, "]", StringComparison.Ordinal))
                    {
                        songs = list;
                        return true;
                    }

                    if (trimmed[trimmed.Length - 1] == ',')
                        trimmed = trimmed.Substring(0, trimmed.Length - 1).TrimEnd();

                    if (trimmed.Length < 2 || trimmed[0] != '{' || trimmed[trimmed.Length - 1] != '}')
                        return false;

                    var song = lineSer.Deserialize<SubsonicSong>(trimmed);
                    if (song != null)
                        list.Add(song);
                }
            }
            return false;
        }

        private static void WriteCatalogSongsStreaming(string catPath, List<SubsonicSong> snapshot)
        {
            string tmp = catPath + ".tmp_" + Guid.NewGuid().ToString("N");
            try
            {
                string dir = Path.GetDirectoryName(catPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var sb = new StringBuilder(512);
                using (var writer = new StreamWriter(tmp, false, new UTF8Encoding(false), 65536))
                {
                    writer.WriteLine("[");
                    int count = snapshot != null ? snapshot.Count : 0;
                    bool wroteAny = false;
                    for (int i = 0; i < count; i++)
                    {
                        var s = snapshot[i];
                        if (s == null) continue;
                        if (wroteAny)
                            writer.WriteLine(",");
                        sb.Clear();
                        AppendCompactSongJson(sb, s);
                        writer.Write(sb.ToString());
                        wroteAny = true;
                    }
                    if (wroteAny)
                        writer.WriteLine();
                    writer.WriteLine("]");
                }

                ReplaceFileAtomic(tmp, catPath);
                tmp = null;
            }
            finally
            {
                if (!string.IsNullOrEmpty(tmp))
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                }
            }
        }

        private static void AppendCompactSongJson(StringBuilder sb, SubsonicSong s)
        {
            sb.Append('{');
            bool first = true;
            AppendStringField(sb, "Id", s.Id, ref first);
            AppendStringField(sb, "Title", s.Title, ref first);
            AppendStringField(sb, "Artist", s.Artist, ref first);
            AppendStringField(sb, "AlbumArtist", s.AlbumArtist, ref first);
            AppendStringField(sb, "Album", s.Album, ref first);
            AppendStringField(sb, "AlbumId", s.AlbumId, ref first);
            AppendStringField(sb, "ArtistId", s.ArtistId, ref first);
            AppendIntField(sb, "Year", s.Year, 0, ref first);
            AppendIntField(sb, "Track", s.Track, 0, ref first);
            AppendIntField(sb, "DiscNumber", s.DiscNumber, 1, ref first);
            AppendStringField(sb, "Genre", s.Genre, ref first);
            AppendStringField(sb, "CoverArt", s.CoverArt, ref first);
            AppendLongField(sb, "Size", s.Size, 0L, ref first);
            AppendStringField(sb, "ContentType", s.ContentType, ref first);
            AppendStringField(sb, "Suffix", s.Suffix, ref first);
            AppendIntField(sb, "Duration", s.Duration, 0, ref first);
            AppendIntField(sb, "BitRate", s.BitRate, 0, ref first);
            AppendStringField(sb, "Path", s.Path, ref first);
            AppendIntField(sb, "PlayCount", s.PlayCount, 0, ref first);
            AppendBoolField(sb, "Starred", s.Starred, ref first);
            AppendIntField(sb, "UserRating", s.UserRating, 0, ref first);
            AppendBoolField(sb, "IsDir", s.IsDir, ref first);
            AppendStringField(sb, "Created", s.Created, ref first);
            AppendStringField(sb, "Played", s.Played, ref first);
            AppendStringField(sb, "Comment", s.Comment, ref first);
            AppendIntField(sb, "SamplingRate", s.SamplingRate, 0, ref first);
            AppendIntField(sb, "ChannelCount", s.ChannelCount, 0, ref first);
            AppendIntField(sb, "BitDepth", s.BitDepth, 0, ref first);
            sb.Append('}');
        }

        private static void AppendFieldPrefix(StringBuilder sb, string name, ref bool first)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append('"').Append(name).Append("\":");
        }

        private static void AppendStringField(StringBuilder sb, string name, string val, ref bool first)
        {
            if (string.IsNullOrEmpty(val)) return;
            AppendFieldPrefix(sb, name, ref first);
            sb.Append('"');
            for (int i = 0; i < val.Length; i++)
            {
                char c = val[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else if (char.IsHighSurrogate(c))
                        {
                            if (i + 1 < val.Length && char.IsLowSurrogate(val[i + 1]))
                            {
                                sb.Append(c);
                                sb.Append(val[++i]);
                            }
                        }
                        else if (!char.IsLowSurrogate(c))
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        private static void AppendIntField(StringBuilder sb, string name, int val, int defaultVal, ref bool first)
        {
            if (val == defaultVal) return;
            AppendFieldPrefix(sb, name, ref first);
            sb.Append(val.ToString(CultureInfo.InvariantCulture));
        }

        private static void AppendLongField(StringBuilder sb, string name, long val, long defaultVal, ref bool first)
        {
            if (val == defaultVal) return;
            AppendFieldPrefix(sb, name, ref first);
            sb.Append(val.ToString(CultureInfo.InvariantCulture));
        }

        private static void AppendBoolField(StringBuilder sb, string name, bool val, ref bool first)
        {
            if (!val) return;
            AppendFieldPrefix(sb, name, ref first);
            sb.Append("true");
        }

        private static void ReplaceFileAtomic(string tmpPath, string targetPath)
        {
            if (File.Exists(targetPath))
            {
                try
                {
                    File.Replace(tmpPath, targetPath, null, ignoreMetadataErrors: true);
                    return;
                }
                catch { }
                File.Delete(targetPath);
            }
            File.Move(tmpPath, targetPath);
        }

        internal static void WriteTextAtomic(string targetPath, string content)
        {
            string tmp = targetPath + ".tmp_" + Guid.NewGuid().ToString("N");
            try
            {
                string dir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(tmp, content, Encoding.UTF8);
                ReplaceFileAtomic(tmp, targetPath);
                tmp = null;
            }
            finally
            {
                if (!string.IsNullOrEmpty(tmp))
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                }
            }
        }
    }
}
