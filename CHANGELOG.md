# Changelog

## v1.0.2
- **Fixed startup "File not found"**: registers the `Navidrome:\` storage device synchronously on `PluginStartup` whenever a cached catalog is present, and falls back to offline cache playback if the server is unreachable instead of unmounting the provider.
- **Safe, opt-in playlist sync**:
  - Pushing MusicBee playlist changes to Navidrome is now **opt-in (off by default)** via Settings.
  - Deleting a playlist in MusicBee **never deletes** the server playlist (it only unlinks the local copy).
  - Never sends empty track lists to the server and never overwrites unlinked same-name server playlists (creates a `<name> (MusicBee)` copy instead).
  - Skips server playlists owned by other Navidrome users, coalesces rapid edits with a 1.5 s debounce, and saves verified JSON backups under `NavidromeCache\PlaylistBackups\` (keeping the last 20 per playlist) before every update — aborting the push if backup fails.
  - Explicit server playlist deletion from the Navidrome node now asks for confirmation and saves a backup first.
- **Fixed tag/rating/star & scrobble sync bugs**:
  - Editing track tags (e.g. Title) can no longer wipe server stars or ratings with blank MusicBee fields.
  - Skipped tracks are no longer scrobbled as plays (filters MusicBee's `PlayCountersChanged` skip events).
  - Startup cache hits now refresh server stars, ratings, and recent play counts (`getStarred2` + recent/highest albums) so changes from mobile/web clients appear automatically.
  - Star/rating updates refresh `All Albums` immediately and coalesce `catalog.json` writes on a 3-second debounce timer.
- **Transcoded stream seeking**: appends `estimateContentLength=true` to transcoded `stream` URLs so MusicBee can seek inside transcoded tracks.
- **Richer track metadata**: exposes `PlayCount`, `LastPlayed`, `DateAdded`, `SampleRate`, `Channels`, and `Comment` from Subsonic/OpenSubsonic.
- **Settings & UX polish**:
  - Auto-normalizes server URLs (adds `https://` or `http://`, strips trailing `/rest`), adds a **Show password** toggle, displays server type/version and track count on **Test Connection**, maps Subsonic/HTTP errors to plain messages, asks confirmation before **Clear**, formats cache sizes in GB, shows playlist backup counts with an **Open backups folder** link, centers on MusicBee, and shows `"Artist – Title"` in scrobble notifications.
  - Cancels background work cleanly on MusicBee shutdown and removes `NavidromeCache\` on uninstall.

## v1.0.1
- Added OpenSubsonic `getLyricsBySongId` and legacy `getLyrics` support with time-synced `[MM:SS.ff]` LRC formatting and plain-text fallback.
- Added persistent local lyrics caching (`NavidromeCache/Lyrics/*.lrc` & `*.txt`), next-track lyrics prefetching, and stats/clearing in Settings.
- Added safe in-memory lyrics injection into MusicBee's track cache and active Lyrics panel without triggering `FolderFile` disk tag-write errors.
- Added automated GitHub Release workflow so release ZIP and DLL download links resolve cleanly.

## v1.0.0
- Initial public release of NaviBee (`mb_NaviBee.dll`).
- Native MusicBee `PluginType.Storage` provider for Navidrome and Subsonic / OpenSubsonic servers.
- Persistent `catalog.json` index with `getScanStatus` delta checks and paginated `search3` sync.
- HTTP `Range`-seekable `ConnectStream` with single-pass stream-to-cache capture, transcoding bitrate cache keys, LRU disk quota enforcement, and next-track queue prefetching.
- Multi-threaded album artwork pre-warming and disk cache (`Original`, `1200`, `800`, `300 px`).
- Two-way sync: Now Playing, scrobbles, track stars (`star` / `unstar`), 1-5 star ratings (`setRating`), and playlist create/update/delete synchronization.
- DPAPI-encrypted password storage and portable-aware persistent cache paths.
