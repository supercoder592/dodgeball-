// ---------------------------------------------------------------------------------------------------------------
// Pure scalar helpers for the player module (motor, health, status). No three.js import so they can be unit tested
// with `node --test` (see player_math.test.js). Units: metres, seconds, radians.
// ---------------------------------------------------------------------------------------------------------------

/** @param {number} v @param {number} lo @param {number} hi */
export const clamp = (v, lo, hi) => (v < lo ? lo : v > hi ? hi : v);
/** @param {number} v */
export const clamp01 = (v) => (v < 0 ? 0 : v > 1 ? 1 : v);
/** @param {number} a @param {number} b @param {number} t */
export const lerp = (a, b, t) => a + (b - a) * t;

/** Moves `current` toward `target` by at most `maxDelta` (never overshoots). */
export function moveTowards(current, target, maxDelta) {
  const d = target - current;
  if (Math.abs(d) <= maxDelta) return target;
  return current + Math.sign(d) * maxDelta;
}

/** Wraps an angle to (-PI, PI]. */
export function wrapAngle(a) {
  if (!Number.isFinite(a)) return 0;
  const TWO_PI = Math.PI * 2;
  let r = a % TWO_PI;
  if (r <= -Math.PI) r += TWO_PI;
  else if (r > Math.PI) r -= TWO_PI;
  return r;
}

/** Shortest signed difference `to - from` in (-PI, PI]. */
export const deltaAngle = (from, to) => wrapAngle(to - from);

/** Rotates the angle `current` toward `target` along the shortest arc by at most `maxDelta` radians. */
export function moveTowardsAngle(current, target, maxDelta) {
  const d = deltaAngle(current, target);
  if (Math.abs(d) <= maxDelta) return wrapAngle(target);
  return wrapAngle(current + Math.sign(d) * maxDelta);
}

/**
 * Acceleration curve (multiplier on the profile's acceleration) as a function of normalised speed
 * n = speed / topSpeed: strong push from a standstill (`start`), tapering toward `end` near top speed. The taper follows
 * n^exponent so the first metres feel explosive while the last km/h take a moment (sprinter-like build-up).
 */
export function accelerationCurve(n, start = 1.3, end = 0.4, exponent = 2) {
  const x = clamp01(n);
  return Math.max(0.05, start + (end - start) * Math.pow(x, exponent));
}

/** Take-off speed for a jump apex of `height` metres under `gravity` (m/s^2): v = sqrt(2 g h). */
export function jumpSpeed(gravity, height) {
  return Math.sqrt(2 * Math.max(0, gravity) * Math.max(0, height));
}

/** Airborne time of a jump (up and back to the same height): t = 2 v / g. */
export function jumpAirTime(gravity, height) {
  return gravity > 1e-6 ? (2 * jumpSpeed(gravity, height)) / gravity : 0;
}

/**
 * Slide entry speed with momentum preservation: max(current planar speed, walk speed * speedMul) * slideBoost.
 * A sprinting player therefore slides faster than a walking one; a standing one still gets a usable dodge.
 */
export function slideStartSpeed(currentSpeed, walkSpeed, speedMul, boost) {
  return Math.max(Math.max(0, currentSpeed), Math.max(0, walkSpeed) * Math.max(0, speedMul)) * Math.max(0, boost);
}

/**
 * One step of slide friction loss. On low traction (ice) friction is scaled toward `iceScale` so slides go much
 * further. Returns the new speed (never negative).
 */
export function slideFrictionStep(speed, friction, traction, iceScale, dt) {
  const f = Math.max(0, friction) * lerp(clamp01(iceScale), 1, clamp01(traction));
  return Math.max(0, speed - f * dt);
}

/**
 * Movement control left after a planar velocity change (knockback) of `planarImpulse` m/s: the harder the hit the less
 * control, down to `minControl` at `fullLossSpeed`. Never raises the current control.
 */
export function knockbackControl(currentControl, planarImpulse, fullLossSpeed, minControl) {
  const loss = clamp01(Math.max(0, planarImpulse) / Math.max(1e-3, fullLossSpeed));
  return Math.min(currentControl, lerp(1, clamp01(minControl), loss));
}

/**
 * Status movement multiplier: (1 - slow) * (1 + haste). The strongest slow and the strongest haste apply (effects of the
 * same type do not stack). Clamped so a slow never becomes a root and a haste never more than doubles speed.
 */
export function statusSpeedMultiplier(slow, haste, maxSlow = 0.9, maxHaste = 1) {
  const s = clamp(slow || 0, 0, maxSlow);
  const h = clamp(haste || 0, 0, maxHaste);
  return (1 - s) * (1 + h);
}

/**
 * Keeps one axis inside [min, max]. Operates on `state = { p, v }` in place (no allocation):
 * outside -> snap back and remove the outward velocity; inside -> clip the velocity so the next step cannot cross.
 * Returns true when the position was corrected.
 */
export function clampAxis(state, min, max, dt) {
  if (min > max) { const mid = (min + max) * 0.5; min = mid; max = mid; }
  if (state.p < min) {
    state.p = min;
    if (state.v < 0) state.v = 0;
    return true;
  }
  if (state.p > max) {
    state.p = max;
    if (state.v > 0) state.v = 0;
    return true;
  }
  if (dt > 0) {
    const next = state.p + state.v * dt;
    if (next < min) state.v = (min - state.p) / dt;
    else if (next > max) state.v = (max - state.p) / dt;
  }
  return false;
}

/**
 * Ragdoll impulse magnitude (N*s) from a ball hit: real momentum (m * v) times a readability factor plus an upward
 * share, capped. Returns { planarScale, up } where the impulse = velocity * planarScale + UP * up (keeps callers
 * allocation-free).
 */
export function ragdollImpulseParts(ballSpeed, ballMass, factor, upwardBias, maxImpulse) {
  const base = Math.max(0, ballSpeed) * Math.max(0, ballMass) * Math.max(0, factor);
  const up = base * clamp01(upwardBias);
  const total = Math.hypot(base, up);
  const k = total > maxImpulse && total > 1e-6 ? maxImpulse / total : 1;
  return { planarScale: ballSpeed > 1e-6 ? (base * k) / ballSpeed : 0, up: up * k };
}
