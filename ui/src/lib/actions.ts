// Menus and pickers shared by the list, the sidebar and the keyboard shortcuts in App.svelte.

import { bpm } from './bpm.svelte'
import { library, songs } from './library.svelte'
import { playlists, type Playlist } from './playlists.svelte'
import { tags, type Tag } from './tags.svelte'
import { ui, type MenuItem, type Point } from './ui.svelte'

/** Where a popover opened from the keyboard should appear: under the cursor row, or mid-list. */
function cursorAnchor(): Point {
  const row = document.querySelector(`.tracks .row[data-index="${library.cursor}"]`)
  const list = document.querySelector('.tracks .viewport')
  if (row && list) {
    const r = row.getBoundingClientRect()
    const l = list.getBoundingClientRect()
    if (r.bottom > l.top && r.top < l.bottom) return { x: l.left + 48, y: r.bottom + 2 }
  }
  const l = list?.getBoundingClientRect()
  return l ? { x: l.left + l.width / 2 - 150, y: l.top + 40 } : { x: 200, y: 120 }
}

export const openTagPicker = (at: Point = cursorAnchor()) => ui.openPicker('tag', library.selectedIds(), at)

export const openPlaylistPicker = (at: Point = cursorAnchor()) => ui.openPicker('playlist', library.selectedIds(), at)

export const openBpmEditor = (at: Point = cursorAnchor()) => ui.openBpmEditor(library.selectedIds(), at)

export function openRowMenu(at: Point, index: number) {
  library.contextSelect(index)
  const count = library.selectedCount
  const items: MenuItem[] = [
    { label: 'Play', shortcut: 'Enter', action: () => library.playIndex(index) },
    { separator: true },
    { label: count > 1 ? `Tag ${songs(count)}…` : 'Tag…', shortcut: 'T', action: () => openTagPicker(at) },
    { label: 'Add to playlist…', shortcut: 'P', action: () => openPlaylistPicker(at) },
    { separator: true },
    { label: 'Edit BPM…', shortcut: 'B', action: () => openBpmEditor(at) },
    { label: 'Double BPM', action: () => bpm.scaleSelected(2) },
    { label: 'Halve BPM', action: () => bpm.scaleSelected(0.5) },
    { label: count > 1 ? `Analyze BPM of ${songs(count)}` : 'Analyze BPM', action: bpm.analyzeSelected },
  ]
  items.push({ separator: true })
  const hide: MenuItem = { label: 'Hide from library', shortcut: 'H', action: library.hideSelected }
  const recycle: MenuItem = { label: 'Delete file…', shortcut: 'Del', danger: true, action: library.confirmRecycle }
  switch (library.view.kind) {
    case 'suggested':
      items.push({ label: 'Keep', shortcut: 'K', action: library.keepSelected }, hide, recycle)
      break
    case 'hidden':
      items.push({ label: 'Unhide', shortcut: 'H', action: library.unhideSelected }, recycle)
      break
    case 'manual':
      items.push(hide, {
        label: 'Remove from playlist',
        shortcut: 'Del',
        danger: true,
        action: library.removeSelectedFromPlaylist,
      })
      break
    default:
      items.push(hide)
  }
  ui.openMenu(at, items)
}

export function openTagMenu(at: Point, tag: Tag) {
  ui.openMenu(at, [
    { label: 'Show only this tag', action: () => library.showTag(tag.id) },
    { label: 'Rename', action: () => (ui.renaming = { kind: 'tag', id: tag.id }) },
    { swatches: tags.palette, current: tag.color, pick: (color) => ui.run(() => tags.setColor(tag.id, color)) },
    { separator: true },
    {
      label: 'Delete…',
      danger: true,
      action: () =>
        ui.openMenu(at, [
          {
            label: tag.count > 0 ? `Delete “${tag.name}” from ${songs(tag.count)}` : `Delete “${tag.name}”`,
            danger: true,
            action: () =>
              ui.run(async () => {
                await tags.remove(tag.id)
                library.dropTag(tag.id)
              }),
          },
          { label: 'Cancel', action: () => {} },
        ]),
    },
  ])
}

export function openPlaylistMenu(at: Point, playlist: Playlist) {
  const items: MenuItem[] = [
    { label: 'Open', action: () => library.openPlaylist(playlist) },
    { label: 'Rename', action: () => (ui.renaming = { kind: 'playlist', id: playlist.id }) },
  ]
  if (playlist.kind === 'filter') {
    items.push({
      label: 'Update from current view',
      // Only the library and filter views are searches that can be saved.
      disabled: library.view.kind !== 'library' && library.view.kind !== 'filter',
      action: () =>
        ui.run(async () => {
          await playlists.updateFilter(playlist.id, library.context)
          library.view = { kind: 'filter', id: playlist.id }
          ui.notify(`Saved the current view as ${playlist.name}.`)
        }),
    })
  }
  items.push(
    { separator: true },
    {
      label: 'Delete…',
      danger: true,
      action: () =>
        ui.openMenu(at, [
          {
            label: `Delete “${playlist.name}”`,
            danger: true,
            action: () =>
              ui.run(async () => {
                if (library.playlist?.id === playlist.id) library.openLibrary()
                await playlists.remove(playlist.id)
              }),
          },
          { label: 'Cancel', action: () => {} },
        ]),
    },
  )
  ui.openMenu(at, items)
}

/** The point to open a menu at for a mouse event. */
export const pointOf = (e: MouseEvent): Point => ({ x: e.clientX, y: e.clientY })
