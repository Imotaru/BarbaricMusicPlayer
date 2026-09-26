import { call, hasHost, on } from './bridge'

export interface Tag {
  id: number
  name: string
  color: string
  /** Songs in the library (not missing) that carry the tag. */
  count: number
}

/** Reactive mirror of the host's tags. The host emits `tags.changed` after every change. */
class Tags {
  list = $state<Tag[]>([])
  palette = $state<string[]>([])
  byId = $derived(new Map(this.list.map((t) => [t.id, t])))

  constructor() {
    if (!hasHost) return
    on('tags.changed', () => this.reload())
    call<string[]>('tags.getPalette').then((p) => (this.palette = p))
    this.reload()
  }

  create = (name: string) => call<Tag>('tags.create', { name })

  rename = (id: number, name: string) => call('tags.rename', { id, name })

  setColor = (id: number, color: string) => call('tags.setColor', { id, color })

  remove = (id: number) => call('tags.delete', { id })

  apply = (tagId: number, trackIds: number[], add: boolean) => call('tags.apply', { tagId, trackIds, add })

  /** How many of the given tracks carry each tag. */
  async usage(trackIds: number[]): Promise<Map<number, number>> {
    const rows = await call<{ tagId: number; count: number }[]>('tags.getUsage', { trackIds })
    return new Map(rows.map((r) => [r.tagId, r.count]))
  }

  /** The tags with these ids, by name. Unknown ids (e.g. just deleted) are skipped. */
  resolve(ids: readonly number[]): Tag[] {
    return ids
      .map((id) => this.byId.get(id))
      .filter((t) => t !== undefined)
      .sort((a, b) => a.name.localeCompare(b.name))
  }

  private async reload() {
    this.list = await call<Tag[]>('tags.getAll')
  }
}

export const tags = new Tags()
