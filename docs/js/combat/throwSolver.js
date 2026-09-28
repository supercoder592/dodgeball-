// ---------------------------------------------------------------------------------------------------------------
// Throw solving, trajectory prediction, aim assist and hit resolution (combat module).
//
//   finalSpeed(params)                 V = V_base * speedMul * (1 + 0.10 * rally), capped at 220 km/h
//   ThrowSolver.solve(params)          velocity + flight time: lead-aim a target's chest, else the aim point, else
//                                      straight along aimDir - always a low-arc ballistic under gravity*gravityScale
//   Trajectory.*                       closed-form ballistic queries (AI catch timing, Danger Sense, bots)
//   Targeting.*                        aim-assist cone / range / line-of-sight, target cycling
//   HitResolver.resolveHit             the single place a ball hit becomes damage: payload hooks -> Health ->
//                                      EV.BallHitPlayer (which feeds Juice: hitstop, shake, squash, flash)
// Pure math lives in throwMath.js / sweep.js (unit tested); this file adapts it to THREE.Vector3 and game state.
// ---------------------------------------------------------------------------------------------------------------
import * as THREE from 'three';
import { game } from '../game.js';
import { EV } from '../core/events.js';
import { GRAVITY, MS_TO_KMH, STANDARD_HIT_DAMAGE, BALL_RADIUS, ZONE } from '../core/constants.js';
import {
  computeFinalSpeed, solveLaunch, leadTarget, positionAt, firstTimeWithin, knockbackForSpeed, planarAngle,
  signedPlanarAngle,
} from './throwMath.js';
import { makeSweepHit, sweepSphereAABB, sweepSphereCapsuleY, sweepSpherePlaneY } from './sweep.js';

const DEG = Math.PI / 180;

/** Resolution tuning (ult gains are only granted here when no Match system grants them - see HitResolver). */
export const HIT_TUNING = Object.freeze({
  damage: STANDARD_HIT_DAMAGE,
  ultOnHit: 0.12,
  ultOnEliminate: 0.08,
  /** Upward share of the knockback direction for ability hits given as a scalar. */
  abilityKnockbackLift: 0.2,
});

/** Prediction tuning. */
export const PREDICT = Object.freeze({
  /** Sub-step of Trajectory.predictImpact (s); matches the 60 Hz simulation. */
  step: 1 / 60,
  /** Default look-ahead (s). */
  maxTime: 2,
});

// ------------------------------------------------------------------ module temporaries (no per-frame allocations)
const _sol = { x: 0, y: 0, z: 0, time: 0, ok: false, px: 0, py: 0, pz: 0 };
const _a = new THREE.Vector3();
const _b = new THREE.Vector3();
const _c = new THREE.Vector3();
const _p0 = new THREE.Vector3();
const _p1 = new THREE.Vector3();
const _hit = makeSweepHit();
const _zeroV = new THREE.Vector3();

// ------------------------------------------------------------------ player helpers (tolerant of partial players)
/** Capsule radius of a player (m). */
export const bodyRadiusOf = (p) => (p && p.radius > 0 ? p.radius : 0.3);
/** Standing height of a player (m). */
export const bodyHeightOf = (p) => (p && p.height > 0 ? p.height : 1.8);
/** Chest position of a player into `out`. */
export function chestOf(p, out) {
  const c = p && p.chestPosition;
  if (c && c.isVector3) return out.copy(c);
  return out.copy(p.position).setY(p.position.y + bodyHeightOf(p) * 0.72);
}
/** Can live enemy balls strike this player? (infield, alive, not mid-elimination) */
export function isTargetable(p) {
  if (!p) return false;
  if (typeof p.isTargetable === 'boolean') return p.isTargetable;
  if (p.zone === ZONE.OUTFIELD) return false;
  return !(p.health && p.health.isAlive === false);
}
/** Cloaked and not revealed: aim assist ignores it. */
export function isHiddenFromAim(p) {
  const s = p && p.status;
  return !!(s && s.has && s.has('cloaked') && !s.has('revealed'));
}

// =============================================================================================================
// Speed
// =============================================================================================================
/**
 * Release speed (m/s) of a throw: RallyMath.computeSpeed(baseSpeed * speedMul, rallyCount) - the Rally Boost
 * formula V = V_base * (1 + 0.10 * RallyCount) with the 220 km/h cap.
 * @param {object} params ThrowParams
 */
export function finalSpeed(params) {
  if (!params) return 0;
  return computeFinalSpeed(params.baseSpeed || 0, params.speedMul ?? 1, params.rallyCount | 0);
}

// =============================================================================================================
// ThrowSolver
// =============================================================================================================
export const ThrowSolver = {
  /**
   * Solves the release velocity for ThrowParams. Priority: a valid target (lead-aimed at the chest, planar lead),
   * else the aim point (crosshair world point, gravity compensated), else straight along aimDir.
   * @param {object} params ThrowParams
   * @param {{velocity?:THREE.Vector3}} [out]
   * @returns {{velocity:THREE.Vector3, flightTime:number, aimPoint:THREE.Vector3, ok:boolean}}
   */
  solve(params, out = {}) {
    const velocity = out.velocity && out.velocity.isVector3 ? out.velocity : new THREE.Vector3();
    const aim = out.aimPoint && out.aimPoint.isVector3 ? out.aimPoint : new THREE.Vector3();
    const speed = finalSpeed(params);
    const g = GRAVITY * (Number.isFinite(params.gravityScale) ? params.gravityScale : 1);
    const origin = params.origin || _zeroV;
    const target = params.target;
    let flightTime = Infinity, ok = false;

    if (target && target.position) {
      chestOf(target, _a);
      leadTarget(origin, _a, target.velocity || _zeroV, speed, g, _sol);
      velocity.set(_sol.x, _sol.y, _sol.z);
      aim.set(_sol.px, _sol.py, _sol.pz);
      flightTime = _sol.time; ok = _sol.ok;
    } else if (params.aimPoint && params.aimPoint.isVector3 && params.aimPoint.distanceToSquared(origin) > 1) {
      solveLaunch(origin, params.aimPoint, speed, g, _sol);
      velocity.set(_sol.x, _sol.y, _sol.z);
      aim.copy(params.aimPoint);
      flightTime = _sol.time; ok = _sol.ok;
    } else {
      // Straight along the aim direction (else the thrower's facing, else +Z).
      if (params.aimDir && params.aimDir.lengthSq() > 1e-8) _b.copy(params.aimDir);
      else if (params.thrower) _b.set(Math.sin(params.thrower.yaw || 0), 0, Math.cos(params.thrower.yaw || 0));
      else _b.set(0, 0, 1);
      _b.normalize();
      velocity.copy(_b).multiplyScalar(speed);
      aim.copy(origin).addScaledVector(_b, 12);
      flightTime = 12 / Math.max(1e-3, speed); ok = true;
    }
    if (!Number.isFinite(velocity.x) || !Number.isFinite(velocity.y) || !Number.isFinite(velocity.z)) velocity.set(0, 0, speed);
    out.velocity = velocity; out.aimPoint = aim; out.flightTime = flightTime; out.ok = ok;
    return out;
  },

  /**
   * Launch solution from `origin` toward `target` (a Player - lead-aimed at its chest - or a point) at `speed`
   * under `gravity` (m/s^2, already scaled).
   * @returns {{velocity:THREE.Vector3, flightTime:number, point:THREE.Vector3, ok:boolean}}
   */
  intercept(origin, target, speed, gravity = GRAVITY) {
    const velocity = new THREE.Vector3(), point = new THREE.Vector3();
    if (target && target.position && !target.isVector3) {
      chestOf(target, _a);
      leadTarget(origin, _a, target.velocity || _zeroV, speed, gravity, _sol);
      point.set(_sol.px, _sol.py, _sol.pz);
    } else if (target) {
      solveLaunch(origin, target, speed, gravity, _sol);
      point.copy(target);
    } else {
      return { velocity, flightTime: Infinity, point, ok: false };
    }
    velocity.set(_sol.x, _sol.y, _sol.z);
    return { velocity, flightTime: _sol.time, point, ok: _sol.ok };
  },
};

// =============================================================================================================
// Trajectory
// =============================================================================================================
/** Gravity (m/s^2) currently acting on a ball: scaled while live, full otherwise. */
function gravityOf(ball) {
  if (!ball) return GRAVITY;
  if (ball.state === 'live') return GRAVITY * (Number.isFinite(ball.gravityScale) ? ball.gravityScale : 1);
  if (ball.state === 'stasis' || ball.state === 'held' || ball.state === 'despawned') return 0;
  return GRAVITY;
}

export const Trajectory = {
  gravityOf,

  /** Ballistic position p + v t - 1/2 g t^2 into `out` (THREE.Vector3). */
  positionAt(p, v, g, t, out = new THREE.Vector3()) {
    return positionAt(p, v, g, t, out);
  },

  /**
   * First time (s from now) at which `ball`'s centre comes within `radius` of `point`, with that position.
   * @returns {{t:number, point:THREE.Vector3}|null}
   */
  timeToReach(ball, point, radius, maxTime = PREDICT.maxTime) {
    if (!ball || !point || ball.state === 'held' || ball.state === 'despawned') return null;
    const v = ball.state === 'stasis' ? _zeroV : ball.velocity;
    const g = gravityOf(ball);
    const t = firstTimeWithin(ball.position, v, g, point, radius, maxTime);
    if (t < 0) return null;
    return { t, point: positionAt(ball.position, v, g, t, new THREE.Vector3()) };
  },

  /**
   * When will `ball` touch `player`'s body capsule (same capsule + gravity model as the live hit test)? Stops at
   * the floor. Uses the player's current position (no player extrapolation).
   * @returns {{t:number, point:THREE.Vector3}|null} t in seconds from now, point = ball centre at contact
   */
  predictImpact(ball, player, maxTime = PREDICT.maxTime) {
    if (!ball || !player || !player.position) return null;
    if (ball.state !== 'live' && ball.state !== 'free') return null;
    const v = ball.velocity, g = gravityOf(ball);
    const r = ball.radius || BALL_RADIUS;
    const R = bodyRadiusOf(player), H = bodyHeightOf(player);
    const pos = player.position;
    const y0 = pos.y + R, y1 = pos.y + Math.max(R, H - R);
    const floorY = game.court ? game.court.floorY : 0;
    const step = PREDICT.step;
    _p0.copy(ball.position);
    for (let t = 0; t < maxTime; t += step) {
      positionAt(ball.position, v, g, t + step, _p1);
      if (sweepSphereCapsuleY(_p0, _p1, r, pos.x, pos.z, y0, y1, R, _hit)) {
        return { t: t + _hit.t * step, point: new THREE.Vector3(_hit.px, _hit.py, _hit.pz) };
      }
      if (sweepSpherePlaneY(_p0, _p1, r, floorY, _hit)) return null; // lands first
      _p0.copy(_p1);
    }
    return null;
  },
};

// =============================================================================================================
// Targeting (aim assist)
// =============================================================================================================
/** Segment a -> b is not blocked by an arena collider (walls, bleachers). */
export function hasLineOfSight(a, b) {
  const cols = game.arena && game.arena.colliders;
  if (!cols || !cols.length) return true;
  for (let i = 0; i < cols.length; i++) {
    const box = cols[i];
    if (!box || !box.min) continue;
    if (sweepSphereAABB(a, b, 0.02, box.min, box.max, _hit) && _hit.t > 0 && _hit.t < 0.999) return false;
  }
  return true;
}

export const Targeting = {
  /**
   * Best enemy inside the aim cone: smallest (angle / maxAngle + 0.35 * distance / maxDist), targetable, not
   * cloaked, in line of sight. The cone is measured in the horizontal plane (camera pitch does not matter).
   * @returns {object|null} Player
   */
  findBest(thrower, origin, dir, maxAngleDeg = 16, maxDist = 32) {
    if (!thrower || !origin || !dir) return null;
    const maxA = Math.max(1e-3, maxAngleDeg * DEG);
    let best = null, bestScore = Infinity;
    for (const p of game.players) {
      if (!game.areEnemies(thrower, p) || !isTargetable(p) || isHiddenFromAim(p)) continue;
      chestOf(p, _c);
      const dx = _c.x - origin.x, dz = _c.z - origin.z;
      const dist = Math.hypot(dx, _c.y - origin.y, dz);
      if (dist > maxDist) continue;
      const ang = planarAngle(dir.x, dir.z, dx, dz);
      if (ang > maxA) continue;
      if (!hasLineOfSight(origin, _c)) continue;
      const score = ang / maxA + 0.35 * dist / maxDist;
      if (score < bestScore) { bestScore = score; best = p; }
    }
    return best;
  },

  /**
   * Next enemy clockwise (to the right) from `current` around the aim direction; wraps. With no current target
   * the one closest to the aim direction.
   * @returns {object|null} Player
   */
  cycle(thrower, current, origin, dir, maxDist = 40) {
    if (!thrower || !origin || !dir) return null;
    const list = [];
    for (const p of game.players) {
      if (!game.areEnemies(thrower, p) || !isTargetable(p) || isHiddenFromAim(p)) continue;
      chestOf(p, _c);
      if (_c.distanceTo(origin) > maxDist) continue;
      list.push({ p, a: signedPlanarAngle(dir.x, dir.z, _c.x - origin.x, _c.z - origin.z) });
    }
    if (!list.length) return null;
    list.sort((x, y) => x.a - y.a);
    const i = list.findIndex((e) => e.p === current);
    if (i < 0) {
      let best = list[0];
      for (const e of list) if (Math.abs(e.a) < Math.abs(best.a)) best = e;
      return best.p;
    }
    return list[(i + 1) % list.length].p;
  },
};

// =============================================================================================================
// HitResolver
// =============================================================================================================
const LANDED = new Set(['damaged', 'eliminated', 'delayed']);

/** Does the running Match grant hit / elimination / catch ult itself (so we must not double it)? */
export function matchGrantsUlt() {
  const r = game.match && game.match.rules;
  return !!(r && typeof r.ultOnHit === 'number');
}

function safeCall(obj, fn, ...args) {
  if (!obj || typeof obj[fn] !== 'function') return undefined;
  try { return obj[fn](...args); } catch (e) { console.error(`[combat] payload.${fn} threw`, e); return undefined; }
}

export const HitResolver = {
  /**
   * A live ball struck `victim`'s body (the catch attempt already failed). Builds the hit, runs the payload's
   * onHitPlayer (returning false or setting hit.cancelled cancels it), Health.receiveHit (filters, shields, Chrono /
   * Specter interceptors, elimination + ragdoll), then publishes EV.BallHitPlayer for Juice / HUD / Audio / Match
   * and runs payload.onAfterHitPlayer.
   * Health applies the knockback impulse and hit reaction for 'damaged'; the ragdoll impulse for 'eliminated'.
   * @returns {string} outcome 'ignored'|'negated'|'damaged'|'eliminated'|'delayed'|'prevented'
   *          ('ignored' means the ball passes through, e.g. the victim is no longer targetable)
   */
  resolveHit(ball, victim, point, normal) {
    if (!ball || !victim) return 'ignored';
    const attacker = ball.lastThrower || null;
    const speed = ball.velocity.length();
    const speedKmh = speed * MS_TO_KMH;
    const hit = {
      ball, attacker, victim,
      point: point ? point.clone() : chestOf(victim, new THREE.Vector3()),
      normal: normal ? normal.clone() : new THREE.Vector3(0, 1, 0),
      velocity: ball.velocity.clone(),
      damage: HIT_TUNING.damage,
      unblockable: !!ball.unblockable,
      forceEliminate: false,
      knockback: knockbackForSpeed(speed),   // m/s body shove (Health applies it along the ball's travel)
      isAbility: !!(ball.throwParams ? ball.throwParams.isAbility : ball.isAbilityBall),
      cancelled: false,
      speedKmh,
    };

    const payload = ball.payload;
    const keep = safeCall(payload, 'onHitPlayer', ball, hit);
    let outcome;
    if (keep === false || hit.cancelled) {
      outcome = typeof hit.outcome === 'string' ? hit.outcome : 'negated';
    } else if (victim.health && typeof victim.health.receiveHit === 'function') {
      outcome = victim.health.receiveHit(hit) || 'ignored';
    } else {
      // No Health component (partial build): still read as a hit so the pipeline stays testable.
      outcome = isTargetable(victim) ? 'damaged' : 'ignored';
      if (outcome === 'damaged') {
        victim.motor?.addImpulse?.(_a.set(hit.velocity.x, 0, hit.velocity.z).normalize().multiplyScalar(hit.knockback));
        victim.avatar?.playHit?.(_a.clone());
      }
    }

    if (outcome === 'delayed') {
      // Chrono's Delayed Impact: the body still takes the blow (Health only reacts to non-lethal hits).
      _a.set(hit.velocity.x, 0, hit.velocity.z);
      if (_a.lengthSq() > 1e-6) {
        _a.normalize();
        victim.motor?.addImpulse?.(_b.copy(_a).multiplyScalar(hit.knockback || 0));
        victim.avatar?.playHit?.(_a.clone());
      }
    }

    if (LANDED.has(outcome) && attacker && attacker !== victim && game.areEnemies(attacker, victim) && !matchGrantsUlt()) {
      let gain = HIT_TUNING.ultOnHit;
      if (outcome === 'eliminated') gain += HIT_TUNING.ultOnEliminate;
      attacker.abilities?.addUltimateCharge?.(gain, 'hit');
    }

    game.events.emit(EV.BallHitPlayer, {
      ball, attacker, victim, point: hit.point, normal: hit.normal, velocity: hit.velocity, speedKmh,
      damage: hit.damage, outcome, local: !!((attacker && attacker.isLocal) || victim.isLocal),
      isAbility: hit.isAbility, unblockable: hit.unblockable,
    });
    safeCall(payload, 'onAfterHitPlayer', ball, hit, outcome);
    return outcome;
  },

  /**
   * Ability damage / shove without a ball (shockwaves, tackles, turret splash). `knockback` is a THREE.Vector3
   * velocity change or a scalar (m/s, directed from `point` toward the victim). Goes through Health so filters,
   * invulnerability and interceptors apply; damage 0 = pure shove.
   * @returns {string} outcome
   */
  resolveAbilityHit(attacker, victim, damage, point, knockback) {
    if (!victim) return 'ignored';
    const at = point && point.isVector3 ? point.clone() : chestOf(victim, new THREE.Vector3());
    const dir = new THREE.Vector3();
    let magnitude = 0;
    if (knockback && knockback.isVector3) {
      magnitude = knockback.length();
      if (magnitude > 1e-6) dir.copy(knockback).divideScalar(magnitude);
    } else if (Number.isFinite(knockback) && knockback > 0) {
      magnitude = knockback;
      dir.set(victim.position.x - at.x, 0, victim.position.z - at.z);
      if (dir.lengthSq() < 1e-6 && attacker && attacker.position) dir.set(victim.position.x - attacker.position.x, 0, victim.position.z - attacker.position.z);
      if (dir.lengthSq() < 1e-6) dir.set(-Math.sin(victim.yaw || 0), 0, -Math.cos(victim.yaw || 0));
      dir.normalize();
      dir.y = HIT_TUNING.abilityKnockbackLift;
      dir.normalize();
    }
    const hit = {
      ball: null, attacker: attacker || null, victim, point: at,
      normal: dir.lengthSq() > 0 ? dir.clone().negate() : new THREE.Vector3(0, 1, 0),
      velocity: dir.clone().multiplyScalar(Math.max(magnitude, 1e-3)),
      damage: Math.max(0, damage || 0), unblockable: true, forceEliminate: false,
      knockback: magnitude, isAbility: true, cancelled: false,
    };
    let outcome;
    if (victim.health && typeof victim.health.receiveHit === 'function') {
      outcome = victim.health.receiveHit(hit) || 'ignored';
    } else {
      outcome = isTargetable(victim) ? 'damaged' : 'ignored';
      if (outcome === 'damaged' && magnitude > 0) victim.motor?.addImpulse?.(dir.clone().multiplyScalar(magnitude));
    }
    if (outcome === 'delayed' && magnitude > 0) victim.motor?.addImpulse?.(dir.clone().multiplyScalar(magnitude));
    if (hit.damage > 0 && LANDED.has(outcome) && attacker && game.areEnemies(attacker, victim) && !matchGrantsUlt()) {
      attacker.abilities?.addUltimateCharge?.(HIT_TUNING.ultOnHit + (outcome === 'eliminated' ? HIT_TUNING.ultOnEliminate : 0), 'abilityHit');
    }
    return outcome;
  },
};
