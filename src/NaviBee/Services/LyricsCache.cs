using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace MusicBeePlugin
{
    /// <summary>Caches fetched OpenSubsonic/Subsonic lyrics in memory and under NavidromeCache\Lyrics.</summary>
    public sealed class LyricsCache
    {
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        public static LyricsCache Instance { get; } = new LyricsCache();

        private string _cacheDir;
        private readonly object _diskLock = new object();
        private readonly ConcurrentDictionary<string, SubsonicLyricsResult> _memCache =
            new ConcurrentDictionary<string, SubsonicLyricsResult>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, Lazy<Task<SubsonicLyricsResult>>> _inFlight =
            new ConcurrentDictionary<string, Lazy<Task<SubsonicLyricsResult>>>(StringComparer.OrdinalIgnoreCase);

        public LyricsCache()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            ConfigureStorage(Path.Combine(appData, "MusicBee"));
        }

        public void ConfigureStorage(string persistentRoot)
        {
            if (string.IsNullOrWhiteSpace(persistentRoot)) return;
            string nextDir = Path.Combine(persistentRoot, "NavidromeCache", "Lyrics");
            if (!string.Equals(_cacheDir, nextDir, StringComparison.OrdinalIgnoreCase))
            {
                _cacheDir = nextDir;
                _memCache.Clear();
            }
            try
            {
                if (!Directory.Exists(_cacheDir))
                    Directory.CreateDirectory(_cacheDir);
            }
            catch { }
        }

        private static string GetSafeKey(string songId)
        {
            if (string.IsNullOrEmpty(songId)) return "unknown";
            char[] invalid = Path.GetInvalidFileNameChars();
            if (songId.IndexOfAny(invalid) >= 0 || songId.Length > 60)
            {
                using (var md5 = MD5.Create())
                {
                    byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(songId));
                    return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                }
            }
            return songId;
        }

        private string GetLrcPath(string songId) => Path.Combine(_cacheDir, GetSafeKey(songId) + ".lrc");
        private string GetTxtPath(string songId) => Path.Combine(_cacheDir, GetSafeKey(songId) + ".txt");

        public bool TryGetCached(string songId, out SubsonicLyricsResult result)
        {
            result = SubsonicLyricsResult.Empty;
            if (string.IsNullOrWhiteSpace(songId))
                return false;

            if (_memCache.TryGetValue(songId, out var mem))
            {
                result = mem ?? SubsonicLyricsResult.Empty;
                return true;
            }

            try
            {
                string lrcPath = GetLrcPath(songId);
                if (File.Exists(lrcPath))
                {
                    string raw = File.ReadAllText(lrcPath, Encoding.UTF8);
                    var parsed = SubsonicLyricsFormatter.NormalizeRawLyrics(raw);
                    if (parsed.HasLyrics)
                    {
                        var lrcResult = new SubsonicLyricsResult(parsed.Text, isSynced: true);
                        _memCache[songId] = lrcResult;
                        result = lrcResult;
                        return true;
                    }
                }

                string txtPath = GetTxtPath(songId);
                if (File.Exists(txtPath))
                {
                    string raw = File.ReadAllText(txtPath, Encoding.UTF8);
                    var parsed = SubsonicLyricsFormatter.NormalizeRawLyrics(raw);
                    if (parsed.HasLyrics)
                    {
                        _memCache[songId] = parsed;
                        result = parsed;
                        return true;
                    }
                }
            }
            catch { }

            return false;
        }

        public Task<SubsonicLyricsResult> GetOrFetchAsync(SubsonicSong song, SubsonicClient client)
        {
            if (song == null || string.IsNullOrWhiteSpace(song.Id) || client == null)
                return Task.FromResult(SubsonicLyricsResult.Empty);

            if (TryGetCached(song.Id, out var cached))
                return Task.FromResult(cached);

            var lazy = _inFlight.GetOrAdd(song.Id, id => new Lazy<Task<SubsonicLyricsResult>>(() =>
                Task.Run(async () =>
                {
                    try
                    {
                        return await Task.FromResult(FetchAndCache(song, client)).ConfigureAwait(false);
                    }
                    finally
                    {
                        _inFlight.TryRemove(id, out _);
                    }
                })));

            return lazy.Value;
        }

        public void PrefetchAsync(SubsonicSong song, SubsonicClient client)
        {
            if (song == null || string.IsNullOrWhiteSpace(song.Id) || client == null)
                return;
            if (TryGetCached(song.Id, out _))
                return;

            _ = GetOrFetchAsync(song, client).ContinueWith(t => { var _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }

        private SubsonicLyricsResult FetchAndCache(SubsonicSong song, SubsonicClient client)
        {
            if (TryGetCached(song.Id, out var cached))
                return cached;

            var fetched = client.GetBestLyrics(song);
            if (fetched != null && fetched.HasLyrics)
            {
                SaveToDisk(song.Id, fetched);
                _memCache[song.Id] = fetched;
                return fetched;
            }

            if (client.LastError == null)
                _memCache[song.Id] = SubsonicLyricsResult.Empty;

            return SubsonicLyricsResult.Empty;
        }

        private void SaveToDisk(string songId, SubsonicLyricsResult result)
        {
            if (string.IsNullOrWhiteSpace(_cacheDir) || result == null || !result.HasLyrics)
                return;

            lock (_diskLock)
            {
                try
                {
                    if (!Directory.Exists(_cacheDir))
                        Directory.CreateDirectory(_cacheDir);

                    string target = result.IsSynced ? GetLrcPath(songId) : GetTxtPath(songId);
                    string stale = result.IsSynced ? GetTxtPath(songId) : GetLrcPath(songId);
                    string tmp = target + ".tmp." + Guid.NewGuid().ToString("N");

                    File.WriteAllText(tmp, result.Text, Utf8NoBom);
                    if (File.Exists(target)) File.Delete(target);
                    File.Move(tmp, target);
                    if (File.Exists(stale)) File.Delete(stale);
                }
                catch { }
            }
        }

        public void GetCacheStats(out int fileCount, out long totalBytes)
        {
            fileCount = 0;
            totalBytes = 0;
            try
            {
                if (!Directory.Exists(_cacheDir)) return;
                foreach (var fi in new DirectoryInfo(_cacheDir).GetFiles())
                {
                    if (fi.Extension.Equals(".lrc", StringComparison.OrdinalIgnoreCase) ||
                        fi.Extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
                    {
                        fileCount++;
                        totalBytes += fi.Length;
                    }
                }
            }
            catch { }
        }

        public void ClearMemoryCache() => _memCache.Clear();

        public void ClearCache()
        {
            _memCache.Clear();
            lock (_diskLock)
            {
                try
                {
                    if (!Directory.Exists(_cacheDir)) return;
                    foreach (var file in Directory.GetFiles(_cacheDir))
                    {
                        try { File.Delete(file); } catch { }
                    }
                }
                catch { }
            }
        }
    }
}
