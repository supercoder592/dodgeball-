// ---------------------------------------------------------------------------------------------------------------
// Pure (three-free) math used by the character animation layer (avatar.js) and the ragdoll. Kept separate so it can
// be unit-tested with node:test (see avatarMath.test.js). Units: metres, seconds, radians.
// ---------------------------------------------------------------------------------------------------------------

/** Clamp v to [lo, hi]. */
export function clamp(v, lo, hi) { return v < lo ? lo : v > hi ? hi : v; }

/** Hermite smoothstep of x between e0 and e1 (0..1). */
export function smoothstep(e0, e1, x) {
  const t = clamp((x - e0) / (e1 - e0 || 1e-9), 0, 1);
  return t * t * (3 - 2 * t);
}

/**
 * Frame-rate independent exponential approach ("critically damped lerp").
 * @param {number} current
 * @param {number} target
 * @param {number} rate  1/s (higher = snappier). rate * dt = number of time constants elapsed.
 * @param {number} dt    seconds
 */
export function damp(current, target, rate, dt) {
  if (dt <= 0) return current;
  return current + (target - current) * (1 - Math.exp(-rate * dt));
}

/** Blend factor for damp-style interpolation of vectors/quaternions. */
export function dampFactor(rate, dt) { return dt <= 0 ? 0 : 1 - Math.exp(-rate * dt); }

/** Wrap an angle to [-PI, PI). */
export function wrapAngle(a) {
  a = (a + Math.PI) % (2 * Math.PI);
  if (a < 0) a += 2 * Math.PI;
  return a - Math.PI;
}

/** Fractional part in [0, 1) (works for negative values, used for backwards playback of the gait phase). */
export function frac(x) { return x - Math.floor(x); }

/**
 * Locomotion blend weights from ground speed. The motion-capture clips move at their own "stride speeds" (root motion
 * of walk/run/sprint at 1x playback); we cross-fade between the two clips bracketing the current speed so the feet do
 * not skate, and return the blended stride speed / cycle duration so the caller can compute a matching playback rate.
 * @param {number} speed  planar ground speed (m/s)
 * @param {{walk:{speed:number,duration:number}, run:{speed:number,duration:number}, sprint:{speed:number,duration:number}}} strides
 * @param {number} idleSpeed below this speed the character stands (idle weight 1)
 * @param {object} [out]
 * @returns {{idle:number, walk:number, run:number, sprint:number, strideSpeed:number, cycle:number}}
 */
export function locomotionBlend(speed, strides, idleSpeed = 0.18, out = {}) {
  const w = strides.walk, r = strides.run, s = strides.sprint;
  out.idle = 0; out.walk = 0; out.run = 0; out.sprint = 0;
  if (speed <= idleSpeed) {
    out.idle = 1;
  } else if (speed < w.speed) {
    const t = (speed - idleSpeed) / Math.max(1e-6, w.speed - idleSpeed);
    out.idle = 1 - t; out.walk = t;
  } else if (speed < r.speed) {
    const t = (speed - w.speed) / Math.max(1e-6, r.speed - w.speed);
    out.walk = 1 - t; out.run = t;
  } else if (speed < s.speed) {
    const t = (speed - r.speed) / Math.max(1e-6, s.speed - r.speed);
    out.run = 1 - t; out.sprint = t;
  } else {
    out.sprint = 1;
  }
  // Blended stride speed / cycle duration of the moving part of the blend (idle excluded).
  const moving = out.walk + out.run + out.sprint;
  if (moving > 1e-6) {
    out.strideSpeed = (out.walk * w.speed + out.run * r.speed + out.sprint * s.speed) / moving;
    out.cycle = (out.walk * w.duration + out.run * r.duration + out.sprint * s.duration) / moving;
  } else {
    out.strideSpeed = w.speed;
    out.cycle = w.duration;
  }
  return out;
}

/**
 * Playback rate that makes the blended gait cover `speed` metres per second (feet planted), clamped so extreme
 * speeds do not produce comically fast or slow legs.
 */
export function gaitRate(speed, strideSpeed, minRate = 0.55, maxRate = 1.55) {
  if (strideSpeed <= 1e-6) return 1;
  return clamp(speed / strideSpeed, minRate, maxRate);
}

/**
 * Body lean from planar acceleration (athletes lean INTO the acceleration: tan(theta) = a / g).
 * Forward acceleration pitches the body forward (+pitch), acceleration toward the character's left rolls left (-roll).
 * @returns {{pitch:number, roll:number}} radians, pitch about the lateral axis (+ = forward), roll about the forward axis (+ = right)
 */
export function leanFromAcceleration(aForward, aLeft, g, pitchGain, rollGain, maxPitch, maxRoll, out = {}) {
  out.pitch = clamp(Math.atan((aForward * pitchGain) / g), -maxPitch, maxPitch);
  out.roll = clamp(-Math.atan((aLeft * rollGain) / g), -maxRoll, maxRoll);
  return out;
}

/**
 * One semi-implicit Euler step of a damped spring x'' = -w^2 x - 2 z w x' (used for additive hit flinches).
 * Mutates and returns `s` ({x, v}).
 */
export function springStep(s, omega, zeta, dt) {
  if (dt <= 0) return s;
  // Sub-step for stability at low frame rates.
  const n = Math.max(1, Math.ceil(dt / (1 / 120)));
  const h = dt / n;
  for (let i = 0; i < n; i++) {
    const a = -omega * omega * s.x - 2 * zeta * omega * s.v;
    s.v += a * h;
    s.x += s.v * h;
  }
  return s;
}

/**
 * Interior elbow/knee angle (radians, PI = straight) that places the end effector at distance d from the root of a
 * two-bone chain with segment lengths a and b (law of cosines). d is clamped to the reachable range.
 */
export function twoBoneInteriorAngle(a, b, d) {
  const dd = clamp(d, Math.abs(a - b) + 1e-6, a + b - 1e-6);
  return Math.acos(clamp((a * a + b * b - dd * dd) / (2 * a * b), -1, 1));
}

/**
 * Split the direction of travel (relative to where the character faces) into a leg yaw so the gait clips (all
 * forward motions) can express strafing and back-pedalling: the pelvis turns toward the travel direction (legs run
 * that way, the spine counter-rotates to keep the chest on target) and beyond `backStart` the gait plays backwards.
 * Hysteresis is supplied by the caller through `wasBackwards`.
 * @param {number} moveAngle signed angle of the velocity relative to facing (0 = forward, + = toward the left)
 * @returns {{legYaw:number, backwards:boolean}}
 */
export function strafeLegYaw(moveAngle, maxLegYaw, backMaxLegYaw, backStart, backEnd, wasBackwards, out = {}) {
  const abs = Math.abs(moveAngle);
  const backwards = wasBackwards ? abs > backEnd : abs > backStart;
  out.backwards = backwards;
  if (!backwards) out.legYaw = clamp(moveAngle, -maxLegYaw, maxLegYaw);
  else out.legYaw = clamp(wrapAngle(moveAngle - Math.PI), -backMaxLegYaw, backMaxLegYaw);
  return out;
}

/** One-shot clip envelope: fade in over fadeIn, hold, fade out over fadeOut before `duration`. */
export function oneShotEnvelope(t, duration, fadeIn, fadeOut) {
  if (t < 0 || t >= duration) return 0;
  const a = fadeIn > 0 ? Math.min(1, t / fadeIn) : 1;
  const b = fadeOut > 0 ? Math.min(1, (duration - t) / fadeOut) : 1;
  return Math.min(a, b);
}

/**
 * Normalises a weight table in place so the weights sum to 1 (three.js mixes the remainder of a sum < 1 with the
 * bind pose, which would make the character sag toward the A-pose). Returns the original sum.
 * @param {Record<string, number>} w
 * @param {string[]} keys
 */
export function normalizeWeights(w, keys) {
  let sum = 0;
  for (const k of keys) sum += w[k] || 0;
  if (sum > 1e-6) for (const k of keys) w[k] = (w[k] || 0) / sum;
  return sum;
}

/**
 * Effective ragdoll impulse: incoming impulses have wildly different magnitudes (ball momentum, ability knockback),
 * so we map them to a plausible whole-body velocity change (m/s) - gain * |J| / mass, clamped.
 */
export function ragdollDeltaV(impulseMagnitude, totalMass, gain, minDv, maxDv) {
  if (!(impulseMagnitude > 0)) return minDv;
  return clamp((impulseMagnitude / Math.max(1e-3, totalMass)) * gain, minDv, maxDv);
}
