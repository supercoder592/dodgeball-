// Cooldown timer and clamped resource meter (ultimate charge).
export class Cooldown {
  constructor(duration = 0) { this.duration = Math.max(0, duration); this.remaining = 0; }
  get ready() { return this.remaining <= 0; }
  /** 1 when just started, 0 when ready. */
  get normalized() { return this.duration <= 0 ? 0 : Math.min(1, Math.max(0, this.remaining / this.duration)); }
  start(duration = this.duration) { this.duration = Math.max(0, duration); this.remaining = this.duration; }
  reset() { this.remaining = 0; }
  reduce(seconds) { this.remaining = Math.max(0, this.remaining - Math.max(0, seconds)); }
  /** Returns true on the tick the cooldown becomes ready. */
  tick(dt) {
    if (this.remaining <= 0) return false;
    this.remaining -= dt;
    if (this.remaining > 0) return false;
    this.remaining = 0;
    return true;
  }
}

export class Meter {
  constructor(max = 1, initial = 0) { this.max = max > 0 ? max : 1; this.value = Math.min(this.max, Math.max(0, initial)); }
  get normalized() { return this.value / this.max; }
  get full() { return this.value >= this.max - 1e-5; }
  /** Adds (or removes) and clamps. Returns { delta, becameFull }. */
  add(amount) {
    const before = this.value, wasFull = this.full;
    this.value = Math.min(this.max, Math.max(0, this.value + amount));
    return { delta: this.value - before, becameFull: !wasFull && this.full };
  }
  tryConsume(amount) {
    if (this.value + 1e-5 < amount) return false;
    this.value = Math.max(0, this.value - amount);
    return true;
  }
  set(v) { this.value = Math.min(this.max, Math.max(0, v)); }
}
