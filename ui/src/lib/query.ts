// The shape of list queries as the host understands them (see TrackQuery / TrackFilter in Barbaric.Core).

export type SortKey = 'artist' | 'title' | 'album' | 'duration' | 'bpm' | 'added' | 'position'

/** Tag and BPM constraints. Empty lists and nulls mean "no constraint". */
export interface TrackFilter {
  allTags: number[]
  anyTags: number[]
  noneTags: number[]
  bpmMin: number | null
  bpmMax: number | null
}

/** Which list a track was played from, so the host can queue the rest of it. */
export interface QueryContext {
  text: string
  sort: SortKey
  desc: boolean
  filter: TrackFilter | null
  /** Set when the list is a manual playlist. */
  playlistId: number | null
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
 * excluded tags. BPM bounds ride along untouched until phase 4 gives them controls.
 */
export interface TagFilter {
  include: number[]
  mode: 'all' | 'any'
  exclude: number[]
  bpmMin: number | null
  bpmMax: number | null
}

export const emptyFilter = (): TagFilter => ({ include: [], mode: 'all', exclude: [], bpmMin: null, bpmMax: null })

export const isEmptyFilter = (f: TagFilter) =>
  f.include.length === 0 && f.exclude.length === 0 && f.bpmMin === null && f.bpmMax === null

export function toTrackFilter(f: TagFilter): TrackFilter | null {
  if (isEmptyFilter(f)) return null
  return {
    allTags: f.mode === 'all' ? f.include : [],
    anyTags: f.mode === 'any' ? f.include : [],
    noneTags: f.exclude,
    bpmMin: f.bpmMin,
    bpmMax: f.bpmMax,
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
    bpmMin: f?.bpmMin ?? null,
    bpmMax: f?.bpmMax ?? null,
  }
}

/** True when a view (text, sort and filter) matches a saved query, ignoring id order and blank text. */
export function sameView(context: QueryContext, saved: SavedQuery) {
  const key = (text: string | null, sort: SortKey, desc: boolean, filter: TrackFilter | null) => {
    const f = filter ?? { allTags: [], anyTags: [], noneTags: [], bpmMin: null, bpmMax: null }
    const ids = (list: number[] | undefined) => [...(list ?? [])].sort((a, b) => a - b).join(',')
    return [
      (text ?? '').trim(),
      sort,
      desc,
      ids(f.allTags),
      ids(f.anyTags),
      ids(f.noneTags),
      f.bpmMin ?? '',
      f.bpmMax ?? '',
    ].join('|')
  }
  const savedFilter = toTrackFilter(fromTrackFilter(saved.filter))
  return (
    key(context.text, context.sort, context.desc, context.filter) ===
    key(saved.text, saved.sort, saved.descending, savedFilter)
  )
}
