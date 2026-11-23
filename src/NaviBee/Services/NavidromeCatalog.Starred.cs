using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace MusicBeePlugin
{
    public partial class NavidromeCatalog
    {
        private void RebuildAllTagArraysLocked()
        {
            if (_allSongs == null || _allSongs.Count == 0)
            {
                _allTagArrays = new KeyValuePair<byte, string>[0][];
                return;
            }

            var arr = new KeyValuePair<byte, string>[_allSongs.Count][];
            for (int i = 0; i < _allSongs.Count; i++)
            {
                var s = _allSongs[i];
                arr[i] = s.CachedTags ?? SubsonicUrlHelper.ToTags(s);
            }
            _allTagArrays = arr;
        }

        private void RebuildDerivedListsLocked()
        {
            var starred = new List<SubsonicSong>();
            var topRated = new List<SubsonicSong>();
            var mostPlayed = new List<SubsonicSong>();

            for (int i = 0; i < _allSongs.Count; i++)
            {
                var s = _allSongs[i];
                if (s.Starred) starred.Add(s);
                if (s.UserRating >= 3) topRated.Add(s);
                if (s.PlayCount > 0) mostPlayed.Add(s);
            }

            topRated.Sort((a, b) => b.UserRating.CompareTo(a.UserRating));
            mostPlayed.Sort((a, b) => b.PlayCount.CompareTo(a.PlayCount));

            _starredSongs = starred;
            _topRatedSongs = topRated;
            _mostPlayedSongs = mostPlayed;
        }

        private void UpdateSingleSongTagArrayLocked(SubsonicSong s)
        {
            s.CachedTags = null;
            s.CachedTags = SubsonicUrlHelper.ToTags(s);
            if (s.CatalogIndex >= 0 && _allTagArrays != null && s.CatalogIndex < _allTagArrays.Length)
                _allTagArrays[s.CatalogIndex] = s.CachedTags;
            else
                RebuildAllTagArraysLocked();
        }

        public void UpdateStarred(string songId, bool starred, bool scheduleSave = true)
        {
            if (string.IsNullOrEmpty(songId)) return;
            bool changed = false;
            lock (_lock)
            {
                if (_byId.TryGetValue(songId, out var s) && s.Starred != starred)
                {
                    s.Starred = starred;
                    if (starred && !_starredSongs.Contains(s))
                        _starredSongs.Add(s);
                    else if (!starred)
                        _starredSongs.Remove(s);

                    UpdateSingleSongTagArrayLocked(s);
                    changed = true;
                }
            }
            if (changed && scheduleSave)
                ScheduleSaveToDisk();
        }

        public void UpdateRating(string songId, int userRating, bool scheduleSave = true)
        {
            if (string.IsNullOrEmpty(songId)) return;
            int clamped = Math.Max(0, Math.Min(5, userRating));
            bool changed = false;
            lock (_lock)
            {
                if (_byId.TryGetValue(songId, out var s) && s.UserRating != clamped)
                {
                    s.UserRating = clamped;
                    if (clamped >= 3 && !_topRatedSongs.Contains(s))
                        _topRatedSongs.Add(s);
                    else if (clamped < 3)
                        _topRatedSongs.Remove(s);
                    _topRatedSongs.Sort((a, b) => b.UserRating.CompareTo(a.UserRating));

                    UpdateSingleSongTagArrayLocked(s);
                    changed = true;
                }
            }
            if (changed && scheduleSave)
                ScheduleSaveToDisk();
        }

        public void IncrementPlayCount(string songId)
        {
            if (string.IsNullOrEmpty(songId)) return;
            int nextCount = 1;
            lock (_lock)
            {
                if (_byId.TryGetValue(songId, out var s))
                    nextCount = s.PlayCount + 1;
                else
                    return;
            }
            UpdatePlayCount(songId, nextCount, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        }

        public void UpdatePlayCount(string songId, int playCount, string playedUtc = null)
        {
            if (string.IsNullOrEmpty(songId)) return;
            bool changed = false;
            lock (_lock)
            {
                if (_byId.TryGetValue(songId, out var s))
                {
                    if (playCount > s.PlayCount)
                    {
                        s.PlayCount = playCount;
                        changed = true;
                    }
                    if (!string.IsNullOrWhiteSpace(playedUtc) && IsNewerPlayedTimestamp(playedUtc, s.Played))
                    {
                        s.Played = playedUtc;
                        changed = true;
                    }

                    if (changed)
                    {
                        if (s.PlayCount > 0 && !_mostPlayedSongs.Contains(s))
                            _mostPlayedSongs.Add(s);
                        _mostPlayedSongs.Sort((a, b) => b.PlayCount.CompareTo(a.PlayCount));

                        UpdateSingleSongTagArrayLocked(s);
                    }
                }
            }
            if (changed)
                ScheduleSaveToDisk();
        }

        internal static bool TryParseUtcTimestamp(string raw, out DateTime utc)
        {
            utc = DateTime.MinValue;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            return DateTime.TryParse(
                raw.Trim(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out utc);
        }

        internal static bool IsNewerPlayedTimestamp(string candidateRaw, string existingRaw)
        {
            if (string.IsNullOrWhiteSpace(candidateRaw)) return false;
            if (string.IsNullOrWhiteSpace(existingRaw)) return true;
            if (string.Equals(candidateRaw, existingRaw, StringComparison.Ordinal)) return false;
            if (TryParseUtcTimestamp(candidateRaw, out var candUtc) && TryParseUtcTimestamp(existingRaw, out var existUtc))
                return candUtc > existUtc.AddSeconds(1);
            return true;
        }

        public bool RefreshUserState(SubsonicClient client, CancellationToken ct = default)
        {
            if (client == null || !HasData || ct.IsCancellationRequested)
                return false;

            var starredFromServer = client.GetStarred2();
            if (starredFromServer == null || ct.IsCancellationRequested)
                return false;

            var starredIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var serverUpdates = new Dictionary<string, SubsonicSong>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in starredFromServer)
            {
                if (!string.IsNullOrEmpty(s.Id))
                {
                    starredIds.Add(s.Id);
                    serverUpdates[s.Id] = s;
                }
            }

            int songCountSnapshot;
            DateTime lastSyncUtc;
            lock (_lock)
            {
                songCountSnapshot = _allSongs.Count;
                TryParseUtcTimestamp(_meta.LastUserStateSyncUtc, out lastSyncUtc);
            }

            // For catalogs <= 25,000 tracks, a fast paginated search3 captures every playcount,
            // lastPlayed, and rating change made in Synfonium/mobile clients without missing anything.
            bool fetchedViaSearch = false;
            bool fallbackHadError = false;
            if (songCountSnapshot <= 25000 && !ct.IsCancellationRequested)
            {
                try
                {
                    int offset = 0;
                    const int pageSize = 500;
                    int maxOffset = Math.Max(26000, songCountSnapshot + 2000);
                    var seenSearchIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    bool searchHadError = false;
                    int consecutiveZeroNew = 0;

                    while (!ct.IsCancellationRequested && offset < maxOffset)
                    {
                        List<SubsonicSong> page = null;
                        bool ok = false;
                        for (int attempt = 0; attempt < 2 && !ct.IsCancellationRequested; attempt++)
                        {
                            if (attempt > 0 && !WaitBackoff(500, ct))
                                return false;
                            if (client.TrySearchSongs(pageSize, offset, "", out page, out _, 60000))
                            {
                                ok = true;
                                break;
                            }
                        }

                        if (!ok)
                        {
                            searchHadError = true;
                            break;
                        }
                        if (page == null || page.Count == 0)
                            break;

                        int addedThisPage = 0;
                        foreach (var s in page)
                        {
                            if (s == null || string.IsNullOrEmpty(s.Id)) continue;
                            serverUpdates[s.Id] = s;
                            if (s.Starred) starredIds.Add(s.Id);
                            if (seenSearchIds.Add(s.Id))
                                addedThisPage++;
                        }

                        if (addedThisPage == 0)
                        {
                            if (++consecutiveZeroNew >= 2)
                                break;
                        }
                        else
                        {
                            consecutiveZeroNew = 0;
                        }

                        offset += page.Count;
                    }

                    fetchedViaSearch = !searchHadError &&
                                       seenSearchIds.Count > 0 &&
                                       seenSearchIds.Count >= (int)Math.Ceiling(songCountSnapshot * 0.90);
                }
                catch { }
            }

            // Fallback for very large catalogs (>25k tracks) or servers where empty search3 is unsupported/truncated:
            // paginate all recent albums newer than LastUserStateSyncUtc plus all highest-rated albums.
            if (!fetchedViaSearch && !ct.IsCancellationRequested)
            {
                var albumIdsToProbe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!CollectAlbumIdsForUserState(client, "recent", lastSyncUtc, albumIdsToProbe, ct))
                    fallbackHadError = true;
                if (!CollectAlbumIdsForUserState(client, "highest", DateTime.MinValue, albumIdsToProbe, ct))
                    fallbackHadError = true;

                foreach (var albId in albumIdsToProbe)
                {
                    if (ct.IsCancellationRequested) break;
                    if (!client.TryGetAlbum(albId, out var albSongs, out _, 30000)) { fallbackHadError = true; continue; }
                    foreach (var s in albSongs)
                        if (s != null && !string.IsNullOrEmpty(s.Id)) serverUpdates[s.Id] = s;
                }
            }

            if (ct.IsCancellationRequested)
                return false;

            bool anyChanged = false;
            lock (_lock)
            {
                if (fetchedViaSearch || !fallbackHadError)
                    _meta.LastUserStateSyncUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

                for (int i = 0; i < _allSongs.Count; i++)
                {
                    var local = _allSongs[i];
                    bool songChanged = false;

                    bool serverStarred = !string.IsNullOrEmpty(local.Id) && starredIds.Contains(local.Id);
                    bool targetStarred = PendingSyncQueue.Instance.TryGetPendingStar(local.Id, out bool pendingStar)
                        ? pendingStar
                        : serverStarred;
                    if (local.Starred != targetStarred)
                    {
                        local.Starred = targetStarred;
                        songChanged = true;
                    }

                    if (!string.IsNullOrEmpty(local.Id) && serverUpdates.TryGetValue(local.Id, out var remote))
                    {
                        int targetRating = PendingSyncQueue.Instance.TryGetPendingRating(local.Id, out int pendingRating)
                            ? pendingRating
                            : remote.UserRating;
                        if (local.UserRating != targetRating)
                        {
                            local.UserRating = targetRating;
                            songChanged = true;
                        }
                        if (remote.PlayCount > local.PlayCount)
                        {
                            local.PlayCount = remote.PlayCount;
                            songChanged = true;
                        }
                        if (!string.IsNullOrEmpty(remote.Played) && IsNewerPlayedTimestamp(remote.Played, local.Played))
                        {
                            local.Played = remote.Played;
                            songChanged = true;
                        }
                    }

                    if (songChanged)
                    {
                        local.CachedTags = null;
                        local.CachedTags = SubsonicUrlHelper.ToTags(local);
                        anyChanged = true;
                    }
                }

                if (anyChanged)
                {
                    RebuildDerivedListsLocked();
                    RebuildAllTagArraysLocked();
                }
            }

            ScheduleSaveToDisk(1000);
            return anyChanged;
        }

        private static bool CollectAlbumIdsForUserState(
            SubsonicClient client,
            string listType,
            DateTime cutoffUtc,
            HashSet<string> target,
            CancellationToken ct)
        {
            const int pageSize = 500;
            int offset = 0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                while (offset < 2500 && !ct.IsCancellationRequested)
                {
                    if (!client.TryGetAlbumList2(listType, pageSize, offset, out var page, out _, 60000))
                        return false;
                    if (page == null || page.Count == 0) break;
                    bool reachedCutoff = false;
                    int newIds = 0;
                    foreach (var a in page)
                    {
                        if (a != null && !string.IsNullOrEmpty(a.Id))
                        {
                            target.Add(a.Id);
                            if (seen.Add(a.Id)) newIds++;
                        }
                        if (cutoffUtc != DateTime.MinValue &&
                            TryParseUtcTimestamp(a?.Played, out var albPlayed) &&
                            albPlayed < cutoffUtc.AddMinutes(-5))
                            reachedCutoff = true;
                    }
                    if (reachedCutoff || newIds == 0) break;
                    offset += page.Count;
                }
                return true;
            }
            catch { return false; }
        }
    }
}
