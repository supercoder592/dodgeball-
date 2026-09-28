// ---------------------------------------------------------------------------------------------------------------
// Chrono - runtime time-manipulation helpers shared by Stasis Field and Temporal Reset (+ guarded presentation).
// Relies only on contract members: game.players / game.balls.active, Player.history / Ball.history
// (RewindHistory.sample), Player.teleport, Motor.setPlanarVelocity, Ball.teleport / makeFree, BallManager.recycle.
// Threat checks are allocation-free; rewinds allocate a couple of vectors per rewound entity (ultimate, rare).
// ---------------------------------------------------------------------------------------------------------------
import * as THREE from 'three';
import { game } from '../../game.js';
import { GRAVITY } from '../../core/constants.js';
import { clampToPlayerZone } from '../abilityUtil.js';
import { BALL_REWIND, classifyBallRewind, snapshotPredatesLaunch } from './chronoMath.js';

/** Result of a rewind on one entity (VFX / diagnostics). */
export const REWIND_RESULT = Object.freeze({ SKIPPED: 'skipped', REWOUND: 'rewound', THROW_UNDONE: 'throwUndone', ERASED: 'erased' });

/** Sampling step (s) of the gravity-aware threat look-ahead. */
const THREAT_STEP = 0.05;
/** Body approximated as a vertical segment from knee height to just below the crown (m above the feet). */
const BODY_BOTTOM = 0.35, BODY_TOP_FROM_HEIGHT = 0.1, CHEST_HEIGHT = 1.3, DEFAULT_HEIGHT = 1.8;
/** Incapacitation reasons whose bodies are owned by another system and must not be teleported by a rewind. */
const NO_REWIND_INCAP = new Set(['eliminated', 'grabbed', 'round', 'teleporting']);

const _zero = new THREE.Vector3();
const _planar = new THREE.Vector3();
/** Reused result of findMostThreateningEnemyBall. */
const _found = { ball: null, time: Infinity };

/** True when `player`'s state machine is Incapacitated for one of `reasons` (a Set of incapReason strings). */
export function isIncapacitatedBy(player, reasons) {
  const fsm = player && player.fsm;
  if (!fsm) return false;
  const incap = typeof fsm.is === 'function' ? fsm.is('incapacitated') : fsm.current === 'incapacitated';
  return incap && reasons.has(fsm.incapReason);
}

/** True when `ball` was last thrown by an enemy of `owner`. */
export function isEnemyBall(ball, owner) {
  return !!ball && !!ball.lastThrower && game.areEnemies(ball.lastThrower, owner);
}

/** Gravity magnitude (m/s²) acting on `ball`. */
export function ballGravity(ball) {
  const s = ball && Number.isFinite(ball.gravityScale) ? ball.gravityScale : 1;
  return GRAVITY * Math.max(0, s);
}

/**
 * Seconds until the live `ball` endangers a targetable member of `team` (Infinity when it does not): its
 * gravity-aware flight over the next `lookAhead` seconds passes within `radius` of a body, or it is soft-locked on
 * one of them (then distance / speed).
 */
export function threatTimeForTeam(ball, team, lookAhead, radius) {
  if (!ball || ball.state !== 'live' || !ball.position || !ball.velocity) return Infinity;
  const p = ball.position, v = ball.velocity, g = ballGravity(ball);
  const r2 = radius * radius;
  let best = Infinity;
  for (const pl of game.players) {
    if (!pl || pl.team !== team || !pl.isTargetable) continue;
    const feet = pl.position;
    const bottom = feet.y + BODY_BOTTOM, top = feet.y + (pl.height || DEFAULT_HEIGHT) - BODY_TOP_FROM_HEIGHT;
    let hit = Infinity;
    for (let t = 0; t <= lookAhead && t < best; t += THREAT_STEP) {
      const y = p.y + v.y * t - 0.5 * g * t * t;
      const dy = y < bottom ? bottom - y : (y > top ? y - top : 0);
      const dx = p.x + v.x * t - feet.x, dz = p.z + v.z * t - feet.z;
      if (dx * dx + dy * dy + dz * dz <= r2) { hit = t; break; }
    }
    if (hit === Infinity && ball.lockedTarget === pl) {
      // A soft-locked throw counts even when the look-ahead is too short to reach the victim.
      const dx = feet.x - p.x, dy = feet.y + CHEST_HEIGHT - p.y, dz = feet.z - p.z;
      hit = Math.hypot(dx, dy, dz) / Math.max(1, v.length());
    }
    if (hit < best) best = hit;
  }
  return best;
}

/**
 * The live enemy ball (relative to `owner`) within `range` of `origin` that reaches one of `owner`'s team soonest,
 * ignoring threats closer than `minTime` seconds (too late to react). Returns a REUSED { ball, time }.
 */
export function findMostThreateningEnemyBall(owner, origin, range, lookAhead, radius, minTime = 0) {
  const out = _found;
  out.ball = null; out.time = Infinity;
  const list = game.balls && game.balls.active;
  if (!owner || !Array.isArray(list)) return out;
  const range2 = range * range;
  for (let i = 0; i < list.length; i++) {
    const ball = list[i];
    if (!ball || ball.state !== 'live' || !isEnemyBall(ball, owner)) continue;
    if (ball.position.distanceToSquared(origin) > range2) continue;
    const t = threatTimeForTeam(ball, owner.team, lookAhead, radius);
    if (t < minTime || t >= out.time) continue;
    out.time = t; out.ball = ball;
  }
  return out;
}

/**
 * Moves `player` back to where its history saw it `secondsAgo` seconds before `now`, clamped to the player's CURRENT
 * zone (rewinds never cross the centre line or leave the outfield), with its recorded yaw and (optionally) planar
 * momentum. Ragdolling / grabbed / round-transition bodies and players without history are skipped.
 * `fromOut` / `toOut` (Vector3) receive the old and new feet positions.
 */
export function rewindPlayer(player, now, secondsAgo, restoreVelocity, fromOut, toOut) {
  fromOut.copy(player.position); toOut.copy(player.position);
  const history = player.history;
  if (!history || typeof history.sample !== 'function') return REWIND_RESULT.SKIPPED;
  if (player.health && player.health.isEliminated) return REWIND_RESULT.SKIPPED;
  if (isIncapacitatedBy(player, NO_REWIND_INCAP)) return REWIND_RESULT.SKIPPED;
  const snap = history.sample(now, secondsAgo);
  if (!snap) return REWIND_RESULT.SKIPPED;
  const to = clampToPlayerZone(player, snap.position);
  const floorY = game.court ? game.court.floorY : 0;
  if (to.y < floorY) to.y = floorY;
  player.teleport(to, Number.isFinite(snap.yaw) ? snap.yaw : player.yaw);
  if (player.motor) {
    if (restoreVelocity && snap.velocity) _planar.set(snap.velocity.x, 0, snap.velocity.z);
    else _planar.set(0, 0, 0);
    player.motor.setPlanarVelocity(_planar);
  }
  toOut.copy(to);
  return REWIND_RESULT.REWOUND;
}

/**
 * Restores `ball` to its state `secondsAgo` seconds before `now` (see chronoMath.classifyBallRewind):
 *  - live ball launched before the rewind point: recorded position + velocity, still live (trajectory rewound);
 *  - live match ball launched after it: the throw is undone - a free ball (rally reset) where it was back then (or
 *    at the earliest recorded point of this flight when the history does not reach that far);
 *  - live ability projectile launched after it: recycled (it never existed back then);
 *  - free / stasis ball: teleported to the recorded position (stasis keeps it frozen there).
 * `fromOut` / `toOut` (Vector3) receive the old and new positions.
 */
export function rewindBall(ball, now, secondsAgo, fromOut, toOut) {
  fromOut.copy(ball.position); toOut.copy(ball.position);
  const state = ball.state;
  const decision = classifyBallRewind({ state, launchTime: ball.launchTime, isAbilityBall: !!ball.isAbilityBall, now, secondsAgo });
  if (decision === BALL_REWIND.SKIP) return REWIND_RESULT.SKIPPED;
  if (decision === BALL_REWIND.ERASE) {
    if (game.balls && typeof game.balls.recycle === 'function') game.balls.recycle(ball);
    else ball.makeFree(_zero, true);
    return REWIND_RESULT.ERASED;
  }
  const history = ball.history;
  if (!history || typeof history.sample !== 'function') return REWIND_RESULT.SKIPPED;
  const snap = history.sample(now, secondsAgo);
  if (!snap || snap.state === 'despawned') return REWIND_RESULT.SKIPPED;

  switch (decision) {
    case BALL_REWIND.REWIND_FLIGHT:
      ball.teleport(snap.position, snap.velocity);
      toOut.copy(snap.position);
      return REWIND_RESULT.REWOUND;
    case BALL_REWIND.UNDO_THROW: {
      // Only a snapshot from before the release says where the ball was; a resting/rolling ball keeps its slow
      // recorded motion, one that was in a hand (or in an older flight) simply drops there - a free ball flying at
      // throw speed would read as a phantom throw.
      const predates = snapshotPredatesLaunch(snap.t, ball.launchTime);
      const vel = predates && snap.state === 'free' ? snap.velocity : _zero;
      ball.makeFree(vel, true);
      ball.teleport(snap.position, vel);
      toOut.copy(snap.position);
      return REWIND_RESULT.THROW_UNDONE;
    }
    default: // TELEPORT
      ball.teleport(snap.position, state === 'stasis' ? _zero : snap.velocity);
      toOut.copy(snap.position);
      return REWIND_RESULT.REWOUND;
  }
}

// ------------------------------------------------------------------ guarded presentation (never breaks gameplay)
const _warned = new Set();
function warnOnce(key, e) {
  if (_warned.has(key)) return;
  _warned.add(key);
  console.warn(`[chrono] ${key} failed`, e);
}

/** Cosmetic calls into optional systems (vfx/audio/renderer/avatar): missing systems are skipped, failures warned once. */
export const fx = {
  play(id, position, opts) { try { game.vfx?.play?.(id, position, opts); } catch (e) { warnOnce(`vfx.play(${id})`, e); } },
  attach(id, object3D, opts) {
    try { return object3D ? (game.vfx?.attach?.(id, object3D, opts) ?? null) : null; } catch (e) { warnOnce(`vfx.attach(${id})`, e); return null; }
  },
  stop(handle) { if (handle == null) return; try { game.vfx?.stop?.(handle); } catch (e) { warnOnce('vfx.stop', e); } },
  sfx(id, position, volume = 1, pitch = 1) { try { game.audio?.play?.(id, position, volume, pitch); } catch (e) { warnOnce(`audio.play(${id})`, e); } },
  pulse(type, intensity, duration) { try { game.renderer?.pulse?.(type, intensity, duration); } catch (e) { warnOnce(`renderer.pulse(${type})`, e); } },
  flash(player, color, duration) { try { player?.avatar?.flash?.(color, duration); } catch (e) { warnOnce('avatar.flash', e); } },
  tint(player, color, amount) { try { player?.avatar?.setTint?.(color, amount); } catch (e) { warnOnce('avatar.setTint', e); } },
  fovKick(deg, duration) { try { game.cameraRig?.addFovKick?.(deg, duration); } catch (e) { warnOnce('cameraRig.addFovKick', e); } },
};
