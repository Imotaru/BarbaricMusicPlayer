/** App-wide overlay state: toast messages, the context menu, the popovers, confirmations and inline renames. */

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

const TOAST_MS = 3500

class Ui {
  toast = $state<{ message: string; error: boolean } | null>(null)
  menu = $state<(Point & { items: MenuItem[] }) | null>(null)
  /** The Tag… / Add to playlist… popover, acting on a snapshot of the selected track ids. */
  picker = $state<(Point & { mode: 'tag' | 'playlist'; trackIds: number[] }) | null>(null)
  /** The BPM editor popover, acting on a snapshot of the selected track ids. */
  bpmEditor = $state<(Point & { trackIds: number[] }) | null>(null)
  confirm = $state<Confirm | null>(null)
  /** The sidebar item showing an inline name editor. */
  renaming = $state<{ kind: 'tag' | 'playlist'; id: number } | null>(null)

  private toastTimer: ReturnType<typeof setTimeout> | undefined

  get overlayOpen() {
    return this.menu !== null || this.picker !== null || this.bpmEditor !== null || this.confirm !== null
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
    this.picker = null
    this.bpmEditor = null
    this.confirm = null
    this.menu = { ...at, items }
  }

  closeMenu = () => (this.menu = null)

  openPicker(mode: 'tag' | 'playlist', trackIds: number[], at: Point) {
    if (trackIds.length === 0) return
    this.menu = null
    this.bpmEditor = null
    this.confirm = null
    this.picker = { mode, trackIds, ...at }
  }

  closePicker = () => (this.picker = null)

  openBpmEditor(trackIds: number[], at: Point) {
    if (trackIds.length === 0) return
    this.menu = null
    this.picker = null
    this.confirm = null
    this.bpmEditor = { trackIds, ...at }
  }

  closeBpmEditor = () => (this.bpmEditor = null)

  openConfirm(confirm: Confirm) {
    this.menu = null
    this.picker = null
    this.bpmEditor = null
    this.confirm = confirm
  }

  closeConfirm = () => (this.confirm = null)
}

export const ui = new Ui()
