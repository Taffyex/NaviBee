using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
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
            }
            catch { }
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
                    // Size check only applies to un-transcoded original audio
                    if (transcodeBitrate <= 0 && song.Size > 0 && fi.Length < song.Size)
                        return null;

                    File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
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
                    if (fi.Length == 0 || (transcodeBitrate <= 0 && song.Size > 0 && fi.Length < song.Size))
                    {
                        try { File.Delete(tempPath); } catch { }
                        return;
                    }

                    if (File.Exists(target)) File.Delete(target);
                    File.Move(tempPath, target);
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

        public void StartBackgroundCacheTrack(SubsonicSong song, SubsonicClient client, int maxCacheMb, int transcodeBitrate = 0)
        {
            if (song == null || client == null || maxCacheMb == 0) return;
            string path = GetTrackFilePath(song, transcodeBitrate);
            if (string.IsNullOrEmpty(path) || File.Exists(path)) return;

            object lockObj = _inFlightLocks.GetOrAdd(path, _ => new object());
            Task.Run(() =>
            {
                lock (lockObj)
                {
                    if (File.Exists(path)) return;
                    string tmpPath = path + ".tmp_" + Guid.NewGuid().ToString("N");
                    try
                    {
                        Uri uri = client.GetStreamUri(song.Id, transcodeBitrate);
                        var req = (HttpWebRequest)WebRequest.Create(uri);
                        req.Timeout = 30000;
                        req.ReadWriteTimeout = 30000;
                        SubsonicClient.ConfigureRequest(req);

                        using (var resp = (HttpWebResponse)req.GetResponse())
                        {
                            if (SubsonicClient.IsNonAudioErrorContentType(resp.ContentType))
                                return;

                            using (var inStream = resp.GetResponseStream())
                            using (var outStream = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                            {
                                inStream?.CopyTo(outStream);
                            }
                        }

                        var fi = new FileInfo(tmpPath);
                        if (!fi.Exists || fi.Length == 0 || (transcodeBitrate <= 0 && song.Size > 0 && fi.Length < song.Size))
                        {
                            try { File.Delete(tmpPath); } catch { }
                            return;
                        }

                        if (File.Exists(path)) File.Delete(path);
                        File.Move(tmpPath, path);
                        EnforceQuota(maxCacheMb);
                    }
                    catch
                    {
                        try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                    }
                    finally
                    {
                        _inFlightLocks.TryRemove(path, out _);
                    }
                }
            });
        }

        public void PrefetchNextTrack(SubsonicSong nextSong, SubsonicClient client, NavidromeSettings settings)
        {
            if (nextSong == null || client == null || settings == null || !settings.PrefetchNextTrack)
                return;

            Task.Run(() =>
            {
                try
                {
                    string cId = !string.IsNullOrEmpty(nextSong.CanonicalCoverId) ? nextSong.CanonicalCoverId : nextSong.CoverArt;
                    if (!string.IsNullOrEmpty(cId))
                        client.GetCoverArt(cId, settings.ArtworkSize);
                    StartBackgroundCacheTrack(nextSong, client, settings.AudioCacheMaxMb, settings.TranscodeBitrate);
                }
                catch { }
            });
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
                    foreach (var f in files.OrderBy(f => f.LastAccessTimeUtc))
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
