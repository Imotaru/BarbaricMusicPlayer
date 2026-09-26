// Theme presets. Each one sets every token app.css declares; `bg` must stay a 6-digit hex because it
// is also pushed to the native window border.

export interface Theme {
  id: string
  name: string
  scheme: 'dark' | 'light'
  tokens: {
    bg: string
    surface: string
    'surface-hover': string
    border: string
    text: string
    'text-dim': string
    accent: string
    track: string
    danger: string
    'danger-text': string
    popover: string
    shadow: string
    scrim: string
    /** What tag colours are mixed toward for text on a tinted chip: white on dark themes, black on light. */
    'chip-mix': string
  }
}

const darkShadow = '0 12px 32px rgb(0 0 0 / 0.45), 0 2px 6px rgb(0 0 0 / 0.3)'
const darkDanger = { danger: '#e5484d', 'danger-text': '#ff9592', scrim: 'rgb(0 0 0 / 0.4)', 'chip-mix': 'white', shadow: darkShadow }

export const THEMES: Theme[] = [
  {
    id: 'ember',
    name: 'Ember',
    scheme: 'dark',
    tokens: {
      bg: '#121214',
      surface: '#1b1b1f',
      'surface-hover': '#26262c',
      border: '#2c2c33',
      text: '#ececf1',
      'text-dim': '#8b8b96',
      accent: '#ff5a36',
      track: '#34343c',
      popover: '#202026',
      ...darkDanger,
    },
  },
  {
    id: 'graphite',
    name: 'Graphite',
    scheme: 'dark',
    tokens: {
      bg: '#161616',
      surface: '#1f1f1f',
      'surface-hover': '#2a2a2a',
      border: '#323232',
      text: '#ededed',
      'text-dim': '#8f8f8f',
      accent: '#3ec9a7',
      track: '#3a3a3a',
      popover: '#242424',
      ...darkDanger,
    },
  },
  {
    id: 'midnight',
    name: 'Midnight',
    scheme: 'dark',
    tokens: {
      bg: '#0e1320',
      surface: '#151c2c',
      'surface-hover': '#1f2940',
      border: '#26314a',
      text: '#e6ebf5',
      'text-dim': '#8792ab',
      accent: '#6b95ff',
      track: '#2c3854',
      popover: '#182033',
      ...darkDanger,
    },
  },
  {
    id: 'moss',
    name: 'Moss',
    scheme: 'dark',
    tokens: {
      bg: '#111512',
      surface: '#181e1a',
      'surface-hover': '#222b25',
      border: '#2a352e',
      text: '#e7eee8',
      'text-dim': '#8a978d',
      accent: '#a3d160',
      track: '#33403a',
      popover: '#1c231e',
      ...darkDanger,
    },
  },
  {
    id: 'paper',
    name: 'Paper',
    scheme: 'light',
    tokens: {
      bg: '#f4f3ef',
      surface: '#fbfaf7',
      'surface-hover': '#e9e7e1',
      border: '#dcd9d1',
      text: '#1d1c1a',
      'text-dim': '#6b6861',
      accent: '#d9480f',
      track: '#d5d2c9',
      danger: '#d93036',
      'danger-text': '#b4232a',
      popover: '#ffffff',
      shadow: '0 12px 32px rgb(0 0 0 / 0.14), 0 2px 6px rgb(0 0 0 / 0.08)',
      scrim: 'rgb(0 0 0 / 0.25)',
      'chip-mix': 'black',
    },
  },
]

export const DEFAULT_THEME = THEMES[0]

export const themeById = (id: string) => THEMES.find((t) => t.id === id) ?? DEFAULT_THEME

export const isHexColor = (value: unknown): value is string => typeof value === 'string' && /^#[0-9a-f]{6}$/i.test(value)

/** Black or white, whichever reads better on the colour (WCAG contrast). */
export function contrastOn(hex: string) {
  const [r, g, b] = [1, 3, 5].map((i) => {
    const c = parseInt(hex.slice(i, i + 2), 16) / 255
    return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4
  })
  const luminance = 0.2126 * r + 0.7152 * g + 0.0722 * b
  return (luminance + 0.05) / 0.05 >= 1.05 / (luminance + 0.05) ? '#141414' : '#ffffff'
}

/** Sets the theme's tokens on the page and returns the background colour for the window border. */
export function applyTheme(theme: Theme, accent: string | null) {
  const root = document.documentElement.style
  const tokens = { ...theme.tokens, accent: accent ?? theme.tokens.accent }
  for (const [name, value] of Object.entries(tokens)) root.setProperty(`--${name}`, value)
  root.setProperty('--accent-contrast', contrastOn(tokens.accent))
  root.setProperty('color-scheme', theme.scheme)
  root.background = tokens.bg
  return tokens.bg
}
