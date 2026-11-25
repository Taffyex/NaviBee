using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace MusicBeePlugin
{
    public class PlaylistLink
    {
        public string LocalPath { get; set; } = "";
        public string ServerId { get; set; } = "";
        public string ServerName { get; set; } = "";
        public bool CreatedByNaviBee { get; set; }
        public string LastPushedUtc { get; set; } = "";
    }

    public class PlaylistLinkStore
    {
        private static PlaylistLinkStore _instance;
        private static readonly object _instanceLock = new object();

        public static PlaylistLinkStore Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_instanceLock)
                    {
                        if (_instance == null)
                            _instance = new PlaylistLinkStore();
                    }
                }
                return _instance;
            }
        }

        private readonly object _lock = new object();
        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
        private readonly Dictionary<string, PlaylistLink> _byPath = new Dictionary<string, PlaylistLink>(StringComparer.OrdinalIgnoreCase);
        private string _filePath;

        public PlaylistLinkStore()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            ConfigureStorage(Path.Combine(appData, "MusicBee"));
        }

        public void ConfigureStorage(string persistentRoot)
        {
            if (string.IsNullOrWhiteSpace(persistentRoot)) return;
            string dir = Path.Combine(persistentRoot, "NavidromeCache");
            try
            {
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
            }
            catch { }

            lock (_lock)
            {
                _filePath = Path.Combine(dir, "playlist_links.json");
                LoadLocked();
            }
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            return path.Trim().Replace('/', '\\');
        }

        private void LoadLocked()
        {
            _byPath.Clear();
            if (string.IsNullOrEmpty(_filePath) || !File.Exists(_filePath)) return;
            try
            {
                string json = File.ReadAllText(_filePath, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json)) return;
                var items = _serializer.Deserialize<List<PlaylistLink>>(json);
                if (items == null) return;
                foreach (var item in items)
                {
                    if (item == null || string.IsNullOrWhiteSpace(item.LocalPath) || string.IsNullOrWhiteSpace(item.ServerId))
                        continue;
                    string key = NormalizePath(item.LocalPath);
                    item.LocalPath = key;
                    _byPath[key] = item;
                }
            }
            catch { }
        }

        private void SaveLocked()
        {
            if (string.IsNullOrEmpty(_filePath)) return;
            try
            {
                string json = _serializer.Serialize(_byPath.Values.ToList());
                NavidromeCatalog.WriteTextAtomic(_filePath, json);
            }
            catch { }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _byPath.Clear();
                SaveLocked();
            }
        }

        public PlaylistLink GetByPath(string localPath)
        {
            string key = NormalizePath(localPath);
            if (string.IsNullOrEmpty(key)) return null;
            lock (_lock)
            {
                return _byPath.TryGetValue(key, out var link) ? Clone(link) : null;
            }
        }

        public PlaylistLink GetByServerId(string serverId)
        {
            if (string.IsNullOrWhiteSpace(serverId)) return null;
            lock (_lock)
            {
                foreach (var link in _byPath.Values)
                {
                    if (string.Equals(link.ServerId, serverId, StringComparison.OrdinalIgnoreCase))
                        return Clone(link);
                }
                return null;
            }
        }

        public void Upsert(string localPath, string serverId, string serverName, bool createdByNaviBee)
        {
            string key = NormalizePath(localPath);
            if (string.IsNullOrEmpty(key) || string.IsNullOrWhiteSpace(serverId)) return;
            lock (_lock)
            {
                _byPath[key] = new PlaylistLink
                {
                    LocalPath = key,
                    ServerId = serverId,
                    ServerName = serverName ?? "",
                    CreatedByNaviBee = createdByNaviBee,
                    LastPushedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
                };
                SaveLocked();
            }
        }

        public PlaylistLink RemoveByPath(string localPath)
        {
            string key = NormalizePath(localPath);
            if (string.IsNullOrEmpty(key)) return null;
            lock (_lock)
            {
                if (_byPath.TryGetValue(key, out var existing))
                {
                    _byPath.Remove(key);
                    SaveLocked();
                    return Clone(existing);
                }
                return null;
            }
        }

        public void RemoveByServerId(string serverId)
        {
            if (string.IsNullOrWhiteSpace(serverId)) return;
            lock (_lock)
            {
                var keys = _byPath.Where(kv => string.Equals(kv.Value.ServerId, serverId, StringComparison.OrdinalIgnoreCase))
                    .Select(kv => kv.Key)
                    .ToList();
                if (keys.Count == 0) return;
                foreach (var k in keys) _byPath.Remove(k);
                SaveLocked();
            }
        }

        public bool TryRelinkMovedPlaylist(string newPath)
        {
            string newKey = NormalizePath(newPath);
            if (string.IsNullOrEmpty(newKey)) return false;
            string newName = SubsonicUrlHelper.GetFileNameWithoutExtensionSafe(newKey);
            if (string.IsNullOrEmpty(newName)) return false;

            lock (_lock)
            {
                if (_byPath.ContainsKey(newKey))
                    return true;

                // Find a previously linked entry whose file no longer exists on disk and shares the same playlist name.
                string candidateKey = null;
                foreach (var kv in _byPath)
                {
                    if (File.Exists(kv.Key)) continue;
                    string oldName = SubsonicUrlHelper.GetFileNameWithoutExtensionSafe(kv.Key);
                    if (string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(kv.Value.ServerName, newName, StringComparison.OrdinalIgnoreCase))
                    {
                        candidateKey = kv.Key;
                        break;
                    }
                }

                if (candidateKey == null) return false;
                var link = _byPath[candidateKey];
                _byPath.Remove(candidateKey);
                link.LocalPath = newKey;
                _byPath[newKey] = link;
                SaveLocked();
                return true;
            }
        }

        private static PlaylistLink Clone(PlaylistLink src) => new PlaylistLink
        {
            LocalPath = src.LocalPath,
            ServerId = src.ServerId,
            ServerName = src.ServerName,
            CreatedByNaviBee = src.CreatedByNaviBee,
            LastPushedUtc = src.LastPushedUtc
        };
    }
}
