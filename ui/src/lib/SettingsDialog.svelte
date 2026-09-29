<script lang="ts">
  import { exportBackup, importBackup } from './backup'
  import { commandById, COMMANDS, type Command } from './commands'
  import { chordLabel, chordOf, RESERVED } from './keys'
  import { keymap } from './keymap.svelte'
  import { player } from './player.svelte'
  import { prefs } from './prefs.svelte'
  import { THEMES, themeById } from './themes'
  import { ui, type SettingsTab } from './ui.svelte'

  const tabs: { id: SettingsTab; label: string }[] = [
    { id: 'appearance', label: 'Appearance' },
    { id: 'playback', label: 'Playback' },
    { id: 'keyboard', label: 'Keyboard' },
    { id: 'backup', label: 'Backup' },
  ]

  /** Accents offered next to the colour picker: every theme's own, so any theme can wear another's. */
  const accents = [...new Set(THEMES.map((t) => t.tokens.accent))]

  const theme = $derived(themeById(prefs.theme))
  const accent = $derived(prefs.accent ?? theme.tokens.accent)

  const groups = [...new Set(COMMANDS.map((c) => c.group))].map((group) => ({
    group,
    commands: COMMANDS.filter((c) => c.group === group && (!c.hidden || c.global || c.id === 'app.palette')),
  }))

  let dialog = $state<HTMLDivElement>()
  /** The shortcut being recorded, and whether it's an in-app or a global one. */
  let recording = $state<{ id: string; global: boolean } | null>(null)
  let message = $state<{ id: string; text: string; error?: boolean } | null>(null)

  $effect(() => {
    dialog?.focus()
  })

  // While recording, the host lets go of its global hotkeys so every key reaches the recorder.
  $effect(() => {
    if (!recording) return
    keymap.suspendGlobals(true)
    return () => keymap.suspendGlobals(false)
  })

  function record(id: string, global: boolean) {
    message = null
    recording = { id, global }
  }

  async function onRecordKey(e: KeyboardEvent) {
    if (!recording) return
    e.preventDefault()
    e.stopPropagation()
    if (e.key === 'Escape') {
      recording = null
      return
    }

    const chord = chordOf(e)
    if (!chord) return
    const { id, global } = recording
    recording = null

    if (global) {
      const result = await keymap.setGlobal(id, chord)
      message =
        result === 'ok'
          ? null
          : result === 'inUse'
            ? { id, text: `${chordLabel(chord)} is taken by another app. Try another.`, error: true }
            : { id, text: 'Use Ctrl+Alt, Ctrl+Shift or Win with a key, or F13–F24.', error: true }
      return
    }

    if (RESERVED.has(chord)) {
      message = { id, text: `${chordLabel(chord)} is used for moving around the list.`, error: true }
      return
    }

    const previous = keymap.add(id, chord)
    if (previous) message = { id, text: `Moved ${chordLabel(chord)} from “${commandById.get(previous)?.label}”.` }
  }

  function onKeydown(e: KeyboardEvent) {
    if (recording) {
      onRecordKey(e)
      return
    }

    e.stopPropagation()
    if (e.key === 'Escape') {
      e.preventDefault()
      ui.closeSettings()
    }
  }

  /** In-app chords that a global hotkey takes over: the system delivers those to the hotkey, never to the page. */
  const shadowed = $derived(new Set(keymap.globals.map((g) => g.keys).filter((k): k is string => k !== null)))

  function setVolumeLimit(e: Event & { currentTarget: HTMLInputElement }) {
    const percent = e.currentTarget.valueAsNumber
    if (Number.isFinite(percent)) player.setVolumeLimit(Math.round(percent) / 100)
    // Shows what was kept, also when a typed value was out of range or unchanged after clamping.
    e.currentTarget.value = String(Math.round(player.volumeLimit * 100))
  }

  const isRecording = (command: Command, global: boolean) =>
    recording?.id === command.id && recording.global === global
</script>

<!-- svelte-ignore a11y_click_events_have_key_events, a11y_no_static_element_interactions -->
<div class="scrim" onclick={(e) => e.target === e.currentTarget && !recording && ui.closeSettings()}>
  <div
    class="settings popover"
    role="dialog"
    aria-modal="true"
    aria-label="Settings"
    tabindex="-1"
    bind:this={dialog}
    onkeydown={onKeydown}
  >
    <header>
      <div class="tabs" role="tablist">
        {#each tabs as tab (tab.id)}
          <button
            role="tab"
            aria-selected={ui.settings === tab.id}
            class:active={ui.settings === tab.id}
            onclick={() => ((ui.settings = tab.id), (recording = null))}
          >
            {tab.label}
          </button>
        {/each}
      </div>
      <button class="close" aria-label="Close settings" onclick={ui.closeSettings}>
        <svg viewBox="0 0 10 10"><path d="M1 1l8 8M9 1l-8 8" /></svg>
      </button>
    </header>

    <div class="content">
      {#if ui.settings === 'appearance'}
        <section>
          <h3>Theme</h3>
          <div class="themes">
            {#each THEMES as t (t.id)}
              <button
                class="theme"
                class:active={t.id === prefs.theme}
                aria-pressed={t.id === prefs.theme}
                style:--t-bg={t.tokens.bg}
                style:--t-surface={t.tokens.surface}
                style:--t-text={t.tokens.text}
                style:--t-accent={prefs.accent ?? t.tokens.accent}
                onclick={() => prefs.setTheme(t.id)}
              >
                <span class="preview" aria-hidden="true">
                  <span class="bar"></span>
                  <span class="line"></span>
                  <span class="line short"></span>
                  <span class="dot"></span>
                </span>
                <span class="name">{t.name}</span>
              </button>
            {/each}
          </div>
        </section>

        <section>
          <h3>Accent</h3>
          <div class="accents">
            {#each accents as color (color)}
              <button
                class="swatch"
                class:active={color === accent}
                style:--c={color}
                aria-label="Accent {color}"
                aria-pressed={color === accent}
                onclick={() => prefs.setAccent(color === theme.tokens.accent ? null : color)}
              ></button>
            {/each}
            <label class="custom" title="Pick any colour">
              <input
                type="color"
                value={accent}
                oninput={(e) => prefs.setAccent(e.currentTarget.value, false)}
                onchange={(e) => prefs.setAccent(e.currentTarget.value)}
              />
              Custom…
            </label>
            {#if prefs.accent}
              <button class="link" onclick={() => prefs.setAccent(null)}>Use the theme's accent</button>
            {/if}
          </div>
        </section>

        <section>
          <h3>Song list</h3>
          <label class="check">
            <input type="checkbox" checked={prefs.showSkips} onchange={(e) => prefs.setShowSkips(e.currentTarget.checked)} />
            Show how often songs were skipped
          </label>
          <p class="intro">
            Turn this off to hide the Skips column. Suggested for removal still shows it, since that's what the list is
            about.
          </p>
          <p class="intro">Drag the lines between column headers to resize the columns; they keep that width.</p>
          <button class="action" disabled={Object.keys(prefs.columnWidths).length === 0} onclick={prefs.resetColumnWidths}>
            Reset column widths
          </button>
        </section>

        <section>
          <h3>Mini player</h3>
          <p class="intro">Drag the mini player's edges to resize it; it keeps that size.</p>
          <button class="action" disabled={!ui.compactResized} onclick={ui.resetCompactSize}>Reset mini player size</button>
        </section>
      {:else if ui.settings === 'playback'}
        <section>
          <h3>Volume</h3>
          <label class="check">
            <input type="checkbox" checked={player.normalize} onchange={(e) => player.setNormalize(e.currentTarget.checked)} />
            Play every song equally loud
          </label>
          <p class="intro">
            Each song's volume is measured once, in the background, by how loud its loud parts get, so a quiet intro
            doesn't fool it. Quiet songs are only turned up as far as they can go without distorting. The Song slider
            still adjusts a song from there.
          </p>
          <label class="limit">
            Highest volume
            <input
              type="number"
              min="1"
              max="100"
              step="1"
              value={Math.round(player.volumeLimit * 100)}
              onchange={setVolumeLimit}
            />
            %
          </label>
          <p class="intro">
            The Master slider goes up to this, so the volumes you use get the whole slider. Volume keys step in
            proportion.
          </p>
        </section>
        <section>
          <h3>Shuffle</h3>
          <label class="check">
            <input type="checkbox" checked={player.weighBySkips} onchange={(e) => player.setWeighBySkips(e.currentTarget.checked)} />
            Play songs I skip less often
          </label>
          <p class="intro">
            Songs you tend to skip come up later in the shuffle. Even a song you always skip still turns up now and then.
          </p>
          <label class="check">
            <input type="checkbox" checked={player.weighByLength} onchange={(e) => player.setWeighByLength(e.currentTarget.checked)} />
            Play long songs less often
          </label>
          <p class="intro">
            A song comes up in proportion to how short it is: a 1-minute song ten times as often as a 10-minute one, so
            one long song doesn't take over a playlist. Long songs sit out some rounds of the shuffle instead.
          </p>
        </section>
      {:else if ui.settings === 'backup'}
        <section>
          <h3>Export</h3>
          <p class="intro">
            Saves your tags, playlists and every song's info, BPM, volume, plays and skips to a file, along with your
            theme, shortcuts and volume. Songs are recognised by their content, so the backup still fits after files are
            moved or renamed, or on another PC.
          </p>
          <button class="action" onclick={exportBackup}>Export backup…</button>
        </section>

        <section>
          <h3>Import</h3>
          <p class="intro">
            Merges a backup into this library. For songs, tags and playlists in both, the backup's version wins; nothing
            else is removed. Songs whose file can't be found are listed by file name, and come back when a scan finds them
            in one of your music folders.
          </p>
          <button class="action" onclick={importBackup}>Import backup…</button>
        </section>
      {:else}
        <p class="intro">
          Click a shortcut's <b>+</b> and press the keys. Global hotkeys work even while another app is in front;
          media keys already do.
        </p>
        <div class="keys" role="table" aria-label="Keyboard shortcuts">
          <div class="row head" role="row">
            <span role="columnheader">Command</span>
            <span role="columnheader">In the app</span>
            <span role="columnheader">Global</span>
          </div>
          {#each groups as { group, commands } (group)}
            <div class="group" role="row"><span role="rowheader">{group}</span></div>
            {#each commands as command (command.id)}
              {@const global = keymap.globalFor(command.id)}
              <div class="row" role="row">
                <span class="label" role="cell">
                  {command.label}
                  {#if message?.id === command.id}
                    <small class:error={message.error}>{message.text}</small>
                  {/if}
                </span>
                <span class="chords" role="cell">
                  {#if command.id !== 'window.show'}
                    {#each keymap.bindings(command.id) as chord (chord)}
                      <span class="chord" class:shadowed={shadowed.has(chord)} title={shadowed.has(chord) ? 'A global hotkey takes this over' : undefined}>
                        {chordLabel(chord, keymap.layout)}
                        <button aria-label="Remove {chordLabel(chord)}" onclick={() => keymap.remove(command.id, chord)}>×</button>
                      </span>
                    {/each}
                    <button class="add" class:recording={isRecording(command, false)} onclick={() => record(command.id, false)}>
                      {isRecording(command, false) ? 'Press keys…' : '+'}
                    </button>
                    {#if !keymap.isDefault(command.id)}
                      <button class="link" onclick={() => keymap.reset(command.id)}>Reset</button>
                    {/if}
                  {/if}
                </span>
                <span class="chords" role="cell">
                  {#if command.global}
                    {#if global?.keys}
                      <span class="chord" class:error={global.inUse} title={global.inUse ? 'Another app holds this hotkey' : undefined}>
                        {chordLabel(global.keys, keymap.layout)}
                        <button aria-label="Remove global {chordLabel(global.keys)}" onclick={() => keymap.setGlobal(command.id, null)}>×</button>
                      </span>
                    {/if}
                    <button class="add" class:recording={isRecording(command, true)} onclick={() => record(command.id, true)}>
                      {isRecording(command, true) ? 'Press keys…' : global?.keys ? 'Change' : '+'}
                    </button>
                  {/if}
                </span>
              </div>
            {/each}
          {/each}
        </div>
        {#if Object.keys(prefs.keys).length > 0}
          <button class="link reset-all" onclick={prefs.resetKeys}>Reset all in-app shortcuts</button>
        {/if}
      {/if}
    </div>
  </div>
</div>

<style>
  .scrim {
    position: fixed;
    inset: 0;
    z-index: 60;
    display: grid;
    /* One row the scrim's height, so the dialog's max-height of 100% means the window, not its own content. */
    grid-template-rows: minmax(0, 1fr);
    place-items: center;
    padding: 16px;
    background: var(--scrim);
  }

  .settings {
    display: flex;
    flex-direction: column;
    width: min(680px, 100%);
    max-height: min(640px, 100%);
    outline: 0;
  }

  header {
    display: flex;
    align-items: center;
    padding: 10px 10px 0 14px;
    border-bottom: 1px solid var(--border);
  }

  .tabs {
    display: flex;
    gap: 4px;
  }

  .tabs button {
    height: 34px;
    padding: 0 12px;
    border: 0;
    border-bottom: 2px solid transparent;
    background: transparent;
    color: var(--text-dim);
    font-size: 13px;
    font-weight: 600;
  }

  .tabs button.active {
    border-bottom-color: var(--accent);
    color: var(--text);
  }

  .close {
    margin-left: auto;
    width: 30px;
    height: 30px;
    display: grid;
    place-items: center;
    border: 0;
    border-radius: 6px;
    background: transparent;
    color: var(--text-dim);
  }

  .close:hover {
    background: var(--surface-hover);
    color: var(--text);
  }

  .close svg {
    width: 10px;
    height: 10px;
    fill: none;
    stroke: currentColor;
    stroke-width: 1.2;
  }

  .content {
    overflow-y: auto;
    padding: 14px 16px 16px;
  }

  section + section {
    margin-top: 18px;
  }

  h3 {
    margin: 0 0 10px;
    font-size: 11px;
    font-weight: 700;
    letter-spacing: 0.08em;
    text-transform: uppercase;
    color: var(--text-dim);
  }

  .themes {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(112px, 1fr));
    gap: 10px;
  }

  .theme {
    display: grid;
    gap: 6px;
    padding: 6px;
    border: 1px solid var(--border);
    border-radius: 10px;
    background: transparent;
    color: var(--text);
    text-align: left;
  }

  .theme.active {
    border-color: var(--accent);
    box-shadow: 0 0 0 1px var(--accent);
  }

  .preview {
    position: relative;
    display: grid;
    align-content: start;
    gap: 5px;
    height: 58px;
    padding: 18px 8px 8px 30px;
    border-radius: 6px;
    background: var(--t-bg);
    overflow: hidden;
  }

  .preview .bar {
    position: absolute;
    inset: 0 auto 0 0;
    width: 22px;
    background: var(--t-surface);
  }

  .preview .line {
    height: 5px;
    border-radius: 3px;
    background: var(--t-text);
    opacity: 0.8;
  }

  .preview .line.short {
    width: 60%;
    opacity: 0.4;
  }

  .preview .dot {
    position: absolute;
    right: 8px;
    bottom: 8px;
    width: 14px;
    height: 14px;
    border-radius: 50%;
    background: var(--t-accent);
  }

  .theme .name {
    padding: 0 2px 2px;
    font-size: 12px;
    font-weight: 600;
  }

  .accents {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 10px;
  }

  .swatch {
    width: 26px;
    height: 26px;
    padding: 0;
    border: 2px solid transparent;
    border-radius: 50%;
    background: var(--c) content-box;
  }

  .swatch.active {
    border-color: var(--text);
  }

  .custom {
    display: flex;
    align-items: center;
    gap: 8px;
    font-size: 12px;
    color: var(--text-dim);
    cursor: pointer;
  }

  .custom input {
    width: 28px;
    height: 28px;
    padding: 0;
    border: 1px solid var(--border);
    border-radius: 6px;
    background: transparent;
    cursor: pointer;
  }

  .link {
    padding: 0;
    border: 0;
    background: none;
    color: var(--accent);
    font-size: 12px;
  }

  .link:hover {
    text-decoration: underline;
  }

  .check {
    display: flex;
    align-items: center;
    gap: 8px;
    margin-bottom: 8px;
    font-size: 13px;
  }

  .limit {
    display: flex;
    align-items: center;
    gap: 6px;
    margin-bottom: 8px;
    font-size: 13px;
  }

  .limit input {
    width: 56px;
    height: 26px;
    margin-left: 4px;
    padding: 0 6px;
    border: 1px solid var(--border);
    border-radius: 6px;
    background: var(--bg);
    color: var(--text);
    font: inherit;
  }

  .limit input:focus {
    outline: 0;
    border-color: var(--accent);
  }

  .check input {
    margin: 0;
    accent-color: var(--accent);
  }

  .intro {
    margin: 0 0 12px;
    font-size: 12px;
    line-height: 1.5;
    color: var(--text-dim);
  }

  .row {
    display: grid;
    grid-template-columns: minmax(0, 1.2fr) minmax(0, 1.3fr) minmax(0, 1fr);
    align-items: center;
    gap: 10px;
    min-height: 34px;
    padding: 3px 0;
    border-bottom: 1px solid color-mix(in srgb, var(--border) 60%, transparent);
    font-size: 13px;
  }

  .row.head {
    min-height: 0;
    padding-bottom: 6px;
    font-size: 11px;
    color: var(--text-dim);
  }

  .group {
    padding: 14px 0 4px;
    font-size: 11px;
    font-weight: 700;
    letter-spacing: 0.08em;
    text-transform: uppercase;
    color: var(--text-dim);
  }

  .label {
    display: grid;
    gap: 2px;
  }

  small {
    font-size: 11px;
    color: var(--text-dim);
  }

  small.error {
    color: var(--danger-text);
  }

  .chords {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 6px;
  }

  .chord {
    display: inline-flex;
    align-items: center;
    gap: 2px;
    height: 24px;
    padding: 0 2px 0 8px;
    border: 1px solid var(--border);
    border-radius: 5px;
    background: var(--bg);
    font-size: 12px;
    white-space: nowrap;
  }

  .chord.shadowed {
    border-style: dashed;
    color: var(--text-dim);
  }

  .chord.error {
    border-color: var(--danger);
    color: var(--danger-text);
  }

  .chord button {
    width: 18px;
    height: 18px;
    padding: 0;
    border: 0;
    border-radius: 3px;
    background: transparent;
    color: var(--text-dim);
    line-height: 1;
  }

  .chord button:hover {
    background: var(--surface-hover);
    color: var(--text);
  }

  .add {
    height: 24px;
    min-width: 24px;
    padding: 0 8px;
    border: 1px dashed var(--border);
    border-radius: 5px;
    background: transparent;
    color: var(--text-dim);
    font-size: 12px;
  }

  .add:hover {
    color: var(--text);
    border-color: var(--text-dim);
  }

  .add.recording {
    border-style: solid;
    border-color: var(--accent);
    color: var(--accent);
  }

  .reset-all {
    margin-top: 14px;
  }

  .action {
    height: 30px;
    padding: 0 14px;
    border: 1px solid var(--border);
    border-radius: 999px;
    background: transparent;
    color: var(--text);
    font-size: 13px;
    font-weight: 600;
  }

  .action:disabled {
    opacity: 0.45;
  }

  .action:hover:not(:disabled) {
    background: var(--surface-hover);
  }
</style>
