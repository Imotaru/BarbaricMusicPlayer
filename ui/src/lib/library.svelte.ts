import { call, hasHost, on } from './bridge'
import { player } from './player.svelte'
import { playlists, type Playlist } from './playlists.svelte'
import {
  emptyFilter,
  formatRange,
  fromTrackFilter,
  isEmptyFilter,
  isOpenRange,
  openRange,
  sameView,
  toTrackFilter,
  withLens,
  type BpmRange,
  type QueryContext,
  type SavedQuery,
  type SortKey,
  type TagFilter,
  type TrackScope,
} from './query'
import { tags } from './tags.svelte'
import { ui } from './ui.svelte'

export type { SortKey }

export interface TrackRow {
  id: number
  title: string
  artist: string | null
  album: string | null
  /** Where the file is, or was last seen for a missing song. */
  path: string
  durationMs: number
  bpm: number | null
  bpmSource: BpmSource | null
  bpmConfidence: number | null
  /** Zero-based place in the manual playlist being shown; null elsewhere. */
  position: number | null
  tagIds: number[]
  playCount: number
  skipCount: number
}

export type BpmSource = 'tag' | 'analyzed' | 'manual'

/** A track's tempo after it changed, as the host reports it. */
export interface BpmInfo {
  id: number
  bpm: number | null
  bpmSource: BpmSource | null
  bpmConfidence: number | null
}

/** A track's play and skip counts, as the host reports them. */
export interface SkipInfo {
  id: number
  playCount: number
  skipCount: number
}

/** A song's editable details, as the host reports them. Null details may be left out. */
export interface TrackInfo {
  id: number
  fileName: string
  title: string
  artist?: string | null
  album?: string | null
  albumArtist?: string | null
  genre?: string | null
  year?: number | null
  trackNumber?: number | null
  /** The fields set by hand, which a rescan leaves alone. */
  overridden: InfoField[]
}

export type InfoField = 'title' | 'artist' | 'album' | 'albumArtist' | 'genre' | 'year' | 'trackNumber'

export interface ScanStatus {
  running: boolean
  processed: number
  total: number
}

/**
 * What the list shows: the whole library, a filter playlist loaded into the controls, a manual
 * playlist, the songs suggested for removal, the hidden ones, or the ones whose file is missing.
 */
export type View =
  | { kind: 'library' }
  | { kind: 'filter'; id: number }
  | { kind: 'manual'; id: number }
  | { kind: 'suggested' }
  | { kind: 'hidden' }
  | { kind: 'missing' }

/** How many songs the suggested, hidden and missing views hold, and how many library songs have no tags. */
export interface LibraryCounts {
  suggested: number
  hidden: number
  untagged: number
  missing: number
}

interface RecycleResult {
  recycled: number
  failed: string[]
}

interface QueryPage {
  total: number
  rows: TrackRow[]
}

export const PAGE_SIZE = 200
const SEARCH_DEBOUNCE_MS = 60
const LENS_THROTTLE_MS = 60
const LENS_QUEUE_DEBOUNCE_MS = 250
const BPM_REFRESH_MS = 1000

/**
 * The library list as a lazily loaded, paged view over the host's query results.
 * Rows are fetched a page at a time as the list scrolls; every refresh bumps `version`, and
 * replies for older versions are dropped.
 *
 * Selection: `cursor` is the row the keyboard acts on. Ctrl/Shift+click build an explicit
 * `selection` of track ids; while it is empty, the cursor row counts as the selection.
 */
class Library {
  folders = $state<string[]>([])
  scan = $state<ScanStatus>({ running: false, processed: 0, total: 0 })
  view = $state<View>({ kind: 'library' })
  text = $state('')
  sort = $state<SortKey>('artist')
  desc = $state(false)
  filter = $state<TagFilter>(emptyFilter())
  /** The BPM range narrowing every view (and the queue). It stays set when the view changes. */
  lens = $state<BpmRange>(openRange())
  total = $state(0)
  cursor = $state(0)
  /** Explicitly selected track ids, each mapped to the row index it had when selected (for list order). */
  selection = $state.raw(new Map<number, number>())
  loaded = $state(false)
  error = $state<string | null>(null)
  /** Bumped when the list should scroll back to the top (new search, sort, filter or view). */
  scrollResets = $state(0)
  counts = $state<LibraryCounts>({ suggested: 0, hidden: 0, untagged: 0, missing: 0 })

  private pages = $state.raw(new Map<number, TrackRow[]>())
  private version = 0
  /** Bumped when the list's contents change identity, so late selection replies are dropped. */
  private listId = 0
  private anchor = 0
  private moving = false
  private loading = new Set<number>()
  private visiblePages: number[] = [0]
  private debounce: ReturnType<typeof setTimeout> | undefined
  /** The library view's sort, restored when coming back from a playlist. */
  private librarySort: { sort: SortKey; desc: boolean } = { sort: 'artist', desc: false }
  private lensTimer: ReturnType<typeof setTimeout> | undefined
  private lensPending = false
  private lensQueueTimer: ReturnType<typeof setTimeout> | undefined
  private bpmRefreshTimer: ReturnType<typeof setTimeout> | undefined
  /** Songs tagged while the Untagged list is shown; they stay in it until the list is opened anew. */
  private kept = new Set<number>()

  constructor() {
    if (!hasHost) return
    on<ScanStatus>('library.scan', (s) => (this.scan = s))
    on('library.changed', () => {
      this.refresh()
      this.refreshCounts()
    })
    on<{ message: string }>('library.error', (e) => (this.error = e.message))
    call<string[]>('library.getFolders').then((f) => (this.folders = f))
    call<ScanStatus>('library.getScanStatus').then((s) => (this.scan = s))
    this.refresh()
    this.refreshCounts()
  }

  get context(): QueryContext {
    return {
      text: this.text,
      sort: this.sort,
      desc: this.desc,
      filter: toTrackFilter(this.filter),
      playlistId: this.view.kind === 'manual' ? this.view.id : null,
      bpm: this.lensActive ? { ...this.lens } : null,
      scope: this.scope,
      ...(this.kept.size > 0 ? { keepIds: [...this.kept] } : {}),
    }
  }

  get scope(): TrackScope {
    const kind = this.view.kind
    return kind === 'suggested' || kind === 'hidden' || kind === 'missing' ? kind : 'library'
  }

  get lensActive() {
    return !isOpenRange(this.lens)
  }

  /** Anything hiding songs from the list: search text, the view's filter, or the BPM lens. */
  get narrowed() {
    return this.text.trim() !== '' || this.hasFilter || this.lensActive
  }

  /** The playlist being shown, if any. */
  get playlist(): Playlist | undefined {
    return this.view.kind === 'filter' || this.view.kind === 'manual' ? playlists.byId.get(this.view.id) : undefined
  }

  /** A filter playlist whose search, filter or sort was changed since it was opened or saved. */
  get dirty() {
    const query = this.view.kind === 'filter' ? this.playlist?.query : null
    return query != null && !sameView(this.context, query)
  }

  get hasFilter() {
    return !isEmptyFilter(this.filter)
  }

  /** Rows can be rearranged only when the list shows the whole manual playlist in its own order. */
  get canReorder() {
    return this.view.kind === 'manual' && this.sort === 'position' && !this.desc && !this.narrowed
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

  // ---- Views ----------------------------------------------------------------------------------

  openLibrary = () => {
    if (this.view.kind === 'library' && !this.text && !this.hasFilter) return
    this.view = { kind: 'library' }
    this.text = ''
    this.filter = emptyFilter()
    ;({ sort: this.sort, desc: this.desc } = this.librarySort)
    this.resetList()
  }

  openPlaylist = (playlist: Playlist) => {
    if (this.view.kind === 'library') this.librarySort = { sort: this.sort, desc: this.desc }
    if (playlist.kind === 'manual') {
      this.view = { kind: 'manual', id: playlist.id }
      this.text = ''
      this.filter = emptyFilter()
      this.sort = 'position'
      this.desc = false
    } else {
      this.view = { kind: 'filter', id: playlist.id }
      this.load(playlist.query)
    }
    this.resetList()
  }

  /** Songs skipped so often they may not belong in the library; most skipped first. */
  openSuggested = () => this.openScope({ kind: 'suggested' }, 'skips', true)

  openHidden = () => this.openScope({ kind: 'hidden' }, 'artist', false)

  /** Songs whose file is gone, e.g. from a backup whose files aren't in the library folders yet. */
  openMissing = () => this.openScope({ kind: 'missing' }, 'title', false)

  /** Shows the whole library narrowed to one tag. */
  showTag = (id: number) => this.showFiltered({ ...emptyFilter(), include: [id] })

  /** Shows the library songs that carry no tags yet, such as ones that just arrived. */
  showUntagged = () => this.showFiltered({ ...emptyFilter(), untagged: true })

  /** Throws away edits to the open filter playlist. */
  revert = () => {
    if (this.view.kind !== 'filter' || !this.playlist) return
    this.load(this.playlist.query)
    this.resetList()
  }

  saveChanges = () => {
    const playlist = this.playlist
    if (this.view.kind !== 'filter' || !playlist) return
    ui.run(() => playlists.updateFilter(playlist.id, this.context))
  }

  /**
   * Saves the current search, filter and sort as a new filter playlist, then offers to name it.
   * An active BPM lens is saved as the playlist's own range, so it keeps showing what is on screen.
   */
  saveViewAsPlaylist = () =>
    ui.run(async () => {
      const filter = withLens(this.context.filter, this.context.bpm)
      const id = await playlists.createFilter(this.suggestName(), { ...this.context, filter })
      this.view = { kind: 'filter', id }
      this.filter = fromTrackFilter(filter)
      ui.renaming = { kind: 'playlist', id }
    })

  // ---- Search, sort and filter -----------------------------------------------------------------

  setText(text: string) {
    this.text = text
    this.clearForNewList()
    clearTimeout(this.debounce)
    this.debounce = setTimeout(() => this.refresh({ resetScroll: true }), SEARCH_DEBOUNCE_MS)
  }

  setSort(key: SortKey) {
    if (key === 'position' && this.view.kind !== 'manual') return
    if (this.sort === key) this.desc = !this.desc
    else {
      this.sort = key
      this.desc = false
    }
    this.resetList()
  }

  setFilter(filter: TagFilter) {
    this.filter = filter
    this.resetList()
  }

  /** Ctrl+click on a sidebar tag: add it to (or take it out of) the included tags. Untagged no longer applies. */
  toggleInclude = (id: number) => {
    const f = this.filter
    this.setFilter({
      ...f,
      include: f.include.includes(id) ? f.include.filter((t) => t !== id) : [...f.include, id],
      exclude: f.exclude.filter((t) => t !== id),
      untagged: false,
    })
  }

  /** Alt+click on a sidebar tag: exclude it (or stop excluding it). Untagged no longer applies. */
  toggleExclude = (id: number) => {
    const f = this.filter
    this.setFilter({
      ...f,
      include: f.include.filter((t) => t !== id),
      exclude: f.exclude.includes(id) ? f.exclude.filter((t) => t !== id) : [...f.exclude, id],
      untagged: false,
    })
  }

  /** Filter chip click: an included tag becomes excluded and vice versa. */
  flipTag = (id: number) => {
    if (this.filter.include.includes(id)) this.toggleExclude(id)
    else this.toggleInclude(id)
  }

  dropTag = (id: number) => {
    const f = this.filter
    if (!f.include.includes(id) && !f.exclude.includes(id)) return
    this.setFilter({ ...f, include: f.include.filter((t) => t !== id), exclude: f.exclude.filter((t) => t !== id) })
  }

  dropUntagged = () => this.setFilter({ ...this.filter, untagged: false })

  /** Called before tagging songs: in the Untagged list they stay put instead of vanishing. */
  keepListed = (ids: number[]) => {
    if (this.filter.untagged) for (const id of ids) this.kept.add(id)
  }

  setMatchMode = (mode: 'all' | 'any') => this.setFilter({ ...this.filter, mode })

  clearFilter = () => this.setFilter(emptyFilter())

  /** Removes the view's own BPM range (a filter playlist's saved one); the lens is separate. */
  dropBpmRange = () =>
    this.setFilter({ ...this.filter, bpmMin: null, bpmMax: null, includeUnknownBpm: false })

  // ---- BPM lens -----------------------------------------------------------------------------------

  /**
   * Narrows every view to a BPM range. The list follows within a frame or two while a handle is
   * dragged; the queue is re-filtered once the range settles.
   */
  setLens(range: BpmRange) {
    this.lens = range
    this.clearForNewList()
    this.lensPending = true
    if (this.lensTimer === undefined) {
      const flush = () => {
        if (this.lensPending) {
          this.lensPending = false
          this.refresh({ resetScroll: true })
          this.lensTimer = setTimeout(flush, LENS_THROTTLE_MS)
        } else {
          this.lensTimer = undefined
        }
      }
      flush()
    }

    clearTimeout(this.lensQueueTimer)
    this.lensQueueTimer = setTimeout(() => player.setBpmLens(this.context.bpm), LENS_QUEUE_DEBOUNCE_MS)
  }

  resetLens = () => this.setLens(openRange())

  /** Puts changed BPMs into the rows already loaded; re-queries when the change can move rows. */
  patchRows(updates: BpmInfo[]) {
    const byId = new Map(updates.map((u) => [u.id, u]))
    let next: Map<number, TrackRow[]> | undefined
    for (const [page, rows] of this.pages) {
      let copy: TrackRow[] | undefined
      rows.forEach((row, i) => {
        const u = byId.get(row.id)
        if (!u) return
        copy ??= [...rows]
        copy[i] = { ...row, bpm: u.bpm, bpmSource: u.bpmSource, bpmConfidence: u.bpmConfidence }
      })
      if (copy) (next ??= new Map(this.pages)).set(page, copy)
    }
    if (next) this.pages = next

    const bpmShapesList =
      this.sort === 'bpm' || this.lensActive || !isOpenRange({ min: this.filter.bpmMin, max: this.filter.bpmMax })
    if (bpmShapesList && this.bpmRefreshTimer === undefined) {
      this.bpmRefreshTimer = setTimeout(() => {
        this.bpmRefreshTimer = undefined
        this.refresh()
      }, BPM_REFRESH_MS)
    }
  }

  // ---- Selection -------------------------------------------------------------------------------

  isSelected(index: number, id: number | undefined) {
    return this.selection.size > 0 ? id !== undefined && this.selection.has(id) : index === this.cursor
  }

  get selectedCount() {
    return this.selection.size > 0 ? this.selection.size : this.total > 0 ? 1 : 0
  }

  /** The selected track ids in list order (the cursor row when nothing is explicitly selected). */
  selectedIds(): number[] {
    if (this.selection.size > 0) {
      return [...this.selection].sort((a, b) => a[1] - b[1]).map(([id]) => id)
    }
    const row = this.row(this.cursor)
    return row ? [row.id] : []
  }

  /** A click on a row, following the usual Ctrl (toggle) and Shift (range) conventions. */
  click(index: number, e: { ctrlKey: boolean; shiftKey: boolean }) {
    if (e.shiftKey) {
      this.cursor = index
      this.selectRange(this.anchor, index, e.ctrlKey)
      return
    }

    if (e.ctrlKey) {
      const row = this.row(index)
      if (!row) return
      const next = new Map(this.selection)
      // Ctrl+click extends the implicit selection rather than replacing it.
      const current = this.row(this.cursor)
      if (next.size === 0 && current && this.cursor !== index) next.set(current.id, this.cursor)
      if (next.has(row.id)) next.delete(row.id)
      else next.set(row.id, index)
      this.selection = next
      this.cursor = this.anchor = index
      return
    }

    this.setCursor(index)
  }

  /** Right-click: keep the selection if the row is part of it, otherwise select just that row. */
  contextSelect(index: number) {
    if (!this.isSelected(index, this.row(index)?.id)) this.setCursor(index)
  }

  /** Arrow keys: move the cursor; with Shift, grow the selection from the anchor. */
  moveCursor(delta: number, extend = false) {
    if (this.total === 0) return
    const next = Math.min(Math.max(this.cursor + delta, 0), this.total - 1)
    if (extend) {
      this.cursor = next
      this.selectRange(this.anchor, next, false)
    } else {
      this.setCursor(next)
    }
  }

  setCursor(index: number) {
    this.selection = new Map()
    this.cursor = this.anchor = index
  }

  selectAll = async () => {
    const listId = this.listId
    const ids = await call<number[]>('library.queryIds', this.queryParams())
    if (listId !== this.listId) return
    this.selection = new Map(ids.map((id, i) => [id, i]))
  }

  /** Returns false when there was no explicit selection to clear. */
  clearSelection() {
    if (this.selection.size === 0) return false
    this.selection = new Map()
    return true
  }

  // ---- Playback and playlist edits ---------------------------------------------------------------

  playIndex(index: number) {
    const row = this.row(index)
    if (!row) return
    this.cursor = index
    // A missing song has no file to play.
    if (this.view.kind !== 'missing') player.playTrack(row.id, this.context)
  }

  playSelected = () => this.playIndex(this.cursor)

  playShuffled = () => {
    if (this.total > 0 && this.view.kind !== 'missing') player.playShuffled(this.context)
  }

  /** Takes the selected songs out of the manual playlist being shown. */
  removeSelectedFromPlaylist = () => {
    const playlist = this.playlist
    const ids = this.selectedIds()
    if (this.view.kind !== 'manual' || !playlist || ids.length === 0) return
    ui.run(async () => {
      await playlists.removeTracks(playlist.id, ids)
      this.setCursor(Math.min(this.cursor, Math.max(this.total - ids.length - 1, 0)))
      ui.notify(`Removed ${songs(ids.length)} from ${playlist.name}.`)
    })
  }

  /** Alt+↑/↓: moves the selected songs one place up or down in the manual playlist. */
  moveSelected = async (direction: -1 | 1) => {
    const playlist = this.playlist
    if (this.view.kind !== 'manual' || !playlist || this.moving) return
    if (!this.canReorder) {
      ui.notify('Sort by # and clear the search and filter to rearrange songs.')
      return
    }

    const block =
      this.selection.size > 0
        ? [...this.selection].sort((a, b) => a[1] - b[1])
        : this.row(this.cursor)
          ? [[this.row(this.cursor)!.id, this.cursor] as [number, number]]
          : []
    if (block.length === 0) return

    const first = block[0][1]
    const contiguous = block.every(([, index], i) => index === first + i)
    const to = Math.min(Math.max(first + direction, 0), this.total - block.length)
    if (contiguous && to === first) return

    this.moving = true
    try {
      await playlists.moveTracks(playlist.id, block.map(([id]) => id), to)
      const cursorOffset = Math.max(block.findIndex(([, index]) => index === this.cursor), 0)
      if (this.selection.size > 0) this.selection = new Map(block.map(([id], i) => [id, to + i]))
      this.cursor = to + cursorOffset
      this.anchor = to
    } catch (e) {
      ui.fail(e)
    } finally {
      this.moving = false
    }
  }

  // ---- Suggestions, hiding and deleting ------------------------------------------------------------

  /** Takes the selected songs off the suggestions by starting their play and skip counts over. */
  keepSelected = () =>
    this.changeSelected('library.keep', (n) => `Kept ${songs(n)}. ${n === 1 ? 'Its' : 'Their'} play and skip counts start over.`)

  getSkips = (trackIds: number[]) => call<SkipInfo[]>('library.getSkips', { trackIds })

  /** Sets skip counts by hand; songs whose flag changes join or leave the suggestions. */
  setSkips = (trackIds: number[], skips: number) => call('library.setSkips', { trackIds, skips })

  clearSkipsSelected = () => {
    const ids = this.selectedIds()
    if (ids.length === 0) return
    ui.run(async () => {
      await this.setSkips(ids, 0)
      // Only the suggestions lose the songs; elsewhere the selection stays where it is.
      if (this.view.kind === 'suggested') this.setCursor(Math.min(this.cursor, Math.max(this.total - ids.length - 1, 0)))
      ui.notify(ids.length === 1 ? 'Cleared the skips.' : `Cleared the skips of ${songs(ids.length)}.`)
    })
  }

  getInfo = (trackIds: number[]) => call<TrackInfo[]>('library.getInfo', { trackIds })

  /** Overrides the given fields (null clears one) and puts the file's tags back for the `reset` ones. */
  setInfo = (trackIds: number[], set: Partial<Record<InfoField, string | number | null>>, reset: InfoField[] = []) =>
    call('library.setInfo', { trackIds, set, reset })

  hideSelected = () =>
    this.changeSelected('library.hide', (n) => `Hid ${songs(n)}. Find ${n === 1 ? 'it' : 'them'} under Hidden songs.`)

  unhideSelected = () => this.changeSelected('library.unhide', (n) => `${songs(n)} back in the library.`)

  /** Asks, then moves the selected songs' files to the Recycle Bin. */
  confirmRecycle = () => {
    const ids = this.selectedIds()
    if (ids.length === 0) return
    const titles = this.titlesOf(ids)
    ui.openConfirm({
      title: ids.length === 1 ? 'Delete this song?' : `Delete ${songs(ids.length)}?`,
      message: `The ${ids.length === 1 ? 'file moves' : 'files move'} to the Recycle Bin, so you can still restore ${ids.length === 1 ? 'it' : 'them'} from there.`,
      items: titles.length < ids.length ? [...titles, `and ${(ids.length - titles.length).toLocaleString()} more`] : titles,
      confirmLabel: 'Move to Recycle Bin',
      danger: true,
      action: () =>
        ui.run(async () => {
          const result = await call<RecycleResult>('library.recycle', { trackIds: ids })
          this.setCursor(Math.min(this.cursor, Math.max(this.total - result.recycled - 1, 0)))
          if (result.failed.length > 0) {
            ui.notify(`Couldn't delete ${result.failed.map((t) => `“${t}”`).join(', ')}.`, true)
          } else {
            ui.notify(`Moved ${songs(result.recycled)} to the Recycle Bin.`)
          }
        }),
    })
  }

  /** Asks, then drops the selected missing songs from the library for good. */
  confirmForget = () => {
    const ids = this.selectedIds()
    if (ids.length === 0 || this.view.kind !== 'missing') return
    const titles = this.titlesOf(ids)
    ui.openConfirm({
      title: ids.length === 1 ? 'Forget this song?' : `Forget ${songs(ids.length)}?`,
      message:
        ids.length === 1
          ? 'Its tags, playlist places, plays and skips are removed. If the file turns up later, it comes back as a new song.'
          : 'Their tags, playlist places, plays and skips are removed. If the files turn up later, they come back as new songs.',
      items: titles.length < ids.length ? [...titles, `and ${(ids.length - titles.length).toLocaleString()} more`] : titles,
      confirmLabel: 'Forget',
      danger: true,
      action: () =>
        ui.run(async () => {
          await call('library.forget', { trackIds: ids })
          this.setCursor(Math.min(this.cursor, Math.max(this.total - ids.length - 1, 0)))
          ui.notify(`Forgot ${songs(ids.length)}.`)
        }),
    })
  }

  /** Copies where the selected songs' files are (or were), one per line. */
  copySelectedPaths = () => {
    const wanted = new Set(this.selectedIds())
    const paths: string[] = []
    for (const rows of this.pages.values()) {
      for (const row of rows) if (wanted.has(row.id)) paths.push(row.path)
    }
    if (paths.length === 0) return
    ui.run(async () => {
      await navigator.clipboard.writeText(paths.join('\r\n'))
      ui.notify(paths.length === 1 ? 'Copied the file path.' : `Copied ${paths.length.toLocaleString()} file paths.`)
    })
  }

  // ---- Folders ---------------------------------------------------------------------------------

  addFolder = async () => {
    const folders = await call<string[] | null>('library.addFolder')
    if (folders) this.folders = folders
  }

  removeFolder = async (path: string) => {
    this.folders = await call<string[]>('library.removeFolder', { path })
  }

  rescan = () => call('library.rescan')

  // ---- Internals -------------------------------------------------------------------------------

  private showFiltered(filter: TagFilter) {
    if (this.view.kind !== 'library') {
      this.view = { kind: 'library' }
      ;({ sort: this.sort, desc: this.desc } = this.librarySort)
    }
    this.text = ''
    this.filter = filter
    this.resetList()
  }

  private openScope(view: { kind: 'suggested' | 'hidden' | 'missing' }, sort: SortKey, desc: boolean) {
    if (this.view.kind === view.kind) return
    if (this.view.kind === 'library') this.librarySort = { sort: this.sort, desc: this.desc }
    this.view = view
    this.text = ''
    this.filter = emptyFilter()
    this.sort = sort
    this.desc = desc
    this.resetList()
  }

  /** Runs a host action on the selected songs; the ones that leave the list take the cursor with them. */
  private changeSelected(method: string, message: (count: number) => string) {
    const ids = this.selectedIds()
    if (ids.length === 0) return
    ui.run(async () => {
      await call(method, { trackIds: ids })
      this.setCursor(Math.min(this.cursor, Math.max(this.total - ids.length - 1, 0)))
      ui.notify(message(ids.length))
    })
  }

  private async refreshCounts() {
    try {
      this.counts = await call<LibraryCounts>('library.getCounts')
    } catch {
      // The sidebar keeps the last counts; the next change tries again.
    }
  }

  /** Titles of the given songs that are loaded, at most `max` of them. */
  private titlesOf(ids: number[], max = 6) {
    const wanted = new Set(ids)
    const titles: string[] = []
    for (const rows of this.pages.values()) {
      for (const row of rows) {
        if (wanted.has(row.id) && titles.length < max) titles.push(row.title)
      }
    }
    return titles
  }

  private load(query: SavedQuery | null | undefined) {
    this.text = query?.text ?? ''
    this.sort = query?.sort && query.sort !== 'position' ? query.sort : 'artist'
    this.desc = query?.descending ?? false
    this.filter = fromTrackFilter(query?.filter)
  }

  /** A name for a saved view, from its tags, BPM range and search text: "chill + party −live 120–130 BPM “beat”". */
  private suggestName() {
    const range = withLens(this.context.filter, this.context.bpm)
    const names = (ids: number[]) => tags.resolve(ids).map((t) => t.name)
    const parts = [
      this.filter.untagged ? 'Untagged' : '',
      names(this.filter.include).join(this.filter.mode === 'all' ? ' + ' : ' / '),
      names(this.filter.exclude)
        .map((n) => `−${n}`)
        .join(' '),
      range && !isOpenRange({ min: range.bpmMin, max: range.bpmMax }) ? `${formatRange(range.bpmMin, range.bpmMax)} BPM` : '',
      this.text.trim() ? `“${this.text.trim()}”` : '',
    ].filter(Boolean)
    return parts.join(' ') || 'New playlist'
  }

  private clearForNewList() {
    this.listId++
    this.kept.clear()
    this.selection = new Map()
    this.cursor = this.anchor = 0
  }

  private resetList() {
    clearTimeout(this.debounce)
    this.clearForNewList()
    this.refresh({ resetScroll: true })
  }

  private async selectRange(from: number, to: number, add: boolean) {
    const lo = Math.min(from, to)
    const hi = Math.max(from, to)
    const listId = this.listId

    let ids: number[] = []
    for (let i = lo; i <= hi; i++) {
      const row = this.row(i)
      if (!row) {
        ids = await call<number[]>('library.queryIds', this.queryParams(lo, hi - lo + 1))
        if (listId !== this.listId) return
        break
      }
      ids.push(row.id)
    }

    const next = add ? new Map(this.selection) : new Map<number, number>()
    ids.forEach((id, i) => next.set(id, lo + i))
    this.selection = next
  }

  private queryParams(offset?: number, limit?: number): Record<string, unknown> {
    return { ...this.context, offset, limit }
  }

  /**
   * Reloads the pages currently on screen and swaps them in together, so a refresh during a
   * scan doesn't blank the list. Search, sort, filter and view changes jump back to the top.
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
      this.cursor = Math.min(this.cursor, Math.max(this.total - 1, 0))
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

export const songs = (n: number) => `${n.toLocaleString()} ${n === 1 ? 'song' : 'songs'}`

export const library = new Library()
