// Pure rules math (no three.js) - unit tested with `node --test docs/js/core/`.
import {
  MAX_BALL_SPEED_MS, RALLY_BOOST_PER_COUNT, PERFECT_CATCH_WINDOW, NORMAL_CATCH_WINDOW,
} from './constants.js';

/** Rally Boost: V = V_base * (1 + 0.10 * RallyCount), capped at 220 km/h. */
export const RallyMath = {
  multiplier(rallyCount, boostPerCount = RALLY_BOOST_PER_COUNT) {
    return 1 + boostPerCount * Math.max(0, rallyCount | 0);
  },
  computeSpeed(baseSpeed, rallyCount, cap = MAX_BALL_SPEED_MS, boostPerCount = RALLY_BOOST_PER_COUNT) {
    if (!(baseSpeed > 0)) return 0;
    return Math.min(baseSpeed * RallyMath.multiplier(rallyCount, boostPerCount), cap);
  },
  clampToCap(speed, cap = MAX_BALL_SPEED_MS) {
    return Math.max(0, Math.min(speed, cap));
  },
  next(rallyCount) {
    return rallyCount < 0 ? 1 : rallyCount + 1;
  },
};

export const CatchQuality = Object.freeze({ MISS: 'miss', NORMAL: 'normal', PERFECT: 'perfect' });

/** Perfect Catch <=> 0 <= t_input <= 0.15 s before impact, where t = impactTime - inputTime. */
export const CatchTiming = {
  classify(secondsBeforeImpact, perfectWindow = PERFECT_CATCH_WINDOW, catchWindow = NORMAL_CATCH_WINDOW) {
    if (secondsBeforeImpact < 0) return CatchQuality.MISS;
    if (secondsBeforeImpact <= perfectWindow) return CatchQuality.PERFECT;
    if (secondsBeforeImpact <= Math.max(catchWindow, perfectWindow)) return CatchQuality.NORMAL;
    return CatchQuality.MISS;
  },
  scaledPerfectWindow(multiplier, base = PERFECT_CATCH_WINDOW) {
    return base * (multiplier > 0 ? multiplier : 1);
  },
};

/** Scalar ballistics (vertical plane through launch point and target). */
export const Ballistics = {
  /**
   * Launch elevation (radians) so a projectile at `speed` passes a point `dx` away horizontally and `dy` higher.
   * tan(t) = (v^2 -/+ sqrt(v^4 - g(g x^2 + 2 y v^2))) / (g x). Returns { ok, angle } (angle = 45deg fallback when out of range).
   */
  solveAngle(speed, dx, dy, gravity, lowArc = true) {
    if (!(speed > 0)) return { ok: false, angle: 0 };
    if (dx < 1e-4) return { ok: true, angle: dy >= 0 ? Math.PI / 2 : -Math.PI / 2 };
    if (gravity <= 1e-6) return { ok: true, angle: Math.atan2(dy, dx) };
    const v2 = speed * speed;
    const disc = v2 * v2 - gravity * (gravity * dx * dx + 2 * dy * v2);
    if (disc < 0) return { ok: false, angle: Math.PI / 4 };
    const root = Math.sqrt(disc);
    const tan = lowArc ? (v2 - root) / (gravity * dx) : (v2 + root) / (gravity * dx);
    return { ok: true, angle: Math.atan(tan) };
  },
  flightTime(speed, angle, dx) {
    const h = speed * Math.cos(angle);
    return h < 1e-5 ? Infinity : dx / h;
  },
  maxRange(speed, gravity) {
    return gravity <= 1e-6 ? Infinity : (speed * speed) / gravity;
  },
};
