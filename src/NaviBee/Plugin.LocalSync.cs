using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MusicBeePlugin
{
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
        private static int _isLocalSyncActive;

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

        internal static int ParseLeadingInt(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return 0;
            string s = raw.Trim();
            int slash = s.IndexOf('/');
            if (slash > 0) s = s.Substring(0, slash).Trim();
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int val) ? val : 0;
        }

        internal static int ParseDurationSeconds(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return 0;
            string s = raw.Trim();
            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long num))
            {
                if (num <= 0) return 0;
                if (num >= 10_000_000L) return (int)(num / 10_000_000L);
                if (num >= 10_000L) return (int)(num / 1000L);
                return (int)num;
            }

            string[] parts = s.Split(':');
            if (parts.Length == 2 &&
                int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int m2) &&
                double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out double s2))
                return Math.Max(0, m2 * 60 + (int)Math.Round(s2));
            if (parts.Length == 3 &&
                int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int h3) &&
                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int m3) &&
                double.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out double s3))
                return Math.Max(0, h3 * 3600 + m3 * 60 + (int)Math.Round(s3));
            return 0;
        }

        internal static DateTime ParseLibraryLastPlayedUtc(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return DateTime.MinValue;
            string s = raw.Trim();
            if (s == "0") return DateTime.MinValue;
            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks) && ticks > 0)
            {
                try { return new DateTime(ticks, DateTimeKind.Local).ToUniversalTime(); }
                catch { return DateTime.MinValue; }
            }
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var inv))
                return inv.ToUniversalTime();
            if (DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var loc))
                return loc.ToUniversalTime();
            return DateTime.MinValue;
        }

        internal static LocalTrackInfo ReadLocalTrackInfo(string localUrl, MetaDataType pidField)
        {
            var info = new LocalTrackInfo { Url = localUrl };
            if (string.IsNullOrEmpty(localUrl)) return info;
            try
            {
                if (Api.Library_GetFileTags != null)
                {
                    var fields = new[] { pidField, MetaDataType.Artist, MetaDataType.AlbumArtist, MetaDataType.Album, MetaDataType.TrackTitle, MetaDataType.DiscNo, MetaDataType.TrackNo };
                    if (Api.Library_GetFileTags(localUrl, fields, out var values) && values != null && values.Length == 7)
                    {
                        info.PidTag = values[0] ?? ""; info.Artist = values[1] ?? ""; info.AlbumArtist = values[2] ?? "";
                        info.Album = values[3] ?? ""; info.Title = values[4] ?? "";
                        info.DiscNo = ParseLeadingInt(values[5]); info.TrackNo = ParseLeadingInt(values[6]);
                    }
                }
                else if (Api.Library_GetFileTag != null)
                {
                    info.PidTag = Api.Library_GetFileTag(localUrl, pidField) ?? "";
                    info.Artist = Api.Library_GetFileTag(localUrl, MetaDataType.Artist) ?? "";
                    info.AlbumArtist = Api.Library_GetFileTag(localUrl, MetaDataType.AlbumArtist) ?? "";
                    info.Album = Api.Library_GetFileTag(localUrl, MetaDataType.Album) ?? "";
                    info.Title = Api.Library_GetFileTag(localUrl, MetaDataType.TrackTitle) ?? "";
                    info.DiscNo = ParseLeadingInt(Api.Library_GetFileTag(localUrl, MetaDataType.DiscNo));
                    info.TrackNo = ParseLeadingInt(Api.Library_GetFileTag(localUrl, MetaDataType.TrackNo));
                }
                if (Api.Library_GetFileProperty != null)
                    info.DurationSeconds = ParseDurationSeconds(Api.Library_GetFileProperty(localUrl, FilePropertyType.Duration));
            }
            catch { }
            return info;
        }

        internal LocalLinkSummary LinkLocalLibraryInternal(bool allowEmbedPid, CancellationToken ct)
        {
            var empty = new LocalLinkSummary();
            if (Api.Library_QueryFilesEx == null || !NavidromeCatalog.Instance.HasData) return empty;
            if (!Api.Library_QueryFilesEx("domain=Library", out var files) || files == null || files.Length == 0) return empty;

            var settings = Settings ?? new NavidromeSettings();
            MetaDataType pidField = settings.GetPidMetaDataType();
            var localInfos = new List<LocalTrackInfo>(files.Length);

            for (int i = 0; i < files.Length; i++)
            {
                if (ct.IsCancellationRequested) return empty;
                string f = files[i];
                if (!string.IsNullOrWhiteSpace(f) && !LocalLibraryLinker.IsVirtualNavidromeUrl(f))
                    localInfos.Add(ReadLocalTrackInfo(f, pidField));
            }

            Action<string, MetaDataType, string> writePid = null;
            if (allowEmbedPid && Api.Library_SetFileTag != null && Api.Library_CommitTagsToFile != null)
            {
                writePid = (url, field, songId) =>
                {
                    Api.Library_SetFileTag(url, field, songId);
                    Api.Library_CommitTagsToFile(url);
                };
            }

            return LocalLibraryLinker.Instance.LinkLocalTracks(
                localInfos, NavidromeCatalog.Instance, settings, writePid, pruneMissingLocalPaths: true, ct: ct);
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

            if (Api.Library_QueryFilesEx != null && Api.Library_QueryFilesEx("Navidrome:", out var navFiles) && navFiles != null)
            {
                foreach (var url in navFiles)
                {
                    if (!string.IsNullOrEmpty(url) && seen.Add(url) && TryResolveSong(url, out var song, allowPlaceholder: false))
                        targets.Add(new KeyValuePair<string, SubsonicSong>(url, song));
                }
            }

            foreach (var item in targets)
            {
                if (ct.IsCancellationRequested) break;
                string url = item.Key;
                SubsonicSong s = item.Value;
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

                if (plan.PushStarToServer && Subsonic != null && Subsonic.Star(s.Id, true))
                {
                    NavidromeCatalog.Instance.UpdateStarred(s.Id, true);
                    summary.RatingsOrStarsPushed++;
                }
                if (plan.PushRatingToServer && Subsonic != null && Subsonic.SetRating(s.Id, plan.RatingToPush))
                {
                    NavidromeCatalog.Instance.UpdateRating(s.Id, plan.RatingToPush);
                    summary.RatingsOrStarsPushed++;
                }

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

                _lastKnownTags[s.Id] = new TrackTagSnapshot { IsLoved = plan.FinalLoved, Stars = plan.FinalStars };
                if (plan.HasLibraryWrites) { Api.Library_CommitTagsToFile(url); summary.LibraryTracksUpdated++; }
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

        public void LinkAndSyncLocalLibrary(bool showDialog = true, bool pushLocalRatingsOnLink = true, Action<string> statusCallback = null, Action onComplete = null)
        {
            if (Interlocked.CompareExchange(ref _isLocalSyncActive, 1, 0) != 0)
            {
                if (showDialog) ShowUiMessage("Local library linking and sync is already in progress.", "Navidrome Local Link", MessageBoxIcon.Information);
                onComplete?.Invoke();
                return;
            }

            var ct = ShutdownToken;
            Task.Run(() =>
            {
                try
                {
                    void Report(string msg) { statusCallback?.Invoke(msg); Api.MB_SetBackgroundTaskMessage?.Invoke(msg); }

                    if (!NavidromeCatalog.Instance.HasData)
                    {
                        string msg = "Please connect and sync the Navidrome catalog before linking local files.";
                        Report(msg);
                        if (showDialog) ShowUiMessage(msg, "Navidrome Local Link", MessageBoxIcon.Information);
                        return;
                    }

                    Report("Navidrome: Refreshing server ratings & playcounts...");
                    NavidromeCatalog.Instance.RefreshUserState(Subsonic, ct);
                    if (ct.IsCancellationRequested) return;

                    Report("Navidrome: Matching local library tracks to server...");
                    var linkRes = LinkLocalLibraryInternal(allowEmbedPid: Settings?.EmbedNavidromeIdInLocalTag ?? true, ct);
                    if (ct.IsCancellationRequested) return;

                    Report($"Navidrome: Syncing stars, ratings & playcounts across {linkRes.Linked:N0} linked tracks...");
                    var syncRes = SyncStateToMusicBeeLibraryCore(pushLocalRatingsToServer: pushLocalRatingsOnLink, ct);
                    if (ct.IsCancellationRequested) return;

                    Api.MB_RefreshPanels?.Invoke();
                    string summary = $"Navidrome: Linked {linkRes.Linked:N0} of {linkRes.Scanned:N0} local tracks " +
                                     $"(PID: {linkRes.MatchedByPid:N0}, Path: {linkRes.MatchedByPath:N0}, Tags: {linkRes.MatchedByMetadata:N0}; " +
                                     $"embedded {linkRes.PidEmbedded:N0} IDs; updated {syncRes.LibraryTracksUpdated:N0} tracks, " +
                                     $"pulled {syncRes.PlayCountsUpdated:N0} playcounts, pushed {syncRes.RatingsOrStarsPushed:N0} ratings/stars).";
                    Report(summary);
                    if (showDialog) ShowUiMessage(summary, "Navidrome Local Library Link & Sync", MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    string err = $"Navidrome local link error: {FriendlyErrors.FormatException(ex)}";
                    statusCallback?.Invoke(err);
                    Api.MB_SetBackgroundTaskMessage?.Invoke(err);
                }
                finally
                {
                    Interlocked.Exchange(ref _isLocalSyncActive, 0);
                    onComplete?.Invoke();
                }
            }, ct);
        }
    }
}
