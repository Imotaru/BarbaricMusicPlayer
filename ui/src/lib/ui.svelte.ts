/**
 * App-wide overlay state: toast messages, the context menu, the popovers, confirmations, inline
 * renames, the command palette and the settings, plus whether the window is the mini-player.
 */

import { call, hasHost, on } from './bridge'
import { initial } from './prefs.svelte'

export type MenuItem =
  | { label: string; shortcut?: string; danger?: boolean; disabled?: boolean; action: () => void }
  | { separator: true }
  | { swatches: string[]; current: string; pick: (color: string) => void }

export interface Point {
  x: number
  y: number
}

/** A yes/no question before something that is hard to undo. */
export interface Confirm {
  title: string
  message: string
  /** Names of what the action touches, listed under the message. */
  items?: string[]
  confirmLabel: string
  danger?: boolean
  action: () => void
}

export type SettingsTab = 'appearance' | 'playback' | 'keyboard' | 'backup'

/** What the host reports about the window frame. */
interface WindowState {
  compact: boolean
  compactResized: boolean
}

/** A song from an imported backup whose file couldn't be found. */
export interface MissingSong {
  id: number
  fileName: string
  path: string
  title: string
  artist: string | null
  album: string | null
}

/** What importing a backup did, shown once it's done. */
export interface ImportReport {
  matched: number
  added: number
  tagsCreated: number
  playlistsCreated: number
  playlistsReplaced: number
  missing: MissingSong[]
}

const TOAST_MS = 3500

class Ui {
  toast = $state<{ message: string; error: boolean } | null>(null)
  menu = $state<(Point & { items: MenuItem[] }) | null>(null)
  /** The Tag… / Add to playlist… popover, acting on a snapshot of the selected track ids. */
  picker = $state<(Point & { mode: 'tag' | 'playlist'; trackIds: number[] }) | null>(null)
  /** The BPM editor popover, acting on a snapshot of the selected track ids. */
  bpmEditor = $state<(Point & { trackIds: number[] }) | null>(null)
  /** The skip count editor popover, acting on a snapshot of the selected track ids. */
  skipsEditor = $state<(Point & { trackIds: number[] }) | null>(null)
  /** The song info editor popover, acting on a snapshot of the selected track ids. */
  infoEditor = $state<(Point & { trackIds: number[] }) | null>(null)
  confirm = $state<Confirm | null>(null)
  importReport = $state<ImportReport | null>(null)
  /** The sidebar item showing an inline name editor. */
  renaming = $state<{ kind: 'tag' | 'playlist'; id: number } | null>(null)
  palette = $state(false)
  settings = $state<SettingsTab | null>(null)
  /** The window is the small always-on-top mini-player. */
  compact = $state(initial.compact === true)
  /** The mini-player has been resized away from its default size. */
  compactResized = $state(false)

  private toastTimer: ReturnType<typeof setTimeout> | undefined

  constructor() {
    if (!hasHost) return
    call<WindowState>('window.getState').then((s) => {
      this.compact = s.compact
      this.compactResized = s.compactResized
    })
    on<WindowState>('window.state', (s) => {
      this.compact = s.compact
      this.compactResized = s.compactResized
      if (s.compact) this.closeAll()
    })
  }

  get overlayOpen() {
    return (
      this.menu !== null ||
      this.picker !== null ||
      this.bpmEditor !== null ||
      this.skipsEditor !== null ||
      this.infoEditor !== null ||
      this.confirm !== null ||
      this.importReport !== null ||
      this.palette ||
      this.settings !== null
    )
  }

  notify = (message: string, error = false) => {
    this.toast = { message, error }
    clearTimeout(this.toastTimer)
    this.toastTimer = setTimeout(() => (this.toast = null), error ? TOAST_MS * 2 : TOAST_MS)
  }

  fail = (e: unknown) => this.notify(e instanceof Error ? e.message : String(e), true)

  /** Runs a host action, reporting any failure as an error toast. */
  run = async (action: () => Promise<unknown>) => {
    try {
      await action()
    } catch (e) {
      this.fail(e)
    }
  }

  openMenu(at: Point, items: MenuItem[]) {
    this.closeDialogs()
    this.picker = null
    this.bpmEditor = null
    this.skipsEditor = null
    this.infoEditor = null
    this.confirm = null
    this.menu = { ...at, items }
  }

  closeMenu = () => (this.menu = null)

  openPicker(mode: 'tag' | 'playlist', trackIds: number[], at: Point) {
    if (trackIds.length === 0) return
    this.closeDialogs()
    this.menu = null
    this.bpmEditor = null
    this.skipsEditor = null
    this.infoEditor = null
    this.confirm = null
    this.picker = { mode, trackIds, ...at }
  }

  closePicker = () => (this.picker = null)

  openBpmEditor(trackIds: number[], at: Point) {
    if (trackIds.length === 0) return
    this.closeDialogs()
    this.menu = null
    this.picker = null
    this.skipsEditor = null
    this.infoEditor = null
    this.confirm = null
    this.bpmEditor = { trackIds, ...at }
  }

  closeBpmEditor = () => (this.bpmEditor = null)

  openSkipsEditor(trackIds: number[], at: Point) {
    if (trackIds.length === 0) return
    this.closeDialogs()
    this.menu = null
    this.picker = null
    this.bpmEditor = null
    this.infoEditor = null
    this.confirm = null
    this.skipsEditor = { trackIds, ...at }
  }

  closeSkipsEditor = () => (this.skipsEditor = null)

  openInfoEditor(trackIds: number[], at: Point) {
    if (trackIds.length === 0) return
    this.closeDialogs()
    this.menu = null
    this.picker = null
    this.bpmEditor = null
    this.skipsEditor = null
    this.confirm = null
    this.infoEditor = { trackIds, ...at }
  }

  closeInfoEditor = () => (this.infoEditor = null)

  openConfirm(confirm: Confirm) {
    this.closeDialogs()
    this.menu = null
    this.picker = null
    this.bpmEditor = null
    this.skipsEditor = null
    this.infoEditor = null
    this.confirm = confirm
  }

  closeConfirm = () => (this.confirm = null)

  openImportReport(report: ImportReport) {
    this.closeAll()
    this.importReport = report
  }

  closeImportReport = () => (this.importReport = null)

  openPalette() {
    this.closeAll()
    this.palette = true
  }

  closePalette = () => (this.palette = false)

  openSettings(tab: SettingsTab = 'appearance') {
    this.closeAll()
    this.settings = tab
  }

  closeSettings = () => (this.settings = null)

  closeAll = () => {
    this.menu = null
    this.picker = null
    this.bpmEditor = null
    this.skipsEditor = null
    this.infoEditor = null
    this.confirm = null
    this.importReport = null
    this.renaming = null
    this.closeDialogs()
  }

  /** Switches the window to or from the mini-player; the host answers with a window.state event. */
  setCompact = (on: boolean) => {
    if (on) this.closeAll()
    if (hasHost) this.run(() => call('window.setCompact', { on }))
  }

  /** Puts the mini-player back to its default size, now if it is showing or else the next time. */
  resetCompactSize = () => {
    if (hasHost) this.run(() => call('window.resetCompactSize'))
  }

  private closeDialogs() {
    this.palette = false
    this.settings = null
  }
}

export const ui = new Ui()
