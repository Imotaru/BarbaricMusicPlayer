<script lang="ts">
  import { call, hasHost } from './bridge'
  import { keymap } from './keymap.svelte'
  import { library } from './library.svelte'
  import { formatTime, player } from './player.svelte'
  import { ui } from './ui.svelte'

  let scrubbing = $state<number | null>(null)
  const shownPosition = $derived(scrubbing ?? player.position)
  const progress = $derived(player.duration > 0 ? (shownPosition / player.duration) * 100 : 0)

  const playOrToggle = () => (player.loaded ? player.toggle() : library.playSelected())
</script>

<!-- The whole mini-player drags the window; its controls opt out. -->
<div class="mini">
  <div class="top">
    <div class="now">
      {#if player.loaded}
        <p class="title" title={player.title ?? undefined}>{player.title}</p>
        <p class="sub">{player.artist ?? 'Unknown artist'}</p>
      {:else}
        <p class="title dim">Nothing playing</p>
      {/if}
    </div>
    <div class="window">
      <button aria-label={keymap.titled('Back to the full player', 'window.compact')} title={keymap.titled('Full player', 'window.compact')} onclick={() => ui.setCompact(false)}>
        <svg viewBox="0 0 12 12"><path d="M7 1h4v4M11 1 7 5M5 11H1V7M1 11l4-4" /></svg>
      </button>
      <button class="close" aria-label="Close" onclick={() => hasHost && call('window.close')}>
        <svg viewBox="0 0 12 12"><path d="M2 2l8 8M10 2l-8 8" /></svg>
      </button>
    </div>
  </div>

  <div class="bottom">
    <div class="transport">
      <button class="icon" onclick={player.previous} disabled={!player.loaded} aria-label={keymap.titled('Previous', 'player.previous')}>
        <svg viewBox="0 0 24 24"><path d="M6 5h2v14H6zM20 5v14L9 12z" /></svg>
      </button>
      <button
        class="play"
        onclick={playOrToggle}
        disabled={!player.loaded && library.total === 0}
        aria-label={keymap.titled(player.state === 'playing' ? 'Pause' : 'Play', 'player.toggle')}
      >
        {#if player.state === 'playing'}
          <svg viewBox="0 0 24 24"><path d="M7 5h3v14H7zM14 5h3v14h-3z" /></svg>
        {:else}
          <svg viewBox="0 0 24 24"><path d="M8 5l11 7-11 7z" /></svg>
        {/if}
      </button>
      <button class="icon" onclick={player.next} disabled={!player.hasNext} aria-label={keymap.titled('Next', 'player.next')}>
        <svg viewBox="0 0 24 24"><path d="M16 5h2v14h-2zM4 5v14l11-7z" /></svg>
      </button>
    </div>
    <input
      type="range"
      min="0"
      max={player.duration || 1}
      step="0.1"
      value={shownPosition}
      disabled={!player.loaded}
      style:--progress="{progress}%"
      oninput={(e) => (scrubbing = e.currentTarget.valueAsNumber)}
      onchange={(e) => {
        player.seek(e.currentTarget.valueAsNumber)
        scrubbing = null
      }}
      aria-label="Seek"
    />
    <span class="time">{formatTime(shownPosition)}</span>
  </div>
</div>

<style>
  .mini {
    height: 100vh;
    display: grid;
    grid-template-rows: 1fr auto;
    gap: 4px;
    padding: 8px 8px 8px 14px;
    background: var(--surface);
    -webkit-app-region: drag;
    user-select: none;
  }

  .top {
    display: flex;
    align-items: flex-start;
    gap: 8px;
    min-width: 0;
  }

  .now {
    flex: 1;
    min-width: 0;
    padding-top: 2px;
  }

  .now p {
    margin: 0;
    overflow: hidden;
    white-space: nowrap;
    text-overflow: ellipsis;
  }

  .title {
    font-size: 13px;
    font-weight: 600;
  }

  .sub,
  .dim {
    font-size: 11px;
    color: var(--text-dim);
  }

  .title.dim {
    font-weight: 400;
  }

  .window {
    display: flex;
    -webkit-app-region: no-drag;
  }

  .window button {
    width: 26px;
    height: 24px;
    display: grid;
    place-items: center;
    border: 0;
    border-radius: 5px;
    background: transparent;
    color: var(--text-dim);
  }

  .window button:hover {
    background: var(--surface-hover);
    color: var(--text);
  }

  .window button.close:hover {
    background: #c42b1c;
    color: #fff;
  }

  .window svg {
    width: 10px;
    height: 10px;
    fill: none;
    stroke: currentColor;
    stroke-width: 1.3;
  }

  .bottom {
    display: grid;
    grid-template-columns: auto minmax(0, 1fr) 36px;
    align-items: center;
    gap: 10px;
  }

  .transport {
    display: flex;
    align-items: center;
    gap: 4px;
    -webkit-app-region: no-drag;
  }

  .icon {
    width: 28px;
    height: 28px;
    display: grid;
    place-items: center;
    border: 0;
    border-radius: 50%;
    background: transparent;
    color: var(--text-dim);
  }

  .icon:hover:not(:disabled) {
    color: var(--text);
  }

  .icon:disabled,
  .play:disabled {
    opacity: 0.35;
  }

  .icon svg {
    width: 16px;
    height: 16px;
    fill: currentColor;
  }

  .play {
    width: 32px;
    height: 32px;
    display: grid;
    place-items: center;
    border: 0;
    border-radius: 50%;
    background: var(--accent);
    color: var(--accent-contrast);
  }

  .play svg {
    width: 16px;
    height: 16px;
    fill: currentColor;
  }

  input[type='range'] {
    -webkit-app-region: no-drag;
  }

  .time {
    font-size: 11px;
    font-variant-numeric: tabular-nums;
    color: var(--text-dim);
    text-align: right;
  }
</style>
