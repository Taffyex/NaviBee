using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

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
        public string Url, PidTag, Artist, AlbumArtist, Album, Title;
        public int DiscNo, TrackNo, DurationSeconds;
    }

    public class LocalLinkSummary
    {
        public int Scanned { get; set; }
        public int Linked { get; set; }
        public int NewlyLinked { get; set; }
        public int PidEmbedded { get; set; }
        public int Unmatched { get; set; }
        public int MatchedByPid { get; set; }
        public int MatchedByPath { get; set; }
        public int MatchedByMetadata { get; set; }
    }

    public class LocalLibraryLinker
    {
        private static LocalLibraryLinker _instance;
        private static readonly object _instanceLock = new object();

        public static LocalLibraryLinker Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_instanceLock)
                    {
                        if (_instance == null) _instance = new LocalLibraryLinker();
                    }
                }
                return _instance;
            }
        }

        private readonly object _lock = new object();
        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 };
        private readonly Dictionary<string, LocalTrackLink> _byLocalPath = new Dictionary<string, LocalTrackLink>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<string>> _localPathsBySongId = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        private string _filePath;

        public int LinkCount { get { lock (_lock) { return _byLocalPath.Count; } } }

        public LocalLibraryLinker()
        {
            ConfigureStorage(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MusicBee"));
        }

        public static bool IsVirtualNavidromeUrl(string url) =>
            !string.IsNullOrWhiteSpace(url) &&
            (url.StartsWith("Navidrome:", StringComparison.OrdinalIgnoreCase) ||
             url.StartsWith("subsonic://", StringComparison.OrdinalIgnoreCase));

        internal static string NormalizeLocalPath(string path) =>
            string.IsNullOrWhiteSpace(path) ? "" : path.Trim().Replace('/', '\\');

        internal static void ExtractPathSuffixes(string rawPath, out string suf3, out string suf2, out string stem3, out string stem2)
        {
            suf3 = suf2 = stem3 = stem2 = null;
            if (string.IsNullOrWhiteSpace(rawPath)) return;

            string[] parts = rawPath.Trim().Replace('/', '\\').Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return;

            string file = parts[parts.Length - 1];
            string ext = Path.GetExtension(file);
            string stem = !string.IsNullOrEmpty(ext) ? file.Substring(0, file.Length - ext.Length) : file;
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
                }
            }
        }

        internal static string NormalizeMetaToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            var sb = new StringBuilder(value.Length);
            bool lastSpace = false;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '\u2018' || c == '\u2019') c = '\'';
                else if (c == '\u2013' || c == '\u2014') c = '-';
                if (char.IsWhiteSpace(c))
                {
                    if (sb.Length > 0 && !lastSpace) { sb.Append(' '); lastSpace = true; }
                }
                else
                {
                    sb.Append(char.ToLowerInvariant(c));
                    lastSpace = false;
                }
            }
            if (sb.Length > 0 && sb[sb.Length - 1] == ' ') sb.Length--;
            return sb.ToString();
        }

        public void ConfigureStorage(string persistentRoot)
        {
            if (string.IsNullOrWhiteSpace(persistentRoot)) return;
            string dir = Path.Combine(persistentRoot, "NavidromeCache");
            try { if (!Directory.Exists(dir)) Directory.CreateDirectory(dir); } catch { }

            lock (_lock)
            {
                _filePath = Path.Combine(dir, "local_links.json");
                LoadLocked();
            }
        }

        public void Clear(bool persist = false)
        {
            lock (_lock)
            {
                _byLocalPath.Clear();
                _localPathsBySongId.Clear();
                if (persist) SaveLocked();
            }
        }

        private void LoadLocked()
        {
            _byLocalPath.Clear();
            _localPathsBySongId.Clear();
            if (string.IsNullOrEmpty(_filePath) || !File.Exists(_filePath)) return;
            try
            {
                string json = File.ReadAllText(_filePath, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json)) return;
                var items = _serializer.Deserialize<List<LocalTrackLink>>(json);
                if (items == null) return;
                foreach (var item in items)
                {
                    if (item == null || string.IsNullOrWhiteSpace(item.LocalPath) || string.IsNullOrWhiteSpace(item.SongId))
                        continue;
                    item.LocalPath = NormalizeLocalPath(item.LocalPath);
                    IndexLinkLocked(item);
                }
            }
            catch { }
        }

        private void IndexLinkLocked(LocalTrackLink link)
        {
            if (_byLocalPath.TryGetValue(link.LocalPath, out var existing) &&
                !string.Equals(existing.SongId, link.SongId, StringComparison.OrdinalIgnoreCase))
                RemoveReverseIndexLocked(existing.SongId, existing.LocalPath);

            _byLocalPath[link.LocalPath] = link;
            if (!_localPathsBySongId.TryGetValue(link.SongId, out var list))
            {
                list = new List<string>(1);
                _localPathsBySongId[link.SongId] = list;
            }
            if (!list.Any(p => string.Equals(p, link.LocalPath, StringComparison.OrdinalIgnoreCase)))
                list.Add(link.LocalPath);
        }

        private void RemoveReverseIndexLocked(string songId, string localPath)
        {
            if (string.IsNullOrEmpty(songId) || !_localPathsBySongId.TryGetValue(songId, out var list)) return;
            list.RemoveAll(p => string.Equals(p, localPath, StringComparison.OrdinalIgnoreCase));
            if (list.Count == 0) _localPathsBySongId.Remove(songId);
        }

        private void SaveLocked()
        {
            if (string.IsNullOrEmpty(_filePath)) return;
            string tmp = _filePath + ".tmp_" + Guid.NewGuid().ToString("N");
            try
            {
                string dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(tmp, _serializer.Serialize(_byLocalPath.Values.ToList()), Encoding.UTF8);
                if (File.Exists(_filePath)) File.Delete(_filePath);
                File.Move(tmp, _filePath);
                tmp = null;
            }
            catch { }
            finally
            {
                if (!string.IsNullOrEmpty(tmp))
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        public void SaveToDisk() { lock (_lock) { SaveLocked(); } }

        public bool TryGetSongId(string localUrl, out string songId)
        {
            songId = null;
            string key = NormalizeLocalPath(localUrl);
            if (string.IsNullOrEmpty(key)) return false;
            lock (_lock)
            {
                if (_byLocalPath.TryGetValue(key, out var link) && !string.IsNullOrEmpty(link.SongId))
                {
                    songId = link.SongId;
                    return true;
                }
                return false;
            }
        }

        public bool TryGetLinkedSong(string localUrl, out SubsonicSong song, NavidromeCatalog catalog = null)
        {
            song = null;
            if (!TryGetSongId(localUrl, out var id)) return false;
            song = (catalog ?? NavidromeCatalog.Instance).FindSongById(id);
            return song != null;
        }

        public bool IsSongLinkedToLocal(string songId)
        {
            if (string.IsNullOrWhiteSpace(songId)) return false;
            lock (_lock)
                return _localPathsBySongId.TryGetValue(songId, out var list) && list.Count > 0;
        }

        public List<KeyValuePair<string, string>> GetAllLinkedLocalFiles()
        {
            lock (_lock)
                return _byLocalPath.Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value.SongId)).ToList();
        }

        public bool UpsertLink(string localUrl, string songId, string matchTier, bool persist = false)
        {
            string key = NormalizeLocalPath(localUrl);
            if (string.IsNullOrEmpty(key) || string.IsNullOrWhiteSpace(songId)) return false;
            lock (_lock)
            {
                bool changed = !_byLocalPath.TryGetValue(key, out var prev) ||
                               !string.Equals(prev.SongId, songId, StringComparison.OrdinalIgnoreCase);
                IndexLinkLocked(new LocalTrackLink
                {
                    LocalPath = key,
                    SongId = songId,
                    MatchTier = matchTier ?? "",
                    LinkedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
                });
                if (persist && changed) SaveLocked();
                return changed;
            }
        }

        public SubsonicSong MatchTrack(LocalTrackInfo track, NavidromeCatalog catalog, out string matchTier)
        {
            matchTier = null;
            if (catalog == null || string.IsNullOrWhiteSpace(track.Url) || IsVirtualNavidromeUrl(track.Url))
                return null;

            if (!string.IsNullOrWhiteSpace(track.PidTag))
            {
                var byPid = catalog.FindSongById(track.PidTag.Trim());
                if (byPid != null) { matchTier = "pid_tag"; return byPid; }
            }
            if (TryGetSongId(track.Url, out var mappedId))
            {
                var byMap = catalog.FindSongById(mappedId);
                if (byMap != null) { matchTier = "link_map"; return byMap; }
            }
            var byPath = catalog.FindSongByServerPathSuffix(track.Url);
            if (byPath != null) { matchTier = "path_suffix"; return byPath; }

            var byMeta = catalog.FindSongByMetadata(
                track.Artist, track.AlbumArtist, track.Album,
                track.DiscNo, track.TrackNo, track.Title, track.DurationSeconds);
            if (byMeta != null) { matchTier = "metadata"; return byMeta; }
            return null;
        }

        public LocalLinkSummary LinkLocalTracks(
            IEnumerable<LocalTrackInfo> localTracks,
            NavidromeCatalog catalog,
            NavidromeSettings settings,
            Action<string, MetaDataType, string> writePidTag = null,
            bool pruneMissingLocalPaths = true,
            CancellationToken ct = default)
        {
            var summary = new LocalLinkSummary();
            if (localTracks == null || catalog == null || !catalog.HasData) return summary;

            MetaDataType pidField = MetaDataType.Custom16;
            bool shouldEmbed = settings != null && settings.TryGetPidMetaDataType(out pidField) && writePidTag != null;
            var seenLocalPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool anyDirty = false;

            foreach (var track in localTracks)
            {
                if (ct.IsCancellationRequested) break;
                if (string.IsNullOrWhiteSpace(track.Url) || IsVirtualNavidromeUrl(track.Url)) continue;

                summary.Scanned++;
                string normPath = NormalizeLocalPath(track.Url);
                seenLocalPaths.Add(normPath);

                var song = MatchTrack(track, catalog, out var tier);
                if (song == null) { summary.Unmatched++; continue; }

                summary.Linked++;
                if (tier == "pid_tag" || tier == "link_map") summary.MatchedByPid++;
                else if (tier == "path_suffix") summary.MatchedByPath++;
                else if (tier == "metadata") summary.MatchedByMetadata++;

                if (UpsertLink(normPath, song.Id, tier, persist: false))
                {
                    summary.NewlyLinked++;
                    anyDirty = true;
                }

                if (shouldEmbed && !string.Equals((track.PidTag ?? "").Trim(), song.Id, StringComparison.OrdinalIgnoreCase))
                {
                    try { writePidTag(track.Url, pidField, song.Id); summary.PidEmbedded++; }
                    catch { }
                }
            }

            if (pruneMissingLocalPaths && !ct.IsCancellationRequested)
            {
                lock (_lock)
                {
                    var stale = _byLocalPath.Keys
                        .Where(p => !seenLocalPaths.Contains(p) || catalog.FindSongById(_byLocalPath[p].SongId) == null)
                        .ToList();
                    foreach (var p in stale)
                    {
                        var old = _byLocalPath[p];
                        _byLocalPath.Remove(p);
                        RemoveReverseIndexLocked(old.SongId, p);
                        anyDirty = true;
                    }
                }
            }

            if (anyDirty) SaveToDisk();
            return summary;
        }
    }
}
