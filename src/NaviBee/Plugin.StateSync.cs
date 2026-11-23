using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace MusicBeePlugin
{
    public struct TrackTagSnapshot
    {
        public bool IsLoved;
        public int Stars;
    }

    public struct TrackPlayBaseline
    {
        public int PlayCount;
        public int SkipCount;
        public bool HasPlayCount;
        public bool HasSkipCount;
    }

    public struct LibraryTrackSyncPlan
    {
        public bool PushStarToServer, PushRatingToServer, WriteLoveToLibrary, TargetLove;
        public bool WriteRatingToLibrary, WritePlayCountToLibrary, WriteLastPlayedToLibrary, FinalLoved;
        public int RatingToPush, TargetRating, TargetPlayCount, FinalStars;
        public DateTime TargetLastPlayedUtc;
        public bool HasLibraryWrites =>
            WriteLoveToLibrary || WriteRatingToLibrary || WritePlayCountToLibrary || WriteLastPlayedToLibrary;
    }

    public class LibrarySyncSummary
    {
        public int TracksChecked { get; set; }
        public int LibraryTracksUpdated { get; set; }
        public int PlayCountsUpdated { get; set; }
        public int RatingsOrStarsPulled { get; set; }
        public int RatingsOrStarsPushed { get; set; }
    }

    public partial class Plugin
    {
        public static LibraryTrackSyncPlan EvaluateLibraryTrackSync(
            bool isVirtual, bool syncStarsAndRatings, bool syncPlayCounts, bool pushLocalRatingsToServer,
            bool localLoved, bool hasLocalRating, int localStars, int localPlayCount, DateTime localLastPlayedUtc,
            bool serverStarred, int serverRating, int serverPlayCount, DateTime serverLastPlayedUtc)
        {
            var plan = new LibraryTrackSyncPlan { FinalLoved = localLoved, FinalStars = localStars };

            if (pushLocalRatingsToServer && localLoved && !serverStarred)
            {
                plan.PushStarToServer = true;
                plan.FinalLoved = true;
            }
            else if (syncStarsAndRatings && localLoved != serverStarred)
            {
                plan.WriteLoveToLibrary = true;
                plan.TargetLove = serverStarred;
                plan.FinalLoved = serverStarred;
            }

            if (pushLocalRatingsToServer && hasLocalRating && localStars > 0 && serverRating == 0)
            {
                plan.PushRatingToServer = true;
                plan.RatingToPush = localStars;
                plan.FinalStars = localStars;
            }
            else if (syncStarsAndRatings && hasLocalRating &&
                     ((serverRating > 0 && localStars != serverRating) || (isVirtual && localStars != serverRating)))
            {
                plan.WriteRatingToLibrary = true;
                plan.TargetRating = serverRating;
                plan.FinalStars = serverRating;
            }

            if (syncPlayCounts && serverPlayCount > localPlayCount)
            {
                plan.WritePlayCountToLibrary = true;
                plan.TargetPlayCount = Math.Min(serverPlayCount, ushort.MaxValue);
            }

            if (syncPlayCounts && serverLastPlayedUtc != DateTime.MinValue &&
                (localLastPlayedUtc == DateTime.MinValue || serverLastPlayedUtc > localLastPlayedUtc.AddSeconds(2)))
            {
                plan.WriteLastPlayedToLibrary = true;
                plan.TargetLastPlayedUtc = serverLastPlayedUtc;
            }
            return plan;
        }

        internal LibrarySyncSummary SyncStateToMusicBeeLibraryCore(bool pushLocalRatingsToServer, CancellationToken ct)
        {
            var summary = new LibrarySyncSummary();
            if (Api.Library_SetFileTag == null || Api.Library_CommitTagsToFile == null) return summary;

            var settings = Settings ?? new NavidromeSettings();
            bool syncStars = settings.SyncServerStarsToLibrary;
            bool syncPlays = settings.SyncPlayCountsToLibrary;
            if (!syncStars && !syncPlays && !pushLocalRatingsToServer) return summary;

            var targets = new List<KeyValuePair<string, SubsonicSong>>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (settings.LinkLocalLibraryTracks)
            {
                foreach (var kv in LocalLibraryLinker.Instance.GetAllLinkedLocalFiles())
                {
                    if (!seen.Add(kv.Key)) continue;
                    var song = NavidromeCatalog.Instance.FindSongById(kv.Value);
                    if (song != null) targets.Add(new KeyValuePair<string, SubsonicSong>(kv.Key, song));
                }
            }

            foreach (var url in QueryLibraryVirtualNavidromeFiles())
            {
                if (!string.IsNullOrEmpty(url) && seen.Add(url) && TryResolveSong(url, out var song, allowPlaceholder: false))
                    targets.Add(new KeyValuePair<string, SubsonicSong>(url, song));
            }

            var pendingPushes = new List<Tuple<string, bool, int>>();

            for (int i = 0; i < targets.Count; i++)
            {
                if (ct.IsCancellationRequested) break;
                string url = targets[i].Key;
                SubsonicSong s = targets[i].Value;
                summary.TracksChecked++;

                string loveRaw = Api.Library_GetFileTag != null ? Api.Library_GetFileTag(url, MetaDataType.RatingLove) : "";
                string ratingRaw = Api.Library_GetFileTag != null ? Api.Library_GetFileTag(url, MetaDataType.Rating) : "";
                bool localLoved = ParseLoveTag(loveRaw);
                bool hasLocalRating = TryParseStarRating(ratingRaw, out int localStars);

                var baseline = ReadPlayBaseline(url);
                int localPlays = baseline.HasPlayCount ? baseline.PlayCount : 0;
                DateTime localLastPlayedUtc = Api.Library_GetFileProperty != null
                    ? ParseLibraryLastPlayedUtc(Api.Library_GetFileProperty(url, FilePropertyType.LastPlayed))
                    : DateTime.MinValue;
                NavidromeCatalog.TryParseUtcTimestamp(s.Played, out DateTime serverLastPlayedUtc);

                var plan = EvaluateLibraryTrackSync(
                    LocalLibraryLinker.IsVirtualNavidromeUrl(url), syncStars, syncPlays, pushLocalRatingsToServer,
                    localLoved, hasLocalRating, localStars, localPlays, localLastPlayedUtc,
                    s.Starred, s.UserRating, s.PlayCount, serverLastPlayedUtc);

                if (plan.PushStarToServer || plan.PushRatingToServer)
                    pendingPushes.Add(Tuple.Create(s.Id, plan.PushStarToServer, plan.PushRatingToServer ? plan.RatingToPush : 0));

                _lastKnownTags[s.Id] = new TrackTagSnapshot { IsLoved = plan.FinalLoved, Stars = plan.FinalStars };
                if (plan.HasLibraryWrites && !LocalLibraryLinker.IsCueSubTrackUrl(url))
                {
                    Interlocked.Increment(ref _suppressTagChangeNotifications);
                    try
                    {
                        if (plan.WriteLoveToLibrary)
                        {
                            Api.Library_SetFileTag(url, MetaDataType.RatingLove, plan.TargetLove ? "L" : "");
                            summary.RatingsOrStarsPulled++;
                        }
                        if (plan.WriteRatingToLibrary)
                        {
                            Api.Library_SetFileTag(url, MetaDataType.Rating,
                                plan.TargetRating > 0 ? plan.TargetRating.ToString(CultureInfo.InvariantCulture) : "");
                            summary.RatingsOrStarsPulled++;
                        }
                        if (plan.WritePlayCountToLibrary)
                        {
                            baseline.PlayCount = plan.TargetPlayCount;
                            baseline.HasPlayCount = true;
                            _playBaselines[s.Id] = baseline;
                            Api.Library_SetFileTag(url, (MetaDataType)FilePropertyType.PlayCount,
                                plan.TargetPlayCount.ToString(CultureInfo.InvariantCulture));
                            summary.PlayCountsUpdated++;
                        }
                        if (plan.WriteLastPlayedToLibrary)
                        {
                            Api.Library_SetFileTag(url, (MetaDataType)FilePropertyType.LastPlayed,
                                plan.TargetLastPlayedUtc.ToLocalTime().ToString("o", CultureInfo.InvariantCulture));
                        }

                        if (Api.Library_CommitTagsToFile(url))
                            summary.LibraryTracksUpdated++;
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _suppressTagChangeNotifications);
                    }
                }

                if (summary.TracksChecked % 500 == 0 || i == targets.Count - 1)
                    Api.MB_SetBackgroundTaskMessage?.Invoke($"Navidrome: Syncing state ({summary.TracksChecked:N0} / {targets.Count:N0})...");
            }

            if (pendingPushes.Count > 0 && Subsonic != null && !ct.IsCancellationRequested)
            {
                int pushedCount = 0;
                int completed = 0;
                int total = pendingPushes.Count;
                try
                {
                    Parallel.ForEach(
                        pendingPushes,
                        new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
                        item =>
                        {
                            if (item.Item2 && Subsonic.Star(item.Item1, true))
                            {
                                NavidromeCatalog.Instance.UpdateStarred(item.Item1, true, scheduleSave: false);
                                Interlocked.Increment(ref pushedCount);
                            }
                            if (item.Item3 > 0 && Subsonic.SetRating(item.Item1, item.Item3))
                            {
                                NavidromeCatalog.Instance.UpdateRating(item.Item1, item.Item3, scheduleSave: false);
                                Interlocked.Increment(ref pushedCount);
                            }
                            int done = Interlocked.Increment(ref completed);
                            if (done % 25 == 0 || done == total)
                                Api.MB_SetBackgroundTaskMessage?.Invoke($"Navidrome: Pushing local ratings & stars ({done:N0} / {total:N0})...");
                        });
                }
                catch (OperationCanceledException) { }

                summary.RatingsOrStarsPushed += pushedCount;
                if (pushedCount > 0)
                    NavidromeCatalog.Instance.ScheduleSaveToDisk();
            }
            return summary;
        }

        private void SyncStateToMusicBeeLibrary(bool pushLocalRatingsToServer = false)
        {
            var ct = ShutdownToken;
            Task.Run(() =>
            {
                try
                {
                    var res = SyncStateToMusicBeeLibraryCore(pushLocalRatingsToServer, ct);
                    if (!ct.IsCancellationRequested && res.LibraryTracksUpdated > 0)
                        Api.MB_RefreshPanels?.Invoke();
                }
                catch { }
            }, ct);
        }
    }
}
