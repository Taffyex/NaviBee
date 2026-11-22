# Changelog

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
