# NaviBee v1.2.0

NaviBee is a native **Navidrome and Subsonic / OpenSubsonic storage provider and two-way sync plugin** for [MusicBee](https://getmusicbee.com/). Engineered in pure C# (.NET Framework 4.8, x86) against the MusicBee plugin API, it links your local MusicBee master library to Navidrome and mounts your remote library with HTTP `Range`-seekable streaming, single-pass audio/artwork/lyrics caching, time-synced LRC lyrics, full two-way playcount/rating/star sync, and safe opt-in playlist synchronization.

---

## Installation

### Quick Install (Recommended)
1. Download **[`NaviBee_v1.2.0.zip`](https://github.com/Taffyex/NaviBee/releases/download/v1.2.0/NaviBee_v1.2.0.zip)** (or raw **[`mb_NaviBee.dll`](https://github.com/Taffyex/NaviBee/releases/download/v1.2.0/mb_NaviBee.dll)**) from the [latest release](https://github.com/Taffyex/NaviBee/releases).
2. In MusicBee, press `Ctrl + O` (or go to **Edit** > **Preferences**).
3. Select **Plugins**, click **Add Plugin**, and choose `NaviBee_v1.2.0.zip` (or `mb_NaviBee.dll`).
4. Click **Apply** and restart MusicBee.

### Manual Drop-in
1. Close MusicBee.
2. Copy `mb_NaviBee.dll` into your MusicBee plugins directory:
   - **Installer edition:** `C:\Program Files (x86)\MusicBee\Plugins\`
   - **Portable edition:** `<MusicBee Folder>\Plugins\`
   - **Windows Store:** `%APPDATA%\MusicBee\Plugins\`
3. Launch MusicBee.

---

## Settings

![NaviBee Settings](screenshots/navibee_settings.png)

---

## Features

- **Local Master Library Linking & Two-Way Sync**: Pairs your local MusicBee files with Navidrome tracks via embedded PID (`Custom16`), server-relative path suffix (extension-agnostic), or canonical metadata (`AlbumArtist/Artist`, `Album`, `Disc`, `Track`, `Title`, duration). Playing, rating, loving, or editing playlists with local files syncs two-way with Navidrome and mobile clients like Synfonium—pulling `PlayCount`, `LastPlayed`, stars, and ratings back into MusicBee without rewriting audio files for playcount updates.
- **Native Storage Provider (`PluginType.Storage`) & Deduplicated Import**: Browse `All Albums`, `Favorites`, `Top Rated`, `Most Played`, `Playlists`, and `Artist / Album` inside MusicBee, or run **Tools** > **Navidrome: Import Missing Tracks into Music Library** to import only remote tracks not already linked locally.
- **Time-Synced Lyrics & Local Cache**: Fetches OpenSubsonic `getLyricsBySongId` (with `getLyrics` fallback), formats synced `.lrc` lines with offset correction, caches lyrics under `NavidromeCache\Lyrics`, pre-fetches the next queued track, and injects lyrics in memory into MusicBee's Lyrics panel.
- **Smart Startup Catalog Cache & Offline Resilience**: Streams compact `catalog.json` line-by-line (keeping 32-bit memory spikes minimal even on 50k+ track libraries), mounts `Navidrome:\` immediately on startup, checks `getScanStatus`, refreshes full server user state (`Starred`, `UserRating`, `PlayCount`, `Played`), and queues offline scrobbles (with exact playback timestamps), stars, and ratings in `pending_actions.json` for automatic replay on reconnect.
- **Range-Seekable Streaming & Hardened LRU Audio Cache**: Streams via `ConnectStream` with HTTP `Range` seeking, `Content-Length`-verified single-pass capture, optional server transcoding (`Original`, `128`, `192`, `256`, `320 kbps` with `estimateContentLength=true`), configurable cache limits (`0` to `Unlimited`), NTFS-safe LRU eviction, and cancellable next-track prefetching.
- **High-Resolution Artwork Cache**: Background pre-warming and on-demand caching at configurable sizes (`Original`, `1200 px`, `800 px`, `300 px`).
- **Safe Opt-In Playlist Sync**: Off by default; never deletes server playlists when a MusicBee playlist is deleted, never sends empty playlists, never overwrites unlinked same-name server playlists or playlists owned by other users, and saves verified JSON backups (`NavidromeCache\PlaylistBackups\`) before every update.
- **Portable-Aware & Encrypted**: Stores cache and settings under MusicBee's persistent storage path and encrypts credentials with Windows DPAPI (`ProtectedData`).

> **Note on virtual storage tags:** MusicBee's `Navidrome` storage view is backed by the Subsonic API (`Rating`, `Love`, `PlayCount`, and `Playlists` sync to Navidrome; full ID3/FLAC tag editing should be performed on your linked local master files in the main MusicBee library).

---

## Configuration

1. Open **Edit** > **Preferences** > **Plugins** > **Navidrome Storage & Sync** > **Configure**.
2. Enter your **Navidrome URL** (for example `https://music.example.com` or `http://localhost:4533`), **Username**, and **Password**.
3. Click **Test Connection**, then **Save**.
4. Click **Link Local Library** (or run **Tools** > **Navidrome: Link Local Library Tracks & Sync State**) to pair existing local tracks and run an initial two-way sync.

---

## Building from Source

NaviBee targets .NET Framework 4.8 (x86) to match the 32-bit MusicBee host architecture.

```powershell
dotnet build NaviBee.sln -c Release -warnaserror
```

The compiled binary is written to `src/NaviBee/bin/mb_NaviBee.dll`.

---

## License

MIT License. Copyright (c) 2026 TaffyEx.
