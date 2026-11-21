using System;
using System.Collections.Generic;
using System.IO;

namespace MusicBeePlugin
{
    public partial class Plugin
    {
        public KeyValuePair<byte, string>[] GetFile(string url)
        {
            string clean = CleanPath(url);
            SubsonicSong song = NavidromeCatalog.Instance.FindSongByPath(clean)
                             ?? NavidromeCatalog.Instance.FindSongById(clean);
            if (song == null)
            {
                lock (CacheLock)
                {
                    SongByPath.TryGetValue(clean, out song);
                    if (song == null) SongById.TryGetValue(clean, out song);
                }
            }
            if (song != null) return song.CachedTags ?? SubsonicUrlHelper.ToTags(song);

            var info = SubsonicUrlHelper.ParseUrl(url);
            if (!string.IsNullOrEmpty(info.Id))
            {
                try
                {
                    var resp = Subsonic.SendRequest("getSong", new Dictionary<string, string> { { "id", info.Id } });
                    if (resp != null && resp.TryGetValue("song", out var songObj) && songObj is IDictionary<string, object> songDict)
                    {
                        song = SubsonicJsonParser.ParseSong(songDict);
                        RegisterSong(song);
                        return song.CachedTags ?? SubsonicUrlHelper.ToTags(song);
                    }
                }
                catch { }
            }
            return null;
        }

        public bool FileExists(string url)
        {
            string clean = CleanPath(url);
            if (string.IsNullOrEmpty(clean)) return false;

            if (NavidromeCatalog.Instance.FindSongByPath(clean) != null ||
                NavidromeCatalog.Instance.FindSongById(clean) != null)
                return true;

            lock (CacheLock)
            {
                if (SongByPath.ContainsKey(clean) || SongById.ContainsKey(clean)) return true;
            }

            if (url != null && url.IndexOf("?id=", StringComparison.OrdinalIgnoreCase) >= 0)
                return !string.IsNullOrEmpty(SubsonicUrlHelper.ParseUrl(url).Id);

            return false;
        }

        public byte[] GetFileArtwork(string url)
        {
            string coverId = NavidromeCatalog.Instance.ResolveCoverArtId(url);
            if (string.IsNullOrEmpty(coverId))
            {
                string clean = CleanPath(url);
                SubsonicSong song = NavidromeCatalog.Instance.FindSongByPath(clean)
                                 ?? NavidromeCatalog.Instance.FindSongById(clean);
                if (song != null)
                    coverId = !string.IsNullOrEmpty(song.CanonicalCoverId) ? song.CanonicalCoverId : song.CoverArt;
            }

            int targetSize = Settings != null ? Settings.ArtworkSize : 800;
            if (!string.IsNullOrEmpty(coverId))
                return Subsonic.GetCoverArt(coverId, targetSize);

            return null;
        }

        public Stream GetStream(string url)
        {
            string clean = CleanPath(url);
            SubsonicSong song = NavidromeCatalog.Instance.FindSongByPath(clean)
                             ?? NavidromeCatalog.Instance.FindSongById(clean);
            if (song == null)
            {
                lock (CacheLock)
                {
                    SongByPath.TryGetValue(clean, out song);
                    if (song == null) SongById.TryGetValue(clean, out song);
                }
            }

            if (song == null)
            {
                var info = SubsonicUrlHelper.ParseUrl(url);
                if (!string.IsNullOrEmpty(info.Id))
                {
                    song = NavidromeCatalog.Instance.FindSongById(info.Id);
                    if (song == null)
                    {
                        lock (CacheLock) { SongById.TryGetValue(info.Id, out song); }
                    }
                }
            }

            int bitrate = Settings != null ? Settings.TranscodeBitrate : 0;
            int cacheMaxMb = Settings != null ? Settings.AudioCacheMaxMb : 0;

            if (song != null)
            {
                var cachedStream = AudioStreamCache.Instance.TryOpenCachedStream(song, bitrate);
                if (cachedStream != null)
                    return cachedStream;

                Uri streamUri = Subsonic.GetStreamUri(song.Id, bitrate);
                long expectedSize = bitrate > 0 ? 0 : song.Size;
                if (cacheMaxMb != 0)
                {
                    string teePath = AudioStreamCache.Instance.CreateTeeTempPath(song, bitrate);
                    return new ConnectStream(streamUri, expectedSize, teePath, completed =>
                        AudioStreamCache.Instance.CommitTeeFile(song, bitrate, completed, cacheMaxMb));
                }
                return new ConnectStream(streamUri, expectedSize);
            }

            var fallbackInfo = SubsonicUrlHelper.ParseUrl(url);
            if (!string.IsNullOrEmpty(fallbackInfo.Id))
            {
                Uri uri = Subsonic.GetStreamUri(fallbackInfo.Id, bitrate);
                return new ConnectStream(uri, bitrate > 0 ? 0 : fallbackInfo.Size);
            }

            throw new FileNotFoundException($"Cannot stream unknown track: {url}");
        }

        public KeyValuePair<string, string>[] GetPlaylists()
        {
            var list = new List<KeyValuePair<string, string>>();
            try
            {
                var playlists = Subsonic.GetPlaylists();
                lock (CacheLock)
                {
                    PlaylistLookup.Clear();
                    foreach (var p in playlists)
                    {
                        if (!string.IsNullOrEmpty(p.Name))
                        {
                            PlaylistLookup[p.Name] = p.Id;
                            list.Add(new KeyValuePair<string, string>(p.Id, p.Name));
                        }
                    }
                }
            }
            catch { }
            return list.ToArray();
        }

        public KeyValuePair<byte, string>[][] GetPlaylistFiles(string id)
        {
            try
            {
                var songs = Subsonic.GetPlaylist(id);
                return ToTagMatrix(songs);
            }
            catch
            {
                return new KeyValuePair<byte, string>[0][];
            }
        }

        public bool DeleteFile(string url) => false;
        public bool DeleteFolder(string path) => false;
        public bool DeletePlaylist(string id, bool deleteFiles) => Subsonic.DeletePlaylist(id);

        public Exception GetError() => Subsonic?.LastError;
    }
}
