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

        public List<SubsonicSong> GetAlbum(string albumId)
        {
            var resp = SendRequest("getAlbum", new Dictionary<string, string> { { "id", albumId } });
            var result = new List<SubsonicSong>();
            if (resp != null && resp.TryGetValue("album", out var albumObj) && albumObj is IDictionary<string, object> album)
            {
                if (album.TryGetValue("song", out var songListObj))
                    result.AddRange(SubsonicJsonParser.CastList(songListObj, SubsonicJsonParser.ParseSong));
            }
            return result;
        }

        public List<SubsonicPlaylist> GetPlaylists()
        {
            var resp = SendRequest("getPlaylists");
            var result = new List<SubsonicPlaylist>();
            if (resp != null && resp.TryGetValue("playlists", out var plObj) && plObj is IDictionary<string, object> playlists)
            {
                if (playlists.TryGetValue("playlist", out var listObj))
                    result.AddRange(SubsonicJsonParser.CastList(listObj, SubsonicJsonParser.ParsePlaylist));
            }
            return result;
        }

        public List<SubsonicSong> GetPlaylist(string playlistId)
        {
            var resp = SendRequest("getPlaylist", new Dictionary<string, string> { { "id", playlistId } });
            var result = new List<SubsonicSong>();
            if (resp != null && resp.TryGetValue("playlist", out var plObj) && plObj is IDictionary<string, object> playlist)
            {
                if (playlist.TryGetValue("entry", out var entryListObj))
                    result.AddRange(SubsonicJsonParser.CastList(entryListObj, SubsonicJsonParser.ParseSong));
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

        public bool Scrobble(string songId, bool submission)
        {
            var q = new Dictionary<string, string>
            {
                { "id", songId },
                { "submission", submission ? "true" : "false" },
                { "time", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString() }
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

        public List<SubsonicAlbum> GetAlbumList2(string type = "alphabeticalByName", int size = 100, int offset = 0)
        {
            var q = new Dictionary<string, string>
            {
                { "type", type },
                { "size", size.ToString() },
                { "offset", offset.ToString() }
            };
            var resp = SendRequest("getAlbumList2", q, 30000);
            var result = new List<SubsonicAlbum>();
            if (resp != null && resp.TryGetValue("albumList2", out var listObj) && listObj is IDictionary<string, object> listDict)
            {
                if (listDict.TryGetValue("album", out var albumsObj))
                    result.AddRange(SubsonicJsonParser.CastList(albumsObj, SubsonicJsonParser.ParseAlbum));
            }
            return result;
        }

        public List<SubsonicSong> SearchSongs(int count = 1000, int offset = 0, string query = "")
        {
            var q = new Dictionary<string, string>
            {
                { "query", query ?? "" },
                { "artistCount", "0" },
                { "albumCount", "0" },
                { "songCount", count.ToString() },
                { "songOffset", offset.ToString() }
            };
            var resp = SendRequest("search3", q, 30000);
            var result = new List<SubsonicSong>();
            if (resp != null && resp.TryGetValue("searchResult3", out var resObj) && resObj is IDictionary<string, object> resDict)
            {
                if (resDict.TryGetValue("song", out var songsObj))
                    result.AddRange(SubsonicJsonParser.CastList(songsObj, SubsonicJsonParser.ParseSong));
            }
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
