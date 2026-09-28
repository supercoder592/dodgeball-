// ---------------------------------------------------------------------------------------------------------------
// Specter - runtime threat queries + guarded presentation helpers shared by Danger Sense, Precognition Dodge and
// Time Reversal. Allocation-free in the hot paths (module-scope temporaries, one reused result object).
//
// Impact prediction goes through the combat module's Trajectory.predictImpact (gravity-aware, knows the exact ball
// physics). The namespace import tolerates a combat build without that export: a local sampler over the same
// ballistic model (g * ball.gravityScale) vs. the player capsule is used instead.
// ---------------------------------------------------------------------------------------------------------------
import * as THREE from 'three';
import { game } from '../../game.js';
import { GRAVITY, BALL_RADIUS } from '../../core/constants.js';
import * as ThrowSolver from '../../combat/throwSolver.js';
import { closestApproach, evadeDirection } from './specterMath.js';

/** A ball the predictor says will miss still counts as a threat to side-step when it passes this close (m). */
export const NEAR_MISS_RADIUS = 2.5;
/** Fallback sampler step (s). */
const SAMPLE_STEP = 0.02;
/** Capsule defaults when a player does not expose radius / height. */
const DEFAULT_RADIUS = 0.35, DEFAULT_HEIGHT = 1.8;
/** Chest height above the feet (m) used for near-miss checks (avoids the allocating chestPosition getter). */
const CHEST_HEIGHT = 1.3;

const _incoming = [];
const _chest = new THREE.Vector3();
const _approach = { time: 0, distance: 0 };
const _evade = { x: 0, z: 0 };
/** Reused result of findMostThreateningBall. */
const _threat = { ball: null, time: Infinity, predicted: false };

/** Gravity magnitude (m/s²) acting on `ball`. */
export function ballGravity(ball) {
  const s = ball && Number.isFinite(ball.gravityScale) ? ball.gravityScale : 1;
  return GRAVITY * Math.max(0, s);
}

/**
 * Local gravity-aware impact sampler: first time (s) within `horizon` at which the ball sphere touches the player's
 * vertical capsule (feet + 0.1 m .. head), or null.
 */
export function sampleImpactTime(ball, player, horizon) {
  if (!ball || !player || !ball.position || !ball.velocity) return null;
  const p = ball.position, v = ball.velocity, g = ballGravity(ball);
  const feet = player.position;
  const r = (player.radius || DEFAULT_RADIUS) + (ball.radius || BALL_RADIUS);
  const r2 = r * r;
  const bottom = feet.y + 0.1, top = feet.y + (player.height || DEFAULT_HEIGHT);
  const floor = game.court ? game.court.floorY : 0;
  for (let t = 0; t <= horizon; t += SAMPLE_STEP) {
    const y = p.y + v.y * t - 0.5 * g * t * t;
    const dy = y < bottom ? bottom - y : (y > top ? y - top : 0);
    const dx = p.x + v.x * t - feet.x, dz = p.z + v.z * t - feet.z;
    if (dx * dx + dy * dy + dz * dz <= r2) return t;
    if (y < floor - 0.2) break; // hit the floor first
  }
  return null;
}

/** Seconds until `ball` is predicted to strike `player` within `horizon`, or null when it misses. */
export function predictImpactTime(ball, player, horizon) {
  const T = ThrowSolver.Trajectory;
  if (T && typeof T.predictImpact === 'function') {
    try {
      const r = T.predictImpact(ball, player, horizon);
      return r && Number.isFinite(r.t) ? r.t : null;
    } catch (e) {
      warnOnce('Trajectory.predictImpact', e);
    }
  }
  return sampleImpactTime(ball, player, horizon);
}

/** True when `ball` is a live ball last thrown by an enemy of `player` (unowned live balls count as hostile). */
export function isHostileLiveBall(ball, player) {
  if (!ball || ball.state !== 'live') return false;
  const thrower = ball.lastThrower;
  if (!thrower) return !ball.isPass;
  return game.areEnemies(thrower, player);
}

/**
 * The hostile live ball endangering `player` the most: the one predicted to hit soonest; when none is predicted to
 * connect, the approaching ball with the closest straight-line near miss (within NEAR_MISS_RADIUS), so evasive
 * movement still has something to flee from. Returns a REUSED object { ball, time, predicted } - copy what you keep.
 */
export function findMostThreateningBall(player, horizon) {
  const out = _threat;
  out.ball = null; out.time = Infinity; out.predicted = false;
  const balls = game.balls;
  if (!player || !balls) return out;

  _incoming.length = 0;
  if (typeof balls.incomingLive === 'function') balls.incomingLive(player, _incoming);
  else if (Array.isArray(balls.active)) for (const b of balls.active) if (b && b.state === 'live') _incoming.push(b);

  _chest.copy(player.position);
  _chest.y += CHEST_HEIGHT;
  let nearMiss = null, nearMissTime = Infinity, nearMissDist = Infinity;
  for (let i = 0; i < _incoming.length; i++) {
    const ball = _incoming[i];
    if (!isHostileLiveBall(ball, player)) continue;
    const t = predictImpactTime(ball, player, horizon);
    if (t !== null) {
      if (t < out.time) { out.time = t; out.ball = ball; }
      continue;
    }
    if (closestApproach(ball.position, ball.velocity, _chest, _approach) && _approach.time <= horizon &&
        _approach.distance <= NEAR_MISS_RADIUS && _approach.distance < nearMissDist) {
      nearMissDist = _approach.distance; nearMissTime = _approach.time; nearMiss = ball;
    }
  }
  _incoming.length = 0;
  if (out.ball) { out.predicted = true; return out; }
  out.ball = nearMiss; out.time = nearMissTime;
  return out;
}

/**
 * Planar unit direction (written into `out`, a THREE.Vector3) that moves `player` sideways out of the flight line of
 * `ball`, keeping inside the player's court confinement; without a ball it side-steps relative to the facing.
 */
export function computeEvadeDirection(player, ball, probe, out) {
  let tx, tz, ox = 0, oz = 0;
  if (ball && ball.velocity) {
    tx = ball.velocity.x; tz = ball.velocity.z;
    ox = player.position.x - ball.position.x; oz = player.position.z - ball.position.z;
  } else {
    const f = player.forward;
    tx = f ? f.x : 0; tz = f ? f.z : 1;
  }
  const bounds = game.court ? game.court.confinement(player.team, player.zone) : null;
  evadeDirection(tx, tz, ox, oz, bounds, player.position.x, player.position.z, probe, _evade);
  return out.set(_evade.x, 0, _evade.z);
}

// ------------------------------------------------------------------ guarded presentation (never breaks gameplay)
const _warned = new Set();
function warnOnce(key, e) {
  if (_warned.has(key)) return;
  _warned.add(key);
  console.warn(`[specter] ${key} failed`, e);
}

/** Cosmetic calls into optional systems (vfx/audio/renderer/avatar): missing systems are skipped, failures warned once. */
export const fx = {
  play(id, position, opts) { try { game.vfx?.play?.(id, position, opts); } catch (e) { warnOnce(`vfx.play(${id})`, e); } },
  attach(id, object3D, opts) {
    try { return object3D ? (game.vfx?.attach?.(id, object3D, opts) ?? null) : null; } catch (e) { warnOnce(`vfx.attach(${id})`, e); return null; }
  },
  stop(handle) { if (handle == null) return; try { game.vfx?.stop?.(handle); } catch (e) { warnOnce('vfx.stop', e); } },
  sfx(id, position, volume = 1, pitch = 1) { try { game.audio?.play?.(id, position, volume, pitch); } catch (e) { warnOnce(`audio.play(${id})`, e); } },
  sfx2D(id, volume = 1, pitch = 1) { try { game.audio?.play2D?.(id, volume, pitch); } catch (e) { warnOnce(`audio.play2D(${id})`, e); } },
  pulse(type, intensity, duration) { try { game.renderer?.pulse?.(type, intensity, duration); } catch (e) { warnOnce(`renderer.pulse(${type})`, e); } },
  flash(player, color, duration) { try { player?.avatar?.flash?.(color, duration); } catch (e) { warnOnce('avatar.flash', e); } },
  afterimage(player, lifetime, color) { try { player?.avatar?.spawnAfterimage?.(lifetime, color); } catch (e) { warnOnce('avatar.spawnAfterimage', e); } },
};
