<script lang="ts">
  /** A text box that replaces a name in place. Enter or leaving the box saves, Esc cancels. */
  let {
    value,
    label,
    onsave,
    ondone,
  }: { value: string; label: string; onsave: (name: string) => Promise<unknown>; ondone: () => void } = $props()

  let input = $state<HTMLInputElement>()
  let finished = false

  $effect(() => {
    input?.focus()
    input?.select()
  })

  async function finish(save: boolean) {
    if (finished) return
    finished = true
    const name = input?.value.trim() ?? ''
    try {
      if (save && name && name !== value) await onsave(name)
    } finally {
      ondone()
    }
  }

  function onKeydown(e: KeyboardEvent) {
    e.stopPropagation()
    if (e.key === 'Enter') {
      e.preventDefault()
      finish(true)
    } else if (e.key === 'Escape') {
      e.preventDefault()
      finish(false)
    }
  }
</script>

<input
  class="inline-name"
  bind:this={input}
  {value}
  aria-label={label}
  maxlength={100}
  spellcheck="false"
  autocomplete="off"
  onkeydown={onKeydown}
  onblur={() => finish(true)}
/>

<style>
  .inline-name {
    flex: 1;
    min-width: 0;
    height: 24px;
    margin: -3px 0;
    padding: 0 6px;
    border: 1px solid var(--accent);
    border-radius: 5px;
    outline: 0;
    background: var(--bg);
    color: var(--text);
    font: inherit;
  }
</style>
