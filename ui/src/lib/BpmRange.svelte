<script lang="ts">
  import { library } from './library.svelte'
  import { BPM_DOMAIN, formatRange } from './query'

  const { min: LOW, max: HIGH } = BPM_DOMAIN

  let track = $state<HTMLDivElement>()
  let dragging = $state<'lo' | 'hi' | null>(null)

  // A handle parked at either end of the track means that side has no limit.
  const lo = $derived(library.lens.min ?? LOW)
  const hi = $derived(library.lens.max ?? HIGH)
  const active = $derived(library.lensActive)
  const label = $derived(active ? formatRange(library.lens.min, library.lens.max) : 'Any')
  const percent = (v: number) => ((v - LOW) / (HIGH - LOW)) * 100

  function apply(nextLo: number, nextHi: number) {
    nextLo = Math.round(Math.min(Math.max(nextLo, LOW), HIGH))
    nextHi = Math.round(Math.min(Math.max(nextHi, LOW), HIGH))
    if (nextLo > nextHi) [nextLo, nextHi] = [nextHi, nextLo]
    const min = nextLo <= LOW ? null : nextLo
    const max = nextHi >= HIGH ? null : nextHi
    if (min === library.lens.min && max === library.lens.max) return
    library.setLens({ ...library.lens, min, max })
  }

  function valueAt(clientX: number) {
    const rect = track!.getBoundingClientRect()
    return LOW + ((clientX - rect.left) / rect.width) * (HIGH - LOW)
  }

  function onPointerdown(e: PointerEvent) {
    if (e.button !== 0 || !track) return
    e.preventDefault()
    const v = valueAt(e.clientX)
    // Grab the nearer handle; when they overlap, the drag direction decides.
    dragging = Math.abs(v - lo) < Math.abs(v - hi) || (lo === hi && v < lo) ? 'lo' : 'hi'
    track.setPointerCapture(e.pointerId)
    move(v)
    ;(track.querySelector(`[data-handle="${dragging}"]`) as HTMLElement | null)?.focus()
  }

  function move(v: number) {
    if (dragging === 'lo') apply(Math.min(v, hi), hi)
    else if (dragging === 'hi') apply(lo, Math.max(v, lo))
  }

  function onKeydown(e: KeyboardEvent, handle: 'lo' | 'hi') {
    const current = handle === 'lo' ? lo : hi
    const step = e.shiftKey ? 5 : 1
    const next =
      e.key === 'ArrowRight' || e.key === 'ArrowUp' ? current + step
      : e.key === 'ArrowLeft' || e.key === 'ArrowDown' ? current - step
      : e.key === 'PageUp' ? current + 10
      : e.key === 'PageDown' ? current - 10
      : e.key === 'Home' ? LOW
      : e.key === 'End' ? HIGH
      : null
    if (next === null) return

    // Handled here, so the list doesn't also move its cursor or seek.
    e.preventDefault()
    e.stopPropagation()
    if (handle === 'lo') apply(Math.min(next, hi), hi)
    else apply(lo, Math.max(next, lo))
  }

  const toggleUnknown = () => library.setLens({ ...library.lens, includeUnknown: !library.lens.includeUnknown })
</script>

<div class="bpm-range" class:active role="group" aria-label="BPM range">
  <span class="label">BPM</span>

  <div
    class="track"
    bind:this={track}
    role="presentation"
    onpointerdown={onPointerdown}
    onpointermove={(e) => dragging && move(valueAt(e.clientX))}
    onpointerup={() => (dragging = null)}
    onpointercancel={() => (dragging = null)}
  >
    <div class="rail"></div>
    <div class="fill" style:left="{percent(lo)}%" style:right="{100 - percent(hi)}%"></div>
    {#each [['lo', lo, 'Lowest BPM'], ['hi', hi, 'Highest BPM']] as const as [handle, v, name] (handle)}
      <div
        class="handle"
        class:dragging={dragging === handle}
        data-handle={handle}
        style:left="{percent(v)}%"
        role="slider"
        tabindex="0"
        aria-label={name}
        aria-valuemin={LOW}
        aria-valuemax={HIGH}
        aria-valuenow={v}
        aria-valuetext={(handle === 'lo' ? v <= LOW : v >= HIGH) ? 'no limit' : `${v} BPM`}
        onkeydown={(e) => onKeydown(e, handle)}
      ></div>
    {/each}
  </div>

  <span class="readout" title={active ? 'Songs outside this range are hidden everywhere, including the queue' : ''}>{label}</span>

  {#if active}
    <button
      class="unknown"
      class:on={library.lens.includeUnknown}
      aria-pressed={library.lens.includeUnknown}
      title="Also show songs whose BPM isn't known"
      onclick={toggleUnknown}
    >
      +?
    </button>
    <button class="reset" aria-label="Show every BPM" title="Show every BPM" onclick={library.resetLens}>
      <svg viewBox="0 0 10 10"><path d="M2.5 2.5l5 5M7.5 2.5l-5 5" /></svg>
    </button>
  {/if}
</div>

<style>
  .bpm-range {
    flex: none;
    display: flex;
    align-items: center;
    gap: 10px;
    width: 330px;
    height: 38px;
    padding: 0 8px 0 12px;
    border-radius: 10px;
    background: var(--surface);
    border: 1px solid var(--border);
  }

  .bpm-range.active {
    border-color: color-mix(in srgb, var(--accent) 55%, var(--border));
  }

  .label {
    font-size: 11px;
    font-weight: 600;
    letter-spacing: 0.08em;
    color: var(--text-dim);
  }

  .track {
    position: relative;
    flex: 1;
    min-width: 80px;
    height: 24px;
    margin: 0 7px;
    cursor: pointer;
    touch-action: none;
  }

  .rail,
  .fill {
    position: absolute;
    top: 50%;
    height: 4px;
    margin-top: -2px;
    border-radius: 2px;
  }

  .rail {
    left: 0;
    right: 0;
    background: var(--track);
  }

  .fill {
    background: var(--text-dim);
  }

  .active .fill {
    background: var(--accent);
  }

  .handle {
    position: absolute;
    top: 50%;
    width: 14px;
    height: 14px;
    margin: -7px 0 0 -7px;
    border-radius: 50%;
    background: var(--text);
    box-shadow: 0 1px 3px rgb(0 0 0 / 0.5);
    outline: 0;
  }

  .handle:focus-visible,
  .handle.dragging {
    box-shadow: 0 0 0 3px color-mix(in srgb, var(--accent) 45%, transparent);
  }

  .readout {
    min-width: 58px;
    font-size: 13px;
    font-variant-numeric: tabular-nums;
    text-align: right;
    white-space: nowrap;
  }

  .active .readout {
    color: var(--accent);
    font-weight: 600;
  }

  .unknown {
    height: 22px;
    padding: 0 6px;
    border: 1px solid var(--border);
    border-radius: 999px;
    background: none;
    color: var(--text-dim);
    font-size: 11px;
    font-weight: 600;
  }

  .unknown.on {
    border-color: transparent;
    background: var(--surface-hover);
    color: var(--text);
  }

  .reset {
    display: grid;
    place-items: center;
    width: 22px;
    height: 22px;
    padding: 0;
    border: 0;
    border-radius: 50%;
    background: none;
    color: var(--text-dim);
  }

  .reset:hover {
    background: var(--surface-hover);
    color: var(--text);
  }

  .reset svg {
    width: 9px;
    height: 9px;
    fill: none;
    stroke: currentColor;
    stroke-width: 1.5;
  }
</style>
