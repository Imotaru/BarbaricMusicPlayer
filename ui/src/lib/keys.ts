// Keyboard chords as text, e.g. "Ctrl+Shift+ArrowRight", "KeyT", "Ctrl+,".
// Letters, digits and named keys use `KeyboardEvent.code`, so a shortcut stays on the same physical key
// whatever the layout. Punctuation uses the character the layout produced, since its keys move around.

/** The default in-app shortcuts, by command id (see commands.ts). */
export const DEFAULT_KEYS: Record<string, string[]> = {
  'player.toggle': ['Space'],
  'player.next': ['Ctrl+ArrowRight'],
  'player.previous': ['Ctrl+ArrowLeft'],
  'player.seekForward': ['ArrowRight'],
  'player.seekBack': ['ArrowLeft'],
  'player.seekForwardLong': ['Shift+ArrowRight'],
  'player.seekBackLong': ['Shift+ArrowLeft'],
  'player.volumeUp': ['Ctrl+ArrowUp'],
  'player.volumeDown': ['Ctrl+ArrowDown'],
  'player.shuffle': ['KeyS'],
  'player.loop': ['KeyL'],
  'player.openFile': ['Ctrl+KeyO'],
  'library.search': ['Ctrl+KeyF', '/'],
  'selection.edit': ['KeyE'],
  'selection.tag': ['KeyT'],
  'selection.bpm': ['KeyB'],
  'selection.playlist': ['KeyP'],
  'selection.hide': ['KeyH'],
  'selection.keep': ['KeyK'],
  'selection.delete': ['Delete'],
  'selection.moveUp': ['Alt+ArrowUp'],
  'selection.moveDown': ['Alt+ArrowDown'],
  'app.palette': ['Ctrl+KeyK'],
  'app.settings': ['Ctrl+,'],
  'window.compact': ['Ctrl+KeyM'],
}

/** Keys the list, the focus order or the system own; they can't be given to a command. */
export const RESERVED = new Set([
  'ArrowUp',
  'ArrowDown',
  'Shift+ArrowUp',
  'Shift+ArrowDown',
  'PageUp',
  'PageDown',
  'Shift+PageUp',
  'Shift+PageDown',
  'Home',
  'End',
  'Shift+Home',
  'Shift+End',
  'Enter',
  'Escape',
  'Tab',
  'Shift+Tab',
  'Ctrl+KeyA',
  'F12',
  'Alt+F4',
])

const CODE_KEYS =
  /^(Key[A-Z]|Digit\d|Numpad\d|F\d{1,2}|Arrow(Up|Down|Left|Right)|Space|Enter|Escape|Tab|Backspace|Delete|Insert|Home|End|PageUp|PageDown)$/

const MODIFIER_KEYS = new Set(['Control', 'Shift', 'Alt', 'Meta', 'AltGraph', 'CapsLock'])

/** The chord a key press makes, or null for a lone modifier. */
export function chordOf(e: KeyboardEvent): string | null {
  if (MODIFIER_KEYS.has(e.key)) return null
  let key = e.key
  let shift = e.shiftKey
  if (CODE_KEYS.test(e.code)) key = e.code
  else if (e.key.length === 1) {
    // Shift is part of how some layouts type the character (e.g. "/" on German keyboards), not a modifier.
    key = e.key.toLowerCase()
    shift = false
  }

  const parts: string[] = []
  if (e.ctrlKey) parts.push('Ctrl')
  if (e.altKey) parts.push('Alt')
  if (shift) parts.push('Shift')
  if (e.metaKey) parts.push('Win')
  parts.push(key)
  return parts.join('+')
}

export const hasModifier = (chord: string) => /^(Ctrl|Alt|Win)\+/.test(chord)

const NAMES: Record<string, string> = {
  ArrowUp: '↑',
  ArrowDown: '↓',
  ArrowLeft: '←',
  ArrowRight: '→',
  Delete: 'Del',
  Escape: 'Esc',
  PageUp: 'PgUp',
  PageDown: 'PgDn',
  Backspace: 'Bksp',
  Insert: 'Ins',
}

/** How a chord reads to people, e.g. "Ctrl+→". `layout` maps codes to what the user's keyboard prints on them. */
export function chordLabel(chord: string, layout?: Map<string, string> | null) {
  return chord
    .split(/\+(?=.)/)
    .map((part, i, parts) => {
      if (i < parts.length - 1) return part
      const printed = layout?.get(part)
      if (printed && /^(Key|Digit)/.test(part)) return printed.toUpperCase()
      if (/^Key[A-Z]$/.test(part)) return part.slice(3)
      if (/^Digit\d$/.test(part)) return part.slice(5)
      if (/^Numpad\d$/.test(part)) return `Num ${part.slice(6)}`
      return NAMES[part] ?? part
    })
    .join('+')
}
