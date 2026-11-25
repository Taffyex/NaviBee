using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
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
        private readonly object _saveTimerLock = new object();
        private Timer _debouncedSaveTimer;
        private volatile bool _hasPendingSave;

        private List<SubsonicSong> _allSongs = new List<SubsonicSong>();
        private KeyValuePair<byte, string>[][] _allTagArrays = new KeyValuePair<byte, string>[0][];
        private Dictionary<string, SubsonicSong> _byPath = new Dictionary<string, SubsonicSong>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, SubsonicSong> _byUniqueFileName = new Dictionary<string, SubsonicSong>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, SubsonicSong> _byId = new Dictionary<string, SubsonicSong>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, SubsonicSong> _byServerSuffix3 = new Dictionary<string, SubsonicSong>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, SubsonicSong> _byServerSuffix2 = new Dictionary<string, SubsonicSong>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, SubsonicSong> _byServerStem3 = new Dictionary<string, SubsonicSong>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, SubsonicSong> _byServerStem2 = new Dictionary<string, SubsonicSong>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, SubsonicSong> _byServerDiscSuffix = new Dictionary<string, SubsonicSong>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, SubsonicSong> _byServerDiscStem = new Dictionary<string, SubsonicSong>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, SubsonicSong> _byMetaExact = new Dictionary<string, SubsonicSong>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, List<SubsonicSong>> _byMetaLoose = new Dictionary<string, List<SubsonicSong>>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, List<SubsonicSong>> _byAlbumTitle = new Dictionary<string, List<SubsonicSong>>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, List<SubsonicSong>> _byArtistTitle = new Dictionary<string, List<SubsonicSong>>(StringComparer.OrdinalIgnoreCase);
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
        public int ServerSongCount { get { lock (_lock) { return _meta.ServerSongCount; } } }
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

        public bool IsServerChanged(SubsonicClient client)
        {
            if (client == null) return false;
            lock (_lock)
            {
                return !string.IsNullOrEmpty(_meta.ServerUrl) &&
                       !string.Equals(client.ServerUrlNormalized, _meta.ServerUrl, StringComparison.OrdinalIgnoreCase);
            }
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
                string fname = SubsonicUrlHelper.GetFileNameSafe(normalized);
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

        public void LoadSongsForTesting(IEnumerable<SubsonicSong> songs)
        {
            var list = songs != null ? new List<SubsonicSong>(songs) : new List<SubsonicSong>();
            lock (_lock)
            {
                _meta = new CatalogMetadata { ServerSongCount = list.Count };
                BuildIndexes(list);
            }
        }
    }
}
