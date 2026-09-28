// ---------------------------------------------------------------------------------------------------------------
// Pure geometry / steering math shared by the tank heroes' world objects (Bear's field + barrier, Gouki's tackle guard,
// Screws' turret and recycle field). NO three.js import: every function works on plain {x, y, z} objects (THREE.Vector3
// satisfies that shape) so the file is unit tested with node:test (screwsGadgetMath.test.js).
//
// Conventions: a "swept" test moves a point from `from` to `to` during one fixed step; the returned `t` is the fraction
// (0..1) of that segment at first contact, or -1 for no contact. A sphere moving with radius r against a shape is the
// same as a point against the shape inflated by r (Minkowski sum) - callers inflate before calling.
// ---------------------------------------------------------------------------------------------------------------

const EPS = 1e-9;

/**
 * First contact of a point moving from `from` to `to` with a sphere (center, radius).
 * Starting inside the sphere counts as contact at t = 0.
 * @returns {number} fraction 0..1, or -1 when the segment misses
 */
export function sweptPointVsSphere(from, to, center, radius) {
  const dx = to.x - from.x, dy = to.y - from.y, dz = to.z - from.z;
  const mx = from.x - center.x, my = from.y - center.y, mz = from.z - center.z;
  const c = mx * mx + my * my + mz * mz - radius * radius;
  if (c <= 0) return 0; // already inside
  const a = dx * dx + dy * dy + dz * dz;
  if (a < EPS) return -1; // not moving and outside
  const b = mx * dx + my * dy + mz * dz;
  if (b >= 0) return -1; // moving away
  const disc = b * b - a * c;
  if (disc < 0) return -1;
  const t = (-b - Math.sqrt(disc)) / a;
  return t >= 0 && t <= 1 ? t : -1;
}

/**
 * First contact of a point moving from `from` to `to` with an axis-aligned box [min, max] (slab method).
 * Writes the outward face normal of the entered face into `outNormal` ({x,y,z}).
 * Starting inside returns t = 0 with the normal of the nearest face (tunnel recovery).
 * @returns {number} fraction 0..1, or -1 when the segment misses
 */
export function sweptPointVsAabb(from, to, min, max, outNormal) {
  const ox = from.x, oy = from.y, oz = from.z;
  const inside = ox >= min.x && ox <= max.x && oy >= min.y && oy <= max.y && oz >= min.z && oz <= max.z;
  if (inside) {
    // Recovery: push out through the face of least penetration.
    let best = ox - min.x, axis = 0, sign = -1;
    const cand = [max.x - ox, oy - min.y, max.y - oy, oz - min.z, max.z - oz];
    const axes = [0, 1, 1, 2, 2], signs = [1, -1, 1, -1, 1];
    for (let i = 0; i < 5; i++) if (cand[i] < best) { best = cand[i]; axis = axes[i]; sign = signs[i]; }
    setAxisNormal(outNormal, axis, sign);
    return 0;
  }
  let tEnter = 0, tExit = 1, axis = -1, sign = 0;
  for (let i = 0; i < 3; i++) {
    const o = i === 0 ? ox : i === 1 ? oy : oz;
    const d = i === 0 ? to.x - ox : i === 1 ? to.y - oy : to.z - oz;
    const lo = i === 0 ? min.x : i === 1 ? min.y : min.z;
    const hi = i === 0 ? max.x : i === 1 ? max.y : max.z;
    if (Math.abs(d) < EPS) {
      if (o < lo || o > hi) return -1; // parallel and outside this slab
      continue;
    }
    const inv = 1 / d;
    let t0 = (lo - o) * inv, t1 = (hi - o) * inv;
    let s = -1; // entering through the min face -> outward normal is -axis
    if (t0 > t1) { const tmp = t0; t0 = t1; t1 = tmp; s = 1; }
    if (t0 > tEnter) { tEnter = t0; axis = i; sign = s; }
    if (t1 < tExit) tExit = t1;
    if (tEnter > tExit) return -1;
  }
  if (axis < 0) return -1;
  setAxisNormal(outNormal, axis, sign);
  return tEnter >= 0 && tEnter <= 1 ? tEnter : -1;
}

function setAxisNormal(out, axis, sign) {
  if (!out) return;
  out.x = axis === 0 ? sign : 0;
  out.y = axis === 1 ? sign : 0;
  out.z = axis === 2 ? sign : 0;
}

/**
 * Squared distance from point `p` to the segment `a` -> `a + d` (d = displacement). Used for predictive captures:
 * "does the ball pass within r of the hand during this step?".
 */
export function segmentPointDistSq(a, d, p) {
  const len2 = d.x * d.x + d.y * d.y + d.z * d.z;
  let t = 0;
  if (len2 > EPS) {
    t = ((p.x - a.x) * d.x + (p.y - a.y) * d.y + (p.z - a.z) * d.z) / len2;
    t = t < 0 ? 0 : t > 1 ? 1 : t;
  }
  const cx = a.x + d.x * t - p.x, cy = a.y + d.y * t - p.y, cz = a.z + d.z * t - p.z;
  return cx * cx + cy * cy + cz * cz;
}

/** Frame-rate independent exponential blend factor: fraction of the gap closed in `dt` at `rate` (1/s). */
export function expBlend(rate, dt) {
  return rate <= 0 || dt <= 0 ? 0 : 1 - Math.exp(-rate * dt);
}

/** Moves vector `v` toward `target` by at most `maxDelta` (in place). Returns v. */
export function moveTowards(v, target, maxDelta) {
  const dx = target.x - v.x, dy = target.y - v.y, dz = target.z - v.z;
  const len = Math.sqrt(dx * dx + dy * dy + dz * dz);
  if (len <= maxDelta || len < EPS) { v.x = target.x; v.y = target.y; v.z = target.z; return v; }
  const k = maxDelta / len;
  v.x += dx * k; v.y += dy * k; v.z += dz * k;
  return v;
}

/**
 * Magnetic homing step used by Bear's field: blends a velocity toward `toTarget` (the vector from the ball to the
 * hands) with a homing rate that rises from `edgeRate` at the field edge to `coreRate` at the hands, easing the speed
 * down to `arrivalSpeed` near the hands but never below `minSpeed`. Writes the steered velocity into `out`.
 * @param {{x,y,z}} v current velocity
 * @param {{x,y,z}} toTarget vector from the ball to the target point
 * @param {number} radius field radius (m)
 * @returns {number} closeness 0 (edge) .. 1 (at the hands), or -1 when outside the field (out untouched)
 */
export function magneticHoming(v, toTarget, radius, edgeRate, coreRate, arrivalSpeed, minSpeed, dt, out) {
  const dist = Math.sqrt(toTarget.x * toTarget.x + toTarget.y * toTarget.y + toTarget.z * toTarget.z);
  if (dist > radius || dist < 1e-4) return -1;
  const closeness = 1 - dist / radius;
  const speed = Math.sqrt(v.x * v.x + v.y * v.y + v.z * v.z);
  // Keep momentum far out, ease to a catchable speed near the hands.
  let target = speed + (Math.min(speed, arrivalSpeed) - speed) * closeness;
  if (target < minSpeed) target = minSpeed;
  const k = target / dist;
  const blend = expBlend(edgeRate + (coreRate - edgeRate) * closeness, dt);
  out.x = v.x + (toTarget.x * k - v.x) * blend;
  out.y = v.y + (toTarget.y * k - v.y) * blend;
  out.z = v.z + (toTarget.z * k - v.z) * blend;
  return closeness;
}

/**
 * Signed distance (m) of a z coordinate into `sideSign`'s own half (positive = inside own half).
 * sideSign: -1 for Home (defends z < 0), +1 for Away.
 */
export function depthIntoOwnHalf(sideSign, z) {
  return sideSign * z;
}

/**
 * Copy of confinement `bounds` whose centre-line edge is pushed `extra` metres into the opponent's half
 * (Gouki's Tackle Intercept). sideSign: -1 Home (centre-line edge = maxZ), +1 Away (centre-line edge = minZ).
 */
export function extendPastCentreLine(bounds, sideSign, extra, out = {}) {
  out.minX = bounds.minX; out.maxX = bounds.maxX; out.minZ = bounds.minZ; out.maxZ = bounds.maxZ;
  if (sideSign < 0) out.maxZ = Math.max(bounds.maxZ, extra);
  else out.minZ = Math.min(bounds.minZ, -extra);
  return out;
}

/**
 * True when a point at planar offset (dx, dz) from an attacker facing planar unit (fx, fz) is within `radius` and not
 * behind him (`minForwardDot` = cosine-like cut on the normalised offset; -0.2 lets a slightly-beside victim be grabbed).
 */
export function inFrontWithin(dx, dz, fx, fz, radius, minForwardDot = -0.2) {
  const d2 = dx * dx + dz * dz;
  if (d2 > radius * radius) return false;
  if (d2 < 1e-6) return true;
  const inv = 1 / Math.sqrt(d2);
  return (dx * fx + dz * fz) * inv >= minForwardDot;
}

/**
 * Tackle deflection volume test: a ball at offset `rel` from the chest, relative to a charge direction `dir` (planar
 * unit), is deflected when it lies between `-behind` and `reach` metres ahead and within `halfWidth` of the charge axis.
 * @returns {number} forward distance of the ball along `dir` when inside the volume, NaN otherwise
 */
export function inChargeVolume(rel, dir, reach, halfWidth, behind = 0.25) {
  const fwd = rel.x * dir.x + rel.z * dir.z;
  if (fwd < -behind || fwd > reach) return NaN;
  const lx = rel.x - dir.x * fwd, lz = rel.z - dir.z * fwd;
  const lateral2 = lx * lx + lz * lz + rel.y * rel.y * 0.5; // vertical offset counts half (the body is tall)
  return lateral2 <= halfWidth * halfWidth ? fwd : NaN;
}

/**
 * First-order target lead: where to aim a projectile of `speed` from `origin` at a target at `pos` moving with `vel`
 * (planar prediction, a couple of refinement passes). Writes into `out` and returns the flight time estimate.
 */
export function leadTarget(origin, pos, vel, speed, out, maxLeadTime = 1.2) {
  let t = 0;
  for (let i = 0; i < 3; i++) {
    const px = pos.x + vel.x * t, pz = pos.z + vel.z * t;
    const dx = px - origin.x, dz = pz - origin.z, dy = pos.y - origin.y;
    t = Math.min(maxLeadTime, Math.sqrt(dx * dx + dy * dy + dz * dz) / Math.max(1, speed));
  }
  out.x = pos.x + vel.x * t; out.y = pos.y; out.z = pos.z + vel.z * t;
  return t;
}

/** Wraps an angle to (-PI, PI]. */
export function wrapAngle(a) {
  a = (a + Math.PI) % (2 * Math.PI);
  if (a < 0) a += 2 * Math.PI;
  return a - Math.PI;
}

/** Smooth angle approach (radians) at `maxRate` rad/s; handles wrap-around. */
export function approachAngle(current, target, maxRate, dt) {
  const d = wrapAngle(target - current);
  const step = maxRate * dt;
  if (Math.abs(d) <= step) return target;
  return current + Math.sign(d) * step;
}

/**
 * Critically damped spring step (value, velocity) toward `target` with angular frequency `omega` (rad/s).
 * Returns the new value and writes the new velocity into state.v. Used for recoil / deploy animations.
 */
export function springStep(state, target, omega, dt) {
  const x = state.x - target;
  const exp = Math.exp(-omega * dt);
  const tmp = (state.v + omega * x) * dt;
  state.v = (state.v - omega * tmp) * exp;
  state.x = target + (x + tmp) * exp;
  return state.x;
}

/** Deterministic 1-D value noise in [-1, 1] (for procedural blob outlines). */
export function valueNoise1(x, seed = 0) {
  const i = Math.floor(x), f = x - i;
  const u = f * f * (3 - 2 * f);
  return hash1(i, seed) * (1 - u) + hash1(i + 1, seed) * u;
}

function hash1(n, seed) {
  const s = Math.sin((n + seed * 57.13) * 127.1) * 43758.5453;
  return (s - Math.floor(s)) * 2 - 1;
}

function hash2(ix, iy, seed) {
  const s = Math.sin(ix * 127.1 + iy * 311.7 + seed * 74.7) * 43758.5453;
  return s - Math.floor(s);
}

/**
 * Tileable 2-D value noise in [0, 1]: the lattice wraps every `period` cells, so sampling x in [0, period) gives a
 * seamless texture (used for the procedural normal maps of the glue puddle and the turret's cast metal).
 */
export function periodicValueNoise2(x, y, period, seed = 0) {
  const ix = Math.floor(x), iy = Math.floor(y);
  const fx = x - ix, fy = y - iy;
  const ux = fx * fx * (3 - 2 * fx), uy = fy * fy * (3 - 2 * fy);
  const m = (v) => ((v % period) + period) % period;
  const x0 = m(ix), x1 = m(ix + 1), y0 = m(iy), y1 = m(iy + 1);
  const a = hash2(x0, y0, seed), b = hash2(x1, y0, seed), c = hash2(x0, y1, seed), d = hash2(x1, y1, seed);
  return (a * (1 - ux) + b * ux) * (1 - uy) + (c * (1 - ux) + d * ux) * uy;
}

/** Irregular blob radius at angle `theta` (radians) for a puddle of base radius `r` (+-`wobble` fraction). */
export function blobRadius(theta, r, wobble, seed = 0) {
  // Periodic lattice around the circle (period = cell count) so the outline closes without a seam at theta = 2 PI.
  const t = (((theta / (2 * Math.PI)) % 1) + 1) % 1;
  const n = (periodicValueNoise2(t * 7, 0.5, 7, seed) * 2 - 1) * 0.65 + (periodicValueNoise2(t * 17, 3.5, 17, seed + 3) * 2 - 1) * 0.35;
  return r * (1 + wobble * n);
}
