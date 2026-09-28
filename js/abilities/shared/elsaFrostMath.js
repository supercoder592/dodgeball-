// ---------------------------------------------------------------------------------------------------------------
// Pure math for Elsa's frost effects (Frost Trail patches, Absolute Zero field envelope, procedural frost textures).
// NO three.js import so it is unit tested with node:test (elsaFrostMath.test.js).
// Yaw convention: yaw 0 faces +Z; a direction (dx, dz) has yaw atan2(dx, dz).
// ---------------------------------------------------------------------------------------------------------------

/**
 * Walks along the ground track of a moving ball and reports evenly spaced points (every `spacing` metres), independent
 * of frame rate or ball speed: a 220 km/h ball covers ~1 m per frame, so spawning "once per frame" would leave gaps.
 */
export class TrailStepper {
  /**
   * @param {number} spacing metres between two emitted points
   * @param {number} maxPoints hard cap of points for one trail (performance guard)
   */
  constructor(spacing = 0.9, maxPoints = 48) {
    this.spacing = Math.max(0.05, spacing);
    this.maxPoints = maxPoints;
    this.lastX = 0; this.lastZ = 0;
    this.toNext = 0;
    this.emitted = 0;
  }

  /** Starts a new trail at (x, z); the first point is emitted after `firstOffset` metres of travel. */
  reset(x, z, firstOffset = this.spacing * 0.5) {
    this.lastX = x; this.lastZ = z;
    this.toNext = Math.max(0, firstOffset);
    this.emitted = 0;
  }

  get exhausted() { return this.emitted >= this.maxPoints; }

  /**
   * Advances the track to (x, z). For every spacing boundary crossed calls emit(px, pz, yaw).
   * @param {number} x
   * @param {number} z
   * @param {(px:number, pz:number, yaw:number) => void} emit
   * @param {number} maxThisCall cap on points emitted by this call (per-frame guard)
   * @returns {number} points emitted
   */
  advance(x, z, emit, maxThisCall = 8) {
    const dx = x - this.lastX, dz = z - this.lastZ;
    const len = Math.sqrt(dx * dx + dz * dz);
    if (len < 1e-6) return 0;
    const ux = dx / len, uz = dz / len;
    const yaw = Math.atan2(dx, dz);
    let travelled = 0, n = 0;
    while (this.toNext <= len - travelled && n < maxThisCall && this.emitted < this.maxPoints) {
      travelled += this.toNext;
      emit(this.lastX + ux * travelled, this.lastZ + uz * travelled, yaw);
      this.emitted++;
      n++;
      this.toNext = this.spacing;
    }
    if (n >= maxThisCall && this.toNext <= len - travelled) {
      // Per-call cap hit: skip ahead instead of accumulating a backlog (the gap is invisible at that speed).
      this.toNext = this.spacing;
    } else {
      this.toNext -= len - travelled;
    }
    this.lastX = x; this.lastZ = z;
    return n;
  }
}

/**
 * True when planar point (px, pz) lies on an oriented rectangle centred at (cx, cz) with yaw `yaw`,
 * half width `halfW` (across travel) and half length `halfL` (along travel), grown by `margin`.
 */
export function pointInPatch(px, pz, cx, cz, yaw, halfW, halfL, margin = 0) {
  const rx = px - cx, rz = pz - cz;
  const s = Math.sin(yaw), c = Math.cos(yaw);
  const along = rx * s + rz * c;       // local +Z (travel direction)
  const across = rx * c - rz * s;      // local +X
  return Math.abs(across) <= halfW + margin && Math.abs(along) <= halfL + margin;
}

/**
 * Size envelope of an ice patch: forms over `growTime`, holds, melts (shrinks) over the last `meltTime`.
 * @returns {number} 0..1 (0 = gone)
 */
export function patchEnvelope(age, life, growTime = 0.12, meltTime = 0.7) {
  if (age < 0 || age >= life) return 0;
  const grow = growTime > 0 ? Math.min(1, age / growTime) : 1;
  const melt = meltTime > 0 ? Math.min(1, (life - age) / meltTime) : 1;
  // Ease-out growth (ice "flashes" onto the floor), ease-in melt.
  const g = 1 - (1 - grow) * (1 - grow);
  const m = melt * melt * (3 - 2 * melt);
  return Math.max(0, Math.min(g, m));
}

/**
 * Absolute Zero field envelope at `elapsed` seconds of a `duration` window.
 * spread: 0..1 how far the frost has crept from the centre line toward the baseline (0..spreadTime).
 * alpha:  0..1 visual opacity (fades in with the creep, thaws over fadeTime after the window).
 * Writes into `out` and returns it; out.done is true once the thaw finished.
 */
export function fieldEnvelope(elapsed, duration, spreadTime, fadeTime, out = { spread: 0, alpha: 0, done: false }) {
  const t = Math.max(0, elapsed);
  const sp = spreadTime > 0 ? Math.min(1, t / spreadTime) : 1;
  out.spread = 1 - (1 - sp) * (1 - sp) * (1 - sp); // ease-out cubic: fast crack across the floor, then settles
  let a = Math.min(1, sp * 1.6);
  if (t > duration) a = fadeTime > 0 ? Math.max(0, 1 - (t - duration) / fadeTime) : 0;
  out.alpha = a;
  out.done = t >= duration + Math.max(0, fadeTime);
  return out;
}

// ------------------------------------------------------------------ procedural noise (tileable) for frost textures

/** Integer hash -> [0, 1). */
export function hash2(ix, iy, seed = 0) {
  let h = Math.imul(ix | 0, 374761393) ^ Math.imul(iy | 0, 668265263) ^ Math.imul(seed | 0, 1442695041);
  h = Math.imul(h ^ (h >>> 13), 1274126177);
  h ^= h >>> 16;
  return (h >>> 0) / 4294967296;
}

const wrap = (i, p) => ((i % p) + p) % p;

/** Smooth value noise, periodic with integer `period` (so textures tile seamlessly). */
export function valueNoise(x, y, period, seed = 0) {
  const x0 = Math.floor(x), y0 = Math.floor(y);
  const fx = x - x0, fy = y - y0;
  const ux = fx * fx * (3 - 2 * fx), uy = fy * fy * (3 - 2 * fy);
  const ax = wrap(x0, period), bx = wrap(x0 + 1, period);
  const ay = wrap(y0, period), by = wrap(y0 + 1, period);
  const a = hash2(ax, ay, seed), b = hash2(bx, ay, seed), c = hash2(ax, by, seed), d = hash2(bx, by, seed);
  return a + (b - a) * ux + (c - a) * uy + (a - b - c + d) * ux * uy;
}

/** Fractal Brownian motion of periodic value noise; u, v in [0, 1). Result ~[0, 1]. */
export function fbm(u, v, baseFreq, octaves, seed = 0) {
  let sum = 0, amp = 0.5, norm = 0, f = baseFreq;
  for (let o = 0; o < octaves; o++) {
    sum += amp * valueNoise(u * f, v * f, f, seed + o * 17);
    norm += amp; amp *= 0.5; f *= 2;
  }
  return sum / norm;
}
