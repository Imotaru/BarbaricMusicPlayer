<script lang="ts">
  import { library, songs } from './library.svelte'
  import { ui, type ImportReport } from './ui.svelte'

  const { report }: { report: ImportReport } = $props()

  let dialog = $state<HTMLDivElement>()
  let copied = $state(false)

  $effect(() => dialog?.focus())

  const summary = $derived.by(() => {
    const parts = [`${songs(report.matched + report.added)} imported`]
    if (report.tagsCreated > 0) parts.push(`${report.tagsCreated.toLocaleString()} new ${report.tagsCreated === 1 ? 'tag' : 'tags'}`)
    const lists = report.playlistsCreated + report.playlistsReplaced
    if (lists > 0) {
      parts.push(
        `${lists.toLocaleString()} ${lists === 1 ? 'playlist' : 'playlists'}` +
          (report.playlistsReplaced > 0 ? ` (${report.playlistsReplaced.toLocaleString()} replaced)` : ''),
      )
    }
    return parts.join(', ') + '.'
  })

  const describe = (song: { title: string; artist: string | null }) => (song.artist ? `${song.title} — ${song.artist}` : song.title)

  const copy = () =>
    ui.run(async () => {
      const lines = report.missing.map((m) => `${m.fileName}\t${m.path}\t${describe(m)}`)
      await navigator.clipboard.writeText(lines.join('\r\n'))
      copied = true
    })

  const showMissing = () => {
    ui.closeImportReport()
    library.openMissing()
  }

  function onKeydown(e: KeyboardEvent) {
    e.stopPropagation()
    if (e.key === 'Escape') {
      e.preventDefault()
      ui.closeImportReport()
    }
  }
</script>

<!-- svelte-ignore a11y_click_events_have_key_events, a11y_no_static_element_interactions -->
<div class="scrim" onclick={(e) => e.target === e.currentTarget && ui.closeImportReport()}>
  <div
    class="report popover"
    role="dialog"
    aria-modal="true"
    aria-labelledby="report-title"
    tabindex="-1"
    bind:this={dialog}
    onkeydown={onKeydown}
  >
    <h2 id="report-title">Backup imported</h2>
    <p>{summary}</p>

    {#if report.missing.length > 0}
      <p>
        {report.missing.length === 1 ? "One song's file" : `The files of ${songs(report.missing.length)}`} couldn't be found.
        Their tags, playlist places and stats are kept, and come back as soon as a scan finds the file in one of your
        music folders, even if it was moved or renamed.
      </p>
      <ul>
        {#each report.missing as song (song.id)}
          <li>
            <span class="file">{song.fileName}</span>
            <span class="path" title={song.path}>{song.path}</span>
            <span class="song">{describe(song)}</span>
          </li>
        {/each}
      </ul>
    {:else}
      <p>Every song's file was found.</p>
    {/if}

    <div class="actions">
      {#if report.missing.length > 0}
        <button class="ghost" onclick={copy}>{copied ? 'Copied' : 'Copy list'}</button>
        <button class="ghost" onclick={showMissing}>Show missing songs</button>
      {/if}
      <button class="primary" onclick={ui.closeImportReport}>Close</button>
    </div>
  </div>
</div>

<style>
  .scrim {
    position: fixed;
    inset: 0;
    z-index: 60;
    display: grid;
    /* One row the scrim's height, so the dialog's max-height of 100% means the window, not its own content. */
    grid-template-rows: minmax(0, 1fr);
    place-items: center;
    padding: 16px;
    background: var(--scrim);
  }

  .report {
    display: flex;
    flex-direction: column;
    width: min(560px, 100%);
    max-height: min(560px, 100%);
    padding: 18px 18px 14px;
    outline: 0;
  }

  h2 {
    margin: 0 0 6px;
    font-size: 15px;
    font-weight: 650;
  }

  p {
    margin: 0 0 6px;
    font-size: 13px;
    line-height: 1.5;
    color: var(--text-dim);
  }

  ul {
    min-height: 0;
    margin: 6px 0 0;
    padding: 4px 12px;
    overflow-y: auto;
    border-radius: 8px;
    background: var(--bg);
    list-style: none;
  }

  li {
    display: grid;
    padding: 6px 0;
    font-size: 12px;
    line-height: 1.5;
  }

  li + li {
    border-top: 1px solid color-mix(in srgb, var(--border) 60%, transparent);
  }

  li span {
    overflow: hidden;
    white-space: nowrap;
    text-overflow: ellipsis;
  }

  .file {
    font-weight: 650;
    user-select: text;
  }

  /* The whole path, wrapped: it's what the user goes looking for. */
  li .path {
    white-space: normal;
    overflow-wrap: anywhere;
    color: var(--text-dim);
    user-select: text;
  }

  .song {
    color: var(--text-dim);
  }

  .actions {
    display: flex;
    justify-content: flex-end;
    gap: 8px;
    margin-top: 14px;
  }

  button {
    height: 30px;
    padding: 0 14px;
    border-radius: 999px;
    font-size: 13px;
    font-weight: 600;
  }

  .ghost {
    border: 1px solid var(--border);
    background: transparent;
    color: var(--text);
  }

  .ghost:hover {
    background: var(--surface-hover);
  }

  .primary {
    border: 0;
    background: var(--accent);
    color: var(--accent-contrast);
  }
</style>
