// ---------------------------------------------------------------------------------------------------------------
// Pure math behind Shadow's illusions (hittable capsules, formation slots, shell-game shuffles).
// NO three.js import: every function works on plain {x, y, z} objects (THREE.Vector3 satisfies that shape) and on
// plain bounds objects { minX, maxX, minZ, maxZ } (Court.confinement) so it is unit tested with node:test
// (shadowMath.test.js).
//
// Swept-test convention (same as the other ability math modules): a point moves from `from` to `to` during one fixed
// step; the returned `t` is the fraction 0..1 of that segment at first contact, or -1 for no contact. A sphere of
// radius r against a shape is a point against the shape inflated by r (Minkowski sum) - callers inflate the radius.
// ---------------------------------------------------------------------------------------------------------------

const EPS = 1e-9;

/**
 * First contact of a point moving from `from` to `to` with a sphere (center, radius). Starting inside -> t = 0.
 * @returns {number} fraction 0..1 or -1
 */
export function sweptPointVsSphere(from, to, center, radius) {
  const dx = to.x - from.x, dy = to.y - from.y, dz = to.z - from.z;
  const mx = from.x - center.x, my = from.y - center.y, mz = from.z - center.z;
  const c = mx * mx + my * my + mz * mz - radius * radius;
  if (c <= 0) return 0;
  const a = dx * dx + dy * dy + dz * dz;
  if (a < EPS) return -1;
  const b = mx * dx + my * dy + mz * dz;
  if (b >= 0) return -1;
  const disc = b * b - a * c;
  if (disc < 0) return -1;
  const t = (-b - Math.sqrt(disc)) / a;
  return t >= 0 && t <= 1 ? t : -1;
}

/**
 * First contact of a point moving from `from` to `to` with a VERTICAL capsule whose axis runs from
 * (axis.x, y0, axis.z) to (axis.x, y1, axis.z) with radius `radius` (y0 <= y1).
 * Writes the contact position of the moving point into `outPoint` and the outward surface normal into `outNormal`.
 * @returns {number} fraction 0..1 or -1
 */
export function segmentVsVerticalCapsule(from, to, axis, y0, y1, radius, outPoint, outNormal) {
  const dx = to.x - from.x, dy = to.y - from.y, dz = to.z - from.z;
  let best = -1;
  // Already inside (tunnelled or spawned inside): contact at t = 0, normal away from the nearest axis point.
  {
    const cy = Math.min(y1, Math.max(y0, from.y));
    const ox = from.x - axis.x, oy = from.y - cy, oz = from.z - axis.z;
    if (ox * ox + oy * oy + oz * oz <= radius * radius) {
      best = 0;
    }
  }
  if (best < 0) {
    // Infinite vertical cylinder (2D circle test in XZ); valid only when the entry height lies on the straight part.
    const mx = from.x - axis.x, mz = from.z - axis.z;
    const a = dx * dx + dz * dz;
    if (a > EPS) {
      const b = mx * dx + mz * dz;
      const c = mx * mx + mz * mz - radius * radius;
      const disc = b * b - a * c;
      if (disc < 0) return -1; // the line misses the infinite cylinder, so it misses the end spheres too
      const t = (-b - Math.sqrt(disc)) / a;
      if (t >= 0 && t <= 1) {
        const y = from.y + dy * t;
        if (y >= y0 && y <= y1) best = t;
      }
    }
    // End spheres (rounded caps).
    const _c = { x: axis.x, y: y0, z: axis.z };
    const t0 = sweptPointVsSphere(from, to, _c, radius);
    if (t0 >= 0 && (best < 0 || t0 < best)) best = t0;
    _c.y = y1;
    const t1 = sweptPointVsSphere(from, to, _c, radius);
    if (t1 >= 0 && (best < 0 || t1 < best)) best = t1;
  }
  if (best < 0) return -1;
  const px = from.x + dx * best, py = from.y + dy * best, pz = from.z + dz * best;
  if (outPoint) { outPoint.x = px; outPoint.y = py; outPoint.z = pz; }
  if (outNormal) {
    const cy = Math.min(y1, Math.max(y0, py));
    let nx = px - axis.x, ny = py - cy, nz = pz - axis.z;
    const len = Math.hypot(nx, ny, nz);
    if (len > 1e-6) { nx /= len; ny /= len; nz /= len; } else {
      // Degenerate (point on the axis): oppose the motion.
      const ml = Math.hypot(dx, dy, dz) || 1;
      nx = -dx / ml; ny = -dy / ml; nz = -dz / ml;
    }
    outNormal.x = nx; outNormal.y = ny; outNormal.z = nz;
  }
  return best;
}

/** Frame-rate independent exponential smoothing factor: lerp(a, b, expSmoothing(k, dt)) approaches b at rate k (1/s). */
export function expSmoothing(sharpness, dt) {
  return dt > 0 && sharpness > 0 ? 1 - Math.exp(-sharpness * dt) : 0;
}

/** Hermite smoothstep on [0, 1] (zero velocity at both ends). */
export function smoothstep01(t) {
  const x = t < 0 ? 0 : t > 1 ? 1 : t;
  return x * x * (3 - 2 * x);
}

/** d/dt of smoothstep01 on [0, 1] (per unit of t): 6 t (1 - t). */
export function smoothstep01Derivative(t) {
  const x = t < 0 ? 0 : t > 1 ? 1 : t;
  return 6 * x * (1 - x);
}

/** Shortest signed angle from a to b (radians, in (-PI, PI]). */
export function deltaAngle(a, b) {
  let d = (b - a) % (Math.PI * 2);
  if (d > Math.PI) d -= Math.PI * 2;
  else if (d <= -Math.PI) d += Math.PI * 2;
  return d;
}

/**
 * Lateral offset of flank slot i for a Night Parade formation: +1, -1, +2, -2 ... times `spacing`
 * (i = 0 right flank, i = 1 left flank, then the next ring outward).
 */
export function flankOffset(i, spacing) {
  const side = i % 2 === 0 ? 1 : -1;
  return side * (Math.floor(i / 2) + 1) * spacing;
}

/** Clamp a point's x/z into bounds (mutates and returns p). */
export function clampToBounds(bounds, p) {
  if (!bounds) return p;
  p.x = Math.min(bounds.maxX, Math.max(bounds.minX, p.x));
  p.z = Math.min(bounds.maxZ, Math.max(bounds.minZ, p.z));
  return p;
}

/**
 * World x/z of formation slot `slot` for a line through `anchor` along the planar unit `lateral`
 * (slot `centre` sits on the anchor, slots `spacing` metres apart). Writes x/z into `out` (y untouched).
 */
export function slotPosition(anchor, lateral, slot, centre, spacing, out) {
  const s = (slot - centre) * spacing;
  out.x = anchor.x + lateral.x * s;
  out.z = anchor.z + lateral.z * s;
  return out;
}

/** True when the ideal slot position lies within `tolerance` metres of its clamp into `bounds`. */
export function isSlotInsideBounds(bounds, anchor, lateral, slot, centre, spacing, tolerance = 0.2) {
  if (!bounds) return true;
  const s = (slot - centre) * spacing;
  const x = anchor.x + lateral.x * s, z = anchor.z + lateral.z * s;
  const cx = Math.min(bounds.maxX, Math.max(bounds.minX, x));
  const cz = Math.min(bounds.maxZ, Math.max(bounds.minZ, z));
  const ex = cx - x, ez = cz - z;
  return ex * ex + ez * ez <= tolerance * tolerance;
}

/**
 * Which slot the real player should start in so that the most of the formation line fits inside `bounds`
 * (ties -> the slot closest to the centre). `position` is the real player's feet.
 * @returns {number} slot index 0..slotCount-1
 */
export function chooseInitialSlot(bounds, position, lateral, slotCount, spacing, tolerance = 0.2) {
  const centre = (slotCount - 1) * 0.5;
  let best = Math.round(centre), bestInside = -1, bestCentreDistance = Infinity;
  const anchor = { x: 0, z: 0 };
  for (let k = 0; k < slotCount; k++) {
    anchor.x = position.x - lateral.x * ((k - centre) * spacing);
    anchor.z = position.z - lateral.z * ((k - centre) * spacing);
    let inside = 0;
    for (let s = 0; s < slotCount; s++) if (isSlotInsideBounds(bounds, anchor, lateral, s, centre, spacing, tolerance)) inside++;
    const cd = Math.abs(k - centre);
    if (inside > bestInside || (inside === bestInside && cd < bestCentreDistance)) {
      best = k; bestInside = inside; bestCentreDistance = cd;
    }
  }
  return best;
}

/**
 * Uniform "which body does an observer aim at" pick among `cloneCount` illusions + the real player.
 * @param {number} cloneCount
 * @param {number} r01 random 0..1
 * @returns {number} 0..cloneCount-1 = that illusion, cloneCount = the real player
 */
export function pickPerceivedIndex(cloneCount, r01) {
  if (cloneCount <= 0) return 0;
  const x = r01 < 0 ? 0 : r01 >= 1 ? 0.999999 : r01;
  return Math.min(cloneCount, Math.floor(x * (cloneCount + 1)));
}
