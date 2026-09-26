<script lang="ts">
  import { library } from './library.svelte'
  import { GAIN_MAX_DB, GAIN_MIN_DB, formatGain, formatTime, player } from './player.svelte'

  // While dragging the seek bar, show the drag position instead of live playback position.
  let scrubbing = $state<number | null>(null)
  const shownPosition = $derived(scrubbing ?? player.position)
  const progress = $derived(player.duration > 0 ? (shownPosition / player.duration) * 100 : 0)
  const gainProgress = $derived(((player.trackGainDb - GAIN_MIN_DB) / (GAIN_MAX_DB - GAIN_MIN_DB)) * 100)

  // With nothing loaded, the play button starts the selected song in the list.
  const playOrToggle = () => (player.loaded ? player.toggle() : library.playSelected())
</script>

<footer class="player-bar">
  <div class="now">
    {#if player.loaded}
      <p class="title" title={player.path}>{player.title}</p>
      <p class="sub">{[player.artist, player.album].filter(Boolean).join(' — ') || 'Unknown artist'}</p>
    {:else}
      <p class="title dim">Nothing playing</p>
    {/if}
    {#if player.error}
      <p class="error" role="alert" title={player.error}>{player.error}</p>
    {/if}
  </div>

  <div class="center">
    <div class="transport">
      <button class="icon" onclick={player.previous} disabled={!player.loaded} aria-label="Previous (Ctrl+←)">
        <svg viewBox="0 0 24 24"><path d="M6 5h2v14H6zM20 5v14L9 12z" /></svg>
      </button>
      <button
        class="play"
        onclick={playOrToggle}
        disabled={!player.loaded && library.total === 0}
        aria-label={player.state === 'playing' ? 'Pause' : 'Play'}
      >
        {#if player.state === 'playing'}
          <svg viewBox="0 0 24 24"><path d="M7 5h3v14H7zM14 5h3v14h-3z" /></svg>
        {:else}
          <svg viewBox="0 0 24 24"><path d="M8 5l11 7-11 7z" /></svg>
        {/if}
      </button>
      <button class="icon" onclick={player.next} disabled={!player.hasNext} aria-label="Next (Ctrl+→)">
        <svg viewBox="0 0 24 24"><path d="M16 5h2v14h-2zM4 5v14l11-7z" /></svg>
      </button>
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
  </div>

  <div class="levels">
    <label title="Volume for this song only — remembered per song. Double-click to reset.">
      <span>Song</span>
      <input
        type="range"
        min={GAIN_MIN_DB}
        max={GAIN_MAX_DB}
        step="0.5"
        value={player.trackGainDb}
        disabled={!player.loaded}
        oninput={(e) => player.setTrackGain(e.currentTarget.valueAsNumber)}
        ondblclick={() => player.setTrackGain(0)}
        style:--progress="{gainProgress}%"
      />
      <output>{formatGain(player.trackGainDb)}</output>
    </label>
    <label title="Master volume">
      <span>Master</span>
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
</footer>

<style>
  .player-bar {
    display: grid;
    grid-template-columns: minmax(0, 1fr) minmax(0, 1.4fr) minmax(0, 1fr);
    align-items: center;
    gap: 24px;
    padding: 12px 20px 14px;
    border-top: 1px solid var(--border);
    background: var(--surface);
  }

  .now {
    min-width: 0;
  }

  .now p {
    margin: 0;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .title {
    font-size: 14px;
    font-weight: 600;
  }

  .sub,
  .dim {
    font-size: 12px;
    color: var(--text-dim);
  }

  .title.dim {
    font-size: 14px;
    font-weight: 400;
  }

  .error {
    margin-top: 2px !important;
    font-size: 12px;
    color: #ff9592;
  }

  .center {
    display: grid;
    gap: 6px;
  }

  .transport {
    display: flex;
    justify-content: center;
    align-items: center;
    gap: 14px;
  }

  .icon {
    width: 32px;
    height: 32px;
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

  .icon:disabled {
    opacity: 0.35;
  }

  .icon svg {
    width: 18px;
    height: 18px;
    fill: currentColor;
  }

  .play {
    width: 40px;
    height: 40px;
    display: grid;
    place-items: center;
    border: 0;
    border-radius: 50%;
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
    width: 20px;
    height: 20px;
    fill: currentColor;
  }

  .seek {
    display: grid;
    grid-template-columns: 44px 1fr 44px;
    align-items: center;
    gap: 10px;
  }

  .time {
    font-size: 11px;
    font-variant-numeric: tabular-nums;
    color: var(--text-dim);
    text-align: center;
  }

  .levels {
    display: grid;
    gap: 6px;
    justify-self: end;
    width: min(260px, 100%);
  }

  .levels label {
    display: grid;
    grid-template-columns: 48px 1fr 58px;
    align-items: center;
    gap: 8px;
    font-size: 12px;
    color: var(--text-dim);
  }

  output {
    font-size: 11px;
    font-variant-numeric: tabular-nums;
    text-align: right;
  }

  @media (max-width: 760px) {
    .player-bar {
      grid-template-columns: minmax(0, 1fr) minmax(0, 1.4fr);
    }

    .levels {
      display: none;
    }
  }
</style>
