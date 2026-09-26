<script lang="ts">
  import { openRowMenu, pointOf } from './actions'
  import FilterBar from './FilterBar.svelte'
  import { library, type SortKey } from './library.svelte'
  import { formatTime, player } from './player.svelte'
  import { tags } from './tags.svelte'
  import ViewHeader from './ViewHeader.svelte'

  const ROW_HEIGHT = 34
  const OVERSCAN = 12

  type Column = { key: SortKey | null; label: string; numeric?: boolean; optional?: boolean }

  const columns = $derived<Column[]>([
    ...(library.view.kind === 'manual' ? [{ key: 'position', label: '#', numeric: true } as Column] : []),
    { key: 'title', label: 'Title' },
    { key: 'artist', label: 'Artist' },
    { key: 'album', label: 'Album', optional: true },
    { key: null, label: 'Tags', optional: true },
    { key: 'bpm', label: 'BPM', numeric: true, optional: true },
    { key: 'duration', label: 'Time', numeric: true },
  ])

  let viewport = $state<HTMLDivElement>()
  let scrollTop = $state(0)
  let height = $state(0)

  // Only the rows in (and just around) the viewport exist in the DOM.
  const start = $derived(Math.max(0, Math.floor(scrollTop / ROW_HEIGHT) - OVERSCAN))
  const end = $derived(Math.min(library.total, Math.ceil((scrollTop + height) / ROW_HEIGHT) + OVERSCAN))
  const indexes = $derived(Array.from({ length: Math.max(0, end - start) }, (_, i) => start + i))

  const narrowed = $derived(library.text.trim() !== '' || library.hasFilter)

  $effect(() => library.ensureRange(start, end))

  // A new search, sort, filter or view starts at the top.
  $effect(() => {
    library.scrollResets
    if (viewport) viewport.scrollTop = 0
  })

  // Keep the cursor row visible when it moves via the keyboard.
  $effect(() => {
    const top = library.cursor * ROW_HEIGHT
    if (!viewport) return
    if (top < viewport.scrollTop) viewport.scrollTop = top
    else if (top + ROW_HEIGHT > viewport.scrollTop + viewport.clientHeight) {
      viewport.scrollTop = top + ROW_HEIGHT - viewport.clientHeight
    }
  })

  function onSearchKeydown(e: KeyboardEvent) {
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault()
      library.moveCursor(e.key === 'ArrowDown' ? 1 : -1)
    } else if (e.key === 'Enter') {
      e.preventDefault()
      library.playSelected()
    } else if (e.key === 'Escape') {
      if (library.text) library.setText('')
      else e.currentTarget instanceof HTMLElement && e.currentTarget.blur()
    }
  }

  function onRowContextmenu(e: MouseEvent, index: number) {
    e.preventDefault()
    if (library.row(index)) openRowMenu(pointOf(e), index)
  }
</script>

<section class="tracks" class:numbered={library.view.kind === 'manual'}>
  <ViewHeader />

  <div class="search">
    <svg viewBox="0 0 16 16" aria-hidden="true"><circle cx="7" cy="7" r="4.5" /><path d="M10.5 10.5l3.5 3.5" /></svg>
    <input
      id="search"
      type="search"
      placeholder={library.view.kind === 'library' ? 'Search songs, artists, albums…' : 'Search this playlist…'}
      autocomplete="off"
      spellcheck="false"
      value={library.text}
      oninput={(e) => library.setText(e.currentTarget.value)}
      onkeydown={onSearchKeydown}
    />
    <kbd>Ctrl F</kbd>
  </div>

  <FilterBar />

  <div class="row header" role="row">
    {#each columns as column (column.label)}
      {#if column.key}
        {@const key = column.key}
        <button
          class="cell"
          class:numeric={column.numeric}
          class:optional={column.optional}
          class:active={library.sort === key}
          onclick={() => library.setSort(key)}
        >
          {column.label}
          {#if library.sort === key}
            <span class="arrow">{library.desc ? '▾' : '▴'}</span>
          {/if}
        </button>
      {:else}
        <span class="cell" class:optional={column.optional}>{column.label}</span>
      {/if}
    {/each}
  </div>

  <div
    class="viewport"
    bind:this={viewport}
    bind:clientHeight={height}
    onscroll={(e) => (scrollTop = e.currentTarget.scrollTop)}
    role="grid"
    aria-rowcount={library.total}
    aria-multiselectable="true"
  >
    {#if library.loaded && library.total === 0}
      <div class="empty">
        {#if library.view.kind === 'library' && library.folders.length === 0}
          <p class="big">Your library is empty</p>
          <p>Add the folders where your music lives and they'll be scanned automatically.</p>
          <button class="cta" onclick={library.addFolder}>Add music folder</button>
        {:else if narrowed}
          <p class="big">No matches</p>
          {#if library.text.trim()}
            <p>Nothing {library.hasFilter ? 'with these tags ' : ''}matches “{library.text}”.</p>
          {:else}
            <p>No songs have this combination of tags.</p>
          {/if}
        {:else if library.view.kind === 'manual'}
          <p class="big">This playlist is empty</p>
          <p>Select songs anywhere in your library and press <kbd>P</kbd> to add them.</p>
        {:else if library.scan.running}
          <p class="big">Scanning…</p>
        {:else}
          <p class="big">No songs found</p>
          <p>The library folders don't contain any supported audio files.</p>
        {/if}
      </div>
    {:else}
      <div class="spacer" style:height="{library.total * ROW_HEIGHT}px">
        {#each indexes as index (index)}
          {@const row = library.row(index)}
          {@const selected = library.isSelected(index, row?.id)}
          <!-- Keyboard selection and playback are handled app-wide in App.svelte. -->
          <!-- svelte-ignore a11y_click_events_have_key_events -->
          <div
            class="row"
            class:selected
            class:cursor={library.selection.size > 0 && index === library.cursor}
            class:playing={row !== undefined && row.id === player.trackId}
            style:transform="translateY({index * ROW_HEIGHT}px)"
            role="row"
            tabindex="-1"
            aria-rowindex={index + 1}
            aria-selected={selected}
            data-index={index}
            onclick={(e) => library.click(index, e)}
            ondblclick={(e) => !e.ctrlKey && !e.shiftKey && library.playIndex(index)}
            oncontextmenu={(e) => onRowContextmenu(e, index)}
          >
            {#if row}
              {#if library.view.kind === 'manual'}
                <span class="cell numeric dim">{row.position === null ? '' : row.position + 1}</span>
              {/if}
              <span class="cell title">
                {#if row.id === player.trackId}
                  <svg class="now" viewBox="0 0 12 12" aria-label="Now playing"><path d="M1 4h2v4H1zM5 2h2v8H5zM9 5h2v2H9z" /></svg>
                {/if}
                {row.title}
              </span>
              <span class="cell dim">{row.artist ?? ''}</span>
              <span class="cell dim optional">{row.album ?? ''}</span>
              <span class="cell chips optional">
                {#each tags.resolve(row.tagIds) as tag (tag.id)}
                  <span class="chip" style:--c={tag.color}>{tag.name}</span>
                {/each}
              </span>
              <span class="cell numeric dim optional">{row.bpm ? Math.round(row.bpm) : ''}</span>
              <span class="cell numeric dim">{row.durationMs ? formatTime(row.durationMs / 1000) : ''}</span>
            {/if}
          </div>
        {/each}
      </div>
    {/if}
  </div>
</section>

<style>
  .tracks {
    --columns: minmax(0, 2.2fr) minmax(0, 1.4fr) minmax(0, 1.4fr) minmax(0, 1.3fr) 56px 64px;
    display: flex;
    flex-direction: column;
    min-height: 0;
    min-width: 0;
  }

  .tracks.numbered {
    --columns: 36px minmax(0, 2.2fr) minmax(0, 1.4fr) minmax(0, 1.4fr) minmax(0, 1.3fr) 56px 64px;
  }

  .search {
    flex: none;
    display: flex;
    align-items: center;
    gap: 10px;
    margin: 6px 20px 10px;
    padding: 0 12px;
    height: 38px;
    border-radius: 10px;
    background: var(--surface);
    border: 1px solid var(--border);
  }

  .search:focus-within {
    border-color: var(--accent);
  }

  .search svg {
    flex: none;
    width: 15px;
    height: 15px;
    fill: none;
    stroke: var(--text-dim);
    stroke-width: 1.5;
  }

  .search input {
    flex: 1;
    min-width: 0;
    border: 0;
    outline: 0;
    background: transparent;
    color: var(--text);
    font: inherit;
    font-size: 14px;
  }

  .search input::placeholder {
    color: var(--text-dim);
  }

  .search input::-webkit-search-cancel-button {
    display: none;
  }

  kbd {
    flex: none;
    padding: 1px 6px;
    border-radius: 4px;
    border: 1px solid var(--border);
    font: inherit;
    font-size: 11px;
    color: var(--text-dim);
  }

  .row {
    display: grid;
    grid-template-columns: var(--columns);
    align-items: center;
    gap: 16px;
    height: 34px;
    padding: 0 20px;
    font-size: 13px;
  }

  .header {
    flex: none;
    height: 30px;
    border-bottom: 1px solid var(--border);
  }

  .header .cell {
    padding: 0;
    border: 0;
    background: none;
    text-align: left;
    font-size: 11px;
    font-weight: 600;
    letter-spacing: 0.08em;
    text-transform: uppercase;
    color: var(--text-dim);
  }

  .header .cell:hover,
  .header .cell.active {
    color: var(--text);
  }

  .arrow {
    margin-left: 2px;
  }

  .viewport {
    flex: 1;
    position: relative;
    overflow-y: auto;
    min-height: 0;
    outline: 0;
  }

  .spacer {
    position: relative;
  }

  .spacer .row {
    position: absolute;
    top: 0;
    left: 0;
    right: 0;
    cursor: default;
    user-select: none;
  }

  .spacer .row:hover {
    background: var(--surface);
  }

  .spacer .row.selected {
    background: var(--surface-hover);
  }

  /* With several rows selected, a thin bar marks the one the keyboard is on. */
  .spacer .row.cursor {
    box-shadow: inset 2px 0 0 var(--accent);
  }

  .row.playing .title {
    color: var(--accent);
  }

  .cell {
    min-width: 0;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .cell.numeric {
    text-align: right;
    font-variant-numeric: tabular-nums;
  }

  .dim {
    color: var(--text-dim);
  }

  .chips {
    display: flex;
    gap: 4px;
    text-overflow: clip;
    mask-image: linear-gradient(to left, transparent, black 14px);
  }

  .chip {
    flex: none;
    padding: 1px 7px;
    border-radius: 999px;
    background: color-mix(in srgb, var(--c) 22%, transparent);
    color: color-mix(in srgb, var(--c) 75%, white);
    font-size: 11px;
    font-weight: 600;
    line-height: 16px;
  }

  .empty kbd {
    padding: 0 5px;
    border: 1px solid var(--border);
    border-radius: 4px;
    font: inherit;
    font-size: 12px;
  }

  .now {
    width: 11px;
    height: 11px;
    margin-right: 6px;
    fill: currentColor;
    vertical-align: -1px;
  }

  .empty {
    display: grid;
    justify-items: center;
    gap: 4px;
    padding: 80px 24px;
    text-align: center;
    color: var(--text-dim);
    font-size: 14px;
  }

  .empty p {
    margin: 0;
  }

  .empty .big {
    font-size: 20px;
    font-weight: 600;
    color: var(--text);
  }

  .cta {
    margin-top: 16px;
    padding: 10px 20px;
    border: 0;
    border-radius: 999px;
    background: var(--accent);
    color: var(--accent-contrast);
    font-weight: 600;
  }

  @media (max-width: 760px) {
    .tracks {
      --columns: minmax(0, 2fr) minmax(0, 1.4fr) 56px;
    }

    .tracks.numbered {
      --columns: 28px minmax(0, 2fr) minmax(0, 1.4fr) 56px;
    }

    .optional {
      display: none;
    }
  }
</style>
