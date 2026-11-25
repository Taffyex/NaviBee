using System;
using System.Collections.Generic;
using System.Linq;

namespace MusicBeePlugin
{
    public partial class NavidromeCatalog
    {
        private void BuildIndexes(List<SubsonicSong> songs)
        {
            var cleanSongs = new List<SubsonicSong>(songs != null ? songs.Count : 0);
            if (songs != null)
            {
                for (int i = 0; i < songs.Count; i++)
                {
                    if (songs[i] != null)
                        cleanSongs.Add(songs[i]);
                }
            }

            var albumArtists = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var albumExplicitArtist = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var s in cleanSongs)
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

            foreach (var s in cleanSongs)
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

            cleanSongs.Sort((a, b) =>
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

            var indexedSongs = new List<SubsonicSong>(cleanSongs.Count);
            var byPath = new Dictionary<string, SubsonicSong>(cleanSongs.Count * 2, StringComparer.OrdinalIgnoreCase);
            var byUniqueFileName = new Dictionary<string, SubsonicSong>(cleanSongs.Count, StringComparer.OrdinalIgnoreCase);
            var byId = new Dictionary<string, SubsonicSong>(cleanSongs.Count, StringComparer.OrdinalIgnoreCase);
            var localMatch = new LocalMatchIndexSet(cleanSongs.Count);
            var byArtist = new Dictionary<string, List<SubsonicSong>>(StringComparer.OrdinalIgnoreCase);
            var byAlbumKey = new Dictionary<string, List<SubsonicSong>>(StringComparer.OrdinalIgnoreCase);
            var albumsByArtist = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var coverToCanonical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var coverBySanitizedAlbumDir = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var coverBySanitizedAlbumName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var allAlbumKeys = new List<string>();
            var starredSongs = new List<SubsonicSong>();
            var topRatedSongs = new List<SubsonicSong>();
            var mostPlayedSongs = new List<SubsonicSong>();

            var seenAlbums = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenAlbumsByArtist = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var distinctCovers = new HashSet<string>(StringComparer.Ordinal);
            var tagsList = new List<KeyValuePair<byte, string>[]>(cleanSongs.Count);

            for (int i = 0; i < cleanSongs.Count; i++)
            {
                var s = cleanSongs[i];
                try
                {
                    string rel = SubsonicUrlHelper.BuildRelativePath(s);
                    if (byPath.ContainsKey(rel) && !string.IsNullOrEmpty(s.Id))
                    {
                        SubsonicUrlHelper.SplitStemAndExtensionSafe(rel, out string stem, out string ext);
                        string shortId = s.Id.Length > 8 ? s.Id.Substring(0, 8) : s.Id;
                        rel = $"{stem} [{SubsonicUrlHelper.Sanitize(shortId)}]{ext}";
                    }

                    s.CachedRelativePath = rel;
                    s.CachedTags = null;
                    var tags = SubsonicUrlHelper.ToTags(s);
                    s.CachedTags = tags;

                    string fname = SubsonicUrlHelper.GetFileNameSafe(rel);
                    string artistName = SubsonicUrlHelper.SanitizeFolderSegment(!string.IsNullOrEmpty(s.AlbumArtist) ? s.AlbumArtist : s.Artist);
                    string albumFolder = SubsonicUrlHelper.SanitizeFolderSegment(!string.IsNullOrEmpty(s.Album) ? s.Album : "Unknown Album");
                    string artistAlbumKey = $"{artistName}\\{albumFolder}";
                    string globalAlbKey = $"{albumFolder} — {artistName}";
                    string canonicalCover = s.CanonicalCoverId;

                    s.CatalogIndex = indexedSongs.Count;
                    indexedSongs.Add(s);
                    tagsList.Add(tags);

                    byPath[rel] = s;
                    byPath["Navidrome:\\" + rel] = s;
                    if (!string.IsNullOrEmpty(s.Id))
                        byId[s.Id] = s;

                    try { IndexLocalMatchKeys(localMatch, s, rel); }
                    catch { }

                    if (!string.IsNullOrEmpty(fname))
                    {
                        if (byUniqueFileName.ContainsKey(fname))
                            byUniqueFileName[fname] = null; // Ambiguous filename across albums
                        else
                            byUniqueFileName[fname] = s;
                    }

                    if (!string.IsNullOrEmpty(canonicalCover))
                    {
                        distinctCovers.Add(canonicalCover);
                        if (!string.IsNullOrEmpty(s.CoverArt))
                            coverToCanonical[s.CoverArt] = canonicalCover;
                        if (!string.IsNullOrEmpty(s.Id))
                            coverToCanonical[s.Id] = canonicalCover;
                        if (!string.IsNullOrEmpty(s.AlbumId))
                            coverToCanonical["al-" + s.AlbumId] = canonicalCover;
                    }

                    if (s.Starred) starredSongs.Add(s);
                    if (s.UserRating >= 3) topRatedSongs.Add(s);
                    if (s.PlayCount > 0) mostPlayedSongs.Add(s);

                    if (!byArtist.TryGetValue(artistName, out var artistList))
                    {
                        artistList = new List<SubsonicSong>();
                        byArtist[artistName] = artistList;
                    }
                    artistList.Add(s);

                    if (!byAlbumKey.TryGetValue(artistAlbumKey, out var albSongs))
                    {
                        albSongs = new List<SubsonicSong>();
                        byAlbumKey[artistAlbumKey] = albSongs;
                    }
                    albSongs.Add(s);

                    if (!byAlbumKey.ContainsKey(albumFolder))
                        byAlbumKey[albumFolder] = albSongs;

                    if (!string.IsNullOrEmpty(canonicalCover))
                    {
                        if (!coverBySanitizedAlbumDir.ContainsKey(artistAlbumKey))
                            coverBySanitizedAlbumDir[artistAlbumKey] = canonicalCover;
                        if (!coverBySanitizedAlbumName.ContainsKey(albumFolder))
                            coverBySanitizedAlbumName[albumFolder] = canonicalCover;
                    }

                    if (!albumsByArtist.TryGetValue(artistName, out var albList))
                    {
                        albList = new List<string>();
                        albumsByArtist[artistName] = albList;
                        seenAlbumsByArtist[artistName] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    }
                    if (seenAlbumsByArtist[artistName].Add(albumFolder))
                        albList.Add(albumFolder);

                    if (seenAlbums.Add(globalAlbKey))
                    {
                        allAlbumKeys.Add(globalAlbKey);
                        byAlbumKey[globalAlbKey] = albSongs;
                    }
                }
                catch { }
            }

            allAlbumKeys.Sort(StringComparer.OrdinalIgnoreCase);
            topRatedSongs.Sort((a, b) => b.UserRating.CompareTo(a.UserRating));
            mostPlayedSongs.Sort((a, b) => b.PlayCount.CompareTo(a.PlayCount));

            _allSongs = indexedSongs;
            _allTagArrays = tagsList.ToArray();
            _byPath = byPath;
            _byUniqueFileName = byUniqueFileName;
            _byId = byId;
            CommitLocalMatchIndexesLocked(localMatch);
            _byArtist = byArtist;
            _byAlbumKey = byAlbumKey;
            _albumsByArtist = albumsByArtist;
            _coverToCanonical = coverToCanonical;
            _coverBySanitizedAlbumDir = coverBySanitizedAlbumDir;
            _coverBySanitizedAlbumName = coverBySanitizedAlbumName;
            _allAlbumKeys = allAlbumKeys;
            _distinctAlbumCoverIds = distinctCovers.ToList();
            _starredSongs = starredSongs;
            _topRatedSongs = topRatedSongs;
            _mostPlayedSongs = mostPlayedSongs;
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
