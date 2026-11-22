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

        public void UpdateStarred(string songId, bool starred)
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

                    s.CachedTags = null;
                    s.CachedTags = SubsonicUrlHelper.ToTags(s);
                    RebuildAllTagArraysLocked();
                    changed = true;
                }
            }
            if (changed)
                ScheduleSaveToDisk();
        }

        public void UpdateRating(string songId, int userRating)
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

                    s.CachedTags = null;
                    s.CachedTags = SubsonicUrlHelper.ToTags(s);
                    RebuildAllTagArraysLocked();
                    changed = true;
                }
            }
            if (changed)
                ScheduleSaveToDisk();
        }

        public void IncrementPlayCount(string songId)
        {
            if (string.IsNullOrEmpty(songId)) return;
            bool changed = false;
            lock (_lock)
            {
                if (_byId.TryGetValue(songId, out var s))
                {
                    s.PlayCount++;
                    s.Played = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                    if (!_mostPlayedSongs.Contains(s))
                        _mostPlayedSongs.Add(s);
                    _mostPlayedSongs.Sort((a, b) => b.PlayCount.CompareTo(a.PlayCount));

                    s.CachedTags = null;
                    s.CachedTags = SubsonicUrlHelper.ToTags(s);
                    RebuildAllTagArraysLocked();
                    changed = true;
                }
            }
            if (changed)
                ScheduleSaveToDisk();
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

            // Also sample top-rated and recently played albums to pick up ratings/plays made on other devices.
            var albumIdsToProbe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var highest = client.GetAlbumList2("highest", 25, 0);
                if (highest != null)
                {
                    foreach (var a in highest)
                        if (!string.IsNullOrEmpty(a.Id)) albumIdsToProbe.Add(a.Id);
                }
                if (!ct.IsCancellationRequested)
                {
                    var recent = client.GetAlbumList2("recent", 20, 0);
                    if (recent != null)
                    {
                        foreach (var a in recent)
                            if (!string.IsNullOrEmpty(a.Id)) albumIdsToProbe.Add(a.Id);
                    }
                }
            }
            catch { }

            int probed = 0;
            foreach (var albId in albumIdsToProbe)
            {
                if (ct.IsCancellationRequested || probed >= 30) break;
                probed++;
                var albSongs = client.GetAlbum(albId);
                if (albSongs == null) continue;
                foreach (var s in albSongs)
                {
                    if (!string.IsNullOrEmpty(s.Id))
                        serverUpdates[s.Id] = s;
                }
            }

            if (ct.IsCancellationRequested)
                return false;

            bool anyChanged = false;
            lock (_lock)
            {
                for (int i = 0; i < _allSongs.Count; i++)
                {
                    var local = _allSongs[i];
                    bool songChanged = false;

                    bool serverStarred = !string.IsNullOrEmpty(local.Id) && starredIds.Contains(local.Id);
                    if (local.Starred != serverStarred)
                    {
                        local.Starred = serverStarred;
                        songChanged = true;
                    }

                    if (!string.IsNullOrEmpty(local.Id) && serverUpdates.TryGetValue(local.Id, out var remote))
                    {
                        if (local.UserRating != remote.UserRating)
                        {
                            local.UserRating = remote.UserRating;
                            songChanged = true;
                        }
                        if (remote.PlayCount > local.PlayCount)
                        {
                            local.PlayCount = remote.PlayCount;
                            songChanged = true;
                        }
                        if (!string.IsNullOrEmpty(remote.Played) && !string.Equals(local.Played, remote.Played, StringComparison.Ordinal))
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

            if (anyChanged)
                ScheduleSaveToDisk(1000);

            return anyChanged;
        }
    }
}
