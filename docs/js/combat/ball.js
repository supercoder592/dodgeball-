// ---------------------------------------------------------------------------------------------------------------
// Ball - one regulation rubber dodgeball: state machine, custom swept physics, realistic look, trail.
//
// States
//   free       loose: gravity, bounces (restitution 0.55 + friction), rolling resistance, spin, pushed by feet
//   held       follows the holder's avatar.rightHandSocket (visual) - Combat owns throwing it
//   live       thrown: field effects -> payload.onTick -> gravity x gravityScale + air drag -> SWEPT SPHERE against
//              hittables (clones, shields, turrets), enemy catch reach / bodies / held balls, arena colliders and the
//              floor, resolved in time-of-impact order (ties: hittables first). Any surface contact ends 'live'.
//   stasis     hovers with a slow bob (Chrono), then resumes its preserved velocity
//   despawned  hidden (Houdini's Grand Vanish, out of arena), then respawns
// Live contact rules
//   thrower ignored for 0.15 s; teammates pass through unless it is a pass (or they are catch-armed) -> receivePass;
//   enemies: unblockable ? no catch : victim.combat.tryResolveCatch(ball, point, impactTime); caught -> stop;
//   else HitResolver.resolveHit -> pierce keeps flying (never the same player twice), otherwise deflects off the
//   body (reflected, damped, a little lift) and becomes free. 'ignored' outcomes (untargetable, evaded) fly through.
// Scene graph
//   root (Group, world position)  ->  visual (Object3D: Juice's squash target, never written here)
//     -> stretch (speed stretch along the velocity + size)  ->  mesh (spin orientation, counter-rotated)
// Time: physics in fixedUpdate (scaled 60 Hz); render interpolation, spin, stretch and trail in lateUpdate.
// ---------------------------------------------------------------------------------------------------------------
import * as THREE from 'three';
import { game } from '../game.js';
import { EV } from '../core/events.js';
import { BALL_RADIUS, GRAVITY, MS_TO_KMH } from '../core/constants.js';
import { RewindHistory } from '../gameplay/history.js';
import { HitResolver, bodyRadiusOf, bodyHeightOf, chestOf, isTargetable } from './throwSolver.js';
import {
  makeSweepHit, sweepSphereCapsuleY, sweepSphereSphere, sweepSphereAABB, sweepSpherePlaneY, bounceVelocity,
} from './sweep.js';
import { getBallTextures } from './ballTextures.js';
import { BallTrail } from './ballTrail.js';

export const BALL_STATE = Object.freeze({ FREE: 'free', HELD: 'held', LIVE: 'live', STASIS: 'stasis', DESPAWNED: 'despawned' });
export const BALL_STYLES = Object.freeze(['standard', 'meteor', 'beam', 'glue', 'freeze', 'turret']);

/** Physical tuning (SI units). */
export const BALL_PHYS = Object.freeze({
  floorRestitution: 0.55,        // rubber on hardwood
  wallRestitution: 0.5,          // padded walls / bleachers
  surfaceTangentKeep: 0.85,      // tangential speed kept per bounce (friction)
  bodyRestitution: 0.28,         // a ball thudding off a body loses most of its normal speed
  bodyTangentKeep: 0.45,
  bodyDeflectLift: 1.4,          // m/s pop-up after a body hit (reads well, lets teammates react)
  blockRestitution: 0.35,        // off shields / barriers ('block')
  heldBlockRestitution: 0.5,     // off a ball held up as a shield
  heldBlockShove: 0.6,           // m/s shove on the blocker
  absorbKeep: 0.08,              // speed kept when a hittable swallows the ball ('absorb')
  liveDrag: 0.0018,              // quadratic air drag while live (1/m): a = k v^2 (gentle, keeps rally speeds)
  freeDrag: 0.006,               // quadratic drag while loose
  rollingDecel: 0.45,            // m/s^2 rolling resistance of a rubber ball on hardwood (~0.05 g)
  restingSpeed: 0.04,            // m/s below which a rolling ball stops
  bounceStopSpeed: 0.55,         // m/s normal speed below which a floor contact becomes rolling
  angularDamping: 0.4,           // 1/s while airborne
  throwerGrace: 0.15,            // s the thrower is ignored after release
  maxLiveTime: 7,                // s safety: a live ball that never touches anything becomes free
  launchSpin: 16,                // rad/s topspin on release (visual)
  stasisBobAmplitude: 0.02,      // m
  stasisBobFrequency: 0.8,       // Hz
  stasisSpin: 0.35,              // rad/s
  minBounceEventSpeed: 0.8,      // m/s: slower contacts emit no BallBounced (no audio spam while rolling)
  minBounceEventInterval: 0.06,  // s between BallBounced events of one ball
  stretchPerMs: 0.0022,          // live speed stretch per m/s along the flight direction
  maxStretch: 1.14,
  footRestitution: 0.3,          // a loose ball bumping off legs
  maxPickupSpeed: 12,            // m/s: faster loose balls cannot be grabbed
  pickupZoneReach: 0.55,         // m beyond a player's movement confinement a ball may be grabbed (to the line)
  catchReach: 0.85,              // m: default hands reach around the chest (Combat profile.catchRadius wins)
});

/** Material look per style (colour is multiplied with the greyscale pebble albedo). */
export const BALL_LOOK = Object.freeze({
  standard: { color: 0xc01b22, emissive: 0x000000, emissiveIntensity: 0, roughness: 1.0, clearcoat: 0.3, clearcoatRoughness: 0.45, flicker: 0 },
  meteor: { color: 0x3a1206, emissive: 0xff5212, emissiveIntensity: 2.6, roughness: 1.0, clearcoat: 0.1, clearcoatRoughness: 0.6, flicker: 0.35 },
  beam: { color: 0xdff6ff, emissive: 0x9fe9ff, emissiveIntensity: 3.2, roughness: 0.6, clearcoat: 0.6, clearcoatRoughness: 0.2, flicker: 0.12 },
  glue: { color: 0x3f8a16, emissive: 0x0c2a02, emissiveIntensity: 0.4, roughness: 0.45, clearcoat: 1.0, clearcoatRoughness: 0.08, flicker: 0 },
  freeze: { color: 0xc8ecff, emissive: 0x2f7dff, emissiveIntensity: 0.7, roughness: 0.5, clearcoat: 1.0, clearcoatRoughness: 0.05, flicker: 0.06 },
  turret: { color: 0xd49a18, emissive: 0x2a1600, emissiveIntensity: 0.3, roughness: 0.9, clearcoat: 0.5, clearcoatRoughness: 0.3, flicker: 0 },
});

// Contact types (priority on equal time of impact: hittables first).
const C_NONE = 0, C_HITTABLE = 1, C_CATCH = 2, C_RECEIVE = 3, C_PLAYER = 4, C_HELD = 5, C_SURFACE = 6;
const PRIORITY = [99, 0, 1, 1, 2, 3, 4];
const STOP = 0, CONTINUE = 1;

// ------------------------------------------------------------------ shared resources & temporaries
let _geometry = null;
function ballGeometry() {
  if (!_geometry) _geometry = new THREE.SphereGeometry(BALL_RADIUS, 48, 32);
  return _geometry;
}

const _UP = new THREE.Vector3(0, 1, 0);
const _from = new THREE.Vector3();
const _to = new THREE.Vector3();
const _pt = new THREE.Vector3();
const _n = new THREE.Vector3();
const _v = new THREE.Vector3();
const _w = new THREE.Vector3();
const _chest = new THREE.Vector3();
const _q = new THREE.Quaternion();
const _qInv = new THREE.Quaternion();
const _sw = makeSweepHit();
const _warned = new Set();

// Movement confinement per (team, zone), cached per Court instance (Court.confinement allocates; geometry is fixed).
let _zoneCourt = null;
const _zoneCache = [[null, null], [null, null]];
/**
 * Confinement bounds {minX,maxX,minZ,maxZ} of `team` in `zone` (cached), or null without a court / team.
 * @param {number} team TEAM.HOME | TEAM.AWAY
 * @param {string} zone 'infield' | 'outfield'
 */
export function zoneBounds(team, zone) {
  const court = game.court;
  if (!court || !(team === 0 || team === 1)) return null;
  if (_zoneCourt !== court) {
    _zoneCourt = court;
    _zoneCache[0][0] = _zoneCache[0][1] = _zoneCache[1][0] = _zoneCache[1][1] = null;
  }
  const zi = zone === 'outfield' ? 1 : 0;
  let b = _zoneCache[team][zi];
  if (!b) b = _zoneCache[team][zi] = court.confinement(team, zi ? 'outfield' : 'infield');
  return b;
}

function warnOnce(key, e) {
  if (_warned.has(key)) return;
  _warned.add(key);
  console.warn(`[ball] ${key}`, e);
}

/** Calls payload[fn](...args) if present, isolating ability errors from ball physics. */
function payloadCall(payload, fn, ...args) {
  if (!payload || typeof payload[fn] !== 'function') return undefined;
  try { return payload[fn](...args); } catch (e) { console.error(`[ball] payload.${fn} threw`, e); return undefined; }
}

export class Ball {
  /**
   * @param {string|number} id
   * @param {boolean} [isAbilityBall] temporary projectile conjured by an ability (recycled when no longer live)
   */
  constructor(id, isAbilityBall = false) {
    this.id = id;
    this.isAbilityBall = !!isAbilityBall;
    /** 'free'|'held'|'live'|'stasis'|'despawned' */
    this.state = BALL_STATE.FREE;
    this.style = 'standard';
    /** Player (or any object with a socket) currently holding the ball. */
    this.holder = null;
    this.lastHolder = null;
    /** Player who launched the current / last flight. */
    this.lastThrower = null;
    /** Consecutive catch-and-rethrows without touching the floor (Rally Boost). */
    this.rallyCount = 0;
    this.isPass = false;
    this.unblockable = false;
    this.pierce = false;
    this.isCounter = false;
    this.gravityScale = 1;
    /** Ability hooks for the current flight (see WEB_ARCHITECTURE.md §3.2). */
    this.payload = null;
    /** The player the throw was aimed at (Danger Sense, AI). */
    this.lockedTarget = null;
    this.launchTime = -Infinity;
    /** ThrowParams of the current / last flight. */
    this.throwParams = null;
    this.position = new THREE.Vector3(0, BALL_RADIUS, 0);
    this.velocity = new THREE.Vector3();
    this.angularVelocity = new THREE.Vector3();
    this.radius = BALL_RADIUS;
    /** Where this match ball respawns (centre-line slot). */
    this.homePosition = new THREE.Vector3(0, BALL_RADIUS, 0);
    this.prevPosition = new THREE.Vector3();
    /** Resting / rolling on the court floor. */
    this.grounded = false;

    // Per-flight bookkeeping.
    this.hitPlayers = new Set();
    this._passedHittables = new Set();
    this._catchTried = new Set();
    this._contact = { type: C_NONE, t: 0, prio: 99, px: 0, py: 0, pz: 0, nx: 0, ny: 1, nz: 0, ref: null, hx: 0, hy: 0, hz: 0 };
    this._hitInfo = { t: 0, point: new THREE.Vector3(), normal: new THREE.Vector3(), ball: this };
    this._lastBounceEvent = -Infinity;

    // Stasis / despawn timers.
    this._stasisRemaining = 0;
    this._stasisResume = BALL_STATE.LIVE;
    this._stasisAnchor = new THREE.Vector3();
    this._stasisVelocity = new THREE.Vector3();
    this._stasisPhase = 0;
    this._despawnRemaining = 0;
    this._respawnPos = new THREE.Vector3();
    this._hasRespawnPos = false;
    /** Set when a timed despawn elapsed on an ability ball (BallManager recycles it). */
    this._despawnDone = false;

    // BallManager lifecycle fields.
    /** Ability ball spawned but not launched yet: seconds left before it is recycled. */
    this._awaitLaunch = 0;
    /** Ability ball fade-out progress (0 = none, 0..1 fading). */
    this._fade = 0;
    /** In the ability-ball pool (inactive). */
    this.recycled = false;
    this._unreachableTime = 0;
    this._reachCheck = 0;

    // Visuals.
    this.root = new THREE.Group();
    this.root.name = `Ball_${id}`;
    this.visual = new THREE.Object3D();
    this.visual.name = 'BallVisual';
    this._stretch = new THREE.Object3D();
    this.material = createBallMaterial();
    this.mesh = new THREE.Mesh(ballGeometry(), this.material);
    this.mesh.name = 'BallMesh';
    this.mesh.castShadow = true;
    this.mesh.receiveShadow = false;
    this._stretch.add(this.mesh);
    this.visual.add(this._stretch);
    this.root.add(this.visual);
    this._spin = new THREE.Quaternion();
    this._stretchAmount = 1;
    this._look = BALL_LOOK.standard;
    this._flickerPhase = Math.random() * 100;
    this.trail = new BallTrail();

    /** Snapshots for Chrono's Temporal Reset (position, velocity, state) at 30 Hz for 4 s. */
    this._snap = { position: this.position, velocity: this.velocity, state: this.state };
    this.history = new RewindHistory(() => { this._snap.state = this.state; return this._snap; }, 4, 30);

    this.setStyle('standard', true);
  }

  // ================================================================== readouts
  get isLive() { return this.state === BALL_STATE.LIVE; }
  get isFree() { return this.state === BALL_STATE.FREE; }
  get isHeld() { return this.state === BALL_STATE.HELD; }
  /** Current speed in km/h (0 while held / hovering / hidden). */
  get speedKmh() {
    return this.state === BALL_STATE.LIVE || this.state === BALL_STATE.FREE ? this.velocity.length() * MS_TO_KMH : 0;
  }
  get speed() { return this.velocity.length(); }

  // ================================================================== state changes
  /**
   * Throws the ball (Combat.launchBall builds `params` and solves `velocity`).
   * @param {object} params ThrowParams
   * @param {THREE.Vector3} velocity m/s
   */
  launch(params, velocity) {
    params = params || {};
    this._detachFromHolder();
    if (this.state === BALL_STATE.LIVE || this.state === BALL_STATE.STASIS) this._endLive('relaunch');
    this.root.visible = true;
    this.state = BALL_STATE.LIVE;
    this.lastThrower = params.thrower || null;
    this.rallyCount = Math.max(0, params.rallyCount | 0);
    this.isPass = !!params.isPass;
    this.unblockable = !!params.unblockable;
    this.pierce = !!params.pierce;
    this.isCounter = !!params.isCounter;
    this.gravityScale = Number.isFinite(params.gravityScale) ? params.gravityScale : 1;
    this.payload = params.payload || null;
    this.lockedTarget = params.target || null;
    this.launchTime = game.time.now;
    this.throwParams = params;
    this.radius = BALL_RADIUS * (params.radiusMul > 0 ? params.radiusMul : 1);
    this.setStyle(params.style || 'standard');
    if (params.origin && params.origin.isVector3) this.position.copy(params.origin);
    this.prevPosition.copy(this.position);
    if (velocity && velocity.isVector3) this.velocity.copy(velocity);
    // Topspin around the axis right of the flight (tilted a little at random, visual only).
    _w.crossVectors(_UP, this.velocity);
    if (_w.lengthSq() < 1e-8) _w.set(1, 0, 0);
    _w.normalize();
    _w.x += (Math.random() - 0.5) * 0.5; _w.y += (Math.random() - 0.5) * 0.5;
    this.angularVelocity.copy(_w.normalize()).multiplyScalar(BALL_PHYS.launchSpin * (0.8 + 0.4 * Math.random()));
    this.hitPlayers.clear();
    this._passedHittables.clear();
    this._catchTried.clear();
    this.grounded = false;
    this._fade = 0;
    this._awaitLaunch = 0;
    this._unreachableTime = 0;
    this.trail.reset();
    payloadCall(this.payload, 'onLaunched', this);
  }

  /**
   * Puts the ball in `holder`'s hand. Ends a live flight (the caller - e.g. a catch - has already run its hooks).
   * Keeps Combat in sync: the holder's combat.heldBall becomes this ball.
   * @param {object} holder Player (or any object with avatar.rightHandSocket / socket / root / position)
   */
  attachTo(holder) {
    if (!holder) return;
    if (this.holder === holder && this.state === BALL_STATE.HELD) return;
    if (this.holder) this._detachFromHolder();
    if (this.state === BALL_STATE.LIVE || this.state === BALL_STATE.STASIS) this._endLive('held');
    this.root.visible = true;
    this.state = BALL_STATE.HELD;
    this.holder = holder;
    this.lastHolder = holder;
    this.velocity.set(0, 0, 0);
    this.angularVelocity.set(0, 0, 0);
    this.grounded = false;
    this._fade = 0;
    this._awaitLaunch = 0;
    this._unreachableTime = 0;
    this._despawnRemaining = 0;
    this.radius = BALL_RADIUS;
    this.setStyle('standard');
    if (holder.combat && holder.combat.heldBall !== this && typeof holder.combat._acceptBall === 'function') holder.combat._acceptBall(this);
    this._followHolder();
    this.prevPosition.copy(this.position);
    this.trail.reset();
  }

  /**
   * Releases the ball as a loose ball.
   * @param {THREE.Vector3} [velocity] new velocity (current one kept when omitted)
   * @param {boolean} [resetRally]
   */
  makeFree(velocity, resetRally = false) {
    if (this.holder) this._detachFromHolder();
    if (this.state === BALL_STATE.LIVE || this.state === BALL_STATE.STASIS) this._endLive('free');
    if (this.state === BALL_STATE.DESPAWNED) { this.root.visible = true; this.prevPosition.copy(this.position); }
    this.state = BALL_STATE.FREE;
    if (velocity && velocity.isVector3) this.velocity.copy(velocity);
    if (resetRally) this.rallyCount = 0;
    this.grounded = false;
    this._despawnRemaining = 0;
  }

  /** Overrides the velocity (fields, knockback of loose balls...). */
  setVelocity(v) {
    if (!v) return;
    this.velocity.copy(v);
    if (this.state === BALL_STATE.FREE && v.y > 0.05) this.grounded = false;
    if (this.state === BALL_STATE.STASIS) this._stasisVelocity.copy(v);
  }

  /**
   * Moves the ball instantly. A held ball is released (free); live / stasis / free keep their state.
   * @param {THREE.Vector3} pos
   * @param {THREE.Vector3} [vel] (zero when omitted)
   * @param {boolean} [makeLive] re-enter live flight with the last thrower (rewinds)
   */
  teleport(pos, vel, makeLive = false) {
    if (this.holder) { this._detachFromHolder(); this.state = BALL_STATE.FREE; }
    if (this.state === BALL_STATE.DESPAWNED) { this.root.visible = true; this.state = BALL_STATE.FREE; }
    if (pos) this.position.copy(pos);
    this.prevPosition.copy(this.position);
    if (vel && vel.isVector3) this.velocity.copy(vel); else this.velocity.set(0, 0, 0);
    this.grounded = false;
    if (this.state === BALL_STATE.STASIS) { this._stasisAnchor.copy(this.position); this._stasisVelocity.copy(this.velocity); this.velocity.set(0, 0, 0); }
    if (makeLive && this.state === BALL_STATE.FREE && this.lastThrower) {
      this.state = BALL_STATE.LIVE;
      this.launchTime = game.time.now - BALL_PHYS.throwerGrace;
      this.hitPlayers.clear(); this._passedHittables.clear(); this._catchTried.clear();
    }
    this.trail.reset();
  }

  /**
   * Freezes the ball in the air for `d` seconds (hover + bob), then resumes: a live ball continues its flight with
   * the preserved velocity (resume = 'live') or drops (resume = 'free').
   * @returns {boolean}
   */
  enterStasis(d, resume = BALL_STATE.LIVE) {
    if (this.state === BALL_STATE.HELD || this.state === BALL_STATE.DESPAWNED || !(d > 0)) return false;
    if (this.state !== BALL_STATE.STASIS) {
      this._stasisResume = this.state === BALL_STATE.LIVE && resume === BALL_STATE.LIVE ? BALL_STATE.LIVE : BALL_STATE.FREE;
      this._stasisVelocity.copy(this.velocity);
      this._stasisAnchor.copy(this.position);
      this._stasisPhase = 0;
    }
    this._stasisRemaining = Math.max(this._stasisRemaining, d);
    this.velocity.set(0, 0, 0);
    this.angularVelocity.multiplyScalar(0.05);
    if (this.angularVelocity.lengthSq() < 1e-6) this.angularVelocity.set(0, BALL_PHYS.stasisSpin, 0);
    this.state = BALL_STATE.STASIS;
    return true;
  }

  /**
   * Hides the ball (detaching it from any holder) for `d` seconds, then respawns it at `respawnPos` (its home slot
   * when omitted). d <= 0: hidden until resetTo() is called.
   */
  despawn(d, respawnPos) {
    if (this.holder) this._detachFromHolder();
    if (this.state === BALL_STATE.LIVE || this.state === BALL_STATE.STASIS) this._endLive('despawn');
    this.state = BALL_STATE.DESPAWNED;
    this.root.visible = false;
    this.velocity.set(0, 0, 0);
    this.angularVelocity.set(0, 0, 0);
    this._despawnRemaining = d > 0 ? d : Infinity;
    this._despawnDone = false;
    this._hasRespawnPos = !!(respawnPos && respawnPos.isVector3);
    if (this._hasRespawnPos) this._respawnPos.copy(respawnPos);
    this.trail.reset();
  }

  /** Loose, at rest at `pos` (home slot when omitted), rally cleared, plain style. */
  resetTo(pos) {
    if (this.holder) this._detachFromHolder();
    if (this.state === BALL_STATE.LIVE || this.state === BALL_STATE.STASIS) this._endLive('reset');
    this.state = BALL_STATE.FREE;
    this.position.copy(pos && pos.isVector3 ? pos : this.homePosition);
    const floorY = game.court ? game.court.floorY : 0;
    if (this.position.y < floorY + BALL_RADIUS) this.position.y = floorY + BALL_RADIUS;
    this.prevPosition.copy(this.position);
    this.velocity.set(0, 0, 0);
    this.angularVelocity.set(0, 0, 0);
    this.rallyCount = 0;
    this.radius = BALL_RADIUS;
    this.lastThrower = null;
    this.lockedTarget = null;
    this.grounded = this.position.y <= floorY + BALL_RADIUS + 1e-3;
    this._fade = 0;
    this._unreachableTime = 0;
    this._despawnRemaining = 0;
    this._despawnDone = false;
    this._stasisRemaining = 0;
    this.root.visible = true;
    this.setStyle('standard');
    this.trail.reset();
  }

  /** Visual style: 'standard'|'meteor'|'beam'|'glue'|'freeze'|'turret' (material tint/emissive + trail colour). */
  setStyle(style, force = false) {
    const s = BALL_LOOK[style] ? style : 'standard';
    if (s === this.style && !force) return;
    this.style = s;
    const L = BALL_LOOK[s];
    this._look = L;
    const m = this.material;
    m.color.setHex(L.color);
    m.emissive.setHex(L.emissive);
    m.emissiveIntensity = L.emissiveIntensity;
    m.roughness = L.roughness;
    m.clearcoat = L.clearcoat;
    m.clearcoatRoughness = L.clearcoatRoughness;
    this.trail.setStyle(s);
  }

  /** May `p` pick this ball up right now? */
  canBePickedUpBy(p) {
    if (!p || this.state !== BALL_STATE.FREE || this._fade > 0 || this._awaitLaunch > 0) return false;
    const c = p.combat;
    if (c && (c.hasBall || (c._pickupLockUntil > game.time.now))) return false;
    if (p.canAct === false) return false;
    if (p.health && p.health.isAlive === false) return false;
    if (this.velocity.lengthSq() > BALL_PHYS.maxPickupSpeed * BALL_PHYS.maxPickupSpeed) return false;
    // Only balls on your own side of the lines (plus arm's reach): no grabbing across the centre line or, from the
    // outfield strip, out of the enemy court.
    const b = zoneBounds(p.team, p.zone);
    if (b) {
      const m = BALL_PHYS.pickupZoneReach, q = this.position;
      if (q.x < b.minX - m || q.x > b.maxX + m || q.z < b.minZ - m || q.z > b.maxZ + m) return false;
    }
    return true;
  }

  setRallyCount(n) { this.rallyCount = Math.max(0, n | 0); }

  // ================================================================== simulation
  /** Fixed 60 Hz step on scaled time. */
  fixedUpdate(dt) {
    this.prevPosition.copy(this.position);
    switch (this.state) {
      case BALL_STATE.HELD: this._stepHeld(); break;
      case BALL_STATE.LIVE: this._stepLive(dt); break;
      case BALL_STATE.FREE: this._stepFree(dt); break;
      case BALL_STATE.STASIS: this._stepStasis(dt); break;
      case BALL_STATE.DESPAWNED: this._stepDespawned(dt); break;
      default: break;
    }
    if (this.state !== BALL_STATE.DESPAWNED) this.history.record(game.time.now);
  }

  _stepHeld() {
    const h = this.holder;
    if (!h) { this.makeFree(null, false); return; }
    // Safety net: an eliminated holder or a Combat that forgot this ball never keeps it glued to the hand.
    if (h.health && h.health.isAlive === false) {
      if (h.combat && h.combat.heldBall === this && typeof h.combat.dropBall === 'function') h.combat.dropBall();
      else this.makeFree(h.velocity || null, false);
      return;
    }
    if (h.combat && h.combat.heldBall !== this) { this.makeFree(null, false); return; }
    this._followHolder();
  }

  _stepLive(dt) {
    const now = game.time.now;
    const stepStart = now - dt;
    if (game.balls && typeof game.balls.applyFields === 'function') game.balls.applyFields(this, dt);
    if (this.state !== BALL_STATE.LIVE) return;
    payloadCall(this.payload, 'onTick', this, dt);
    if (this.state !== BALL_STATE.LIVE) return;
    if (now - this.launchTime > BALL_PHYS.maxLiveTime) { this.makeFree(null, false); return; }

    // Semi-implicit Euler: velocity first (gravity x gravityScale + light quadratic drag), then a swept move.
    const v = this.velocity;
    v.y -= GRAVITY * this.gravityScale * dt;
    const sp = v.length();
    if (sp > 0) v.multiplyScalar(Math.max(0, 1 - BALL_PHYS.liveDrag * sp * dt));

    let tBase = 0;
    _from.copy(this.position);
    for (let iter = 0; iter < 8 && this.state === BALL_STATE.LIVE; iter++) {
      _to.copy(_from).addScaledVector(v, dt * (1 - tBase));
      const c = this._firstLiveContact(_from, _to, now);
      if (!c) { this.position.copy(_to); return; }
      const tAbs = tBase + (1 - tBase) * c.t;
      this.position.set(c.px, c.py, c.pz);
      if (this._resolveLiveContact(c, stepStart + dt * tAbs) !== CONTINUE) return;
      tBase = tAbs;
      _from.copy(this.position);
    }
  }

  /** Earliest contact of the segment (ball centre from -> to) this step, or null. */
  _firstLiveContact(from, to, now) {
    const c = this._contact;
    c.type = C_NONE; c.t = Infinity; c.prio = 99; c.ref = null;
    const r = this.radius;
    const thrower = this.lastThrower;
    const inGrace = now - this.launchTime < BALL_PHYS.throwerGrace;

    // 1) Hittables: clones, shields, turrets... (own team's objects never stop own balls).
    if (game.hittables && game.hittables.size) {
      for (const h of game.hittables) {
        if (!h || this._passedHittables.has(h) || typeof h.intersect !== 'function') continue;
        if (thrower && Number.isInteger(h.team) && h.team >= 0 && h.team === thrower.team) continue;
        let res = null;
        try { res = h.intersect(from, to, r); } catch (e) { warnOnce('hittable.intersect threw', e); continue; }
        if (!res || !(res.t >= 0 && res.t <= 1)) continue;
        if (!this._better(c, res.t, C_HITTABLE)) continue;
        _sw.t = res.t;
        _sw.px = from.x + (to.x - from.x) * res.t; _sw.py = from.y + (to.y - from.y) * res.t; _sw.pz = from.z + (to.z - from.z) * res.t;
        if (res.normal && res.normal.isVector3 && res.normal.lengthSq() > 1e-8) { _sw.nx = res.normal.x; _sw.ny = res.normal.y; _sw.nz = res.normal.z; }
        else { _n.subVectors(from, to).normalize(); _sw.nx = _n.x; _sw.ny = _n.y; _sw.nz = _n.z; }
        this._take(c, C_HITTABLE, h);
        if (res.point && res.point.isVector3) { c.hx = res.point.x; c.hy = res.point.y; c.hz = res.point.z; }
        else { c.hx = c.px; c.hy = c.py; c.hz = c.pz; }
      }
    }

    // 2) Players: catch reach, bodies, balls held up as shields; teammates only receive passes.
    const players = game.players;
    for (let i = 0; i < players.length; i++) {
      const p = players[i];
      if (!p || !p.position) continue;
      if (p === thrower && inGrace) continue;
      if (this.hitPlayers.has(p)) continue;
      const pc = p.combat;
      const enemy = thrower ? game.areEnemies(thrower, p) : true;
      if (enemy) {
        if (!isTargetable(p)) continue;
        const R = bodyRadiusOf(p), H = bodyHeightOf(p);
        if (!this.unblockable && pc && pc.catchArmed && !pc.hasBall && !this._catchTried.has(p)) {
          chestOf(p, _chest);
          const reach = pc.profile && pc.profile.catchRadius > 0 ? pc.profile.catchRadius : BALL_PHYS.catchReach;
          if (sweepSphereSphere(from, to, r, _chest, reach, _sw) && this._better(c, _sw.t, C_CATCH)) this._take(c, C_CATCH, p);
        }
        const pos = p.position;
        if (sweepSphereCapsuleY(from, to, r, pos.x, pos.z, pos.y + R, pos.y + Math.max(R, H - R), R, _sw) && this._better(c, _sw.t, C_PLAYER)) {
          this._take(c, C_PLAYER, p);
        }
        const held = pc && pc.heldBall;
        if (!this.unblockable && held && held !== this && held.state === BALL_STATE.HELD) {
          if (sweepSphereSphere(from, to, r, held.position, held.radius, _sw) && this._better(c, _sw.t, C_HELD)) this._take(c, C_HELD, p);
        }
      } else if (thrower && game.areTeammates(thrower, p)) {
        if (!pc || pc.hasBall || typeof pc.receivePass !== 'function') continue;
        const receiving = (this.isPass && p.canReceive !== false) || pc.catchArmed;
        if (!receiving) continue;
        chestOf(p, _chest);
        const reach = pc.profile && pc.profile.catchRadius > 0 ? pc.profile.catchRadius : BALL_PHYS.catchReach;
        if (sweepSphereSphere(from, to, r, _chest, reach, _sw) && this._better(c, _sw.t, C_RECEIVE)) this._take(c, C_RECEIVE, p);
      }
    }

    // 3) Arena colliders and the court floor.
    this._surfaceContacts(from, to, c);
    return c.type === C_NONE ? null : c;
  }

  _surfaceContacts(from, to, c) {
    const r = this.radius;
    const cols = game.arena && game.arena.colliders;
    if (cols) {
      for (let i = 0; i < cols.length; i++) {
        const box = cols[i];
        if (!box || !box.min || !box.max) continue;
        if (sweepSphereAABB(from, to, r, box.min, box.max, _sw) && this._better(c, _sw.t, C_SURFACE)) this._take(c, C_SURFACE, box);
      }
    }
    const floorY = game.court ? game.court.floorY : 0;
    if (sweepSpherePlaneY(from, to, r, floorY, _sw) && this._better(c, _sw.t, C_SURFACE)) this._take(c, C_SURFACE, null);
  }

  _better(c, t, type) {
    return t < c.t - 1e-7 || (Math.abs(t - c.t) <= 1e-7 && PRIORITY[type] < c.prio);
  }

  _take(c, type, ref) {
    c.type = type; c.prio = PRIORITY[type]; c.ref = ref;
    c.t = _sw.t; c.px = _sw.px; c.py = _sw.py; c.pz = _sw.pz; c.nx = _sw.nx; c.ny = _sw.ny; c.nz = _sw.nz;
  }

  /** @returns {number} STOP or CONTINUE (keep sweeping the rest of the step) */
  _resolveLiveContact(c, impactTime) {
    _n.set(c.nx, c.ny, c.nz);
    switch (c.type) {
      case C_HITTABLE: {
        const h = c.ref, info = this._hitInfo;
        info.t = c.t; info.point.set(c.hx, c.hy, c.hz); info.normal.copy(_n);
        let res;
        try { res = h.onBallHit ? h.onBallHit(this, info) : 'pass'; } catch (e) { warnOnce('hittable.onBallHit threw', e); res = 'pass'; }
        if (this.state !== BALL_STATE.LIVE) return STOP; // the object took the ball (collected, vanished...)
        if (res === 'block') {
          const point = info.point.clone(), normal = _n.clone();
          bounceVelocity(this.velocity, _n, BALL_PHYS.blockRestitution, BALL_PHYS.surfaceTangentKeep);
          this.position.addScaledVector(_n, 1e-3);
          game.events.emit(EV.BallBlocked, { ball: this, blocker: h, point, normal });
          this.makeFree(null, false);
          return STOP;
        }
        if (res === 'absorb') {
          this.velocity.multiplyScalar(BALL_PHYS.absorbKeep);
          this.makeFree(null, false);
          return STOP;
        }
        if (res === 'handled') return STOP;
        this._passedHittables.add(h);
        return CONTINUE;
      }
      case C_CATCH: {
        const p = c.ref;
        this._catchTried.add(p);
        _pt.set(c.px, c.py, c.pz);
        const q = p.combat.tryResolveCatch(this, _pt, impactTime);
        if ((q && q !== 'miss') || this.state !== BALL_STATE.LIVE) return STOP;
        return CONTINUE; // the body test decides now
      }
      case C_RECEIVE: {
        const p = c.ref;
        if (p.combat.receivePass(this, this.lastThrower) || this.state !== BALL_STATE.LIVE) return STOP;
        this.hitPlayers.add(p);
        return CONTINUE;
      }
      case C_PLAYER: {
        const p = c.ref;
        // Contact point on the body surface (the ball centre is one radius out along the normal).
        _pt.set(c.px, c.py, c.pz).addScaledVector(_n, -this.radius);
        if (!this.unblockable && !this._catchTried.has(p) && p.combat && typeof p.combat.tryResolveCatch === 'function') {
          this._catchTried.add(p);
          const q = p.combat.tryResolveCatch(this, _pt, impactTime);
          if ((q && q !== 'miss') || this.state !== BALL_STATE.LIVE) return STOP;
        }
        this.hitPlayers.add(p);
        if (this.isPass) {
          // Passes are soft lobs: they never hurt, they just bounce off whoever is in the way.
          this._deflectOffBody(_n);
          this.makeFree(null, false);
          return STOP;
        }
        const normal = _n.clone(), point = _pt.clone();
        const outcome = HitResolver.resolveHit(this, p, point, normal);
        if (this.state !== BALL_STATE.LIVE) return STOP;
        if (outcome === 'ignored' || this.pierce) return CONTINUE;
        this._deflectOffBody(normal);
        this.makeFree(null, false);
        return STOP;
      }
      case C_HELD: {
        const p = c.ref;
        const point = _pt.set(c.px, c.py, c.pz).addScaledVector(_n, -this.radius).clone(), normal = _n.clone();
        bounceVelocity(this.velocity, _n, BALL_PHYS.heldBlockRestitution, BALL_PHYS.surfaceTangentKeep);
        this.position.addScaledVector(_n, 1e-3);
        if (p.motor && typeof p.motor.addImpulse === 'function') {
          _v.set(-_n.x, 0, -_n.z);
          if (_v.lengthSq() > 1e-6) p.motor.addImpulse(_v.normalize().multiplyScalar(BALL_PHYS.heldBlockShove));
        }
        game.events.emit(EV.BallBlocked, { ball: this, blocker: p, point, normal });
        this.makeFree(null, false);
        return STOP;
      }
      case C_SURFACE: {
        const isFloor = c.ny > 0.6;
        const impact = bounceVelocity(this.velocity, _n, isFloor ? BALL_PHYS.floorRestitution : BALL_PHYS.wallRestitution, BALL_PHYS.surfaceTangentKeep);
        this._spinFromSurface(_n);
        this.position.addScaledVector(_n, 1e-4);
        const point = new THREE.Vector3(c.px, c.py, c.pz).addScaledVector(_n, -this.radius), normal = _n.clone();
        payloadCall(this.payload, 'onHitSurface', this, point, normal, isFloor);
        if (this.state !== BALL_STATE.LIVE) return STOP;
        const rallyReset = isFloor && this.rallyCount > 0;
        if (isFloor) this.rallyCount = 0;
        this._lastBounceEvent = game.time.now;
        game.events.emit(EV.BallBounced, { ball: this, point, normal, impactSpeed: impact, floor: isFloor, rallyReset, endedLive: true });
        this.makeFree(null, false);
        return STOP;
      }
      default: return STOP;
    }
  }

  /** Thud off a body: normal component reversed and heavily damped, tangential damped, a little pop-up. */
  _deflectOffBody(n) {
    const v = this.velocity;
    const vn = v.dot(n);
    if (vn < 0) {
      v.addScaledVector(n, -vn).multiplyScalar(BALL_PHYS.bodyTangentKeep).addScaledVector(n, -vn * BALL_PHYS.bodyRestitution);
    } else {
      v.multiplyScalar(BALL_PHYS.bodyTangentKeep);
    }
    v.y += BALL_PHYS.bodyDeflectLift;
    this.position.addScaledVector(n, 2e-3);
    this._spinFromSurface(n);
  }

  /** Spin matching the tangential slip after a contact (a bounce makes a rubber ball roll along). */
  _spinFromSurface(n) {
    _w.crossVectors(n, this.velocity).multiplyScalar(1 / Math.max(1e-3, this.radius));
    this.angularVelocity.lerp(_w, 0.6);
  }

  _stepFree(dt) {
    const v = this.velocity, r = this.radius;
    const floorY = game.court ? game.court.floorY : 0;
    const P = BALL_PHYS;

    if (this.grounded) {
      v.y = 0;
      this.position.y = floorY + r;
      const s = Math.hypot(v.x, v.z);
      if (s > 0) {
        const ns = Math.max(0, s - (P.rollingDecel + P.freeDrag * s * s) * dt);
        if (ns < P.restingSpeed) { v.x = 0; v.z = 0; } else { const f = ns / s; v.x *= f; v.z *= f; }
      }
    } else {
      v.y -= GRAVITY * dt;
      const sp = v.length();
      if (sp > 0) v.multiplyScalar(Math.max(0, 1 - P.freeDrag * sp * dt));
      this.angularVelocity.multiplyScalar(Math.max(0, 1 - P.angularDamping * dt));
    }

    // Legs push loose balls around (a resting ball is kicked by whoever walks into it).
    this._pushOutOfPlayers();

    if (this.grounded && v.x === 0 && v.z === 0 && v.y <= 0) { this.angularVelocity.set(0, 0, 0); return; }

    let tBase = 0;
    _from.copy(this.position);
    const c = this._contact;
    for (let iter = 0; iter < 4; iter++) {
      _to.copy(_from).addScaledVector(v, dt * (1 - tBase));
      c.type = C_NONE; c.t = Infinity; c.prio = 99; c.ref = null;
      this._surfaceContacts(_from, _to, c);
      if (c.type === C_NONE) { this.position.copy(_to); break; }
      const tAbs = tBase + (1 - tBase) * c.t;
      this.position.set(c.px, c.py, c.pz);
      this._resolveFreeContact(c, floorY);
      tBase = tAbs;
      _from.copy(this.position);
      if (tBase >= 1) break;
    }

    // Settle onto the floor once a bounce is too weak to leave it.
    if (!this.grounded && this.position.y <= floorY + r + 2e-3 && Math.abs(v.y) < P.bounceStopSpeed) {
      this.grounded = true;
      v.y = 0;
      this.position.y = floorY + r;
    }
    if (this.grounded) {
      if (this.position.y < floorY + r) this.position.y = floorY + r;
      // Rolling without slipping: w = up x v / r.
      this.angularVelocity.set(v.z, 0, -v.x).multiplyScalar(1 / Math.max(1e-3, r));
    }
  }

  _resolveFreeContact(c, floorY) {
    const P = BALL_PHYS, v = this.velocity;
    _n.set(c.nx, c.ny, c.nz);
    const isCourtFloor = c.ref === null && c.ny > 0.99;
    const isFloor = c.ny > 0.6;
    const approach = -v.dot(_n);
    let impact = 0;
    if (isCourtFloor && approach < P.bounceStopSpeed) {
      // Too soft to bounce: start rolling.
      this.grounded = true;
      v.y = 0;
      this.position.y = floorY + this.radius;
    } else {
      impact = bounceVelocity(v, _n, isFloor ? P.floorRestitution : P.wallRestitution, P.surfaceTangentKeep);
      this.position.addScaledVector(_n, 1e-4);
      this._spinFromSurface(_n);
      if (!isCourtFloor && _n.y < -0.5) this.grounded = false;
    }
    let rallyReset = false;
    if (isFloor && this.rallyCount > 0) { this.rallyCount = 0; rallyReset = true; }
    const now = game.time.now;
    if (impact >= P.minBounceEventSpeed && now - this._lastBounceEvent >= P.minBounceEventInterval) {
      this._lastBounceEvent = now;
      game.events.emit(EV.BallBounced, {
        ball: this, point: new THREE.Vector3(c.px, c.py, c.pz).addScaledVector(_n, -this.radius), normal: _n.clone(),
        impactSpeed: impact, floor: isFloor, rallyReset, endedLive: false,
      });
    }
  }

  /** Overlap push-out against player capsules with a restitution kick carrying the leg's velocity. */
  _pushOutOfPlayers() {
    const players = game.players;
    const r = this.radius, v = this.velocity;
    for (let i = 0; i < players.length; i++) {
      const p = players[i];
      if (!p || !p.position) continue;
      if (p.health && p.health.isAlive === false) continue; // ragdolls are handled by the physics world
      const R = bodyRadiusOf(p) + r, H = bodyHeightOf(p);
      const pos = p.position;
      const y0 = pos.y + bodyRadiusOf(p), y1 = pos.y + Math.max(bodyRadiusOf(p), H - bodyRadiusOf(p));
      const ay = this.position.y < y0 ? y0 : this.position.y > y1 ? y1 : this.position.y;
      _n.set(this.position.x - pos.x, this.position.y - ay, this.position.z - pos.z);
      const d2 = _n.lengthSq();
      if (d2 >= R * R) continue;
      const d = Math.sqrt(d2);
      if (d > 1e-5) _n.multiplyScalar(1 / d); else _n.set(Math.sin(p.yaw || 0), 0, Math.cos(p.yaw || 0));
      if (this.grounded) { _n.y = 0; if (_n.lengthSq() < 1e-6) _n.set(1, 0, 0); _n.normalize(); }
      this.position.addScaledVector(_n, R - d + 1e-3);
      // Relative velocity along the normal (the leg moves with the player's velocity).
      const pv = p.velocity;
      const rel = (v.x - (pv ? pv.x : 0)) * _n.x + (v.y - (pv ? pv.y : 0)) * _n.y + (v.z - (pv ? pv.z : 0)) * _n.z;
      if (rel < 0) v.addScaledVector(_n, -(1 + BALL_PHYS.footRestitution) * rel);
    }
  }

  _stepStasis(dt) {
    this._stasisPhase += dt;
    this._stasisRemaining -= dt;
    const bob = Math.sin(this._stasisPhase * Math.PI * 2 * BALL_PHYS.stasisBobFrequency) * BALL_PHYS.stasisBobAmplitude;
    this.position.copy(this._stasisAnchor);
    this.position.y += bob;
    if (this._stasisRemaining > 0) return;
    this._stasisRemaining = 0;
    this.position.copy(this._stasisAnchor);
    this.prevPosition.copy(this.position);
    if (this._stasisResume === BALL_STATE.LIVE) {
      // Time resumes: same flight, same velocity; everyone may be hit again (fresh grace from now).
      this.state = BALL_STATE.LIVE;
      this.velocity.copy(this._stasisVelocity);
      this.launchTime = game.time.now - BALL_PHYS.throwerGrace;
      this._catchTried.clear();
    } else {
      this.makeFree(_v.set(0, 0, 0), false);
    }
  }

  _stepDespawned(dt) {
    if (!Number.isFinite(this._despawnRemaining)) return;
    this._despawnRemaining -= dt;
    if (this._despawnRemaining > 0) return;
    this._despawnRemaining = 0;
    if (this.isAbilityBall) { this._despawnDone = true; return; }
    this.resetTo(this._hasRespawnPos ? this._respawnPos : this.homePosition);
    game.vfx?.play?.('teleport', this.position, { scale: 0.35 });
  }

  /** Ends the live flight: clears flight flags and runs payload.onEnded. */
  _endLive(reason) {
    const payload = this.payload;
    this.payload = null;
    this.isPass = false;
    this.unblockable = false;
    this.pierce = false;
    this.isCounter = false;
    this.gravityScale = 1;
    this.lockedTarget = null;
    this._stasisRemaining = 0;
    this.hitPlayers.clear();
    this._passedHittables.clear();
    this._catchTried.clear();
    // Match balls lose their ability look as soon as the flight ends; ability balls keep it while they fade.
    if (!this.isAbilityBall) {
      this.setStyle('standard');
      this.radius = BALL_RADIUS;
    }
    payloadCall(payload, 'onEnded', this, reason);
  }

  _detachFromHolder() {
    const h = this.holder;
    if (!h) return;
    this.holder = null;
    if (h.combat && h.combat.heldBall === this && typeof h.combat._forgetBall === 'function') h.combat._forgetBall(this);
  }

  /** Position in the holder's right hand (avatar socket), else a plausible carry point. */
  _followHolder() {
    const h = this.holder;
    if (!h) return;
    const sock = (h.avatar && h.avatar.rightHandSocket) || h.ballSocket || h.socket || null;
    if (sock && typeof sock.getWorldPosition === 'function') {
      sock.getWorldPosition(this.position);
      if (!h.position || this.position.distanceToSquared(h.position) < 4 && Number.isFinite(this.position.x)) return;
    }
    if (h.position) {
      const yaw = h.yaw || 0;
      const fx = Math.sin(yaw), fz = Math.cos(yaw);   // forward
      const rx = -Math.cos(yaw), rz = Math.sin(yaw);  // right (yaw 0 faces +Z, right is -X)
      if (h.chestPosition || h.height) chestOf(h, this.position); else this.position.copy(h.position).setY(h.position.y + 1.2);
      this.position.x += rx * 0.26 + fx * 0.22;
      this.position.z += rz * 0.26 + fz * 0.22;
      this.position.y -= 0.12;
    } else if (h.root && h.root.getWorldPosition) {
      h.root.getWorldPosition(this.position);
    }
  }

  // ================================================================== presentation
  /** Per-frame look (scaled dt so effects freeze during hitstop). */
  update(dt, realDt) {
    const L = this._look;
    if (L.flicker > 0 && this.state !== BALL_STATE.DESPAWNED) {
      this._flickerPhase += dt;
      const t = this._flickerPhase;
      const n = Math.sin(t * 31.7) * 0.5 + Math.sin(t * 17.3 + 1.3) * 0.3 + Math.sin(t * 53.1 + 0.4) * 0.2;
      this.material.emissiveIntensity = L.emissiveIntensity * (1 + L.flicker * n);
    }
  }

  /** After every system updated (hand pose final): place, spin, stretch, trail. */
  lateUpdate(dt, realDt) {
    if (this.state === BALL_STATE.DESPAWNED || this.recycled) { this.trail.update(dt, this.root.position, 0, false, null); return; }
    if (this.state === BALL_STATE.HELD) {
      this._followHolder();
      this.root.position.copy(this.position);
      this._applyHolderCloak();
    } else {
      if (this.state === BALL_STATE.LIVE || this.state === BALL_STATE.FREE) {
        // Interpolate between the last two fixed steps for smooth motion at any frame rate.
        const a = Math.min(1, Math.max(0, game.alpha || 0));
        this.root.position.lerpVectors(this.prevPosition, this.position, a);
      } else {
        this.root.position.copy(this.position);
      }
      if (!this.visual.visible) this.visual.visible = true;
    }

    // Spin (world-space angular velocity integrated into an orientation).
    const w = this.angularVelocity.length();
    if (w > 1e-4 && dt > 0) {
      _w.copy(this.angularVelocity).multiplyScalar(1 / w);
      _q.setFromAxisAngle(_w, w * dt);
      this._spin.premultiply(_q).normalize();
    }

    // Speed stretch along the flight (live only), smoothed on real time so it relaxes even in hitstop.
    const speed = this.velocity.length();
    const live = this.state === BALL_STATE.LIVE;
    const target = live ? Math.min(BALL_PHYS.maxStretch, 1 + BALL_PHYS.stretchPerMs * speed) : 1;
    this._stretchAmount += (target - this._stretchAmount) * (1 - Math.exp(-(realDt || dt) * 25));
    if (live && speed > 0.5) {
      _v.copy(this.velocity).multiplyScalar(1 / speed);
      this._stretch.quaternion.setFromUnitVectors(_UP, _v);
    }
    const size = (this.radius / BALL_RADIUS) * (1 - Math.min(1, this._fade));
    const s = this._stretchAmount, side = 1 / Math.sqrt(s);
    this._stretch.scale.set(size * side, size * s, size * side);
    // Counter-rotate so the stretch frame never drags the pebble pattern around: mesh = stretch^-1 * spin.
    _qInv.copy(this._stretch.quaternion).invert();
    this.mesh.quaternion.multiplyQuaternions(_qInv, this._spin);

    const emitting = live || (this.state === BALL_STATE.FREE && !this.grounded && speed > 8);
    this.trail.update(dt, this.root.position, speed, emitting, game.camera, this.radius / BALL_RADIUS);
  }

  /** A cloaked holder's ball is hidden from enemies of the local player (it would give the position away). */
  _applyHolderCloak() {
    const h = this.holder;
    let visible = true;
    if (h && h.status && typeof h.status.has === 'function' && h.status.has('cloaked') && !h.status.has('revealed')) {
      const local = game.localPlayer;
      visible = !!(local && !game.areEnemies(local, h));
    }
    if (this.visual.visible !== visible) this.visual.visible = visible;
  }

  dispose() {
    this._detachFromHolder();
    this.root.removeFromParent();
    this.trail.dispose();
    this.material.dispose();
  }
}

/** Realistic red rubber: pebbled normal map, worn-top roughness, thin clearcoat (fresh moulded sheen). */
export function createBallMaterial() {
  const tex = getBallTextures();
  const L = BALL_LOOK.standard;
  return new THREE.MeshPhysicalMaterial({
    color: L.color,
    map: tex.map,
    normalMap: tex.normalMap,
    normalScale: new THREE.Vector2(0.9, 0.9),
    roughnessMap: tex.roughnessMap,
    roughness: L.roughness,
    metalness: 0,
    clearcoat: L.clearcoat,
    clearcoatRoughness: L.clearcoatRoughness,
    emissive: 0x000000,
    emissiveIntensity: 0,
    envMapIntensity: 0.9,
  });
}
