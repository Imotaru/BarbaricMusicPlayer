<script lang="ts">
  import { library } from './library.svelte'
  import { formatRange } from './query'
  import { tags } from './tags.svelte'

  const included = $derived(tags.resolve(library.filter.include))
  const excluded = $derived(tags.resolve(library.filter.exclude))
  // A filter playlist's own saved range; the lens above the list is separate.
  const range = $derived(formatRange(library.filter.bpmMin, library.filter.bpmMax))
</script>

{#if included.length > 0 || excluded.length > 0 || range}
  <div class="filter-bar" role="group" aria-label="Filter">
    {#if included.length > 0 || excluded.length > 0}
      <span class="label">Tags</span>
    {/if}

    {#each included as tag (tag.id)}
      <span class="chip" style:--c={tag.color}>
        <button class="toggle" title="Click to exclude instead" onclick={() => library.flipTag(tag.id)}>{tag.name}</button>
        <button class="remove" aria-label="Stop filtering by {tag.name}" onclick={() => library.dropTag(tag.id)}>
          <svg viewBox="0 0 10 10"><path d="M2.5 2.5l5 5M7.5 2.5l-5 5" /></svg>
        </button>
      </span>
    {/each}

    {#if included.length > 1}
      <div class="mode" role="radiogroup" aria-label="Songs must have">
        <button role="radio" aria-checked={library.filter.mode === 'all'} class:on={library.filter.mode === 'all'} onclick={() => library.setMatchMode('all')}>all</button>
        <button role="radio" aria-checked={library.filter.mode === 'any'} class:on={library.filter.mode === 'any'} onclick={() => library.setMatchMode('any')}>any</button>
      </div>
    {/if}

    {#each excluded as tag (tag.id)}
      <span class="chip excluded" style:--c={tag.color}>
        <button class="toggle" title="Click to include instead" onclick={() => library.flipTag(tag.id)}>
          <span class="not">not</span>
          {tag.name}
        </button>
        <button class="remove" aria-label="Stop excluding {tag.name}" onclick={() => library.dropTag(tag.id)}>
          <svg viewBox="0 0 10 10"><path d="M2.5 2.5l5 5M7.5 2.5l-5 5" /></svg>
        </button>
      </span>
    {/each}

    {#if range}
      <span class="chip bpm" title="This playlist's own BPM range">
        <span class="toggle">
          {range} BPM{#if library.filter.includeUnknownBpm}<span class="not">&nbsp;+ unknown</span>{/if}
        </span>
        <button class="remove" aria-label="Stop filtering by BPM" onclick={library.dropBpmRange}>
          <svg viewBox="0 0 10 10"><path d="M2.5 2.5l5 5M7.5 2.5l-5 5" /></svg>
        </button>
      </span>
    {/if}

    <button class="clear" onclick={library.clearFilter}>Clear</button>
  </div>
{/if}

<style>
  .filter-bar {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 6px;
    margin: 0 20px 10px;
  }

  .label {
    margin-right: 2px;
    font-size: 11px;
    font-weight: 600;
    letter-spacing: 0.08em;
    text-transform: uppercase;
    color: var(--text-dim);
  }

  .chip {
    display: inline-flex;
    align-items: center;
    height: 24px;
    border-radius: 999px;
    background: color-mix(in srgb, var(--c) 22%, transparent);
    color: color-mix(in srgb, var(--c) 75%, var(--chip-mix));
    font-size: 12px;
    font-weight: 600;
  }

  .chip.bpm {
    background: var(--surface-hover);
    color: var(--text);
  }

  .chip.bpm .toggle {
    display: inline-flex;
    align-items: center;
    height: 100%;
  }

  .chip.excluded {
    background: transparent;
    box-shadow: inset 0 0 0 1px color-mix(in srgb, var(--c) 45%, transparent);
    color: var(--text-dim);
  }

  .chip.excluded .toggle {
    text-decoration: line-through;
    text-decoration-color: color-mix(in srgb, var(--c) 70%, transparent);
  }

  .not {
    display: inline-block;
    margin-right: 3px;
    text-decoration: none;
    font-weight: 400;
    color: var(--text-dim);
  }

  .chip button {
    height: 100%;
    border: 0;
    background: none;
    color: inherit;
    font: inherit;
  }

  .toggle {
    padding: 0 4px 0 10px;
  }

  .remove {
    display: grid;
    place-items: center;
    width: 22px;
    padding: 0;
    border-radius: 0 999px 999px 0;
    opacity: 0.7;
  }

  .remove:hover {
    opacity: 1;
  }

  .remove svg {
    width: 9px;
    height: 9px;
    fill: none;
    stroke: currentColor;
    stroke-width: 1.5;
  }

  .mode {
    display: inline-flex;
    height: 24px;
    padding: 2px;
    border: 1px solid var(--border);
    border-radius: 999px;
  }

  .mode button {
    padding: 0 8px;
    border: 0;
    border-radius: 999px;
    background: none;
    color: var(--text-dim);
    font-size: 11px;
  }

  .mode button.on {
    background: var(--surface-hover);
    color: var(--text);
  }

  .clear {
    margin-left: 4px;
    padding: 0;
    border: 0;
    background: none;
    color: var(--text-dim);
    font-size: 12px;
    text-decoration: underline;
    text-underline-offset: 2px;
  }

  .clear:hover {
    color: var(--text);
  }
</style>
