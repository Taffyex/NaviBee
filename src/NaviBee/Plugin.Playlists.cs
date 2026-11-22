using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MusicBeePlugin
{
    public interface IPlaylistServerOps
    {
        List<SubsonicPlaylist> GetPlaylists(out Exception error);
        List<SubsonicSong> GetPlaylistWithMeta(string playlistId, out SubsonicPlaylist meta, out Exception error);
        string CreatePlaylist(string name, IEnumerable<string> songIds, string existingPlaylistId, out Exception error);
        bool DeletePlaylist(string playlistId, out Exception error);
    }

    internal sealed class SubsonicPlaylistServerOps : IPlaylistServerOps
    {
        private readonly SubsonicClient _client;
        public SubsonicPlaylistServerOps(SubsonicClient client) => _client = client;

        public List<SubsonicPlaylist> GetPlaylists(out Exception error)
        {
            var list = _client.GetPlaylists();
            error = _client.LastError;
            return list;
        }

        public List<SubsonicSong> GetPlaylistWithMeta(string playlistId, out SubsonicPlaylist meta, out Exception error)
        {
            var list = _client.GetPlaylistWithMeta(playlistId, out meta);
            error = _client.LastError;
            return list;
        }

        public string CreatePlaylist(string name, IEnumerable<string> songIds, string existingPlaylistId, out Exception error)
        {
            string id = _client.CreatePlaylist(name, songIds, existingPlaylistId);
            error = _client.LastError;
            return id;
        }

        public bool DeletePlaylist(string playlistId, out Exception error)
        {
            bool ok = _client.DeletePlaylist(playlistId);
            error = _client.LastError;
            return ok;
        }
    }

    public partial class Plugin
    {
        private static readonly ConcurrentDictionary<string, System.Threading.Timer> _playlistDebounceTimers =
            new ConcurrentDictionary<string, System.Threading.Timer>(StringComparer.OrdinalIgnoreCase);

        internal static void DisposePlaylistDebounceTimers()
        {
            foreach (var key in _playlistDebounceTimers.Keys.ToList())
            {
                if (_playlistDebounceTimers.TryRemove(key, out var t))
                {
                    try { t.Dispose(); } catch { }
                }
            }
        }

        private void OnPlaylistNotification(string sourceFileUrl, NotificationType type)
        {
            if (string.IsNullOrEmpty(sourceFileUrl) || Subsonic == null || Settings == null)
                return;

            if (!Settings.PushPlaylistsToServer)
                return;

            string plName = Path.GetFileNameWithoutExtension(sourceFileUrl);
            if (string.IsNullOrEmpty(plName)) return;

            if (type == NotificationType.PlaylistDeleted)
            {
                CancelPendingPlaylistPush(sourceFileUrl);
                var removed = PlaylistLinkStore.Instance.RemoveByPath(sourceFileUrl);
                if (removed != null)
                {
                    Api.MB_SetBackgroundTaskMessage?.Invoke(
                        $"Navidrome: unlinked playlist '{plName}' (server copy '{removed.ServerName}' kept).");
                }
                return;
            }

            if (type == NotificationType.PlaylistMoved)
            {
                PlaylistLinkStore.Instance.TryRelinkMovedPlaylist(sourceFileUrl);
                return;
            }

            // Debounce PlaylistCreated / PlaylistUpdated bursts (e.g. drag-reorders or multi-track adds)
            ScheduleDebouncedPlaylistPush(sourceFileUrl, plName);
        }

        private static void CancelPendingPlaylistPush(string localPath)
        {
            if (_playlistDebounceTimers.TryRemove(localPath, out var existing))
            {
                try { existing.Dispose(); } catch { }
            }
        }

        private void ScheduleDebouncedPlaylistPush(string localPath, string plName)
        {
            CancellationToken ct = ShutdownToken;
            var timer = new System.Threading.Timer(_ =>
            {
                if (_playlistDebounceTimers.TryRemove(localPath, out var self))
                {
                    try { self.Dispose(); } catch { }
                }
                if (ct.IsCancellationRequested || Settings == null || !Settings.PushPlaylistsToServer)
                    return;

                Task.Run(() => PushLocalPlaylistSafe(localPath, plName, ct), ct);
            }, null, 1500, Timeout.Infinite);

            _playlistDebounceTimers.AddOrUpdate(localPath, timer, (_, old) =>
            {
                try { old.Dispose(); } catch { }
                return timer;
            });
        }

        private void PushLocalPlaylistSafe(string localPath, string plName, CancellationToken ct)
        {
            if (ct.IsCancellationRequested || Api.Playlist_QueryFilesEx == null)
                return;

            try
            {
                if (!Api.Playlist_QueryFilesEx(localPath, out var filenames))
                    return;

                var songIds = new List<string>();
                if (filenames != null)
                {
                    foreach (var fn in filenames)
                    {
                        if (TryResolveSong(fn, out var song, allowPlaceholder: true))
                            songIds.Add(song.Id);
                    }
                }

                string status = ExecuteSafePlaylistPush(
                    localPath,
                    plName,
                    songIds,
                    Settings,
                    new SubsonicPlaylistServerOps(Subsonic),
                    PlaylistLinkStore.Instance,
                    PlaylistBackup.Instance,
                    (name, id) =>
                    {
                        lock (CacheLock) { PlaylistLookup[name] = id; }
                    });

                if (!string.IsNullOrEmpty(status))
                    Api.MB_SetBackgroundTaskMessage?.Invoke(status);
            }
            catch (Exception ex)
            {
                Api.MB_SetBackgroundTaskMessage?.Invoke($"Navidrome playlist sync error: {FriendlyErrors.FormatException(ex)}");
            }
        }

        public static string ExecuteSafePlaylistPush(
            string localPath,
            string plName,
            IReadOnlyList<string> songIds,
            NavidromeSettings settings,
            IPlaylistServerOps server,
            PlaylistLinkStore linkStore,
            PlaylistBackup backupService,
            Action<string, string> onServerPlaylistMapped = null)
        {
            if (settings == null || !settings.PushPlaylistsToServer)
                return null;
            if (server == null || linkStore == null || backupService == null)
                return null;

            // Safety invariant 1: never send an empty track list to the server.
            if (songIds == null || songIds.Count == 0)
                return $"Navidrome: skipped playlist '{plName}' (no Navidrome tracks).";

            var serverPlaylists = server.GetPlaylists(out var listErr);
            if (listErr != null || serverPlaylists == null)
                return $"Navidrome: skipped playlist '{plName}' ({FriendlyErrors.FormatException(listErr)}).";

            var link = linkStore.GetByPath(localPath);
            if (link == null)
            {
                // Safety invariant 2: never overwrite an unlinked server playlist with the same name.
                string targetName = ChooseNonCollidingPlaylistName(plName, serverPlaylists);
                string newId = server.CreatePlaylist(targetName, songIds, null, out var createErr);
                if (createErr != null || string.IsNullOrEmpty(newId))
                    return $"Navidrome: failed to create playlist '{targetName}' ({FriendlyErrors.FormatException(createErr)}).";

                linkStore.Upsert(localPath, newId, targetName, createdByNaviBee: true);
                onServerPlaylistMapped?.Invoke(targetName, newId);
                return $"Navidrome: created playlist '{targetName}' ({songIds.Count} tracks).";
            }

            var serverPl = serverPlaylists.FirstOrDefault(p => string.Equals(p.Id, link.ServerId, StringComparison.OrdinalIgnoreCase));
            if (serverPl == null)
            {
                linkStore.RemoveByPath(localPath);
                return $"Navidrome: server playlist '{link.ServerName}' no longer exists; unlinked.";
            }

            // Safety invariant 3: never modify a playlist owned by another user.
            if (!string.IsNullOrEmpty(serverPl.Owner) &&
                !string.IsNullOrEmpty(settings.Username) &&
                !string.Equals(serverPl.Owner, settings.Username, StringComparison.OrdinalIgnoreCase))
            {
                return $"Navidrome: skipped playlist '{serverPl.Name}' (owned by '{serverPl.Owner}').";
            }

            // Safety invariant 4: always save a verified backup before overwriting.
            var existingSongs = server.GetPlaylistWithMeta(serverPl.Id, out var freshMeta, out var fetchErr);
            if (fetchErr != null || existingSongs == null)
                return $"Navidrome: skipped playlist '{serverPl.Name}' (could not read server copy for backup).";

            if (!backupService.SaveBackup(freshMeta ?? serverPl, existingSongs))
                return $"Navidrome: skipped playlist '{serverPl.Name}' (backup failed; push aborted).";

            string updatedId = server.CreatePlaylist(serverPl.Name, songIds, serverPl.Id, out var updateErr);
            if (updateErr != null || string.IsNullOrEmpty(updatedId))
                return $"Navidrome: failed to update playlist '{serverPl.Name}' ({FriendlyErrors.FormatException(updateErr)}).";

            linkStore.Upsert(localPath, updatedId, serverPl.Name, link.CreatedByNaviBee);
            onServerPlaylistMapped?.Invoke(serverPl.Name, updatedId);
            return $"Navidrome: synced playlist '{serverPl.Name}' ({songIds.Count} tracks).";
        }

        private static string ChooseNonCollidingPlaylistName(string baseName, IReadOnlyList<SubsonicPlaylist> existing)
        {
            bool NameExists(string candidate) =>
                existing.Any(p => string.Equals(p.Name, candidate, StringComparison.OrdinalIgnoreCase));

            if (!NameExists(baseName))
                return baseName;

            string suffixed = $"{baseName} (MusicBee)";
            if (!NameExists(suffixed))
                return suffixed;

            for (int i = 2; i < 100; i++)
            {
                string numbered = $"{baseName} (MusicBee {i})";
                if (!NameExists(numbered))
                    return numbered;
            }

            return $"{baseName} (MusicBee {DateTime.UtcNow:yyyyMMddHHmmss})";
        }

        internal bool ConfirmAndDeleteServerPlaylist(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || Subsonic == null)
                return false;

            var songs = Subsonic.GetPlaylistWithMeta(id, out var meta);
            string name = !string.IsNullOrWhiteSpace(meta?.Name) ? meta.Name : id;
            int count = songs?.Count ?? meta?.SongCount ?? 0;

            string prompt = $"Delete server playlist \"{name}\" ({count} tracks) from Navidrome?\n\nA JSON backup will be saved before deleting.";
            if (!AskUiConfirmation(prompt, "Delete Navidrome Playlist"))
                return false;

            var backupMeta = meta ?? new SubsonicPlaylist { Id = id, Name = name, SongCount = count };
            if (!PlaylistBackup.Instance.SaveBackup(backupMeta, songs ?? new List<SubsonicSong>()))
            {
                ShowUiMessage("Could not save a backup of the playlist. Deletion was cancelled.", "Navidrome Playlist", MessageBoxIcon.Warning);
                return false;
            }

            bool ok = Subsonic.DeletePlaylist(id);
            if (ok)
            {
                PlaylistLinkStore.Instance.RemoveByServerId(id);
                lock (CacheLock)
                {
                    var keys = PlaylistLookup.Where(kv => string.Equals(kv.Value, id, StringComparison.OrdinalIgnoreCase))
                        .Select(kv => kv.Key)
                        .ToList();
                    foreach (var k in keys) PlaylistLookup.Remove(k);
                }
                Api.MB_SetBackgroundTaskMessage?.Invoke($"Navidrome: deleted server playlist '{name}' (backup saved).");
            }
            return ok;
        }
    }
}
