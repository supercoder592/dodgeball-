// ---------------------------------------------------------------------------------------------------------------
// Screen pulses (owner: render) - pure logic, no three.js. The Renderer's grade pass reads `values` every frame.
//   pulse(type, intensity, duration)  one-shot envelope (fast attack, eased decay) in REAL time
//   setSustained(type, amount)        persistent level (e.g. Danger Sense while a ball is locked on), smoothed
// Each pulse type is a weighted mix of grade channels (exposure, flash, chromatic aberration, ...). The sum of all
// active pulses + sustained levels is clamped per channel so stacked juice never blows the image out.
// ---------------------------------------------------------------------------------------------------------------

/** Grade channels, in the order used by the compiled profiles. */
export const PULSE_CHANNELS = Object.freeze([
  'exposure',   // multiplicative exposure boost (final = 1 + exposure)
  'flash',      // additive warm-white flash (linear HDR units)
  'ca',         // extra chromatic aberration (uv offset at the frame edge)
  'saturation', // saturation delta (final = base + saturation)
  'vignette',   // extra vignette strength
  'danger',     // red pulsing screen edges (Specter's Danger Sense)
  'freeze',     // cold tint + frost edges (Glacier Freeze / Absolute Zero)
  'rewind',     // desaturated, scan-lined rewind look (Time Reversal / Temporal Reset)
  'zoom',       // radial punch-in (fraction of the frame)
]);

const CH = Object.fromEntries(PULSE_CHANNELS.map((c, i) => [c, i]));

/**
 * Channel weights per pulse type at intensity 1. Tuned for a broadcast look: hits read clearly but never wash the
 * frame; the perfect catch is the brightest, most saturated beat; the ultimate is a heavy, wide punch.
 */
export const PULSE_PROFILES = Object.freeze({
  hit:          { flash: 0.10, exposure: 0.05, ca: 0.0035, vignette: 0.12, zoom: 0.004 },
  heavyHit:     { flash: 0.22, exposure: 0.10, ca: 0.0090, vignette: 0.30, saturation: -0.25, zoom: 0.012 },
  perfectCatch: { flash: 0.28, exposure: 0.18, ca: 0.0040, saturation: 0.35, vignette: -0.08, zoom: 0.008 },
  ultimate:     { exposure: 0.12, saturation: 0.30, ca: 0.0070, vignette: 0.35, zoom: 0.015 },
  freeze:       { freeze: 1.0, saturation: -0.20, ca: 0.0020 },
  rewind:       { rewind: 1.0, ca: 0.0060 },
  danger:       { danger: 1.0, vignette: 0.10 },
});

/** Per-channel clamp [min, max] of the summed result. */
export const PULSE_LIMITS = Object.freeze({
  exposure: [-0.5, 1.0], flash: [0, 0.8], ca: [0, 0.025], saturation: [-1, 1], vignette: [-0.3, 0.8],
  danger: [0, 1], freeze: [0, 1], rewind: [0, 1], zoom: [0, 0.05],
});

/** Sustained levels approach their target with this rate (1/s, exponential). */
export const SUSTAIN_RATE = 10;
/** Longest attack of a one-shot pulse (s); short pulses use 20% of their duration. */
export const MAX_ATTACK = 0.035;

// Compile profiles to dense weight arrays once (no per-frame object lookups / allocations).
const COMPILED = {};
for (const [type, prof] of Object.entries(PULSE_PROFILES)) {
  const w = new Float64Array(PULSE_CHANNELS.length);
  for (const [ch, v] of Object.entries(prof)) w[CH[ch]] = v;
  COMPILED[type] = w;
}
const LIMIT_MIN = PULSE_CHANNELS.map((c) => PULSE_LIMITS[c][0]);
const LIMIT_MAX = PULSE_CHANNELS.map((c) => PULSE_LIMITS[c][1]);

/**
 * Envelope of a one-shot pulse: linear attack to 1, then a quadratic ease-out decay to 0 at `duration`.
 * @param {number} age seconds since the pulse started
 * @param {number} duration total seconds
 * @returns {number} 0..1
 */
export function pulseEnvelope(age, duration) {
  if (duration <= 0 || age < 0 || age >= duration) return 0;
  const attack = Math.min(MAX_ATTACK, duration * 0.2);
  if (age < attack) return age / attack;
  const k = 1 - (age - attack) / (duration - attack);
  return k * k;
}

export class ScreenPulses {
  /** @param {number} capacity maximum simultaneous one-shot pulses (oldest weakest is recycled) */
  constructor(capacity = 24) {
    this._slots = [];
    for (let i = 0; i < capacity; i++) this._slots.push({ active: false, weights: null, intensity: 0, duration: 0, age: 0 });
    this._sustainTarget = {};
    this._sustain = {};
    for (const t of Object.keys(PULSE_PROFILES)) { this._sustainTarget[t] = 0; this._sustain[t] = 0; }
    this._acc = new Float64Array(PULSE_CHANNELS.length);
    /** Current clamped channel values (read by the grade pass). */
    this.values = {};
    for (const c of PULSE_CHANNELS) this.values[c] = 0;
  }

  /** True if `type` is a known pulse type. */
  static has(type) { return Object.prototype.hasOwnProperty.call(COMPILED, type); }

  /**
   * Starts a one-shot pulse. Unknown types are ignored (returns false).
   * @param {string} type 'hit'|'heavyHit'|'perfectCatch'|'ultimate'|'freeze'|'rewind'|'danger'
   * @param {number} intensity 0..~2 (scales every channel of the profile)
   * @param {number} duration real seconds
   */
  pulse(type, intensity = 1, duration = 0.3) {
    const weights = COMPILED[type];
    if (!weights || !(duration > 0) || !(intensity > 0)) return false;
    let slot = null, weakest = null, weakestScore = Infinity;
    for (const s of this._slots) {
      if (!s.active) { slot = s; break; }
      const score = s.intensity * pulseEnvelope(s.age, s.duration);
      if (score < weakestScore) { weakestScore = score; weakest = s; }
    }
    slot = slot || weakest;
    slot.active = true; slot.weights = weights; slot.intensity = Math.min(3, intensity); slot.duration = duration; slot.age = 0;
    return true;
  }

  /** Sets a persistent level (0..1) for `type`; it is approached smoothly in update(). */
  setSustained(type, amount) {
    if (!COMPILED[type]) return false;
    this._sustainTarget[type] = Math.max(0, Math.min(1, Number(amount) || 0));
    return true;
  }

  /** Current smoothed sustained level of `type`. */
  sustained(type) { return this._sustain[type] || 0; }

  /** Stops every pulse and sustained effect immediately. */
  clear() {
    for (const s of this._slots) s.active = false;
    for (const t of Object.keys(this._sustain)) { this._sustain[t] = 0; this._sustainTarget[t] = 0; }
    for (const c of PULSE_CHANNELS) this.values[c] = 0;
  }

  /** Number of active one-shot pulses. */
  get activeCount() { let n = 0; for (const s of this._slots) if (s.active) n++; return n; }

  /**
   * Advances every envelope by `realDt` (unscaled seconds: screen juice keeps animating during hitstop) and
   * recomputes `values`.
   */
  update(realDt) {
    const dt = Math.max(0, Math.min(0.25, realDt || 0));
    const acc = this._acc;
    acc.fill(0);
    for (const s of this._slots) {
      if (!s.active) continue;
      s.age += dt;
      const e = pulseEnvelope(s.age, s.duration);
      if (s.age >= s.duration) { s.active = false; continue; }
      const k = e * s.intensity, w = s.weights;
      for (let i = 0; i < w.length; i++) acc[i] += w[i] * k;
    }
    const blend = 1 - Math.exp(-SUSTAIN_RATE * dt);
    for (const type in this._sustain) {
      const cur = this._sustain[type] + (this._sustainTarget[type] - this._sustain[type]) * blend;
      this._sustain[type] = cur < 1e-4 && this._sustainTarget[type] === 0 ? 0 : cur;
      if (this._sustain[type] <= 0) continue;
      const w = COMPILED[type];
      for (let i = 0; i < w.length; i++) acc[i] += w[i] * this._sustain[type];
    }
    for (let i = 0; i < PULSE_CHANNELS.length; i++) {
      this.values[PULSE_CHANNELS[i]] = Math.max(LIMIT_MIN[i], Math.min(LIMIT_MAX[i], acc[i]));
    }
    return this.values;
  }
}
