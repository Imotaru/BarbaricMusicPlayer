import { call, hasHost, on } from './bridge'

export type PlaybackState = 'stopped' | 'playing' | 'paused'

export interface PlayerSnapshot {
  state: PlaybackState
  trackId: number | null
  path: string | null
  title: string | null
  artist: string | null
  album: string | null
  duration: number
  position: number
  trackGainDb: number
  volume: number
  hasNext: boolean
  hasPrevious: boolean
}

/** Which list a track was played from, so the host can queue the rest of it. */
export interface QueryContext {
  text: string
  sort: string
  desc: boolean
}

export const GAIN_MIN_DB = -24
export const GAIN_MAX_DB = 12

/** Reactive mirror of the host's playback state. All mutations go through the host. */
class Player {
  state = $state<PlaybackState>('stopped')
  trackId = $state<number | null>(null)
  path = $state<string | null>(null)
  title = $state<string | null>(null)
  artist = $state<string | null>(null)
  album = $state<string | null>(null)
  duration = $state(0)
  position = $state(0)
  trackGainDb = $state(0)
  volume = $state(1)
  hasNext = $state(false)
  hasPrevious = $state(false)
  error = $state<string | null>(null)

  constructor() {
    if (!hasHost) return
    on<PlayerSnapshot>('player.state', (s) => this.apply(s))
    on<{ position: number }>('player.position', (p) => (this.position = p.position))
    on<{ message: string }>('player.error', (e) => (this.error = e.message))
    call<PlayerSnapshot>('player.getState').then((s) => this.apply(s))
  }

  get loaded() {
    return this.path !== null
  }

  openFile = () =>
    this.run(async () => {
      const snapshot = await call<PlayerSnapshot | null>('player.openFile')
      if (snapshot) this.apply(snapshot)
    })

  playTrack = (id: number, context: QueryContext) => this.run(() => call('player.playTrack', { id, context }))

  toggle = () => this.loaded && this.run(() => call('player.toggle'))

  next = () => this.run(() => call('player.next'))

  previous = () => this.run(() => call('player.previous'))

  seek = (seconds: number) => {
    if (!this.loaded) return
    this.position = Math.min(Math.max(seconds, 0), this.duration)
    this.run(() => call('player.seek', { seconds: this.position }))
  }

  seekBy = (delta: number) => this.seek(this.position + delta)

  setTrackGain = (db: number) => {
    this.trackGainDb = db
    this.run(() => call('player.setTrackGain', { db }))
  }

  setVolume = (volume: number) => {
    this.volume = Math.min(Math.max(volume, 0), 1)
    this.run(() => call('player.setVolume', { volume: this.volume }))
  }

  private apply(s: PlayerSnapshot) {
    this.state = s.state
    this.trackId = s.trackId
    this.path = s.path
    this.title = s.title
    this.artist = s.artist
    this.album = s.album
    this.duration = s.duration
    this.position = s.position
    this.trackGainDb = s.trackGainDb
    this.volume = s.volume
    this.hasNext = s.hasNext
    this.hasPrevious = s.hasPrevious
  }

  private async run(action: () => Promise<unknown>) {
    try {
      this.error = null
      await action()
    } catch (e) {
      this.error = e instanceof Error ? e.message : String(e)
    }
  }
}

export const player = new Player()

export function formatTime(seconds: number) {
  const total = Math.max(0, Math.floor(seconds))
  const h = Math.floor(total / 3600)
  const m = Math.floor((total % 3600) / 60)
  const s = (total % 60).toString().padStart(2, '0')
  return h > 0 ? `${h}:${m.toString().padStart(2, '0')}:${s}` : `${m}:${s}`
}

export const formatGain = (db: number) => `${db > 0 ? '+' : ''}${db.toFixed(1)} dB`
