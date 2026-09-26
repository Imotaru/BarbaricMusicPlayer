<script lang="ts">
  import { ui } from './ui.svelte'
</script>

{#if ui.toast}
  <div class="toast popover" class:error={ui.toast.error} role={ui.toast.error ? 'alert' : 'status'}>
    {ui.toast.message}
  </div>
{/if}

<style>
  .toast {
    position: fixed;
    left: 50%;
    bottom: 104px;
    z-index: 60;
    max-width: min(520px, calc(100vw - 32px));
    padding: 9px 16px;
    transform: translateX(-50%);
    font-size: 13px;
    pointer-events: none;
    animation: rise 160ms ease-out;
  }

  .toast.error {
    color: var(--danger-text);
    border-color: color-mix(in srgb, var(--danger) 50%, var(--border));
  }

  @keyframes rise {
    from {
      opacity: 0;
      transform: translate(-50%, 6px);
    }
  }
</style>
