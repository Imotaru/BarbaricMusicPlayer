<script lang="ts">
  import { untrack } from 'svelte'
  import { library, songs } from './library.svelte'
  import { playlists, type Playlist } from './playlists.svelte'
  import { tags, type Tag } from './tags.svelte'
  import { ui, type Point } from './ui.svelte'

  const props: { mode: 'tag' | 'playlist'; trackIds: number[]; at: Point } = $props()

  // A snapshot: the picker acts on the songs it was opened for, and keeps doing so while it
  // closes (closing clears ui.picker, which the props are read from).
  const { mode, trackIds, at } = untrack(() => ({ ...props }))
  const count = trackIds.length

  type Item =
    | { kind: 'tag'; tag: Tag; state: 'all' | 'some' | 'none' }
    | { kind: 'playlist'; playlist: Playlist }
    | { kind: 'create'; name: string }

  let popover = $state<HTMLDivElement>()
  let input = $state<HTMLInputElement>()
  let position = $state({ x: 0, y: 0 })
  let query = $state('')
  let active = $state(0)
  let busy = $state(false)
  /** Tag mode: how many of the chosen tracks carry each tag. */
  let usage = $state.raw(new Map<number, number>())

  const items = $derived.by((): Item[] => {
    const q = query.trim().toLowerCase()
    const matches = (name: string) => name.toLowerCase().includes(q)
    const exact = (name: string) => name.toLowerCase() === q
    if (mode === 'tag') {
      const list: Item[] = tags.list.filter((t) => matches(t.name)).map((tag) => {
        const n = usage.get(tag.id) ?? 0
        return { kind: 'tag', tag, state: n === 0 ? 'none' : n >= count ? 'all' : 'some' }
      })
      if (q && !tags.list.some((t) => exact(t.name))) list.push({ kind: 'create', name: query.trim() })
      return list
    }
    const list: Item[] = playlists.manual.filter((p) => matches(p.name)).map((playlist) => ({ kind: 'playlist', playlist }))
    if (!playlists.manual.some((p) => exact(p.name))) list.push({ kind: 'create', name: query.trim() })
    return list
  })

  $effect(() => {
    if (mode === 'tag') tags.usage(trackIds).then((u) => (usage = u), ui.fail)
  })

  // Place below/right of the anchor but inside the window, then focus the filter box.
  $effect(() => {
    if (!popover) return
    const { width, height } = popover.getBoundingClientRect()
    position = {
      x: Math.max(4, Math.min(at.x, innerWidth - width - 4)),
      y: Math.max(4, Math.min(at.y, innerHeight - height - 4)),
    }
    input?.focus()
  })

  $effect(() => {
    query
    active = 0
  })

  async function choose(item: Item | undefined) {
    if (!item || busy) return
    busy = true
    try {
      if (item.kind === 'tag') await toggle(item.tag.id, item.state !== 'all')
      else if (item.kind === 'playlist') await addTo(item.playlist)
      else if (mode === 'tag') {
        const tag = await tags.create(item.name)
        await toggle(tag.id, true)
        query = ''
      } else {
        const name = item.name || 'New playlist'
        const id = await playlists.createManual(name, trackIds)
        ui.closePicker()
        ui.notify(`Created ${name} with ${songs(count)}.`)
        if (!item.name) ui.renaming = { kind: 'playlist', id }
      }
    } catch (e) {
      ui.fail(e)
    } finally {
      busy = false
      input?.focus()
    }
  }

  // The first toggle tags every chosen song; toggling a tag they all have untags them.
  async function toggle(tagId: number, add: boolean) {
    library.keepListed(trackIds)
    await tags.apply(tagId, trackIds, add)
    usage = new Map(usage).set(tagId, add ? count : 0)
  }

  async function addTo(playlist: Playlist) {
    const added = await playlists.addTracks(playlist.id, trackIds)
    ui.closePicker()
    const skipped = count - added
    ui.notify(
      added === 0
        ? `${count === 1 ? 'That song is' : 'Those songs are'} already in ${playlist.name}.`
        : `Added ${songs(added)} to ${playlist.name}${skipped > 0 ? ` (${skipped.toLocaleString()} already there)` : ''}.`,
    )
  }

  function onKeydown(e: KeyboardEvent) {
    e.stopPropagation()
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault()
      if (items.length > 0) active = (active + (e.key === 'ArrowDown' ? 1 : -1) + items.length) % items.length
    } else if (e.key === 'Enter') {
      e.preventDefault()
      choose(items[active])
    } else if (e.key === 'Escape' || e.key === 'Tab') {
      e.preventDefault()
      ui.closePicker()
    }
  }

  function onPointerdown(e: PointerEvent) {
    if (!popover?.contains(e.target as Node)) ui.closePicker()
  }

  // Keep the highlighted row in view while arrowing through a long list.
  $effect(() => {
    popover?.querySelector(`[data-item="${active}"]`)?.scrollIntoView({ block: 'nearest' })
  })

  const title = $derived(
    mode === 'tag'
      ? `Tag ${songs(count)}`
      : `Add ${songs(count)} to${library.view.kind === 'manual' ? ' another' : ' a'} playlist`,
  )
</script>

<svelte:window onpointerdown={onPointerdown} onblur={ui.closePicker} onresize={ui.closePicker} />

<div
  class="picker popover"
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
    bind:value={query}
    placeholder={mode === 'tag' ? 'Find or create a tag…' : 'Find or name a playlist…'}
    autocomplete="off"
    spellcheck="false"
    maxlength={100}
    aria-controls="picker-list"
  />
  <ul id="picker-list" role="listbox" aria-busy={busy}>
    {#each items as item, i (item.kind === 'tag' ? `t${item.tag.id}` : item.kind === 'playlist' ? `p${item.playlist.id}` : 'new')}
      <!-- The filter box handles the keyboard for the whole list. -->
      <!-- svelte-ignore a11y_click_events_have_key_events -->
      <li
        role="option"
        aria-selected={i === active}
        class:active={i === active}
        data-item={i}
        onpointerenter={() => (active = i)}
        onclick={() => choose(item)}
      >
        {#if item.kind === 'tag'}
          <span class="check {item.state}" style:--c={item.tag.color} aria-label={item.state === 'some' ? 'on some' : item.state === 'all' ? 'on all' : 'off'}>
            {#if item.state === 'all'}
              <svg viewBox="0 0 10 10"><path d="M2 5.2l2 2 4-4.4" /></svg>
            {:else if item.state === 'some'}
              <svg viewBox="0 0 10 10"><path d="M2.5 5h5" /></svg>
            {/if}
          </span>
          <span class="name">{item.tag.name}</span>
          <span class="count">{item.tag.count.toLocaleString()}</span>
        {:else if item.kind === 'playlist'}
          <svg class="icon" viewBox="0 0 16 16" aria-hidden="true"><path d="M2 4h9M2 8h9M2 12h6M13 10v5M10.5 12.5h5" /></svg>
          <span class="name">{item.playlist.name}</span>
          <span class="count">{(item.playlist.count ?? 0).toLocaleString()}</span>
        {:else}
          <svg class="icon" viewBox="0 0 16 16" aria-hidden="true"><path d="M8 3v10M3 8h10" /></svg>
          <span class="name">
            {#if mode === 'tag'}Create tag “{item.name}”{:else if item.name}New playlist “{item.name}”{:else}New playlist…{/if}
          </span>
        {/if}
      </li>
    {:else}
      <li class="empty">{mode === 'tag' ? 'Type a name to create your first tag.' : 'No playlists yet.'}</li>
    {/each}
  </ul>
  {#if mode === 'tag'}
    <p class="hint"><kbd>Enter</kbd> toggle · <kbd>Esc</kbd> done</p>
  {/if}
</div>

<style>
  .picker {
    position: fixed;
    z-index: 50;
    width: 300px;
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
    width: 100%;
    height: 32px;
    padding: 0 10px;
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

  ul {
    list-style: none;
    margin: 6px 0 0;
    padding: 0;
    max-height: 280px;
    overflow-y: auto;
  }

  li {
    display: flex;
    align-items: center;
    gap: 10px;
    height: 32px;
    padding: 0 8px;
    border-radius: 6px;
    font-size: 13px;
    cursor: default;
  }

  li.active {
    background: var(--surface-hover);
  }

  li.empty {
    color: var(--text-dim);
    font-size: 12px;
  }

  .name {
    flex: 1;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .count {
    color: var(--text-dim);
    font-size: 11px;
    font-variant-numeric: tabular-nums;
  }

  .check {
    flex: none;
    display: grid;
    place-items: center;
    width: 16px;
    height: 16px;
    border: 1.5px solid var(--c);
    border-radius: 4px;
  }

  .check.all,
  .check.some {
    background: var(--c);
  }

  .check svg {
    width: 10px;
    height: 10px;
    fill: none;
    stroke: var(--bg);
    stroke-width: 1.8;
  }

  .icon {
    flex: none;
    width: 16px;
    height: 16px;
    fill: none;
    stroke: var(--text-dim);
    stroke-width: 1.4;
  }

  .hint {
    margin: 8px 2px 0;
    font-size: 11px;
    color: var(--text-dim);
  }

  kbd {
    font: inherit;
    padding: 0 4px;
    border: 1px solid var(--border);
    border-radius: 3px;
  }
</style>
