import { call, hasHost } from './bridge'
import { DEFAULT_THEME, isHexColor, themeById } from './themes'

/** The UI's own settings. The host stores them as-is and hands them back before the page's first paint. */
export interface Prefs {
  v: 1
  theme: string
  /** A custom accent over the theme's own, or null for the theme's. */
  accent: string | null
  sidebarWidth: number
  /** Shortcut overrides by command id; commands not listed keep their defaults. */
  keys: Record<string, string[]>
  /** Whether song lists show skip counts. Suggested for removal always does. */
  showSkips: boolean
}

export const SIDEBAR_DEFAULT = 230
export const SIDEBAR_MIN = 180
export const SIDEBAR_MAX = 420

/** Settings the host injects before the page loads (see MainWindow.Frame.cs). */
export interface Initial {
  ui?: unknown
  background?: string
  compact?: boolean
}

export const initial: Initial = window.__barbaricInitial ?? {}

function clean(raw: unknown): Prefs {
  const value = (typeof raw === 'object' && raw !== null ? raw : {}) as Partial<Prefs>
  const keys: Record<string, string[]> = {}
  if (typeof value.keys === 'object' && value.keys !== null) {
    for (const [id, chords] of Object.entries(value.keys)) {
      if (Array.isArray(chords)) keys[id] = chords.filter((c): c is string => typeof c === 'string')
    }
  }
  return {
    v: 1,
    theme: themeById(typeof value.theme === 'string' ? value.theme : DEFAULT_THEME.id).id,
    accent: isHexColor(value.accent) ? value.accent.toLowerCase() : null,
    sidebarWidth:
      typeof value.sidebarWidth === 'number' && Number.isFinite(value.sidebarWidth)
        ? Math.min(Math.max(Math.round(value.sidebarWidth), SIDEBAR_MIN), SIDEBAR_MAX)
        : SIDEBAR_DEFAULT,
    keys,
    showSkips: value.showSkips !== false,
  }
}

class PrefsStore {
  theme = $state(DEFAULT_THEME.id)
  accent = $state<string | null>(null)
  sidebarWidth = $state(SIDEBAR_DEFAULT)
  keys = $state<Record<string, string[]>>({})
  showSkips = $state(true)

  /** What the host has, so an unchanged value is never written back. */
  private saved = ''

  constructor() {
    this.apply(clean(initial.ui))
    this.saved = JSON.stringify(this.snapshot())

    // A reload can run on a startup script older than the last change; the host's copy wins.
    this.reload()
  }

  /** Takes the host's copy, e.g. after a backup was imported. */
  reload = async () => {
    if (!hasHost) return
    const stored = await call<unknown>('settings.getUi')
    if (stored != null) {
      this.apply(clean(stored))
      this.saved = JSON.stringify(this.snapshot())
    }
  }

  setTheme = (id: string) => {
    this.theme = themeById(id).id
    this.save()
  }

  /** While dragging in a colour picker, pass `persist: false`; save once a colour is chosen. */
  setAccent = (accent: string | null, persist = true) => {
    this.accent = isHexColor(accent) ? accent.toLowerCase() : null
    if (persist) this.save()
  }

  /** While dragging, pass `persist: false`; save once the drag ends. */
  setSidebarWidth = (width: number, persist = true) => {
    this.sidebarWidth = Math.min(Math.max(Math.round(width), SIDEBAR_MIN), SIDEBAR_MAX)
    if (persist) this.save()
  }

  /** Replaces the shortcuts of every command in `changes`; null goes back to the default. */
  setKeys = (changes: Record<string, string[] | null>) => {
    const keys = { ...this.keys }
    for (const [id, chords] of Object.entries(changes)) {
      if (chords === null) delete keys[id]
      else keys[id] = chords
    }
    this.keys = keys
    this.save()
  }

  resetKeys = () => {
    this.keys = {}
    this.save()
  }

  setShowSkips = (on: boolean) => {
    this.showSkips = on
    this.save()
  }

  private snapshot(): Prefs {
    return { v: 1, theme: this.theme, accent: this.accent, sidebarWidth: this.sidebarWidth, keys: this.keys, showSkips: this.showSkips }
  }

  private apply(prefs: Prefs) {
    this.theme = prefs.theme
    this.accent = prefs.accent
    this.sidebarWidth = prefs.sidebarWidth
    this.keys = prefs.keys
    this.showSkips = prefs.showSkips
  }

  private save() {
    const value = this.snapshot()
    const json = JSON.stringify(value)
    if (json === this.saved || !hasHost) return
    this.saved = json
    call('settings.setUi', { value }).catch(() => (this.saved = ''))
  }
}

export const prefs = new PrefsStore()
