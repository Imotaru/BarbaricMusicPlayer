<script lang="ts">
  import { untrack } from 'svelte'
  import { library, songs, type SkipInfo } from './library.svelte'
  import { ui, type Point } from './ui.svelte'

  const MAX = 99_999

  const props: { trackIds: number[]; at: Point } = $props()

  // A snapshot, like the BPM editor: it keeps acting on the songs it was opened for.
  const { trackIds, at } = untrack(() => ({ ...props }))
  const count = trackIds.length

  let popover = $state<HTMLDivElement>()
  let input = $state<HTMLInputElement>()
  let position = $state({ x: 0, y: 0 })
  let current = $state.raw<SkipInfo[]>([])
  let value = $state('')
  let busy = $state(false)

  const mixed = $derived(new Set(current.map((t) => t.skipCount)).size > 1)
  const parsed = $derived(/^\d+$/.test(value.trim()) ? Number(value.trim()) : NaN)
  const valid = $derived(Number.isInteger(parsed) && parsed <= MAX)

  async function load() {
    current = await library.getSkips(trackIds)
    const counts = new Set(current.map((t) => t.skipCount))
    value = counts.size === 1 ? String([...counts][0]) : ''
    input?.select()
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

  async function apply(skips: number) {
    if (busy) return
    busy = true
    try {
      await library.setSkips(trackIds, skips)
      if (count > 1) {
        ui.notify(skips === 0 ? `Cleared the skips of ${songs(count)}.` : `Set ${songs(count)} to ${skips.toLocaleString()} skips.`)
      }
      ui.closeSkipsEditor()
    } catch (e) {
      ui.fail(e)
      busy = false
      input?.focus()
    }
  }

  const save = () => valid && apply(parsed)

  function onKeydown(e: KeyboardEvent) {
    e.stopPropagation()
    if (e.key === 'Enter') {
      e.preventDefault()
      save()
    } else if (e.key === 'Escape') {
      e.preventDefault()
      ui.closeSkipsEditor()
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
    if (!popover?.contains(e.target as Node)) ui.closeSkipsEditor()
  }

  const title = $derived(count === 1 ? 'Skips' : `Skips of ${songs(count)}`)
  const detail = $derived(
    current.length === 1
      ? `${current[0].playCount.toLocaleString()} ${current[0].playCount === 1 ? 'play' : 'plays'}`
      : mixed
        ? 'These songs have different skip counts.'
        : '',
  )
</script>

<svelte:window onpointerdown={onPointerdown} onblur={ui.closeSkipsEditor} onresize={ui.closeSkipsEditor} />

<div
  class="skips-editor popover"
  role="dialog"
  aria-label={title}
  tabindex="-1"
  bind:this={popover}
  style:left="{position.x}px"
  style:top="{position.y}px"
  onkeydown={onKeydown}
>
  <p class="title">{title}</p>

  <input
    bind:this={input}
    bind:value
    inputmode="numeric"
    placeholder={mixed ? 'mixed' : '0'}
    autocomplete="off"
    spellcheck="false"
    maxlength={5}
    aria-label="Skips"
    aria-invalid={value !== '' && !valid}
  />
  <p class="detail">{detail}</p>

  <div class="actions">
    <button class="link" disabled={busy} onclick={() => apply(0)} title="Set the skip count to zero">Clear</button>
    <button class="primary" disabled={busy || !valid} onclick={save}>Save</button>
  </div>
</div>

<style>
  .skips-editor {
    position: fixed;
    z-index: 50;
    width: 220px;
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

  input {
    box-sizing: border-box;
    width: 100%;
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

  .detail {
    min-height: 16px;
    margin: 6px 2px 8px;
    font-size: 11px;
    color: var(--text-dim);
  }

  .actions {
    display: flex;
    align-items: center;
    justify-content: space-between;
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
