import { call, hasHost, on } from './bridge'
import type { QueryContext, SavedQuery } from './query'

export interface Playlist {
  id: number
  name: string
  kind: 'filter' | 'manual'
  /** Songs in a manual playlist; null for filter playlists. */
  count: number | null
  /** A filter playlist's saved view; null for manual playlists. */
  query: SavedQuery | null
}

/** Reactive mirror of the host's playlists. The host emits `playlists.changed` after every change. */
class Playlists {
  list = $state<Playlist[]>([])
  loaded = $state(false)
  byId = $derived(new Map(this.list.map((p) => [p.id, p])))
  manual = $derived(this.list.filter((p) => p.kind === 'manual'))

  constructor() {
    if (!hasHost) return
    on('playlists.changed', () => this.reload())
    this.reload()
  }

  /** Creates a filter playlist from a view and resolves once the sidebar list includes it. */
  createFilter = async (name: string, query: QueryContext) => {
    const id = await call<number>('playlists.createFilter', { name, query })
    await this.reload()
    return id
  }

  createManual = async (name: string, trackIds: number[] = []) => {
    const id = await call<number>('playlists.createManual', { name, trackIds })
    await this.reload()
    return id
  }

  rename = (id: number, name: string) => call('playlists.rename', { id, name })

  updateFilter = (id: number, query: QueryContext) => call('playlists.updateFilter', { id, query })

  remove = (id: number) => call('playlists.delete', { id })

  /** Resolves with how many songs were new to the playlist. */
  addTracks = (id: number, trackIds: number[]) => call<number>('playlists.addTracks', { id, trackIds })

  removeTracks = (id: number, trackIds: number[]) => call('playlists.removeTracks', { id, trackIds })

  moveTracks = (id: number, trackIds: number[], toIndex: number) =>
    call('playlists.moveTracks', { id, trackIds, toIndex })

  private async reload() {
    this.list = await call<Playlist[]>('playlists.getAll')
    this.loaded = true
  }
}

export const playlists = new Playlists()
