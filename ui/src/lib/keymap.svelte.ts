import { call, hasHost } from './bridge'
import { DEFAULT_KEYS, chordLabel } from './keys'
import { prefs } from './prefs.svelte'

/** A system-wide shortcut the host registered (or couldn't, because another app holds it). */
export interface GlobalBinding {
  id: string
  keys: string | null
  inUse: boolean
}

export type GlobalResult = 'ok' | 'inUse' | 'invalid'

/** Which shortcuts do what: the defaults with the user's overrides on top, plus the global hotkeys. */
class Keymap {
  /** What the user's keyboard prints on each key, for labels. */
  layout = $state.raw<Map<string, string> | null>(null)
  globals = $state<GlobalBinding[]>([])

  /** The command each chord runs. When two commands claim a chord, the first one listed wins. */
  readonly byChord = $derived.by(() => {
    const map = new Map<string, string>()
    for (const id of new Set([...Object.keys(DEFAULT_KEYS), ...Object.keys(prefs.keys)])) {
      for (const chord of this.bindings(id)) if (!map.has(chord)) map.set(chord, id)
    }
    return map
  })

  constructor() {
    navigator.keyboard
      ?.getLayoutMap()
      .then((map) => (this.layout = map))
      .catch(() => {})
    if (hasHost) call<GlobalBinding[]>('hotkeys.getGlobal').then((g) => (this.globals = g))
  }

  bindings(id: string): string[] {
    return prefs.keys[id] ?? DEFAULT_KEYS[id] ?? []
  }

  isDefault(id: string) {
    return prefs.keys[id] === undefined
  }

  /** The first shortcut of a command as people read it, e.g. "Ctrl+→", or undefined when it has none. */
  label(id: string) {
    const chord = this.bindings(id)[0]
    return chord === undefined ? undefined : chordLabel(chord, this.layout)
  }

  /** "Name (shortcut)", for tooltips and aria labels. */
  titled(name: string, id: string) {
    const label = this.label(id)
    return label ? `${name} (${label})` : name
  }

  /** Gives a chord to a command, taking it away from whichever command had it. Returns that command's id. */
  add(id: string, chord: string): string | undefined {
    const previous = this.byChord.get(chord)
    if (previous === id) return undefined
    const changes: Record<string, string[] | null> = { [id]: [...this.bindings(id), chord] }
    if (previous) changes[previous] = this.bindings(previous).filter((c) => c !== chord)
    prefs.setKeys(changes)
    return previous
  }

  remove(id: string, chord: string) {
    prefs.setKeys({ [id]: this.bindings(id).filter((c) => c !== chord) })
  }

  reset(id: string) {
    prefs.setKeys({ [id]: null })
  }

  globalFor(id: string) {
    return this.globals.find((g) => g.id === id)
  }

  async setGlobal(id: string, keys: string | null): Promise<GlobalResult> {
    const reply = await call<{ result: GlobalResult; state: GlobalBinding[] }>('hotkeys.setGlobal', { id, keys })
    this.globals = reply.state
    return reply.result
  }

  /** While a shortcut is being recorded, the global hotkeys let go so the recorder sees every key. */
  suspendGlobals(on: boolean) {
    if (hasHost) call('hotkeys.suspend', { on }).catch(() => {})
  }
}

export const keymap = new Keymap()
