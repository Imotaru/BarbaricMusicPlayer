<script lang="ts">
  import { onDestroy, untrack } from 'svelte'
  import { bpm, describeBpm } from './bpm.svelte'
  import { songs, type BpmInfo } from './library.svelte'
  import { TapTempo } from './tapTempo'
  import { ui, type Point } from './ui.svelte'

  const MIN = 20
  const MAX = 400

  const props: { trackIds: number[]; at: Point } = $props()

  // A snapshot, like the picker: the editor keeps acting on the songs it was opened for.
  const { trackIds, at } = untrack(() => ({ ...props }))
  const count = trackIds.length

  let popover = $state<HTMLDivElement>()
  let input = $state<HTMLInputElement>()
  let position = $state({ x: 0, y: 0 })
  let current = $state.raw<BpmInfo[]>([])
  let value = $state('')
  let busy = $state(false)
  let tapped = $state<number | null>(null)
  let taps = $state(0)
  let flash = $state(false)
  let flashTimer: ReturnType<typeof setTimeout> | undefined
  const tapper = new TapTempo()

  /** The songs' shared BPM, or null when they differ (or none is known). */
  const shared = $derived.by(() => {
    const known = new Set(current.map((t) => (t.bpm === null ? null : Math.round(t.bpm * 10) / 10)))
    return known.size === 1 ? [...known][0] : null
  })
  const mixed = $derived(current.length > 1 && new Set(current.map((t) => t.bpm)).size > 1)
  const parsed = $derived(Number.parseFloat(value.replace(',', '.')))
  const valid = $derived(Number.isFinite(parsed) && parsed >= MIN && parsed <= MAX)

  async function load() {
    current = await bpm.get(trackIds)
    value = shared === null ? '' : String(shared)
  }

  load().catch(ui.fail)

  $effect(() => {
    if (!popover) return
    const { width, height } = popover.getBoundingClientRect()
    position = {
      x: Math.max(4, Math.min(at.x, innerWidth - width - 4)),
      y: Math.max(4, Math.min(at.y, innerHeight - height - 4)),
    }
    input?.focus()
    input?.select()
  })

  onDestroy(() => clearTimeout(flashTimer))

  async function act(action: () => Promise<unknown>, close = false) {
    if (busy) return
    busy = true
    try {
      await action()
      if (close) ui.closeBpmEditor()
      else await load()
    } catch (e) {
      ui.fail(e)
    } finally {
      busy = false
      input?.focus()
    }
  }

  const save = () => {
    if (!valid) return
    act(async () => {
      await bpm.set(trackIds, parsed)
      if (count > 1) ui.notify(`Set ${songs(count)} to ${Math.round(parsed * 10) / 10} BPM.`)
    }, true)
  }

  const scale = (factor: number) =>
    act(async () => {
      const changed = await bpm.scale(trackIds, factor)
      if (changed < count) ui.notify(`${(count - changed).toLocaleString()} without a BPM (or out of range) left alone.`)
    })

  const reset = () => act(() => bpm.reset(trackIds), true)

  function tap() {
    const tempo = tapper.tap()
    taps = tapper.count
    tapped = tempo
    if (tempo !== null) value = String(Math.round(tempo * 10) / 10)
    flash = true
    clearTimeout(flashTimer)
    flashTimer = setTimeout(() => (flash = false), 90)
  }

  function onKeydown(e: KeyboardEvent) {
    e.stopPropagation()
    if (e.key === 'Enter') {
      e.preventDefault()
      save()
    } else if (e.key === 'Escape') {
      e.preventDefault()
      ui.closeBpmEditor()
    } else if ((e.key === 't' || e.key === 'T') && !e.ctrlKey && !e.altKey && !e.metaKey) {
      e.preventDefault()
      if (!e.repeat) tap()
    } else if (e.key === 'Tab') {
      // Keep focus inside the popover.
      const focusable = [...(popover?.querySelectorAll<HTMLElement>('input, button:not(:disabled)') ?? [])]
      const i = focusable.indexOf(document.activeElement as HTMLElement)
      const next = focusable[(i + (e.shiftKey ? -1 : 1) + focusable.length) % focusable.length]
      e.preventDefault()
      next?.focus()
    }
  }

  function onPointerdown(e: PointerEvent) {
    if (!popover?.contains(e.target as Node)) ui.closeBpmEditor()
  }

  const title = $derived(count === 1 ? 'BPM' : `BPM of ${songs(count)}`)
  const detail = $derived(
    current.length === 1 ? describeBpm(current[0]) : mixed ? 'These songs have different BPMs.' : '',
  )
</script>

<svelte:window onpointerdown={onPointerdown} onblur={ui.closeBpmEditor} onresize={ui.closeBpmEditor} />

<div
  class="bpm-editor popover"
  role="dialog"
  aria-label={title}
  tabindex="-1"
  bind:this={popover}
  style:left="{position.x}px"
  style:top="{position.y}px"
  onkeydown={onKeydown}
>
  <p class="title">{title}</p>

  <div class="value">
    <input
      bind:this={input}
      bind:value
      inputmode="decimal"
      placeholder={mixed ? 'mixed' : '—'}
      autocomplete="off"
      spellcheck="false"
      maxlength={6}
      aria-label="BPM"
      aria-invalid={value !== '' && !valid}
    />
    <button class="ghost" disabled={busy} onclick={() => scale(0.5)} title="Halve the BPM">÷2</button>
    <button class="ghost" disabled={busy} onclick={() => scale(2)} title="Double the BPM">×2</button>
  </div>
  <p class="detail">{detail}</p>

  <button class="tap" class:flash onclick={tap} disabled={busy}>
    <span class="big">{tapped === null ? 'Tap' : Math.round(tapped * 10) / 10}</span>
    <span class="small">
      {#if taps === 0}
        Tap along with the beat (or press <kbd>T</kbd>)
      {:else if tapped === null}
        Keep going…
      {:else}
        {taps} taps
      {/if}
    </span>
  </button>

  <div class="actions">
    <button class="link" disabled={busy} onclick={reset} title="Go back to the file's tag, or measure it again">
      Reset to automatic
    </button>
    <button class="primary" disabled={busy || !valid} onclick={save}>Save</button>
  </div>
</div>

<style>
  .bpm-editor {
    position: fixed;
    z-index: 50;
    width: 260px;
    padding: 10px;
    outline: 0;
  }

  .title {
    margin: 0 2px 8px;
    font-size: 11px;
    font-weight: 700;
    letter-spacing: 0.08em;
    text-transform: uppercase;
    color: var(--text-dim);
  }

  .value {
    display: flex;
    gap: 6px;
  }

  input {
    flex: 1;
    min-width: 0;
    height: 32px;
    padding: 0 10px;
    border: 1px solid var(--border);
    border-radius: 8px;
    outline: 0;
    background: var(--bg);
    color: var(--text);
    font: inherit;
    font-size: 15px;
    font-variant-numeric: tabular-nums;
  }

  input:focus {
    border-color: var(--accent);
  }

  input[aria-invalid='true'] {
    border-color: var(--danger);
  }

  .ghost {
    flex: none;
    width: 38px;
    height: 32px;
    border: 1px solid var(--border);
    border-radius: 8px;
    background: none;
    color: var(--text);
    font-size: 13px;
  }

  .ghost:hover:not(:disabled) {
    background: var(--surface-hover);
  }

  .detail {
    min-height: 16px;
    margin: 6px 2px 8px;
    font-size: 11px;
    color: var(--text-dim);
  }

  .tap {
    display: grid;
    justify-items: center;
    gap: 2px;
    width: 100%;
    padding: 12px 8px;
    border: 1px dashed var(--border);
    border-radius: 10px;
    background: var(--bg);
    color: var(--text);
    transition: background 90ms;
  }

  .tap:hover:not(:disabled) {
    border-color: var(--text-dim);
  }

  .tap.flash {
    background: color-mix(in srgb, var(--accent) 22%, var(--bg));
  }

  .big {
    font-size: 22px;
    font-weight: 600;
    font-variant-numeric: tabular-nums;
  }

  .small {
    font-size: 11px;
    color: var(--text-dim);
  }

  kbd {
    font: inherit;
    padding: 0 4px;
    border: 1px solid var(--border);
    border-radius: 3px;
  }

  .actions {
    display: flex;
    align-items: center;
    justify-content: space-between;
    margin-top: 10px;
  }

  .link {
    padding: 0;
    border: 0;
    background: none;
    color: var(--text-dim);
    font-size: 12px;
    text-decoration: underline;
    text-underline-offset: 2px;
  }

  .link:hover:not(:disabled) {
    color: var(--text);
  }

  .primary {
    height: 30px;
    padding: 0 16px;
    border: 0;
    border-radius: 999px;
    background: var(--accent);
    color: var(--accent-contrast);
    font-weight: 600;
    font-size: 13px;
  }

  .primary:disabled {
    opacity: 0.5;
  }
</style>
