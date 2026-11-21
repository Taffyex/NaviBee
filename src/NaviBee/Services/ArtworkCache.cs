using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MusicBeePlugin
{
    public class ArtworkCache
    {
        private static ArtworkCache _instance;
        private static readonly object _instanceLock = new object();

        public static ArtworkCache Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_instanceLock)
                    {
                        if (_instance == null)
                            _instance = new ArtworkCache();
                    }
                }
                return _instance;
            }
        }

        private string _cacheDir;
        private readonly ConcurrentDictionary<string, byte[]> _memCache = new ConcurrentDictionary<string, byte[]>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, object> _inFlightLocks = new ConcurrentDictionary<string, object>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, object> _fileLocks = new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        private const int MaxMemCacheEntries = 1000;
        private CancellationTokenSource _prewarmCts;

        public ArtworkCache()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            ConfigureStorage(Path.Combine(appData, "MusicBee"));
        }

        public void ConfigureStorage(string persistentRoot)
        {
            if (string.IsNullOrWhiteSpace(persistentRoot)) return;
            _cacheDir = Path.Combine(persistentRoot, "NavidromeCache", "Artwork");
            try
            {
                if (!Directory.Exists(_cacheDir))
                    Directory.CreateDirectory(_cacheDir);
            }
            catch { }
        }

        public void ClearMemoryCache() => _memCache.Clear();

        private static string MemKey(string coverId, int size) => $"{coverId}@{size}";

        public string GetCacheFilePath(string coverId, int size = 800)
        {
            if (string.IsNullOrEmpty(coverId)) return null;
            string hex;
            using (var md5 = MD5.Create())
            {
                byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(coverId));
                hex = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
            string suffix = size > 0 ? $"_s{size}" : "_orig";
            return Path.Combine(_cacheDir, $"{hex}{suffix}.jpg");
        }

        private static bool HasValidImageFileHeader(string filePath)
        {
            try
            {
                var fi = new FileInfo(filePath);
                if (!fi.Exists || fi.Length < 12) return false;
                byte[] header = new byte[12];
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    int n = fs.Read(header, 0, 12);
                    if (n < 12) return false;
                }
                return SubsonicClient.IsValidImageBytes(header);
            }
            catch
            {
                return false;
            }
        }

        public string GetLocalFilePathIfExists(string coverId, int size = 800)
        {
            string p = GetCacheFilePath(coverId, size);
            if (p != null && File.Exists(p))
            {
                if (HasValidImageFileHeader(p))
                    return p;
                try { File.Delete(p); } catch { }
            }
            return null;
        }

        public static void WriteFileAtomic(string filePath, byte[] data)
        {
            if (string.IsNullOrEmpty(filePath) || !SubsonicClient.IsValidImageBytes(data)) return;
            object l = _fileLocks.GetOrAdd(filePath, _ => new object());
            lock (l)
            {
                try
                {
                    string dir = Path.GetDirectoryName(filePath);
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    string tmp = filePath + ".tmp_" + Guid.NewGuid().ToString("N");
                    File.WriteAllBytes(tmp, data);
                    if (File.Exists(filePath))
                    {
                        try { File.Delete(filePath); } catch { }
                    }
                    File.Move(tmp, filePath);
                }
                catch { }
            }
        }

        public string GetOrFetchLocalFilePath(string coverId, SubsonicClient client, int size = 800)
        {
            if (string.IsNullOrEmpty(coverId)) return null;
            string existing = GetLocalFilePathIfExists(coverId, size);
            if (existing != null) return existing;

            string filePath = GetCacheFilePath(coverId, size);
            if (_memCache.TryGetValue(MemKey(coverId, size), out var memData) && SubsonicClient.IsValidImageBytes(memData))
            {
                WriteFileAtomic(filePath, memData);
                if (File.Exists(filePath)) return filePath;
            }

            if (client != null)
            {
                byte[] fetched = GetArtwork(coverId, size, (id, s) => client.GetCoverArtDirect(id, s));
                if (SubsonicClient.IsValidImageBytes(fetched) && File.Exists(filePath))
                    return filePath;
            }
            return null;
        }

        public byte[] GetArtwork(string coverId, int size, Func<string, int, byte[]> remoteFetcher)
        {
            if (string.IsNullOrEmpty(coverId)) return null;
            string key = MemKey(coverId, size);
            if (_memCache.TryGetValue(key, out var memData)) return memData;

            string filePath = GetCacheFilePath(coverId, size);
            byte[] cached = TryReadDisk(key, filePath);
            if (cached != null) return cached;
            if (remoteFetcher == null) return null;

            object lockObj = _inFlightLocks.GetOrAdd(key, _ => new object());
            lock (lockObj)
            {
                if (_memCache.TryGetValue(key, out memData)) return memData;
                cached = TryReadDisk(key, filePath);
                if (cached != null) return cached;

                try
                {
                    byte[] remoteData = remoteFetcher(coverId, size);
                    if (SubsonicClient.IsValidImageBytes(remoteData))
                    {
                        PutMemory(coverId, size, remoteData);
                        if (filePath != null) WriteFileAtomic(filePath, remoteData);
                        return remoteData;
                    }
                }
                finally { _inFlightLocks.TryRemove(key, out _); }
            }
            return null;
        }

        private byte[] TryReadDisk(string memKey, string filePath)
        {
            if (filePath == null || !File.Exists(filePath)) return null;
            try
            {
                byte[] diskData = File.ReadAllBytes(filePath);
                if (SubsonicClient.IsValidImageBytes(diskData))
                {
                    PutMemoryByKey(memKey, diskData);
                    return diskData;
                }
                // Purge poisoned non-image cache entry
                try { File.Delete(filePath); } catch { }
            }
            catch { }
            return null;
        }

        public void PutMemory(string coverId, int size, byte[] data)
        {
            if (string.IsNullOrEmpty(coverId)) return;
            PutMemoryByKey(MemKey(coverId, size), data);
        }

        private void PutMemoryByKey(string key, byte[] data)
        {
            if (string.IsNullOrEmpty(key) || !SubsonicClient.IsValidImageBytes(data)) return;
            if (_memCache.Count >= MaxMemCacheEntries)
            {
                int toRemove = 150;
                foreach (var k in _memCache.Keys)
                {
                    _memCache.TryRemove(k, out _);
                    if (--toRemove <= 0) break;
                }
            }
            _memCache[key] = data;
        }

        public void StartBackgroundPrewarm(IEnumerable<string> coverIds, SubsonicClient client, Action<string> progressCallback, int targetSize = 800)
        {
            if (coverIds == null || client == null) return;
            lock (_instanceLock)
            {
                _prewarmCts?.Cancel();
                _prewarmCts = new CancellationTokenSource();
            }
            var token = _prewarmCts.Token;

            Task.Run(() =>
            {
                try
                {
                    var needed = coverIds
                        .Where(id => !string.IsNullOrEmpty(id))
                        .Distinct(StringComparer.Ordinal)
                        .Where(id => GetLocalFilePathIfExists(id, targetSize) == null)
                        .ToList();
                    if (needed.Count == 0) return;

                    int total = needed.Count;
                    int done = 0;
                    progressCallback?.Invoke($"Navidrome: Caching artwork (0 / {total})...");

                    var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token };
                    Parallel.ForEach(needed, parallelOptions, id =>
                    {
                        if (token.IsCancellationRequested) return;
                        GetArtwork(id, targetSize, (cId, sz) => client.GetCoverArtDirect(cId, sz));
                        int count = Interlocked.Increment(ref done);
                        if (count % 10 == 0 || count == total)
                            progressCallback?.Invoke($"Navidrome: Caching artwork ({count} / {total})...");
                    });
                    progressCallback?.Invoke("Navidrome: Artwork cache ready");
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { progressCallback?.Invoke($"Navidrome: Artwork prewarm notice: {ex.Message}"); }
            }, token);
        }

        public void UpgradeAndRecacheArtwork(IEnumerable<string> canonicalCoverIds, SubsonicClient client, int targetSize, Action<string> progressCallback, Action onCompleted)
        {
            if (canonicalCoverIds == null || client == null) return;
            StopPrewarm();
            ClearMemoryCache();

            Task.Run(() =>
            {
                try
                {
                    var distinct = canonicalCoverIds.Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal).ToList();
                    int total = distinct.Count;
                    int done = 0;
                    progressCallback?.Invoke($"Navidrome: Upgrading {total} album covers to high-res...");

                    Parallel.ForEach(distinct, new ParallelOptions { MaxDegreeOfParallelism = 4 }, id =>
                    {
                        byte[] data = client.GetCoverArtDirect(id, targetSize);
                        if (SubsonicClient.IsValidImageBytes(data))
                        {
                            PutMemory(id, targetSize, data);
                            string p = GetCacheFilePath(id, targetSize);
                            if (p != null) WriteFileAtomic(p, data);
                        }
                        int c = Interlocked.Increment(ref done);
                        if (c % 10 == 0 || c == total)
                            progressCallback?.Invoke($"Navidrome: Upgraded {c} / {total} album covers...");
                    });

                    progressCallback?.Invoke($"Navidrome: High-res artwork upgrade complete ({done} covers).");
                    onCompleted?.Invoke();
                }
                catch (Exception ex) { progressCallback?.Invoke($"Navidrome: Artwork upgrade error: {ex.Message}"); }
            });
        }

        public void GetCacheStats(out int fileCount, out long totalBytes)
        {
            fileCount = 0;
            totalBytes = 0;
            try
            {
                if (Directory.Exists(_cacheDir))
                {
                    var files = new DirectoryInfo(_cacheDir).GetFiles("*.jpg");
                    fileCount = files.Length;
                    totalBytes = files.Sum(f => f.Length);
                }
            }
            catch { }
        }

        public void ClearDiskCache()
        {
            ClearMemoryCache();
            try
            {
                if (Directory.Exists(_cacheDir))
                {
                    foreach (var f in Directory.GetFiles(_cacheDir))
                    {
                        try { File.Delete(f); } catch { }
                    }
                }
            }
            catch { }
        }

        public void StopPrewarm()
        {
            lock (_instanceLock)
            {
                _prewarmCts?.Cancel();
                _prewarmCts = null;
            }
        }
    }
}
