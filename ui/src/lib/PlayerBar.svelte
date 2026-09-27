<script lang="ts">
  import { keymap } from './keymap.svelte'
  import { library, songs } from './library.svelte'
  import { GAIN_MAX_DB, GAIN_MIN_DB, formatGain, formatTime, formatVolume, player } from './player.svelte'

  // While dragging the seek bar, show the drag position instead of live playback position.
  let scrubbing = $state<number | null>(null)
  const shownPosition = $derived(scrubbing ?? player.position)
  const progress = $derived(player.duration > 0 ? (shownPosition / player.duration) * 100 : 0)
  const gainProgress = $derived(((player.trackGainDb - GAIN_MIN_DB) / (GAIN_MAX_DB - GAIN_MIN_DB)) * 100)
  const songGainTitle = $derived.by(() => {
    const base = 'Volume for this song only — remembered per song. Double-click to reset.'
    if (!player.loaded || !player.normalize) return base
    const auto = player.autoGainDb === null ? 'not measured yet' : formatGain(player.autoGainDb)
    return `${base}
Adjusts on top of the automatic level (${auto}) that evens it out with other songs.`
  })

  // The list the playing songs came from; its tile opens them.
  const source = $derived(player.loaded && player.source ? library.describeSource(player.source) : null)
  const sourceTitle = $derived(
    source ? keymap.titled(`Playing from ${source.name} · ${songs(player.poolSize)}. Show them`, 'view.playing') : '',
  )

  // With nothing loaded, the play button starts the selected song in the list.
  const playOrToggle = () => (player.loaded ? player.toggle() : library.playSelected())
</script>

<footer class="player-bar">
  <div class="now">
    {#if source}
      <button
        class="source"
        class:active={library.view.kind === 'playing'}
        style:--tint={source.color ?? 'var(--accent)'}
        onclick={library.openNowPlaying}
        aria-label="Show the songs playing from {source.name}"
        title={sourceTitle}
      >
        <svg viewBox="0 0 16 16" aria-hidden="true">
          {#if source.kind === 'tags'}
            <path d="M2 2.5h5.3l6.2 6.2-4.8 4.8L2.5 7.3z" /><circle cx="5.3" cy="5.3" r="1" />
          {:else if source.kind === 'filter'}
            <path d="M2 3h12l-4.5 5.5V13l-3 1.5V8.5z" />
          {:else if source.kind === 'manual'}
            <path d="M2 4h9M2 8h9M2 12h6M13 10v5M10.5 12.5h5" />
          {:else if source.kind === 'search'}
            <circle cx="7" cy="7" r="4.5" /><path d="m10.5 10.5 4 4" />
          {:else if source.kind === 'suggested'}
            <path d="M3 3.5 10 8l-7 4.5zM12.5 3.5v9" />
          {:else if source.kind === 'hidden'}
            <path d="M1.5 8S3.9 3.5 8 3.5 14.5 8 14.5 8 12.1 12.5 8 12.5 1.5 8 1.5 8z" /><circle cx="8" cy="8" r="2" /><path d="m2.5 13.5 11-11" />
          {:else}
            <path d="M6 12.5V3.5l7-1.5v9" /><circle cx="4.5" cy="12.5" r="1.8" /><circle cx="11.5" cy="11" r="1.8" />
          {/if}
        </svg>
      </button>
    {/if}
    <div class="text">
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
  </div>

  <div class="center">
    <div class="transport">
      <button
        class="icon"
        class:on={player.shuffle}
        onclick={player.toggleShuffle}
        aria-pressed={player.shuffle}
        aria-label={keymap.titled('Shuffle', 'player.shuffle')}
        title={keymap.titled(player.shuffle ? 'Shuffle is on: songs you often skip come up less' : 'Shuffle', 'player.shuffle')}
      >
        <svg viewBox="0 0 24 24">
          <path
            d="M10.59 9.17 5.41 4 4 5.41l5.17 5.17zM14.5 4l2.04 2.04L4 18.59 5.41 20 17.96 7.46 20 9.5V4zm.33 9.41-1.41 1.41 3.13 3.13L14.5 20H20v-5.5l-2.04 2.04z"
          />
        </svg>
      </button>
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
      <button
        class="icon"
        class:on={player.loop}
        onclick={player.toggleLoop}
        aria-pressed={player.loop}
        aria-label={keymap.titled('Loop song', 'player.loop')}
        title={keymap.titled(player.loop ? 'Loop song is on: this song repeats' : 'Loop song', 'player.loop')}
      >
        <svg viewBox="0 0 24 24">
          <path d="M7 7h10v3l4-4-4-4v3H5v6h2zm10 10H7v-3l-4 4 4 4v-3h12v-6h-2zm-4-2V9h-1l-2 1v1h1.5v4z" />
        </svg>
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
    <label title={songGainTitle}>
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
        max={player.volumeLimit}
        step={player.volumeLimit / 100}
        value={player.volume}
        oninput={(e) => player.setVolume(e.currentTarget.valueAsNumber)}
        style:--progress="{(player.volume / player.volumeLimit) * 100}%"
      />
      <output>{formatVolume(player.volume)}</output>
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
    display: flex;
    align-items: center;
    gap: 12px;
    min-width: 0;
  }

  .text {
    min-width: 0;
  }

  .source {
    flex: none;
    display: grid;
    place-items: center;
    width: 40px;
    height: 40px;
    padding: 0;
    border: 1px solid color-mix(in srgb, var(--tint) 35%, var(--border));
    border-radius: 8px;
    background: color-mix(in srgb, var(--tint) 16%, var(--surface));
    color: var(--tint);
    transition: background 120ms ease;
  }

  .source:hover {
    background: color-mix(in srgb, var(--tint) 26%, var(--surface));
  }

  .source.active {
    border-color: var(--tint);
    box-shadow: 0 0 0 1px var(--tint);
  }

  .source svg {
    width: 18px;
    height: 18px;
    fill: none;
    stroke: currentColor;
    stroke-width: 1.5;
    stroke-linecap: round;
    stroke-linejoin: round;
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
    color: var(--danger-text);
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

  .icon.on,
  .icon.on:hover {
    color: var(--accent);
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
