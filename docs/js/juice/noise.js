// ---------------------------------------------------------------------------------------------------------------
// Small, allocation-free 1D gradient (Perlin) noise + fractal Brownian motion for procedural camera shake.
// Pure JS (no three.js) so it is unit tested by `npm test` (noise.test.js).
//
// Why gradient noise and not Math.random(): random jitter per frame looks like a broken camera (white noise, frame-rate
// dependent). Perlin noise is band-limited and continuous, so sampling it at `realNow * frequency` gives a smooth,
// frame-rate independent wobble whose "busyness" is controlled by one number (frequency, Hz). Layering octaves adds the
// fine high-frequency rattle of a real handheld/impact shake on top of the slow sway.
// ---------------------------------------------------------------------------------------------------------------

/** Table size (power of two so `& MASK` wraps the lattice). */
const SIZE = 256;
const MASK = SIZE - 1;

/**
 * Deterministic 32-bit PRNG (mulberry32) used only to build the lattice tables, so a given seed always produces the
 * same noise curve (camera shake is reproducible with ?seed and in tests).
 * @param {number} seed
 * @returns {() => number} generator of floats in [0, 1)
 */
export function mulberry32(seed) {
  let a = (seed >>> 0) || 0x9e3779b9;
  return () => {
    a = (a + 0x6d2b79f5) | 0;
    let t = Math.imul(a ^ (a >>> 15), 1 | a);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

/** Quintic fade 6t^5 - 15t^4 + 10t^3 (C2 continuous: no visible "kinks" in the shake at lattice points). */
export function fade(t) {
  return t * t * t * (t * (t * 6 - 15) + 10);
}

/**
 * Seeded 1D Perlin gradient noise.
 * - `noise(x)` is continuous, zero at integer lattice points and bounded to [-1, 1].
 * - `fbm(x, octaves, lacunarity, gain)` sums octaves and renormalises by the total amplitude, so the result stays in
 *   [-1, 1] for any octave count (amplitude tuning stays meaningful when designers change octaves).
 */
export class Perlin1D {
  /** @param {number} seed any integer; different seeds give uncorrelated curves (one per shake axis). */
  constructor(seed = 1) {
    const rand = mulberry32(seed);
    // Permutation (doubled to avoid a second wrap) + one gradient per lattice point in [-1, 1].
    this.perm = new Uint8Array(SIZE * 2);
    this.grad = new Float32Array(SIZE);
    const p = new Uint8Array(SIZE);
    for (let i = 0; i < SIZE; i++) p[i] = i;
    for (let i = SIZE - 1; i > 0; i--) { // Fisher-Yates
      const j = Math.floor(rand() * (i + 1));
      const tmp = p[i]; p[i] = p[j]; p[j] = tmp;
    }
    for (let i = 0; i < SIZE * 2; i++) this.perm[i] = p[i & MASK];
    // Gradients avoid |g| < 0.25 so every lattice cell has visible motion (no long "dead" stretches in the shake).
    for (let i = 0; i < SIZE; i++) {
      const g = 0.25 + rand() * 0.75;
      this.grad[i] = rand() < 0.5 ? -g : g;
    }
  }

  /**
   * Single octave of gradient noise.
   * @param {number} x sample coordinate (e.g. seconds * frequency)
   * @returns {number} value in [-1, 1]
   */
  noise(x) {
    const xf = Math.floor(x);
    const i0 = xf & MASK;
    const f = x - xf;
    const g0 = this.grad[this.perm[i0]];
    const g1 = this.grad[this.perm[i0 + 1]];
    // Contributions of the two surrounding lattice gradients, blended with the quintic fade.
    const n0 = g0 * f;
    const n1 = g1 * (f - 1);
    // Max |n| of 1D gradient noise with |g| <= 1 is 0.5 -> scale to [-1, 1].
    return (n0 + (n1 - n0) * fade(f)) * 2;
  }

  /**
   * Fractal Brownian motion: `octaves` layers, each `lacunarity` x the frequency and `gain` x the amplitude of the
   * previous one. Normalised to [-1, 1].
   */
  fbm(x, octaves = 3, lacunarity = 2.0, gain = 0.5) {
    let sum = 0, amp = 1, freq = 1, norm = 0;
    for (let o = 0; o < octaves; o++) {
      // Offset each octave so their lattice zeros do not line up (otherwise every octave is 0 at integer x).
      sum += amp * this.noise(x * freq + o * 17.31);
      norm += amp;
      amp *= gain;
      freq *= lacunarity;
    }
    return norm > 0 ? sum / norm : 0;
  }
}
