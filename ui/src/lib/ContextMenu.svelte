<script lang="ts">
  import { untrack } from 'svelte'
  import { ui, type MenuItem, type Point } from './ui.svelte'

  const props: { at: Point; items: MenuItem[] } = $props()

  // A snapshot, so item handlers still work after closing clears ui.menu (which the props read).
  const { at, items } = untrack(() => ({ ...props }))

  let menu = $state<HTMLDivElement>()
  let position = $state({ x: 0, y: 0 })
  let active = $state(-1)

  const actionable = items.flatMap((item, i) => ('action' in item && !item.disabled ? [i] : []))

  // Keep the menu on screen, then take focus so the keyboard drives it.
  $effect(() => {
    if (!menu) return
    const { width, height } = menu.getBoundingClientRect()
    position = {
      x: Math.max(4, Math.min(at.x, innerWidth - width - 4)),
      y: Math.max(4, Math.min(at.y, innerHeight - height - 4)),
    }
    menu.focus()
  })

  function activate(item: MenuItem) {
    if (!('action' in item) || item.disabled) return
    ui.closeMenu()
    item.action()
  }

  function step(delta: number) {
    if (actionable.length === 0) return
    const at = actionable.indexOf(active)
    const next = at < 0 ? (delta > 0 ? 0 : actionable.length - 1) : (at + delta + actionable.length) % actionable.length
    active = actionable[next]
  }

  function onKeydown(e: KeyboardEvent) {
    e.stopPropagation()
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault()
      step(e.key === 'ArrowDown' ? 1 : -1)
    } else if (e.key === 'Enter' || e.key === ' ') {
      e.preventDefault()
      if (active >= 0) activate(items[active])
    } else if (e.key === 'Escape' || e.key === 'Tab') {
      e.preventDefault()
      ui.closeMenu()
    }
  }

  function onPointerdown(e: PointerEvent) {
    if (!menu?.contains(e.target as Node)) ui.closeMenu()
  }
</script>

<svelte:window onpointerdown={onPointerdown} onblur={ui.closeMenu} onresize={ui.closeMenu} />

<div
  class="menu popover"
  role="menu"
  tabindex="-1"
  bind:this={menu}
  style:left="{position.x}px"
  style:top="{position.y}px"
  onkeydown={onKeydown}
  oncontextmenu={(e) => e.preventDefault()}
>
  {#each items as item, i (i)}
    {#if 'separator' in item}
      <div class="separator" role="separator"></div>
    {:else if 'swatches' in item}
      <div class="swatches" role="group" aria-label="Colour">
        {#each item.swatches as color (color)}
          <button
            class="swatch"
            class:current={color === item.current}
            style:--c={color}
            aria-label="Colour {color}"
            onclick={() => {
              ui.closeMenu()
              item.pick(color)
            }}
          ></button>
        {/each}
      </div>
    {:else}
      <button
        class="item"
        class:danger={item.danger}
        class:active={i === active}
        role="menuitem"
        disabled={item.disabled}
        onpointerenter={() => (active = item.disabled ? -1 : i)}
        onclick={() => activate(item)}
      >
        <span>{item.label}</span>
        {#if item.shortcut}<kbd>{item.shortcut}</kbd>{/if}
      </button>
    {/if}
  {/each}
</div>

<style>
  .menu {
    position: fixed;
    z-index: 50;
    min-width: 200px;
    max-width: 320px;
    padding: 4px;
    outline: 0;
  }

  .item {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 16px;
    width: 100%;
    padding: 6px 10px;
    border: 0;
    border-radius: 6px;
    background: none;
    color: var(--text);
    font-size: 13px;
    text-align: left;
  }

  .item span {
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .item.active {
    background: var(--surface-hover);
  }

  .item.danger {
    color: var(--danger);
  }

  .item:disabled {
    color: var(--text-dim);
    opacity: 0.6;
  }

  kbd {
    flex: none;
    font: inherit;
    font-size: 11px;
    color: var(--text-dim);
  }

  .separator {
    height: 1px;
    margin: 4px 6px;
    background: var(--border);
  }

  .swatches {
    display: flex;
    flex-wrap: wrap;
    gap: 6px;
    padding: 6px 10px;
  }

  .swatch {
    width: 18px;
    height: 18px;
    padding: 0;
    border: 2px solid transparent;
    border-radius: 50%;
    background: var(--c);
    background-clip: content-box;
  }

  .swatch:hover,
  .swatch.current {
    border-color: var(--text);
  }
</style>
