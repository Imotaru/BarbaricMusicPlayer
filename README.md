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
| UI | Svelte 5 + TypeScript + Vite, in `ui/` |
| Bridge | JSON messages over `chrome.webview.postMessage` (`src/Barbaric.App/Bridge/WebBridge.cs`, `ui/src/lib/bridge.ts`) |

## Layout

```
src/Barbaric.Core/          audio engine and (later) library, BPM, shuffle logic; no UI dependencies
src/Barbaric.App/           WPF host: window, WebView2, bridge, player API
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
| Ctrl+O | Open a file |
