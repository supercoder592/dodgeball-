// ---------------------------------------------------------------------------------------------------------------
// Swept-sphere collision math (pure, no three.js - unit tested by throwSolver.test.js).
//
// A ball moving from p0 to p1 during one fixed step is a sphere swept along a segment. Each routine returns the
// earliest time of impact t in [0, 1] (fraction of the segment) and the contact normal, written into a reusable
// hit record { t, px, py, pz, nx, ny, nz } where (px, py, pz) is the BALL CENTRE at contact and n points from the
// obstacle toward the ball. A sphere that already overlaps the obstacle at p0 reports t = 0 with the push-out normal.
//
// Vectors are any objects with numeric x, y, z (THREE.Vector3 works, so do plain literals).
// ---------------------------------------------------------------------------------------------------------------

const EPS = 1e-9;
const _d = new Float64Array(3), _o = new Float64Array(3), _lo = new Float64Array(3), _hi = new Float64Array(3);

/** Reusable hit record factory. */
export function makeSweepHit() {
  return { t: 0, px: 0, py: 0, pz: 0, nx: 0, ny: 1, nz: 0 };
}

function writeHit(out, t, p0, dx, dy, dz, nx, ny, nz) {
  out.t = t;
  out.px = p0.x + dx * t; out.py = p0.y + dy * t; out.pz = p0.z + dz * t;
  out.nx = nx; out.ny = ny; out.nz = nz;
  return true;
}

/**
 * First entry time of a ray o + d*t (t in [0,1]) into a sphere of radius R centred at the origin of (ox, oy, oz)
 * (o is given RELATIVE to the sphere centre). Returns -1 when missed or when starting inside.
 */
function rayEnterSphere(ox, oy, oz, dx, dy, dz, R2) {
  const a = dx * dx + dy * dy + dz * dz;
  if (a < EPS) return -1;
  const b = 2 * (ox * dx + oy * dy + oz * dz);
  const c = ox * ox + oy * oy + oz * oz - R2;
  if (c <= 0) return -1;            // inside: handled by the callers' overlap test
  if (b >= 0) return -1;            // moving away
  const disc = b * b - 4 * a * c;
  if (disc < 0) return -1;
  const t = (-b - Math.sqrt(disc)) / (2 * a);
  return t >= 0 && t <= 1 ? t : -1;
}

/**
 * Sphere (radius r) swept p0 -> p1 against a VERTICAL capsule: axis x = cx, z = cz from y = yBottom to y = yTop
 * (the centres of its hemispherical caps), radius capR. Player bodies are modelled this way
 * (feet + radius .. head - radius).
 * @returns {boolean} hit (details in out)
 */
export function sweepSphereCapsuleY(p0, p1, r, cx, cz, yBottom, yTop, capR, out) {
  if (yTop < yBottom) yTop = yBottom;
  const R = r + capR, R2 = R * R;
  const dx = p1.x - p0.x, dy = p1.y - p0.y, dz = p1.z - p0.z;
  const ox = p0.x - cx, oz = p0.z - cz;

  // Already overlapping at the start: report t = 0 with the push-out normal.
  const ay0 = p0.y < yBottom ? yBottom : p0.y > yTop ? yTop : p0.y;
  const sy = p0.y - ay0;
  const d2 = ox * ox + sy * sy + oz * oz;
  if (d2 <= R2) {
    const len = Math.sqrt(d2);
    if (len > 1e-6) return writeHit(out, 0, p0, dx, dy, dz, ox / len, sy / len, oz / len);
    // Dead centre: push back against the motion (planar), else up.
    const pl = Math.hypot(dx, dz);
    return pl > 1e-6 ? writeHit(out, 0, p0, dx, dy, dz, -dx / pl, 0, -dz / pl) : writeHit(out, 0, p0, dx, dy, dz, 0, 1, 0);
  }

  let best = 2, nx = 0, ny = 0, nz = 0;

  // Cylinder wall (infinite vertical cylinder, then clipped to the axis span).
  const a = dx * dx + dz * dz;
  if (a > EPS) {
    const b = 2 * (ox * dx + oz * dz);
    const c = ox * ox + oz * oz - R2;
    const disc = b * b - 4 * a * c;
    if (disc >= 0 && b < 0) {
      const t = (-b - Math.sqrt(disc)) / (2 * a);
      if (t >= 0 && t <= 1) {
        const y = p0.y + dy * t;
        if (y >= yBottom && y <= yTop) {
          best = t;
          nx = (ox + dx * t) / R; ny = 0; nz = (oz + dz * t) / R;
        }
      }
    }
  }

  // Hemispherical caps.
  for (let k = 0; k < 2; k++) {
    const capY = k === 0 ? yBottom : yTop;
    const t = rayEnterSphere(ox, p0.y - capY, oz, dx, dy, dz, R2);
    if (t < 0 || t >= best) continue;
    const y = p0.y + dy * t;
    if (yTop > yBottom && (k === 0 ? y > yBottom : y < yTop)) continue; // that part belongs to the cylinder
    best = t;
    nx = (ox + dx * t) / R; ny = (y - capY) / R; nz = (oz + dz * t) / R;
  }

  if (best > 1) return false;
  return writeHit(out, best, p0, dx, dy, dz, nx, ny, nz);
}

/**
 * Sphere (radius r) swept p0 -> p1 against a static sphere (centre c, radius R) - used for held balls and catch
 * reach volumes.
 * @returns {boolean}
 */
export function sweepSphereSphere(p0, p1, r, c, R, out) {
  const Rs = r + R, R2 = Rs * Rs;
  const dx = p1.x - p0.x, dy = p1.y - p0.y, dz = p1.z - p0.z;
  const ox = p0.x - c.x, oy = p0.y - c.y, oz = p0.z - c.z;
  const d2 = ox * ox + oy * oy + oz * oz;
  if (d2 <= R2) {
    const len = Math.sqrt(d2);
    return len > 1e-6 ? writeHit(out, 0, p0, dx, dy, dz, ox / len, oy / len, oz / len) : writeHit(out, 0, p0, dx, dy, dz, 0, 1, 0);
  }
  const t = rayEnterSphere(ox, oy, oz, dx, dy, dz, R2);
  if (t < 0) return false;
  return writeHit(out, t, p0, dx, dy, dz, (ox + dx * t) / Rs, (oy + dy * t) / Rs, (oz + dz * t) / Rs);
}

/**
 * Sphere (radius r) swept p0 -> p1 against an axis-aligned box [min, max] (Minkowski-expanded by r; the rounded
 * edges are approximated by the expanded box, which is standard and invisible at ball scale).
 * @returns {boolean}
 */
export function sweepSphereAABB(p0, p1, r, min, max, out) {
  // Module-scope scratch (allocation-free hot path: every ball x every collider x every fixed step).
  const d = _d, o = _o, lo = _lo, hi = _hi;
  d[0] = p1.x - p0.x; d[1] = p1.y - p0.y; d[2] = p1.z - p0.z;
  o[0] = p0.x; o[1] = p0.y; o[2] = p0.z;
  lo[0] = min.x - r; lo[1] = min.y - r; lo[2] = min.z - r;
  hi[0] = max.x + r; hi[1] = max.y + r; hi[2] = max.z + r;
  let tEnter = -Infinity, tExit = Infinity, axis = -1, sign = 0;
  for (let k = 0; k < 3; k++) {
    if (Math.abs(d[k]) < EPS) {
      if (o[k] < lo[k] || o[k] > hi[k]) return false;
      continue;
    }
    let t1 = (lo[k] - o[k]) / d[k], t2 = (hi[k] - o[k]) / d[k], s = -1;
    if (t1 > t2) { const tmp = t1; t1 = t2; t2 = tmp; s = 1; }
    if (t1 > tEnter) { tEnter = t1; axis = k; sign = s; }
    if (t2 < tExit) tExit = t2;
    if (tEnter > tExit) return false;
  }
  if (tExit < 0 || tEnter > 1) return false;
  if (tEnter < 0 || axis < 0) {
    // Started inside the expanded box: push out through the face of least penetration.
    let bestPen = Infinity, bk = 1, bs = 1;
    for (let k = 0; k < 3; k++) {
      const penLo = o[k] - lo[k], penHi = hi[k] - o[k];
      if (penLo < bestPen) { bestPen = penLo; bk = k; bs = -1; }
      if (penHi < bestPen) { bestPen = penHi; bk = k; bs = 1; }
    }
    return writeHit(out, 0, p0, d[0], d[1], d[2], bk === 0 ? bs : 0, bk === 1 ? bs : 0, bk === 2 ? bs : 0);
  }
  return writeHit(out, tEnter, p0, d[0], d[1], d[2], axis === 0 ? sign : 0, axis === 1 ? sign : 0, axis === 2 ? sign : 0);
}

/**
 * Sphere (radius r) swept p0 -> p1 against the horizontal plane y = planeY (the court floor), from above.
 * A sphere already sinking below the plane reports t = 0 only while it is still moving down (a resting ball never
 * re-triggers).
 * @returns {boolean}
 */
export function sweepSpherePlaneY(p0, p1, r, planeY, out) {
  const a0 = p0.y - r - planeY, a1 = p1.y - r - planeY;
  const dx = p1.x - p0.x, dy = p1.y - p0.y, dz = p1.z - p0.z;
  if (a0 < 0) {
    if (dy >= 0) return false;
    return writeHit(out, 0, p0, dx, dy, dz, 0, 1, 0);
  }
  if (a1 >= 0 || a0 === a1) return false;
  return writeHit(out, a0 / (a0 - a1), p0, dx, dy, dz, 0, 1, 0);
}

/**
 * Closest distance between point q and the vertical segment (cx, y0..y1, cz). Handy for overlap tests.
 * @returns {number}
 */
export function distanceToVerticalSegment(q, cx, cz, y0, y1) {
  const y = q.y < y0 ? y0 : q.y > y1 ? y1 : q.y;
  return Math.hypot(q.x - cx, q.y - y, q.z - cz);
}

/**
 * Reflects velocity v (mutated, any {x,y,z}) off a surface with unit normal n: the normal component is reversed and
 * scaled by `restitution`, the tangential component scaled by `tangentKeep` (friction). No-op when separating.
 * @returns {number} the approach speed along the normal (>= 0), i.e. the impact speed
 */
export function bounceVelocity(v, n, restitution, tangentKeep) {
  const vn = v.x * n.x + v.y * n.y + v.z * n.z;
  if (vn >= 0) return 0;
  const tx = v.x - vn * n.x, ty = v.y - vn * n.y, tz = v.z - vn * n.z;
  v.x = tx * tangentKeep - vn * restitution * n.x;
  v.y = ty * tangentKeep - vn * restitution * n.y;
  v.z = tz * tangentKeep - vn * restitution * n.z;
  return -vn;
}
