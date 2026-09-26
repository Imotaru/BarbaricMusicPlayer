const RESET_AFTER_MS = 2000
const MAX_TAPS = 9

/** Tap along to a song to measure its tempo: the average of the last eight intervals. */
export class TapTempo {
  private taps: number[] = []

  get count() {
    return this.taps.length
  }

  /** Records a tap; returns the tempo once there are three taps, otherwise null. */
  tap(now = performance.now()): number | null {
    const last = this.taps.at(-1)
    if (last !== undefined && now - last > RESET_AFTER_MS) this.taps = []
    this.taps.push(now)
    if (this.taps.length > MAX_TAPS) this.taps.shift()
    if (this.taps.length < 3) return null
    return (60000 * (this.taps.length - 1)) / (this.taps.at(-1)! - this.taps[0])
  }

  reset() {
    this.taps = []
  }
}
