// ---------------------------------------------------------------------------------------------------------------
// Pure juice math (no three.js): speed -> intensity curve, dynamic hitstop, shake falloff, envelopes and the analytic
// damped spring used by squash & stretch. Unit tested in juiceMath.test.js; consumed by juice.js and cameraRig.js.
// ---------------------------------------------------------------------------------------------------------------

/** Clamp `v` to [lo, hi]. */
export const clamp = (v, lo, hi) => (v < lo ? lo : v > hi ? hi : v);
/** Clamp to [0, 1]. */
export const clamp01 = (v) => (v < 0 ? 0 : v > 1 ? 1 : v);
/** Linear interpolation. */
export const lerp = (a, b, t) => a + (b - a) * t;
/** Inverse lerp, unclamped (0 at a, 1 at b). */
export const inverseLerp = (a, b, v) => (b === a ? 0 : (v - a) / (b - a));
/** Hermite smoothstep on [0, 1]. */
export const smoothstep01 = (t) => { const x = clamp01(t); return x * x * (3 - 2 * x); };
/** Perlin smootherstep on [0, 1] (zero 1st and 2nd derivative at both ends - used for camera transitions). */
export const smootherstep01 = (t) => { const x = clamp01(t); return x * x * x * (x * (x * 6 - 15) + 10); };

/**
 * Frame-rate independent exponential smoothing factor: `current += (target - current) * damp(k, dt)`.
 * `k` is the convergence rate in 1/s (63% of the remaining distance is covered every 1/k seconds).
 */
export const dampFactor = (k, dt) => 1 - Math.exp(-k * dt);

/**
 * Ball speed (km/h) -> juice intensity (0..1). Linear position between `minKmh` and `maxKmh`, remapped by an
 * ease-in-out (smoothstep) that starts at `floor` so even a lob gets a readable minimum of feedback.
 * @param {number} speedKmh
 * @param {number} minKmh speed at which intensity == floor (spec: 40 km/h)
 * @param {number} maxKmh speed at which intensity == 1 (spec: the 220 km/h Rally cap)
 * @param {number} floor intensity at or below minKmh
 */
export function intensityForSpeed(speedKmh, minKmh, maxKmh, floor = 0.25) {
  if (!(speedKmh > 0)) return floor;
  const t = clamp01(inverseLerp(minKmh, maxKmh, speedKmh));
  return floor + (1 - floor) * smoothstep01(t);
}

/**
 * Dynamic hitstop duration: lerp(min, max, intensity), x `eliminationMul` when the hit eliminated, clamped to the
 * spec window [min, max] (0.03..0.1 s) so stacked multipliers never exceed the design budget.
 */
export function hitstopDuration(intensity, min, max, eliminated = false, eliminationMul = 1.25) {
  let d = lerp(min, max, clamp01(intensity));
  if (eliminated) d *= eliminationMul;
  return clamp(d, min, max);
}

/**
 * Distance attenuation for shakes of events that do not involve the local player: 1 inside `near`, smoothly down to
 * `floor` at `far` and beyond (cross-court hits stay faintly perceptible instead of vanishing).
 */
export function distanceFalloff(distance, near, far, floor = 0) {
  if (!(distance > near)) return 1;
  if (distance >= far) return floor;
  return lerp(1, floor, smoothstep01(inverseLerp(near, far, distance)));
}

/**
 * Envelope of an explicit timed shake: full at t = 0, eases to 0 at t = duration ((1 - t/d)^2 - fast initial decay
 * like a real impact, long soft tail).
 */
export function shakeEnvelope(t, duration) {
  if (!(duration > 0) || t >= duration) return 0;
  const k = 1 - Math.max(0, t) / duration;
  return k * k;
}

/**
 * FOV kick envelope: quick linear attack over `attack` (fraction of duration), then quadratic ease back to 0.
 * Returns the fraction of the kick applied at time t.
 */
export function kickEnvelope(t, duration, attack = 0.15) {
  if (!(duration > 0) || t >= duration || t < 0) return 0;
  const a = Math.max(1e-4, attack * duration);
  if (t < a) return t / a;
  const k = 1 - (t - a) / (duration - a);
  return k * k;
}

/**
 * Damped harmonic oscillator released from x = 0 with initial velocity v0 (an "impact"):
 *   x(t) = (v0 / wd) * e^(-zeta w t) * sin(wd t),   wd = w * sqrt(1 - zeta^2)
 * Closed form = unconditionally stable at any frame time (realDt may be clamped to 0.1 s).
 * @param {number} t seconds since release
 * @param {number} v0 initial velocity
 * @param {number} omega natural angular frequency w (rad/s)
 * @param {number} zeta damping ratio (0 < zeta < 1: under-damped -> squash, then overshoot into stretch)
 */
export function dampedImpulse(t, v0, omega, zeta) {
  if (t <= 0) return 0;
  const z = clamp(zeta, 0.01, 0.99);
  const wd = omega * Math.sqrt(1 - z * z);
  return (v0 / wd) * Math.exp(-z * omega * t) * Math.sin(wd * t);
}

/**
 * Peak |x| of `dampedImpulse` per unit (v0 / omega). Used to pick v0 so the first compression peak equals the
 * requested squash exactly: v0 = peak * omega / impulsePeakFactor(zeta).
 */
export function impulsePeakFactor(zeta) {
  const z = clamp(zeta, 0.01, 0.99);
  const s = Math.sqrt(1 - z * z);
  return Math.exp((-z / s) * Math.atan2(s, z));
}

/**
 * Natural frequency so the oscillation envelope decays to `residual` (e.g. 2%) after `duration` seconds:
 * e^(-zeta w d) = residual  ->  w = -ln(residual) / (zeta d).
 */
export function omegaForSettle(duration, zeta, residual = 0.02) {
  const z = clamp(zeta, 0.01, 0.99);
  return -Math.log(clamp(residual, 1e-4, 0.5)) / (z * Math.max(1e-3, duration));
}

/**
 * Volume-preserving squash scale for compression s along the axis: axis scale (1 - s), perpendicular scale
 * 1/sqrt(1 - s) so that axis * perp^2 == 1 (the rubber ball keeps its volume). Negative s stretches.
 * Writes into `out` = { axis, perp } and returns it.
 */
export function squashScales(s, out = { axis: 1, perp: 1 }) {
  const a = Math.max(0.05, 1 - s);
  out.axis = a;
  out.perp = 1 / Math.sqrt(a);
  return out;
}
