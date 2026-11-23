# Changelog

## v1.2.0
- **Performance & 32-bit x86 Memory Hardening (20k–50k+ Track Libraries)**:
  - **Line-Streamed Compact `catalog.json`**: Replaced single-string serialization with a buffered streaming line-delimited JSON array writer/reader (omitting empty/zero defaults), cutting 32-bit Large Object Heap (LOH) spikes from ~40–80 MB down to `<64 KB` while remaining 100% valid JSON with automatic legacy file fallback.
  - **O(1) In-Place Tag Matrix Updates**: Added `CatalogIndex` tracking on `SubsonicSong` so star, rating, and playcount updates refresh `_allTagArrays` in place without reallocating or scanning the full catalog.
  - **Faster State Sync & Indexing**: Replaced O(N²) album-folder dedupe scans with per-artist `HashSet<string>`, batched `ToTagMatrix` cache registration under a single lock, and decoupled server star/rating pushes into bounded parallel batches (`DOP=4`) with a single debounced disk save.
- **UI & Audio Thread Deadlock Elimination**:
  - Removed synchronous `lyricsTask.Wait(250)` and synchronous UI `Invoke` stalls from BASS's `GetStream()` open path; lyrics now attach non-blockingly via `BeginInvoke`.
  - Hardened `MusicBeeArtworkBridge.RunOnUiThread` (never falling through to background threads on `Invoke` failure) and wrapped per-type AppDomain reflection discovery in `try/catch` guards.
  - Fixed a timer disposal race in `ScheduleDebouncedPlaylistPush` during rapid playlist edits.
- **Offline Resilience & Hardened Audio/Lyrics Caches**:
  - **Persistent Offline Sync Queue (`pending_actions.json`)**: Failed scrobbles (preserving exact original playback Unix timestamps), stars, and ratings while offline are queued atomically, deduplicated per track, protected from being clobbered by `RefreshUserState`, and replayed automatically on reconnect or resync.
  - **Audio Cache Integrity & Cancellation**: Verified `ConnectStream` tee completion against HTTP `Content-Length`, added startup cleanup of orphaned `.tmp_`/`.tee_` files, added cancellable chunked prefetch downloads with `SemaphoreSlim(2, 2)`, relaxed size checks to 90% when `Content-Length` is absent, and switched LRU eviction to NTFS-reliable `LastWriteTimeUtc`.
  - **Bounded Lyrics Cache & Accurate LRC Trimming**: Capped in-memory lyrics cache at 2,000 entries with atomic disk writes and fixed leading prose trimming so bracketed text like `[Live]`/`[Intro]` no longer breaks synced LRC detection.
- **CUE Sub-Track Safety, Expanded Local Matching & Server-Switch Cleanup**:
  - **MusicBee 3.7 CUE Sub-Track Protection**: Detects `#<n>#` CUE sub-track URLs, skips path-suffix false matches, and avoids MusicBee's unguarded `Library_SetFileTag`/`Library_CommitTagsToFile` crash while still linking via metadata and syncing read-side state.
  - **Expanded Local Library Matching**: Improved multi-artist normalization, Unicode NFD/NFC handling, `[Unknown Album]` matching, `CD1`/`Disc 1` folder collapsing, `&`/`and` variants, false-positive guards, and partial-catalog link preservation, plus `unmatched_tracks.log` diagnostics.
  - **Clean Server Switching & Import Accounting**: Switching Navidrome servers automatically clears stale caches/links (preserving `PlaylistBackups`); library imports now accurately count added vs. failed tracks and skip redundant per-track artwork calls.
  - **DPI-Aware Custom Storage Icon & Backup Safety**: Added a crisp vector-rendered NaviBee sidebar icon (`StorageIconRenderer`) and same-millisecond filename collision protection in `PlaylistBackup`.

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
