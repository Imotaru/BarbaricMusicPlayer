<script lang="ts">
  import { call, hasHost, on } from './bridge'
  import { keymap } from './keymap.svelte'
  import { ui } from './ui.svelte'

  let maximized = $state(false)

  if (hasHost) {
    call<{ maximized: boolean }>('window.getState').then((s) => (maximized = s.maximized))
    on<{ maximized: boolean }>('window.state', (s) => (maximized = s.maximized))
  }

  const command = (method: string) => hasHost && call(method)
</script>

<header class="titlebar">
  <span class="brand">Barbaric</span>
  <div class="controls">
    <button class="tool" aria-label={keymap.titled('Command palette', 'app.palette')} title={keymap.titled('Commands', 'app.palette')} onclick={() => ui.openPalette()}>
      <svg viewBox="0 0 12 12"><path d="M2 3.5h8M2 6h8M2 8.5h5" /></svg>
    </button>
    <button class="tool" aria-label={keymap.titled('Settings', 'app.settings')} title={keymap.titled('Settings', 'app.settings')} onclick={() => ui.openSettings()}>
      <svg viewBox="0 0 12 12"><circle cx="6" cy="6" r="1.8" /><path d="M6 1v1.6M6 9.4V11M1 6h1.6M9.4 6H11M2.5 2.5l1.1 1.1M8.4 8.4l1.1 1.1M2.5 9.5l1.1-1.1M8.4 3.6l1.1-1.1" /></svg>
    </button>
    <button class="tool" aria-label={keymap.titled('Mini player', 'window.compact')} title={keymap.titled('Mini player', 'window.compact')} onclick={() => ui.setCompact(true)}>
      <svg viewBox="0 0 12 12"><path d="M1 2h10v8H1zM6 6.5h4v3H6z" /></svg>
    </button>
    <span class="divider" aria-hidden="true"></span>
    <button aria-label="Minimize" onclick={() => command('window.minimize')}>
      <svg viewBox="0 0 10 10"><path d="M1 5h8" /></svg>
    </button>
    <button aria-label={maximized ? 'Restore' : 'Maximize'} onclick={() => command('window.toggleMaximize')}>
      {#if maximized}
        <svg viewBox="0 0 10 10"><path d="M3 1h6v6M1 3h6v6H1z" /></svg>
      {:else}
        <svg viewBox="0 0 10 10"><path d="M1 1h8v8H1z" /></svg>
      {/if}
    </button>
    <button class="close" aria-label="Close" onclick={() => command('window.close')}>
      <svg viewBox="0 0 10 10"><path d="M1 1l8 8M9 1l-8 8" /></svg>
    </button>
  </div>
</header>

<style>
  .titlebar {
    display: flex;
    align-items: center;
    height: var(--titlebar-height);
    padding-left: 14px;
    -webkit-app-region: drag;
    user-select: none;
  }

  .brand {
    font-size: 12px;
    font-weight: 700;
    letter-spacing: 0.18em;
    text-transform: uppercase;
    color: var(--text-dim);
  }

  .controls {
    display: flex;
    margin-left: auto;
    height: 100%;
    -webkit-app-region: no-drag;
  }

  button {
    width: 46px;
    height: 100%;
    display: grid;
    place-items: center;
    border: 0;
    background: transparent;
    color: var(--text-dim);
  }

  button:hover {
    background: var(--surface-hover);
    color: var(--text);
  }

  button.tool {
    width: 38px;
  }

  button.tool svg {
    width: 12px;
    height: 12px;
  }

  .divider {
    align-self: center;
    width: 1px;
    height: 14px;
    margin: 0 4px;
    background: var(--border);
  }

  button.close:hover {
    background: #c42b1c;
    color: #fff;
  }

  svg {
    width: 10px;
    height: 10px;
    fill: none;
    stroke: currentColor;
    stroke-width: 1;
  }
</style>
