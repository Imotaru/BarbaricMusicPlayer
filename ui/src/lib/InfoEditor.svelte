<script lang="ts">
  import { untrack } from 'svelte'
  import { library, songs, type InfoField, type TrackInfo } from './library.svelte'
  import { ui, type Point } from './ui.svelte'

  const MAX_NUMBER = 9999

  interface Field {
    key: InfoField
    label: string
    number?: boolean
  }

  const WIDE: Field[] = [
    { key: 'title', label: 'Title' },
    { key: 'artist', label: 'Artist' },
    { key: 'album', label: 'Album' },
    { key: 'albumArtist', label: 'Album artist' },
  ]
  const NARROW: Field[] = [
    { key: 'genre', label: 'Genre' },
    { key: 'year', label: 'Year', number: true },
    { key: 'trackNumber', label: 'Track', number: true },
  ]
  const FIELDS = [...WIDE, ...NARROW]

  const props: { trackIds: number[]; at: Point } = $props()

  // A snapshot, like the other editors: it keeps acting on the songs it was opened for.
  const { trackIds, at } = untrack(() => ({ ...props }))
  const count = trackIds.length

  const blank = () => Object.fromEntries(FIELDS.map((f) => [f.key, ''])) as Record<InfoField, string>

  let popover = $state<HTMLDivElement>()
  let position = $state({ x: 0, y: 0 })
  let current = $state.raw<TrackInfo[]>([])
  /** What each field showed when loaded: the songs' shared value, or empty when they differ. */
  let initial = $state.raw(blank())
  let mixed = $state.raw<Partial<Record<InfoField, boolean>>>({})
  let values = $state(blank())
  /** Fields staged to go back to the file's tags on Save. */
  let resets = $state<Partial<Record<InfoField, boolean>>>({})
  let busy = $state(false)

  const text = (v: string | number | null | undefined) => (v === null || v === undefined ? '' : String(v))

  async function load() {
    current = await library.getInfo(trackIds)
    const shared = blank()
    const differ: Partial<Record<InfoField, boolean>> = {}
    for (const { key } of FIELDS) {
      const seen = new Set(current.map((t) => text(t[key])))
      differ[key] = seen.size > 1
      shared[key] = seen.size === 1 ? [...seen][0] : ''
    }
    initial = shared
    mixed = differ
    values = { ...shared }
    focusTitle()
  }

  function focusTitle() {
    const title = popover?.querySelector('input')
    title?.focus()
    title?.select()
  }

  load().catch(ui.fail)

  $effect(() => {
    if (!popover) return
    const { width, height } = popover.getBoundingClientRect()
    position = {
      x: Math.max(4, Math.min(at.x, innerWidth - width - 4)),
      y: Math.max(4, Math.min(at.y, innerHeight - height - 4)),
    }
    focusTitle()
  })

  const overridden = $derived(FIELDS.filter((f) => current.some((t) => t.overridden.includes(f.key))).map((f) => f.key))
  const changed = $derived(FIELDS.filter((f) => !resets[f.key] && values[f.key] !== initial[f.key]))
  const staged = $derived(FIELDS.filter((f) => resets[f.key]).map((f) => f.key))

  function invalid(field: Field) {
    if (resets[field.key] || values[field.key] === initial[field.key]) return false
    const value = values[field.key].trim()
    if (field.key === 'title') return value === ''
    if (!field.number || value === '') return false
    return !/^\d+$/.test(value) || Number(value) < 1 || Number(value) > MAX_NUMBER
  }

  const valid = $derived(FIELDS.every((f) => !invalid(f)))
  const canSave = $derived(!busy && current.length > 0 && valid && (changed.length > 0 || staged.length > 0))

  async function apply(set: Partial<Record<InfoField, string | number | null>>, reset: InfoField[], message: string) {
    if (busy) return
    busy = true
    try {
      await library.setInfo(trackIds, set, reset)
      if (count > 1) ui.notify(message)
      ui.closeInfoEditor()
    } catch (e) {
      ui.fail(e)
      busy = false
      focusTitle()
    }
  }

  function save() {
    if (!canSave) return
    const set: Partial<Record<InfoField, string | number | null>> = {}
    for (const field of changed) {
      const value = values[field.key].trim()
      set[field.key] = value === '' ? null : field.number ? Number(value) : value
    }
    apply(set, staged, `Updated ${songs(count)}.`)
  }

  const useFileTags = () => apply({}, overridden, `Put back the file tags of ${songs(count)}.`)

  function stageReset(key: InfoField) {
    resets[key] = true
    values[key] = ''
  }

  function onKeydown(e: KeyboardEvent) {
    e.stopPropagation()
    if (e.key === 'Enter') {
      e.preventDefault()
      save()
    } else if (e.key === 'Escape') {
      e.preventDefault()
      ui.closeInfoEditor()
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
    if (!popover?.contains(e.target as Node)) ui.closeInfoEditor()
  }

  const title = count === 1 ? 'Edit song' : `Edit ${songs(count)}`
  const detail = $derived(
    current.length === 1
      ? current[0].fileName
      : Object.values(mixed).some(Boolean)
        ? 'Fields left as they are keep each song’s own value.'
        : '',
  )

  function placeholder(key: InfoField) {
    if (resets[key]) return 'From the file'
    if (mixed[key]) return 'Multiple values'
    return ''
  }
</script>

<svelte:window onpointerdown={onPointerdown} onblur={ui.closeInfoEditor} onresize={ui.closeInfoEditor} />

{#snippet input(field: Field)}
  <label class="field" class:narrow={field.number}>
    <span class="label">
      {field.label}
      {#if overridden.includes(field.key) && !resets[field.key]}
        <button
          class="reset"
          type="button"
          disabled={busy}
          title="Use the file’s tag"
          aria-label="Use the file’s tag for {field.label.toLowerCase()}"
          onclick={() => stageReset(field.key)}>↺</button
        >
      {/if}
    </span>
    <input
      bind:value={values[field.key]}
      oninput={() => (resets[field.key] = false)}
      placeholder={placeholder(field.key)}
      inputmode={field.number ? 'numeric' : undefined}
      maxlength={field.number ? 4 : undefined}
      autocomplete="off"
      spellcheck="false"
      aria-invalid={invalid(field)}
    />
  </label>
{/snippet}

<div
  class="info-editor popover"
  role="dialog"
  aria-label={title}
  tabindex="-1"
  bind:this={popover}
  style:left="{position.x}px"
  style:top="{position.y}px"
  onkeydown={onKeydown}
>
  <p class="title">{title}</p>
  <p class="detail" title={detail}>{detail}</p>

  {#each WIDE as field (field.key)}
    {@render input(field)}
  {/each}
  <div class="row">
    {#each NARROW as field (field.key)}
      {@render input(field)}
    {/each}
  </div>

  <div class="actions">
    {#if overridden.length > 0}
      <button class="link" disabled={busy} onclick={useFileTags} title="Undo every edit and show what the file’s tags say">
        Use file tags
      </button>
    {:else}
      <span></span>
    {/if}
    <button class="primary" disabled={!canSave} onclick={save}>Save</button>
  </div>
</div>

<style>
  .info-editor {
    position: fixed;
    z-index: 50;
    width: 360px;
    padding: 10px 12px 12px;
    outline: 0;
  }

  .title {
    margin: 0 2px 2px;
    font-size: 11px;
    font-weight: 700;
    letter-spacing: 0.08em;
    text-transform: uppercase;
    color: var(--text-dim);
  }

  .detail {
    min-height: 16px;
    margin: 0 2px 6px;
    overflow: hidden;
    font-size: 11px;
    color: var(--text-dim);
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .field {
    display: flex;
    flex-direction: column;
    gap: 3px;
    margin-bottom: 8px;
  }

  .row {
    display: flex;
    gap: 8px;
  }

  .row .field {
    flex: 1;
    min-width: 0;
  }

  .row .field.narrow {
    flex: 0 0 64px;
  }

  .label {
    display: flex;
    align-items: center;
    gap: 4px;
    height: 16px;
    margin: 0 2px;
    font-size: 11px;
    color: var(--text-dim);
  }

  .reset {
    padding: 0 3px;
    border: 0;
    border-radius: 4px;
    background: none;
    color: var(--accent);
    font-size: 12px;
    line-height: 16px;
  }

  .reset:hover:not(:disabled) {
    background: var(--surface-hover);
  }

  input {
    box-sizing: border-box;
    width: 100%;
    height: 30px;
    padding: 0 9px;
    border: 1px solid var(--border);
    border-radius: 8px;
    outline: 0;
    background: var(--bg);
    color: var(--text);
    font: inherit;
    font-size: 13px;
  }

  input:focus {
    border-color: var(--accent);
  }

  input[aria-invalid='true'] {
    border-color: var(--danger);
  }

  input::placeholder {
    color: var(--text-dim);
    font-style: italic;
  }

  .actions {
    display: flex;
    align-items: center;
    justify-content: space-between;
    margin-top: 4px;
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
