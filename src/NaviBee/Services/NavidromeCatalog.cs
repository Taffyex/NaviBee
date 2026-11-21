using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace MusicBeePlugin
{
    public partial class NavidromeCatalog
    {
        private static NavidromeCatalog _instance;
        private static readonly object _instanceLock = new object();

        public static NavidromeCatalog Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_instanceLock)
                    {
                        if (_instance == null)
                            _instance = new NavidromeCatalog();
                    }
                }
                return _instance;
            }
        }

        private string _catalogFilePath;
        private string _metaFilePath;
        private readonly object _lock = new object();
        private readonly object _diskWriteLock = new object();
        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer { MaxJsonLength = 100 * 1024 * 1024 };

        private List<SubsonicSong> _allSongs = new List<SubsonicSong>();
        private KeyValuePair<byte, string>[][] _allTagArrays = new KeyValuePair<byte, string>[0][];
        private Dictionary<string, SubsonicSong> _byPath = new Dictionary<string, SubsonicSong>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, SubsonicSong> _byUniqueFileName = new Dictionary<string, SubsonicSong>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, SubsonicSong> _byId = new Dictionary<string, SubsonicSong>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, List<SubsonicSong>> _byArtist = new Dictionary<string, List<SubsonicSong>>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, List<SubsonicSong>> _byAlbumKey = new Dictionary<string, List<SubsonicSong>>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, List<string>> _albumsByArtist = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, string> _coverToCanonical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, string> _coverBySanitizedAlbumDir = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, string> _coverBySanitizedAlbumName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private List<string> _allAlbumKeys = new List<string>();
        private List<string> _distinctAlbumCoverIds = new List<string>();
        private List<SubsonicSong> _starredSongs = new List<SubsonicSong>();
        private List<SubsonicSong> _topRatedSongs = new List<SubsonicSong>();
        private List<SubsonicSong> _mostPlayedSongs = new List<SubsonicSong>();
        private CatalogMetadata _meta = new CatalogMetadata();

        public int SongCount { get { lock (_lock) { return _allSongs.Count; } } }
        public bool HasData { get { lock (_lock) { return _allSongs.Count > 0; } } }

        public NavidromeCatalog()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            ConfigureStorage(Path.Combine(appData, "MusicBee"));
        }

        public void ConfigureStorage(string persistentRoot)
        {
            if (string.IsNullOrWhiteSpace(persistentRoot)) return;
            string cacheDir = Path.Combine(persistentRoot, "NavidromeCache");
            try
            {
                if (!Directory.Exists(cacheDir))
                    Directory.CreateDirectory(cacheDir);
            }
            catch { }
            lock (_lock)
            {
                _catalogFilePath = Path.Combine(cacheDir, "catalog.json");
                _metaFilePath = Path.Combine(cacheDir, "catalog_meta.json");
            }
        }

        public bool LoadFromDisk()
        {
            lock (_lock)
            {
                if (File.Exists(_metaFilePath))
                {
                    try { _meta = _serializer.Deserialize<CatalogMetadata>(File.ReadAllText(_metaFilePath, Encoding.UTF8)) ?? new CatalogMetadata(); }
                    catch { _meta = new CatalogMetadata(); }
                }

                if (!File.Exists(_catalogFilePath)) return false;
                try
                {
                    string json = File.ReadAllText(_catalogFilePath, Encoding.UTF8);
                    if (string.IsNullOrEmpty(json)) return false;
                    var songs = _serializer.Deserialize<List<SubsonicSong>>(json);
                    if (songs == null || songs.Count == 0) return false;
                    BuildIndexes(songs);
                    return true;
                }
                catch { return false; }
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
                metaSnapshot = _meta;
                catPath = _catalogFilePath;
                metaPath = _metaFilePath;
            }

            WriteSnapshotToDisk(snapshot, metaSnapshot, catPath, metaPath);
        }

        private void WriteSnapshotToDisk(List<SubsonicSong> snapshot, CatalogMetadata metaSnapshot, string catPath, string metaPath)
        {
            if (string.IsNullOrEmpty(catPath) || string.IsNullOrEmpty(metaPath)) return;
            lock (_diskWriteLock)
            {
                try
                {
                    string json = _serializer.Serialize(snapshot);
                    string tmp = catPath + ".tmp_" + Guid.NewGuid().ToString("N");
                    File.WriteAllText(tmp, json, Encoding.UTF8);
                    if (File.Exists(catPath)) File.Delete(catPath);
                    File.Move(tmp, catPath);

                    string metaJson = _serializer.Serialize(metaSnapshot);
                    File.WriteAllText(metaPath, metaJson, Encoding.UTF8);
                }
                catch { }
            }
        }

        public bool IsServerChanged(SubsonicClient client)
        {
            if (client == null) return false;
            lock (_lock)
            {
                return !string.IsNullOrEmpty(_meta.ServerUrl) &&
                       !string.Equals(client.ServerUrlNormalized, _meta.ServerUrl, StringComparison.OrdinalIgnoreCase);
            }
        }

        public bool SmartSyncIfNeeded(SubsonicClient client, int artworkSize, Action<string> progressCallback, out bool catalogChanged)
        {
            catalogChanged = false;
            if (client == null) return false;

            var scanStatus = client.GetScanStatus();
            if (scanStatus != null && HasData)
            {
                lock (_lock)
                {
                    bool sameServer = string.IsNullOrEmpty(_meta.ServerUrl) ||
                                      string.Equals(client.ServerUrlNormalized, _meta.ServerUrl, StringComparison.OrdinalIgnoreCase);
                    if (sameServer &&
                        scanStatus.Count == _meta.ServerSongCount &&
                        string.Equals(scanStatus.LastScan, _meta.LastScan, StringComparison.OrdinalIgnoreCase))
                    {
                        progressCallback?.Invoke($"Navidrome: Instant cache hit ({SongCount} tracks ready)");
                        ArtworkCache.Instance.StartBackgroundPrewarm(GetDistinctAlbumCoverIds(), client, progressCallback, artworkSize);
                        return true;
                    }
                }
            }

            catalogChanged = true;
            return SyncFromServer(client, progressCallback, scanStatus, artworkSize);
        }

        public bool SyncFromServer(SubsonicClient client, Action<string> progressCallback, SubsonicScanStatus scanStatus = null, int artworkSize = 800)
        {
            if (client == null) return false;
            progressCallback?.Invoke("Navidrome: Syncing library catalog...");
            var fetched = new List<SubsonicSong>();
            int offset = 0;
            const int pageSize = 1000;

            while (true)
            {
                var page = client.SearchSongs(pageSize, offset);
                if (page == null || page.Count == 0) break;
                fetched.AddRange(page);
                progressCallback?.Invoke($"Navidrome: Synced {fetched.Count} tracks...");
                if (page.Count < pageSize) break;
                offset += page.Count;
            }

            // Fallback for older Subsonic/Airsonic servers where search3?query= returns empty
            if (fetched.Count == 0)
            {
                var quoted = client.SearchSongs(pageSize, 0, "\"\"");
                if (quoted != null && quoted.Count > 0)
                {
                    fetched.AddRange(quoted);
                    int qOffset = quoted.Count;
                    while (quoted.Count >= pageSize)
                    {
                        quoted = client.SearchSongs(pageSize, qOffset, "\"\"");
                        if (quoted == null || quoted.Count == 0) break;
                        fetched.AddRange(quoted);
                        qOffset += quoted.Count;
                    }
                }
            }

            if (fetched.Count == 0)
            {
                int albOffset = 0;
                const int albPage = 500;
                while (true)
                {
                    var albums = client.GetAlbumList2("alphabeticalByName", albPage, albOffset);
                    if (albums == null || albums.Count == 0) break;
                    foreach (var alb in albums)
                        fetched.AddRange(client.GetAlbum(alb.Id));
                    progressCallback?.Invoke($"Navidrome: Synced {fetched.Count} tracks...");
                    if (albums.Count < albPage) break;
                    albOffset += albums.Count;
                }
            }

            if (fetched.Count == 0) return false;
            if (scanStatus == null) scanStatus = client.GetScanStatus();

            lock (_lock)
            {
                _meta = new CatalogMetadata
                {
                    LastScan = scanStatus?.LastScan ?? "",
                    ServerSongCount = scanStatus != null && scanStatus.Count > 0 ? scanStatus.Count : fetched.Count,
                    ServerUrl = client.ServerUrlNormalized,
                    ArtworkSize = artworkSize
                };
                BuildIndexes(fetched);
            }
            SaveToDisk();

            progressCallback?.Invoke($"Navidrome: {fetched.Count} tracks ready");
            ArtworkCache.Instance.StartBackgroundPrewarm(GetDistinctAlbumCoverIds(), client, progressCallback, artworkSize);
            return true;
        }

        public KeyValuePair<byte, string>[][] GetAllTagArrays() { lock (_lock) return _allTagArrays ?? new KeyValuePair<byte, string>[0][]; }
        public List<string> GetDistinctAlbumCoverIds() { lock (_lock) return new List<string>(_distinctAlbumCoverIds); }
        public List<SubsonicSong> GetAllSongs() { lock (_lock) return new List<SubsonicSong>(_allSongs); }
        public List<SubsonicSong> GetStarredSongs() { lock (_lock) return new List<SubsonicSong>(_starredSongs); }
        public List<SubsonicSong> GetTopRatedSongs() { lock (_lock) return new List<SubsonicSong>(_topRatedSongs); }
        public List<SubsonicSong> GetMostPlayedSongs() { lock (_lock) return new List<SubsonicSong>(_mostPlayedSongs); }
        public List<string> GetArtists() { lock (_lock) return _byArtist.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList(); }
        public List<string> GetAllAlbumKeys() { lock (_lock) return new List<string>(_allAlbumKeys); }

        public List<string> GetAlbumsForArtist(string artistFolder)
        {
            lock (_lock)
                return _albumsByArtist.TryGetValue(artistFolder, out var list) ? new List<string>(list) : new List<string>();
        }

        public List<SubsonicSong> GetSongsByArtist(string artistFolder)
        {
            lock (_lock)
                return _byArtist.TryGetValue(artistFolder, out var list) ? new List<SubsonicSong>(list) : new List<SubsonicSong>();
        }

        public List<SubsonicSong> GetSongsByAlbumKey(string albumKey)
        {
            lock (_lock)
                return _byAlbumKey.TryGetValue(albumKey, out var list) ? new List<SubsonicSong>(list) : new List<SubsonicSong>();
        }

        public SubsonicSong FindSongByPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string normalized = path.Replace('/', '\\');
            lock (_lock)
            {
                if (_byPath.TryGetValue(normalized, out var s)) return s;
                string clean = normalized.TrimStart('\\');
                if (_byPath.TryGetValue(clean, out s)) return s;
                string fname = Path.GetFileName(normalized);
                if (!string.IsNullOrEmpty(fname) && _byUniqueFileName.TryGetValue(fname, out s) && s != null) return s;
                return null;
            }
        }

        public SubsonicSong FindSongById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            lock (_lock)
            {
                _byId.TryGetValue(id, out var s);
                return s;
            }
        }

        public void UpdateStarred(string songId, bool starred)
        {
            if (string.IsNullOrEmpty(songId)) return;
            bool changed = false;
            lock (_lock)
            {
                if (_byId.TryGetValue(songId, out var s))
                {
                    s.Starred = starred;
                    if (starred && !_starredSongs.Contains(s))
                        _starredSongs.Add(s);
                    else if (!starred)
                        _starredSongs.Remove(s);
                    s.CachedTags = null;
                    s.CachedTags = SubsonicUrlHelper.ToTags(s);
                    changed = true;
                }
            }
            if (changed)
                Task.Run(() => SaveToDisk());
        }

        public void UpdateRating(string songId, int userRating)
        {
            if (string.IsNullOrEmpty(songId)) return;
            int clamped = Math.Max(0, Math.Min(5, userRating));
            bool changed = false;
            lock (_lock)
            {
                if (_byId.TryGetValue(songId, out var s))
                {
                    s.UserRating = clamped;
                    if (clamped >= 3 && !_topRatedSongs.Contains(s))
                        _topRatedSongs.Add(s);
                    else if (clamped < 3)
                        _topRatedSongs.Remove(s);
                    s.CachedTags = null;
                    s.CachedTags = SubsonicUrlHelper.ToTags(s);
                    changed = true;
                }
            }
            if (changed)
                Task.Run(() => SaveToDisk());
        }
    }
}
