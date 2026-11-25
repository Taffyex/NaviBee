using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace MusicBeePlugin
{
    public partial class Plugin
    {
        private static readonly object _nowPlayingLock = new object();
        private static string _lastNowPlayingUrl = "";
        private static readonly ConcurrentDictionary<string, TrackTagSnapshot> _lastKnownTags =
            new ConcurrentDictionary<string, TrackTagSnapshot>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, TrackPlayBaseline> _playBaselines =
            new ConcurrentDictionary<string, TrackPlayBaseline>(StringComparer.OrdinalIgnoreCase);

        internal static void ResetSyncTracking()
        {
            _lastKnownTags.Clear();
            _playBaselines.Clear();
            lock (_nowPlayingLock) { _lastNowPlayingUrl = ""; }
        }

        private static bool TryResolveSong(string sourceFileUrl, out SubsonicSong song, bool allowPlaceholder = true)
        {
            song = null;
            if (string.IsNullOrEmpty(sourceFileUrl)) return false;

            bool isVirtual = LocalLibraryLinker.IsVirtualNavidromeUrl(sourceFileUrl);
            if (isVirtual)
            {
                string clean = CleanPath(sourceFileUrl);
                song = NavidromeCatalog.Instance.FindSongByPath(clean)
                    ?? NavidromeCatalog.Instance.FindSongById(clean);
                if (song != null) return true;

                lock (CacheLock)
                {
                    if (SongByPath.TryGetValue(clean, out song) || SongById.TryGetValue(clean, out song))
                        return true;
                }

                string id = Api.Library_GetFileTag != null ? Api.Library_GetFileTag(sourceFileUrl, MetaDataType.Custom16) : null;
                if (!string.IsNullOrEmpty(id))
                {
                    song = NavidromeCatalog.Instance.FindSongById(id);
                    if (song != null) return true;
                    lock (CacheLock) { if (SongById.TryGetValue(id, out song)) return true; }
                    if (!allowPlaceholder) return false;
                    song = new SubsonicSong { Id = id };
                    return true;
                }
                return false;
            }

            // Local file resolution (when local library linking is enabled)
            if (Settings != null && !Settings.LinkLocalLibraryTracks)
                return false;

            if (LocalLibraryLinker.Instance.TryGetLinkedSong(sourceFileUrl, out song))
                return true;

            if (Api.Library_GetFileTag != null)
            {
                MetaDataType pidField = Settings != null ? Settings.GetPidMetaDataType() : MetaDataType.Custom16;
                string pid = Api.Library_GetFileTag(sourceFileUrl, pidField);
                if (string.IsNullOrWhiteSpace(pid) && pidField != MetaDataType.Custom16)
                    pid = Api.Library_GetFileTag(sourceFileUrl, MetaDataType.Custom16);

                if (!string.IsNullOrWhiteSpace(pid))
                {
                    pid = pid.Trim();
                    song = NavidromeCatalog.Instance.FindSongById(pid);
                    if (song == null)
                    {
                        lock (CacheLock) { SongById.TryGetValue(pid, out song); }
                    }
                    if (song != null)
                    {
                        if (LocalLibraryLinker.Instance.UpsertLink(sourceFileUrl, song.Id, "pid_tag", persist: false))
                            LocalLibraryLinker.Instance.ScheduleSaveToDisk();
                        return true;
                    }
                }
            }

            if (!NavidromeCatalog.Instance.HasData)
                return false;

            song = NavidromeCatalog.Instance.FindSongByServerPathSuffix(sourceFileUrl);
            if (song != null)
            {
                if (LocalLibraryLinker.Instance.UpsertLink(sourceFileUrl, song.Id, "path_suffix", persist: false))
                    LocalLibraryLinker.Instance.ScheduleSaveToDisk();
                return true;
            }

            var info = ReadLocalTrackInfo(sourceFileUrl, Settings != null ? Settings.GetPidMetaDataType() : MetaDataType.Custom16);
            song = NavidromeCatalog.Instance.FindSongByMetadata(
                info.Artist, info.AlbumArtist, info.Album, info.DiscNo, info.TrackNo, info.Title, info.DurationSeconds, info.PrimaryArtist);
            if (song != null)
            {
                if (LocalLibraryLinker.Instance.UpsertLink(sourceFileUrl, song.Id, "metadata", persist: false))
                    LocalLibraryLinker.Instance.ScheduleSaveToDisk();
                return true;
            }

            return false;
        }

        private static bool ParseLoveTag(string love)
        {
            return string.Equals(love, "L", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(love, "1", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(love, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static void CaptureLibraryTagSnapshot(string sourceFileUrl, SubsonicSong song)
        {
            if (song == null || string.IsNullOrEmpty(song.Id) || Api.Library_GetFileTag == null) return;
            try
            {
                string love = Api.Library_GetFileTag(sourceFileUrl, MetaDataType.RatingLove) ?? "";
                string ratingStr = Api.Library_GetFileTag(sourceFileUrl, MetaDataType.Rating) ?? "";
                TryParseStarRating(ratingStr, out int stars);
                _lastKnownTags[song.Id] = new TrackTagSnapshot
                {
                    IsLoved = ParseLoveTag(love),
                    Stars = stars
                };
            }
            catch { }
        }

        private static TrackPlayBaseline ReadPlayBaseline(string sourceFileUrl)
        {
            var b = new TrackPlayBaseline();
            if (Api.Library_GetFileProperty == null || string.IsNullOrEmpty(sourceFileUrl))
                return b;
            try
            {
                string playStr = Api.Library_GetFileProperty(sourceFileUrl, FilePropertyType.PlayCount);
                if (int.TryParse(playStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pc))
                {
                    b.PlayCount = pc;
                    b.HasPlayCount = true;
                }
                string skipStr = Api.Library_GetFileProperty(sourceFileUrl, FilePropertyType.SkipCount);
                if (int.TryParse(skipStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int sc))
                {
                    b.SkipCount = sc;
                    b.HasSkipCount = true;
                }
            }
            catch { }
            return b;
        }

        private void OnSelectedFileChanged(string sourceFileUrl)
        {
            if (string.IsNullOrEmpty(sourceFileUrl)) return;
            if (TryResolveSong(sourceFileUrl, out var song, allowPlaceholder: false))
                CaptureLibraryTagSnapshot(sourceFileUrl, song);
        }

        private void OnTrackChanged(string sourceFileUrl, bool isPlayStateEvent)
        {
            if (isPlayStateEvent)
            {
                if (Api.Player_GetPlayState != null && Api.Player_GetPlayState() != PlayState.Playing)
                {
                    if (Api.Player_GetPlayState() == PlayState.Stopped)
                    {
                        lock (_nowPlayingLock) { _lastNowPlayingUrl = ""; }
                    }
                    return;
                }
                if (string.IsNullOrEmpty(sourceFileUrl) && Api.NowPlaying_GetFileUrl != null)
                    sourceFileUrl = Api.NowPlaying_GetFileUrl();
            }

            if (!TryResolveSong(sourceFileUrl, out var song)) return;

            CaptureLibraryTagSnapshot(sourceFileUrl, song);
            _playBaselines[song.Id] = ReadPlayBaseline(sourceFileUrl);

            lock (_nowPlayingLock)
            {
                if (isPlayStateEvent && string.Equals(_lastNowPlayingUrl, sourceFileUrl, StringComparison.OrdinalIgnoreCase))
                    return;
                _lastNowPlayingUrl = sourceFileUrl ?? "";
            }

            bool isVirtual = LocalLibraryLinker.IsVirtualNavidromeUrl(sourceFileUrl);
            int targetSize = Settings != null ? Settings.ArtworkSize : 800;
            var ct = ShutdownToken;
            Task.Run(() =>
            {
                if (ct.IsCancellationRequested) return;
                Subsonic?.Scrobble(song.Id, submission: false);

                if (!isVirtual)
                    return;

                try
                {
                    var lyr = LyricsCache.Instance.GetOrFetchAsync(song, Subsonic).GetAwaiter().GetResult();
                    if (lyr != null && lyr.HasLyrics && !ct.IsCancellationRequested)
                        MusicBeeLyricsBridge.AttachLyrics(sourceFileUrl, lyr.Text, lyr.IsSynced, refreshActivePanel: true);
                }
                catch { }

                if (MusicBeeArtworkBridge.IsAvailable && !ct.IsCancellationRequested)
                {
                    string coverId = !string.IsNullOrEmpty(song.CanonicalCoverId) ? song.CanonicalCoverId : song.CoverArt;
                    if (string.IsNullOrEmpty(coverId))
                        coverId = NavidromeCatalog.Instance.ResolveCoverArtId(sourceFileUrl);

                    if (!string.IsNullOrEmpty(coverId))
                    {
                        string localPath = ArtworkCache.Instance.GetOrFetchLocalFilePath(coverId, Subsonic, targetSize);
                        if (localPath != null && MusicBeeArtworkBridge.AttachArtwork(sourceFileUrl, localPath))
                            Api.MB_RefreshPanels?.Invoke();
                    }
                }
            }, ct);

            if (Api.NowPlayingList_GetCurrentIndex != null && Api.NowPlayingList_GetListFileUrl != null)
            {
                int curIdx = Api.NowPlayingList_GetCurrentIndex();
                if (curIdx >= 0)
                {
                    string nextUrl = Api.NowPlayingList_GetListFileUrl(curIdx + 1);
                    if (LocalLibraryLinker.IsVirtualNavidromeUrl(nextUrl) && TryResolveSong(nextUrl, out var nextSong))
                    {
                        LyricsCache.Instance.PrefetchAsync(nextSong, Subsonic);
                        if (Settings != null && Settings.PrefetchNextTrack)
                            AudioStreamCache.Instance.PrefetchNextTrack(nextSong, Subsonic, Settings);
                    }
                }
            }
        }

        private static bool TryParseStarRating(string ratingStr, out int stars)
        {
            stars = 0;
            if (string.IsNullOrWhiteSpace(ratingStr))
                return true;

            if (double.TryParse(ratingStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var val) ||
                double.TryParse(ratingStr, NumberStyles.Any, CultureInfo.CurrentCulture, out val))
            {
                if (val < 0) return false;
                double scaled = val > 5.0 ? (val / 20.0) : val;
                stars = Math.Max(0, Math.Min(5, (int)Math.Round(scaled, MidpointRounding.AwayFromZero)));
                return true;
            }
            return false;
        }

        private void OnPlayCountersChanged(string sourceFileUrl)
        {
            if (!TryResolveSong(sourceFileUrl, out var song)) return;

            var current = ReadPlayBaseline(sourceFileUrl);
            if (_playBaselines.TryGetValue(song.Id, out var prev))
            {
                bool skipIncreased = current.HasSkipCount && prev.HasSkipCount && current.SkipCount > prev.SkipCount;
                bool playIncreased = current.HasPlayCount && prev.HasPlayCount && current.PlayCount > prev.PlayCount;
                _playBaselines[song.Id] = current;

                // Do not scrobble skips or server-pulled playcount updates that did not increment local play count.
                if (skipIncreased && !playIncreased)
                    return;
                if (current.HasPlayCount && prev.HasPlayCount && !playIncreased)
                    return;
            }
            else
            {
                _playBaselines[song.Id] = current;
            }

            NavidromeCatalog.Instance.IncrementPlayCount(song.Id);
            string label = FriendlyErrors.FormatTrackLabel(song);
            long playedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var ct = ShutdownToken;

            Task.Run(() =>
            {
                if (ct.IsCancellationRequested) return;
                if (Subsonic != null && Subsonic.Scrobble(song.Id, submission: true, playedAtMs))
                {
                    Api.MB_SetBackgroundTaskMessage?.Invoke($"Navidrome: scrobbled \"{label}\"");
                }
                else
                {
                    PendingSyncQueue.Instance.EnqueueScrobble(song.Id, playedAtMs);
                    Api.MB_SetBackgroundTaskMessage?.Invoke($"Navidrome offline: queued scrobble for \"{label}\"");
                }
            }, ct);
        }

        public static void EvaluateTagSyncDecision(
            bool hasPreviousSnapshot,
            TrackTagSnapshot prev,
            bool serverStarred,
            int serverRating,
            string loveRaw,
            string ratingRaw,
            out bool shouldPushStar,
            out bool newStarred,
            out bool shouldPushRating,
            out int newRating)
        {
            shouldPushStar = false;
            shouldPushRating = false;
            newStarred = ParseLoveTag(loveRaw);
            bool validRating = TryParseStarRating(ratingRaw, out newRating);

            if (hasPreviousSnapshot)
            {
                if (newStarred != prev.IsLoved && newStarred != serverStarred)
                    shouldPushStar = true;
                if (validRating && newRating != prev.Stars && newRating != serverRating)
                    shouldPushRating = true;
            }
            else
            {
                // Without a prior MusicBee snapshot, never wipe server state with blank fields.
                if (newStarred && !serverStarred)
                    shouldPushStar = true;
                if (validRating && newRating > 0 && newRating != serverRating)
                    shouldPushRating = true;
            }
        }

        private void OnTagsChanged(string sourceFileUrl, NotificationType type)
        {
            if (Volatile.Read(ref _suppressTagChangeNotifications) > 0)
                return;
            if (!TryResolveSong(sourceFileUrl, out var song, allowPlaceholder: false))
                return;

            string loveRaw = Api.Library_GetFileTag != null ? Api.Library_GetFileTag(sourceFileUrl, MetaDataType.RatingLove) : "";
            string ratingRaw = Api.Library_GetFileTag != null ? Api.Library_GetFileTag(sourceFileUrl, MetaDataType.Rating) : "";

            // Virtual storage tracks that are not in the main .mbl library may only expose live edits via NowPlaying_GetFileTag.
            if (string.IsNullOrEmpty(loveRaw) && string.IsNullOrEmpty(ratingRaw) &&
                LocalLibraryLinker.IsVirtualNavidromeUrl(sourceFileUrl) &&
                Api.NowPlaying_GetFileUrl != null && Api.NowPlaying_GetFileTag != null &&
                string.Equals(Api.NowPlaying_GetFileUrl(), sourceFileUrl, StringComparison.OrdinalIgnoreCase))
            {
                loveRaw = Api.NowPlaying_GetFileTag(MetaDataType.RatingLove) ?? "";
                ratingRaw = Api.NowPlaying_GetFileTag(MetaDataType.Rating) ?? "";
            }

            bool hasPrev = _lastKnownTags.TryGetValue(song.Id, out var prev);
            EvaluateTagSyncDecision(
                hasPrev,
                prev,
                song.Starred,
                song.UserRating,
                loveRaw,
                ratingRaw,
                out bool pushStar,
                out bool isLoved,
                out bool pushRating,
                out int stars);

            _lastKnownTags[song.Id] = new TrackTagSnapshot { IsLoved = isLoved, Stars = stars };

            var ct = ShutdownToken;
            if (pushStar)
            {
                NavidromeCatalog.Instance.UpdateStarred(song.Id, isLoved);
                Task.Run(() =>
                {
                    if (ct.IsCancellationRequested) return;
                    if (Subsonic == null || !Subsonic.Star(song.Id, isLoved))
                        PendingSyncQueue.Instance.EnqueueStar(song.Id, isLoved);
                }, ct);
            }

            if (pushRating)
            {
                NavidromeCatalog.Instance.UpdateRating(song.Id, stars);
                Task.Run(() =>
                {
                    if (ct.IsCancellationRequested) return;
                    if (Subsonic == null || !Subsonic.SetRating(song.Id, stars))
                        PendingSyncQueue.Instance.EnqueueRating(song.Id, stars);
                }, ct);
            }
        }
    }
}
