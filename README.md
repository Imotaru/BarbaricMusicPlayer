# Barbaric Music Player

A lightweight Windows music player for local files. Features:
- a volume setting per song
- silence skipping, and your own start and end time per song, set on its waveform
- BPM detection, plus filtering by BPM range
- tag-based playlists
- instant search
- skip-aware shuffle, with suggestions for songs you always skip

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
src/Barbaric.Core/          audio engine, library (database, scanner, search), BPM analysis, playback queue; no UI dependencies
src/Barbaric.App/           WPF host: window, WebView2, bridge, player, library, tag, playlist and BPM APIs
tests/Barbaric.Core.Tests/  xUnit tests for Core
ui/                         web UI
```

## Requirements

- .NET 10 SDK
- Node.js 22+
- Microsoft Edge WebView2 Runtime (included with Windows 11)

## Running

Run `build.bat` to build a Release copy into `publish\` (this takes a minute), then start `publish\Barbaric.App.exe`. To pick up new changes later, close the player and run `build.bat` again.

You can also pin `publish\Barbaric.App.exe` to the taskbar or Start menu. It opens files passed to it, so you can use it with "Open with".

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
- **Editing song info:** press E (or right-click → Edit info…) to change the title, artist, album, album artist, genre, year or track number of the selection. With several songs selected, only the fields you change are applied. Edits are stored in the library, never written to the files, and a rescan keeps them even if the file's tags change. ↺ next to a field, or "Use file tags", goes back to what the file says.
- **Playing:** a song plays from the current list (search, filter, sort or playlist) and continues down it.
- **Now playing:** the square left of the song title in the player bar shows where the songs come from. Click it to list exactly the songs being played through, in the order of their list. Songs tagged or added after playback started join only when the list is drawn again: a new shuffle pass, turning shuffle on or off, or a new BPM range. Double-click a song there to play it without starting the list over. "from …" next to the title opens the original list as it is now.

## BPM

- **Detection:** songs without a BPM are analyzed in the background, at low priority, after each scan. The sidebar shows progress, with Stop to pause it and "Analyze BPM (N left)" to resume.
  - The analyzer decodes a minute from the middle of the song at about 11 kHz. It builds an onset envelope from spectral flux (per frequency band, weighted towards the lows) and autocorrelates it over 60–200 BPM.
  - Half- and double-tempo candidates are checked before settling on a value.
  - A song with no clear beat shows "–" and isn't retried.
- **Sources:** a BPM from the file's tag is kept until you ask for an analysis ("Analyze BPM" in the right-click menu), and then the measured value wins. A BPM set by hand is never overwritten, by analysis or by a rescan.
- **The BPM column:** hover it to see where a value came from. Uncertain measurements show a "?", and values set by hand have a dot.
- **Editing:** press B (or double-click a BPM cell) to open the editor for the selection. Type a value, use ÷2 / ×2 to fix an octave error, or tap along with the beat (click the pad or press T). "Reset to automatic" goes back to the file's tag, or measures the song again. Double BPM and Halve BPM are also in the right-click menu.
- **BPM range:** the slider next to the search box narrows every view (the library and any playlist) and the play queue to a tempo range.
  - A handle at either end means no limit on that side.
  - "+?" also keeps songs whose BPM isn't known.
  - The range stays set as you switch views. Changing it re-filters the queue, and the song that is playing carries on.

## Silence, start and end

- **Skipping silence:** songs start where their sound starts and move on where it ends. The edges are found in the same background pass that measures each song's volume, and a song is only measured once.
  - A little room is left around the sound (0.1 s before, 0.3 s after), so soft attacks and fading tails aren't cut.
  - Settings → Playback turns it off, and sets how quiet counts as silence (-70 to -30 dB, default -50). Changing the level takes effect at once, without measuring again.
  - A song that is quieter than the level all the way through plays whole.
  - The seek bar shows the parts that are skipped as a fainter track.
- **Your own start and end:** right-click a song → **Edit start and end…** to open its waveform.
  - Drag the Start and End lines, nudge them with the arrow keys (Shift for bigger steps), or type the times.
  - Scroll to zoom and drag to move around. The strip on top shows the whole song, and dashed lines mark where the silence is.
  - "Preview start" plays the first seconds from the start line, "Preview end" the last seconds up to the end line, and a click on the waveform plays from there. Space starts or stops a preview.
  - The song that's playing pauses while you preview and carries on when the editor closes. Previews never count as plays or skips.
  - A time left where the silence puts it keeps following the silence. "Reset to automatic" goes back to that. Your own times apply even with silence skipping off.
- **Counting plays:** plays and skips are measured within the part that plays, so skipped silence doesn't count as listening.

## Tags and playlists

- **Selecting:** Ctrl+click toggles a song, Shift+click selects a range, and Ctrl+A selects the whole list. Right-click for a menu; "Reveal in File Explorer" there opens each song's folder with its file selected.
- **Tags:** press T to tag the selection. Type to find a tag, or type a new name to create one. Each toggle applies to every selected song.
- **Filtering by tag:** in the sidebar's Tags section:
  - Click a tag to show only that tag.
  - Ctrl+click to require it as well.
  - Alt+click to exclude it.

  The chips above the list switch between include and exclude, and between matching all or any of the included tags.
- **Filtering by artist:** click a song's artist to show only songs with exactly that artist. Tags can then narrow it further, and its chip's × removes it.
- **Filter playlists:** "Save as playlist" keeps the current search, artist and tag filter, BPM range and sort as a playlist that updates itself. The BPM range becomes the playlist's own, shown as a chip, and still combines with whatever range the slider is set to later. Opening one loads it back into the controls. If you change it, the header offers Save changes or Revert.
- **Manual playlists:** press P to add the selection to a playlist or start a new one. A song appears at most once in a playlist. Sort by # to keep the playlist's own order: Alt+↑/↓ moves the selection, and Del removes it.
- **Renaming:** double-click a playlist or tag in the sidebar, or right-click it to rename, recolour or delete it.

## Plays, skips and shuffle

- **Counting:** each time you leave a song, the player logs how much of it played.
  - Pressing Next, or picking another song, before 30% of it has played counts as a skip.
  - Playing 80% or more of it, or to the end, counts as a play.
  - Anything in between, and Previous, Stop or closing the app, updates "last played" without counting either way.
  - The Plays and Skips columns show the counts, and sort by them. Settings → Appearance can hide the Skips column everywhere but Suggested for removal.
  - Right-click songs to **Edit skips…** (set the count by hand) or **Clear skips**. Plays are left alone, and the song joins or leaves Suggested for removal to match.
- **Smart shuffle:** press S (or the shuffle button) to shuffle the list you're playing from, without repeats.
  - Songs you tend to skip come up later, and so do songs you heard in the last day or so.
  - Long songs come up less often, in proportion to their length.
  - Settings → Playback can turn off the skip and length weighting.
  - Nothing is ruled out: even a song you always skip still turns up now and then.
  - The playing song carries on when shuffle is switched on or off.
- **Suggested for removal:** a song you have skipped at least 5 times, and most of the times it came on, shows up in this sidebar view. For each one you can:
  - **Keep** it (K): its counts start over.
  - **Hide** it (H): it stays on disk but leaves the library, playlists and tag counts. Hidden songs are listed under "Hidden songs", where H unhides them again.
  - **Delete** the file (Del): after you confirm, it moves to the Recycle Bin. If you restore it from there, the next scan brings it back with its history.

  A song that wins you back drops off the list by itself.

## Backup

- **Export** (Settings → Backup, or "Export backup…" in the command palette) saves a readable JSON file with:
  - tags (with their colours) and playlists, in order;
  - each song's info edits, BPM, volume, start and end times, plays, skips, last played, hidden flag and play history;
  - the theme, accent, keyboard shortcuts, global hotkeys, volume, loop and silence settings.

  Music folders, window placement and the queue belong to one PC and are left out.
- **Import** merges a backup into the library, after showing what it holds:
  - Songs are matched by content fingerprint, so they're found even after a move or rename, or on another PC.
  - For songs, tags (matched by name) and playlists (matched by name) that are in both, the backup's version wins. Anything else stays as it is.
  - A BPM that came from a file's tag is read from the file again rather than taken from the backup.
- **Missing files:** a song whose file isn't in the library keeps its data and is listed when the import finishes, by file name and the path it had, so you can go and find it. Copy list puts that list on the clipboard.
  - Such songs also appear under **Missing songs** in the sidebar, which shows where each file was last seen.
  - Adding the folder that holds the file (or a rescan) brings the song back with its tags, playlists and stats, even if the file was moved or renamed.
  - Forget (Del) drops a missing song and its data for good.

## Look and feel

- **Settings** (the gear in the title bar, or Ctrl+,): pick a theme (Ember, Graphite, Midnight, Moss or the light Paper) and an accent colour, and change keyboard shortcuts.
- **Remembered between sessions:** the theme, window size and position (including maximized), master volume, sidebar width, song list column widths, and the queue. The last song comes back paused where you left it, with the list it was playing from and shuffle.
- **Sidebar:** drag its edge to resize it. Double-click the edge to reset it.
- **Song list columns:** drag the line between two column headers to resize them. Width moves between those two, so the list always fits the window. Double-click a line (or use Settings → Appearance) to reset every column.
- **Mini player** (Ctrl+M, or the button in the title bar): shrinks the window to a small always-on-top player. It remembers where you put it.
- **Command palette** (Ctrl+K): run any command, or jump to a playlist or tag, by typing part of its name.
- **Shortcuts:** every command's keys can be changed under Settings → Keyboard. Playback commands, the mini player and "bring to front" can also get a **global** hotkey that works while another app is in front. Global hotkeys start empty and must use Ctrl+Alt, Ctrl+Shift or Win, or F13–F24.
- **Windows integration:** the media keys, the volume flyout and the lock screen show and control what's playing (with the song's cover art). The taskbar thumbnail has previous, play/pause and next buttons.

Settings live in the library database, so `BARBARIC_LIBRARY_DB` gives a test run its own settings (and its own WebView2 profile) too.

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

These are the defaults; change them under Settings → Keyboard. The list keys (arrows, PgUp/PgDn, Home/End, Enter, Esc, Ctrl+A) can't be changed.

| Key | Action |
|---|---|
| Space | Play / pause |
| ← / → | Seek back / forward 5 s (with Shift: 30 s) |
| Ctrl+← / Ctrl+→ | Previous / next song |
| Ctrl+↑ / Ctrl+↓ | Volume up / down |
| ↑ / ↓, PgUp / PgDn, Home / End | Move in the list (with Shift: extend the selection) |
| Ctrl+A | Select every song in the list |
| Enter | Play the selected song |
| T | Tag the selected songs |
| P | Add the selected songs to a playlist |
| B | Edit the BPM of the selected songs (in the editor: T taps the tempo, Enter saves) |
| Alt+↑ / Alt+↓ | Move the selected songs within a manual playlist |
| S | Shuffle on / off |
| H | Hide the selected songs (in Hidden songs: unhide them) |
| K | Keep the selected songs (in Suggested for removal) |
| Del | Remove the selected songs from the manual playlist; in Suggested for removal and Hidden songs, move their files to the Recycle Bin |
| Esc | Clear the selection, then the filter (the BPM range slider stays; reset it with its ×) |
| Ctrl+F or / | Search (↑/↓ and Enter work from the search box; Esc clears it) |
| Ctrl+O | Open a file |
| Ctrl+K | Command palette |
| Ctrl+, | Settings |
| Ctrl+M | Mini player on / off |
