<script lang="ts">
  import { call, hasHost } from './lib/bridge'
  import NowPlaying from './lib/NowPlaying.svelte'
  import { player } from './lib/player.svelte'
  import TitleBar from './lib/TitleBar.svelte'

  // Keep the native window border in sync with the theme background.
  if (hasHost) {
    const background = getComputedStyle(document.documentElement).getPropertyValue('--bg').trim()
    call('window.setBackground', { color: background })
  }

  function onKeydown(e: KeyboardEvent) {
    const target = e.target as HTMLElement
    if (target.matches('input[type="text"], input[type="search"], textarea')) return

    if (e.ctrlKey && e.key.toLowerCase() === 'o') {
      e.preventDefault()
      player.openFile()
    } else if (e.key === ' ') {
      e.preventDefault()
      player.toggle()
    } else if (e.key === 'ArrowRight') {
      e.preventDefault()
      player.seekBy(e.shiftKey ? 30 : 5)
    } else if (e.key === 'ArrowLeft') {
      e.preventDefault()
      player.seekBy(e.shiftKey ? -30 : -5)
    }
  }
</script>

<svelte:window onkeydown={onKeydown} />

<div class="shell">
  <TitleBar />
  <main>
    {#if !hasHost}
      <p class="notice">Running outside the app: the audio host isn't connected.</p>
    {/if}
    <NowPlaying />
  </main>
</div>

<style>
  .shell {
    height: 100vh;
    display: grid;
    grid-template-rows: auto 1fr;
  }

  main {
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    gap: 24px;
    padding: 24px 32px 48px;
    overflow: auto;
  }

  .notice {
    margin: 0;
    padding: 8px 14px;
    border-radius: 999px;
    font-size: 12px;
    background: var(--surface);
    color: var(--text-dim);
  }
</style>
