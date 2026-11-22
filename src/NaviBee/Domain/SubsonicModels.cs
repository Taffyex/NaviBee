using System;
using System.Collections.Generic;
using System.Globalization;
using System.Web;
using System.Web.Script.Serialization;

namespace MusicBeePlugin
{
    public class SubsonicPingInfo
    {
        public bool Ok { get; set; }
        public string ServerType { get; set; } = "";
        public string ServerVersion { get; set; } = "";
        public string ApiVersion { get; set; } = "";
        public bool OpenSubsonic { get; set; }
    }

    public class SubsonicScanStatus
    {
        public bool Scanning { get; set; }
        public int Count { get; set; }
        public string LastScan { get; set; } = "";
    }

    public class CatalogMetadata
    {
        public string LastScan { get; set; } = "";
        public int ServerSongCount { get; set; }
        public string ServerUrl { get; set; } = "";
        public int ArtworkSize { get; set; }
    }

    public class SubsonicSong
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
        public string AlbumArtist { get; set; } = "";
        public string Album { get; set; } = "";
        public string AlbumId { get; set; } = "";
        public string ArtistId { get; set; } = "";
        public int Year { get; set; }
        public int Track { get; set; }
        public int DiscNumber { get; set; } = 1;
        public string Genre { get; set; } = "";
        public string CoverArt { get; set; } = "";
        public long Size { get; set; }
        public string ContentType { get; set; } = "";
        public string Suffix { get; set; } = "";
        public int Duration { get; set; } // in seconds
        public int BitRate { get; set; }
        public string Path { get; set; } = "";
        public int PlayCount { get; set; }
        public bool Starred { get; set; }
        public int UserRating { get; set; }
        public bool IsDir { get; set; }
        public string Created { get; set; } = "";
        public string Played { get; set; } = "";
        public string Comment { get; set; } = "";
        public int SamplingRate { get; set; }
        public int ChannelCount { get; set; }
        public int BitDepth { get; set; }

        [ScriptIgnore]
        public string CachedRelativePath { get; set; }

        [ScriptIgnore]
        public string CanonicalCoverId => !string.IsNullOrEmpty(CoverArt)
            ? CoverArt
            : (!string.IsNullOrEmpty(AlbumId) ? "al-" + AlbumId : "");

        [ScriptIgnore]
        public KeyValuePair<byte, string>[] CachedTags { get; set; }
    }

    public class SubsonicAlbum
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Artist { get; set; } = "";
        public string ArtistId { get; set; } = "";
        public string CoverArt { get; set; } = "";
        public int SongCount { get; set; }
        public int Duration { get; set; }
        public int Year { get; set; }
        public string Genre { get; set; } = "";
    }

    public class SubsonicArtist
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string CoverArt { get; set; } = "";
        public int AlbumCount { get; set; }
    }

    public class SubsonicPlaylist
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Comment { get; set; } = "";
        public string Owner { get; set; } = "";
        public int SongCount { get; set; }
        public int Duration { get; set; }
        public string CoverArt { get; set; } = "";
    }

    public class SubsonicUrlInfo
    {
        public string Id { get; set; } = "";
        public string AlbumId { get; set; } = "";
        public string CoverArt { get; set; } = "";
        public long Size { get; set; }
        public int Duration { get; set; }
        public int BitRate { get; set; }
        public string DisplayPath { get; set; } = "";
    }

    public static class SubsonicUrlHelper
    {
        public static string Sanitize(string segment)
        {
            if (string.IsNullOrEmpty(segment)) return "Unknown";
            char[] invalid = System.IO.Path.GetInvalidFileNameChars();
            char[] chars = segment.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (Array.IndexOf(invalid, chars[i]) >= 0 || chars[i] == '/' || chars[i] == '\\')
                    chars[i] = '_';
            }
            string s = new string(chars).Trim().TrimEnd('.');
            return string.IsNullOrEmpty(s) ? "_" : s;
        }

        public static string SanitizeFolderSegment(string segment) => Sanitize(segment);

        public static string BuildRelativePath(SubsonicSong song)
        {
            if (song == null) return "";
            string artist = Sanitize(!string.IsNullOrEmpty(song.AlbumArtist) ? song.AlbumArtist : song.Artist);
            string album = Sanitize(song.Album);
            string title = Sanitize(song.Title);
            string ext = !string.IsNullOrEmpty(song.Suffix) ? song.Suffix.TrimStart('.') : "mp3";
            string disc = song.DiscNumber > 1 ? $"{song.DiscNumber}-" : "";
            string track = song.Track > 0 ? $"{disc}{song.Track:D2} - " : "";
            return $"{artist}\\{album}\\{track}{title}.{ext}";
        }

        public static string BuildUrl(SubsonicSong song) => BuildRelativePath(song);

        public static SubsonicUrlInfo ParseUrl(string url)
        {
            var info = new SubsonicUrlInfo();
            if (string.IsNullOrEmpty(url)) return info;

            string clean = url.Trim();
            const string p1 = "Navidrome:\\";
            const string p2 = "Navidrome:";
            const string p3 = "subsonic://";
            if (clean.StartsWith(p1, StringComparison.OrdinalIgnoreCase))
                clean = clean.Substring(p1.Length);
            else if (clean.StartsWith(p2, StringComparison.OrdinalIgnoreCase))
                clean = clean.Substring(p2.Length);
            else if (clean.StartsWith(p3, StringComparison.OrdinalIgnoreCase))
                clean = clean.Substring(p3.Length);

            int qIdx = clean.IndexOf('?');
            if (qIdx >= 0)
            {
                string queryString = clean.Substring(qIdx + 1);
                clean = clean.Substring(0, qIdx);
                string[] pairs = queryString.Split('&');
                foreach (var pair in pairs)
                {
                    int eq = pair.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = pair.Substring(0, eq).Trim();
                    string val = Uri.UnescapeDataString(pair.Substring(eq + 1).Trim());

                    switch (key.ToLowerInvariant())
                    {
                        case "id": info.Id = val; break;
                        case "albumid": info.AlbumId = val; break;
                        case "cover": info.CoverArt = val; break;
                        case "size": if (long.TryParse(val, out var sz)) info.Size = sz; break;
                        case "dur": if (int.TryParse(val, out var dur)) info.Duration = dur; break;
                        case "br": if (int.TryParse(val, out var br)) info.BitRate = br; break;
                    }
                }
            }

            if (clean.StartsWith("subsonic://", StringComparison.OrdinalIgnoreCase))
                clean = clean.Substring(11);

            info.DisplayPath = clean.TrimStart('\\');
            if (string.IsNullOrEmpty(info.Id))
                info.Id = info.DisplayPath;

            return info;
        }

        public static KeyValuePair<byte, string>[] ToTags(SubsonicSong song)
        {
            if (song == null) return null;
            if (song.CachedTags != null) return song.CachedTags;

            string relPath = !string.IsNullOrEmpty(song.CachedRelativePath)
                ? song.CachedRelativePath
                : BuildRelativePath(song);
            string albArtist = !string.IsNullOrEmpty(song.AlbumArtist) ? song.AlbumArtist : (song.Artist ?? "");
            string cover = !string.IsNullOrEmpty(song.CanonicalCoverId) ? song.CanonicalCoverId : (song.CoverArt ?? "");

            var tagList = new List<KeyValuePair<byte, string>>(24)
            {
                new KeyValuePair<byte, string>((byte)FilePropertyType.Url, relPath),
                new KeyValuePair<byte, string>((byte)MetaDataType.Artist, song.Artist ?? ""),
                new KeyValuePair<byte, string>((byte)MetaDataType.AlbumArtist, albArtist),
                new KeyValuePair<byte, string>((byte)MetaDataType.AlbumArtistRaw, albArtist),
                new KeyValuePair<byte, string>((byte)MetaDataType.TrackTitle, song.Title ?? ""),
                new KeyValuePair<byte, string>((byte)MetaDataType.Album, song.Album ?? ""),
                new KeyValuePair<byte, string>((byte)MetaDataType.Year, song.Year > 0 ? song.Year.ToString(CultureInfo.InvariantCulture) : ""),
                new KeyValuePair<byte, string>((byte)MetaDataType.TrackNo, song.Track > 0 ? song.Track.ToString(CultureInfo.InvariantCulture) : ""),
                new KeyValuePair<byte, string>((byte)MetaDataType.DiscNo, song.DiscNumber > 0 ? song.DiscNumber.ToString(CultureInfo.InvariantCulture) : "1"),
                new KeyValuePair<byte, string>((byte)MetaDataType.Genre, song.Genre ?? ""),
                new KeyValuePair<byte, string>((byte)FilePropertyType.Duration, (song.Duration * 1000).ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<byte, string>((byte)FilePropertyType.Bitrate, song.BitRate > 0 ? song.BitRate.ToString(CultureInfo.InvariantCulture) : ""),
                new KeyValuePair<byte, string>((byte)FilePropertyType.Size, song.Size > 0 ? song.Size.ToString(CultureInfo.InvariantCulture) : ""),
                new KeyValuePair<byte, string>((byte)MetaDataType.Artwork, cover),
                new KeyValuePair<byte, string>((byte)MetaDataType.RatingLove, song.Starred ? "L" : ""),
                new KeyValuePair<byte, string>((byte)MetaDataType.Rating, song.UserRating > 0 ? (song.UserRating * 20).ToString(CultureInfo.InvariantCulture) : ""),
                new KeyValuePair<byte, string>((byte)MetaDataType.Custom16, song.Id ?? "")
            };

            if (song.PlayCount > 0)
                tagList.Add(new KeyValuePair<byte, string>((byte)FilePropertyType.PlayCount, song.PlayCount.ToString(CultureInfo.InvariantCulture)));
            if (!string.IsNullOrWhiteSpace(song.Played) && DateTime.TryParse(song.Played, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var playedDt))
                tagList.Add(new KeyValuePair<byte, string>((byte)FilePropertyType.LastPlayed, playedDt.ToLocalTime().ToString("o", CultureInfo.InvariantCulture)));
            if (!string.IsNullOrWhiteSpace(song.Created) && DateTime.TryParse(song.Created, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var createdDt))
                tagList.Add(new KeyValuePair<byte, string>((byte)FilePropertyType.DateAdded, createdDt.ToLocalTime().ToString("o", CultureInfo.InvariantCulture)));
            if (song.SamplingRate > 0)
                tagList.Add(new KeyValuePair<byte, string>((byte)FilePropertyType.SampleRate, song.SamplingRate.ToString(CultureInfo.InvariantCulture)));
            if (song.ChannelCount > 0)
                tagList.Add(new KeyValuePair<byte, string>((byte)FilePropertyType.Channels, song.ChannelCount.ToString(CultureInfo.InvariantCulture)));
            if (!string.IsNullOrWhiteSpace(song.Comment))
                tagList.Add(new KeyValuePair<byte, string>((byte)MetaDataType.Comment, song.Comment));

            var tags = tagList.ToArray();
            song.CachedTags = tags;
            return tags;
        }
    }
}
