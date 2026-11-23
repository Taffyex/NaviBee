using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace MusicBeePlugin
{
    public sealed class PendingScrobbleEntry
    {
        public string SongId { get; set; } = "";
        public long TimestampMs { get; set; }
    }

    public sealed class PendingActionsState
    {
        public List<PendingScrobbleEntry> Scrobbles { get; set; } = new List<PendingScrobbleEntry>();
        public Dictionary<string, bool> Stars { get; set; } = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> Ratings { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class PendingSyncQueue
    {
        private const int MaxPendingScrobbles = 500;
        private static PendingSyncQueue _instance;
        private static readonly object _instanceLock = new object();

        public static PendingSyncQueue Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_instanceLock)
                    {
                        if (_instance == null)
                            _instance = new PendingSyncQueue();
                    }
                }
                return _instance;
            }
        }

        private readonly object _lock = new object();
        private readonly object _diskLock = new object();
        private string _filePath;
        private List<PendingScrobbleEntry> _scrobbles = new List<PendingScrobbleEntry>();
        private Dictionary<string, bool> _stars = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, int> _ratings = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public PendingSyncQueue()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            ConfigureStorage(Path.Combine(appData, "MusicBee"));
        }

        public int PendingCount
        {
            get
            {
                lock (_lock)
                {
                    return _scrobbles.Count + _stars.Count + _ratings.Count;
                }
            }
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
                _filePath = Path.Combine(cacheDir, "pending_actions.json");
                _scrobbles.Clear();
                _stars.Clear();
                _ratings.Clear();
            }
            LoadFromDisk();
        }

        public void LoadFromDisk()
        {
            string path;
            lock (_lock) { path = _filePath; }
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

            try
            {
                string json = File.ReadAllText(path, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json)) return;
                var ser = new JavaScriptSerializer { MaxJsonLength = 10 * 1024 * 1024 };
                var state = ser.Deserialize<PendingActionsState>(json);
                if (state == null) return;

                lock (_lock)
                {
                    _scrobbles = new List<PendingScrobbleEntry>();
                    if (state.Scrobbles != null)
                    {
                        foreach (var e in state.Scrobbles)
                        {
                            if (e != null && !string.IsNullOrEmpty(e.SongId))
                                _scrobbles.Add(e);
                        }
                        if (_scrobbles.Count > MaxPendingScrobbles)
                            _scrobbles.RemoveRange(0, _scrobbles.Count - MaxPendingScrobbles);
                    }

                    _stars = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                    if (state.Stars != null)
                    {
                        foreach (var kv in state.Stars)
                        {
                            if (!string.IsNullOrEmpty(kv.Key))
                                _stars[kv.Key] = kv.Value;
                        }
                    }

                    _ratings = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    if (state.Ratings != null)
                    {
                        foreach (var kv in state.Ratings)
                        {
                            if (!string.IsNullOrEmpty(kv.Key))
                                _ratings[kv.Key] = Math.Max(0, Math.Min(5, kv.Value));
                        }
                    }
                }
            }
            catch { }
        }

        public void EnqueueScrobble(string songId, long timestampMs)
        {
            if (string.IsNullOrEmpty(songId)) return;
            lock (_lock)
            {
                _scrobbles.Add(new PendingScrobbleEntry { SongId = songId, TimestampMs = timestampMs });
                if (_scrobbles.Count > MaxPendingScrobbles)
                    _scrobbles.RemoveRange(0, _scrobbles.Count - MaxPendingScrobbles);
            }
            SaveToDisk();
        }

        public void EnqueueStar(string songId, bool starred)
        {
            if (string.IsNullOrEmpty(songId)) return;
            lock (_lock) { _stars[songId] = starred; }
            SaveToDisk();
        }

        public void EnqueueRating(string songId, int rating)
        {
            if (string.IsNullOrEmpty(songId)) return;
            int clamped = Math.Max(0, Math.Min(5, rating));
            lock (_lock) { _ratings[songId] = clamped; }
            SaveToDisk();
        }

        public bool TryGetPendingStar(string songId, out bool starred)
        {
            starred = false;
            if (string.IsNullOrEmpty(songId)) return false;
            lock (_lock) { return _stars.TryGetValue(songId, out starred); }
        }

        public bool TryGetPendingRating(string songId, out int rating)
        {
            rating = 0;
            if (string.IsNullOrEmpty(songId)) return false;
            lock (_lock) { return _ratings.TryGetValue(songId, out rating); }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _scrobbles.Clear();
                _stars.Clear();
                _ratings.Clear();
            }
            SaveToDisk();
        }

        public int FlushPending(SubsonicClient client, CancellationToken ct = default)
        {
            if (client == null || ct.IsCancellationRequested) return 0;

            List<PendingScrobbleEntry> scrobblesSnap;
            List<KeyValuePair<string, bool>> starsSnap;
            List<KeyValuePair<string, int>> ratingsSnap;

            lock (_lock)
            {
                if (_scrobbles.Count == 0 && _stars.Count == 0 && _ratings.Count == 0)
                    return 0;
                scrobblesSnap = new List<PendingScrobbleEntry>(_scrobbles);
                starsSnap = _stars.ToList();
                ratingsSnap = _ratings.ToList();
            }

            int flushed = 0;
            var flushedScrobbles = new HashSet<PendingScrobbleEntry>();
            var flushedStars = new List<KeyValuePair<string, bool>>();
            var flushedRatings = new List<KeyValuePair<string, int>>();

            foreach (var entry in scrobblesSnap)
            {
                if (ct.IsCancellationRequested) break;
                if (client.Scrobble(entry.SongId, submission: true, entry.TimestampMs))
                {
                    flushedScrobbles.Add(entry);
                    flushed++;
                }
            }

            foreach (var kv in starsSnap)
            {
                if (ct.IsCancellationRequested) break;
                if (client.Star(kv.Key, kv.Value))
                {
                    flushedStars.Add(kv);
                    flushed++;
                }
            }

            foreach (var kv in ratingsSnap)
            {
                if (ct.IsCancellationRequested) break;
                if (client.SetRating(kv.Key, kv.Value))
                {
                    flushedRatings.Add(kv);
                    flushed++;
                }
            }

            if (flushed > 0)
            {
                lock (_lock)
                {
                    if (flushedScrobbles.Count > 0)
                        _scrobbles.RemoveAll(e => flushedScrobbles.Contains(e));

                    foreach (var kv in flushedStars)
                    {
                        if (_stars.TryGetValue(kv.Key, out bool cur) && cur == kv.Value)
                            _stars.Remove(kv.Key);
                    }

                    foreach (var kv in flushedRatings)
                    {
                        if (_ratings.TryGetValue(kv.Key, out int cur) && cur == kv.Value)
                            _ratings.Remove(kv.Key);
                    }
                }
                SaveToDisk();
            }

            return flushed;
        }

        private void SaveToDisk()
        {
            PendingActionsState snapshot;
            string path;
            lock (_lock)
            {
                path = _filePath;
                if (string.IsNullOrEmpty(path)) return;
                snapshot = new PendingActionsState
                {
                    Scrobbles = new List<PendingScrobbleEntry>(_scrobbles),
                    Stars = new Dictionary<string, bool>(_stars, StringComparer.OrdinalIgnoreCase),
                    Ratings = new Dictionary<string, int>(_ratings, StringComparer.OrdinalIgnoreCase)
                };
            }

            lock (_diskLock)
            {
                try
                {
                    var ser = new JavaScriptSerializer { MaxJsonLength = 10 * 1024 * 1024 };
                    NavidromeCatalog.WriteTextAtomic(path, ser.Serialize(snapshot));
                }
                catch { }
            }
        }
    }
}
