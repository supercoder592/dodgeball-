// ---------------------------------------------------------------------------------------------------------------
// Court math (pure, no three.js - unit tested by world/courtMath.test.js). Used by world/court.js, the Motor, the
// AI bounds helpers and the ball pickup / ball-boy rules.
//
// REGION = { minX, maxX, minZ, maxZ, hole: null | { minX, maxX, minZ, maxZ } }   (planar, metres)
//   A plain box (hole null) is an infield half. A box with a hole is a team's U-shaped OUTFIELD: the outer box minus
//   the opponent's half. The hole always overshoots the open (centre-line) side of the U, so that side is never an
//   exit; an EXIT edge is a hole edge lying strictly inside the outer box. Hole tests are strict: hole edges are
//   walkable (a point on an edge is inside the region).
//
// U PATH: the centre line of a team's U, arc-length parametrised by u in [0, 1]:
//   A(-xs, s*z0) -> B(-xs, s*zb) -> C(xs, s*zb) -> D(xs, s*z0)     (u = 0 is always the -X end)
//   with xs = halfW + sideW/2, zb = halfL + depth/2, z0 = the front inset near the centre line, s = +1 for the U on
//   the +Z side (Home's) and -1 for the mirror (Away's).
//
// Every function writes into caller-provided objects (allocation-free).
// ---------------------------------------------------------------------------------------------------------------
import { TEAM, ZONE } from '../core/constants.js';

/** Is (x, z) inside the region (outer box inclusive, hole strict)? */
export function regionContainsXZ(b, x, z) {
  if (x < b.minX || x > b.maxX || z < b.minZ || z > b.maxZ) return false;
  const h = b.hole;
  return !(h && x > h.minX && x < h.maxX && z > h.minZ && z < h.maxZ);
}

/**
 * Closest point of the region to (x, z) into out {x, z}: clamp into the outer box; a point then strictly inside the
 * hole moves to the nearest EXIT edge (ties: minX, maxX, minZ, maxZ). Exact for the U.
 * @returns {{x:number, z:number}} out
 */
export function regionClampXZ(b, x, z, out) {
  let cx = x < b.minX ? b.minX : x > b.maxX ? b.maxX : x;
  let cz = z < b.minZ ? b.minZ : z > b.maxZ ? b.maxZ : z;
  const h = b.hole;
  if (h && cx > h.minX && cx < h.maxX && cz > h.minZ && cz < h.maxZ) {
    let best = Infinity, axis = -1;
    if (h.minX > b.minX && cx - h.minX < best) { best = cx - h.minX; axis = 0; }
    if (h.maxX < b.maxX && h.maxX - cx < best) { best = h.maxX - cx; axis = 1; }
    if (h.minZ > b.minZ && cz - h.minZ < best) { best = cz - h.minZ; axis = 2; }
    if (h.maxZ < b.maxZ && h.maxZ - cz < best) { best = h.maxZ - cz; axis = 3; }
    if (axis === 0) cx = h.minX;
    else if (axis === 1) cx = h.maxX;
    else if (axis === 2) cz = h.minZ;
    else if (axis === 3) cz = h.maxZ;
  }
  out.x = cx; out.z = cz;
  return out;
}

const _c = { x: 0, z: 0 };

/** Planar distance from (x, z) to the region (0 inside). */
export function regionDistanceXZ(b, x, z) {
  regionClampXZ(b, x, z, _c);
  const dx = _c.x - x, dz = _c.z - z;
  return Math.sqrt(dx * dx + dz * dz);
}

/**
 * Region inset by `m` metres (m < 0 expands): the outer box shrinks and the hole grows. `out` may be `b` itself; its
 * hole object is re-used when present (allocates one only when `out` has none and `b` has a hole).
 * @returns {object} out
 */
export function insetRegion(b, m, out = {}) {
  const h = b.hole;
  const hx0 = h ? h.minX : 0, hx1 = h ? h.maxX : 0, hz0 = h ? h.minZ : 0, hz1 = h ? h.maxZ : 0;
  out.minX = b.minX + m; out.maxX = b.maxX - m; out.minZ = b.minZ + m; out.maxZ = b.maxZ - m;
  if (h) {
    const oh = out.hole && out.hole !== h ? out.hole : (out === b ? h : {});
    oh.minX = hx0 - m; oh.maxX = hx1 + m; oh.minZ = hz0 - m; oh.maxZ = hz1 + m;
    out.hole = oh;
  } else {
    out.hole = null;
  }
  return out;
}

/**
 * Geometry of the U centre line.
 * @param {number} halfW half court width   @param {number} halfL half court length
 * @param {number} sideW side strip width    @param {number} depth back strip depth
 * @param {number} front inset of the arm ends from the centre line
 * @returns {{xs:number, zb:number, z0:number, arm:number, back:number, length:number, c1:number, c2:number}}
 */
export function uPathGeometry(halfW, halfL, sideW, depth, front) {
  const xs = halfW + sideW / 2, zb = halfL + depth / 2, z0 = front;
  const arm = Math.max(0, zb - z0), back = 2 * xs, length = 2 * arm + back;
  return { xs, zb, z0, arm, back, length, c1: arm / length, c2: (arm + back) / length };
}

/** Point of the U centre line at `u` (clamped to [0, 1]) for side sign `s`, into out {x, z}. */
export function uPointXZ(g, s, u, out) {
  const t = (u < 0 ? 0 : u > 1 ? 1 : u) * g.length;
  if (t <= g.arm) { out.x = -g.xs; out.z = s * (g.z0 + t); }
  else if (t <= g.arm + g.back) { out.x = -g.xs + (t - g.arm); out.z = s * g.zb; }
  else { out.x = g.xs; out.z = s * (g.zb - (t - g.arm - g.back)); }
  return out;
}

/** u of the point of the U centre line nearest to (x, z) (ties: the lower u). */
export function uParamXZ(g, s, x, z) {
  const zl = s * z; // work on the +Z mirror
  // Left arm: x = -xs, z from z0 to zb.
  let zc = zl < g.z0 ? g.z0 : zl > g.zb ? g.zb : zl;
  let dx = x + g.xs, dz = zl - zc;
  let best = dx * dx + dz * dz, t = zc - g.z0;
  // Back: z = zb, x from -xs to xs.
  let xc = x < -g.xs ? -g.xs : x > g.xs ? g.xs : x;
  dx = x - xc; dz = zl - g.zb;
  let d = dx * dx + dz * dz;
  if (d < best) { best = d; t = g.arm + (xc + g.xs); }
  // Right arm: x = xs, z from zb down to z0.
  zc = zl < g.z0 ? g.z0 : zl > g.zb ? g.zb : zl;
  dx = x - g.xs; dz = zl - zc;
  d = dx * dx + dz * dz;
  if (d < best) { best = d; t = g.arm + g.back + (g.zb - zc); }
  return g.length > 0 ? t / g.length : 0;
}

/**
 * Zone owning a floor point on the painted lines: an infield half, else a team's U (within the outer boundary +
 * margin), else null. z == 0 belongs to the Away side (Court.halfOwner): (0, 0) is Away infield, (5.5, 0) Home U.
 * @param {{halfW:number, halfL:number, outerHalfW:number, outerHalfL:number}} dims
 * @param {{team:number, zone:string}} out
 * @returns {{team:number, zone:string}|null}
 */
export function zoneAtXZ(x, z, dims, out, margin = 0) {
  const ax = x < 0 ? -x : x, az = z < 0 ? -z : z;
  if (ax <= dims.halfW && az <= dims.halfL) {
    out.team = z < 0 ? TEAM.HOME : TEAM.AWAY; out.zone = ZONE.INFIELD;
    return out;
  }
  if (ax <= dims.outerHalfW + margin && az <= dims.outerHalfL + margin) {
    out.team = z >= 0 ? TEAM.HOME : TEAM.AWAY; out.zone = ZONE.OUTFIELD;
    return out;
  }
  return null;
}

/**
 * Keeps a moving body out of a region's hole. `state` = { px, pz, vx, vz } (planar position and velocity).
 *  - dt > 0 (predictive, before integration): if p + v dt would end strictly inside the hole, the velocity component
 *    across the wall entered LAST (slab-entry order) is limited so the step ends on that wall.
 *  - dt = 0 (resolve, after integration) - and whenever p is already strictly inside: push out through the nearest
 *    EXIT edge of `outer` and remove the velocity pointing back into the hole.
 * No-op without a hole.
 * @returns {boolean} true when something was clamped
 */
export function clampOutOfHole(state, hole, outer, dt) {
  if (!hole) return false;
  const px = state.px, pz = state.pz;
  const inside = px > hole.minX && px < hole.maxX && pz > hole.minZ && pz < hole.maxZ;
  if (inside) {
    let best = Infinity, axis = -1;
    const o = outer;
    if ((!o || hole.minX > o.minX) && px - hole.minX < best) { best = px - hole.minX; axis = 0; }
    if ((!o || hole.maxX < o.maxX) && hole.maxX - px < best) { best = hole.maxX - px; axis = 1; }
    if ((!o || hole.minZ > o.minZ) && pz - hole.minZ < best) { best = pz - hole.minZ; axis = 2; }
    if ((!o || hole.maxZ < o.maxZ) && hole.maxZ - pz < best) { best = hole.maxZ - pz; axis = 3; }
    if (axis === 0) { state.px = hole.minX; if (state.vx > 0) state.vx = 0; }
    else if (axis === 1) { state.px = hole.maxX; if (state.vx < 0) state.vx = 0; }
    else if (axis === 2) { state.pz = hole.minZ; if (state.vz > 0) state.vz = 0; }
    else if (axis === 3) { state.pz = hole.maxZ; if (state.vz < 0) state.vz = 0; }
    return axis >= 0;
  }
  if (!(dt > 0)) return false;
  const nx = px + state.vx * dt, nz = pz + state.vz * dt;
  if (!(nx > hole.minX && nx < hole.maxX && nz > hole.minZ && nz < hole.maxZ)) return false;
  // Entry time (fraction of the step) into each slab; an axis already strictly inside its slab entered at -Inf.
  let tx = -Infinity, wx = 0;
  if (px <= hole.minX) { wx = hole.minX; tx = state.vx > 0 ? (hole.minX - px) / (state.vx * dt) : Infinity; }
  else if (px >= hole.maxX) { wx = hole.maxX; tx = state.vx < 0 ? (hole.maxX - px) / (state.vx * dt) : Infinity; }
  let tz = -Infinity, wz = 0;
  if (pz <= hole.minZ) { wz = hole.minZ; tz = state.vz > 0 ? (hole.minZ - pz) / (state.vz * dt) : Infinity; }
  else if (pz >= hole.maxZ) { wz = hole.maxZ; tz = state.vz < 0 ? (hole.maxZ - pz) / (state.vz * dt) : Infinity; }
  if (tx >= tz) state.vx = (wx - px) / dt;
  else state.vz = (wz - pz) / dt;
  return true;
}
