<script lang="ts">
  import { library } from './library.svelte'

  const folderName = (path: string) => path.split(/[\\/]/).filter(Boolean).at(-1) ?? path
  const progress = $derived(library.scan.total > 0 ? (library.scan.processed / library.scan.total) * 100 : 0)
</script>

<aside class="sidebar">
  <h2>Library</h2>

  <ul class="folders">
    {#each library.folders as folder (folder)}
      <li title={folder}>
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

  <div class="status">
    {#if library.scan.running}
      <p>Scanning… {library.scan.processed.toLocaleString()} / {library.scan.total.toLocaleString()}</p>
      <div class="bar"><div style:width="{progress}%"></div></div>
    {:else if library.folders.length > 0}
      <p>{library.total.toLocaleString()} {library.text ? (library.total === 1 ? 'match' : 'matches') : library.total === 1 ? 'song' : 'songs'}</p>
      <button class="link" onclick={library.rescan}>Rescan</button>
    {/if}
  </div>
</aside>

<style>
  .sidebar {
    display: flex;
    flex-direction: column;
    gap: 6px;
    padding: 8px 12px 16px;
    border-right: 1px solid var(--border);
    min-height: 0;
  }

  h2 {
    margin: 4px 8px 6px;
    font-size: 11px;
    font-weight: 700;
    letter-spacing: 0.12em;
    text-transform: uppercase;
    color: var(--text-dim);
  }

  .folders {
    list-style: none;
    margin: 0;
    padding: 0;
    overflow: auto;
  }

  li {
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 6px 8px;
    border-radius: 8px;
    font-size: 13px;
  }

  li:hover {
    background: var(--surface);
  }

  li > svg {
    flex: none;
    width: 14px;
    height: 14px;
    fill: none;
    stroke: var(--text-dim);
    stroke-width: 1.2;
  }

  .name {
    flex: 1;
    min-width: 0;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
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

  li:hover .remove,
  .remove:focus-visible {
    opacity: 1;
  }

  .remove:hover {
    background: var(--surface-hover);
    color: var(--text);
  }

  .remove svg,
  .add svg {
    width: 9px;
    height: 9px;
    fill: none;
    stroke: currentColor;
    stroke-width: 1.4;
  }

  .add {
    display: flex;
    align-items: center;
    gap: 8px;
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
    margin-top: auto;
    padding: 0 8px;
    font-size: 12px;
    color: var(--text-dim);
  }

  .status p {
    margin: 0 0 6px;
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
