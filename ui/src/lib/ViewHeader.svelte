<script lang="ts">
  import { library, songs } from './library.svelte'

  const title = $derived(library.view.kind === 'library' ? 'All songs' : (library.playlist?.name ?? ''))
  const count = $derived(
    library.view.kind === 'library' && library.narrowed
      ? `${library.total.toLocaleString()} ${library.total === 1 ? 'match' : 'matches'}`
      : songs(library.total),
  )
</script>

<div class="view-header">
  {#if library.view.kind === 'filter'}
    <svg class="kind" viewBox="0 0 16 16" aria-label="Filter playlist"><path d="M2 3h12l-4.5 5.5V13l-3 1.5V8.5z" /></svg>
  {:else if library.view.kind === 'manual'}
    <svg class="kind" viewBox="0 0 16 16" aria-label="Playlist"><path d="M2 4h9M2 8h9M2 12h6M13 10v5M10.5 12.5h5" /></svg>
  {/if}
  <h1 title={title}>{title}</h1>
  {#if library.loaded}
    <span class="count">{count}</span>
  {/if}
  {#if library.dirty}
    <span class="edited">edited</span>
  {/if}
  {#if library.selection.size > 1}
    <span class="count">· {library.selection.size.toLocaleString()} selected</span>
  {/if}

  <div class="actions">
    {#if library.dirty}
      <button class="ghost" onclick={library.revert}>Revert</button>
      <button class="primary" onclick={library.saveChanges}>Save changes</button>
    {:else if library.view.kind === 'library' && library.narrowed}
      <button class="ghost" onclick={library.saveViewAsPlaylist} title="Keep this search and filter as a playlist that updates itself">
        Save as playlist
      </button>
    {/if}
  </div>
</div>

<style>
  .view-header {
    display: flex;
    align-items: center;
    gap: 10px;
    min-width: 0;
    height: 44px;
    margin: 8px 20px 0;
  }

  .kind {
    flex: none;
    width: 16px;
    height: 16px;
    fill: none;
    stroke: var(--accent);
    stroke-width: 1.5;
    stroke-linejoin: round;
  }

  h1 {
    margin: 0;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    font-size: 20px;
    font-weight: 650;
  }

  .count {
    flex: none;
    font-size: 12px;
    color: var(--text-dim);
    font-variant-numeric: tabular-nums;
  }

  .edited {
    flex: none;
    padding: 1px 8px;
    border-radius: 999px;
    background: color-mix(in srgb, var(--accent) 18%, transparent);
    color: var(--accent);
    font-size: 11px;
    font-weight: 600;
  }

  .actions {
    display: flex;
    gap: 8px;
    margin-left: auto;
  }

  .actions button {
    flex: none;
    height: 28px;
    padding: 0 12px;
    border-radius: 999px;
    font-size: 12px;
    font-weight: 600;
  }

  .ghost {
    border: 1px solid var(--border);
    background: transparent;
    color: var(--text);
  }

  .ghost:hover {
    background: var(--surface);
  }

  .primary {
    border: 0;
    background: var(--accent);
    color: var(--accent-contrast);
  }
</style>
