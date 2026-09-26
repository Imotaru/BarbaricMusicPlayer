<script lang="ts">
  import { library, type SortKey } from './library.svelte'
  import { formatTime, player } from './player.svelte'

  const ROW_HEIGHT = 34
  const OVERSCAN = 12

  const columns: { key: SortKey; label: string; numeric?: boolean }[] = [
    { key: 'title', label: 'Title' },
    { key: 'artist', label: 'Artist' },
    { key: 'album', label: 'Album' },
    { key: 'bpm', label: 'BPM', numeric: true },
    { key: 'duration', label: 'Time', numeric: true },
  ]

  let viewport = $state<HTMLDivElement>()
  let scrollTop = $state(0)
  let height = $state(0)

  // Only the rows in (and just around) the viewport exist in the DOM.
  const start = $derived(Math.max(0, Math.floor(scrollTop / ROW_HEIGHT) - OVERSCAN))
  const end = $derived(Math.min(library.total, Math.ceil((scrollTop + height) / ROW_HEIGHT) + OVERSCAN))
  const indexes = $derived(Array.from({ length: Math.max(0, end - start) }, (_, i) => start + i))

  $effect(() => library.ensureRange(start, end))

  // A new search or sort starts at the top.
  $effect(() => {
    library.scrollResets
    if (viewport) viewport.scrollTop = 0
  })

  // Keep the selected row visible when it moves via the keyboard.
  $effect(() => {
    const top = library.selected * ROW_HEIGHT
    if (!viewport) return
    if (top < viewport.scrollTop) viewport.scrollTop = top
    else if (top + ROW_HEIGHT > viewport.scrollTop + viewport.clientHeight) {
      viewport.scrollTop = top + ROW_HEIGHT - viewport.clientHeight
    }
  })

  function onSearchKeydown(e: KeyboardEvent) {
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault()
      library.moveSelection(e.key === 'ArrowDown' ? 1 : -1)
    } else if (e.key === 'Enter') {
      e.preventDefault()
      library.playSelected()
    } else if (e.key === 'Escape') {
      if (library.text) library.setText('')
      else e.currentTarget instanceof HTMLElement && e.currentTarget.blur()
    }
  }
</script>

<section class="tracks">
  <div class="search">
    <svg viewBox="0 0 16 16" aria-hidden="true"><circle cx="7" cy="7" r="4.5" /><path d="M10.5 10.5l3.5 3.5" /></svg>
    <input
      id="search"
      type="search"
      placeholder="Search songs, artists, albums…"
      autocomplete="off"
      spellcheck="false"
      value={library.text}
      oninput={(e) => library.setText(e.currentTarget.value)}
      onkeydown={onSearchKeydown}
    />
    <kbd>Ctrl F</kbd>
  </div>

  <div class="row header" role="row">
    {#each columns as column (column.key)}
      <button
        class="cell"
        class:numeric={column.numeric}
        class:active={library.sort === column.key}
        onclick={() => library.setSort(column.key)}
      >
        {column.label}
        {#if library.sort === column.key}
          <span class="arrow">{library.desc ? '▾' : '▴'}</span>
        {/if}
      </button>
    {/each}
  </div>

  <div
    class="viewport"
    bind:this={viewport}
    bind:clientHeight={height}
    onscroll={(e) => (scrollTop = e.currentTarget.scrollTop)}
    role="grid"
    aria-rowcount={library.total}
  >
    {#if library.loaded && library.total === 0}
      <div class="empty">
        {#if library.folders.length === 0}
          <p class="big">Your library is empty</p>
          <p>Add the folders where your music lives and they'll be scanned automatically.</p>
          <button class="cta" onclick={library.addFolder}>Add music folder</button>
        {:else if library.text}
          <p class="big">No matches</p>
          <p>Nothing matches “{library.text}”.</p>
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
          <!-- Keyboard selection and playback are handled app-wide in App.svelte. -->
          <!-- svelte-ignore a11y_click_events_have_key_events -->
          <div
            class="row"
            class:selected={index === library.selected}
            class:playing={row !== undefined && row.id === player.trackId}
            style:transform="translateY({index * ROW_HEIGHT}px)"
            role="row"
            tabindex="-1"
            aria-rowindex={index + 1}
            onclick={() => library.select(index)}
            ondblclick={() => library.playIndex(index)}
          >
            {#if row}
              <span class="cell title">
                {#if row.id === player.trackId}
                  <svg class="now" viewBox="0 0 12 12" aria-label="Now playing"><path d="M1 4h2v4H1zM5 2h2v8H5zM9 5h2v2H9z" /></svg>
                {/if}
                {row.title}
              </span>
              <span class="cell dim">{row.artist ?? ''}</span>
              <span class="cell dim">{row.album ?? ''}</span>
              <span class="cell numeric dim">{row.bpm ? Math.round(row.bpm) : ''}</span>
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
    display: grid;
    grid-template-rows: auto auto 1fr;
    min-height: 0;
    min-width: 0;
  }

  .search {
    display: flex;
    align-items: center;
    gap: 10px;
    margin: 12px 20px 10px;
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
    grid-template-columns: minmax(0, 2.2fr) minmax(0, 1.5fr) minmax(0, 1.5fr) 56px 64px;
    align-items: center;
    gap: 16px;
    height: 34px;
    padding: 0 20px;
    font-size: 13px;
  }

  .header {
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
    .row {
      grid-template-columns: minmax(0, 2fr) minmax(0, 1.4fr) 0 0 56px;
    }

    .row > :nth-child(3),
    .row > :nth-child(4) {
      visibility: hidden;
    }
  }
</style>
