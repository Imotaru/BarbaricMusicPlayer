<script lang="ts">
  import { call, hasHost, on } from './bridge'

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
