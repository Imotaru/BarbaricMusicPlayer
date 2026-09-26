// Everything the keyboard and the command palette can do. Shortcuts live in keys.ts (defaults) and
// the user's preferences (overrides); see keymap.svelte.ts.

import { openBpmEditor, openInfoEditor, openPlaylistPicker, openSkipsEditor, openTagPicker } from './actions'
import { library } from './library.svelte'
import { player } from './player.svelte'
import { ui } from './ui.svelte'

export interface Command {
  id: string
  label: string
  group: 'Playback' | 'Songs' | 'Go to' | 'App'
  run: () => void
  /** Whether it can run right now; commands that can't are skipped by keys and left out of the palette. */
  when?: () => boolean
  /** Its shortcuts also work while typing in a text box (only chords with Ctrl, Alt or Win). */
  inInputs?: boolean
  /** It can also be given a system-wide hotkey (the host runs it; see GlobalHotkeys.cs). */
  global?: boolean
  /** Left out of the palette (it only makes sense as a hotkey). */
  hidden?: boolean
}

export const focusSearch = () => document.getElementById('search')?.focus()

const listShown = () => !ui.compact
const hasSelection = () => !ui.compact && library.total > 0
const view = () => library.view.kind

export const COMMANDS: Command[] = [
  {
    id: 'player.toggle',
    label: 'Play / pause',
    group: 'Playback',
    global: true,
    run: () => (player.loaded ? player.toggle() : library.playSelected()),
  },
  { id: 'player.next', label: 'Next song', group: 'Playback', global: true, run: player.next },
  { id: 'player.previous', label: 'Previous song', group: 'Playback', global: true, run: player.previous },
  { id: 'player.seekForward', label: 'Forward 5 seconds', group: 'Playback', global: true, run: () => player.seekBy(5) },
  { id: 'player.seekBack', label: 'Back 5 seconds', group: 'Playback', global: true, run: () => player.seekBy(-5) },
  { id: 'player.seekForwardLong', label: 'Forward 30 seconds', group: 'Playback', run: () => player.seekBy(30) },
  { id: 'player.seekBackLong', label: 'Back 30 seconds', group: 'Playback', run: () => player.seekBy(-30) },
  { id: 'player.volumeUp', label: 'Volume up', group: 'Playback', global: true, run: () => player.changeVolume(0.05) },
  { id: 'player.volumeDown', label: 'Volume down', group: 'Playback', global: true, run: () => player.changeVolume(-0.05) },
  { id: 'player.shuffle', label: 'Shuffle on / off', group: 'Playback', global: true, run: player.toggleShuffle },
  { id: 'player.loop', label: 'Loop song on / off', group: 'Playback', global: true, run: player.toggleLoop },
  {
    id: 'library.playShuffled',
    label: 'Shuffle play this list',
    group: 'Playback',
    when: () => listShown() && library.total > 0 && view() !== 'suggested' && view() !== 'hidden',
    run: library.playShuffled,
  },
  { id: 'player.openFile', label: 'Open a file…', group: 'Playback', inInputs: true, run: player.openFile },

  { id: 'library.search', label: 'Search', group: 'Songs', inInputs: true, when: listShown, run: focusSearch },
  { id: 'selection.edit', label: 'Edit info of selected songs…', group: 'Songs', when: hasSelection, run: () => openInfoEditor() },
  { id: 'selection.tag', label: 'Tag selected songs…', group: 'Songs', when: hasSelection, run: () => openTagPicker() },
  { id: 'selection.playlist', label: 'Add selected songs to a playlist…', group: 'Songs', when: hasSelection, run: () => openPlaylistPicker() },
  { id: 'selection.bpm', label: 'Edit BPM of selected songs…', group: 'Songs', when: hasSelection, run: () => openBpmEditor() },
  { id: 'selection.skips', label: 'Edit skips of selected songs…', group: 'Songs', when: hasSelection, run: () => openSkipsEditor() },
  { id: 'selection.clearSkips', label: 'Clear skips of selected songs', group: 'Songs', when: hasSelection, run: library.clearSkipsSelected },
  {
    id: 'selection.hide',
    label: 'Hide / unhide selected songs',
    group: 'Songs',
    when: hasSelection,
    run: () => (view() === 'hidden' ? library.unhideSelected() : library.hideSelected()),
  },
  {
    id: 'selection.keep',
    label: 'Keep selected songs',
    group: 'Songs',
    when: () => hasSelection() && view() === 'suggested',
    run: library.keepSelected,
  },
  {
    id: 'selection.delete',
    label: 'Remove from playlist / delete file',
    group: 'Songs',
    when: () => hasSelection() && ['manual', 'suggested', 'hidden'].includes(view()),
    run: () => (view() === 'manual' ? library.removeSelectedFromPlaylist() : library.confirmRecycle()),
  },
  {
    id: 'selection.moveUp',
    label: 'Move selected songs up',
    group: 'Songs',
    when: () => hasSelection() && library.canReorder,
    run: () => library.moveSelected(-1),
  },
  {
    id: 'selection.moveDown',
    label: 'Move selected songs down',
    group: 'Songs',
    when: () => hasSelection() && library.canReorder,
    run: () => library.moveSelected(1),
  },
  { id: 'library.selectAll', label: 'Select all', group: 'Songs', when: hasSelection, run: library.selectAll },
  { id: 'library.clearFilter', label: 'Clear tag filter', group: 'Songs', when: () => listShown() && library.hasFilter, run: library.clearFilter },
  { id: 'library.resetLens', label: 'Reset BPM range', group: 'Songs', when: () => listShown() && library.lensActive, run: library.resetLens },

  { id: 'view.library', label: 'Library', group: 'Go to', when: listShown, run: library.openLibrary },
  { id: 'view.suggested', label: 'Suggested for removal', group: 'Go to', when: listShown, run: library.openSuggested },
  { id: 'view.hidden', label: 'Hidden songs', group: 'Go to', when: listShown, run: library.openHidden },

  { id: 'app.palette', label: 'Command palette', group: 'App', inInputs: true, hidden: true, when: listShown, run: () => ui.openPalette() },
  { id: 'app.settings', label: 'Settings…', group: 'App', inInputs: true, when: listShown, run: () => ui.openSettings() },
  { id: 'window.compact', label: 'Mini player on / off', group: 'App', global: true, inInputs: true, run: () => ui.setCompact(!ui.compact) },
  { id: 'window.show', label: 'Bring the player to the front', group: 'App', global: true, hidden: true, run: () => {} },
  { id: 'library.addFolder', label: 'Add a music folder…', group: 'App', run: library.addFolder },
  { id: 'library.rescan', label: 'Rescan music folders', group: 'App', run: library.rescan },
]

export const commandById = new Map(COMMANDS.map((c) => [c.id, c]))

export const canRun = (command: Command) => command.when?.() ?? true
