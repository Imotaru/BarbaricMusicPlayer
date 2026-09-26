<script lang="ts">
  import { openPlaylistMenu, openTagMenu, pointOf } from './actions'
  import { bpm } from './bpm.svelte'
  import InlineName from './InlineName.svelte'
  import { keymap } from './keymap.svelte'
  import { library } from './library.svelte'
  import { playlists, type Playlist } from './playlists.svelte'
  import { tags, type Tag } from './tags.svelte'
  import { ui } from './ui.svelte'

  const folderName = (path: string) => path.split(/[\\/]/).filter(Boolean).at(-1) ?? path
  const progress = $derived(library.scan.total > 0 ? (library.scan.processed / library.scan.total) * 100 : 0)
  const bpmProgress = $derived(bpm.status.total > 0 ? (bpm.status.done / bpm.status.total) * 100 : 0)

  const isActive = (p: Playlist) => library.playlist?.id === p.id
  const isRenaming = (kind: 'tag' | 'playlist', id: number) => ui.renaming?.kind === kind && ui.renaming.id === id
  const stopRenaming = () => (ui.renaming = null)

  function onTagClick(e: MouseEvent, tag: Tag) {
    if (e.ctrlKey) library.toggleInclude(tag.id)
    else if (e.altKey) library.toggleExclude(tag.id)
    else library.showTag(tag.id)
  }

  const newPlaylist = () =>
    ui.run(async () => {
      const id = await playlists.createManual('New playlist')
      const playlist = playlists.byId.get(id)
      if (playlist) library.openPlaylist(playlist)
      ui.renaming = { kind: 'playlist', id }
    })
</script>

<aside class="sidebar">
  <div class="scroll">
    <nav aria-label="Library">
      <button
        class="item"
        class:active={library.view.kind === 'library' && !library.hasFilter && !library.text}
        onclick={library.openLibrary}
      >
        <svg viewBox="0 0 16 16" aria-hidden="true"><path d="M6 12.5V3.5l7-1.5v9" /><circle cx="4.5" cy="12.5" r="1.8" /><circle cx="11.5" cy="11" r="1.8" /></svg>
        <span class="name">All songs</span>
      </button>
      <button
        class="item"
        class:active={library.view.kind === 'suggested'}
        onclick={library.openSuggested}
        title="Songs you skip most of the time"
      >
        <svg viewBox="0 0 16 16" aria-hidden="true"><path d="M3 3.5 10 8l-7 4.5zM12.5 3.5v9" /></svg>
        <span class="name">Suggested for removal</span>
        {#if library.counts.suggested > 0}
          <span class="count">{library.counts.suggested.toLocaleString()}</span>
        {/if}
      </button>
      {#if library.counts.hidden > 0 || library.view.kind === 'hidden'}
        <button class="item" class:active={library.view.kind === 'hidden'} onclick={library.openHidden}>
          <svg viewBox="0 0 16 16" aria-hidden="true">
            <path d="M1.5 8S3.9 3.5 8 3.5 14.5 8 14.5 8 12.1 12.5 8 12.5 1.5 8 1.5 8z" /><circle cx="8" cy="8" r="2" /><path d="m2.5 13.5 11-11" />
          </svg>
          <span class="name">Hidden songs</span>
          <span class="count">{library.counts.hidden.toLocaleString()}</span>
        </button>
      {/if}
    </nav>

    <section>
      <header>
        <h2>Playlists</h2>
        <button class="icon-button" aria-label="New playlist" title="New playlist" onclick={newPlaylist}>
          <svg viewBox="0 0 10 10"><path d="M5 1v8M1 5h8" /></svg>
        </button>
      </header>
      <ul>
        {#each playlists.list as playlist (playlist.id)}
          <li>
            {#if isRenaming('playlist', playlist.id)}
              <div class="item">
                {@render playlistIcon(playlist)}
                <InlineName
                  value={playlist.name}
                  label="Playlist name"
                  onsave={(name) => ui.run(() => playlists.rename(playlist.id, name))}
                  ondone={stopRenaming}
                />
              </div>
            {:else}
              <button
                class="item"
                class:active={isActive(playlist)}
                title={playlist.kind === 'filter' ? 'Filter playlist: updates itself from a saved search and tags' : undefined}
                onclick={() => library.openPlaylist(playlist)}
                ondblclick={() => (ui.renaming = { kind: 'playlist', id: playlist.id })}
                oncontextmenu={(e) => {
                  e.preventDefault()
                  openPlaylistMenu(pointOf(e), playlist)
                }}
              >
                {@render playlistIcon(playlist)}
                <span class="name">{playlist.name}</span>
                {#if playlist.count !== null}
                  <span class="count">{playlist.count.toLocaleString()}</span>
                {/if}
              </button>
            {/if}
          </li>
        {:else}
          <li class="hint">Save a search as a playlist, or select songs and {#if keymap.label('selection.playlist')}press <kbd>{keymap.label('selection.playlist')}</kbd>{:else}right-click them{/if}.</li>
        {/each}
      </ul>
    </section>

    <section>
      <header>
        <h2>Tags</h2>
      </header>
      <ul>
        {#each tags.list as tag (tag.id)}
          {@const included = library.filter.include.includes(tag.id)}
          {@const excluded = library.filter.exclude.includes(tag.id)}
          <li>
            {#if isRenaming('tag', tag.id)}
              <div class="item">
                <span class="dot" style:--c={tag.color}></span>
                <InlineName
                  value={tag.name}
                  label="Tag name"
                  onsave={(name) => ui.run(() => tags.rename(tag.id, name))}
                  ondone={stopRenaming}
                />
              </div>
            {:else}
              <button
                class="item"
                class:active={included}
                class:excluded
                title="Click: show this tag · Ctrl+click: add to filter · Alt+click: exclude"
                onclick={(e) => onTagClick(e, tag)}
                ondblclick={() => (ui.renaming = { kind: 'tag', id: tag.id })}
                oncontextmenu={(e) => {
                  e.preventDefault()
                  openTagMenu(pointOf(e), tag)
                }}
              >
                <span class="dot" style:--c={tag.color}></span>
                <span class="name">{tag.name}</span>
                <span class="count">{tag.count.toLocaleString()}</span>
              </button>
            {/if}
          </li>
        {:else}
          <li class="hint">Select songs and {#if keymap.label('selection.tag')}press <kbd>{keymap.label('selection.tag')}</kbd>{:else}right-click them{/if} to tag them.</li>
        {/each}
        {#if tags.list.length > 0}
          <li>
            <button
              class="item"
              class:active={library.filter.untagged}
              title="Songs without any tags, such as ones that just arrived"
              onclick={library.showUntagged}
            >
              <span class="dot none"></span>
              <span class="name">Untagged</span>
              <span class="count">{library.counts.untagged.toLocaleString()}</span>
            </button>
          </li>
        {/if}
      </ul>
    </section>

    <section>
      <header>
        <h2>Folders</h2>
      </header>
      <ul class="folders">
        {#each library.folders as folder (folder)}
          <li class="folder" title={folder}>
            <svg viewBox="0 0 16 16" aria-hidden="true"><path d="M1.5 3.5h5l1.5 1.5h6.5v8h-13z" /></svg>
            <span class="name">{folderName(folder)}</span>
            <button class="remove" aria-label="Remove {folder} from library" onclick={() => library.removeFolder(folder)}>
              <svg viewBox="0 0 10 10"><path d="M2 2l6 6M8 2l-6 6" /></svg>
            </button>
          </li>
        {/each}
      </ul>

      <button class="add" onclick={library.addFolder}>
        <svg viewBox="0 0 10 10" aria-hidden="true"><path d="M5 1v8M1 5h8" /></svg>
        Add folder
      </button>
    </section>
  </div>

  <div class="status">
    {#if library.scan.running}
      <p>Scanning… {library.scan.processed.toLocaleString()} / {library.scan.total.toLocaleString()}</p>
      <div class="bar"><div style:width="{progress}%"></div></div>
    {:else if library.folders.length > 0}
      <button class="link" onclick={library.rescan}>Rescan folders</button>
    {/if}

    {#if bpm.status.running}
      <p class="line">
        <span>Analyzing BPM… {bpm.status.done.toLocaleString()} / {bpm.status.total.toLocaleString()}</span>
        <button class="link" onclick={bpm.cancel} title="Stop analyzing for now">Stop</button>
      </p>
      <div class="bar"><div style:width="{bpmProgress}%"></div></div>
    {:else if bpm.status.pending > 0 && !library.scan.running}
      <button class="link" onclick={bpm.start}>Analyze BPM ({bpm.status.pending.toLocaleString()} left)</button>
    {/if}
  </div>
</aside>

{#snippet playlistIcon(playlist: Playlist)}
  {#if playlist.kind === 'filter'}
    <svg viewBox="0 0 16 16" aria-label="Filter playlist"><path d="M2 3h12l-4.5 5.5V13l-3 1.5V8.5z" /></svg>
  {:else}
    <svg viewBox="0 0 16 16" aria-label="Playlist"><path d="M2 4h9M2 8h9M2 12h6M13 10v5M10.5 12.5h5" /></svg>
  {/if}
{/snippet}

<style>
  .sidebar {
    display: flex;
    flex-direction: column;
    border-right: 1px solid var(--border);
    min-height: 0;
  }

  .scroll {
    flex: 1;
    min-height: 0;
    overflow-y: auto;
    padding: 8px 12px 12px;
  }

  nav {
    margin-bottom: 14px;
  }

  section + section {
    margin-top: 16px;
  }

  header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    margin: 0 4px 4px 8px;
  }

  h2 {
    margin: 0;
    font-size: 11px;
    font-weight: 700;
    letter-spacing: 0.12em;
    text-transform: uppercase;
    color: var(--text-dim);
  }

  ul {
    list-style: none;
    margin: 0;
    padding: 0;
  }

  .item {
    display: flex;
    align-items: center;
    gap: 8px;
    width: 100%;
    height: 30px;
    padding: 0 8px;
    border: 0;
    border-radius: 8px;
    background: transparent;
    color: var(--text);
    font-size: 13px;
    text-align: left;
  }

  button.item:hover {
    background: var(--surface);
  }

  .item.active {
    background: var(--surface-hover);
  }

  .item.active .name {
    font-weight: 600;
  }

  .item.excluded .name {
    color: var(--text-dim);
    text-decoration: line-through;
  }

  .item svg,
  .folder > svg {
    flex: none;
    width: 14px;
    height: 14px;
    fill: none;
    stroke: var(--text-dim);
    stroke-width: 1.3;
    stroke-linejoin: round;
  }

  .item.active svg {
    stroke: var(--accent);
  }

  .dot {
    flex: none;
    width: 9px;
    height: 9px;
    margin: 0 2px 0 3px;
    border-radius: 50%;
    background: var(--c);
  }

  .dot.none {
    background: transparent;
    box-shadow: inset 0 0 0 1.5px var(--text-dim);
  }

  .name {
    flex: 1;
    min-width: 0;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .count {
    flex: none;
    font-size: 11px;
    color: var(--text-dim);
    font-variant-numeric: tabular-nums;
  }

  .hint {
    padding: 2px 8px 4px;
    font-size: 12px;
    line-height: 1.5;
    color: var(--text-dim);
  }

  kbd {
    padding: 0 4px;
    border: 1px solid var(--border);
    border-radius: 3px;
    font: inherit;
    font-size: 11px;
  }

  .icon-button {
    display: grid;
    place-items: center;
    width: 22px;
    height: 22px;
    padding: 0;
    border: 0;
    border-radius: 5px;
    background: transparent;
    color: var(--text-dim);
  }

  .icon-button:hover {
    background: var(--surface-hover);
    color: var(--text);
  }

  .icon-button svg,
  .remove svg,
  .add svg {
    width: 9px;
    height: 9px;
    fill: none;
    stroke: currentColor;
    stroke-width: 1.4;
  }

  .folder {
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 6px 8px;
    border-radius: 8px;
    font-size: 13px;
  }

  .folder:hover {
    background: var(--surface);
  }

  .remove {
    flex: none;
    width: 20px;
    height: 20px;
    display: grid;
    place-items: center;
    border: 0;
    border-radius: 4px;
    background: transparent;
    color: var(--text-dim);
    opacity: 0;
  }

  .folder:hover .remove,
  .remove:focus-visible {
    opacity: 1;
  }

  .remove:hover {
    background: var(--surface-hover);
    color: var(--text);
  }

  .add {
    display: flex;
    align-items: center;
    gap: 8px;
    width: 100%;
    margin-top: 4px;
    padding: 7px 8px;
    border: 1px dashed var(--border);
    border-radius: 8px;
    background: transparent;
    color: var(--text-dim);
    font-size: 13px;
  }

  .add:hover {
    color: var(--text);
    border-color: var(--text-dim);
  }

  .status {
    padding: 10px 20px 16px;
    font-size: 12px;
    color: var(--text-dim);
  }

  .status {
    display: grid;
    gap: 6px;
    justify-items: start;
  }

  .status p {
    margin: 0;
  }

  .status .bar {
    justify-self: stretch;
  }

  .status .line {
    display: flex;
    justify-content: space-between;
    gap: 8px;
    justify-self: stretch;
  }

  .bar {
    height: 3px;
    border-radius: 2px;
    background: var(--track);
    overflow: hidden;
  }

  .bar div {
    height: 100%;
    background: var(--accent);
    transition: width 150ms linear;
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

  .link:hover {
    color: var(--text);
  }
</style>
