<script lang="ts">
  import { canRun, COMMANDS } from './commands'
  import { keymap } from './keymap.svelte'
  import { library } from './library.svelte'
  import { playlists } from './playlists.svelte'
  import { tags } from './tags.svelte'
  import { ui } from './ui.svelte'

  interface Entry {
    key: string
    label: string
    group: string
    shortcut?: string
    run: () => void
  }

  let input = $state<HTMLInputElement>()
  let list = $state<HTMLUListElement>()
  let query = $state('')
  let active = $state(0)

  // Taken when the palette opens: what can run depends on the view it was opened over.
  const entries: Entry[] = [
    ...COMMANDS.filter((c) => !c.hidden && canRun(c)).map((c) => ({
      key: c.id,
      label: c.label,
      group: c.group,
      shortcut: keymap.label(c.id),
      run: c.run,
    })),
    ...playlists.list.map((p) => ({
      key: `playlist:${p.id}`,
      label: p.name,
      group: 'Playlist',
      run: () => library.openPlaylist(p),
    })),
    ...tags.list.map((t) => ({ key: `tag:${t.id}`, label: t.name, group: 'Tag', run: () => library.showTag(t.id) })),
    ...(tags.list.length > 0 ? [{ key: 'tag:untagged', label: 'Untagged', group: 'Tag', run: library.showUntagged }] : []),
  ]

  // Every word typed has to appear somewhere in the name or its group, in any order.
  const shown = $derived.by(() => {
    const words = query.toLowerCase().split(/\s+/).filter(Boolean)
    if (words.length === 0) return entries.filter((e) => e.group !== 'Playlist' && e.group !== 'Tag')
    return entries.filter((e) => {
      const text = `${e.label} ${e.group}`.toLowerCase()
      return words.every((w) => text.includes(w))
    })
  })

  $effect(() => input?.focus())

  $effect(() => {
    query
    active = 0
  })

  $effect(() => {
    list?.querySelector(`[data-item="${active}"]`)?.scrollIntoView({ block: 'nearest' })
  })

  function choose(entry: Entry | undefined) {
    if (!entry) return
    ui.closePalette()
    entry.run()
  }

  function onKeydown(e: KeyboardEvent) {
    e.stopPropagation()
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault()
      if (shown.length > 0) active = (active + (e.key === 'ArrowDown' ? 1 : -1) + shown.length) % shown.length
    } else if (e.key === 'Enter') {
      e.preventDefault()
      choose(shown[active])
    } else if (e.key === 'Escape' || e.key === 'Tab') {
      e.preventDefault()
      ui.closePalette()
    }
  }
</script>

<svelte:window onblur={ui.closePalette} />

<!-- svelte-ignore a11y_click_events_have_key_events, a11y_no_static_element_interactions -->
<div class="scrim" onclick={(e) => e.target === e.currentTarget && ui.closePalette()}>
  <div class="palette popover" role="dialog" aria-label="Command palette" tabindex="-1" onkeydown={onKeydown}>
    <input
      bind:this={input}
      bind:value={query}
      placeholder="Type a command, playlist or tag…"
      autocomplete="off"
      spellcheck="false"
      aria-controls="palette-list"
    />
    <ul id="palette-list" role="listbox" bind:this={list}>
      {#each shown as entry, i (entry.key)}
        <!-- The input handles the keyboard for the whole list. -->
        <!-- svelte-ignore a11y_click_events_have_key_events -->
        <li
          role="option"
          aria-selected={i === active}
          class:active={i === active}
          data-item={i}
          onpointermove={() => (active = i)}
          onclick={() => choose(entry)}
        >
          <span class="group">{entry.group}</span>
          <span class="label">{entry.label}</span>
          {#if entry.shortcut}<kbd>{entry.shortcut}</kbd>{/if}
        </li>
      {:else}
        <li class="empty">Nothing matches “{query.trim()}”.</li>
      {/each}
    </ul>
  </div>
</div>

<style>
  .scrim {
    position: fixed;
    inset: 0;
    z-index: 60;
    display: grid;
    justify-items: center;
    align-items: start;
    padding: 12vh 16px 16px;
    background: var(--scrim);
  }

  .palette {
    width: min(560px, 100%);
    padding: 8px;
    outline: 0;
  }

  input {
    width: 100%;
    height: 38px;
    padding: 0 12px;
    border: 1px solid var(--border);
    border-radius: 8px;
    outline: 0;
    background: var(--bg);
    color: var(--text);
    font: inherit;
    font-size: 14px;
  }

  input:focus {
    border-color: var(--accent);
  }

  ul {
    list-style: none;
    margin: 6px 0 0;
    padding: 0;
    max-height: min(360px, 56vh);
    overflow-y: auto;
  }

  li {
    display: flex;
    align-items: center;
    gap: 10px;
    height: 34px;
    padding: 0 10px;
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

  .group {
    flex: none;
    width: 64px;
    font-size: 11px;
    color: var(--text-dim);
  }

  .label {
    flex: 1;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  kbd {
    flex: none;
    font: inherit;
    font-size: 11px;
    padding: 1px 6px;
    border: 1px solid var(--border);
    border-radius: 4px;
    color: var(--text-dim);
  }
</style>
