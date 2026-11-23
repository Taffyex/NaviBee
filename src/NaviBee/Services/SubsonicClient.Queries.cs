using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;

namespace MusicBeePlugin
{
    public partial class SubsonicClient
    {
        public SubsonicScanStatus GetScanStatus()
        {
            var resp = SendRequest("getScanStatus");
            if (resp != null && resp.TryGetValue("scanStatus", out var scanObj) && scanObj is IDictionary<string, object> scanDict)
                return SubsonicJsonParser.ParseScanStatus(scanDict);
            return null;
        }

        public List<SubsonicArtist> GetArtists()
        {
            var resp = SendRequest("getArtists");
            var result = new List<SubsonicArtist>();
            if (resp != null && resp.TryGetValue("artists", out var artistsObj) && artistsObj is IDictionary<string, object> artists)
            {
                if (artists.TryGetValue("index", out var indexObj) && indexObj is ArrayList indexes)
                {
                    foreach (var idx in indexes)
                    {
                        if (idx is IDictionary<string, object> indexDict && indexDict.TryGetValue("artist", out var artistListObj))
                            result.AddRange(SubsonicJsonParser.CastList(artistListObj, SubsonicJsonParser.ParseArtist));
                    }
                }
            }
            return result;
        }

        public List<SubsonicAlbum> GetArtist(string artistId)
        {
            var resp = SendRequest("getArtist", new Dictionary<string, string> { { "id", artistId } });
            var result = new List<SubsonicAlbum>();
            if (resp != null && resp.TryGetValue("artist", out var artistObj) && artistObj is IDictionary<string, object> artist)
            {
                if (artist.TryGetValue("album", out var albumListObj))
                    result.AddRange(SubsonicJsonParser.CastList(albumListObj, SubsonicJsonParser.ParseAlbum));
            }
            return result;
        }

        public bool TryGetAlbum(string albumId, out List<SubsonicSong> songs, out Exception error, int timeoutMs = 30000)
        {
            songs = new List<SubsonicSong>();
            var resp = SendRequest("getAlbum", new Dictionary<string, string> { { "id", albumId } }, timeoutMs, out error);
            if (resp == null) return false;
            if (resp.TryGetValue("album", out var albumObj) && albumObj is IDictionary<string, object> album)
            {
                if (album.TryGetValue("song", out var songListObj))
                    songs.AddRange(SubsonicJsonParser.CastList(songListObj, SubsonicJsonParser.ParseSong));
            }
            return true;
        }

        public List<SubsonicSong> GetAlbum(string albumId)
        {
            TryGetAlbum(albumId, out var result, out _);
            return result;
        }

        public bool TryGetPlaylists(out List<SubsonicPlaylist> playlists, out Exception error)
        {
            playlists = new List<SubsonicPlaylist>();
            var resp = SendRequest("getPlaylists", null, 15000, out error);
            if (resp == null) return false;
            if (resp.TryGetValue("playlists", out var plObj) && plObj is IDictionary<string, object> plDict)
            {
                if (plDict.TryGetValue("playlist", out var listObj))
                    playlists.AddRange(SubsonicJsonParser.CastList(listObj, SubsonicJsonParser.ParsePlaylist));
            }
            return true;
        }

        public List<SubsonicPlaylist> GetPlaylists()
        {
            TryGetPlaylists(out var result, out _);
            return result;
        }

        public List<SubsonicSong> GetPlaylist(string playlistId)
        {
            return GetPlaylistWithMeta(playlistId, out _) ?? new List<SubsonicSong>();
        }

        public List<SubsonicSong> GetPlaylistWithMeta(string playlistId, out SubsonicPlaylist meta)
        {
            meta = null;
            var resp = SendRequest("getPlaylist", new Dictionary<string, string> { { "id", playlistId } });
            if (resp == null) return null;

            var result = new List<SubsonicSong>();
            if (resp.TryGetValue("playlist", out var plObj) && plObj is IDictionary<string, object> playlist)
            {
                meta = SubsonicJsonParser.ParsePlaylist(playlist);
                if (playlist.TryGetValue("entry", out var entryListObj))
                    result.AddRange(SubsonicJsonParser.CastList(entryListObj, SubsonicJsonParser.ParseSong));
            }
            return result;
        }

        public List<SubsonicSong> GetStarred2()
        {
            var resp = SendRequest("getStarred2", null, 20000);
            if (resp == null) return null;

            var result = new List<SubsonicSong>();
            if (resp.TryGetValue("starred2", out var stObj) && stObj is IDictionary<string, object> starred)
            {
                if (starred.TryGetValue("song", out var songListObj))
                    result.AddRange(SubsonicJsonParser.CastList(songListObj, SubsonicJsonParser.ParseSong));
            }
            return result;
        }

        public string CreatePlaylist(string name, IEnumerable<string> songIds, string existingPlaylistId = null)
        {
            LastError = null;
            try
            {
                var settings = _settings;
                string baseUrl = GetBaseUrlOrThrow(settings);
                string auth = BuildAuthParams(settings);
                var body = new StringBuilder($"{auth}&name={Uri.EscapeDataString(name)}");
                if (!string.IsNullOrEmpty(existingPlaylistId))
                    body.Append($"&playlistId={Uri.EscapeDataString(existingPlaylistId)}");

                if (songIds != null)
                {
                    foreach (var id in songIds)
                    {
                        if (!string.IsNullOrEmpty(id))
                            body.Append($"&songId={Uri.EscapeDataString(id)}");
                    }
                }

                byte[] payload = Encoding.UTF8.GetBytes(body.ToString());
                var req = (HttpWebRequest)WebRequest.Create($"{baseUrl}/rest/createPlaylist");
                req.Method = "POST";
                req.ContentType = "application/x-www-form-urlencoded";
                req.Accept = "application/json";
                req.Timeout = 30000;
                req.ReadWriteTimeout = 30000;
                req.ContentLength = payload.Length;
                ConfigureRequest(req);

                using (var reqStream = req.GetRequestStream())
                    reqStream.Write(payload, 0, payload.Length);

                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    var r = ParseSubsonicEnvelope(reader.ReadToEnd());
                    if (r.TryGetValue("playlist", out var pObj) && pObj is IDictionary<string, object> pDict)
                        return SubsonicJsonParser.GetString(pDict, "id");
                }
                return existingPlaylistId ?? "";
            }
            catch (Exception ex)
            {
                LastError = ex;
                return null;
            }
        }

        public bool DeletePlaylist(string playlistId) =>
            SendRequest("deletePlaylist", new Dictionary<string, string> { { "id", playlistId } }) != null;

        public bool Scrobble(string songId, bool submission, long? unixTimeMs = null)
        {
            long ts = unixTimeMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var q = new Dictionary<string, string>
            {
                { "id", songId },
                { "submission", submission ? "true" : "false" },
                { "time", ts.ToString(System.Globalization.CultureInfo.InvariantCulture) }
            };
            return SendRequest("scrobble", q) != null;
        }

        public bool Star(string songId, bool star) =>
            SendRequest(star ? "star" : "unstar", new Dictionary<string, string> { { "id", songId } }) != null;

        public bool SetRating(string songId, int rating)
        {
            int clamped = Math.Max(0, Math.Min(5, rating));
            return SendRequest("setRating", new Dictionary<string, string>
            {
                { "id", songId },
                { "rating", clamped.ToString() }
            }) != null;
        }

        public bool TryGetAlbumList2(string type, int size, int offset, out List<SubsonicAlbum> albums, out Exception error, int timeoutMs = 60000)
        {
            albums = new List<SubsonicAlbum>();
            var q = new Dictionary<string, string>
            {
                { "type", type ?? "alphabeticalByName" },
                { "size", size.ToString() },
                { "offset", offset.ToString() }
            };
            var resp = SendRequest("getAlbumList2", q, timeoutMs, out error);
            if (resp == null) return false;
            if (resp.TryGetValue("albumList2", out var listObj) && listObj is IDictionary<string, object> listDict)
            {
                if (listDict.TryGetValue("album", out var albumsObj))
                    albums.AddRange(SubsonicJsonParser.CastList(albumsObj, SubsonicJsonParser.ParseAlbum));
            }
            return true;
        }

        public bool TryGetAlbumList2(string type, int size, int offset, out List<SubsonicAlbum> albums, int timeoutMs = 60000)
        {
            return TryGetAlbumList2(type, size, offset, out albums, out _, timeoutMs);
        }

        public List<SubsonicAlbum> GetAlbumList2(string type = "alphabeticalByName", int size = 100, int offset = 0)
        {
            TryGetAlbumList2(type, size, offset, out var result, out _, 30000);
            return result;
        }

        public bool TrySearchSongs(int count, int offset, string query, out List<SubsonicSong> songs, out Exception error, int timeoutMs = 60000)
        {
            songs = new List<SubsonicSong>();
            var q = new Dictionary<string, string>
            {
                { "query", query ?? "" },
                { "artistCount", "0" },
                { "albumCount", "0" },
                { "songCount", count.ToString() },
                { "songOffset", offset.ToString() }
            };
            var resp = SendRequest("search3", q, timeoutMs, out error);
            if (resp == null) return false;
            if (resp.TryGetValue("searchResult3", out var resObj) && resObj is IDictionary<string, object> resDict)
            {
                if (resDict.TryGetValue("song", out var songsObj))
                    songs.AddRange(SubsonicJsonParser.CastList(songsObj, SubsonicJsonParser.ParseSong));
            }
            return true;
        }

        public bool TrySearchSongs(int count, int offset, string query, out List<SubsonicSong> songs, int timeoutMs = 60000)
        {
            return TrySearchSongs(count, offset, query, out songs, out _, timeoutMs);
        }

        public List<SubsonicSong> SearchSongs(int count = 1000, int offset = 0, string query = "")
        {
            TrySearchSongs(count, offset, query, out var result, out _, 30000);
            return result;
        }

        public SubsonicLyricsResult GetLyricsBySongId(string songId)
        {
            if (string.IsNullOrWhiteSpace(songId))
                return SubsonicLyricsResult.Empty;

            var resp = SendRequest("getLyricsBySongId", new Dictionary<string, string> { { "id", songId } }, 10000);
            if (resp != null && resp.TryGetValue("lyricsList", out var listObj) && listObj is IDictionary<string, object> lyricsList)
                return SubsonicLyricsFormatter.ParseStructuredLyrics(lyricsList);
            return SubsonicLyricsResult.Empty;
        }

        public SubsonicLyricsResult GetLegacyLyrics(string artist, string title)
        {
            if (string.IsNullOrWhiteSpace(artist) && string.IsNullOrWhiteSpace(title))
                return SubsonicLyricsResult.Empty;

            var q = new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(artist)) q["artist"] = artist;
            if (!string.IsNullOrWhiteSpace(title)) q["title"] = title;

            var resp = SendRequest("getLyrics", q, 10000);
            if (resp != null && resp.TryGetValue("lyrics", out var lyrObj) && lyrObj is IDictionary<string, object> lyrDict)
            {
                string val = SubsonicJsonParser.GetString(lyrDict, "value");
                return SubsonicLyricsFormatter.NormalizeRawLyrics(val);
            }
            return SubsonicLyricsResult.Empty;
        }

        public SubsonicLyricsResult GetBestLyrics(SubsonicSong song)
        {
            if (song == null || string.IsNullOrWhiteSpace(song.Id))
                return SubsonicLyricsResult.Empty;

            var structured = GetLyricsBySongId(song.Id);
            if (structured.HasLyrics)
                return structured;

            return GetLegacyLyrics(song.Artist, song.Title);
        }
    }
}
