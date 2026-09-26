# Barbaric Music Player

A lightweight Windows music player for local files. Planned features:
- a volume setting per song
- BPM detection, plus filtering by BPM range
- tag-based playlists
- instant search
- skip-aware shuffle

## Stack

| Layer | Tech |
|---|---|
| Host window | WPF (.NET 10), frameless window with a WebView2 control |
| Audio | NAudio 3 (Media Foundation decoding, WASAPI output that follows the default device) |
| Library | SQLite (Microsoft.Data.Sqlite + Dapper) with FTS5 search, TagLibSharp for tags |
| UI | Svelte 5 + TypeScript + Vite, in `ui/` |
| Bridge | JSON messages over `chrome.webview.postMessage` (`src/Barbaric.App/Bridge/WebBridge.cs`, `ui/src/lib/bridge.ts`) |

## Layout

```
src/Barbaric.Core/          audio engine, library (database, scanner, search), playback queue; no UI dependencies
src/Barbaric.App/           WPF host: window, WebView2, bridge, player and library APIs
tests/Barbaric.Core.Tests/  xUnit tests for Core
ui/                         web UI
```

## Requirements

- .NET 10 SDK
- Node.js 22+
- Microsoft Edge WebView2 Runtime (included with Windows 11)

## Development

```bash
cd ui && npm install && npm run dev
```

Then run `src/Barbaric.App` in Debug mode (from Visual Studio or Rider, or with `dotnet run --project src/Barbaric.App`).

- **Dev server:** Debug builds load the UI from the Vite dev server at `http://localhost:5173`, so UI edits hot-reload while music keeps playing. If the dev server isn't running, the app falls back to the last `ui/dist` build.
- **Open a file on startup:** pass its path as the first argument, e.g. `Barbaric.App.exe "C:\Music\song.mp3"`.
- **DevTools:** press F12 in Debug builds.
- **Library database:** stored at `%LOCALAPPDATA%\BarbaricMusicPlayer\library.db`. Set `BARBARIC_LIBRARY_DB` to a different path to test against a throwaway library.

## Library

- **Scanning:** add music folders in the sidebar. They're scanned on startup, when a folder is added and on "Rescan".
- **Moved or renamed files:** recognised by a content fingerprint, so a song keeps its saved volume and stats.
- **Deleted files:** hidden rather than deleted, so their data comes back if the file returns.
- **Search:** each word matches the start of a word in the title, artist, album, genre or file name. Accents are ignored, so `bjork` finds Björk.
- **Playing:** a song plays from the current list (search, filter, sort or playlist) and continues down it.

## Tags and playlists

- **Selecting:** Ctrl+click toggles a song, Shift+click selects a range, and Ctrl+A selects the whole list. Right-click for a menu.
- **Tags:** press T to tag the selection. Type to find a tag, or type a new name to create one. Each toggle applies to every selected song.
- **Filtering by tag:** in the sidebar's Tags section:
  - Click a tag to show only that tag.
  - Ctrl+click to require it as well.
  - Alt+click to exclude it.

  The chips above the list switch between include and exclude, and between matching all or any of the included tags.
- **Filter playlists:** "Save as playlist" keeps the current search, tag filter and sort as a playlist that updates itself. Opening one loads it back into the controls. If you change it, the header offers Save changes or Revert.
- **Manual playlists:** press P to add the selection to a playlist or start a new one. A song appears at most once in a playlist. Sort by # to keep the playlist's own order: Alt+↑/↓ moves the selection, and Del removes it.
- **Renaming:** double-click a playlist or tag in the sidebar, or right-click it to rename, recolour or delete it.

## Tests

```bash
dotnet test
```

## Release build

```bash
dotnet publish src/Barbaric.App -c Release
```

Release builds run `npm run build` automatically and copy `ui/dist` into `wwwroot/` next to the exe.

## Keyboard

| Key | Action |
|---|---|
| Space | Play / pause |
| ← / → | Seek back / forward 5 s (with Shift: 30 s) |
| Ctrl+← / Ctrl+→ | Previous / next song |
| ↑ / ↓, PgUp / PgDn, Home / End | Move in the list (with Shift: extend the selection) |
| Ctrl+A | Select every song in the list |
| Enter | Play the selected song |
| T | Tag the selected songs |
| P | Add the selected songs to a playlist |
| Alt+↑ / Alt+↓ | Move the selected songs within a manual playlist |
| Del | Remove the selected songs from the manual playlist |
| Esc | Clear the selection, then the tag filter |
| Ctrl+F or / | Search (↑/↓ and Enter work from the search box; Esc clears it) |
| Ctrl+O | Open a file |
