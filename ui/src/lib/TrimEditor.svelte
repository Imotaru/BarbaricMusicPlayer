<script lang="ts">
  import { untrack } from 'svelte'
  import { call, on } from './bridge'
  import { SILENCE_MAX_DB, SILENCE_MIN_DB, SILENCE_STEP_DB } from './player.svelte'
  import { prefs } from './prefs.svelte'
  import { ui } from './ui.svelte'

  /** What the host sends about a song: its waveform, silence edges and the user's own times (TrimApi.cs). */
  interface TrimInfo {
    id: number
    title: string
    artist: string | null
    durationMs: number
    /** How many ms each byte of the waveform covers. */
    blockMs: number
    floorDb: number
    /** Base64 bytes, 0 = floorDb or quieter, 255 = full scale, on a dB scale. */
    peaks: string
    rms: string
    edges: { db: number; startMs: number; endMs: number }[]
    trimStartMs: number | null
    trimEndMs: number | null
    skipSilence: boolean
    silenceThresholdDb: number
    minTrimmedMs: number
  }

  const props: { trackId: number } = $props()
  const trackId = untrack(() => props.trackId)

  const WAVE_HEIGHT = 170
  const RULER = 18
  const OVERVIEW_HEIGHT = 34
  const MIN_SPAN_MS = 1000
  const PREVIEW_START_MS = 8000
  const PREVIEW_END_MS = 5000

  let info = $state<TrimInfo | null>(null)
  let failed = $state<string | null>(null)
  let peaks = new Uint8Array()
  let rms = new Uint8Array()

  let start = $state(0)
  let end = $state(0)
  let initial = $state({ start: 0, end: 0 })
  let viewStart = $state(0)
  let viewEnd = $state(1)
  let saving = $state(false)

  let previewing = $state(false)
  let previewPos = 0
  let previewAt = 0
  let playhead = $state<number | null>(null)

  let dialog = $state<HTMLDivElement>()
  let stage = $state<HTMLDivElement>()
  let wave = $state<HTMLCanvasElement>()
  let overview = $state<HTMLCanvasElement>()
  let width = $state(0)

  const duration = $derived(info?.durationMs ?? 0)
  const minGap = $derived(info?.minTrimmedMs ?? 1000)

  /** Where the song starts and ends when the user sets nothing: its sound's edges, or the whole song. */
  const auto = $derived.by(() => {
    if (!info) return { start: 0, end: 0 }
    const snapped = Math.min(
      Math.max(Math.round(info.silenceThresholdDb / SILENCE_STEP_DB) * SILENCE_STEP_DB, SILENCE_MIN_DB),
      SILENCE_MAX_DB,
    )
    const edge = info.skipSilence ? info.edges.find((e) => e.db === snapped) : undefined
    return { start: Math.min(edge?.startMs ?? 0, duration), end: Math.min(edge?.endMs ?? duration, duration) }
  })

  const changed = $derived(start !== initial.start || end !== initial.end)
  const markers = $derived([
    { kind: 'start' as const, at: start, label: 'Start' },
    { kind: 'end' as const, at: end, label: 'End' },
  ])
  const isAuto = $derived(start === auto.start && end === auto.end)

  // Loads the waveform and starts where the song plays now.
  $effect(() => {
    call<TrimInfo>('trim.get', { id: trackId })
      .then((result) => {
        peaks = decode(result.peaks)
        rms = decode(result.rms)
        info = result
        const a = untrack(() => auto)
        start = clamp(result.trimStartMs ?? a.start, 0, result.durationMs)
        end = clamp(result.trimEndMs ?? a.end, start, result.durationMs)
        initial = { start, end }
        fit()
      })
      .catch((e) => (failed = e instanceof Error ? e.message : String(e)))
  })

  $effect(() => dialog?.focus())

  // Not passive, so scrolling over the waveform zooms it instead of scrolling the dialog.
  $effect(() => {
    const el = stage
    if (!el) return
    el.addEventListener('wheel', onWheel, { passive: false })
    return () => el.removeEventListener('wheel', onWheel)
  })

  // The preview player reports where it is; the playhead moves smoothly in between.
  $effect(() => {
    const off = on<{ playing: boolean; positionMs: number }>('trim.preview', (p) => {
      previewing = p.playing
      previewPos = p.positionMs
      previewAt = performance.now()
      playhead = p.playing ? p.positionMs : null
    })
    return () => {
      off()
      // The host plays the paused song again and lets go of the file.
      call('trim.close').catch(() => {})
    }
  })

  $effect(() => {
    if (!previewing) return
    let frame = requestAnimationFrame(function tick() {
      playhead = Math.min(previewPos + (performance.now() - previewAt), duration)
      frame = requestAnimationFrame(tick)
    })
    return () => cancelAnimationFrame(frame)
  })

  // Redraws on the next frame after anything shown changes, including the theme's colours.
  $effect(() => {
    void [info, start, end, viewStart, viewEnd, width, auto, prefs.theme, prefs.accent]
    const frame = requestAnimationFrame(draw)
    return () => cancelAnimationFrame(frame)
  })

  function decode(base64: string) {
    const text = atob(base64)
    const bytes = new Uint8Array(text.length)
    for (let i = 0; i < text.length; i++) bytes[i] = text.charCodeAt(i)
    return bytes
  }

  function clamp(value: number, min: number, max: number) {
    return Math.min(Math.max(value, min), max)
  }

  /** m:ss.mmm */
  function formatMs(ms: number) {
    const total = Math.max(0, Math.round(ms))
    const m = Math.floor(total / 60000)
    const s = Math.floor(total / 1000) % 60
    return `${m}:${String(s).padStart(2, '0')}.${String(total % 1000).padStart(3, '0')}`
  }

  /** Reads m:ss.mmm, m:ss or plain seconds. */
  function parseMs(text: string): number | null {
    const match = text.trim().match(/^(?:(\d+):)?(\d+(?:[.,]\d*)?)$/)
    if (!match) return null
    return Math.round(((match[1] ? Number(match[1]) : 0) * 60 + Number(match[2].replace(',', '.'))) * 1000)
  }

  function formatTick(ms: number, step: number) {
    const m = Math.floor(ms / 60000)
    const s = (ms % 60000) / 1000
    const digits = step >= 1000 ? 0 : step >= 100 ? 1 : 2
    const text = s.toFixed(digits)
    return `${m}:${text.padStart(digits > 0 ? 3 + digits : 2, '0')}`
  }

  // ---- The view: which part of the song the waveform shows ----

  function setView(from: number, to: number) {
    const span = clamp(to - from, Math.min(MIN_SPAN_MS, duration), duration)
    viewStart = clamp(from, 0, duration - span)
    viewEnd = viewStart + span
  }

  const fit = () => setView(0, duration)

  function zoomTo(ms: number) {
    const span = Math.min(6000, duration)
    setView(ms - span / 2, ms + span / 2)
  }

  const toX = (ms: number) => ((ms - viewStart) / (viewEnd - viewStart)) * width
  const toMs = (x: number) => viewStart + (x / width) * (viewEnd - viewStart)

  function onWheel(e: WheelEvent) {
    if (!info) return
    e.preventDefault()
    const rect = stage!.getBoundingClientRect()
    const span = viewEnd - viewStart
    if (e.shiftKey || Math.abs(e.deltaX) > Math.abs(e.deltaY)) {
      const delta = (e.shiftKey ? e.deltaY : e.deltaX) || e.deltaY
      setView(viewStart + (delta / width) * span, viewEnd + (delta / width) * span)
      return
    }
    const at = toMs(e.clientX - rect.left)
    const next = clamp(span * Math.exp(e.deltaY * 0.0015), Math.min(MIN_SPAN_MS, duration), duration)
    const share = (at - viewStart) / span
    setView(at - share * next, at - share * next + next)
  }

  // ---- Dragging: the markers, panning the waveform, and the overview ----

  let drag: { pointer: number; kind: 'start' | 'end' | 'pan'; x: number; from: number; moved: boolean } | null = null

  function grabMarker(e: PointerEvent, kind: 'start' | 'end') {
    if (e.button !== 0) return
    e.preventDefault()
    e.stopPropagation()
    ;(e.currentTarget as HTMLElement).setPointerCapture(e.pointerId)
    ;(e.currentTarget as HTMLElement).focus()
    drag = { pointer: e.pointerId, kind, x: e.clientX, from: kind === 'start' ? start : end, moved: false }
  }

  function grabWave(e: PointerEvent) {
    if (e.button !== 0 || !info) return
    e.preventDefault()
    ;(e.currentTarget as HTMLElement).setPointerCapture(e.pointerId)
    drag = { pointer: e.pointerId, kind: 'pan', x: e.clientX, from: viewStart, moved: false }
  }

  function onDrag(e: PointerEvent) {
    if (drag?.pointer !== e.pointerId) return
    const dx = e.clientX - drag.x
    if (Math.abs(dx) > 3) drag.moved = true
    const deltaMs = (dx / width) * (viewEnd - viewStart)
    if (drag.kind === 'pan') {
      if (drag.moved) setView(drag.from - deltaMs, drag.from - deltaMs + (viewEnd - viewStart))
    } else {
      setMarker(drag.kind, drag.from + deltaMs)
    }
  }

  function endDrag(e: PointerEvent) {
    if (drag?.pointer !== e.pointerId) return
    const { kind, moved } = drag
    drag = null
    // A click, not a drag, on the waveform plays from there.
    if (kind === 'pan' && !moved) {
      const rect = (e.currentTarget as HTMLElement).getBoundingClientRect()
      const at = clamp(toMs(e.clientX - rect.left), 0, duration)
      preview(at, at < end ? end : null)
    }
  }

  function setMarker(kind: 'start' | 'end', ms: number) {
    if (kind === 'start') start = Math.round(clamp(ms, 0, Math.max(0, end - minGap)))
    else end = Math.round(clamp(ms, Math.min(duration, start + minGap), duration))
  }

  function onMarkerKeydown(e: KeyboardEvent, kind: 'start' | 'end') {
    const step = e.shiftKey ? 100 : 10
    const current = kind === 'start' ? start : end
    if (e.key === 'ArrowLeft' || e.key === 'ArrowDown') setMarker(kind, current - step)
    else if (e.key === 'ArrowRight' || e.key === 'ArrowUp') setMarker(kind, current + step)
    else return
    e.preventDefault()
    e.stopPropagation()
  }

  function onOverview(e: PointerEvent) {
    if (!info || (e.type === 'pointermove' && !(e.buttons & 1))) return
    if (e.type === 'pointerdown') (e.currentTarget as HTMLElement).setPointerCapture(e.pointerId)
    const rect = (e.currentTarget as HTMLElement).getBoundingClientRect()
    const at = ((e.clientX - rect.left) / rect.width) * duration
    const span = viewEnd - viewStart
    setView(at - span / 2, at + span / 2)
  }

  function onTimeChange(e: Event, kind: 'start' | 'end') {
    const input = e.currentTarget as HTMLInputElement
    const ms = parseMs(input.value)
    if (ms !== null) {
      setMarker(kind, ms)
      const at = kind === 'start' ? start : end
      if (at < viewStart || at > viewEnd) zoomTo(at)
    }
    input.value = formatMs(kind === 'start' ? start : end)
  }

  // ---- Preview, save ----

  function preview(fromMs: number, toMs: number | null) {
    ui.run(() => call('trim.preview', { id: trackId, fromMs: Math.round(fromMs), toMs: toMs === null ? null : Math.round(toMs) }))
  }

  const previewStart = () => preview(start, Math.min(start + PREVIEW_START_MS, end))
  const previewEnd = () => preview(Math.max(start, end - PREVIEW_END_MS), end)
  const stop = () => ui.run(() => call('trim.stopPreview'))

  function reset() {
    start = auto.start
    end = auto.end
  }

  async function save() {
    if (!info || saving) return
    saving = true
    try {
      // A time left where the silence puts it keeps following the silence.
      await call('trim.set', {
        id: trackId,
        startMs: start === auto.start ? null : start,
        endMs: end === auto.end ? null : end,
      })
      ui.closeTrimEditor()
    } catch (e) {
      ui.fail(e)
      saving = false
    }
  }

  function onKeydown(e: KeyboardEvent) {
    e.stopPropagation()
    const target = e.target as HTMLElement
    if (e.key === 'Escape') {
      e.preventDefault()
      ui.closeTrimEditor()
    } else if (e.key === ' ' && !['INPUT', 'BUTTON'].includes(target.tagName) && info) {
      e.preventDefault()
      if (previewing) stop()
      else previewStart()
    } else if (e.key === 'Tab') {
      // Keep focus inside the dialog.
      const focusable = [...(dialog?.querySelectorAll<HTMLElement>('button:not(:disabled), input, [tabindex="0"]') ?? [])]
      const i = focusable.indexOf(document.activeElement as HTMLElement)
      e.preventDefault()
      focusable[(i + (e.shiftKey ? -1 : 1) + focusable.length) % focusable.length]?.focus()
    }
  }

  // ---- Drawing ----

  function colors(el: HTMLElement) {
    const css = getComputedStyle(el)
    const get = (name: string) => css.getPropertyValue(name).trim()
    return { accent: get('--accent'), dim: get('--text-dim'), text: get('--text'), border: get('--border') }
  }

  /** The loudest peak and RMS level (0..1) over a stretch of the song. */
  function levels(from: number, to: number) {
    const blockMs = info!.blockMs
    const first = Math.max(0, Math.floor(from / blockMs))
    const last = Math.min(peaks.length, Math.max(first + 1, Math.ceil(to / blockMs)))
    let p = 0
    let r = 0
    for (let i = first; i < last; i++) {
      if (peaks[i] > p) p = peaks[i]
      if (rms[i] > r) r = rms[i]
    }
    return { peak: p / 255, rms: r / 255 }
  }

  function draw() {
    if (!info || width === 0) return
    drawWave()
    drawOverview()
  }

  function drawWave() {
    const canvas = wave
    if (!canvas) return
    const dpr = devicePixelRatio || 1
    const w = Math.round(width * dpr)
    const h = Math.round(WAVE_HEIGHT * dpr)
    if (canvas.width !== w) canvas.width = w
    if (canvas.height !== h) canvas.height = h
    const ctx = canvas.getContext('2d')!
    const c = colors(canvas)
    ctx.clearRect(0, 0, w, h)

    const top = RULER * dpr
    const mid = top + (h - top) / 2
    const half = (h - top) / 2 - 2 * dpr
    const msPerPx = (viewEnd - viewStart) / w

    // What doesn't play gets a faint wash.
    ctx.fillStyle = c.dim
    ctx.globalAlpha = 0.08
    const sx = toX(start) * dpr
    const ex = toX(end) * dpr
    if (sx > 0) ctx.fillRect(0, top, Math.min(sx, w), h - top)
    if (ex < w) ctx.fillRect(Math.max(0, ex), top, w - Math.max(0, ex), h - top)

    for (let x = 0; x < w; x++) {
      const t0 = viewStart + x * msPerPx
      const t1 = t0 + msPerPx
      if (t0 >= duration) break
      const { peak, rms: r } = levels(t0, t1)
      const inside = t1 > start && t0 < end
      ctx.fillStyle = inside ? c.accent : c.dim
      ctx.globalAlpha = inside ? 0.45 : 0.3
      const ph = Math.max(dpr / 2, peak * half)
      ctx.fillRect(x, mid - ph, 1, ph * 2)
      ctx.globalAlpha = inside ? 1 : 0.5
      const rh = r * half
      ctx.fillRect(x, mid - rh, 1, rh * 2)
    }

    // Where the silence is, at the chosen threshold.
    if (info?.skipSilence) {
      ctx.globalAlpha = 0.8
      ctx.strokeStyle = c.dim
      ctx.lineWidth = dpr
      ctx.setLineDash([3 * dpr, 3 * dpr])
      for (const ms of [auto.start, auto.end]) {
        const x = Math.round(toX(ms) * dpr) + 0.5
        if (x < 0 || x > w) continue
        ctx.beginPath()
        ctx.moveTo(x, top)
        ctx.lineTo(x, h)
        ctx.stroke()
      }
      ctx.setLineDash([])
    }

    // The time ruler: a readable step for how much is shown.
    const steps = [10, 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 15000, 30000, 60000, 120000, 300000, 600000]
    const step = steps.find((s) => (s / (viewEnd - viewStart)) * width >= 80) ?? steps[steps.length - 1]
    ctx.globalAlpha = 1
    ctx.fillStyle = c.dim
    ctx.strokeStyle = c.border
    ctx.font = `${10 * dpr}px system-ui, sans-serif`
    ctx.textBaseline = 'top'
    ctx.beginPath()
    ctx.moveTo(0, top - 0.5)
    ctx.lineTo(w, top - 0.5)
    for (let t = Math.ceil(viewStart / step) * step; t <= viewEnd; t += step) {
      const x = Math.round(toX(t) * dpr) + 0.5
      ctx.moveTo(x, top - 5 * dpr)
      ctx.lineTo(x, top)
      ctx.fillText(formatTick(t, step), x + 3 * dpr, 2 * dpr)
    }
    ctx.stroke()
  }

  function drawOverview() {
    const canvas = overview
    if (!canvas) return
    const dpr = devicePixelRatio || 1
    const w = Math.round(width * dpr)
    const h = Math.round(OVERVIEW_HEIGHT * dpr)
    if (canvas.width !== w) canvas.width = w
    if (canvas.height !== h) canvas.height = h
    const ctx = canvas.getContext('2d')!
    const c = colors(canvas)
    ctx.clearRect(0, 0, w, h)

    const mid = h / 2
    const msPerPx = duration / w
    for (let x = 0; x < w; x++) {
      const t0 = x * msPerPx
      const t1 = t0 + msPerPx
      const inside = t1 > start && t0 < end
      ctx.fillStyle = inside ? c.accent : c.dim
      ctx.globalAlpha = inside ? 0.8 : 0.35
      const ph = Math.max(dpr / 2, levels(t0, t1).peak * (mid - dpr))
      ctx.fillRect(x, mid - ph, 1, ph * 2)
    }

    // The part the big waveform shows.
    const vx = (viewStart / duration) * w
    const vw = Math.max(2 * dpr, ((viewEnd - viewStart) / duration) * w)
    ctx.globalAlpha = 0.12
    ctx.fillStyle = c.text
    ctx.fillRect(vx, 0, vw, h)
    ctx.globalAlpha = 0.9
    ctx.strokeStyle = c.text
    ctx.lineWidth = dpr
    ctx.strokeRect(vx + dpr / 2, dpr / 2, vw - dpr, h - dpr)
  }
</script>

<!-- svelte-ignore a11y_click_events_have_key_events, a11y_no_static_element_interactions -->
<div class="scrim" onclick={(e) => e.target === e.currentTarget && ui.closeTrimEditor()}>
  <div
    class="trim popover"
    role="dialog"
    aria-modal="true"
    aria-labelledby="trim-title"
    tabindex="-1"
    bind:this={dialog}
    onkeydown={onKeydown}
  >
    <header>
      <h2 id="trim-title">Start and end</h2>
      {#if info}
        <p class="song">{info.artist ? `${info.title} — ${info.artist}` : info.title}</p>
      {/if}
    </header>

    {#if failed}
      <p class="message error">{failed}</p>
    {:else if !info}
      <p class="message">Loading the waveform…</p>
    {/if}

    <div class="stage" class:hidden={!info} bind:clientWidth={width}>
      <canvas
        class="overview"
        bind:this={overview}
        style:height="{OVERVIEW_HEIGHT}px"
        onpointerdown={onOverview}
        onpointermove={onOverview}
        aria-label="Whole song; click to show that part below"
      ></canvas>

      <div
        class="wave"
        style:height="{WAVE_HEIGHT}px"
        bind:this={stage}
        onpointerdown={grabWave}
        onpointermove={onDrag}
        onpointerup={endDrag}
        onpointercancel={endDrag}
      >
        <canvas bind:this={wave} style:height="{WAVE_HEIGHT}px"></canvas>

        {#each markers as marker (marker.kind)}
          {@const x = toX(marker.at)}
          {#if info && x >= -8 && x <= width + 8}
            <div
              class="marker {marker.kind}"
              style:left="{x}px"
              role="slider"
              tabindex="0"
              aria-label={marker.label}
              aria-valuemin={0}
              aria-valuemax={duration}
              aria-valuenow={marker.at}
              aria-valuetext={formatMs(marker.at)}
              title="Drag to move the {marker.label.toLowerCase()}. Arrow keys: 10 ms, with Shift 100 ms."
              onpointerdown={(e) => grabMarker(e, marker.kind)}
              onkeydown={(e) => onMarkerKeydown(e, marker.kind)}
            >
              <span class="tab">{marker.label}</span>
            </div>
          {/if}
        {/each}

        {#if playhead !== null && toX(playhead) >= 0 && toX(playhead) <= width}
          <div class="playhead" style:left="{toX(playhead)}px"></div>
        {/if}
      </div>

      <div class="zoom">
        <button class="ghost small" onclick={fit}>Whole song</button>
        <button class="ghost small" onclick={() => zoomTo(start)}>Zoom to start</button>
        <button class="ghost small" onclick={() => zoomTo(end)}>Zoom to end</button>
        <span class="hint">Scroll to zoom, drag or Shift+scroll to move, click to listen from there</span>
      </div>

      <div class="times">
        <label>
          <span>Start</span>
          <input
            type="text"
            value={formatMs(start)}
            onchange={(e) => onTimeChange(e, 'start')}
            onkeydown={(e) => e.key === 'Enter' && onTimeChange(e, 'start')}
            spellcheck="false"
          />
        </label>
        <button class="ghost" onclick={previewStart}>
          <svg viewBox="0 0 16 16" aria-hidden="true"><path d="M4 3v10l9-5z" /></svg>
          Preview start
        </button>
        <label>
          <span>End</span>
          <input
            type="text"
            value={formatMs(end)}
            onchange={(e) => onTimeChange(e, 'end')}
            onkeydown={(e) => e.key === 'Enter' && onTimeChange(e, 'end')}
            spellcheck="false"
          />
        </label>
        <button class="ghost" onclick={previewEnd}>
          <svg viewBox="0 0 16 16" aria-hidden="true"><path d="M4 3v10l9-5z" /></svg>
          Preview end
        </button>
        <button class="ghost" onclick={stop} disabled={!previewing} aria-label="Stop the preview">
          <svg viewBox="0 0 16 16" aria-hidden="true"><path d="M4 4h8v8H4z" /></svg>
          Stop
        </button>
      </div>

      <p class="note">
        Plays {formatMs(end - start)} of {formatMs(duration)}.
        {#if info && !info.skipSilence}
          Skipping silence is off in Settings, so without times of your own the whole song plays.
        {:else}
          The dashed lines show where the silence ends and starts again; times left there follow the silence.
        {/if}
      </p>
    </div>

    <div class="actions">
      <button class="ghost" onclick={reset} disabled={!info || isAuto}>Reset to automatic</button>
      <span class="spacer"></span>
      <button class="ghost" onclick={ui.closeTrimEditor}>Cancel</button>
      <button class="primary" onclick={save} disabled={!info || saving || !changed}>Save</button>
    </div>
  </div>
</div>

<style>
  .scrim {
    position: fixed;
    inset: 0;
    z-index: 60;
    display: grid;
    grid-template-rows: minmax(0, 1fr);
    place-items: center;
    padding: 16px;
    background: var(--scrim);
  }

  .trim {
    display: flex;
    flex-direction: column;
    width: min(920px, 100%);
    max-height: 100%;
    padding: 18px 18px 14px;
    overflow-y: auto;
    outline: 0;
  }

  header {
    margin-bottom: 10px;
  }

  h2 {
    margin: 0 0 2px;
    font-size: 15px;
    font-weight: 650;
  }

  .song,
  .message,
  .note {
    margin: 0;
    font-size: 13px;
    line-height: 1.5;
    color: var(--text-dim);
  }

  .song {
    overflow: hidden;
    white-space: nowrap;
    text-overflow: ellipsis;
  }

  .message {
    padding: 40px 0;
    text-align: center;
  }

  .message.error {
    color: var(--danger-text);
  }

  .stage.hidden {
    display: none;
  }

  canvas {
    display: block;
    width: 100%;
  }

  .overview {
    margin-bottom: 8px;
    border-radius: 6px;
    background: var(--bg);
    cursor: pointer;
    touch-action: none;
  }

  .wave {
    position: relative;
    overflow: hidden;
    border-radius: 8px;
    background: var(--bg);
    cursor: grab;
    touch-action: none;
    user-select: none;
  }

  .marker {
    position: absolute;
    top: 0;
    bottom: 0;
    width: 13px;
    margin-left: -6px;
    cursor: ew-resize;
    outline: 0;
  }

  .marker::before {
    content: '';
    position: absolute;
    top: 0;
    bottom: 0;
    left: 5px;
    width: 3px;
    border-radius: 2px;
    background: var(--text);
  }

  .marker:focus-visible::before,
  .marker:hover::before {
    background: var(--accent);
  }

  .tab {
    position: absolute;
    top: 1px;
    padding: 1px 6px;
    border-radius: 4px;
    background: var(--text);
    color: var(--bg);
    font-size: 10px;
    font-weight: 650;
    line-height: 14px;
    white-space: nowrap;
  }

  .marker.start .tab {
    left: 6px;
  }

  .marker.end .tab {
    right: 6px;
  }

  .marker:focus-visible .tab,
  .marker:hover .tab {
    background: var(--accent);
    color: var(--accent-contrast);
  }

  .playhead {
    position: absolute;
    top: 0;
    bottom: 0;
    width: 1px;
    background: var(--text);
    pointer-events: none;
  }

  .zoom {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 6px;
    margin: 8px 0 12px;
  }

  .hint {
    margin-left: auto;
    font-size: 11px;
    color: var(--text-dim);
  }

  .times {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 8px;
    margin-bottom: 10px;
  }

  .times label {
    display: flex;
    align-items: center;
    gap: 6px;
    font-size: 13px;
  }

  .times label:not(:first-child) {
    margin-left: 10px;
  }

  .times input {
    width: 92px;
    height: 30px;
    padding: 0 8px;
    border: 1px solid var(--border);
    border-radius: 6px;
    background: var(--bg);
    color: var(--text);
    font: inherit;
    font-variant-numeric: tabular-nums;
  }

  .times input:focus {
    outline: 0;
    border-color: var(--accent);
  }

  .times svg {
    width: 11px;
    height: 11px;
    fill: currentColor;
  }

  .actions {
    display: flex;
    align-items: center;
    gap: 8px;
    margin-top: 12px;
  }

  .spacer {
    flex: 1;
  }

  button {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    height: 30px;
    padding: 0 14px;
    border-radius: 999px;
    font-size: 13px;
    font-weight: 600;
  }

  button.small {
    height: 26px;
    padding: 0 10px;
    font-size: 12px;
  }

  button:disabled {
    opacity: 0.45;
    cursor: default;
  }

  .ghost {
    border: 1px solid var(--border);
    background: transparent;
    color: var(--text);
  }

  .ghost:hover:not(:disabled) {
    background: var(--surface-hover);
  }

  .primary {
    border: 0;
    background: var(--accent);
    color: var(--accent-contrast);
  }
</style>
