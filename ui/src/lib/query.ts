// The shape of list queries as the host understands them (see TrackQuery / TrackFilter in Barbaric.Core).

export type SortKey = 'artist' | 'title' | 'album' | 'duration' | 'bpm' | 'added' | 'plays' | 'skips' | 'position'

/** Which part of the library a query looks at: visible songs, the ones suggested for removal, hidden ones, or missing ones. */
export type TrackScope = 'library' | 'suggested' | 'hidden' | 'missing'

/** Tag and BPM constraints. Empty lists and nulls mean "no constraint". */
export interface TrackFilter {
  allTags: number[]
  anyTags: number[]
  noneTags: number[]
  /** Only songs that carry no tags at all. */
  untagged: boolean
  bpmMin: number | null
  bpmMax: number | null
  /** With a BPM bound set, also keep songs whose BPM isn't known. */
  includeUnknownBpm: boolean
}

/** A BPM range; a null bound leaves that side open. */
export interface BpmRange {
  min: number | null
  max: number | null
  includeUnknown: boolean
}

/** The span the BPM range slider covers; a handle at either end means "no limit" on that side. */
export const BPM_DOMAIN = { min: 50, max: 220 } as const

export const openRange = (): BpmRange => ({ min: null, max: null, includeUnknown: false })

export const isOpenRange = (r: { min: number | null; max: number | null }) => r.min === null && r.max === null

/** "120–130", "≥ 120", "≤ 130" or "" for an open range. */
export function formatRange(min: number | null, max: number | null) {
  const n = (v: number) => (Math.round(v * 10) / 10).toString()
  if (min !== null && max !== null) return min === max ? n(min) : `${n(min)}–${n(max)}`
  if (min !== null) return `≥ ${n(min)}`
  if (max !== null) return `≤ ${n(max)}`
  return ''
}

/** Which list a track was played from, so the host can queue the rest of it. */
export interface QueryContext {
  text: string
  sort: SortKey
  desc: boolean
  filter: TrackFilter | null
  /** Set when the list is a manual playlist. */
  playlistId: number | null
  /** The BPM range narrowing every view. Never saved with a playlist. */
  bpm: BpmRange | null
  scope: TrackScope
}

/** A filter playlist's saved view, as the host returns it. */
export interface SavedQuery {
  text: string | null
  sort: SortKey
  descending: boolean
  filter: Partial<TrackFilter> | null
}

/**
 * The tag filter as the UI edits it: a set of included tags matched all-or-any, and a set of
 * excluded tags, or else just the untagged songs. A filter playlist can also carry its own BPM
 * range, shown as a chip.
 */
export interface TagFilter {
  include: number[]
  mode: 'all' | 'any'
  exclude: number[]
  untagged: boolean
  bpmMin: number | null
  bpmMax: number | null
  includeUnknownBpm: boolean
}

export const emptyFilter = (): TagFilter => ({
  include: [],
  mode: 'all',
  exclude: [],
  untagged: false,
  bpmMin: null,
  bpmMax: null,
  includeUnknownBpm: false,
})

export const isEmptyFilter = (f: TagFilter) =>
  f.include.length === 0 && f.exclude.length === 0 && !f.untagged && f.bpmMin === null && f.bpmMax === null

export function toTrackFilter(f: TagFilter): TrackFilter | null {
  if (isEmptyFilter(f)) return null
  return {
    allTags: f.mode === 'all' ? f.include : [],
    anyTags: f.mode === 'any' ? f.include : [],
    noneTags: f.exclude,
    untagged: f.untagged,
    bpmMin: f.bpmMin,
    bpmMax: f.bpmMax,
    includeUnknownBpm: f.includeUnknownBpm,
  }
}

/**
 * The filter to save for a view seen through the BPM lens: the playlist's own range narrowed to
 * the lens, so the saved playlist shows what is on screen now.
 */
export function withLens(filter: TrackFilter | null, lens: BpmRange | null): TrackFilter | null {
  if (!lens || isOpenRange(lens)) return filter
  const f = filter ?? { allTags: [], anyTags: [], noneTags: [], untagged: false, bpmMin: null, bpmMax: null, includeUnknownBpm: false }
  const own = f.bpmMin !== null || f.bpmMax !== null
  const pick = (a: number | null, b: number | null, fn: (x: number, y: number) => number) =>
    a === null ? b : b === null ? a : fn(a, b)
  return {
    ...f,
    bpmMin: pick(f.bpmMin, lens.min, Math.max),
    bpmMax: pick(f.bpmMax, lens.max, Math.min),
    includeUnknownBpm: lens.includeUnknown && (!own || f.includeUnknownBpm),
  }
}

export function fromTrackFilter(f: Partial<TrackFilter> | null | undefined): TagFilter {
  const all = f?.allTags ?? []
  const any = f?.anyTags ?? []
  return {
    // The UI never saves both, but a hand-edited filter might: "all" is the stricter reading.
    include: all.length > 0 ? [...all] : [...any],
    mode: all.length === 0 && any.length > 0 ? 'any' : 'all',
    exclude: [...(f?.noneTags ?? [])],
    untagged: f?.untagged ?? false,
    bpmMin: f?.bpmMin ?? null,
    bpmMax: f?.bpmMax ?? null,
    includeUnknownBpm: f?.includeUnknownBpm ?? false,
  }
}

/**
 * True when a view (text, sort and filter) matches a saved query, ignoring id order and blank text.
 * The BPM lens is not part of a view, so it never makes a playlist look edited.
 */
export function sameView(context: QueryContext, saved: SavedQuery) {
  const key = (text: string | null, sort: SortKey, desc: boolean, filter: TrackFilter | null) => {
    const f = filter ?? { allTags: [], anyTags: [], noneTags: [], untagged: false, bpmMin: null, bpmMax: null, includeUnknownBpm: false }
    const ids = (list: number[] | undefined) => [...(list ?? [])].sort((a, b) => a - b).join(',')
    return [
      (text ?? '').trim(),
      sort,
      desc,
      ids(f.allTags),
      ids(f.anyTags),
      ids(f.noneTags),
      f.untagged ?? false,
      f.bpmMin ?? '',
      f.bpmMax ?? '',
      f.includeUnknownBpm && !isOpenRange({ min: f.bpmMin, max: f.bpmMax }),
    ].join('|')
  }
  const savedFilter = toTrackFilter(fromTrackFilter(saved.filter))
  return (
    key(context.text, context.sort, context.desc, context.filter) ===
    key(saved.text, saved.sort, saved.descending, savedFilter)
  )
}
