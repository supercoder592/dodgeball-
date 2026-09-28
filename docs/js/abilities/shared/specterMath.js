// ---------------------------------------------------------------------------------------------------------------
// Specter - pure evasion math (no three.js, no game context) shared by Danger Sense, Precognition Dodge and the
// Time Reversal AI. Vectors are any objects with numeric { x, y, z } (THREE.Vector3 works); nothing here allocates
// except where an `out` object is omitted. Unit-tested by specterMath.test.js (node:test).
// ---------------------------------------------------------------------------------------------------------------

/** A dodger this close (m, planar) to a ball's flight line counts as "on the line": either side is fine. */
export const ON_LINE_DISTANCE = 0.35;
/** Fraction of the probe distance below which the preferred side counts as walled in by the court line. */
export const WALLED_IN_FRACTION = 0.3;
/** How much roomier (m) the other side must be before the dodge crosses the flight line. */
export const CROSS_LINE_MARGIN = 0.25;

/**
 * Straight-line closest approach of a point moving with `vel` from `pos` to the fixed `point`.
 * Writes `out.time` (s, >= 0) and `out.distance` (m).
 * @returns {boolean} false when the mover is (almost) static or already moving away from the point.
 */
export function closestApproach(pos, vel, point, out) {
  const rx = point.x - pos.x, ry = point.y - pos.y, rz = point.z - pos.z;
  const v2 = vel.x * vel.x + vel.y * vel.y + vel.z * vel.z;
  if (v2 < 1e-4) { out.time = 0; out.distance = Math.hypot(rx, ry, rz); return false; }
  const t = (rx * vel.x + ry * vel.y + rz * vel.z) / v2;
  if (t < 0) { out.time = 0; out.distance = Math.hypot(rx, ry, rz); return false; }
  const dx = pos.x + vel.x * t - point.x, dy = pos.y + vel.y * t - point.y, dz = pos.z + vel.z * t - point.z;
  out.time = t;
  out.distance = Math.hypot(dx, dy, dz);
  return true;
}

/**
 * Planar distance (m, capped at `maxDistance`) available from (x, z) along the planar direction (dirX, dirZ) before
 * leaving `bounds` ({ minX, maxX, minZ, maxZ }). A null bounds means "open floor" and returns the cap.
 */
export function roomAlong(bounds, x, z, dirX, dirZ, maxDistance) {
  if (!bounds) return maxDistance;
  let t = maxDistance;
  if (dirX > 1e-4) t = Math.min(t, (bounds.maxX - x) / dirX);
  else if (dirX < -1e-4) t = Math.min(t, (bounds.minX - x) / dirX);
  if (dirZ > 1e-4) t = Math.min(t, (bounds.maxZ - z) / dirZ);
  else if (dirZ < -1e-4) t = Math.min(t, (bounds.minZ - z) / dirZ);
  return Math.max(0, t);
}

/**
 * Unit planar side-step direction that takes a dodger at (x, z) out of the flight line of a ball travelling along
 * (travelX, travelZ). (offsetX, offsetZ) = dodger - ball on the court plane. The dodge keeps to the side the dodger is
 * already offset to (never crossing the ball's path) unless the dodger is almost on the line or walled in by the
 * court confinement, in which case the clearly roomier side wins. A (near) static ball is treated as coming straight
 * at the dodger. Writes `out.x`, `out.z` and returns `out`.
 */
export function evadeDirection(travelX, travelZ, offsetX, offsetZ, bounds, x, z, probe, out = { x: 0, z: 0 }) {
  let len = Math.hypot(travelX, travelZ);
  if (len < 1e-3) { travelX = -offsetX; travelZ = -offsetZ; len = Math.hypot(travelX, travelZ); }
  if (len < 1e-3) { travelX = 0; travelZ = 1; len = 1; }
  const tx = travelX / len, tz = travelZ / len;
  // Right-hand perpendicular on the court plane: cross(up, travel) = (tz, 0, -tx).
  const px = tz, pz = -tx;
  const side = offsetX * px + offsetZ * pz;
  const sgn = side >= 0 ? 1 : -1;
  const roomPrimary = roomAlong(bounds, x, z, px * sgn, pz * sgn, probe);
  const roomSecondary = roomAlong(bounds, x, z, -px * sgn, -pz * sgn, probe);
  const nearlyOnLine = Math.abs(side) < ON_LINE_DISTANCE;
  const walledIn = roomPrimary < probe * WALLED_IN_FRACTION;
  const s = (nearlyOnLine || walledIn) && roomSecondary > roomPrimary + CROSS_LINE_MARGIN ? -sgn : sgn;
  out.x = px * s;
  out.z = pz * s;
  return out;
}

/**
 * Bot utility (0..1) of Precognition Dodge against the incoming ball.
 * o: { weight, timeToImpact, speedKmh, unblockable, holdingBall, invulnerable, dodgeDisabled, reactionWindow,
 *      preferCatchBelowKmh }
 */
export function dodgeUtility(o) {
  const t = o.timeToImpact;
  if (!(t >= 0.02) || t > o.reactionWindow) return 0;
  if (o.invulnerable || o.dodgeDisabled) return 0;
  const speedFactor = Math.min(1, Math.max(0, ((o.speedKmh || 0) - 60) / 100));
  let u = (o.weight ?? 1) * (0.55 + 0.45 * speedFactor);
  if (o.unblockable) u *= 0.5; // the filter cannot negate it; only the slide itself may get us clear
  if (!o.holdingBall && (o.speedKmh || 0) < o.preferCatchBelowKmh) u *= 0.5; // a (perfect) catch is the better answer
  return Math.min(1, Math.max(0, u));
}

/**
 * Bot utility (0..1) of Time Reversal (insurance ultimate): cast when a ball is about to arrive or an enemy nearby is
 * winding up; halved while the cheaper Precognition Dodge is ready. o: { weight, invulnerable, targetable,
 * incomingTime (s or Infinity), threatWindow, enemyCharging, enemyDistance, chargingRange, dodgeReady }
 */
export function reversalUtility(o) {
  if (o.invulnerable || o.targetable === false) return 0;
  const w = o.weight ?? 1;
  const factor = o.dodgeReady ? 0.5 : 1;
  if (o.incomingTime <= o.threatWindow) return Math.min(1, w * factor);
  if (o.enemyCharging && o.enemyDistance <= o.chargingRange) return Math.min(1, w * 0.8 * factor);
  return 0;
}
