using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MusicBeePlugin
{
    public partial class Plugin
    {
        private static readonly Dictionary<string, string> ArtistLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> AlbumLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> PlaylistLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, SubsonicSong> SongByPath = new Dictionary<string, SubsonicSong>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, SubsonicSong> SongById = new Dictionary<string, SubsonicSong>(StringComparer.OrdinalIgnoreCase);
        private static readonly object CacheLock = new object();
        private static Bitmap _storageIcon;
        private static volatile bool _isConnectionReady;
        private static long _lastPingTicksUtc;
        private static int _pingInFlight;

        internal static void SetConnectionReady(bool ready)
        {
            _isConnectionReady = ready;
            Interlocked.Exchange(ref _lastPingTicksUtc, DateTime.UtcNow.Ticks);
        }

        public bool IsReady()
        {
            if (Subsonic == null || string.IsNullOrWhiteSpace(Settings?.ServerUrl))
                return false;

            if (_isConnectionReady)
                return true;

            if (NavidromeCatalog.Instance.HasData)
            {
                long last = Interlocked.Read(ref _lastPingTicksUtc);
                if (DateTime.UtcNow.Ticks - last > TimeSpan.FromSeconds(30).Ticks &&
                    Interlocked.CompareExchange(ref _pingInFlight, 1, 0) == 0)
                {
                    Task.Run(() =>
                    {
                        try { SetConnectionReady(Subsonic.Ping()); }
                        finally { Interlocked.Exchange(ref _pingInFlight, 0); }
                    });
                }
                return true;
            }

            return false;
        }

        public void Refresh()
        {
            lock (CacheLock)
            {
                ArtistLookup.Clear();
                AlbumLookup.Clear();
                PlaylistLookup.Clear();
                SongByPath.Clear();
                SongById.Clear();
            }
            if (Api.MB_RefreshPanels != null)
                Api.MB_RefreshPanels();
        }

        public Image GetIcon()
        {
            if (_storageIcon != null) return _storageIcon;
            var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var bg = new SolidBrush(Color.FromArgb(0, 122, 204)))
                    g.FillEllipse(bg, 2, 2, 28, 28);
                using (var pen = new Pen(Color.White, 2.2f))
                    g.DrawEllipse(pen, 8, 8, 16, 16);
                using (var dot = new SolidBrush(Color.White))
                    g.FillEllipse(dot, 13, 13, 6, 6);
            }
            _storageIcon = bmp;
            return _storageIcon;
        }

        internal static void WarmPlaylistLookup()
        {
            try
            {
                var playlists = Subsonic?.GetPlaylists();
                if (playlists == null) return;
                lock (CacheLock)
                {
                    PlaylistLookup.Clear();
                    foreach (var p in playlists)
                    {
                        if (!string.IsNullOrEmpty(p.Name))
                            PlaylistLookup[p.Name] = p.Id;
                    }
                }
            }
            catch { }
        }

        private static string CleanPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            string s = path.Trim();
            const string p1 = "Navidrome:\\";
            const string p2 = "Navidrome:";
            const string p3 = "subsonic://";
            if (s.StartsWith(p1, StringComparison.OrdinalIgnoreCase))
                s = s.Substring(p1.Length);
            else if (s.StartsWith(p2, StringComparison.OrdinalIgnoreCase))
                s = s.Substring(p2.Length);
            else if (s.StartsWith(p3, StringComparison.OrdinalIgnoreCase))
                s = s.Substring(p3.Length);
            int qIdx = s.IndexOf('?');
            if (qIdx >= 0)
                s = s.Substring(0, qIdx);
            return s.TrimStart('\\', '/');
        }

        private static void RegisterSong(SubsonicSong song)
        {
            if (song == null || string.IsNullOrEmpty(song.Id)) return;
            string rel = !string.IsNullOrEmpty(song.CachedRelativePath)
                ? song.CachedRelativePath
                : SubsonicUrlHelper.BuildRelativePath(song);
            lock (CacheLock)
            {
                SongByPath[rel] = song;
                SongById[song.Id] = song;
            }
        }

        private static KeyValuePair<byte, string>[][] ToTagMatrix(IReadOnlyList<SubsonicSong> songs)
        {
            if (songs == null || songs.Count == 0) return new KeyValuePair<byte, string>[0][];
            var result = new KeyValuePair<byte, string>[songs.Count][];
            for (int i = 0; i < songs.Count; i++)
            {
                var s = songs[i];
                RegisterSong(s);
                result[i] = s.CachedTags ?? SubsonicUrlHelper.ToTags(s);
            }
            return result;
        }

        public bool FolderExists(string path)
        {
            string clean = CleanPath(path);
            if (string.IsNullOrEmpty(clean) ||
                string.Equals(clean, "All Albums", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(clean, "Favorites", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(clean, "Top Rated", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(clean, "Most Played", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(clean, "Playlists", StringComparison.OrdinalIgnoreCase))
                return true;

            if (clean.StartsWith("All Albums\\", StringComparison.OrdinalIgnoreCase))
                return true;

            if (clean.StartsWith("Playlists\\", StringComparison.OrdinalIgnoreCase))
            {
                string plName = clean.Substring(10);
                lock (CacheLock) { return PlaylistLookup.ContainsKey(plName); }
            }

            if (NavidromeCatalog.Instance.GetSongsByArtist(clean).Count > 0 ||
                NavidromeCatalog.Instance.GetSongsByAlbumKey(clean).Count > 0)
                return true;

            lock (CacheLock)
            {
                return ArtistLookup.ContainsKey(clean) || AlbumLookup.ContainsKey(clean);
            }
        }

        public string[] GetFolders(string path)
        {
            try
            {
                string clean = CleanPath(path);

                if (string.IsNullOrEmpty(clean))
                {
                    var list = new List<string> { "All Albums", "Favorites", "Top Rated", "Most Played", "Playlists" };
                    var catArtists = NavidromeCatalog.Instance.GetArtists();
                    if (catArtists.Count > 0)
                    {
                        list.AddRange(catArtists);
                    }
                    else
                    {
                        var artists = Subsonic.GetArtists();
                        lock (CacheLock)
                        {
                            ArtistLookup.Clear();
                            foreach (var a in artists)
                            {
                                if (!string.IsNullOrEmpty(a.Name))
                                {
                                    string sName = SubsonicUrlHelper.SanitizeFolderSegment(a.Name);
                                    ArtistLookup[sName] = a.Id;
                                    list.Add(sName);
                                }
                            }
                        }
                    }
                    return list.ToArray();
                }

                if (string.Equals(clean, "All Albums", StringComparison.OrdinalIgnoreCase))
                    return NavidromeCatalog.Instance.GetAllAlbumKeys().Select(a => "All Albums\\" + a).ToArray();

                if (string.Equals(clean, "Playlists", StringComparison.OrdinalIgnoreCase))
                {
                    var playlists = Subsonic.GetPlaylists();
                    var list = new List<string>();
                    lock (CacheLock)
                    {
                        PlaylistLookup.Clear();
                        foreach (var p in playlists)
                        {
                            if (!string.IsNullOrEmpty(p.Name))
                            {
                                PlaylistLookup[p.Name] = p.Id;
                                list.Add("Playlists\\" + p.Name);
                            }
                        }
                    }
                    return list.ToArray();
                }

                if (clean.StartsWith("Playlists\\", StringComparison.OrdinalIgnoreCase) ||
                    clean.StartsWith("All Albums\\", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(clean, "Favorites", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(clean, "Top Rated", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(clean, "Most Played", StringComparison.OrdinalIgnoreCase))
                {
                    return new string[0];
                }

                string artistName = clean.TrimEnd('\\');
                var catAlbums = NavidromeCatalog.Instance.GetAlbumsForArtist(artistName);
                if (catAlbums.Count > 0)
                    return catAlbums.Select(alb => $"{artistName}\\{alb}").ToArray();

                string artistId = null;
                lock (CacheLock) { ArtistLookup.TryGetValue(artistName, out artistId); }
                if (!string.IsNullOrEmpty(artistId))
                {
                    var albums = Subsonic.GetArtist(artistId);
                    var albumNames = new List<string>();
                    lock (CacheLock)
                    {
                        foreach (var alb in albums)
                        {
                            string sAlb = SubsonicUrlHelper.SanitizeFolderSegment(alb.Name);
                            string key = $"{artistName}\\{sAlb}";
                            AlbumLookup[key] = alb.Id;
                            albumNames.Add(key);
                        }
                    }
                    return albumNames.ToArray();
                }

                return new string[0];
            }
            catch (Exception ex)
            {
                if (Api.MB_SetBackgroundTaskMessage != null)
                    Api.MB_SetBackgroundTaskMessage($"GetFolders error: {ex.Message}");
                return new string[0];
            }
        }

        public KeyValuePair<byte, string>[][] GetFiles(string path)
        {
            try
            {
                string clean = CleanPath(path);

                if (string.IsNullOrEmpty(clean) || string.Equals(clean, "All Albums", StringComparison.OrdinalIgnoreCase))
                {
                    if (NavidromeCatalog.Instance.HasData)
                        return NavidromeCatalog.Instance.GetAllTagArrays();

                    var albums = Subsonic.GetAlbumList2("alphabeticalByName", 100, 0);
                    var fallbackList = new List<SubsonicSong>();
                    foreach (var alb in albums)
                        fallbackList.AddRange(Subsonic.GetAlbum(alb.Id));
                    return ToTagMatrix(fallbackList);
                }

                if (string.Equals(clean, "Favorites", StringComparison.OrdinalIgnoreCase))
                    return ToTagMatrix(NavidromeCatalog.Instance.GetStarredSongs());

                if (string.Equals(clean, "Top Rated", StringComparison.OrdinalIgnoreCase))
                    return ToTagMatrix(NavidromeCatalog.Instance.GetTopRatedSongs());

                if (string.Equals(clean, "Most Played", StringComparison.OrdinalIgnoreCase))
                    return ToTagMatrix(NavidromeCatalog.Instance.GetMostPlayedSongs());

                if (string.Equals(clean, "Playlists", StringComparison.OrdinalIgnoreCase))
                    return new KeyValuePair<byte, string>[0][];

                if (clean.StartsWith("Playlists\\", StringComparison.OrdinalIgnoreCase))
                {
                    string plName = clean.Substring(10);
                    string plId = null;
                    lock (CacheLock) { PlaylistLookup.TryGetValue(plName, out plId); }
                    if (!string.IsNullOrEmpty(plId))
                        return GetPlaylistFiles(plId);
                    return new KeyValuePair<byte, string>[0][];
                }

                if (clean.StartsWith("All Albums\\", StringComparison.OrdinalIgnoreCase))
                {
                    string albKey = clean.Substring(11);
                    return ToTagMatrix(NavidromeCatalog.Instance.GetSongsByAlbumKey(albKey));
                }

                string[] parts = clean.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 1)
                {
                    var artistSongs = NavidromeCatalog.Instance.GetSongsByArtist(parts[0]);
                    if (artistSongs.Count > 0) return ToTagMatrix(artistSongs);

                    var albSongs = NavidromeCatalog.Instance.GetSongsByAlbumKey(parts[0]);
                    if (albSongs.Count > 0) return ToTagMatrix(albSongs);

                    string artistId = null;
                    lock (CacheLock) { ArtistLookup.TryGetValue(parts[0], out artistId); }
                    if (!string.IsNullOrEmpty(artistId))
                    {
                        var albums = Subsonic.GetArtist(artistId);
                        var songs = new List<SubsonicSong>();
                        foreach (var alb in albums)
                            songs.AddRange(Subsonic.GetAlbum(alb.Id));
                        return ToTagMatrix(songs);
                    }
                }
                else if (parts.Length >= 2)
                {
                    string key = $"{parts[0]}\\{parts[1]}";
                    var albSongs = NavidromeCatalog.Instance.GetSongsByAlbumKey(key);
                    if (albSongs.Count == 0)
                        albSongs = NavidromeCatalog.Instance.GetSongsByAlbumKey(parts[1]);
                    if (albSongs.Count > 0) return ToTagMatrix(albSongs);

                    string albumId = null;
                    lock (CacheLock) { AlbumLookup.TryGetValue(key, out albumId); }
                    if (!string.IsNullOrEmpty(albumId))
                        return ToTagMatrix(Subsonic.GetAlbum(albumId));
                }
            }
            catch (Exception ex)
            {
                if (Api.MB_SetBackgroundTaskMessage != null)
                    Api.MB_SetBackgroundTaskMessage($"GetFiles error: {ex.Message}");
            }
            return new KeyValuePair<byte, string>[0][];
        }
    }
}
