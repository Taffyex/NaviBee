using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MusicBeePlugin
{
    public partial class NavidromeCatalog
    {
        private void BuildIndexes(List<SubsonicSong> songs)
        {
            var albumArtists = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var albumExplicitArtist = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var s in songs)
            {
                string albKey = !string.IsNullOrEmpty(s.AlbumId) ? s.AlbumId : s.Album;
                if (string.IsNullOrEmpty(albKey)) continue;

                if (!string.IsNullOrEmpty(s.AlbumArtist))
                    albumExplicitArtist[albKey] = s.AlbumArtist;
                else
                {
                    if (!albumArtists.TryGetValue(albKey, out var set))
                    {
                        set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        albumArtists[albKey] = set;
                    }
                    if (!string.IsNullOrEmpty(s.Artist)) set.Add(s.Artist);
                }
            }

            foreach (var s in songs)
            {
                string albKey = !string.IsNullOrEmpty(s.AlbumId) ? s.AlbumId : s.Album;
                if (string.IsNullOrEmpty(albKey)) continue;

                if (string.IsNullOrEmpty(s.AlbumArtist))
                {
                    if (albumExplicitArtist.TryGetValue(albKey, out var exp))
                        s.AlbumArtist = exp;
                    else if (albumArtists.TryGetValue(albKey, out var set) && set.Count > 1)
                        s.AlbumArtist = "Various Artists";
                    else if (!string.IsNullOrEmpty(s.Artist))
                        s.AlbumArtist = s.Artist;
                }
            }

            songs.Sort((a, b) =>
            {
                string aArtist = !string.IsNullOrEmpty(a.AlbumArtist) ? a.AlbumArtist : (a.Artist ?? "");
                string bArtist = !string.IsNullOrEmpty(b.AlbumArtist) ? b.AlbumArtist : (b.Artist ?? "");
                int c = string.Compare(aArtist, bArtist, StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c;
                c = a.Year.CompareTo(b.Year);
                if (c != 0) return c;
                c = string.Compare(a.Album ?? "", b.Album ?? "", StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c;
                c = a.DiscNumber.CompareTo(b.DiscNumber);
                if (c != 0) return c;
                c = a.Track.CompareTo(b.Track);
                if (c != 0) return c;
                return string.Compare(a.Title ?? "", b.Title ?? "", StringComparison.OrdinalIgnoreCase);
            });

            _allSongs = songs;
            _byPath = new Dictionary<string, SubsonicSong>(songs.Count * 2, StringComparer.OrdinalIgnoreCase);
            _byUniqueFileName = new Dictionary<string, SubsonicSong>(songs.Count, StringComparer.OrdinalIgnoreCase);
            _byId = new Dictionary<string, SubsonicSong>(songs.Count, StringComparer.OrdinalIgnoreCase);
            ResetLocalMatchIndexesLocked(songs.Count);
            _byArtist = new Dictionary<string, List<SubsonicSong>>(StringComparer.OrdinalIgnoreCase);
            _byAlbumKey = new Dictionary<string, List<SubsonicSong>>(StringComparer.OrdinalIgnoreCase);
            _albumsByArtist = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            _coverToCanonical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _coverBySanitizedAlbumDir = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _coverBySanitizedAlbumName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _allAlbumKeys = new List<string>();
            _starredSongs = new List<SubsonicSong>();
            _topRatedSongs = new List<SubsonicSong>();
            _mostPlayedSongs = new List<SubsonicSong>();

            var seenAlbums = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenAlbumsByArtist = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var distinctCovers = new HashSet<string>(StringComparer.Ordinal);
            var tagsList = new List<KeyValuePair<byte, string>[]>(songs.Count);

            for (int i = 0; i < songs.Count; i++)
            {
                var s = songs[i];
                s.CatalogIndex = i;
                string rel = SubsonicUrlHelper.BuildRelativePath(s);
                if (_byPath.ContainsKey(rel) && !string.IsNullOrEmpty(s.Id))
                {
                    string ext = Path.GetExtension(rel);
                    string stem = rel.Substring(0, rel.Length - ext.Length);
                    string shortId = s.Id.Length > 8 ? s.Id.Substring(0, 8) : s.Id;
                    rel = $"{stem} [{SubsonicUrlHelper.Sanitize(shortId)}]{ext}";
                }

                s.CachedRelativePath = rel;
                s.CachedTags = null;
                s.CachedTags = SubsonicUrlHelper.ToTags(s);
                tagsList.Add(s.CachedTags);

                _byPath[rel] = s;
                _byPath["Navidrome:\\" + rel] = s;
                if (!string.IsNullOrEmpty(s.Id))
                    _byId[s.Id] = s;

                IndexLocalMatchKeysLocked(s, rel);

                string fname = Path.GetFileName(rel);
                if (!string.IsNullOrEmpty(fname))
                {
                    if (_byUniqueFileName.ContainsKey(fname))
                        _byUniqueFileName[fname] = null; // Ambiguous filename across albums
                    else
                        _byUniqueFileName[fname] = s;
                }

                string canonicalCover = s.CanonicalCoverId;
                if (!string.IsNullOrEmpty(canonicalCover))
                {
                    distinctCovers.Add(canonicalCover);
                    if (!string.IsNullOrEmpty(s.CoverArt))
                        _coverToCanonical[s.CoverArt] = canonicalCover;
                    if (!string.IsNullOrEmpty(s.Id))
                        _coverToCanonical[s.Id] = canonicalCover;
                    if (!string.IsNullOrEmpty(s.AlbumId))
                        _coverToCanonical["al-" + s.AlbumId] = canonicalCover;
                }

                if (s.Starred) _starredSongs.Add(s);
                if (s.UserRating >= 3) _topRatedSongs.Add(s);
                if (s.PlayCount > 0) _mostPlayedSongs.Add(s);

                string artistName = SubsonicUrlHelper.SanitizeFolderSegment(!string.IsNullOrEmpty(s.AlbumArtist) ? s.AlbumArtist : s.Artist);
                if (!_byArtist.TryGetValue(artistName, out var artistList))
                {
                    artistList = new List<SubsonicSong>();
                    _byArtist[artistName] = artistList;
                }
                artistList.Add(s);

                string albumFolder = SubsonicUrlHelper.SanitizeFolderSegment(!string.IsNullOrEmpty(s.Album) ? s.Album : "Unknown Album");
                string artistAlbumKey = $"{artistName}\\{albumFolder}";

                if (!_byAlbumKey.TryGetValue(artistAlbumKey, out var albSongs))
                {
                    albSongs = new List<SubsonicSong>();
                    _byAlbumKey[artistAlbumKey] = albSongs;
                }
                albSongs.Add(s);

                if (!_byAlbumKey.ContainsKey(albumFolder))
                    _byAlbumKey[albumFolder] = albSongs;

                if (!string.IsNullOrEmpty(canonicalCover))
                {
                    if (!_coverBySanitizedAlbumDir.ContainsKey(artistAlbumKey))
                        _coverBySanitizedAlbumDir[artistAlbumKey] = canonicalCover;
                    if (!_coverBySanitizedAlbumName.ContainsKey(albumFolder))
                        _coverBySanitizedAlbumName[albumFolder] = canonicalCover;
                }

                if (!_albumsByArtist.TryGetValue(artistName, out var albList))
                {
                    albList = new List<string>();
                    _albumsByArtist[artistName] = albList;
                    seenAlbumsByArtist[artistName] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }
                if (seenAlbumsByArtist[artistName].Add(albumFolder))
                    albList.Add(albumFolder);

                string globalAlbKey = $"{albumFolder} — {artistName}";
                if (seenAlbums.Add(globalAlbKey))
                {
                    _allAlbumKeys.Add(globalAlbKey);
                    _byAlbumKey[globalAlbKey] = albSongs;
                }
            }

            _allTagArrays = tagsList.ToArray();
            _distinctAlbumCoverIds = distinctCovers.ToList();
            _allAlbumKeys.Sort(StringComparer.OrdinalIgnoreCase);
            _topRatedSongs.Sort((a, b) => b.UserRating.CompareTo(a.UserRating));
            _mostPlayedSongs.Sort((a, b) => b.PlayCount.CompareTo(a.PlayCount));
        }

        public string ResolveCanonicalCoverId(string coverId)
        {
            if (string.IsNullOrEmpty(coverId)) return coverId;
            lock (_lock)
            {
                if (_coverToCanonical.TryGetValue(coverId, out var canonical))
                    return canonical;
            }
            return coverId;
        }

        public string ResolveCoverArtId(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            string clean = url.Trim();
            const string p1 = "Navidrome:\\";
            const string p2 = "Navidrome:";
            const string p3 = "subsonic://";
            if (clean.StartsWith(p1, StringComparison.OrdinalIgnoreCase)) clean = clean.Substring(p1.Length);
            else if (clean.StartsWith(p2, StringComparison.OrdinalIgnoreCase)) clean = clean.Substring(p2.Length);
            else if (clean.StartsWith(p3, StringComparison.OrdinalIgnoreCase)) clean = clean.Substring(p3.Length);
            int qIdx = clean.IndexOf('?');
            if (qIdx >= 0) clean = clean.Substring(0, qIdx);
            clean = clean.TrimStart('\\', '/');

            var song = FindSongByPath(clean) ?? FindSongById(clean);
            if (song != null)
            {
                string cId = !string.IsNullOrEmpty(song.CanonicalCoverId) ? song.CanonicalCoverId : song.CoverArt;
                if (!string.IsNullOrEmpty(cId)) return cId;
            }

            string[] parts = clean.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                string dirKey = $"{parts[0]}\\{parts[1]}";
                lock (_lock)
                {
                    if (_coverBySanitizedAlbumDir.TryGetValue(dirKey, out var cId)) return cId;
                }
            }
            else if (parts.Length == 1)
            {
                lock (_lock)
                {
                    if (_coverBySanitizedAlbumName.TryGetValue(parts[0], out var cId)) return cId;
                }
            }

            var albSongs = GetSongsByAlbumKey(clean);
            if (albSongs != null && albSongs.Count > 0)
            {
                string cId = !string.IsNullOrEmpty(albSongs[0].CanonicalCoverId) ? albSongs[0].CanonicalCoverId : albSongs[0].CoverArt;
                if (!string.IsNullOrEmpty(cId)) return cId;
            }

            var info = SubsonicUrlHelper.ParseUrl(url);
            if (!string.IsNullOrEmpty(info.CoverArt)) return info.CoverArt;
            if (!string.IsNullOrEmpty(info.AlbumId)) return "al-" + info.AlbumId;
            if (!string.IsNullOrEmpty(info.Id)) return info.Id;

            return null;
        }
    }
}
