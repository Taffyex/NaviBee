using System;
using System.Collections.Generic;
using System.Threading;

namespace MusicBeePlugin
{
    public partial class NavidromeCatalog
    {
        public bool SmartSyncIfNeeded(
            SubsonicClient client,
            int artworkSize,
            Action<string> progressCallback,
            out bool catalogChanged,
            CancellationToken ct = default,
            bool startPrewarm = true)
        {
            catalogChanged = false;
            if (client == null || ct.IsCancellationRequested) return false;

            var scanStatus = client.GetScanStatus();
            if (scanStatus != null && !scanStatus.Scanning && HasData)
            {
                lock (_lock)
                {
                    bool sameServer = string.IsNullOrEmpty(_meta.ServerUrl) ||
                                      string.Equals(client.ServerUrlNormalized, _meta.ServerUrl, StringComparison.OrdinalIgnoreCase);
                    bool countComplete = scanStatus.Count > 0 &&
                                         scanStatus.Count == _meta.ServerSongCount &&
                                         _allSongs.Count >= scanStatus.Count;
                    bool scanMatches = !string.IsNullOrEmpty(_meta.LastScan) &&
                                       string.Equals(scanStatus.LastScan, _meta.LastScan, StringComparison.OrdinalIgnoreCase);

                    if (sameServer && countComplete && scanMatches)
                    {
                        progressCallback?.Invoke($"Navidrome: Instant cache hit ({_allSongs.Count:N0} tracks ready)");
                        if (startPrewarm)
                            ArtworkCache.Instance.StartBackgroundPrewarm(GetDistinctAlbumCoverIds(), client, progressCallback, artworkSize);
                        return true;
                    }
                }
            }

            catalogChanged = true;
            return SyncFromServer(client, progressCallback, scanStatus, artworkSize, ct, startPrewarm);
        }

        public bool SyncFromServer(
            SubsonicClient client,
            Action<string> progressCallback,
            SubsonicScanStatus scanStatus = null,
            int artworkSize = 800,
            CancellationToken ct = default,
            bool startPrewarm = true)
        {
            if (client == null || ct.IsCancellationRequested) return false;
            progressCallback?.Invoke("Navidrome: Syncing library catalog...");

            if (scanStatus == null)
                scanStatus = client.GetScanStatus();
            if (ct.IsCancellationRequested) return false;

            int expectedCount = scanStatus != null && scanStatus.Count > 0 ? scanStatus.Count : 0;
            var byId = new Dictionary<string, SubsonicSong>(StringComparer.OrdinalIgnoreCase);
            var ordered = new List<SubsonicSong>();

            bool searchError;
            FetchSongsViaSearch3(client, "", byId, ordered, progressCallback, expectedCount, ct, out searchError);
            if (ct.IsCancellationRequested) return false;

            // Fallback for older Subsonic/Airsonic servers where empty search3?query= returns 0 songs
            if (ordered.Count == 0)
            {
                bool quotedError;
                FetchSongsViaSearch3(client, "\"\"", byId, ordered, progressCallback, expectedCount, ct, out quotedError);
                searchError = searchError || quotedError;
                if (ct.IsCancellationRequested) return false;
            }

            bool albumError = false;
            bool needsAlbumSupplement = ordered.Count == 0 ||
                                        (expectedCount > 0 && ordered.Count < (int)Math.Ceiling(expectedCount * 0.95));
            if (needsAlbumSupplement && !ct.IsCancellationRequested)
            {
                SupplementViaAlbumList(client, byId, ordered, progressCallback, expectedCount, ct, out albumError);
            }

            if (ordered.Count == 0 || ct.IsCancellationRequested)
                return false;

            if (scanStatus == null)
                scanStatus = client.GetScanStatus();

            bool anyTransportError = searchError || albumError;
            int existingSameServerCount = 0;
            string prevUserStateSyncUtc = "";
            lock (_lock)
            {
                bool sameServer = !string.IsNullOrEmpty(_meta.ServerUrl) &&
                                  string.Equals(client.ServerUrlNormalized, _meta.ServerUrl, StringComparison.OrdinalIgnoreCase);
                if (sameServer)
                {
                    existingSameServerCount = _allSongs.Count;
                    prevUserStateSyncUtc = _meta.LastUserStateSyncUtc ?? "";
                }
            }

            // Never clobber a healthier loaded catalog on disk with a transport-truncated sync.
            if (anyTransportError && existingSameServerCount > 0 && ordered.Count < (int)(existingSameServerCount * 0.90))
            {
                progressCallback?.Invoke($"Navidrome: Sync interrupted at {ordered.Count:N0} tracks; keeping cached {existingSameServerCount:N0} tracks.");
                return false;
            }

            lock (_lock)
            {
                BuildIndexes(ordered);
                _meta = new CatalogMetadata
                {
                    LastScan = (scanStatus != null && !scanStatus.Scanning) ? (scanStatus.LastScan ?? "") : "",
                    ServerSongCount = scanStatus != null && scanStatus.Count > 0 ? scanStatus.Count : ordered.Count,
                    ServerUrl = client.ServerUrlNormalized,
                    ArtworkSize = artworkSize,
                    LastUserStateSyncUtc = prevUserStateSyncUtc
                };
            }
            SaveToDisk();

            string readyMsg = expectedCount > 0
                ? $"Navidrome: {ordered.Count:N0} / {expectedCount:N0} tracks ready"
                : $"Navidrome: {ordered.Count:N0} tracks ready";
            progressCallback?.Invoke(readyMsg);
            if (startPrewarm)
                ArtworkCache.Instance.StartBackgroundPrewarm(GetDistinctAlbumCoverIds(), client, progressCallback, artworkSize);
            return true;
        }

        private static void FetchSongsViaSearch3(
            SubsonicClient client,
            string query,
            Dictionary<string, SubsonicSong> byId,
            List<SubsonicSong> ordered,
            Action<string> progressCallback,
            int expectedCount,
            CancellationToken ct,
            out bool hadError)
        {
            hadError = false;
            int offset = 0;
            const int pageSize = 500;
            int consecutiveZeroNew = 0;

            while (!ct.IsCancellationRequested)
            {
                List<SubsonicSong> page = null;
                bool ok = false;
                for (int attempt = 0; attempt < 3 && !ct.IsCancellationRequested; attempt++)
                {
                    if (attempt > 0 && !WaitBackoff(attempt * 1000, ct))
                        return;
                    if (client.TrySearchSongs(pageSize, offset, query, out page, out _, 60000))
                    {
                        ok = true;
                        break;
                    }
                }

                if (!ok)
                {
                    hadError = true;
                    break;
                }

                if (page == null || page.Count == 0)
                    break;

                int addedThisPage = MergeSongs(page, byId, ordered);
                ReportSyncProgress(progressCallback, ordered.Count, expectedCount);

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
        }

        private static void SupplementViaAlbumList(
            SubsonicClient client,
            Dictionary<string, SubsonicSong> byId,
            List<SubsonicSong> ordered,
            Action<string> progressCallback,
            int expectedCount,
            CancellationToken ct,
            out bool hadError)
        {
            hadError = false;
            int albOffset = 0;
            const int albPage = 500;
            var seenAlbums = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            while (!ct.IsCancellationRequested)
            {
                List<SubsonicAlbum> albums = null;
                bool ok = false;
                for (int attempt = 0; attempt < 3 && !ct.IsCancellationRequested; attempt++)
                {
                    if (attempt > 0 && !WaitBackoff(attempt * 1000, ct))
                        return;
                    if (client.TryGetAlbumList2("alphabeticalByName", albPage, albOffset, out albums, out _, 60000))
                    {
                        ok = true;
                        break;
                    }
                }

                if (!ok)
                {
                    hadError = true;
                    break;
                }

                if (albums == null || albums.Count == 0)
                    break;

                int newAlbumsThisPage = 0;
                foreach (var alb in albums)
                {
                    if (ct.IsCancellationRequested) return;
                    if (alb == null || string.IsNullOrEmpty(alb.Id) || !seenAlbums.Add(alb.Id))
                        continue;

                    newAlbumsThisPage++;
                    List<SubsonicSong> albSongs = null;
                    bool albOk = false;
                    for (int attempt = 0; attempt < 2 && !ct.IsCancellationRequested; attempt++)
                    {
                        if (attempt > 0 && !WaitBackoff(500, ct))
                            return;
                        if (client.TryGetAlbum(alb.Id, out albSongs, out _, 30000))
                        {
                            albOk = true;
                            break;
                        }
                    }

                    if (!albOk)
                    {
                        hadError = true;
                        continue;
                    }

                    if (albSongs != null && albSongs.Count > 0)
                        MergeSongs(albSongs, byId, ordered);
                }

                ReportSyncProgress(progressCallback, ordered.Count, expectedCount);
                if (newAlbumsThisPage == 0)
                    break;

                albOffset += albums.Count;
            }
        }

        private static int MergeSongs(
            List<SubsonicSong> page,
            Dictionary<string, SubsonicSong> byId,
            List<SubsonicSong> ordered)
        {
            int added = 0;
            for (int i = 0; i < page.Count; i++)
            {
                var song = page[i];
                if (song == null) continue;
                if (string.IsNullOrEmpty(song.Id))
                {
                    ordered.Add(song);
                    added++;
                }
                else if (!byId.ContainsKey(song.Id))
                {
                    byId[song.Id] = song;
                    ordered.Add(song);
                    added++;
                }
            }
            return added;
        }

        private static void ReportSyncProgress(Action<string> progressCallback, int count, int expected)
        {
            if (progressCallback == null) return;
            string msg = expected > 0
                ? $"Navidrome: Synced {count:N0} / {expected:N0} tracks..."
                : $"Navidrome: Synced {count:N0} tracks...";
            progressCallback(msg);
        }

        private static bool WaitBackoff(int delayMs, CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return false;
            try
            {
                return !ct.WaitHandle.WaitOne(delayMs);
            }
            catch
            {
                return !ct.IsCancellationRequested;
            }
        }
    }
}
