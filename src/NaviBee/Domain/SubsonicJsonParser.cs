using System;
using System.Collections;
using System.Collections.Generic;

namespace MusicBeePlugin
{
    public static class SubsonicJsonParser
    {
        private static bool TryFindValue(IDictionary<string, object> dict, string key, out object val)
        {
            val = null;
            if (dict == null) return false;
            if (dict.TryGetValue(key, out val) && val != null)
                return true;
            foreach (var k in dict.Keys)
            {
                if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                {
                    val = dict[k];
                    return val != null;
                }
            }
            return false;
        }

        public static string GetString(IDictionary<string, object> dict, string key, string fallback = "")
        {
            if (TryFindValue(dict, key, out var val))
                return val.ToString();
            return fallback;
        }

        public static int GetInt(IDictionary<string, object> dict, string key, int fallback = 0)
        {
            if (TryFindValue(dict, key, out var val))
            {
                if (int.TryParse(val.ToString(), out var result))
                    return result;
            }
            return fallback;
        }

        public static long GetLong(IDictionary<string, object> dict, string key, long fallback = 0)
        {
            if (TryFindValue(dict, key, out var val))
            {
                if (long.TryParse(val.ToString(), out var result))
                    return result;
            }
            return fallback;
        }

        public static double GetDouble(IDictionary<string, object> dict, string key, double fallback = 0.0)
        {
            if (TryFindValue(dict, key, out var val))
            {
                if (double.TryParse(val.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var result))
                    return result;
            }
            return fallback;
        }

        public static bool GetBool(IDictionary<string, object> dict, string key, bool fallback = false)
        {
            if (TryFindValue(dict, key, out var val))
            {
                if (val is bool b) return b;
                string s = val.ToString().Trim();
                if (bool.TryParse(s, out var result))
                    return result;
                if (s == "1" || s.Equals("yes", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (s == "0" || s.Equals("no", StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return fallback;
        }

        private static bool IsStarredValue(IDictionary<string, object> dict)
        {
            if (!TryFindValue(dict, "starred", out var val) || val == null)
                return false;
            if (val is bool b) return b;
            string s = val.ToString().Trim();
            if (string.IsNullOrEmpty(s) ||
                s.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                s == "0")
                return false;
            return true;
        }

        public static SubsonicSong ParseSong(IDictionary<string, object> dict)
        {
            if (dict == null) return null;
            return new SubsonicSong
            {
                Id = GetString(dict, "id"),
                Title = GetString(dict, "title", GetString(dict, "name")),
                Artist = GetString(dict, "artist"),
                AlbumArtist = GetString(dict, "albumArtist"),
                Album = GetString(dict, "album"),
                AlbumId = GetString(dict, "albumId", GetString(dict, "parent")),
                ArtistId = GetString(dict, "artistId"),
                Year = GetInt(dict, "year"),
                Track = GetInt(dict, "track"),
                DiscNumber = GetInt(dict, "discNumber", 1),
                Genre = GetString(dict, "genre"),
                CoverArt = GetString(dict, "coverArt"),
                Size = GetLong(dict, "size"),
                ContentType = GetString(dict, "contentType"),
                Suffix = GetString(dict, "suffix"),
                Duration = GetInt(dict, "duration"),
                BitRate = GetInt(dict, "bitRate"),
                Path = GetString(dict, "path"),
                PlayCount = GetInt(dict, "playCount"),
                Starred = IsStarredValue(dict),
                UserRating = GetInt(dict, "userRating", GetInt(dict, "rating")),
                IsDir = GetBool(dict, "isDir")
            };
        }

        public static SubsonicAlbum ParseAlbum(IDictionary<string, object> dict)
        {
            if (dict == null) return null;
            return new SubsonicAlbum
            {
                Id = GetString(dict, "id"),
                Name = GetString(dict, "name", GetString(dict, "title")),
                Artist = GetString(dict, "artist"),
                ArtistId = GetString(dict, "artistId"),
                CoverArt = GetString(dict, "coverArt"),
                SongCount = GetInt(dict, "songCount"),
                Duration = GetInt(dict, "duration"),
                Year = GetInt(dict, "year"),
                Genre = GetString(dict, "genre")
            };
        }

        public static SubsonicArtist ParseArtist(IDictionary<string, object> dict)
        {
            if (dict == null) return null;
            return new SubsonicArtist
            {
                Id = GetString(dict, "id"),
                Name = GetString(dict, "name"),
                CoverArt = GetString(dict, "coverArt", GetString(dict, "artistImageUrl")),
                AlbumCount = GetInt(dict, "albumCount")
            };
        }

        public static SubsonicPlaylist ParsePlaylist(IDictionary<string, object> dict)
        {
            if (dict == null) return null;
            return new SubsonicPlaylist
            {
                Id = GetString(dict, "id"),
                Name = GetString(dict, "name"),
                Comment = GetString(dict, "comment"),
                Owner = GetString(dict, "owner"),
                SongCount = GetInt(dict, "songCount"),
                Duration = GetInt(dict, "duration"),
                CoverArt = GetString(dict, "coverArt")
            };
        }

        public static SubsonicScanStatus ParseScanStatus(IDictionary<string, object> dict)
        {
            if (dict == null) return null;
            return new SubsonicScanStatus
            {
                Scanning = GetBool(dict, "scanning"),
                Count = GetInt(dict, "count"),
                LastScan = GetString(dict, "lastScan")
            };
        }

        public static List<T> CastList<T>(object listObj, Func<IDictionary<string, object>, T> parser)
        {
            var list = new List<T>();
            if (listObj is ArrayList arrayList)
            {
                foreach (var item in arrayList)
                {
                    if (item is IDictionary<string, object> dict)
                    {
                        var parsed = parser(dict);
                        if (parsed != null) list.Add(parsed);
                    }
                }
            }
            else if (listObj is IDictionary<string, object> singleDict)
            {
                var parsed = parser(singleDict);
                if (parsed != null) list.Add(parsed);
            }
            return list;
        }
    }
}
