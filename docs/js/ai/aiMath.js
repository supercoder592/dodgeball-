// ---------------------------------------------------------------------------------------------------------------
// Pure AI math (no three.js, no game state): works on any {x, y, z} objects (THREE.Vector3 included) and on plain
// planar bounds { minX, maxX, minZ, maxZ } (world/court.js). Allocation-free: results are written into `out`
// arguments. Unit tested by aiMath.test.js (node:test).
// Units: metres, seconds, degrees only where the name says so. World up = +Y, planar = XZ.
// ---------------------------------------------------------------------------------------------------------------

export const DEG2RAD = Math.PI / 180;
export const RAD2DEG = 180 / Math.PI;

export const clamp = (v, lo, hi) => (v < lo ? lo : v > hi ? hi : v);
export const clamp01 = (v) => (v < 0 ? 0 : v > 1 ? 1 : v);
export const lerp = (a, b, t) => a + (b - a) * t;
/** 0..1 position of v between a and b (clamped); 0 when a == b. */
export const inverseLerp = (a, b, v) => (Math.abs(b - a) < 1e-9 ? 0 : clamp01((v - a) / (b - a)));
export function moveTowards(current, target, maxDelta) {
  if (Math.abs(target - current) <= maxDelta) return target;
  return current + Math.sign(target - current) * maxDelta;
}

// ------------------------------------------------------------------ randomness (seeded Rng from core/rng.js)

/** value * (1 +/- frac), uniformly. */
export function jitter(rng, value, frac) { return value * (1 + (rng.next() * 2 - 1) * frac); }
/** -1 or +1. */
export function randomSign(rng) { return rng.next() < 0.5 ? -1 : 1; }

function hash1(n) {
  const s = Math.sin(n * 127.1 + 311.7) * 43758.5453123;
  return s - Math.floor(s);
}
/**
 * 1D gradient (Perlin) noise in [-1, 1], smooth (C2 fade) - used for human-like positional wander.
 * @param {number} seed per-bot offset
 * @param {number} x    time * frequency
 */
export function perlin1D(seed, x) {
  const i = Math.floor(x), f = x - i;
  const g0 = hash1(i + seed * 91.7) * 2 - 1;
  const g1 = hash1(i + 1 + seed * 91.7) * 2 - 1;
  const n0 = g0 * f, n1 = g1 * (f - 1);
  const u = f * f * f * (f * (f * 6 - 15) + 10);
  return clamp((n0 + (n1 - n0) * u) * 2, -1, 1);
}

// ------------------------------------------------------------------ planar geometry

export function planarDistance(a, b) { return Math.hypot(a.x - b.x, a.z - b.z); }
export function planarDistanceSq(a, b) { const dx = a.x - b.x, dz = a.z - b.z; return dx * dx + dz * dz; }

/** Unsigned planar angle (deg) between two planar directions (need not be normalised). */
export function planarAngleDeg(ax, az, bx, bz) {
  const la = Math.hypot(ax, az), lb = Math.hypot(bx, bz);
  if (la < 1e-9 || lb < 1e-9) return 0;
  return Math.acos(clamp((ax * bx + az * bz) / (la * lb), -1, 1)) * RAD2DEG;
}

/** Copies `b` shrunk by `margin` on every side into `out` (collapses to the centre when too small). */
export function shrinkBounds(b, margin, out) {
  const cx = (b.minX + b.maxX) * 0.5, cz = (b.minZ + b.maxZ) * 0.5;
  out.minX = Math.min(b.minX + margin, cx); out.maxX = Math.max(b.maxX - margin, cx);
  out.minZ = Math.min(b.minZ + margin, cz); out.maxZ = Math.max(b.maxZ - margin, cz);
  return out;
}
export function copyBounds(b, out) { out.minX = b.minX; out.maxX = b.maxX; out.minZ = b.minZ; out.maxZ = b.maxZ; return out; }
export function containsPlanar(b, p) { return p.x >= b.minX && p.x <= b.maxX && p.z >= b.minZ && p.z <= b.maxZ; }

/** Planar clamp of p into b, written into out (out.y = p.y). */
export function clampPlanar(p, b, out) {
  out.x = clamp(p.x, b.minX, b.maxX);
  out.y = p.y;
  out.z = clamp(p.z, b.minZ, b.maxZ);
  return out;
}

/** Planar distance from p to the nearest point of b (0 inside). */
export function planarDistanceToBounds(p, b) {
  const dx = Math.max(b.minX - p.x, 0, p.x - b.maxX);
  const dz = Math.max(b.minZ - p.z, 0, p.z - b.maxZ);
  return Math.hypot(dx, dz);
}

/** Distance (m) one can travel from (px, pz) along the unit direction (dx, dz) before leaving b (0 when outside). */
export function roomAlong(px, pz, dx, dz, b) {
  let t = Infinity;
  if (dx > 1e-6) t = Math.min(t, (b.maxX - px) / dx); else if (dx < -1e-6) t = Math.min(t, (b.minX - px) / dx);
  if (dz > 1e-6) t = Math.min(t, (b.maxZ - pz) / dz); else if (dz < -1e-6) t = Math.min(t, (b.minZ - pz) / dz);
  return Math.max(0, t);
}

/** 0 at the centre of b, 1 on its edge (Chebyshev-normalised). */
export function edgeProximity(px, pz, b) {
  const hx = Math.max(1e-3, (b.maxX - b.minX) * 0.5), hz = Math.max(1e-3, (b.maxZ - b.minZ) * 0.5);
  const cx = (b.minX + b.maxX) * 0.5, cz = (b.minZ + b.maxZ) * 0.5;
  return Math.max(Math.abs(px - cx) / hx, Math.abs(pz - cz) / hz);
}

/** Zeroes the planar components of `move` that would push a body at `p` out of b (within `margin`). */
export function keepInside(p, move, b, margin) {
  if (p.x <= b.minX + margin && move.x < 0) move.x = 0;
  if (p.x >= b.maxX - margin && move.x > 0) move.x = 0;
  if (p.z <= b.minZ + margin && move.z < 0) move.z = 0;
  if (p.z >= b.maxZ - margin && move.z > 0) move.z = 0;
  return move;
}

/**
 * Arrive-steering stick vector from `from` to `to` (planar): zero inside `arrive`, eases off inside `slow`
 * (never below 0.3 so the body keeps walking). Writes out.x/out.z (out.y = 0). Returns the planar distance.
 */
export function seek(from, to, arrive, slow, out) {
  const dx = to.x - from.x, dz = to.z - from.z;
  const d = Math.hypot(dx, dz);
  out.y = 0;
  if (d <= arrive || d < 1e-6) { out.x = 0; out.z = 0; return d; }
  const mag = Math.max(0.3, clamp01((d - arrive) / Math.max(0.01, slow - arrive)));
  out.x = (dx / d) * mag; out.z = (dz / d) * mag;
  return d;
}

// ------------------------------------------------------------------ kinematics

/**
 * Seconds to cover `distance` along a line starting at speed v0 (component along the line), with acceleration
 * `accel` up to `vmax` (trapezoid profile). Used to judge whether a sidestep clears the ball in time.
 */
export function timeToCover(distance, v0, accel, vmax) {
  if (!(distance > 0)) return 0;
  const a = Math.max(0.5, accel), vm = Math.max(0.3, vmax);
  const v = clamp(v0, 0, vm);
  const tAcc = (vm - v) / a;
  const dAcc = v * tAcc + 0.5 * a * tAcc * tAcc;
  if (distance <= dAcc) return (-v + Math.sqrt(v * v + 2 * a * distance)) / a;
  return tAcc + (distance - dAcc) / vm;
}

// Scratch state of predictCapsuleImpact (module scope so the sampler needs no closures / allocations).
let _px = 0, _py = 0, _pz = 0, _vx = 0, _vy = 0, _vz = 0, _g = 0, _sx = 0, _sz = 0, _y0 = 0, _y1 = 0, _r2 = 0;
function _ballY(t) { return _py + _vy * t - 0.5 * _g * t * t; }
function _touches(t) {
  const x = _px + _vx * t, y = _ballY(t), z = _pz + _vz * t;
  const cy = y < _y0 ? _y0 : y > _y1 ? _y1 : y;
  const dx = x - _sx, dy = y - cy, dz = z - _sz;
  return dx * dx + dy * dy + dz * dz <= _r2;
}

/**
 * First time t in [0, maxTime] at which a ballistic sphere touches a static upright capsule.
 * Ball: position p, velocity v, downward gravity g (m/s^2, already scaled), radius ballRadius.
 * Capsule: feet at `base`, radius capRadius, total height capHeight.
 * The prediction stops (no hit) when the ball reaches the floor first (a floor bounce ends a live throw).
 * Sampled every `step` seconds then refined by bisection. Writes the ball centre at contact into `out`.
 * @returns {number} contact time, or -1
 */
export function predictCapsuleImpact(p, v, g, ballRadius, base, capRadius, capHeight, maxTime, out, floorY = 0, step = 1 / 120) {
  _px = p.x; _py = p.y; _pz = p.z; _vx = v.x; _vy = v.y; _vz = v.z; _g = g;
  _sx = base.x; _sz = base.z;
  _y0 = base.y + capRadius; _y1 = base.y + Math.max(capRadius, capHeight - capRadius);
  const reach = capRadius + ballRadius; _r2 = reach * reach;
  let hit = -1;
  if (_touches(0)) hit = 0;
  else {
    let prev = 0;
    for (let t = step; t <= maxTime + 1e-9; t += step) {
      if (_ballY(t) - ballRadius < floorY) return -1;
      if (_touches(t)) {
        let lo = prev, hi = t;
        for (let i = 0; i < 12; i++) { const mid = (lo + hi) * 0.5; if (_touches(mid)) hi = mid; else lo = mid; }
        hit = hi;
        break;
      }
      prev = t;
    }
  }
  if (hit < 0) return -1;
  out.x = _px + _vx * hit; out.y = _ballY(hit); out.z = _pz + _vz * hit;
  return hit;
}

/**
 * Intercept point for a throw of `speed` from `origin` at a target moving with constant planar velocity: fixed-point
 * iteration on the flight time (gravity drop is solved by the throw solver, which aims through the point).
 * The lead is capped at `maxLead` metres (guards against solver blow-ups on very slow throws).
 * Writes the lead point into `out` (y = target.y). Returns the estimated flight time.
 */
export function interceptPoint(origin, target, targetVel, speed, out, maxLead = 12, iterations = 4) {
  const s = Math.max(0.5, speed);
  let t = Math.hypot(target.x - origin.x, target.y - origin.y, target.z - origin.z) / s;
  let lx = 0, lz = 0;
  for (let i = 0; i < iterations; i++) {
    lx = targetVel.x * t; lz = targetVel.z * t;
    const len = Math.hypot(lx, lz);
    if (len > maxLead) { lx *= maxLead / len; lz *= maxLead / len; }
    t = Math.hypot(target.x + lx - origin.x, target.y - origin.y, target.z + lz - origin.z) / s;
  }
  out.x = target.x + lx; out.y = target.y; out.z = target.z + lz;
  return t;
}

// ------------------------------------------------------------------ catching & dodging

/**
 * Seconds before impact at which the bot will press Catch: the ideal lead (scaled by a wider perfect window such as
 * Iron Mitts' 0.225 s, capped at 60% of the window) plus Gaussian timing noise. Negative = pressed too late (the ball
 * hits first, like a human who reacted late); beyond the catch window = pressed too early (whiff).
 */
export function planCatchLead(rng, idealLead, sigma, perfectWindow, catchWindow, basePerfectWindow = 0.15) {
  const pw = perfectWindow > 0 ? perfectWindow : basePerfectWindow;
  const scale = pw / basePerfectWindow;
  const ideal = Math.min(idealLead * scale, pw * 0.6);
  const lead = rng.gaussian(ideal, Math.max(0, sigma));
  return clamp(lead, -0.25, Math.max(catchWindow, pw) + 0.25);
}

/**
 * Sidestep that clears a ball's path with the least movement and has room inside `bounds`.
 * Writes the unit planar direction into outDir (x, z) and returns the distance still needed (m).
 * @param {{x,z}} ballPos
 * @param {{x,z}} ballVel
 * @param {{x,z}} selfPos
 * @param {number} fwdX body forward (fallback path direction)
 * @param {number} fwdZ
 * @param {number} clearance body radius + ball radius + safety margin
 */
export function chooseSidestep(ballPos, ballVel, selfPos, fwdX, fwdZ, clearance, bounds, outDir) {
  let px = ballVel.x, pz = ballVel.z;
  if (px * px + pz * pz < 0.01) { px = selfPos.x - ballPos.x; pz = selfPos.z - ballPos.z; }
  if (px * px + pz * pz < 1e-6) { px = -fwdX; pz = -fwdZ; }
  const pl = Math.hypot(px, pz) || 1;
  px /= pl; pz /= pl;
  // Right-hand side of the ball path: cross(up, path) = (pz, 0, -px).
  const rx = pz, rz = -px;
  const offset = (selfPos.x - ballPos.x) * rx + (selfPos.z - ballPos.z) * rz; // + = already right of the path
  const needRight = clearance - offset, needLeft = clearance + offset;
  const roomRight = roomAlong(selfPos.x, selfPos.z, rx, rz, bounds);
  const roomLeft = roomAlong(selfPos.x, selfPos.z, -rx, -rz, bounds);
  const rightOk = roomRight >= needRight, leftOk = roomLeft >= needLeft;
  let side;
  if (rightOk && leftOk) side = needRight <= needLeft ? 1 : -1;
  else if (rightOk) side = 1;
  else if (leftOk) side = -1;
  else side = roomRight - needRight >= roomLeft - needLeft ? 1 : -1;
  outDir.x = rx * side; outDir.y = 0; outDir.z = rz * side;
  return Math.max(0, side > 0 ? needRight : needLeft);
}

/**
 * Score-weighted random index (roulette) over scores[0..n). `r01` is a uniform sample in [0, 1).
 * Non-positive scores never win unless every score is non-positive (then index 0).
 */
export function weightedPick(scores, n, r01) {
  let total = 0;
  for (let i = 0; i < n; i++) if (scores[i] > 0) total += scores[i];
  if (!(total > 0)) return 0;
  let r = r01 * total;
  for (let i = 0; i < n; i++) {
    if (!(scores[i] > 0)) continue;
    r -= scores[i];
    if (r <= 0) return i;
  }
  for (let i = n - 1; i >= 0; i--) if (scores[i] > 0) return i;
  return 0;
}
