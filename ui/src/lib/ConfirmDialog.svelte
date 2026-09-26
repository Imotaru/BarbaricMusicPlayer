<script lang="ts">
  import { untrack } from 'svelte'
  import { ui, type Confirm } from './ui.svelte'

  const props: { confirm: Confirm } = $props()

  // A snapshot, like the popovers: the answer applies to what the dialog was opened for.
  const { title, message, items, confirmLabel, danger, action } = untrack(() => ({ ...props.confirm }))

  let dialog = $state<HTMLDivElement>()
  let confirmButton = $state<HTMLButtonElement>()

  $effect(() => confirmButton?.focus())

  function answer() {
    ui.closeConfirm()
    action()
  }

  function onKeydown(e: KeyboardEvent) {
    e.stopPropagation()
    if (e.key === 'Escape') {
      e.preventDefault()
      ui.closeConfirm()
    } else if (e.key === 'Tab') {
      // Keep focus on the two buttons.
      const buttons = [...(dialog?.querySelectorAll<HTMLElement>('button') ?? [])]
      const i = buttons.indexOf(document.activeElement as HTMLElement)
      e.preventDefault()
      buttons[(i + (e.shiftKey ? -1 : 1) + buttons.length) % buttons.length]?.focus()
    }
  }
</script>

<!-- svelte-ignore a11y_click_events_have_key_events, a11y_no_static_element_interactions -->
<div class="scrim" onclick={(e) => e.target === e.currentTarget && ui.closeConfirm()}>
  <div
    class="confirm popover"
    role="alertdialog"
    aria-modal="true"
    aria-labelledby="confirm-title"
    aria-describedby="confirm-message"
    tabindex="-1"
    bind:this={dialog}
    onkeydown={onKeydown}
  >
    <h2 id="confirm-title">{title}</h2>
    <p id="confirm-message">{message}</p>
    {#if items && items.length > 0}
      <ul>
        {#each items as item, i (i)}
          <li>{item}</li>
        {/each}
      </ul>
    {/if}
    <div class="actions">
      <button class="ghost" onclick={ui.closeConfirm}>Cancel</button>
      <button class="primary" class:danger bind:this={confirmButton} onclick={answer}>{confirmLabel}</button>
    </div>
  </div>
</div>

<style>
  .scrim {
    position: fixed;
    inset: 0;
    z-index: 60;
    display: grid;
    place-items: center;
    padding: 16px;
    background: rgb(0 0 0 / 0.4);
  }

  .confirm {
    width: min(400px, 100%);
    padding: 18px 18px 14px;
    outline: 0;
  }

  h2 {
    margin: 0 0 6px;
    font-size: 15px;
    font-weight: 650;
  }

  p {
    margin: 0;
    font-size: 13px;
    line-height: 1.5;
    color: var(--text-dim);
  }

  ul {
    margin: 10px 0 0;
    padding: 8px 12px;
    border-radius: 8px;
    background: var(--bg);
    list-style: none;
    font-size: 12px;
    line-height: 1.7;
  }

  li {
    overflow: hidden;
    white-space: nowrap;
    text-overflow: ellipsis;
  }

  .actions {
    display: flex;
    justify-content: flex-end;
    gap: 8px;
    margin-top: 16px;
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

  .primary.danger {
    background: var(--danger);
    color: white;
  }
</style>
