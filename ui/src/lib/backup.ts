// Exporting the library to a backup file and merging one back in (see BackupApi.cs).

import { call } from './bridge'
import { keymap } from './keymap.svelte'
import { songs } from './library.svelte'
import { prefs } from './prefs.svelte'
import { ui, type ImportReport } from './ui.svelte'

interface ExportResult {
  path: string
  songs: number
  tags: number
  playlists: number
}

/** What the picked backup holds, shown before anything is imported. */
interface BackupSummary {
  path: string
  exported: string
  songs: number
  tags: number
  playlists: number
  hasSettings: boolean
}

const count = (n: number, one: string) => `${n.toLocaleString()} ${n === 1 ? one : `${one}s`}`

const fileName = (path: string) => path.split(/[\\/]/).at(-1) ?? path

export const exportBackup = () =>
  ui.run(async () => {
    const result = await call<ExportResult | null>('backup.export')
    if (!result) return
    ui.notify(
      `Backed up ${songs(result.songs)}, ${count(result.tags, 'tag')} and ${count(result.playlists, 'playlist')} to ${fileName(result.path)}.`,
    )
  })

export const importBackup = () =>
  ui.run(async () => {
    const backup = await call<BackupSummary | null>('backup.pickImport')
    if (!backup) return
    const made = new Date(backup.exported).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' })
    ui.openConfirm({
      title: 'Import this backup?',
      message:
        `Made ${made}. It's merged into your library: for songs, tags and playlists in both, the backup's version wins, and the rest stays as it is.` +
        (backup.hasSettings ? ' Your theme, shortcuts, hotkeys and volume come from the backup too.' : ''),
      items: [songs(backup.songs), count(backup.tags, 'tag'), count(backup.playlists, 'playlist')],
      confirmLabel: 'Import',
      action: () =>
        ui.run(async () => {
          ui.notify(`Importing ${fileName(backup.path)}…`)
          const report = await call<ImportReport>('backup.import')
          await Promise.all([prefs.reload(), keymap.reloadGlobals()])
          ui.openImportReport(report)
        }),
    })
  })
