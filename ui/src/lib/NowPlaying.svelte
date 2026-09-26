<script lang="ts">
  import { GAIN_MAX_DB, GAIN_MIN_DB, formatTime, player } from './player.svelte'

  // While dragging the seek bar, show the drag position instead of live playback position.
  let scrubbing = $state<number | null>(null)
  const shownPosition = $derived(scrubbing ?? player.position)
  const progress = $derived(player.duration > 0 ? (shownPosition / player.duration) * 100 : 0)

  const formatGain = (db: number) => `${db > 0 ? '+' : ''}${db.toFixed(1)} dB`
</script>

<section class="now-playing">
  <div class="track">
    {#if player.loaded}
      <h1 title={player.path}>{player.title}</h1>
      <p class="path">{player.path}</p>
    {:else}
      <h1 class="empty">Nothing playing</h1>
      <p class="path">Open a song to get started <kbd>Ctrl</kbd>+<kbd>O</kbd></p>
    {/if}
  </div>

  <div class="seek">
    <span class="time">{formatTime(shownPosition)}</span>
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
    <span class="time">{formatTime(player.duration)}</span>
  </div>

  <div class="transport">
    <button class="secondary" onclick={player.openFile}>Open…</button>
    <button
      class="play"
      onclick={player.toggle}
      disabled={!player.loaded}
      aria-label={player.state === 'playing' ? 'Pause' : 'Play'}
    >
      {#if player.state === 'playing'}
        <svg viewBox="0 0 24 24"><path d="M7 5h3v14H7zM14 5h3v14h-3z" /></svg>
      {:else}
        <svg viewBox="0 0 24 24"><path d="M8 5l11 7-11 7z" /></svg>
      {/if}
    </button>
    <div class="spacer"></div>
  </div>

  <div class="levels">
    <label>
      <span>Track volume <small>(this song)</small></span>
      <input
        type="range"
        min={GAIN_MIN_DB}
        max={GAIN_MAX_DB}
        step="0.5"
        value={player.trackGainDb}
        disabled={!player.loaded}
        oninput={(e) => player.setTrackGain(e.currentTarget.valueAsNumber)}
        ondblclick={() => player.setTrackGain(0)}
        style:--progress="{((player.trackGainDb - GAIN_MIN_DB) / (GAIN_MAX_DB - GAIN_MIN_DB)) * 100}%"
      />
      <output>{formatGain(player.trackGainDb)}</output>
    </label>
    <label>
      <span>Master volume</span>
      <input
        type="range"
        min="0"
        max="1"
        step="0.01"
        value={player.volume}
        oninput={(e) => player.setVolume(e.currentTarget.valueAsNumber)}
        style:--progress="{player.volume * 100}%"
      />
      <output>{Math.round(player.volume * 100)}%</output>
    </label>
  </div>

  {#if player.error}
    <p class="error" role="alert">{player.error}</p>
  {/if}
</section>

<style>
  .now-playing {
    width: min(640px, 100%);
    display: flex;
    flex-direction: column;
    gap: 28px;
  }

  .track {
    min-width: 0;
  }

  h1 {
    margin: 0 0 6px;
    font-size: 30px;
    font-weight: 700;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  h1.empty {
    color: var(--text-dim);
  }

  .path {
    margin: 0;
    font-size: 12px;
    color: var(--text-dim);
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  kbd {
    font: inherit;
    padding: 1px 5px;
    border-radius: 4px;
    background: var(--surface);
    border: 1px solid var(--border);
  }

  .seek {
    display: grid;
    grid-template-columns: 44px 1fr 44px;
    align-items: center;
    gap: 12px;
  }

  .time {
    font-size: 12px;
    font-variant-numeric: tabular-nums;
    color: var(--text-dim);
    text-align: center;
  }

  .transport {
    display: grid;
    grid-template-columns: 1fr auto 1fr;
    align-items: center;
  }

  .secondary {
    justify-self: start;
    padding: 8px 16px;
    border-radius: 999px;
    border: 1px solid var(--border);
    background: var(--surface);
    color: var(--text);
  }

  .secondary:hover {
    background: var(--surface-hover);
  }

  .play {
    width: 64px;
    height: 64px;
    border-radius: 50%;
    border: 0;
    display: grid;
    place-items: center;
    background: var(--accent);
    color: var(--accent-contrast);
    transition: transform 120ms ease;
  }

  .play:hover:not(:disabled) {
    transform: scale(1.06);
  }

  .play:disabled {
    opacity: 0.35;
  }

  .play svg {
    width: 28px;
    height: 28px;
    fill: currentColor;
  }

  .levels {
    display: grid;
    gap: 14px;
    padding: 18px 20px;
    border-radius: 14px;
    background: var(--surface);
    border: 1px solid var(--border);
  }

  .levels label {
    display: grid;
    grid-template-columns: 170px 1fr 70px;
    align-items: center;
    gap: 12px;
    font-size: 13px;
  }

  .levels small {
    color: var(--text-dim);
  }

  output {
    font-size: 12px;
    font-variant-numeric: tabular-nums;
    text-align: right;
    color: var(--text-dim);
  }

  .error {
    margin: 0;
    padding: 10px 14px;
    border-radius: 10px;
    background: color-mix(in srgb, #e5484d 18%, transparent);
    color: #ff9592;
    font-size: 13px;
  }

  @media (max-width: 560px) {
    .levels label {
      grid-template-columns: 1fr 60px;
    }

    .levels label span {
      grid-column: 1 / -1;
    }
  }
</style>
