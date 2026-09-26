<script lang="ts">
  import { openBpmEditor, openPlaylistPicker, openTagPicker } from './lib/actions'
  import BpmEditor from './lib/BpmEditor.svelte'
  import { call, hasHost } from './lib/bridge'
  import ContextMenu from './lib/ContextMenu.svelte'
  import { library } from './lib/library.svelte'
  import Picker from './lib/Picker.svelte'
  import PlayerBar from './lib/PlayerBar.svelte'
  import { player } from './lib/player.svelte'
  import { playlists } from './lib/playlists.svelte'
  import Sidebar from './lib/Sidebar.svelte'
  import TitleBar from './lib/TitleBar.svelte'
  import Toast from './lib/Toast.svelte'
  import TrackList from './lib/TrackList.svelte'
  import { ui } from './lib/ui.svelte'

  // Keep the native window border in sync with the theme background.
  if (hasHost) {
    const background = getComputedStyle(document.documentElement).getPropertyValue('--bg').trim()
    call('window.setBackground', { color: background })
  }

  // A playlist deleted elsewhere (or from its own menu) can't stay open.
  $effect(() => {
    if (library.view.kind !== 'library' && playlists.loaded && !playlists.byId.has(library.view.id)) {
      library.openLibrary()
    }
  })

  const focusSearch = () => document.getElementById('search')?.focus()

  function onKeydown(e: KeyboardEvent) {
    const target = e.target as HTMLElement

    // Menus, pickers and rename boxes handle their own keys.
    if (ui.overlayOpen || ui.renaming) return

    if (e.ctrlKey && e.key.toLowerCase() === 'f') {
      e.preventDefault()
      focusSearch()
      return
    }
    if (e.ctrlKey && e.key.toLowerCase() === 'o') {
      e.preventDefault()
      player.openFile()
      return
    }

    // Text fields and sliders handle their own keys (the search box has its own handler).
    if (target.matches('input:not([type="range"]), textarea')) return
    const onSlider = target.matches('input[type="range"]')

    if (e.key === '/') {
      e.preventDefault()
      focusSearch()
    } else if (e.key === ' ') {
      e.preventDefault()
      if (player.loaded) player.toggle()
      else library.playSelected()
    } else if (e.ctrlKey && e.key === 'ArrowRight') {
      e.preventDefault()
      player.next()
    } else if (e.ctrlKey && e.key === 'ArrowLeft') {
      e.preventDefault()
      player.previous()
    } else if (onSlider) {
      return
    } else if (e.ctrlKey && e.key.toLowerCase() === 'a') {
      e.preventDefault()
      library.selectAll()
    } else if (e.altKey && (e.key === 'ArrowDown' || e.key === 'ArrowUp')) {
      e.preventDefault()
      library.moveSelected(e.key === 'ArrowDown' ? 1 : -1)
    } else if (e.key === 'ArrowRight' || e.key === 'ArrowLeft') {
      e.preventDefault()
      player.seekBy((e.key === 'ArrowRight' ? 1 : -1) * (e.shiftKey ? 30 : 5))
    } else if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault()
      library.moveCursor(e.key === 'ArrowDown' ? 1 : -1, e.shiftKey)
    } else if (e.key === 'PageDown' || e.key === 'PageUp') {
      e.preventDefault()
      library.moveCursor(e.key === 'PageDown' ? 15 : -15, e.shiftKey)
    } else if (e.key === 'Home' || e.key === 'End') {
      e.preventDefault()
      library.moveCursor(e.key === 'Home' ? -library.total : library.total, e.shiftKey)
    } else if (e.key === 'Enter') {
      e.preventDefault()
      library.playSelected()
    } else if (e.ctrlKey || e.altKey || e.metaKey) {
      return
    } else if (e.key === 't' || e.key === 'T') {
      e.preventDefault()
      openTagPicker()
    } else if (e.key === 'b' || e.key === 'B') {
      e.preventDefault()
      openBpmEditor()
    } else if (e.key === 'p' || e.key === 'P') {
      e.preventDefault()
      openPlaylistPicker()
    } else if (e.key === 'Delete') {
      e.preventDefault()
      library.removeSelectedFromPlaylist()
    } else if (e.key === 'Escape') {
      if (!library.clearSelection() && library.hasFilter) library.clearFilter()
    }
  }
</script>

<svelte:window onkeydown={onKeydown} />

<div class="shell">
  <TitleBar />
  <div class="body">
    <Sidebar />
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
<Toast />

<style>
  .shell {
    height: 100vh;
    display: grid;
    grid-template-rows: auto minmax(0, 1fr) auto;
  }

  .body {
    display: grid;
    grid-template-columns: 230px minmax(0, 1fr);
    min-height: 0;
  }

  main {
    display: grid;
    grid-template-rows: auto auto minmax(0, 1fr);
    min-height: 0;
    min-width: 0;
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
    background: color-mix(in srgb, #e5484d 18%, transparent);
    color: #ff9592;
  }

  @media (max-width: 760px) {
    .body {
      grid-template-columns: minmax(0, 1fr);
    }

    .body > :global(.sidebar) {
      display: none;
    }
  }
</style>
