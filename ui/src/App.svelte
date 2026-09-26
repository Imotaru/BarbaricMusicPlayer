<script lang="ts">
  import BpmEditor from './lib/BpmEditor.svelte'
  import { call, hasHost } from './lib/bridge'
  import CommandPalette from './lib/CommandPalette.svelte'
  import { canRun, commandById } from './lib/commands'
  import ConfirmDialog from './lib/ConfirmDialog.svelte'
  import ContextMenu from './lib/ContextMenu.svelte'
  import ImportReport from './lib/ImportReport.svelte'
  import InfoEditor from './lib/InfoEditor.svelte'
  import { chordOf, hasModifier } from './lib/keys'
  import { keymap } from './lib/keymap.svelte'
  import { library } from './lib/library.svelte'
  import MiniPlayer from './lib/MiniPlayer.svelte'
  import Picker from './lib/Picker.svelte'
  import PlayerBar from './lib/PlayerBar.svelte'
  import { playlists } from './lib/playlists.svelte'
  import { prefs, SIDEBAR_DEFAULT } from './lib/prefs.svelte'
  import SettingsDialog from './lib/SettingsDialog.svelte'
  import SkipsEditor from './lib/SkipsEditor.svelte'
  import Sidebar from './lib/Sidebar.svelte'
  import { applyTheme, themeById } from './lib/themes'
  import TitleBar from './lib/TitleBar.svelte'
  import Toast from './lib/Toast.svelte'
  import TrackList from './lib/TrackList.svelte'
  import { ui } from './lib/ui.svelte'

  // Theme tokens on the page, and the same background on the native window border.
  $effect(() => {
    const background = applyTheme(themeById(prefs.theme), prefs.accent)
    if (hasHost) call('window.setBackground', { color: background }).catch(() => {})
  })

  // A playlist deleted elsewhere (or from its own menu) can't stay open.
  $effect(() => {
    const view = library.view
    if ((view.kind === 'filter' || view.kind === 'manual') && playlists.loaded && !playlists.byId.has(view.id)) {
      library.openLibrary()
    }
  })

  /** Keys that move through the list. They can't be rebound. Returns whether the key was used. */
  function listKey(e: KeyboardEvent, chord: string): boolean {
    switch (chord.replace(/^Shift\+/, '')) {
      case 'ArrowDown':
      case 'ArrowUp':
        library.moveCursor(e.key === 'ArrowDown' ? 1 : -1, e.shiftKey)
        return true
      case 'PageDown':
      case 'PageUp':
        library.moveCursor(e.key === 'PageDown' ? 15 : -15, e.shiftKey)
        return true
      case 'Home':
      case 'End':
        library.moveCursor(e.key === 'Home' ? -library.total : library.total, e.shiftKey)
        return true
    }

    switch (chord) {
      case 'Enter':
        library.playSelected()
        return true
      case 'Ctrl+KeyA':
        library.selectAll()
        return true
      case 'Escape':
        if (!library.clearSelection() && library.hasFilter) library.clearFilter()
        return false
    }
    return false
  }

  function onKeydown(e: KeyboardEvent) {
    // Menus, pickers, dialogs and rename boxes handle their own keys.
    if (ui.overlayOpen || ui.renaming) return
    const chord = chordOf(e)
    if (!chord) return

    // Text fields handle their own keys (the search box has its own handler); shortcuts marked for it
    // still work there. A focused slider keeps its arrow and paging keys.
    const target = e.target as HTMLElement
    const inInput = target.matches('input:not([type="range"]), textarea, select')
    const onSlider = target.matches('input[type="range"]')
    if (onSlider && /^(Shift\+)?(Arrow(Up|Down|Left|Right)|Page(Up|Down)|Home|End)$/.test(chord)) return

    if (!inInput && !ui.compact && listKey(e, chord)) {
      e.preventDefault()
      return
    }

    const id = keymap.byChord.get(chord)
    const command = id ? commandById.get(id) : undefined
    if (!command || (inInput && !(command.inInputs && hasModifier(chord))) || !canRun(command)) return
    e.preventDefault()
    command.run()
  }

  // The sidebar follows a drag on its edge; the width is saved when the drag ends.
  let resizing = $state(false)
  const maxSidebar = () => Math.max(SIDEBAR_DEFAULT, innerWidth - 400)

  function startResize(e: PointerEvent) {
    if (e.button !== 0) return
    ;(e.currentTarget as HTMLElement).setPointerCapture(e.pointerId)
    resizing = true
  }

  function resize(e: PointerEvent) {
    if (resizing) prefs.setSidebarWidth(Math.min(e.clientX, maxSidebar()), false)
  }

  function endResize() {
    if (!resizing) return
    resizing = false
    prefs.setSidebarWidth(prefs.sidebarWidth)
  }

  function onResizerKeydown(e: KeyboardEvent) {
    if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight') return
    e.preventDefault()
    e.stopPropagation()
    const step = (e.shiftKey ? 40 : 10) * (e.key === 'ArrowRight' ? 1 : -1)
    prefs.setSidebarWidth(Math.min(prefs.sidebarWidth + step, maxSidebar()))
  }
</script>

<svelte:window onkeydown={onKeydown} />

{#if ui.compact}
  <MiniPlayer />
{:else}
  <div class="shell" class:resizing>
    <TitleBar />
    <div class="body" style:--sidebar-width="{prefs.sidebarWidth}px">
      <Sidebar />
      <div
        class="resizer"
        role="slider"
        aria-orientation="horizontal"
        aria-label="Sidebar width"
        aria-valuenow={prefs.sidebarWidth}
        tabindex="0"
        title="Drag to resize · double-click to reset"
        onpointerdown={startResize}
        onpointermove={resize}
        onpointerup={endResize}
        onpointercancel={endResize}
        ondblclick={() => prefs.setSidebarWidth(SIDEBAR_DEFAULT)}
        onkeydown={onResizerKeydown}
      ></div>
      <main>
        {#if !hasHost}
          <p class="notice">Running outside the app: the audio host isn't connected.</p>
        {/if}
        {#if library.error}
          <p class="notice error" role="alert">{library.error}</p>
        {/if}
        <TrackList />
      </main>
    </div>
    <PlayerBar />
  </div>
{/if}

{#if ui.menu}
  {#key ui.menu}
    <ContextMenu at={ui.menu} items={ui.menu.items} />
  {/key}
{/if}
{#if ui.picker}
  {#key ui.picker}
    <Picker mode={ui.picker.mode} trackIds={ui.picker.trackIds} at={ui.picker} />
  {/key}
{/if}
{#if ui.bpmEditor}
  {#key ui.bpmEditor}
    <BpmEditor trackIds={ui.bpmEditor.trackIds} at={ui.bpmEditor} />
  {/key}
{/if}
{#if ui.skipsEditor}
  {#key ui.skipsEditor}
    <SkipsEditor trackIds={ui.skipsEditor.trackIds} at={ui.skipsEditor} />
  {/key}
{/if}
{#if ui.infoEditor}
  {#key ui.infoEditor}
    <InfoEditor trackIds={ui.infoEditor.trackIds} at={ui.infoEditor} />
  {/key}
{/if}
{#if ui.confirm}
  {#key ui.confirm}
    <ConfirmDialog confirm={ui.confirm} />
  {/key}
{/if}
{#if ui.importReport}
  <ImportReport report={ui.importReport} />
{/if}
{#if ui.palette}
  <CommandPalette />
{/if}
{#if ui.settings}
  <SettingsDialog />
{/if}
<Toast />

<style>
  .shell {
    height: 100vh;
    display: grid;
    grid-template-rows: auto minmax(0, 1fr) auto;
  }

  .shell.resizing {
    cursor: col-resize;
    user-select: none;
  }

  .body {
    position: relative;
    display: grid;
    grid-template-columns: var(--sidebar-width) minmax(0, 1fr);
    min-height: 0;
  }

  /* Sits over the sidebar's right edge, wider than the line it shows so it's easy to grab. */
  .resizer {
    position: absolute;
    top: 0;
    bottom: 0;
    left: calc(var(--sidebar-width) - 3px);
    z-index: 5;
    width: 6px;
    cursor: col-resize;
    outline: 0;
  }

  .resizer:hover,
  .resizer:focus-visible,
  .resizing .resizer {
    background: linear-gradient(to right, transparent 2px, var(--accent) 2px, var(--accent) 4px, transparent 4px);
  }

  /* Notices stack above the list, which takes the rest of the height. */
  main {
    display: flex;
    flex-direction: column;
    min-height: 0;
    min-width: 0;
  }

  main > :global(:last-child) {
    flex: 1;
    min-height: 0;
  }

  .notice {
    margin: 12px 20px 0;
    padding: 8px 14px;
    border-radius: 10px;
    font-size: 12px;
    background: var(--surface);
    color: var(--text-dim);
  }

  .notice.error {
    background: color-mix(in srgb, var(--danger) 18%, transparent);
    color: var(--danger-text);
  }

  @media (max-width: 760px) {
    .body {
      grid-template-columns: minmax(0, 1fr);
    }

    .body > :global(.sidebar),
    .resizer {
      display: none;
    }
  }
</style>
