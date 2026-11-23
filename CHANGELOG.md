# Changelog

## v1.1.0
- **Local Master Library Linking & PID Embedding**:
  - Matches local MusicBee library files to Navidrome songs via 3-tier matching: embedded PID (`Custom16`) / persistent `NavidromeCache\local_links.json`, normalized server-relative path suffix (including extension-agnostic `.flac` ↔ `.opus`/`.mp3` stems), and canonical metadata (`AlbumArtist`/`Artist`, `Album`, `Disc`, `Track`, `Title`, `±3s` duration).
  - Playing, rating, loving, or adding linked local files to playlists now syncs two-way with Navidrome without downloading remote audio streams or overwriting local artwork/lyrics.
  - Optional **Embed Navidrome ID in local tag (Custom16)** toggle (uncheck for database-only linking without touching audio files on disk).
- **Full Two-Way PlayCount, LastPlayed, Star & Rating Sync**:
  - Pulls `PlayCount`, `LastPlayed`, `Starred`, and `UserRating` from Navidrome / Synfonium into both linked local library files and virtual `Navidrome:\` tracks (database-only writes for `PlayCount`/`LastPlayed`, with baseline tracking to prevent double-scrobbling).
  - Initial **Link Local Library** pass seeds existing local MusicBee stars/ratings up to Navidrome without wiping local ratings with server `0`.
  - Removed the 30-album user-state refresh cap so every play and rating made on mobile clients (Synfonium, etc.) is pulled even when Navidrome's folder scan timestamp has not changed.
- **Deduplicated Import & Tools Menu**:
  - Registered all Navidrome commands directly under MusicBee's **Tools** menu (alongside hotkey commands), including **Tools > Navidrome: Link Local Library Tracks & Sync State** and **Tools > Navidrome: Import Missing Tracks into Music Library**.
  - Importing now links existing local library files first and skips tracks already present locally or already imported instead of creating duplicates.
- **Virtual Storage View & Settings Polish**:
  - Immediately refreshes `CachedTags` on star/rating/playcount updates and adds a `NowPlaying_GetFileTag` fallback for virtual storage view edits.
  - Added **Link local library tracks**, **Embed Navidrome ID in local tag (Custom16)**, live linked-track count, and a one-click **Link Local Library** button in Settings.

## v1.0.2
- **Fixed startup "File not found"**: registers `Navidrome:\` synchronously on `PluginStartup` whenever a cached catalog is present, with offline cache playback fallback.
- **Safe, opt-in playlist sync**: opt-in push, never deletes server playlists, never sends empty playlists, avoids name collisions, skips other users' playlists, 1.5 s debounce, and verified JSON backups (`NavidromeCache\PlaylistBackups\`).
- **Fixed tag/rating/star & scrobble sync bugs**: prevents blank-field wipes on tag edits, filters skip events from scrobbling, refreshes user state on startup cache hits, and debounces `catalog.json` saves.
- **Transcoded stream seeking & richer metadata**: `estimateContentLength=true`, `PlayCount`, `LastPlayed`, `DateAdded`, `SampleRate`, `Channels`, and `Comment`.
- **Settings & UX polish**: auto-normalized URLs, password toggle, server info on Test Connection, clean shutdown cancellation, and cache cleanup on uninstall.

## v1.0.1
- Added OpenSubsonic `getLyricsBySongId` and legacy `getLyrics` support with time-synced `[MM:SS.ff]` LRC formatting, disk caching, prefetching, and safe in-memory Lyrics panel injection.
- Added automated GitHub Release workflow.

## v1.0.0
- Initial public release of NaviBee (`mb_NaviBee.dll`).
