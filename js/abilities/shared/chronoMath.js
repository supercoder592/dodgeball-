// ---------------------------------------------------------------------------------------------------------------
// Chrono - pure time-manipulation math (no three.js, no game context): zone geometry, the ball-rewind decision table,
// Stasis Field target scoring and a few AI curves. Vectors are plain { x, y, z }. Unit-tested by chronoMath.test.js.
// ---------------------------------------------------------------------------------------------------------------

/** What a rewind does to one ball. */
export const BALL_REWIND = Object.freeze({
  SKIP: 'skip',                 // held / despawned: nothing to rewind (held balls travel with their holder)
  ERASE: 'erase',               // ability projectile conjured after the rewind point: it never existed back then
  UNDO_THROW: 'undoThrow',      // match ball thrown after the rewind point: back to where it was, as a free ball
  REWIND_FLIGHT: 'rewindFlight',// live ball whose flight predates the rewind point: recorded position + velocity
  TELEPORT: 'teleport',         // free / stasis ball: recorded position (stasis stays frozen there)
});

/** Tolerance (s) when comparing snapshot / launch timestamps. */
export const LAUNCH_EPSILON = 0.02;

/**
 * True when `p` lies inside the court-aligned square zone of side `size` centred on `center`, from `below` metres
 * under the centre up to `height` metres above it.
 */
export function insideZone(p, center, size, height, below = 1) {
  const half = size * 0.5;
  const dy = p.y - center.y;
  return Math.abs(p.x - center.x) <= half && Math.abs(p.z - center.z) <= half && dy >= -below && dy <= height;
}

/**
 * Bounds { minX, maxX, minZ, maxZ } of the whole playing area (both infield halves + both outfield strips, which are
 * `outfieldMargin` wider than the court) for a court-like object { width, length, outfieldDepth }.
 */
export function playAreaBounds(court, outfieldMargin = 1.5) {
  const halfW = (court && court.width ? court.width : 9) / 2;
  const halfL = (court && court.length ? court.length : 18) / 2;
  const depth = court && court.outfieldDepth != null ? court.outfieldDepth : 3;
  return { minX: -halfW - outfieldMargin, maxX: halfW + outfieldMargin, minZ: -halfL - depth, maxZ: halfL + depth };
}

/**
 * Planar zone centre: the aim point limited to `maxRange` metres from the caster (origin) and clamped into `area`.
 * Falls back to `fallbackDistance` metres along (fwdX, fwdZ) when the aim point is invalid (NaN / on the caster).
 * Writes out.x / out.z and returns out.
 */
export function resolveZoneCenter(originX, originZ, aimX, aimZ, fwdX, fwdZ, maxRange, area, fallbackDistance = 8, out = { x: 0, z: 0 }) {
  let dx = aimX - originX, dz = aimZ - originZ;
  if (!Number.isFinite(dx) || !Number.isFinite(dz) || dx * dx + dz * dz < 1e-4) {
    const fl = Math.hypot(fwdX, fwdZ) || 1;
    dx = (fwdX / fl) * fallbackDistance;
    dz = (fwdZ / fl) * fallbackDistance;
  }
  const d = Math.hypot(dx, dz);
  if (d > maxRange && d > 0) { dx *= maxRange / d; dz *= maxRange / d; }
  let x = originX + dx, z = originZ + dz;
  if (area) {
    x = Math.min(area.maxX, Math.max(area.minX, x));
    z = Math.min(area.maxZ, Math.max(area.minZ, z));
  }
  out.x = x; out.z = z;
  return out;
}

/**
 * Decides how a ball is rewound `secondsAgo` seconds at clock `now`. Whether a live ball's current flight already
 * existed at the rewind point is decided from its launch time (snapshot timestamps clamp to the oldest sample when
 * the history is shorter than the rewind, so they cannot answer that question).
 * @param {{state:string, launchTime:number, isAbilityBall:boolean, now:number, secondsAgo:number}} b
 * @returns {string} one of BALL_REWIND
 */
export function classifyBallRewind(b) {
  const state = b.state;
  if (state === 'held' || state === 'despawned' || !state) return BALL_REWIND.SKIP;
  if (state === 'live') {
    const rewindTime = b.now - Math.max(0, b.secondsAgo);
    const thrownAfter = Number.isFinite(b.launchTime) && b.launchTime > rewindTime + LAUNCH_EPSILON;
    if (!thrownAfter) return BALL_REWIND.REWIND_FLIGHT;
    return b.isAbilityBall ? BALL_REWIND.ERASE : BALL_REWIND.UNDO_THROW;
  }
  return BALL_REWIND.TELEPORT; // 'free' | 'stasis'
}

/** True when a snapshot taken at `snapTime` predates the throw launched at `launchTime`. */
export function snapshotPredatesLaunch(snapTime, launchTime, eps = LAUNCH_EPSILON) {
  return Number.isFinite(snapTime) && Number.isFinite(launchTime) && snapTime < launchTime - eps;
}

/**
 * Stasis Field candidate score (lower is better; Infinity = not eligible): aim angle (deg) + distance penalty -
 * threat preference. Balls threatening Chrono's team are always eligible (auto-save); others must be within the cone.
 */
export function stasisScore(angleDeg, distance, threat, maxAimAngle, distancePenaltyPerMetre, threatPreferenceDeg) {
  if (!threat && angleDeg > maxAimAngle) return Infinity;
  return angleDeg + distance * distancePenaltyPerMetre - (threat ? threatPreferenceDeg : 0);
}

/** 0..1 urgency of a threat arriving in `timeToThreat` seconds within a `lookAhead` horizon (1 = now). */
export function urgency(timeToThreat, lookAhead) {
  if (!Number.isFinite(timeToThreat)) return 0;
  return Math.min(1, Math.max(0, 1 - timeToThreat / Math.max(0.1, lookAhead)));
}

/** Heartbeat interval (s) that accelerates from `start` to `end` as the pending window progresses (0..1). */
export function heartbeatInterval(progress, start, end) {
  const k = Math.min(1, Math.max(0, progress));
  return start + (end - start) * k;
}
