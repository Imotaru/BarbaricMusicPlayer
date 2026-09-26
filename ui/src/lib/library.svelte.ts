import { call, hasHost, on } from './bridge'
import { player, type QueryContext } from './player.svelte'

export type SortKey = 'artist' | 'title' | 'album' | 'duration' | 'bpm' | 'added'

export interface TrackRow {
  id: number
  title: string
  artist: string | null
  album: string | null
  durationMs: number
  bpm: number | null
}

export interface ScanStatus {
  running: boolean
  processed: number
  total: number
}

interface QueryPage {
  total: number
  rows: TrackRow[]
}

export const PAGE_SIZE = 200
const SEARCH_DEBOUNCE_MS = 60

/**
 * The library list as a lazily loaded, paged view over the host's query results.
 * Rows are fetched a page at a time as the list scrolls; every change of search or sort bumps
 * `version`, and replies for older versions are dropped.
 */
class Library {
  folders = $state<string[]>([])
  scan = $state<ScanStatus>({ running: false, processed: 0, total: 0 })
  text = $state('')
  sort = $state<SortKey>('artist')
  desc = $state(false)
  total = $state(0)
  selected = $state(0)
  loaded = $state(false)
  error = $state<string | null>(null)
  /** Bumped when the list should scroll back to the top (new search or sort). */
  scrollResets = $state(0)

  private pages = $state.raw(new Map<number, TrackRow[]>())
  private version = 0
  private loading = new Set<number>()
  private visiblePages: number[] = [0]
  private debounce: ReturnType<typeof setTimeout> | undefined

  constructor() {
    if (!hasHost) return
    on<ScanStatus>('library.scan', (s) => (this.scan = s))
    on('library.changed', () => this.refresh())
    on<{ message: string }>('library.error', (e) => (this.error = e.message))
    call<string[]>('library.getFolders').then((f) => (this.folders = f))
    call<ScanStatus>('library.getScanStatus').then((s) => (this.scan = s))
    this.refresh()
  }

  get context(): QueryContext {
    return { text: this.text, sort: this.sort, desc: this.desc }
  }

  row(index: number): TrackRow | undefined {
    return this.pages.get(Math.floor(index / PAGE_SIZE))?.[index % PAGE_SIZE]
  }

  /** Makes sure the rows in [start, end) are loaded. Called by the list as it scrolls. */
  ensureRange(start: number, end: number) {
    const first = Math.floor(start / PAGE_SIZE)
    const last = Math.floor(Math.max(start, end - 1) / PAGE_SIZE)
    this.visiblePages = []
    for (let page = first; page <= last; page++) {
      this.visiblePages.push(page)
      if (!this.pages.has(page) && !this.loading.has(page)) this.loadPage(page)
    }
  }

  setText(text: string) {
    this.text = text
    this.selected = 0
    clearTimeout(this.debounce)
    this.debounce = setTimeout(() => this.refresh({ resetScroll: true }), SEARCH_DEBOUNCE_MS)
  }

  setSort(key: SortKey) {
    if (this.sort === key) this.desc = !this.desc
    else {
      this.sort = key
      this.desc = false
    }
    this.selected = 0
    this.refresh({ resetScroll: true })
  }

  moveSelection(delta: number) {
    if (this.total === 0) return
    this.selected = Math.min(Math.max(this.selected + delta, 0), this.total - 1)
  }

  select(index: number) {
    this.selected = index
  }

  playIndex(index: number) {
    const row = this.row(index)
    if (!row) return
    this.selected = index
    player.playTrack(row.id, this.context)
  }

  playSelected = () => this.playIndex(this.selected)

  addFolder = async () => {
    const folders = await call<string[] | null>('library.addFolder')
    if (folders) this.folders = folders
  }

  removeFolder = async (path: string) => {
    this.folders = await call<string[]>('library.removeFolder', { path })
  }

  rescan = () => call('library.rescan')

  /**
   * Reloads the pages currently on screen and swaps them in together, so a refresh during a
   * scan doesn't blank the list. Search and sort changes jump back to the top.
   */
  private async refresh({ resetScroll = false } = {}) {
    const version = ++this.version
    this.loading.clear()
    if (resetScroll) this.visiblePages = [0]

    try {
      const results = await Promise.all(this.visiblePages.map((page) => this.fetch(page)))
      if (version !== this.version) return
      this.pages = new Map(this.visiblePages.map((page, i) => [page, results[i].rows]))
      this.total = results[0]?.total ?? 0
      if (resetScroll) this.scrollResets++
      this.selected = Math.min(this.selected, Math.max(this.total - 1, 0))
      this.loaded = true
    } catch (e) {
      this.error = e instanceof Error ? e.message : String(e)
    }
  }

  private async loadPage(page: number) {
    const version = this.version
    this.loading.add(page)
    try {
      const result = await this.fetch(page)
      if (version !== this.version) return
      this.pages = new Map(this.pages).set(page, result.rows)
      this.total = result.total
    } finally {
      if (version === this.version) this.loading.delete(page)
    }
  }

  private fetch(page: number) {
    return call<QueryPage>('library.query', { ...this.context, offset: page * PAGE_SIZE, limit: PAGE_SIZE })
  }
}

export const library = new Library()
