<script lang="ts">
  import { call, hasHost } from './lib/bridge'
  import { library } from './lib/library.svelte'
  import PlayerBar from './lib/PlayerBar.svelte'
  import { player } from './lib/player.svelte'
  import Sidebar from './lib/Sidebar.svelte'
  import TitleBar from './lib/TitleBar.svelte'
  import TrackList from './lib/TrackList.svelte'

  // Keep the native window border in sync with the theme background.
  if (hasHost) {
    const background = getComputedStyle(document.documentElement).getPropertyValue('--bg').trim()
    call('window.setBackground', { color: background })
  }

  const focusSearch = () => document.getElementById('search')?.focus()

  function onKeydown(e: KeyboardEvent) {
    const target = e.target as HTMLElement

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
    } else if (e.key === 'ArrowRight' || e.key === 'ArrowLeft') {
      e.preventDefault()
      player.seekBy((e.key === 'ArrowRight' ? 1 : -1) * (e.shiftKey ? 30 : 5))
    } else if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault()
      library.moveSelection(e.key === 'ArrowDown' ? 1 : -1)
    } else if (e.key === 'PageDown' || e.key === 'PageUp') {
      e.preventDefault()
      library.moveSelection(e.key === 'PageDown' ? 15 : -15)
    } else if (e.key === 'Home' || e.key === 'End') {
      e.preventDefault()
      library.select(e.key === 'Home' ? 0 : Math.max(library.total - 1, 0))
    } else if (e.key === 'Enter') {
      e.preventDefault()
      library.playSelected()
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
