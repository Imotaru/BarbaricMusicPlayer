interface Window {
  /** Set by the host before the page loads: the saved UI settings, window background and mini-player state. */
  __barbaricInitial?: import('./lib/prefs.svelte').Initial
}

interface Navigator {
  /** Chromium's Keyboard API, used for layout-correct shortcut labels. */
  keyboard?: { getLayoutMap(): Promise<Map<string, string>> }
}
