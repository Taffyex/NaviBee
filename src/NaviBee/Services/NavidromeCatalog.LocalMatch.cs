using System;
using System.Collections.Generic;

namespace MusicBeePlugin
{
    public partial class NavidromeCatalog
    {
        private void ResetLocalMatchIndexesLocked(int songCount)
        {
            _byServerSuffix3 = new Dictionary<string, SubsonicSong>(songCount, StringComparer.OrdinalIgnoreCase);
            _byServerSuffix2 = new Dictionary<string, SubsonicSong>(songCount, StringComparer.OrdinalIgnoreCase);
            _byServerStem3 = new Dictionary<string, SubsonicSong>(songCount, StringComparer.OrdinalIgnoreCase);
            _byServerStem2 = new Dictionary<string, SubsonicSong>(songCount, StringComparer.OrdinalIgnoreCase);
            _byServerDiscSuffix = new Dictionary<string, SubsonicSong>(StringComparer.OrdinalIgnoreCase);
            _byServerDiscStem = new Dictionary<string, SubsonicSong>(StringComparer.OrdinalIgnoreCase);
            _byMetaExact = new Dictionary<string, SubsonicSong>(songCount * 2, StringComparer.OrdinalIgnoreCase);
            _byMetaLoose = new Dictionary<string, List<SubsonicSong>>(songCount, StringComparer.OrdinalIgnoreCase);
            _byAlbumTitle = new Dictionary<string, List<SubsonicSong>>(songCount, StringComparer.OrdinalIgnoreCase);
            _byArtistTitle = new Dictionary<string, List<SubsonicSong>>(songCount, StringComparer.OrdinalIgnoreCase);
        }

        private static void AddUnique(Dictionary<string, SubsonicSong> dict, string key, SubsonicSong song)
        {
            if (string.IsNullOrEmpty(key) || song == null) return;
            if (dict.TryGetValue(key, out var existing))
            {
                if (existing != null && !string.Equals(existing.Id, song.Id, StringComparison.OrdinalIgnoreCase))
                    dict[key] = null; // Ambiguous key across different songs
            }
            else
            {
                dict[key] = song;
            }
        }

        private static void AddToBucket(Dictionary<string, List<SubsonicSong>> dict, string key, SubsonicSong song)
        {
            if (string.IsNullOrEmpty(key) || song == null) return;
            if (!dict.TryGetValue(key, out var list))
            {
                list = new List<SubsonicSong>(2);
                dict[key] = list;
            }
            for (int i = 0; i < list.Count; i++)
            {
                if (ReferenceEquals(list[i], song) || string.Equals(list[i].Id, song.Id, StringComparison.OrdinalIgnoreCase))
                    return;
            }
            list.Add(song);
        }

        private void IndexLocalMatchKeysLocked(SubsonicSong s, string syntheticRel)
        {
            string serverPath = !string.IsNullOrWhiteSpace(s.Path) ? s.Path : syntheticRel;
            LocalLibraryLinker.ExtractPathSuffixes(
                serverPath,
                out var suf3, out var suf2, out var stem3, out var stem2,
                out var discSuf, out var discStem);

            AddUnique(_byServerSuffix3, suf3, s);
            AddUnique(_byServerSuffix2, suf2, s);
            AddUnique(_byServerStem3, stem3, s);
            AddUnique(_byServerStem2, stem2, s);
            AddUnique(_byServerDiscSuffix, discSuf, s);
            AddUnique(_byServerDiscStem, discStem, s);

            string title = LocalLibraryLinker.NormalizeMetaToken(s.Title);
            if (string.IsNullOrEmpty(title)) return;
            string album = LocalLibraryLinker.NormalizeMetaToken(s.Album);
            int disc = s.DiscNumber > 0 ? s.DiscNumber : 1;

            var artistVariants = LocalLibraryLinker.ExtractArtistVariants(s.AlbumArtist, s.Artist);
            for (int i = 0; i < artistVariants.Count; i++)
            {
                string a = artistVariants[i];
                if (s.Track > 0)
                    AddUnique(_byMetaExact, $"{a}|{album}|{disc}|{s.Track}|{title}", s);

                AddToBucket(_byMetaLoose, $"{a}|{album}|{title}", s);
                AddToBucket(_byArtistTitle, $"{a}|{title}", s);
            }

            if (!string.IsNullOrEmpty(album))
                AddToBucket(_byAlbumTitle, $"{album}|{title}", s);
        }

        public SubsonicSong FindSongByServerPathSuffix(string localPath)
        {
            LocalLibraryLinker.ExtractPathSuffixes(
                localPath,
                out var suf3, out var suf2, out var stem3, out var stem2,
                out var discSuf, out var discStem);
            if (suf2 == null) return null;

            lock (_lock)
            {
                if (suf3 != null && _byServerSuffix3.TryGetValue(suf3, out var s) && s != null) return s;
                if (_byServerSuffix2.TryGetValue(suf2, out s) && s != null) return s;
                if (discSuf != null && _byServerSuffix2.TryGetValue(discSuf, out s) && s != null) return s;
                if (discSuf != null && _byServerDiscSuffix.TryGetValue(discSuf, out s) && s != null) return s;
                if (_byServerDiscSuffix.TryGetValue(suf2, out s) && s != null) return s;

                if (stem3 != null && _byServerStem3.TryGetValue(stem3, out s) && s != null) return s;
                if (_byServerStem2.TryGetValue(stem2, out s) && s != null) return s;
                if (discStem != null && _byServerStem2.TryGetValue(discStem, out s) && s != null) return s;
                if (discStem != null && _byServerDiscStem.TryGetValue(discStem, out s) && s != null) return s;
                if (stem2 != null && _byServerDiscStem.TryGetValue(stem2, out s) && s != null) return s;
                return null;
            }
        }

        public SubsonicSong FindSongByMetadata(
            string artist,
            string albumArtist,
            string album,
            int discNo,
            int trackNo,
            string title,
            int durationSeconds = 0,
            string primaryArtist = null)
        {
            string normTitle = LocalLibraryLinker.NormalizeMetaToken(title);
            if (string.IsNullOrEmpty(normTitle)) return null;

            string normAlbum = LocalLibraryLinker.NormalizeMetaToken(album);
            var artistVariants = LocalLibraryLinker.ExtractArtistVariants(albumArtist, artist, primaryArtist);
            int disc = discNo > 0 ? discNo : 1;

            lock (_lock)
            {
                // Tier 3: Exact ArtistVariant + Album + Disc + Track + Title
                if (trackNo > 0)
                {
                    for (int i = 0; i < artistVariants.Count; i++)
                    {
                        if (_byMetaExact.TryGetValue($"{artistVariants[i]}|{normAlbum}|{disc}|{trackNo}|{normTitle}", out var exact) && exact != null)
                            return exact;
                    }
                }

                // Tier 4: ArtistVariant + Album + Title (+ Duration or unique TrackNo)
                for (int i = 0; i < artistVariants.Count; i++)
                {
                    if (!_byMetaLoose.TryGetValue($"{artistVariants[i]}|{normAlbum}|{normTitle}", out var list) || list == null || list.Count == 0)
                        continue;

                    var matched = PickUniqueCandidate(list, trackNo, durationSeconds, durationTolerance: 3, requireDurationWhenMultiple: false);
                    if (matched != null) return matched;
                }

                // Tier 5a: Compilation / Various Artists mismatch — Album + Title disambiguated by TrackNo and/or Duration
                if (!string.IsNullOrEmpty(normAlbum) &&
                    _byAlbumTitle.TryGetValue($"{normAlbum}|{normTitle}", out var albList) &&
                    albList != null && albList.Count > 0)
                {
                    var matched = PickUniqueCompilationCandidate(albList, trackNo, durationSeconds);
                    if (matched != null) return matched;
                }

                // Tier 5b: Album-edition mismatch (e.g. "Album (Deluxe)" vs "Album") — strict ArtistVariant + Title + TrackNo + Duration ±2s
                if (durationSeconds > 0)
                {
                    for (int i = 0; i < artistVariants.Count; i++)
                    {
                        if (!_byArtistTitle.TryGetValue($"{artistVariants[i]}|{normTitle}", out var artList) || artList == null || artList.Count == 0)
                            continue;

                        var matched = PickUniqueEditionCandidate(artList, trackNo, durationSeconds);
                        if (matched != null) return matched;
                    }
                }

                return null;
            }
        }

        private static SubsonicSong PickUniqueCandidate(
            List<SubsonicSong> list,
            int trackNo,
            int durationSeconds,
            int durationTolerance,
            bool requireDurationWhenMultiple)
        {
            if (durationSeconds > 0)
            {
                SubsonicSong best = null;
                int matches = 0;
                for (int i = 0; i < list.Count; i++)
                {
                    var c = list[i];
                    if (c.Duration <= 0 || Math.Abs(c.Duration - durationSeconds) <= durationTolerance)
                    {
                        best = c;
                        matches++;
                    }
                }
                if (matches == 1) return best;
            }

            if (!requireDurationWhenMultiple && list.Count == 1 &&
                (trackNo <= 0 || list[0].Track <= 0 || list[0].Track == trackNo))
            {
                return list[0];
            }

            if (trackNo > 0)
            {
                SubsonicSong best = null;
                int matches = 0;
                for (int i = 0; i < list.Count; i++)
                {
                    var c = list[i];
                    if (c.Track == trackNo &&
                        (durationSeconds <= 0 || c.Duration <= 0 || Math.Abs(c.Duration - durationSeconds) <= durationTolerance))
                    {
                        best = c;
                        matches++;
                    }
                }
                if (matches == 1) return best;
            }

            return null;
        }

        private static SubsonicSong PickUniqueCompilationCandidate(List<SubsonicSong> list, int trackNo, int durationSeconds)
        {
            if (trackNo > 0 && durationSeconds > 0)
            {
                SubsonicSong best = null;
                int matches = 0;
                for (int i = 0; i < list.Count; i++)
                {
                    var c = list[i];
                    if (c.Track == trackNo && c.Duration > 0 && Math.Abs(c.Duration - durationSeconds) <= 3)
                    {
                        best = c;
                        matches++;
                    }
                }
                if (matches == 1) return best;
            }

            if (durationSeconds > 0)
            {
                SubsonicSong best = null;
                int matches = 0;
                for (int i = 0; i < list.Count; i++)
                {
                    var c = list[i];
                    if (c.Duration > 0 && Math.Abs(c.Duration - durationSeconds) <= 2)
                    {
                        best = c;
                        matches++;
                    }
                }
                if (matches == 1) return best;
            }

            if (trackNo > 0)
            {
                SubsonicSong best = null;
                int matches = 0;
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i].Track == trackNo)
                    {
                        best = list[i];
                        matches++;
                    }
                }
                if (matches == 1) return best;
            }

            return null;
        }

        private static SubsonicSong PickUniqueEditionCandidate(List<SubsonicSong> list, int trackNo, int durationSeconds)
        {
            SubsonicSong best = null;
            int matches = 0;
            for (int i = 0; i < list.Count; i++)
            {
                var c = list[i];
                if (c.Duration <= 0 || Math.Abs(c.Duration - durationSeconds) > 2)
                    continue;
                if (trackNo > 0 && c.Track > 0 && c.Track != trackNo)
                    continue;
                best = c;
                matches++;
            }
            return matches == 1 ? best : null;
        }

        public string DescribeNearestCandidate(LocalTrackInfo track)
        {
            string normTitle = LocalLibraryLinker.NormalizeMetaToken(track.Title);
            if (string.IsNullOrEmpty(normTitle)) return "";

            string normAlbum = LocalLibraryLinker.NormalizeMetaToken(track.Album);
            var artistVariants = LocalLibraryLinker.ExtractArtistVariants(track.AlbumArtist, track.Artist, track.PrimaryArtist);

            lock (_lock)
            {
                SubsonicSong sample = null;
                int count = 0;

                if (!string.IsNullOrEmpty(normAlbum) &&
                    _byAlbumTitle.TryGetValue($"{normAlbum}|{normTitle}", out var albList) &&
                    albList != null && albList.Count > 0)
                {
                    sample = albList[0];
                    count = albList.Count;
                }

                if (sample == null)
                {
                    for (int i = 0; i < artistVariants.Count; i++)
                    {
                        if (_byArtistTitle.TryGetValue($"{artistVariants[i]}|{normTitle}", out var artList) &&
                            artList != null && artList.Count > 0)
                        {
                            sample = artList[0];
                            count = artList.Count;
                            break;
                        }
                    }
                }

                if (sample == null) return "";
                string extra = count > 1 ? $" (+{count - 1} other candidates)" : "";
                return $"Id={sample.Id} | Artist=\"{sample.Artist}\" | AlbumArtist=\"{sample.AlbumArtist}\" | Album=\"{sample.Album}\" | Disc={sample.DiscNumber} Track={sample.Track} | Title=\"{sample.Title}\" | Dur={sample.Duration}s | Path=\"{sample.Path}\"{extra}";
            }
        }
    }
}
