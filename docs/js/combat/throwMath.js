// ---------------------------------------------------------------------------------------------------------------
// Throw math (pure, no three.js - unit tested by throwSolver.test.js).
//
//   charge curve      seconds held -> 0..1 (ease-out: most of the power arrives early, the last bit rewards patience)
//   speed             V = V_base * speedMul * (1 + 0.10 * rally), capped at 220 km/h   (core/rules.js RallyMath)
//   launch solution   low-arc ballistic elevation through a point (core/rules.js Ballistics) -> 3D velocity
//   lead              iterative intercept of a target moving at constant planar velocity
//   trajectory        closed-form positions and first time within a radius of a point
//   knockback         body shove (m/s) scaled by ball speed
// Vectors are any {x, y, z}; results are written into caller-provided plain objects (allocation-free).
// ---------------------------------------------------------------------------------------------------------------
import { RallyMath, Ballistics } from '../core/rules.js';
import { MAX_BALL_SPEED_MS, MS_TO_KMH } from '../core/constants.js';

/** Tuning of the pure throw helpers. */
export const THROW_MATH = Object.freeze({
  /** Longest lead (s) we extrapolate a target's motion - beyond that a juke makes any prediction meaningless. */
  maxLeadTime: 1.25,
  /** Fixed-point iterations of the intercept solver (converges in 3-4 for ball speeds >> player speeds). */
  leadIterations: 5,
  /** Knockback (m/s of body velocity change) at / below knockbackMinKmh and at / above knockbackMaxKmh. */
  knockbackMin: 2.0,
  knockbackMax: 4.5,
  knockbackMinKmh: 40,
  knockbackMaxKmh: 180,
});

export const clamp01 = (x) => (x < 0 ? 0 : x > 1 ? 1 : x);

/**
 * Charge (0..1) after holding for `seconds`; reaches 1 at `fullTime`. Ease-out quadratic.
 * @param {number} seconds
 * @param {number} fullTime
 */
export function chargeCurve(seconds, fullTime) {
  if (!(fullTime > 0)) return 1;
  const x = clamp01(seconds / fullTime);
  return 1 - (1 - x) * (1 - x);
}

/** Inverse of chargeCurve: seconds of hold that produce `charge`. */
export function chargeSecondsFor(charge, fullTime) {
  const c = clamp01(charge);
  return (1 - Math.sqrt(1 - c)) * (fullTime > 0 ? fullTime : 0);
}

/** Speed multiplier of a charge: minChargeMul at 0 .. 1 at full charge. */
export function chargeSpeedMul(charge, minChargeMul) {
  const m = Number.isFinite(minChargeMul) ? minChargeMul : 1;
  return m + (1 - m) * clamp01(charge);
}

/**
 * Rally-boosted, capped release speed (m/s): RallyMath.computeSpeed(base * speedMul, rally).
 * @param {number} baseSpeed m/s
 * @param {number} speedMul product of charge / counter / modifier multipliers
 * @param {number} rallyCount consecutive catch-and-rethrows without touching the floor
 */
export function computeFinalSpeed(baseSpeed, speedMul = 1, rallyCount = 0, cap = MAX_BALL_SPEED_MS) {
  const mul = Number.isFinite(speedMul) ? Math.max(0, speedMul) : 1;
  return RallyMath.computeSpeed(baseSpeed * mul, rallyCount, cap);
}

/**
 * Launch velocity from `o` through point `t` at `speed` under gravity `g` (low arc).
 * out = { x, y, z (velocity), time (s to reach the point's horizontal distance), ok (reachable) }.
 * Out of range: the 45-degree maximum-range elevation toward the point (ok = false).
 */
export function solveLaunch(o, t, speed, g, out) {
  const dx = t.x - o.x, dy = t.y - o.y, dz = t.z - o.z;
  const h = Math.hypot(dx, dz);
  if (!(speed > 0)) { out.x = 0; out.y = 0; out.z = 0; out.time = Infinity; out.ok = false; return out; }
  if (h < 1e-4) {
    // Straight up / down.
    out.x = 0; out.z = 0; out.y = dy >= 0 ? speed : -speed;
    out.time = Math.abs(dy) / speed; out.ok = true;
    return out;
  }
  const sol = Ballistics.solveAngle(speed, h, dy, g, true);
  const a = sol.angle;
  const c = Math.cos(a), s = Math.sin(a);
  out.x = (dx / h) * c * speed;
  out.z = (dz / h) * c * speed;
  out.y = s * speed;
  out.time = c > 1e-5 ? h / (c * speed) : Infinity;
  out.ok = sol.ok;
  return out;
}

const _lead = { x: 0, y: 0, z: 0, time: 0, ok: false };
const _aim = { x: 0, y: 0, z: 0 };

/**
 * Intercept a target at `tp` moving with planar velocity `tv` (vertical motion ignored - jumps are not led).
 * out = { x, y, z (velocity), time, ok, px, py, pz (aim point) }.
 */
export function leadTarget(o, tp, tv, speed, g, out, maxLead = THROW_MATH.maxLeadTime, iterations = THROW_MATH.leadIterations) {
  const vx = tv ? tv.x || 0 : 0, vz = tv ? tv.z || 0 : 0;
  const aim = _aim;
  aim.x = tp.x; aim.y = tp.y; aim.z = tp.z;
  let time = 0;
  for (let i = 0; i < iterations; i++) {
    solveLaunch(o, aim, speed, g, _lead);
    const next = Math.min(Number.isFinite(_lead.time) ? _lead.time : maxLead, maxLead);
    aim.x = tp.x + vx * next;
    aim.z = tp.z + vz * next;
    if (Math.abs(next - time) < 1e-4) { time = next; break; }
    time = next;
  }
  solveLaunch(o, aim, speed, g, out);
  out.px = aim.x; out.py = aim.y; out.pz = aim.z;
  return out;
}

/** Ballistic position p + v t - 1/2 g t^2 (y up) into out. */
export function positionAt(p, v, g, t, out) {
  out.x = p.x + v.x * t;
  out.y = p.y + v.y * t - 0.5 * g * t * t;
  out.z = p.z + v.z * t;
  return out;
}

const _pa = { x: 0, y: 0, z: 0 };

/** Squared distance between the ballistic point at time t and q. */
function d2At(p, v, g, q, t) {
  positionAt(p, v, g, t, _pa);
  const dx = _pa.x - q.x, dy = _pa.y - q.y, dz = _pa.z - q.z;
  return dx * dx + dy * dy + dz * dz;
}

/**
 * First time (s) at which a ballistic point (p, v, g) comes within `radius` of `q`, or -1 within `maxTime`.
 * Coarse march + bisection refinement (the distance is not monotonic under gravity, so no closed form).
 */
export function firstTimeWithin(p, v, g, q, radius, maxTime, step = 1 / 120) {
  const r2 = radius * radius;
  if (d2At(p, v, g, q, 0) <= r2) return 0;
  let prev = 0;
  for (let t = step; t <= maxTime + 1e-9; t += step) {
    if (d2At(p, v, g, q, t) <= r2) {
      let lo = prev, hi = t;
      for (let i = 0; i < 12; i++) {
        const mid = 0.5 * (lo + hi);
        if (d2At(p, v, g, q, mid) <= r2) hi = mid; else lo = mid;
      }
      return hi;
    }
    prev = t;
  }
  return -1;
}

/** Body knockback (m/s) for a ball arriving at `speedMs`: lerp between THROW_MATH.knockbackMin..Max over 40..180 km/h. */
export function knockbackForSpeed(speedMs) {
  const k = THROW_MATH;
  const x = clamp01((speedMs * MS_TO_KMH - k.knockbackMinKmh) / (k.knockbackMaxKmh - k.knockbackMinKmh));
  return k.knockbackMin + (k.knockbackMax - k.knockbackMin) * x;
}

/** Unsigned planar angle (radians) between (ax, az) and (bx, bz); PI when either is ~zero. */
export function planarAngle(ax, az, bx, bz) {
  const la = Math.hypot(ax, az), lb = Math.hypot(bx, bz);
  if (la < 1e-6 || lb < 1e-6) return Math.PI;
  const c = (ax * bx + az * bz) / (la * lb);
  return Math.acos(c < -1 ? -1 : c > 1 ? 1 : c);
}

/** Signed planar angle (radians) from (ax, az) to (bx, bz); positive = clockwise seen from above (to the right). */
export function signedPlanarAngle(ax, az, bx, bz) {
  // With yaw 0 facing +Z and "right" = -X, turning right means rotating +Z toward -X.
  return Math.atan2(ax * bz - az * bx, ax * bx + az * bz);
}
