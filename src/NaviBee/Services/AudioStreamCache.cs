using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MusicBeePlugin
{
    public class AudioStreamCache
    {
        private static AudioStreamCache _instance;
        private static readonly object _instanceLock = new object();

        public static AudioStreamCache Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_instanceLock)
                    {
                        if (_instance == null)
                            _instance = new AudioStreamCache();
                    }
                }
                return _instance;
            }
        }

        private string _cacheDir;
        private readonly ConcurrentDictionary<string, object> _inFlightLocks = new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _downloadGate = new SemaphoreSlim(2, 2);
        private readonly object _ctsLock = new object();
        private CancellationTokenSource _sessionCts = new CancellationTokenSource();
        private CancellationTokenSource _prefetchCts;

        public AudioStreamCache()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            ConfigureStorage(Path.Combine(appData, "MusicBee"));
        }

        public void ConfigureStorage(string persistentRoot)
        {
            if (string.IsNullOrWhiteSpace(persistentRoot)) return;
            _cacheDir = Path.Combine(persistentRoot, "NavidromeCache", "Audio");
            try
            {
                if (!Directory.Exists(_cacheDir))
                    Directory.CreateDirectory(_cacheDir);
                CleanupOrphanTempFiles(_cacheDir);
            }
            catch { }
        }

        private static void CleanupOrphanTempFiles(string dir)
        {
            try
            {
                foreach (var f in Directory.GetFiles(dir))
                {
                    string name = Path.GetFileName(f);
                    if (name.Contains(".tmp_") || name.Contains(".tee_"))
                    {
                        try { File.Delete(f); } catch { }
                    }
                }
            }
            catch { }
        }

        public void StopBackgroundDownloads()
        {
            lock (_ctsLock)
            {
                try { _prefetchCts?.Cancel(); } catch { }
                try { _prefetchCts?.Dispose(); } catch { }
                _prefetchCts = null;

                var oldSession = _sessionCts;
                _sessionCts = new CancellationTokenSource();
                try { oldSession?.Cancel(); } catch { }
                try { oldSession?.Dispose(); } catch { }
            }
        }

        private CancellationToken GetSessionToken()
        {
            lock (_ctsLock)
            {
                return _sessionCts.Token;
            }
        }

        private static string GetSafeId(string songId)
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

        public string GetTrackFilePath(SubsonicSong song, int transcodeBitrate = 0)
        {
            if (song == null || string.IsNullOrEmpty(song.Id)) return null;
            string safeId = GetSafeId(song.Id);
            if (transcodeBitrate > 0)
                return Path.Combine(_cacheDir, $"{safeId}.br{transcodeBitrate}.mp3");

            string ext = !string.IsNullOrEmpty(song.Suffix) ? SubsonicUrlHelper.Sanitize(song.Suffix.TrimStart('.')) : "mp3";
            return Path.Combine(_cacheDir, $"{safeId}.{ext}");
        }

        public static bool IsValidDownloadedSize(long actualBytes, long httpContentLength, SubsonicSong song, int transcodeBitrate)
        {
            if (actualBytes <= 0) return false;
            if (httpContentLength > 0)
                return actualBytes == httpContentLength;
            if (transcodeBitrate <= 0 && song != null && song.Size > 0)
                return actualBytes >= (long)(song.Size * 0.90);
            return true;
        }

        public Stream TryOpenCachedStream(SubsonicSong song, int transcodeBitrate = 0)
        {
            string path = GetTrackFilePath(song, transcodeBitrate);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return null;

            try
            {
                var fi = new FileInfo(path);
                if (fi.Length > 0)
                {
                    try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch { }
                    return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                }
            }
            catch { }
            return null;
        }

        public string CreateTeeTempPath(SubsonicSong song, int transcodeBitrate = 0)
        {
            string target = GetTrackFilePath(song, transcodeBitrate);
            if (string.IsNullOrEmpty(target)) return null;
            return target + ".tee_" + Guid.NewGuid().ToString("N");
        }

        public void CommitTeeFile(SubsonicSong song, int transcodeBitrate, string tempPath, int maxCacheMb)
        {
            if (song == null || string.IsNullOrEmpty(tempPath) || !File.Exists(tempPath))
                return;

            string target = GetTrackFilePath(song, transcodeBitrate);
            if (string.IsNullOrEmpty(target))
            {
                try { File.Delete(tempPath); } catch { }
                return;
            }

            object lockObj = _inFlightLocks.GetOrAdd(target, _ => new object());
            lock (lockObj)
            {
                try
                {
                    var fi = new FileInfo(tempPath);
                    if (!IsValidDownloadedSize(fi.Length, -1, song, transcodeBitrate))
                    {
                        try { File.Delete(tempPath); } catch { }
                        return;
                    }

                    if (File.Exists(target)) File.Delete(target);
                    File.Move(tempPath, target);
                    try { File.SetLastWriteTimeUtc(target, DateTime.UtcNow); } catch { }
                    EnforceQuota(maxCacheMb);
                }
                catch
                {
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                }
                finally
                {
                    _inFlightLocks.TryRemove(target, out _);
                }
            }
        }

        public void StartBackgroundCacheTrack(SubsonicSong song, SubsonicClient client, int maxCacheMb, int transcodeBitrate = 0, CancellationToken ct = default)
        {
            if (song == null || client == null || maxCacheMb == 0) return;
            string path = GetTrackFilePath(song, transcodeBitrate);
            if (string.IsNullOrEmpty(path) || File.Exists(path)) return;

            CancellationToken sessionToken = GetSessionToken();
            object lockObj = _inFlightLocks.GetOrAdd(path, _ => new object());
            Task.Run(() =>
            {
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, sessionToken))
                {
                    var token = linked.Token;
                    bool gateAcquired = false;
                    string tmpPath = null;
                    try
                    {
                        _downloadGate.Wait(token);
                        gateAcquired = true;

                        lock (lockObj)
                        {
                            if (token.IsCancellationRequested || File.Exists(path)) return;
                            tmpPath = path + ".tmp_" + Guid.NewGuid().ToString("N");

                            Uri uri = client.GetStreamUri(song.Id, transcodeBitrate);
                            var req = (HttpWebRequest)WebRequest.Create(uri);
                            req.Timeout = 30000;
                            req.ReadWriteTimeout = 30000;
                            SubsonicClient.ConfigureRequest(req);

                            long contentLength = -1;
                            using (var reg = token.Register(() => { try { req.Abort(); } catch { } }))
                            using (var resp = (HttpWebResponse)req.GetResponse())
                            {
                                if (SubsonicClient.IsNonAudioErrorContentType(resp.ContentType))
                                    return;

                                contentLength = resp.ContentLength;
                                using (var inStream = resp.GetResponseStream())
                                using (var outStream = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                                {
                                    if (inStream != null)
                                    {
                                        byte[] buf = new byte[32768];
                                        int read;
                                        while ((read = inStream.Read(buf, 0, buf.Length)) > 0)
                                        {
                                            if (token.IsCancellationRequested)
                                                return;
                                            outStream.Write(buf, 0, read);
                                        }
                                    }
                                }
                            }

                            if (token.IsCancellationRequested)
                                return;

                            var fi = new FileInfo(tmpPath);
                            if (!fi.Exists || !IsValidDownloadedSize(fi.Length, contentLength, song, transcodeBitrate))
                                return;

                            if (File.Exists(path)) File.Delete(path);
                            File.Move(tmpPath, path);
                            tmpPath = null;
                            try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch { }
                            EnforceQuota(maxCacheMb);
                        }
                    }
                    catch { }
                    finally
                    {
                        if (!string.IsNullOrEmpty(tmpPath))
                        {
                            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                        }
                        if (gateAcquired)
                        {
                            try { _downloadGate.Release(); } catch { }
                        }
                        _inFlightLocks.TryRemove(path, out _);
                    }
                }
            });
        }

        public void PrefetchNextTrack(SubsonicSong nextSong, SubsonicClient client, NavidromeSettings settings, CancellationToken ct = default)
        {
            if (nextSong == null || client == null || settings == null || !settings.PrefetchNextTrack)
                return;

            CancellationToken prefetchToken;
            lock (_ctsLock)
            {
                try { _prefetchCts?.Cancel(); } catch { }
                try { _prefetchCts?.Dispose(); } catch { }
                _prefetchCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _sessionCts.Token);
                prefetchToken = _prefetchCts.Token;
            }

            Task.Run(() =>
            {
                try
                {
                    if (prefetchToken.IsCancellationRequested) return;
                    string cId = !string.IsNullOrEmpty(nextSong.CanonicalCoverId) ? nextSong.CanonicalCoverId : nextSong.CoverArt;
                    if (!string.IsNullOrEmpty(cId))
                        client.GetCoverArt(cId, settings.ArtworkSize);
                    if (!prefetchToken.IsCancellationRequested)
                        StartBackgroundCacheTrack(nextSong, client, settings.AudioCacheMaxMb, settings.TranscodeBitrate, prefetchToken);
                }
                catch { }
            }, prefetchToken);
        }

        private void EnforceQuota(int maxCacheMb)
        {
            if (maxCacheMb <= 0) return;
            try
            {
                var dir = new DirectoryInfo(_cacheDir);
                var files = dir.GetFiles().Where(f => !f.Name.Contains(".tmp_") && !f.Name.Contains(".tee_")).ToArray();
                long maxBytes = (long)maxCacheMb * 1024 * 1024;
                long total = files.Sum(f => f.Length);

                if (total > maxBytes)
                {
                    long targetBytes = (long)(maxBytes * 0.85);
                    foreach (var f in files.OrderBy(f => f.LastWriteTimeUtc))
                    {
                        try
                        {
                            long len = f.Length;
                            f.Delete();
                            total -= len;
                            if (total <= targetBytes) break;
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        public void GetCacheStats(out int fileCount, out long totalBytes)
        {
            fileCount = 0;
            totalBytes = 0;
            try
            {
                if (Directory.Exists(_cacheDir))
                {
                    var files = new DirectoryInfo(_cacheDir).GetFiles().Where(f => !f.Name.Contains(".tmp_") && !f.Name.Contains(".tee_")).ToArray();
                    fileCount = files.Length;
                    totalBytes = files.Sum(f => f.Length);
                }
            }
            catch { }
        }

        public void ClearCache()
        {
            StopBackgroundDownloads();
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
    }
}
