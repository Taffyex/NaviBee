using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace MusicBeePlugin
{
    public class PlaylistBackupTrack
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
        public string Album { get; set; } = "";
        public string Path { get; set; } = "";
    }

    public class PlaylistBackupSnapshot
    {
        public string BackupUtc { get; set; } = "";
        public string ServerId { get; set; } = "";
        public string Name { get; set; } = "";
        public string Owner { get; set; } = "";
        public string Comment { get; set; } = "";
        public int TrackCount { get; set; }
        public List<PlaylistBackupTrack> Tracks { get; set; } = new List<PlaylistBackupTrack>();
    }

    public class PlaylistBackup
    {
        private const int MaxBackupsPerPlaylist = 20;

        private static PlaylistBackup _instance;
        private static readonly object _instanceLock = new object();

        public static PlaylistBackup Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_instanceLock)
                    {
                        if (_instance == null)
                            _instance = new PlaylistBackup();
                    }
                }
                return _instance;
            }
        }

        private readonly object _lock = new object();
        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 };
        private string _backupDir;

        public string BackupDirectory => _backupDir;

        public PlaylistBackup()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            ConfigureStorage(Path.Combine(appData, "MusicBee"));
        }

        public void ConfigureStorage(string persistentRoot)
        {
            if (string.IsNullOrWhiteSpace(persistentRoot)) return;
            _backupDir = Path.Combine(persistentRoot, "NavidromeCache", "PlaylistBackups");
            try
            {
                if (!Directory.Exists(_backupDir))
                    Directory.CreateDirectory(_backupDir);
            }
            catch { }
        }

        public bool SaveBackup(SubsonicPlaylist meta, IReadOnlyList<SubsonicSong> songs)
        {
            if (meta == null || string.IsNullOrWhiteSpace(_backupDir))
                return false;

            string safeName = SubsonicUrlHelper.Sanitize(!string.IsNullOrWhiteSpace(meta.Name) ? meta.Name : meta.Id);
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            string targetPath = Path.Combine(_backupDir, $"{safeName}_{stamp}.json");
            string tmpPath = targetPath + ".tmp_" + Guid.NewGuid().ToString("N");

            var snapshot = new PlaylistBackupSnapshot
            {
                BackupUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ServerId = meta.Id ?? "",
                Name = meta.Name ?? "",
                Owner = meta.Owner ?? "",
                Comment = meta.Comment ?? "",
                TrackCount = songs?.Count ?? 0,
                Tracks = (songs ?? new List<SubsonicSong>()).Select(s => new PlaylistBackupTrack
                {
                    Id = s?.Id ?? "",
                    Title = s?.Title ?? "",
                    Artist = s?.Artist ?? "",
                    Album = s?.Album ?? "",
                    Path = s?.Path ?? ""
                }).ToList()
            };

            lock (_lock)
            {
                try
                {
                    if (!Directory.Exists(_backupDir))
                        Directory.CreateDirectory(_backupDir);

                    string json = _serializer.Serialize(snapshot);
                    File.WriteAllText(tmpPath, json, Encoding.UTF8);

                    var fi = new FileInfo(tmpPath);
                    if (!fi.Exists || fi.Length == 0)
                    {
                        try { File.Delete(tmpPath); } catch { }
                        return false;
                    }

                    if (File.Exists(targetPath)) File.Delete(targetPath);
                    File.Move(tmpPath, targetPath);
                    tmpPath = null;

                    PruneOldBackupsLocked(safeName);
                    return File.Exists(targetPath) && new FileInfo(targetPath).Length > 0;
                }
                catch
                {
                    if (!string.IsNullOrEmpty(tmpPath))
                    {
                        try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                    }
                    return false;
                }
            }
        }

        private void PruneOldBackupsLocked(string safeName)
        {
            try
            {
                string prefix = safeName + "_";
                var files = new DirectoryInfo(_backupDir)
                    .GetFiles("*.json")
                    .Where(f => f.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .ToList();

                for (int i = MaxBackupsPerPlaylist; i < files.Count; i++)
                {
                    try { files[i].Delete(); } catch { }
                }
            }
            catch { }
        }

        public void GetBackupStats(out int fileCount, out long totalBytes)
        {
            fileCount = 0;
            totalBytes = 0;
            try
            {
                if (!string.IsNullOrEmpty(_backupDir) && Directory.Exists(_backupDir))
                {
                    var files = new DirectoryInfo(_backupDir).GetFiles("*.json");
                    fileCount = files.Length;
                    totalBytes = files.Sum(f => f.Length);
                }
            }
            catch { }
        }
    }
}
