// ---------------------------------------------------------------------------------------------------------------
// Rayne's ball payloads (shared world logic for heroes/rayne.js).
//
// A payload rides on a live ball (ThrowParams.payload -> ball.payload) and receives the combat callbacks documented in
// WEB_ARCHITECTURE.md §3.2: onLaunched(ball) onTick(ball, dt) onHitPlayer(ball, hit)->bool onAfterHitPlayer(ball, hit,
// outcome) onHitSurface(ball, point, normal, isFloor) onCaught(ball, catcher, quality) onEnded(ball). Only the hooks a
// payload needs are defined; the direct hit itself is always resolved by Combat (damage, catch rules, juice pipeline).
//
//   MeteorPayload  Supersonic Meteor: fire trail; first player hit / court impact / obstacle -> 3 m shockwave
//                  (radial knockback on ENEMIES with falloff + small lift, 0.35 s stun at the core, loose balls pushed,
//                  shockwave VFX + SFX + Perlin shake). A clean catch smothers the fire (the enemy's counter-play).
//   BeamPayload    Hyperbeam Transpierce: beam trail, heavy juice on every enemy pierced, shove for survivors, grounds
//                  out on the first wall/floor.
// Payload callbacks run in the middle of a ball's fixed step (scaled time): they only change gameplay state through the
// public component APIs (motor.addImpulse, fsm.stun, ball.setVelocity) and never move a player outside its court zone
// (the motor's confinement clamps knockback).
// ---------------------------------------------------------------------------------------------------------------
import * as THREE from 'three';
import { game } from '../../game.js';
import { shockwaveImpulse } from './rayneMath.js';

// Module-scope temporaries (payload callbacks run every fixed step while a ball flies: no allocations).
const _impulse = new THREE.Vector3();
const _epicentre = new THREE.Vector3();
const _ground = new THREE.Vector3();
const _flight = new THREE.Vector3();
const _tmp = new THREE.Vector3();

/** Realistic flame orange used by the meteor (hand wind-up, trail, shockwave ring). */
export const FIRE_COLOR = 0xff7320;
/** White-hot plasma with a cool edge used by the hyperbeam. */
export const BEAM_COLOR = 0xc7e6ff;

/**
 * Shockwave tuning (all overridable from the ability's params). Distances in metres, speeds in m/s, times in seconds.
 * `knockback` is the horizontal velocity change at the epicentre: ~7 m/s is a hard shove that throws a 75 kg athlete
 * off balance without launching them across the court (their own zone confinement still applies).
 */
export const SHOCKWAVE_DEFAULTS = Object.freeze({
  radius: 3,                // spec: 3 m AOE on the floor plane
  knockback: 7,             // m/s at the epicentre
  edgeFraction: 0.25,       // fraction of the knockback left at the rim (linear falloff in between)
  upward: 1.8,              // m/s upward at the epicentre (same falloff): the victim's momentum carries them
  coreStunRadius: 1.25,     // enemies this close are briefly stunned...
  coreStun: 0.35,           // ...for this long (spec: 0.35 s)
  freeBallPush: 3.5,        // m/s given to loose balls lying inside the blast
  detonateOnObstacles: true,// also detonate when a non-player obstacle stops the meteor (a Shadow clone, a shield)
  color: FIRE_COLOR,
  trailScale: 1,
  trailDuration: 3.5,       // safety lifetime of the attached fire trail (it is stopped explicitly on resolve)
  shakeAmplitude: 0.6,      // Perlin shake at the detonation (Juice attenuates with distance to sourcePos)
  shakeFrequency: 20,
  shakeDuration: 0.4,
  hitstop: 0.06,            // extra freeze frame when the blast catches at least one enemy (spec 0.03-0.1 s)
  hitstopScale: 0.05,
});

/** Knockback multiplier of a player (e.g. Gouki's Thick Hide halves it) - read defensively, default 1. */
function knockbackMulOf(p) {
  const m = p && (typeof p.knockbackMul === 'number' ? p.knockbackMul : p.motor && typeof p.motor.knockbackMul === 'number' ? p.motor.knockbackMul : 1);
  return Number.isFinite(m) ? Math.max(0, m) : 1;
}

/** Stun helper: the player state machine owns the Stunned state; the status component is the fallback. */
export function stunPlayer(p, duration, source) {
  if (!p || !(duration > 0)) return;
  if (p.fsm && typeof p.fsm.stun === 'function') p.fsm.stun(duration);
  else if (p.status) p.status.apply('stunned', duration, 1, source);
}

/**
 * Detonates a meteor shockwave at `point` for `owner` (only the owner's ENEMIES are affected).
 * @param {object} owner the throwing Player
 * @param {THREE.Vector3} point world point of the impact (the epicentre is projected onto the floor)
 * @param {object} s settings (see SHOCKWAVE_DEFAULTS)
 * @param {THREE.Vector3|null} flightDir the meteor's flight direction (used to push a victim standing on the epicentre)
 * @param {object} [source] status source tag (the ability)
 * @returns {number} number of enemies knocked back
 */
export function meteorShockwave(owner, point, s, flightDir, source) {
  const floorY = game.court ? game.court.floorY : 0;
  _epicentre.copy(point);
  _ground.set(point.x, floorY + 0.02, point.z);
  const fx = flightDir ? flightDir.x : 0, fz = flightDir ? flightDir.z : 1;

  // --- feedback: ring of fire + dust on the floor, heavy thump, Perlin shake (unscaled time inside Juice) ---
  const scale = s.radius / SHOCKWAVE_DEFAULTS.radius; // 1 = the 3 m spec ring
  game.vfx?.play?.('shockwave', _ground, { scale, color: s.color });
  game.vfx?.play?.('floorDust', _ground, { scale: scale * 1.4, color: 0x8a7a66 });
  game.vfx?.play?.('hit', _epicentre, { scale: 1.2, color: s.color });
  game.audio?.play?.('shockwave', _epicentre, 1, 0.95 + 0.1 * (game.rng ? game.rng.next() : 0.5));
  game.juice?.shake?.(s.shakeAmplitude, s.shakeFrequency, s.shakeDuration, _epicentre);

  // --- enemies: radial knockback with falloff + lift, stun at the core ---
  let affected = 0;
  for (const p of game.players) {
    if (!p || !game.areEnemies(owner, p) || !p.isTargetable) continue;
    const dx = p.position.x - _epicentre.x, dz = p.position.z - _epicentre.z;
    // Measure to the body surface so a player whose capsule overlaps the rim is still caught.
    const centreDist = Math.hypot(dx, dz);
    const bodyDist = Math.max(0, centreDist - (p.radius || 0.35));
    if (bodyDist > s.radius) continue;
    // Scale the offset so shockwaveImpulse sees the surface distance but keeps the true direction.
    const k = centreDist > 1e-6 ? bodyDist / centreDist : 0;
    const f = shockwaveImpulse(dx * k, dz * k, s.radius, s.knockback, s.edgeFraction, s.upward, fx, fz, _impulse);
    if (f <= 0) continue;
    if (centreDist > 0.05 && k === 0) { _impulse.x = (dx / centreDist) * s.knockback; _impulse.z = (dz / centreDist) * s.knockback; }
    _impulse.multiplyScalar(knockbackMulOf(p));
    p.motor?.addImpulse?.(_impulse);
    if (bodyDist <= s.coreStunRadius) stunPlayer(p, s.coreStun, source);
    _tmp.set(_impulse.x, 0, _impulse.z);
    if (_tmp.lengthSq() > 1e-6) _tmp.normalize();
    p.avatar?.playHit?.(_tmp);
    game.juice?.flash?.(p, 0xffffff, 0.05);
    affected++;
  }

  // --- loose balls inside the blast roll away from it ---
  const balls = game.balls && (game.balls.active || game.balls.matchBalls);
  if (balls && s.freeBallPush > 0) {
    for (const b of balls) {
      if (!b || b.state !== 'free' || !b.position) continue;
      const dx = b.position.x - _epicentre.x, dz = b.position.z - _epicentre.z;
      const f = shockwaveImpulse(dx, dz, s.radius, s.freeBallPush, s.edgeFraction, s.freeBallPush * 0.35, fx, fz, _impulse);
      if (f <= 0 || typeof b.setVelocity !== 'function') continue;
      b.setVelocity(_tmp.copy(b.velocity || _tmp.set(0, 0, 0)).add(_impulse));
    }
  }

  if (affected > 0) {
    game.juice?.hitstop?.(s.hitstop, s.hitstopScale);
    const local = game.localPlayer;
    if (local && game.areEnemies(owner, local) && local.position.distanceTo(_epicentre) <= s.radius + 0.5) {
      game.renderer?.pulse?.('heavyHit', 0.8, 0.35);
    }
  }
  return affected;
}

/**
 * Supersonic Meteor payload. States: flying -> (detonated | smothered) -> resolved (flight ended).
 * The owning ability polls `resolved` / `detonated` (holdActive until resolved or its timeout).
 */
export class MeteorPayload {
  /**
   * @param {object} owner throwing Player
   * @param {object} settings shockwave settings (SHOCKWAVE_DEFAULTS shape)
   * @param {object} [source] ability instance (status source tag)
   */
  constructor(owner, settings, source = null) {
    this.owner = owner;
    this.s = { ...SHOCKWAVE_DEFAULTS, ...(settings || {}) };
    this.source = source;
    this.ball = null;
    this.detonated = false;   // the shockwave went off
    this.smothered = false;   // caught cleanly: no shockwave
    this.resolved = false;    // the meteor's flight is over (ability may end)
    this.affected = 0;
    this._trail = null;
    this._trailAttached = false;
  }

  /** Attaches the fire trail (idempotent: the ability calls it too in case Combat does not). */
  onLaunched(ball) {
    this.ball = ball;
    if (this._trailAttached || !ball || !ball.root) return;
    this._trailAttached = true;
    this._trail = game.vfx?.attach?.('fireTrail', ball.root, { duration: this.s.trailDuration, color: this.s.color, scale: this.s.trailScale }) ?? null;
  }

  /** A body was struck (Combat already resolved damage). Anything but a pass-through ('ignored') sets the blast off. */
  onAfterHitPlayer(ball, hit, outcome) {
    if (outcome === 'ignored') return;
    const point = (hit && hit.point) || (hit && hit.victim && hit.victim.position) || (ball && ball.position);
    if (point) this.detonate(point, ball);
  }

  /** Wall, floor or court furniture: detonate at the contact point. */
  onHitSurface(ball, point /* , normal, isFloor */) {
    this.detonate(point || (ball && ball.position), ball);
  }

  /** A clean catch smothers the fire - the counter-play to the meteor. */
  onCaught(ball, catcher /* , quality */) {
    if (!this.detonated && !this.smothered) {
      this.smothered = true;
      const at = (catcher && catcher.position) ? _tmp.copy(catcher.position).setY(catcher.position.y + (catcher.height || 1.8) * 0.72) : ball && ball.position;
      if (at) {
        game.vfx?.play?.('vanishSmoke', at, { scale: 0.45, color: 0x3a3430 });
        game.audio?.play?.('fireSmother', at, 0.7, 1);
      }
    }
    this.finish();
  }

  /** Live flight ended for any reason. If nothing resolved it (absorbed by an obstacle), burst where it stopped. */
  onEnded(ball) {
    if (!this.detonated && !this.smothered && this.s.detonateOnObstacles && ball && ball.position &&
        ball.state !== 'held' && ball.state !== 'despawned' && ball.state !== 'stasis' &&
        !(game.court && game.court.isOutOfArena(ball.position))) {
      this.detonate(ball.position, ball);
    }
    this.finish();
  }

  /** Sets the shockwave off once. */
  detonate(point, ball) {
    if (this.detonated || this.smothered || !point) return;
    this.detonated = true;
    const v = ball && ball.velocity;
    if (v && v.x * v.x + v.z * v.z > 1e-6) _flight.set(v.x, 0, v.z).normalize();
    else if (this.owner && this.owner.forward) _flight.copy(this.owner.forward);
    else _flight.set(0, 0, 1);
    this.affected = meteorShockwave(this.owner, point, this.s, _flight, this.source);
    this.stopTrail();
  }

  finish() {
    this.resolved = true;
    this.stopTrail();
  }

  stopTrail() {
    if (this._trail != null) { game.vfx?.stop?.(this._trail); this._trail = null; }
  }
}

/** Hyperbeam tuning (overridable from the ability's params). */
export const BEAM_DEFAULTS = Object.freeze({
  knockback: 5,            // m/s shove given to enemies that survive the beam (e.g. Gouki's 200 HP)
  hitstop: 0.1,            // heavy freeze on every pierce (Juice clamps to 0.03-0.1 s)
  hitstopScale: 0.02,
  shakeAmplitude: 0.9,
  shakeFrequency: 26,
  shakeDuration: 0.5,
  color: BEAM_COLOR,
  trailScale: 1.3,
  trailDuration: 3,
});

/** Hyperbeam Transpierce payload: counts pierced enemies, grounds out on the first surface. */
export class BeamPayload {
  constructor(owner, settings) {
    this.owner = owner;
    this.s = { ...BEAM_DEFAULTS, ...(settings || {}) };
    this.ball = null;
    this.pierced = 0;
    this.hasHitSurface = false;
    this.resolved = false;
    this._trail = null;
    this._trailAttached = false;
  }

  /** Attaches the beam trail (idempotent). */
  onLaunched(ball) {
    this.ball = ball;
    if (this._trailAttached || !ball || !ball.root) return;
    this._trailAttached = true;
    this._trail = game.vfx?.attach?.('beamTrail', ball.root, { duration: this.s.trailDuration, color: this.s.color, scale: this.s.trailScale }) ?? null;
  }

  onAfterHitPlayer(ball, hit, outcome) {
    if (outcome === 'ignored' || !hit) return;
    this.pierced++;
    const victim = hit.victim;
    const point = hit.point || (victim && victim.position) || (ball && ball.position);
    if (point) {
      game.vfx?.play?.('hit', point, { scale: 1.5, color: this.s.color });
      game.audio?.play?.('beamPierce', point, 1, 0.9 + 0.05 * this.pierced);
      game.juice?.shake?.(this.s.shakeAmplitude, this.s.shakeFrequency, this.s.shakeDuration, point);
    }
    game.juice?.hitstop?.(this.s.hitstop, this.s.hitstopScale);
    if (victim && victim.isLocal) game.renderer?.pulse?.('heavyHit', 1, 0.4);
    // Survivors (Thick Hide, delayed eliminations) are still shoved along the beam.
    if (outcome === 'damaged' && victim && victim.motor && this.s.knockback > 0) {
      const v = ball && ball.velocity;
      if (v && v.x * v.x + v.z * v.z > 1e-6) {
        _impulse.set(v.x, 0, v.z).normalize().multiplyScalar(this.s.knockback * knockbackMulOf(victim));
        _impulse.y = 0.8;
        victim.motor.addImpulse?.(_impulse);
      }
    }
  }

  onHitSurface(ball, point /* , normal, isFloor */) {
    if (this.hasHitSurface) return;
    this.hasHitSurface = true;
    const at = point || (ball && ball.position);
    if (at) {
      game.vfx?.play?.('hit', at, { scale: 1.2, color: this.s.color });
      game.vfx?.play?.('floorDust', at, { scale: 0.8 });
      game.audio?.play?.('beamImpact', at, 0.9, 0.85);
    }
    this.stopTrail();
  }

  onCaught() { this.finish(); }
  onEnded() { this.finish(); }

  finish() { this.resolved = true; this.stopTrail(); }

  stopTrail() {
    if (this._trail != null) { game.vfx?.stop?.(this._trail); this._trail = null; }
  }
}
