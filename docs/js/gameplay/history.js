// Allocation-free ring buffer of snapshots for time-rewind abilities (Specter's Time Reversal, Chrono's Temporal Reset).
// Works for players and balls: the owner supplies capture() -> snapshot fields. Kernel.
import * as THREE from 'three';

export class RewindHistory {
  /** @param {() => {position:THREE.Vector3, velocity:THREE.Vector3, yaw?:number, hp?:number, state?:string}} capture */
  constructor(capture, seconds = 6, rate = 30) {
    this.capture = capture;
    this.rate = rate;
    this.size = Math.ceil(seconds * rate) + 2;
    this.buf = Array.from({ length: this.size }, () => ({ t: -1, position: new THREE.Vector3(), velocity: new THREE.Vector3(), yaw: 0, hp: 0, state: '' }));
    this.head = 0; this.count = 0; this._last = -Infinity; this.paused = false;
  }
  /** Call every fixed step with the scaled clock. */
  record(now) {
    if (this.paused || now - this._last < 1 / this.rate) return;
    this._last = now;
    const s = this.buf[this.head];
    const c = this.capture();
    s.t = now; s.position.copy(c.position); s.velocity.copy(c.velocity || s.velocity.set(0, 0, 0));
    s.yaw = c.yaw || 0; s.hp = c.hp || 0; s.state = c.state || '';
    this.head = (this.head + 1) % this.size; this.count = Math.min(this.count + 1, this.size);
  }
  clear() { this.count = 0; this.head = 0; this._last = -Infinity; }
  /** Snapshot closest to (now - secondsAgo), clamped to the oldest sample. Returns null when empty. */
  sample(now, secondsAgo) {
    if (this.count === 0) return null;
    const target = now - secondsAgo;
    let best = null, bestD = Infinity;
    for (let i = 0; i < this.count; i++) {
      const s = this.buf[(this.head - 1 - i + this.size) % this.size];
      const d = Math.abs(s.t - target);
      if (d < bestD) { bestD = d; best = s; }
      if (s.t < target) break;
    }
    return best ? { t: best.t, position: best.position.clone(), velocity: best.velocity.clone(), yaw: best.yaw, hp: best.hp, state: best.state } : null;
  }
}
