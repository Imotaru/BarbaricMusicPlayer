import { call, hasHost, on } from './bridge'
import { library, songs, type BpmInfo, type TrackRow } from './library.svelte'
import { ui } from './ui.svelte'

export interface BpmStatus {
  running: boolean
  done: number
  total: number
  /** Songs still waiting for analysis while the analyzer is stopped. */
  pending: number
}

/** Analyzed tempos below this confidence are shown as uncertain. */
export const LOW_CONFIDENCE = 0.3

/** Background BPM analysis and BPM edits. Changed tempos come back as `bpm.updated` events. */
class Bpm {
  status = $state<BpmStatus>({ running: false, done: 0, total: 0, pending: 0 })

  constructor() {
    if (!hasHost) return
    on<BpmStatus>('bpm.status', (s) => (this.status = s))
    on<{ tracks: BpmInfo[] }>('bpm.updated', (e) => library.patchRows(e.tracks))
    call<BpmStatus>('bpm.getStatus').then((s) => (this.status = s))
  }

  get = (trackIds: number[]) => call<BpmInfo[]>('bpm.get', { trackIds })

  start = () => ui.run(() => call('bpm.start'))

  cancel = () => ui.run(() => call('bpm.cancel'))

  set = (trackIds: number[], bpm: number) => call('bpm.set', { trackIds, bpm })

  /** Multiplies known BPMs (×2 or ×½); returns how many songs changed. */
  scale = async (trackIds: number[], factor: number) =>
    (await call<{ changed: number }>('bpm.scale', { trackIds, factor })).changed

  /** Back to the file's tag, or to "unknown" so the analyzer measures it again. */
  reset = (trackIds: number[]) => call('bpm.reset', { trackIds })

  /** Doubles or halves the selected songs' BPM, reporting any it had to leave alone. */
  scaleSelected = (factor: number) =>
    ui.run(async () => {
      const ids = library.selectedIds()
      const changed = await this.scale(ids, factor)
      const skipped = ids.length - changed
      if (skipped > 0) {
        ui.notify(
          changed === 0
            ? `${ids.length === 1 ? 'That song has' : 'Those songs have'} no BPM to ${factor > 1 ? 'double' : 'halve'}.`
            : `Changed ${songs(changed)}; ${skipped.toLocaleString()} without a BPM (or out of range) left alone.`,
        )
      }
    })

  analyzeSelected = () =>
    ui.run(async () => {
      const { queued, skippedManual } = await call<{ queued: number; skippedManual: number }>('bpm.analyze', {
        trackIds: library.selectedIds(),
      })
      const skipped = skippedManual > 0 ? ` (${skippedManual.toLocaleString()} set by hand left alone)` : ''
      ui.notify(queued > 0 ? `Analyzing ${songs(queued)}${skipped}.` : `Nothing to analyze${skipped}.`)
    })
}

export const bpm = new Bpm()

/** The list's BPM cell: a whole number, "?" when the analysis wasn't sure, "–" when it found no beat. */
export function formatBpm(row: Pick<TrackRow, 'bpm' | 'bpmSource' | 'bpmConfidence'>) {
  if (row.bpm === null) return row.bpmSource === 'analyzed' ? '–' : ''
  const value = Math.round(row.bpm).toString()
  return isUnsure(row) ? `${value}?` : value
}

export const isUnsure = (row: Pick<TrackRow, 'bpm' | 'bpmSource' | 'bpmConfidence'>) =>
  row.bpmSource === 'analyzed' && row.bpm !== null && (row.bpmConfidence ?? 0) < LOW_CONFIDENCE

export function describeBpm(row: Pick<TrackRow, 'bpm' | 'bpmSource' | 'bpmConfidence'>) {
  const exact = row.bpm === null ? '' : `${Math.round(row.bpm * 10) / 10} BPM · `
  switch (row.bpmSource) {
    case 'manual':
      return `${exact}set by hand`
    case 'tag':
      return `${exact}from the file's tag`
    case 'analyzed':
      return row.bpm === null
        ? 'Analyzed: no clear beat'
        : `${exact}analyzed, ${Math.round((row.bpmConfidence ?? 0) * 100)}% confidence`
    default:
      return 'Not analyzed yet'
  }
}
