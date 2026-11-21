using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MusicBeePlugin
{
    public partial class Plugin
    {
        private static readonly object _nowPlayingLock = new object();
        private static string _lastNowPlayingUrl = "";

        private static bool TryResolveSong(string sourceFileUrl, out SubsonicSong song)
        {
            song = null;
            if (string.IsNullOrEmpty(sourceFileUrl)) return false;
            string clean = CleanPath(sourceFileUrl);

            song = NavidromeCatalog.Instance.FindSongByPath(clean)
                ?? NavidromeCatalog.Instance.FindSongById(clean);
            if (song != null) return true;

            lock (CacheLock)
            {
                if (SongByPath.TryGetValue(clean, out song) || SongById.TryGetValue(clean, out song))
                    return true;
            }
            if (sourceFileUrl.StartsWith("Navidrome:", StringComparison.OrdinalIgnoreCase) ||
                sourceFileUrl.StartsWith("subsonic://", StringComparison.OrdinalIgnoreCase))
            {
                string id = Api.Library_GetFileTag != null ? Api.Library_GetFileTag(sourceFileUrl, MetaDataType.Custom16) : null;
                if (!string.IsNullOrEmpty(id))
                {
                    song = NavidromeCatalog.Instance.FindSongById(id);
                    if (song != null) return true;
                    lock (CacheLock) { if (SongById.TryGetValue(id, out song)) return true; }
                    song = new SubsonicSong { Id = id };
                    return true;
                }
            }
            return false;
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

            lock (_nowPlayingLock)
            {
                if (isPlayStateEvent && string.Equals(_lastNowPlayingUrl, sourceFileUrl, StringComparison.OrdinalIgnoreCase))
                    return;
                _lastNowPlayingUrl = sourceFileUrl ?? "";
            }

            int targetSize = Settings != null ? Settings.ArtworkSize : 800;
            Task.Run(() =>
            {
                Subsonic.Scrobble(song.Id, submission: false);

                if (MusicBeeArtworkBridge.IsAvailable)
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
            });

            if (Settings != null && Settings.PrefetchNextTrack && Api.NowPlayingList_GetCurrentIndex != null && Api.NowPlayingList_GetListFileUrl != null)
            {
                int curIdx = Api.NowPlayingList_GetCurrentIndex();
                if (curIdx >= 0)
                {
                    string nextUrl = Api.NowPlayingList_GetListFileUrl(curIdx + 1);
                    if (!string.IsNullOrEmpty(nextUrl) && TryResolveSong(nextUrl, out var nextSong))
                        AudioStreamCache.Instance.PrefetchNextTrack(nextSong, Subsonic, Settings);
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

        private void SyncStarsToMusicBeeLibrary()
        {
            if (Api.Library_SetFileTag == null || Api.Library_CommitTagsToFile == null || Api.Library_QueryFilesEx == null)
                return;

            Task.Run(() =>
            {
                try
                {
                    if (!Api.Library_QueryFilesEx("Navidrome:", out var libFiles) || libFiles == null || libFiles.Length == 0)
                        return;

                    foreach (var url in libFiles)
                    {
                        if (!TryResolveSong(url, out var s)) continue;

                        bool dirty = false;
                        string currentLove = Api.Library_GetFileTag != null ? Api.Library_GetFileTag(url, MetaDataType.RatingLove) : "";
                        bool isLovedInLibrary = string.Equals(currentLove, "L", StringComparison.OrdinalIgnoreCase);
                        if (isLovedInLibrary != s.Starred)
                        {
                            Api.Library_SetFileTag(url, MetaDataType.RatingLove, s.Starred ? "L" : "");
                            dirty = true;
                        }

                        string currentRating = Api.Library_GetFileTag != null ? Api.Library_GetFileTag(url, MetaDataType.Rating) : "";
                        if (TryParseStarRating(currentRating, out int libStars) && libStars != s.UserRating)
                        {
                            Api.Library_SetFileTag(url, MetaDataType.Rating, s.UserRating > 0 ? s.UserRating.ToString(CultureInfo.InvariantCulture) : "");
                            dirty = true;
                        }

                        if (dirty)
                            Api.Library_CommitTagsToFile(url);
                    }
                }
                catch { }
            });
        }

        private void OnPlayCountersChanged(string sourceFileUrl)
        {
            if (!TryResolveSong(sourceFileUrl, out var song)) return;

            Task.Run(() =>
            {
                Subsonic.Scrobble(song.Id, submission: true);
                Api.MB_SetBackgroundTaskMessage?.Invoke($"Navidrome: scrobbled track {song.Id}");
            });
        }

        private void OnTagsChanged(string sourceFileUrl)
        {
            if (!TryResolveSong(sourceFileUrl, out var song)) return;

            string love = Api.Library_GetFileTag != null ? Api.Library_GetFileTag(sourceFileUrl, MetaDataType.RatingLove) : "";
            string ratingStr = Api.Library_GetFileTag != null ? Api.Library_GetFileTag(sourceFileUrl, MetaDataType.Rating) : "";

            bool isLoved = string.Equals(love, "L", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(love, "1", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(love, "true", StringComparison.OrdinalIgnoreCase);

            if (song.Starred != isLoved)
            {
                NavidromeCatalog.Instance.UpdateStarred(song.Id, isLoved);
                Task.Run(() => Subsonic.Star(song.Id, isLoved));
            }

            if (TryParseStarRating(ratingStr, out int stars) && song.UserRating != stars)
            {
                NavidromeCatalog.Instance.UpdateRating(song.Id, stars);
                Task.Run(() => Subsonic.SetRating(song.Id, stars));
            }
        }

        private void OnPlaylistNotification(string sourceFileUrl, NotificationType type)
        {
            if (string.IsNullOrEmpty(sourceFileUrl) || Subsonic == null)
                return;

            string plName = Path.GetFileNameWithoutExtension(sourceFileUrl);
            if (string.IsNullOrEmpty(plName)) return;

            Task.Run(() =>
            {
                try
                {
                    if (type == NotificationType.PlaylistDeleted)
                    {
                        string plId = null;
                        lock (CacheLock) { PlaylistLookup.TryGetValue(plName, out plId); }
                        if (!string.IsNullOrEmpty(plId))
                        {
                            Subsonic.DeletePlaylist(plId);
                            lock (CacheLock) { PlaylistLookup.Remove(plName); }
                        }
                        return;
                    }

                    if (Api.Playlist_QueryFilesEx != null && Api.Playlist_QueryFilesEx(sourceFileUrl, out var filenames))
                    {
                        var songIds = new List<string>();
                        if (filenames != null)
                        {
                            foreach (var fn in filenames)
                            {
                                if (TryResolveSong(fn, out var song))
                                    songIds.Add(song.Id);
                            }
                        }

                        string existingId = null;
                        lock (CacheLock) { PlaylistLookup.TryGetValue(plName, out existingId); }

                        if (songIds.Count > 0 || !string.IsNullOrEmpty(existingId))
                        {
                            if (type == NotificationType.PlaylistMoved && string.IsNullOrEmpty(existingId))
                                return;

                            string returnedId = Subsonic.CreatePlaylist(plName, songIds, existingId);
                            if (!string.IsNullOrEmpty(returnedId))
                            {
                                lock (CacheLock) { PlaylistLookup[plName] = returnedId; }
                                Api.MB_SetBackgroundTaskMessage?.Invoke($"Navidrome: synced playlist '{plName}' ({songIds.Count} tracks)");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Api.MB_SetBackgroundTaskMessage?.Invoke($"Playlist sync error: {ex.Message}");
                }
            });
        }
    }
}
