import type { BpmStatus } from './bpm.svelte'
import { call, hasHost, on } from './bridge'
import { library, songs } from './library.svelte'
import { ui } from './ui.svelte'

/** Background volume measuring, which lets every song play equally loud. */
class Loudness {
  status = $state<BpmStatus>({ running: false, done: 0, total: 0, pending: 0 })

  constructor() {
    if (!hasHost) return
    on<BpmStatus>('loudness.status', (s) => (this.status = s))
    call<BpmStatus>('loudness.getStatus').then((s) => (this.status = s))
  }

  start = () => ui.run(() => call('loudness.start'))

  cancel = () => ui.run(() => call('loudness.cancel'))

  analyzeSelected = () =>
    ui.run(async () => {
      const { queued } = await call<{ queued: number }>('loudness.analyze', { trackIds: library.selectedIds() })
      ui.notify(`Measuring the volume of ${songs(queued)}.`)
    })
}

export const loudness = new Loudness()
