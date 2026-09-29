<script lang="ts">
  import { openBpmEditor, openRowMenu, pointOf } from './actions'
  import { describeBpm, formatBpm, isUnsure } from './bpm.svelte'
  import BpmRange from './BpmRange.svelte'
  import FilterBar from './FilterBar.svelte'
  import { library, type SortKey } from './library.svelte'
  import { keymap } from './keymap.svelte'
  import { formatTime, player } from './player.svelte'
  import { prefs } from './prefs.svelte'
  import { formatRange } from './query'
  import { tags } from './tags.svelte'
  import ViewHeader from './ViewHeader.svelte'

  const ROW_HEIGHT = 34
  const OVERSCAN = 12

  /** Text columns share the free space by weight; the others have a fixed width in pixels. */
  type Column = { id: string; key: SortKey | null; label: string; flex?: boolean; numeric?: boolean; optional?: boolean }

  const DEFAULT_WIDTHS: Record<string, number> = {
    position: 36,
    title: 220,
    artist: 140,
    album: 140,
    path: 140,
    tags: 130,
    bpm: 56,
    plays: 60,
    skips: 60,
    duration: 64,
  }
  const MIN_FLEX_WIDTH = 40
  const MIN_FIXED_WIDTH = 28
  const minWidth = (c: Column) => Math.min(c.flex ? MIN_FLEX_WIDTH : MIN_FIXED_WIDTH, DEFAULT_WIDTHS[c.id])

  // Suggested for removal is about skips, so it shows them even when song lists otherwise don't.
  const showSkips = $derived(prefs.showSkips || library.view.kind === 'suggested')

  const columns = $derived<Column[]>([
    ...(library.view.kind === 'manual' || library.view.kind === 'playing'
      ? [{ id: 'position', key: 'position', label: '#', numeric: true } as Column]
      : []),
    { id: 'title', key: 'title', label: 'Title', flex: true },
    { id: 'artist', key: 'artist', label: 'Artist', flex: true },
    // Missing songs show where their file was last seen, to help find it again.
    library.view.kind === 'missing'
      ? { id: 'path', key: null, label: 'Last seen at', flex: true, optional: true }
      : { id: 'album', key: 'album', label: 'Album', flex: true, optional: true },
    { id: 'tags', key: null, label: 'Tags', flex: true, optional: true },
    { id: 'bpm', key: 'bpm', label: 'BPM', numeric: true, optional: true },
    { id: 'plays', key: 'plays', label: 'Plays', numeric: true, optional: true },
    ...(showSkips ? [{ id: 'skips', key: 'skips', label: 'Skips', numeric: true, optional: true } as Column] : []),
    { id: 'duration', key: 'duration', label: 'Time', numeric: true },
  ])

  // Last seen at takes Album's place, so it starts at Album's width.
  const widthOf = (id: string) =>
    prefs.columnWidths[id] ?? (id === 'path' ? prefs.columnWidths.album : undefined) ?? DEFAULT_WIDTHS[id]

  const template = $derived(
    columns.map((c) => (c.flex ? `minmax(0, ${widthOf(c.id)}fr)` : `${widthOf(c.id)}px`)).join(' '),
  )

  let header = $state<HTMLDivElement>()
  let drag = $state<{ pointer: number; x: number; widths: Record<string, number>; left: Column; right: Column } | null>(null)

  /**
   * Every shown column's current width in pixels. Text columns' weights then equal their pixels, so trading
   * width between two neighbours moves their shared edge by exactly that much.
   */
  function measure(): Record<string, number> {
    const widths = { ...prefs.columnWidths }
    const cells = header ? [...header.querySelectorAll<HTMLElement>(':scope > .head')] : []
    columns.forEach((c, i) => {
      if (cells[i]) widths[c.id] = cells[i].getBoundingClientRect().width
    })
    return widths
  }

  /** Moves the edge between `left` and `right` by `delta` pixels, taking the width from the other one. */
  function trade(widths: Record<string, number>, left: Column, right: Column, delta: number, persist: boolean) {
    const a = widths[left.id]
    const b = widths[right.id]
    // A small window can leave a column under its minimum already; that alone never moves the edge.
    const d = Math.min(Math.max(delta, Math.min(0, minWidth(left) - a)), Math.max(0, b - minWidth(right)))
    prefs.setColumnWidths({ ...widths, [left.id]: a + d, [right.id]: b - d }, persist)
  }

  function startColumnResize(e: PointerEvent, index: number) {
    if (e.button !== 0) return
    e.preventDefault()
    ;(e.currentTarget as HTMLElement).setPointerCapture(e.pointerId)
    drag = { pointer: e.pointerId, x: e.clientX, widths: measure(), left: columns[index], right: columns[index + 1] }
  }

  function resizeColumn(e: PointerEvent) {
    if (drag?.pointer === e.pointerId) trade(drag.widths, drag.left, drag.right, e.clientX - drag.x, false)
  }

  function endColumnResize(e: PointerEvent) {
    if (drag?.pointer !== e.pointerId) return
    drag = null
    prefs.setColumnWidths(prefs.columnWidths)
  }

  function onGripKeydown(e: KeyboardEvent, index: number) {
    if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight') return
    e.preventDefault()
    e.stopPropagation()
    const step = (e.shiftKey ? 40 : 10) * (e.key === 'ArrowRight' ? 1 : -1)
    trade(measure(), columns[index], columns[index + 1], step, true)
  }

  const placeholders = {
    library: 'Search songs, artists, albums…',
    suggested: 'Search these songs…',
    hidden: 'Search hidden songs…',
    missing: 'Search missing songs…',
    filter: 'Search this playlist…',
    manual: 'Search this playlist…',
    playing: 'Search the songs playing…',
  }

  let viewport = $state<HTMLDivElement>()
  let scrollTop = $state(0)
  let height = $state(0)

  // Only the rows in (and just around) the viewport exist in the DOM.
  const start = $derived(Math.max(0, Math.floor(scrollTop / ROW_HEIGHT) - OVERSCAN))
  const end = $derived(Math.min(library.total, Math.ceil((scrollTop + height) / ROW_HEIGHT) + OVERSCAN))
  const indexes = $derived(Array.from({ length: Math.max(0, end - start) }, (_, i) => start + i))


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

  // Double-clicking the BPM cell edits it instead of playing the song.
  function onBpmDblclick(e: MouseEvent) {
    if (e.ctrlKey || e.shiftKey) return
    e.stopPropagation()
    openBpmEditor(pointOf(e))
  }

  function onRowContextmenu(e: MouseEvent, index: number) {
    e.preventDefault()
    if (library.row(index)) openRowMenu(pointOf(e), index)
  }
</script>

<section
  class="tracks"
  class:numbered={library.view.kind === 'manual' || library.view.kind === 'playing'}
  class:resizing={drag !== null}
  style:--user-columns={template}
>
  <ViewHeader />

  <div class="toolbar">
    <div class="search">
      <svg viewBox="0 0 16 16" aria-hidden="true"><circle cx="7" cy="7" r="4.5" /><path d="M10.5 10.5l3.5 3.5" /></svg>
      <input
        id="search"
        type="search"
        placeholder={placeholders[library.view.kind]}
        autocomplete="off"
        spellcheck="false"
        value={library.text}
        oninput={(e) => library.setText(e.currentTarget.value)}
        onkeydown={onSearchKeydown}
      />
      {#if keymap.label('library.search')}<kbd>{keymap.label('library.search')}</kbd>{/if}
    </div>
    <BpmRange />
  </div>

  <FilterBar />

  <div class="row header" role="row" bind:this={header}>
    {#each columns as column, index (column.id)}
      <div class="head" class:optional={column.optional}>
        {#if column.key}
          {@const key = column.key}
          <button
            class="cell"
            class:numeric={column.numeric}
            class:active={library.sort === key}
            onclick={() => library.setSort(key)}
          >
            {column.label}
            {#if library.sort === key}
              <span class="arrow">{library.desc ? '▾' : '▴'}</span>
            {/if}
          </button>
        {:else}
          <span class="cell">{column.label}</span>
        {/if}
        {#if index < columns.length - 1}
          <!-- Beside the button rather than in it, so a drag never sorts. -->
          <div
            class="grip"
            class:dragging={drag?.left.id === column.id}
            role="slider"
            aria-orientation="horizontal"
            aria-label="{column.label} column width"
            aria-valuenow={Math.round(widthOf(column.id))}
            tabindex="0"
            title="Drag to resize · double-click to reset"
            onpointerdown={(e) => startColumnResize(e, index)}
            onpointermove={resizeColumn}
            onpointerup={endColumnResize}
            onpointercancel={endColumnResize}
            ondblclick={prefs.resetColumnWidths}
            onkeydown={(e) => onGripKeydown(e, index)}
          ></div>
        {/if}
      </div>
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
        {:else if library.narrowed}
          <p class="big">No matches</p>
          {#if library.text.trim()}
            <p>Nothing {library.hasFilter ? 'with this filter ' : ''}matches “{library.text}”.</p>
          {:else if library.hasFilter}
            <p>No songs match this filter{library.lensActive ? ' in this BPM range' : ''}.</p>
          {:else}
            <p>No songs at {formatRange(library.lens.min, library.lens.max)} BPM here.</p>
          {/if}
          {#if library.lensActive}
            <button class="cta" onclick={library.resetLens}>Show every BPM</button>
          {/if}
        {:else if library.view.kind === 'suggested'}
          <p class="big">Nothing to suggest yet</p>
          <p>Songs you skip most of the time show up here, so you can decide whether to keep them.</p>
        {:else if library.view.kind === 'hidden'}
          <p class="big">No hidden songs</p>
          <p>Songs you hide stay on disk but out of your library. Hide one from its right-click menu{#if keymap.label('selection.hide')}&nbsp;or with <kbd>{keymap.label('selection.hide')}</kbd>{/if}.</p>
        {:else if library.view.kind === 'missing'}
          <p class="big">No missing songs</p>
          <p>Songs whose file can't be found wait here, with their tags and playlists, until a scan finds the file again.</p>
        {:else if library.view.kind === 'playing'}
          <p class="big">Nothing playing</p>
          <p>Play a song from any list and the songs it plays through show up here.</p>
        {:else if library.view.kind === 'manual'}
          <p class="big">This playlist is empty</p>
          <p>Select songs anywhere in your library and add them from their right-click menu{#if keymap.label('selection.playlist')}&nbsp;or with <kbd>{keymap.label('selection.playlist')}</kbd>{/if}.</p>
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
              {#if library.view.kind === 'manual' || library.view.kind === 'playing'}
                <span class="cell numeric dim">{row.position === null ? '' : row.position + 1}</span>
              {/if}
              <span class="cell title">
                {#if row.id === player.trackId}
                  <svg class="now" viewBox="0 0 12 12" aria-label="Now playing"><path d="M1 4h2v4H1zM5 2h2v8H5zM9 5h2v2H9z" /></svg>
                {/if}
                {row.title}
              </span>
              <span class="cell dim">{row.artist ?? ''}</span>
              {#if library.view.kind === 'missing'}
                <span class="cell dim optional path" title={row.path}><bdi>{row.path}</bdi></span>
              {:else}
                <span class="cell dim optional">{row.album ?? ''}</span>
              {/if}
              <span class="cell chips optional">
                {#each tags.resolve(row.tagIds) as tag (tag.id)}
                  <span class="chip" style:--c={tag.color}>{tag.name}</span>
                {/each}
              </span>
              <!-- svelte-ignore a11y_no_static_element_interactions -->
              <span
                class="cell numeric dim optional bpm"
                class:manual={row.bpmSource === 'manual'}
                class:unsure={isUnsure(row)}
                title={describeBpm(row)}
                ondblclick={onBpmDblclick}
              >{formatBpm(row)}</span>
              <span class="cell numeric dim optional">{row.playCount || ''}</span>
              {#if showSkips}
                <span class="cell numeric dim optional">{row.skipCount || ''}</span>
              {/if}
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
    /* Built from the columns and their saved widths; narrow windows override it below. */
    --columns: var(--user-columns);
    display: flex;
    flex-direction: column;
    min-height: 0;
    min-width: 0;
  }

  .tracks.resizing {
    cursor: col-resize;
    user-select: none;
  }

  .toolbar {
    flex: none;
    display: flex;
    flex-wrap: wrap;
    gap: 10px;
    margin: 6px 20px 10px;
  }

  .search {
    flex: 1 1 260px;
    display: flex;
    align-items: center;
    gap: 10px;
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

  /* Both keep room for the list's scrollbar, so the header's columns line up with the rows'. */
  .header {
    flex: none;
    height: 30px;
    border-bottom: 1px solid var(--border);
    overflow: hidden;
    scrollbar-gutter: stable;
  }

  .head {
    position: relative;
    min-width: 0;
  }

  /* Sits over the gap after its column, wider than the line it shows so it's easy to grab. */
  .grip {
    position: absolute;
    top: 4px;
    bottom: 4px;
    right: -13px;
    z-index: 1;
    width: 10px;
    cursor: col-resize;
    outline: 0;
  }

  .grip:hover,
  .grip:focus-visible,
  .grip.dragging {
    background: linear-gradient(to right, transparent 4px, var(--accent) 4px, var(--accent) 6px, transparent 6px);
  }

  .header .cell {
    display: block;
    width: 100%;
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
    scrollbar-gutter: stable;
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

  /* A long path is cut at its start, so the folder and file name stay visible. */
  .cell.path {
    direction: rtl;
    text-align: left;
  }

  .dim {
    color: var(--text-dim);
  }

  .bpm.unsure {
    opacity: 0.6;
  }

  /* Set by hand: a small accent dot before the number. */
  .bpm.manual::before {
    content: '';
    display: inline-block;
    width: 4px;
    height: 4px;
    margin: 0 5px 2px 0;
    border-radius: 50%;
    background: var(--accent);
    vertical-align: middle;
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
    color: color-mix(in srgb, var(--c) 75%, var(--chip-mix));
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

    .optional,
    .grip {
      display: none;
    }
  }
</style>
