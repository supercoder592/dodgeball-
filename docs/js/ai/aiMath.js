// ---------------------------------------------------------------------------------------------------------------
// Pure AI math (no three.js, no game state): works on any {x, y, z} objects (THREE.Vector3 included) and on plain
// planar regions { minX, maxX, minZ, maxZ, hole? } (world/court.js; the U outfields carry a hole). Allocation-free:
// results are written into `out` arguments. Unit tested by aiMath.test.js (node:test).
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

// ------------------------------------------------------------------ planar regions (hole-aware)
// A region is a planar box { minX, maxX, minZ, maxZ } with an optional `hole` box of the same shape (world/court.js
// U outfields: the opponent's half, overshooting the open centre-line side). Hole tests are strict, so hole edges are
// walkable. An EXIT edge is a hole edge lying strictly inside the outer box: a point stuck inside the hole leaves
// through the nearest exit edge. Mirrors world/courtMath.js (kept local so the AI's pure tests have no dependencies).

/** Hole edge ids used by the exit-edge helpers. */
const H_MINX = 0, H_MAXX = 1, H_MINZ = 2, H_MAXZ = 3;
function _isExit(b, h, edge) {
  switch (edge) {
    case H_MINX: return h.minX > b.minX;
    case H_MAXX: return h.maxX < b.maxX;
    case H_MINZ: return h.minZ > b.minZ;
    default: return h.maxZ < b.maxZ;
  }
}
/** Point strictly inside the region's hole. */
function _inHole(h, x, z) { return !!h && x > h.minX && x < h.maxX && z > h.minZ && z < h.maxZ; }

/**
 * Copies `b` shrunk by `margin` on every side into `out` (collapses to the centre when too small). A hole grows by
 * `margin` (its exit edges at most up to 1 mm short of the middle of the walkable band, so a narrow band collapses to a
 * line that stays walkable rather than inverting); `out.hole` is a buffer kept on `out` (allocated once) or null.
 */
export function shrinkBounds(b, margin, out) {
  const cx = (b.minX + b.maxX) * 0.5, cz = (b.minZ + b.maxZ) * 0.5;
  out.minX = Math.min(b.minX + margin, cx); out.maxX = Math.max(b.maxX - margin, cx);
  out.minZ = Math.min(b.minZ + margin, cz); out.maxZ = Math.max(b.maxZ - margin, cz);
  const h = b.hole;
  if (!h) { out.hole = null; return out; }
  const oh = _holeBuffer(out);
  // Exit edges: grow toward the outer wall, meeting the shrunk outer edge in the middle of the band at worst.
  if (_isExit(b, h, H_MINX)) { const mid = (b.minX + h.minX) * 0.5; oh.minX = Math.max(h.minX - margin, mid + 1e-3); out.minX = Math.min(out.minX, mid); } else oh.minX = h.minX - margin;
  if (_isExit(b, h, H_MAXX)) { const mid = (b.maxX + h.maxX) * 0.5; oh.maxX = Math.min(h.maxX + margin, mid - 1e-3); out.maxX = Math.max(out.maxX, mid); } else oh.maxX = h.maxX + margin;
  if (_isExit(b, h, H_MINZ)) { const mid = (b.minZ + h.minZ) * 0.5; oh.minZ = Math.max(h.minZ - margin, mid + 1e-3); out.minZ = Math.min(out.minZ, mid); } else oh.minZ = h.minZ - margin;
  if (_isExit(b, h, H_MAXZ)) { const mid = (b.maxZ + h.maxZ) * 0.5; oh.maxZ = Math.min(h.maxZ + margin, mid - 1e-3); out.maxZ = Math.max(out.maxZ, mid); } else oh.maxZ = h.maxZ + margin;
  out.hole = oh;
  return out;
}
function _holeBuffer(out) {
  let h = out._holeBuf;
  if (!h) { h = { minX: 0, maxX: 0, minZ: 0, maxZ: 0 }; Object.defineProperty(out, '_holeBuf', { value: h, enumerable: false }); }
  return h;
}
/** Copies region `b` (and its hole, into a buffer owned by `out`) into `out`. */
export function copyBounds(b, out) {
  out.minX = b.minX; out.maxX = b.maxX; out.minZ = b.minZ; out.maxZ = b.maxZ;
  const h = b.hole;
  if (h) {
    const oh = _holeBuffer(out);
    oh.minX = h.minX; oh.maxX = h.maxX; oh.minZ = h.minZ; oh.maxZ = h.maxZ;
    out.hole = oh;
  } else out.hole = null;
  return out;
}
/** Inside the outer box (inclusive) and not strictly inside the hole. */
export function containsPlanar(b, p) {
  return p.x >= b.minX && p.x <= b.maxX && p.z >= b.minZ && p.z <= b.maxZ && !_inHole(b.hole, p.x, p.z);
}

/**
 * Planar clamp of p into region b (nearest point: outer box, then out of the hole through the nearest exit edge),
 * written into out (out.y = p.y).
 */
export function clampPlanar(p, b, out) {
  let x = clamp(p.x, b.minX, b.maxX), z = clamp(p.z, b.minZ, b.maxZ);
  const h = b.hole;
  if (_inHole(h, x, z)) {
    let best = Infinity, bx = x, bz = z;
    if (_isExit(b, h, H_MINX) && x - h.minX < best) { best = x - h.minX; bx = h.minX; bz = z; }
    if (_isExit(b, h, H_MAXX) && h.maxX - x < best) { best = h.maxX - x; bx = h.maxX; bz = z; }
    if (_isExit(b, h, H_MINZ) && z - h.minZ < best) { best = z - h.minZ; bx = x; bz = h.minZ; }
    if (_isExit(b, h, H_MAXZ) && h.maxZ - z < best) { best = h.maxZ - z; bx = x; bz = h.maxZ; }
    x = bx; z = bz;
  }
  out.x = x; out.y = p.y; out.z = z;
  return out;
}

const _cp = { x: 0, y: 0, z: 0 };
/** Planar distance from p to the nearest point of region b (0 inside). */
export function planarDistanceToBounds(p, b) {
  if (!b.hole) {
    const dx = Math.max(b.minX - p.x, 0, p.x - b.maxX);
    const dz = Math.max(b.minZ - p.z, 0, p.z - b.maxZ);
    return Math.hypot(dx, dz);
  }
  clampPlanar(p, b, _cp);
  return Math.hypot(p.x - _cp.x, p.z - _cp.z);
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

/**
 * Zeroes the planar components of `move` that would push a body at `p` out of b (within `margin`), including the
 * exit walls of a hole (a U outfielder never steps into the opponent's half).
 */
export function keepInside(p, move, b, margin) {
  if (p.x <= b.minX + margin && move.x < 0) move.x = 0;
  if (p.x >= b.maxX - margin && move.x > 0) move.x = 0;
  if (p.z <= b.minZ + margin && move.z < 0) move.z = 0;
  if (p.z >= b.maxZ - margin && move.z > 0) move.z = 0;
  const h = b.hole;
  if (h) {
    const inZ = p.z > h.minZ && p.z < h.maxZ, inX = p.x > h.minX && p.x < h.maxX;
    if (inZ && _isExit(b, h, H_MINX) && p.x <= h.minX + 1e-6 && p.x >= h.minX - margin && move.x > 0) move.x = 0;
    if (inZ && _isExit(b, h, H_MAXX) && p.x >= h.maxX - 1e-6 && p.x <= h.maxX + margin && move.x < 0) move.x = 0;
    if (inX && _isExit(b, h, H_MINZ) && p.z <= h.minZ + 1e-6 && p.z >= h.minZ - margin && move.z > 0) move.z = 0;
    if (inX && _isExit(b, h, H_MAXZ) && p.z >= h.maxZ - 1e-6 && p.z <= h.maxZ + margin && move.z < 0) move.z = 0;
  }
  return move;
}

// ------------------------------------------------------------------ crossfire & passing lanes

/**
 * How well a thrower at P catches enemy E in a crossfire with the ball holder H: (1 - cos angle(E->H, E->P)) / 2.
 * 1 = directly opposite the holder (E cannot face both), 0 = same side as the holder, 0.5 = at 90 degrees.
 */
export function crossfireScore(ex, ez, hx, hz, px, pz) {
  const ax = hx - ex, az = hz - ez, bx = px - ex, bz = pz - ez;
  const la = Math.hypot(ax, az), lb = Math.hypot(bx, bz);
  if (la < 1e-6 || lb < 1e-6) return 0.5;
  return (1 - clamp((ax * bx + az * bz) / (la * lb), -1, 1)) * 0.5;
}

function _distToSeg(px, pz, ax, az, bx, bz) {
  const dx = bx - ax, dz = bz - az, l2 = dx * dx + dz * dz;
  const t = l2 > 1e-12 ? clamp(((px - ax) * dx + (pz - az) * dz) / l2, 0, 1) : 0;
  return Math.hypot(px - (ax + dx * t), pz - (az + dz * t));
}

/**
 * Interception risk of a lobbed pass from O to R: the number of enemies (flat [x0, z0, x1, z1, ...], n of them) within
 * `radius` of the first or last `lowFrac` of the path, where the ball is still low enough to be caught. An enemy
 * standing under the middle of the lob cannot reach it.
 */
export function passLaneRisk(ox, oz, rx, rz, enemiesXZ, n, radius = 1.2, lowFrac = 0.2) {
  const dx = rx - ox, dz = rz - oz;
  const ax = ox + dx * lowFrac, az = oz + dz * lowFrac, bx = rx - dx * lowFrac, bz = rz - dz * lowFrac;
  let risk = 0;
  for (let i = 0; i < n; i++) {
    const ex = enemiesXZ[i * 2], ez = enemiesXZ[i * 2 + 1];
    if (_distToSeg(ex, ez, ox, oz, ax, az) <= radius || _distToSeg(ex, ez, bx, bz, rx, rz) <= radius) risk++;
  }
  return risk;
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
