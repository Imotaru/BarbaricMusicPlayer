import { call, hasHost, on } from './bridge'
import type { BpmRange, QueryContext } from './query'

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
  /** The gain that evens this song out with the others; null until it's measured. */
  autoGainDb: number | null
  normalize: boolean
  weighByLength: boolean
  volume: number
  /** The top of the master volume slider; the volume never goes above it. */
  volumeLimit: number
  hasNext: boolean
  hasPrevious: boolean
  shuffle: boolean
  loop: boolean
  /** Changes whenever the queue or its pool does. */
  queueVersion: number
  /** How many songs the player is going through. */
  poolSize: number
  /** The list those songs were drawn from; null for a song played on its own. */
  source: QueryContext | null
}

export const GAIN_MIN_DB = -24
export const GAIN_MAX_DB = 12
export const VOLUME_LIMIT_MIN = 0.01

/** A master volume as a percent of full volume, with a decimal only when it has one: 30%, 7.5%. */
export const formatVolume = (volume: number) => `${Math.round(volume * 1000) / 10}%`

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
  autoGainDb = $state<number | null>(null)
  normalize = $state(true)
  weighByLength = $state(true)
  volume = $state(1)
  volumeLimit = $state(1)
  hasNext = $state(false)
  hasPrevious = $state(false)
  shuffle = $state(false)
  loop = $state(false)
  queueVersion = $state(0)
  poolSize = $state(0)
  source = $state<QueryContext | null>(null)
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

  /** Plays one of the songs being played through; the queue carries on around it. */
  playFromPool = (id: number) => this.run(() => call('player.playFromPool', { id }))

  /** Turns shuffle on and plays the list from a song shuffle picks, so nothing has to be skipped. */
  playShuffled = (context: QueryContext) => {
    this.shuffle = true
    this.run(() => call('player.playShuffled', { context }))
  }

  /** Re-filters the queue by the BPM lens; the playing song carries on. */
  setBpmLens = (bpm: BpmRange | null) => this.run(() => call('player.setBpmLens', { bpm }))

  toggle = () => this.loaded && this.run(() => call('player.toggle'))

  next = () => this.run(() => call('player.next'))

  previous = () => this.run(() => call('player.previous'))

  /** Skip-aware shuffle; the playing song carries on either way. */
  toggleShuffle = () => {
    this.shuffle = !this.shuffle
    this.run(() => call('player.setShuffle', { on: this.shuffle }))
  }

  /** Repeats the current song instead of moving on when it ends. Lists always start over after their last song. */
  toggleLoop = () => {
    this.loop = !this.loop
    this.run(() => call('player.setLoop', { on: this.loop }))
  }

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

  /** Plays every song equally loud; the Song slider then adjusts from there. */
  setNormalize = (on: boolean) => {
    this.normalize = on
    this.run(() => call('player.setNormalize', { on }))
  }

  /** Makes shuffle play long songs less often, so every song gets about the same listening time. */
  setWeighByLength = (on: boolean) => {
    this.weighByLength = on
    this.run(() => call('player.setWeighByLength', { on }))
  }

  setVolume = (volume: number) => {
    this.volume = Math.min(Math.max(volume, 0), this.volumeLimit)
    this.run(() => call('player.setVolume', { volume: this.volume }))
  }

  /** Turns the volume up or down by steps of a twentieth of the limit, the same as the host's global hotkeys. */
  stepVolume = (steps: number) => {
    const step = this.volumeLimit / 20
    this.setVolume(Math.round((this.volume + steps * step) / step) * step)
  }

  /** Sets the top of the master volume slider; a volume above it comes down to it. */
  setVolumeLimit = (limit: number) => {
    this.volumeLimit = Math.min(Math.max(limit, VOLUME_LIMIT_MIN), 1)
    this.volume = Math.min(this.volume, this.volumeLimit)
    this.run(() => call('player.setVolumeLimit', { limit: this.volumeLimit }))
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
    this.autoGainDb = s.autoGainDb
    this.normalize = s.normalize
    this.weighByLength = s.weighByLength
    this.volume = s.volume
    this.volumeLimit = s.volumeLimit
    this.hasNext = s.hasNext
    this.hasPrevious = s.hasPrevious
    this.shuffle = s.shuffle
    this.loop = s.loop
    this.queueVersion = s.queueVersion
    this.poolSize = s.poolSize
    this.source = s.source
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
