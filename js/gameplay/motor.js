// ---------------------------------------------------------------------------------------------------------------
// Motor - custom kinematic character physics for one Player (no rigid body). Web port of the Unity PlayerMotor.
//
// Every fixed step (scaled time, 60 Hz):
//   1. timers (jump/slide cooldowns, knockback control recovery), speed multiplier (modifiers x Status.moveSpeedMul)
//      and traction (min of traction modifiers; Status 'slippery' feeds one),
//   2. planar motion: the along-track component moves toward the wish speed with a smooth acceleration curve
//      (explosive from standstill, tapering near top speed) or brakes with `deceleration`; sideways drift is killed by
//      a lateral grip. Everything scales with traction (ice = heavy inertia) and knockback control. Air control is
//      limited and never brakes (momentum preserved). Slides keep their momentum and lose speed to friction,
//   3. vertical motion: gravity x gravityMul, jump v = sqrt(2 g h), ground at the court floor (or collider tops),
//   4. integrate, resolve AABB colliders (game.arena.colliders) and teammate capsules, clamp into the confinement
//      region (removing outward velocity). A U-shaped outfield region has a hole (the opponent's half): the step is
//      limited predictively at the hole wall and resolved out through the nearest exit edge (world/courtMath.js),
//   5. turn toward the explicit facing request (aim) > slide direction > move direction, and publish lean readouts
//      (`yawRate`, `planarAccel`).
// The motor writes `player.position` (= player.root.position), `player.yaw` and `player.velocity` directly.
// ---------------------------------------------------------------------------------------------------------------
import * as THREE from 'three';
import { game } from '../game.js';
import { GRAVITY } from '../core/constants.js';
import {
  clamp, clamp01, moveTowards, moveTowardsAngle, deltaAngle, accelerationCurve, jumpSpeed, slideStartSpeed,
  slideFrictionStep, knockbackControl, clampAxis,
} from './player_math.js';
import { clampOutOfHole } from '../world/courtMath.js';

/** Movement regimes requested by the state machine. */
export const MOTOR_MODE = Object.freeze({
  WALK: 'walk', SPRINT: 'sprint', SLIDE: 'slide', AIR: 'air', CHARGING: 'charging', CATCHING: 'catching', LOCKED: 'locked',
});

const DEG = Math.PI / 180;

// Module-scope temporaries (no per-step allocation).
const _planar = new THREE.Vector3();
const _lateral = new THREE.Vector3();
const _desired = new THREE.Vector3();
const _axis = { p: 0, v: 0 };
const _hs = { px: 0, pz: 0, vx: 0, vz: 0 };
const ZERO = new THREE.Vector3();

/** Fallback profile (mirrors roster BASE_MOVEMENT) so a bare Motor never reads undefined. */
const FALLBACK_PROFILE = Object.freeze({
  walkSpeed: 4.6, sprintSpeed: 7.4, chargingSpeedMul: 0.6, catchingSpeedMul: 0.45,
  acceleration: 34, deceleration: 40, airControl: 0.35, turnSpeed: 720, jumpHeight: 1.05, gravityMul: 1.8,
  slideBoost: 1.2, slideFriction: 6.5, slideDuration: 0.8, slideCooldown: 0.5,
});

/** Moves a vector's length toward zero by `maxDelta` (in place). */
function shrinkVector(v, maxDelta) {
  const len = v.length();
  if (len <= maxDelta || len < 1e-9) v.set(0, 0, 0);
  else v.multiplyScalar((len - maxDelta) / len);
  return v;
}

export class Motor {
  /** Motor-level tuning (per game, not per hero). Hero numbers live in the profile (roster heroMovement). */
  static defaults = {
    // ground / air
    groundSnapDistance: 0.25,     // grounded bodies stick to ground up to this far below the feet (m)
    groundTolerance: 0.02,        // gap that still counts as a landing (m)
    stepHeight: 0.3,              // collider tops up to this far above the feet are stepped onto, not walls (m)
    maxFallSpeed: 40,             // terminal fall speed (m/s)
    airTurnMul: 0.5,              // bodies cannot pivot freely mid-air
    jumpCooldown: 0.2,            // s between two jumps
    jumpGroundIgnore: 0.12,       // s after a jump / upward impulse during which ground contact is ignored
    // handling
    lateralGrip: 1.25,            // sideways grip as a multiple of deceleration (tight turns on grip, drifts on ice)
    minTraction: 0.06,            // floor for traction (0 would leave a player helpless on ice)
    maxReportedAccel: 80,         // planarAccel readout clamp (m/s^2), protects procedural lean from collision spikes
    accelCurveStart: 1.3,         // acceleration multiplier from standstill
    accelCurveEnd: 0.4,           // acceleration multiplier at top speed
    accelCurveExponent: 2,
    // slide
    slideMinSpeed: 1.6,           // a slide ends below this speed (m/s)
    slideSteerRate: 35 * DEG,     // stick steering while sliding (rad/s, scaled by traction)
    slideIceFrictionScale: 0.3,   // slide friction multiplier at zero traction
    slideMaxAirTime: 0.2,         // a slide that leaves the ground longer than this ends (s)
    slideHeightScale: 0.55,       // capsule height while sliding (slides duck under chest-high throws)
    // knockback
    knockbackFullLossSpeed: 5,    // planar velocity change (m/s) at which control is fully lost
    knockbackMinControl: 0.1,     // control left right after a full knockback
    knockbackRecoveryTime: 0.45,  // s to regain full control
    upwardImpulseThreshold: 0.5,  // an impulse with more vertical speed than this launches the body (m/s)
    // bodies
    maxInsideDepth: 1.0,          // a collider the capsule centre is deeper than this inside is treated as a volume, not a wall
    teammatePushShare: 0.5,       // share of an overlap this body resolves (the other body resolves the rest)
  };

  /**
   * @param {import('./player.js').Player} player owner (needs position, yaw, velocity, radius, height)
   * @param {object} profile merged movement profile (roster heroMovement(hero))
   */
  constructor(player, profile = {}) {
    this.player = player;
    this.profile = { ...FALLBACK_PROFILE, ...(profile || {}) };
    this.tuning = { ...Motor.defaults };

    /** World velocity (m/s). Shared with player.velocity. */
    this.velocity = (player && player.velocity) || new THREE.Vector3();
    this.mode = MOTOR_MODE.WALK;

    // ---- readouts
    this.planarSpeed = 0;
    this.isGrounded = true;
    /** Signed body yaw rate (rad/s). + = turning right (clockwise seen from above). Drives procedural lean roll. */
    this.yawRate = 0;
    /** Planar acceleration of the last step (m/s^2, clamped). Drives procedural lean pitch. */
    this.planarAccel = new THREE.Vector3();
    this.isSliding = false;
    this.canJump = true;
    this.canSlide = true;
    /** Top speed of the current mode after all multipliers (m/s). While sliding: the slide speed. */
    this.maxSpeed = this.profile.walkSpeed;
    /** Product of speed modifiers and Status.moveSpeedMul. */
    this.speedMul = 1;
    /** 1 = full grip, toward 0 = ice. */
    this.traction = 1;
    /** 0..1 movement control (drops after knockback, recovers over knockbackRecoveryTime). */
    this.control = 1;
    this.airTime = 0;
    this.slideRemaining = 0;
    this.slideSpeed = 0;
    this.slideDir = new THREE.Vector3(0, 0, 1);
    /** Capsule height multiplier (slides lower the body). Player.height reads it. */
    this.heightScale = 1;
    this.isFrozen = false;
    /** When true this body neither pushes nor is pushed by other players. */
    this.noCollide = false;

    // ---- callbacks (assigned by Player)
    /** @type {(impactSpeed:number)=>void|null} */ this.onLand = null;
    /** @type {()=>void|null} */ this.onJump = null;
    /** @type {()=>void|null} */ this.onSlideStart = null;
    /** @type {()=>void|null} */ this.onSlideEnd = null;

    // ---- internals
    this._moveDir = new THREE.Vector3();
    this._moveMag = 0;
    this._facing = new THREE.Vector3();
    this._facingFrame = -Infinity;
    this._jumpRequested = false;
    this._jumpCooldown = 0;
    this._groundIgnore = 0;
    this._slideCooldown = 0;
    this._airMaxSpeed = 0;
    this._prevPlanar = new THREE.Vector3();
    this._prevYaw = player && Number.isFinite(player.yaw) ? player.yaw : 0;
    this._speedMods = new Map();
    this._tractionMods = new Map();
    this._speedModProduct = 1;
    this._tractionMin = 1;
    this._bounds = { minX: -Infinity, maxX: Infinity, minZ: -Infinity, maxZ: Infinity, hole: null };
    this._hasBounds = false;
    /** Hole of the confinement region (U outfield), preallocated; _hasHole = false for plain boxes. */
    this._hole = { minX: 0, maxX: 0, minZ: 0, maxZ: 0 };
    this._hasHole = false;
  }

  // ------------------------------------------------------------------ derived
  /** Effective gravity (m/s^2) = g x profile.gravityMul. */
  get gravity() { return GRAVITY * Math.max(0, this.profile.gravityMul ?? 1); }
  /** Active confinement bounds or null ({minX,maxX,minZ,maxZ,hole}: hole null for a plain box). */
  get confinement() {
    if (!this._hasBounds) return null;
    this._bounds.hole = this._hasHole ? this._hole : null;
    return this._bounds;
  }
  get moveDirection() { return this._moveDir; }
  get moveMagnitude() { return this._moveMag; }
  get slideCooldownRemaining() { return this._slideCooldown; }

  // ------------------------------------------------------------------ commands
  /** Wish direction (world, planar) and magnitude 0..1 for this frame. */
  setMove(dir, mag = 1) {
    if (!dir) { this._moveDir.set(0, 0, 0); this._moveMag = 0; return; }
    const x = dir.x || 0, z = dir.z || 0;
    const len = Math.hypot(x, z);
    const m = Number.isFinite(mag) ? clamp01(mag) : 0;
    if (len < 1e-4 || m <= 1e-4) { this._moveDir.set(0, 0, 0); this._moveMag = 0; return; }
    this._moveDir.set(x / len, 0, z / len);
    this._moveMag = m;
  }

  /** @param {string} mode MOTOR_MODE value. Leaving 'slide' ends the slide but keeps its momentum. */
  setMode(mode) {
    if (this.isSliding && mode !== MOTOR_MODE.SLIDE) this.endSlide();
    this.mode = mode;
  }

  /**
   * Requests facing `dir` (planar) this frame; overrides the move direction while re-issued every frame.
   * @param {THREE.Vector3} dir
   * @param {boolean} [instant] snap now instead of turning at turnSpeed
   */
  setFacing(dir, instant = false) {
    if (!dir || this.isFrozen) return;
    const x = dir.x || 0, z = dir.z || 0;
    const len = Math.hypot(x, z);
    if (len < 1e-5) return;
    this._facing.set(x / len, 0, z / len);
    this._facingFrame = game.time ? game.time.frame : 0;
    if (instant && this.player) {
      this.player.yaw = Math.atan2(this._facing.x, this._facing.z);
      this._prevYaw = this.player.yaw;
    }
  }

  /** Starts a jump (applied at the next fixed step). @returns {boolean} */
  tryJump() {
    this._refreshCaps();
    if (!this.canJump) return false;
    this._jumpRequested = true;
    this.isGrounded = false;
    this._groundIgnore = this.tuning.jumpGroundIgnore;
    this._jumpCooldown = this.tuning.jumpCooldown;
    this.airTime = 0;
    this._airMaxSpeed = Math.max(this._currentPlanarSpeed(), this.profile.walkSpeed * this.speedMul);
    this._refreshCaps();
    if (this.onJump) this.onJump();
    return true;
  }

  /**
   * Starts a slide in `dir` (falls back to the velocity, then the facing). Momentum preserved:
   * start speed = max(current planar speed, walkSpeed x speedMul) x slideBoost.
   * @param {THREE.Vector3} [dir]
   * @param {{speed?:number, duration?:number, force?:boolean}} [opts] abilities: explicit speed/duration; `force`
   *        ignores the cooldown / grounded requirement (never frozen, rooted or dodge-disabled bodies).
   * @returns {boolean}
   */
  trySlide(dir, opts = null) {
    this._refreshCaps();
    const force = !!(opts && opts.force);
    if (force) {
      if (this.isFrozen || this.mode === MOTOR_MODE.LOCKED || this._hasStatus('rooted') || this._hasStatus('dodgeDisabled')) return false;
    } else if (!this.canSlide) {
      return false;
    }
    const p = this.profile;
    const current = this._currentPlanarSpeed();
    let start = slideStartSpeed(current, p.walkSpeed, this.speedMul, p.slideBoost);
    if (opts && opts.speed > 0) start = Math.max(current, opts.speed);
    const duration = opts && opts.duration > 0 ? opts.duration : p.slideDuration;

    let x = dir ? dir.x || 0 : 0, z = dir ? dir.z || 0 : 0;
    if (x * x + z * z < 1e-6) { x = this.velocity.x; z = this.velocity.z; }
    if (x * x + z * z < 1e-4 && this.player) { x = Math.sin(this.player.yaw); z = Math.cos(this.player.yaw); }
    const len = Math.hypot(x, z) || 1;
    this.slideDir.set(x / len, 0, z / len);
    this.slideSpeed = start;
    this.slideRemaining = Math.max(0.05, duration);
    this.isSliding = true;
    this.mode = MOTOR_MODE.SLIDE;
    this.velocity.x = this.slideDir.x * start;
    this.velocity.z = this.slideDir.z * start;
    this.planarSpeed = start;
    this.maxSpeed = start;
    this.heightScale = this.tuning.slideHeightScale;
    this._refreshCaps();
    if (this.onSlideStart) this.onSlideStart();
    return true;
  }

  /** Ends the running slide (momentum is then braked by normal locomotion) and starts the slide cooldown. */
  endSlide() {
    if (!this.isSliding) return;
    this.isSliding = false;
    this.slideRemaining = 0;
    this.slideSpeed = 0;
    this._slideCooldown = Math.max(0, this.profile.slideCooldown || 0);
    if (this.mode === MOTOR_MODE.SLIDE) this.mode = MOTOR_MODE.WALK;
    this.heightScale = 1;
    this._refreshCaps();
    if (this.onSlideEnd) this.onSlideEnd();
  }

  /**
   * Instant velocity change (m/s) - knockback, shockwaves, forced jumps. Planar knockback temporarily removes movement
   * control; an upward component above `upwardImpulseThreshold` launches the body.
   * @param {THREE.Vector3} v
   */
  addImpulse(v) {
    if (!v || this.isFrozen) return;
    if (!Number.isFinite(v.x) || !Number.isFinite(v.y) || !Number.isFinite(v.z)) return;
    this.velocity.add(v);
    const t = this.tuning;
    if (v.y > t.upwardImpulseThreshold) {
      this.isGrounded = false;
      this._groundIgnore = Math.max(this._groundIgnore, t.jumpGroundIgnore);
      this._airMaxSpeed = Math.max(this._currentPlanarSpeed(), this.profile.walkSpeed * this.speedMul);
    }
    const planarImpulse = Math.hypot(v.x, v.z);
    this.control = knockbackControl(this.control, planarImpulse, t.knockbackFullLossSpeed, t.knockbackMinControl);
    // A real shove knocks you out of a slide (the slide would otherwise project the shove away).
    if (this.isSliding && planarImpulse > 2) this.endSlide();
    this.planarSpeed = this._currentPlanarSpeed();
    this._refreshCaps();
  }

  /** Overrides the planar velocity (dashes, tackles). Vertical velocity is kept. */
  setPlanarVelocity(v) {
    if (!v || this.isFrozen) return;
    if (!Number.isFinite(v.x) || !Number.isFinite(v.z)) return;
    this.velocity.x = v.x;
    this.velocity.z = v.z;
    this.planarSpeed = Math.hypot(v.x, v.z);
    if (this.isSliding) this.slideSpeed = Math.max(0, v.x * this.slideDir.x + v.z * this.slideDir.z);
  }

  /** Moves the body instantly (velocity cleared, slide ended, grounded if at/below ground). */
  teleport(pos, yaw) {
    const pl = this.player;
    if (!pl) return;
    if (this.isSliding) this.endSlide();
    if (pos && Number.isFinite(pos.x) && Number.isFinite(pos.y) && Number.isFinite(pos.z)) pl.position.copy(pos);
    if (Number.isFinite(yaw)) pl.yaw = yaw;
    this.velocity.set(0, 0, 0);
    this._resetMotionState();
    const gy = this._groundHeight(pl.position);
    if (pl.position.y <= gy + this.tuning.groundSnapDistance) {
      pl.position.y = gy;
      this.isGrounded = true;
    } else {
      this.isGrounded = false;
      this._airMaxSpeed = this.profile.walkSpeed * this.speedMul;
    }
    this._prevYaw = pl.yaw;
    this._facingFrame = -Infinity;
    this._refreshCaps();
  }

  /** Freezes the body in place (frozen, grabbed, eliminated, rewind playback): no input, no gravity. */
  setFrozen(b) {
    const frozen = !!b;
    if (this.isFrozen === frozen) return;
    if (frozen && this.isSliding) this.endSlide();
    this.isFrozen = frozen;
    this.velocity.set(0, 0, 0);
    this._resetMotionState();
    if (!frozen && this.player) {
      this._prevYaw = this.player.yaw;
      const gy = this._groundHeight(this.player.position);
      this.isGrounded = this.player.position.y <= gy + this.tuning.groundSnapDistance;
    }
    this._refreshCaps();
  }

  /** Multiplicative speed modifier keyed by source (e.g. an ability instance). */
  setSpeedModifier(src, mul) {
    this._speedMods.set(src ?? 'anonymous', Math.max(0, Number.isFinite(mul) ? mul : 1));
    this._recomputeSpeedMods();
  }
  removeSpeedModifier(src) {
    if (this._speedMods.delete(src ?? 'anonymous')) this._recomputeSpeedMods();
  }
  /** Traction modifier keyed by source; the lowest traction wins (1 = grip, 0 = ice). */
  setTractionModifier(src, t) {
    this._tractionMods.set(src ?? 'anonymous', clamp01(Number.isFinite(t) ? t : 1));
    this._recomputeTractionMods();
  }
  removeTractionModifier(src) {
    if (this._tractionMods.delete(src ?? 'anonymous')) this._recomputeTractionMods();
  }
  clearModifiers() {
    this._speedMods.clear();
    this._tractionMods.clear();
    this._recomputeSpeedMods();
    this._recomputeTractionMods();
  }

  /**
   * @param {{minX:number,maxX:number,minZ:number,maxZ:number,hole?:object|null}|null} bounds already inset for the
   *        capsule (Court.confinement); a missing hole means a plain box (Gouki's extended tackle box has none)
   */
  setConfinement(bounds) {
    if (!bounds) { this._hasBounds = false; this._hasHole = false; return; }
    this._bounds.minX = bounds.minX; this._bounds.maxX = bounds.maxX;
    this._bounds.minZ = bounds.minZ; this._bounds.maxZ = bounds.maxZ;
    const h = bounds.hole;
    this._hasHole = !!h;
    if (h) { this._hole.minX = h.minX; this._hole.maxX = h.maxX; this._hole.minZ = h.minZ; this._hole.maxZ = h.maxZ; }
    this._hasBounds = true;
  }

  /** Keeps (pos, vel) out of the confinement hole: predictive when dt > 0, resolve when dt = 0. */
  _clampHole(pos, vel, dt) {
    if (!this._hasHole) return;
    _hs.px = pos.x; _hs.pz = pos.z; _hs.vx = vel.x; _hs.vz = vel.z;
    if (!clampOutOfHole(_hs, this._hole, this._bounds, dt)) return;
    pos.x = _hs.px; pos.z = _hs.pz; vel.x = _hs.vx; vel.z = _hs.vz;
  }

  // ------------------------------------------------------------------ simulation
  /** One fixed simulation step on scaled time. */
  fixedUpdate(dt) {
    if (!(dt > 0) || !this.player) return;
    const pl = this.player, pos = pl.position, vel = this.velocity, t = this.tuning, p = this.profile;

    this._tickTimers(dt);
    this._refreshMultipliers();

    if (this.isFrozen) {
      // Somebody else owns the body (freeze, grab, rewind playback, ragdoll): no input, no gravity.
      this._clearReadouts();
      this._prevYaw = pl.yaw;
      this._refreshCaps();
      return;
    }

    const wasGrounded = this.isGrounded;
    let grounded = wasGrounded && this._groundIgnore <= 0 && !this._jumpRequested;

    // ---- vertical launch
    if (this._jumpRequested) {
      this._jumpRequested = false;
      grounded = false;
      vel.y = jumpSpeed(this.gravity, p.jumpHeight);
    }
    if (grounded) this.airTime = 0; else this.airTime += dt;

    // ---- planar motion
    _planar.set(vel.x, 0, vel.z);
    this.maxSpeed = this._modeMaxSpeed();
    if (this.isSliding) this._slideStep(_planar, grounded, dt);
    else this._locomotionStep(_planar, grounded, dt);
    vel.x = _planar.x;
    vel.z = _planar.z;

    // ---- gravity (trapezoidal: exact for constant acceleration, so the jump apex is exactly jumpHeight)
    let vyAvg = 0;
    if (grounded) vel.y = 0;
    else {
      const vy0 = vel.y;
      vel.y = Math.max(vy0 - this.gravity * dt, -t.maxFallSpeed);
      vyAvg = 0.5 * (vy0 + vel.y);
    }

    // ---- predictive confinement (never step past a line this step)
    if (this._hasBounds) {
      const b = this._bounds;
      _axis.p = pos.x; _axis.v = vel.x; clampAxis(_axis, b.minX, b.maxX, dt); pos.x = _axis.p; vel.x = _axis.v;
      _axis.p = pos.z; _axis.v = vel.z; clampAxis(_axis, b.minZ, b.maxZ, dt); pos.z = _axis.p; vel.z = _axis.v;
      this._clampHole(pos, vel, dt);
    }

    // ---- integrate + resolve
    pos.x += vel.x * dt;
    pos.z += vel.z * dt;
    pos.y += vyAvg * dt;
    this._collideColliders(pos, vel);
    this._separatePlayers(pos, vel);
    if (this._hasBounds) {
      const b = this._bounds;
      _axis.p = pos.x; _axis.v = vel.x; clampAxis(_axis, b.minX, b.maxX, 0); pos.x = _axis.p; vel.x = _axis.v;
      _axis.p = pos.z; _axis.v = vel.z; clampAxis(_axis, b.minZ, b.maxZ, 0); pos.z = _axis.p; vel.z = _axis.v;
      this._clampHole(pos, vel, 0);
    }

    // ---- ground contact
    let landedSpeed = -1;
    const gy = this._groundHeight(pos);
    if (grounded) {
      if (pos.y - gy <= t.groundSnapDistance) {
        pos.y = gy; // stick (also steps up onto low collider tops)
      } else {
        grounded = false; // walked off a ledge
        this._airMaxSpeed = Math.max(Math.hypot(vel.x, vel.z), p.walkSpeed * this.speedMul);
      }
    } else if (pos.y <= gy + t.groundTolerance && vel.y <= 0 && this._groundIgnore <= 0) {
      landedSpeed = Math.max(0, -vel.y);
      pos.y = gy;
      vel.y = 0;
      grounded = true;
      this.airTime = 0;
    } else if (pos.y < gy) {
      pos.y = gy; // safety: never sink through the floor
      if (vel.y < 0) vel.y = 0;
    }
    this.isGrounded = grounded;

    // ---- facing + lean readouts
    this._updateFacing(grounded, dt);
    const ax = (vel.x - this._prevPlanar.x) / dt, az = (vel.z - this._prevPlanar.z) / dt;
    this.planarAccel.set(ax, 0, az);
    const aLen = Math.hypot(ax, az);
    if (aLen > t.maxReportedAccel) this.planarAccel.multiplyScalar(t.maxReportedAccel / aLen);
    this._prevPlanar.set(vel.x, 0, vel.z);
    this.planarSpeed = Math.hypot(vel.x, vel.z);
    if (this.isSliding) this.maxSpeed = this.planarSpeed;
    this._refreshCaps();

    if (landedSpeed >= 0 && this.onLand) this.onLand(landedSpeed);
  }

  // ------------------------------------------------------------------ locomotion
  _locomotionStep(planar, grounded, dt) {
    const p = this.profile, t = this.tuning;
    const voluntary = this.mode !== MOTOR_MODE.LOCKED && !this._hasStatus('rooted');
    const targetSpeed = voluntary ? this.maxSpeed * this._moveMag : 0;
    const dir = this._moveDir;

    if (grounded) {
      const grip = Math.max(t.minTraction, this.traction) * this.control;
      const decel = Math.max(0, p.deceleration) * grip;
      if (targetSpeed > 0.01) {
        let along = planar.dot(dir);
        _lateral.copy(planar).addScaledVector(dir, -along);
        const top = Math.max(0.01, this.maxSpeed);
        const accel = Math.max(0, p.acceleration) * grip *
          accelerationCurve(Math.max(0, along) / top, t.accelCurveStart, t.accelCurveEnd, t.accelCurveExponent);
        // Below target: accelerate along the curve. Above (sprint released, slowed): brake. Reversing: strongest.
        let rate = along < targetSpeed ? accel : decel;
        if (along < 0) rate = Math.max(accel, decel);
        along = moveTowards(along, targetSpeed, rate * dt);
        // Sideways drift dies quickly on grip, slowly on ice (wide drifting arcs).
        shrinkVector(_lateral, decel * t.lateralGrip * dt);
        planar.copy(dir).multiplyScalar(along).add(_lateral);
      } else {
        shrinkVector(planar, decel * dt);
      }
      return;
    }

    // Airborne: limited steering, momentum preserved (no braking above the target, no air drag at these speeds).
    if (targetSpeed > 0.01) {
      const airRate = Math.max(0, p.acceleration) * clamp01(p.airControl ?? 0.35) * this.control;
      let along = planar.dot(dir);
      _lateral.copy(planar).addScaledVector(dir, -along);
      if (along < targetSpeed) along = moveTowards(along, targetSpeed, airRate * dt);
      shrinkVector(_lateral, airRate * 0.5 * dt);
      planar.copy(dir).multiplyScalar(along).add(_lateral);
    }
  }

  _slideStep(planar, grounded, dt) {
    const p = this.profile, t = this.tuning;
    // Momentum preserved: keep whatever speed survived collisions along the slide direction.
    let speed = Math.max(0, planar.dot(this.slideDir));
    // Slight stick steering (less on ice).
    if (this._moveMag > 0.1 && this.mode !== MOTOR_MODE.LOCKED) {
      const maxRad = t.slideSteerRate * Math.max(t.minTraction, this.traction) * dt;
      const cur = Math.atan2(this.slideDir.x, this.slideDir.z);
      const target = Math.atan2(this._moveDir.x, this._moveDir.z);
      const yaw = moveTowardsAngle(cur, target, maxRad);
      this.slideDir.set(Math.sin(yaw), 0, Math.cos(yaw));
    }
    // Friction loss (ice slides go further). No friction while airborne.
    if (grounded) speed = slideFrictionStep(speed, p.slideFriction, this.traction, t.slideIceFrictionScale, dt);
    this.slideSpeed = speed;
    this.slideRemaining -= dt;
    planar.copy(this.slideDir).multiplyScalar(speed);
    if (this.slideRemaining <= 0 || speed < t.slideMinSpeed || (!grounded && this.airTime > t.slideMaxAirTime)) this.endSlide();
  }

  _modeMaxSpeed() {
    const p = this.profile, m = this.speedMul;
    switch (this.mode) {
      case MOTOR_MODE.SPRINT: return p.sprintSpeed * m;
      case MOTOR_MODE.CHARGING: return p.walkSpeed * clamp01(p.chargingSpeedMul ?? 0.6) * m;
      case MOTOR_MODE.CATCHING: return p.walkSpeed * clamp01(p.catchingSpeedMul ?? 0.45) * m;
      case MOTOR_MODE.AIR: return Math.max(this._airMaxSpeed, p.walkSpeed * m);
      case MOTOR_MODE.SLIDE: return this.isSliding ? this.slideSpeed : p.walkSpeed * m;
      case MOTOR_MODE.LOCKED: return 0;
      default: return p.walkSpeed * m;
    }
  }

  // ------------------------------------------------------------------ facing
  _updateFacing(grounded, dt) {
    const pl = this.player;
    const current = pl.yaw;
    const rate = Math.max(0, this.profile.turnSpeed ?? 720) * DEG * (grounded ? 1 : this.tuning.airTurnMul);
    // Priority: explicit facing (aim, re-issued each frame) > slide direction > move direction.
    const frame = game.time ? game.time.frame : 0;
    let has = false;
    if (frame - this._facingFrame <= 1) { _desired.copy(this._facing); has = true; }
    else if (this.isSliding) { _desired.copy(this.slideDir); has = true; }
    else if (this.mode !== MOTOR_MODE.LOCKED && this._moveMag > 0.1) { _desired.copy(this._moveDir); has = true; }

    let yaw = current;
    if (has && (_desired.x !== 0 || _desired.z !== 0)) {
      yaw = moveTowardsAngle(current, Math.atan2(_desired.x, _desired.z), rate * dt);
      pl.yaw = yaw;
    }
    // Yaw 0 faces +Z; increasing yaw turns toward +X, which is a LEFT turn for a body facing +Z (right = -X).
    // The contract wants + = turning right, hence the sign flip.
    this.yawRate = -deltaAngle(this._prevYaw, yaw) / dt;
    this._prevYaw = yaw;
  }

  // ------------------------------------------------------------------ world queries
  /** Ground height below `pos`: court floor, or the top of a collider within step height under the capsule centre. */
  _groundHeight(pos) {
    let gy = game.court && Number.isFinite(game.court.floorY) ? game.court.floorY : 0;
    const cols = game.arena && game.arena.colliders;
    if (!cols || !cols.length) return gy;
    const reach = pos.y + this.tuning.stepHeight;
    for (let i = 0; i < cols.length; i++) {
      const b = cols[i];
      if (!b || !b.max || b.max.y <= gy || b.max.y > reach) continue;
      if (pos.x < b.min.x || pos.x > b.max.x || pos.z < b.min.z || pos.z > b.max.z) continue;
      gy = b.max.y;
    }
    return gy;
  }

  /** Planar push-out of the capsule from AABB colliders that overlap its vertical span. */
  _collideColliders(pos, vel) {
    const cols = game.arena && game.arena.colliders;
    if (!cols || !cols.length) return;
    const pl = this.player, t = this.tuning;
    const r = pl.radius || 0.32;
    const feet = pos.y, top = pos.y + (pl.height || 1.8);
    for (let i = 0; i < cols.length; i++) {
      const b = cols[i];
      if (!b || !b.max || b.max.y <= feet + t.stepHeight || b.min.y >= top) continue;
      const cx = clamp(pos.x, b.min.x, b.max.x), cz = clamp(pos.z, b.min.z, b.max.z);
      let dx = pos.x - cx, dz = pos.z - cz;
      const d2 = dx * dx + dz * dz;
      if (d2 >= r * r) continue;
      let nx, nz, push;
      if (d2 > 1e-10) {
        const d = Math.sqrt(d2);
        nx = dx / d; nz = dz / d; push = r - d;
      } else {
        // Centre inside the footprint: leave through the nearest face (or ignore a volume we are deep inside).
        const pL = pos.x - b.min.x, pR = b.max.x - pos.x, pB = pos.z - b.min.z, pF = b.max.z - pos.z;
        const m = Math.min(pL, pR, pB, pF);
        if (m > t.maxInsideDepth) continue;
        nx = 0; nz = 0;
        if (m === pL) nx = -1; else if (m === pR) nx = 1; else if (m === pB) nz = -1; else nz = 1;
        push = m + r;
      }
      pos.x += nx * push;
      pos.z += nz * push;
      const vn = vel.x * nx + vel.z * nz;
      if (vn < 0) { vel.x -= nx * vn; vel.z -= nz * vn; }
    }
  }

  /** Soft separation from other player capsules in the same zone (each body resolves its share). */
  _separatePlayers(pos, vel) {
    const me = this.player;
    const list = game.players;
    if (this.noCollide || !list || list.length < 2 || me.collidable === false) return;
    const rMe = me.radius || 0.32, hMe = me.height || 1.8;
    for (let i = 0; i < list.length; i++) {
      const o = list[i];
      if (!o || o === me || o.zone !== me.zone || !o.position || o.collidable === false) continue;
      if (o.motor && o.motor.noCollide) continue;
      const dy = o.position.y - pos.y;
      if (dy > hMe || -dy > (o.height || 1.8)) continue; // vertically apart (jumping over)
      let dx = pos.x - o.position.x, dz = pos.z - o.position.z;
      const minD = rMe + (o.radius || 0.32);
      const d2 = dx * dx + dz * dz;
      if (d2 >= minD * minD) continue;
      let d = Math.sqrt(d2);
      if (d < 1e-4) { // exactly stacked: separate along a stable per-id direction
        const a = ((me.id | 0) * 2.399963) % (Math.PI * 2);
        dx = Math.sin(a); dz = Math.cos(a); d = 1;
      }
      // An immovable other body (frozen ice block, grabbed) makes us resolve the whole overlap.
      const share = o.motor && o.motor.isFrozen ? 1 : this.tuning.teammatePushShare;
      const push = (minD - (d2 < 1e-8 ? 0 : Math.sqrt(d2))) * share;
      const nx = dx / d, nz = dz / d;
      pos.x += nx * push;
      pos.z += nz * push;
      const vn = vel.x * nx + vel.z * nz;
      if (vn < 0) { vel.x -= nx * vn; vel.z -= nz * vn; }
    }
  }

  // ------------------------------------------------------------------ helpers
  _tickTimers(dt) {
    if (this._jumpCooldown > 0) this._jumpCooldown = Math.max(0, this._jumpCooldown - dt);
    if (this._groundIgnore > 0) this._groundIgnore = Math.max(0, this._groundIgnore - dt);
    if (this._slideCooldown > 0 && !this.isSliding) this._slideCooldown = Math.max(0, this._slideCooldown - dt);
    if (this.control < 1) this.control = Math.min(1, this.control + dt / Math.max(0.01, this.tuning.knockbackRecoveryTime));
  }

  _refreshMultipliers() {
    const status = this.player && this.player.status;
    const statusMul = status && Number.isFinite(status.moveSpeedMul) ? status.moveSpeedMul : 1;
    this.speedMul = Math.max(0, this._speedModProduct * statusMul);
    this.traction = clamp01(this._tractionMin);
  }

  _refreshCaps() {
    const locked = this.isFrozen || this.mode === MOTOR_MODE.LOCKED;
    const dodgeBlocked = this._hasStatus('dodgeDisabled') || this._hasStatus('rooted');
    const free = !locked && !dodgeBlocked && this.isGrounded && !this.isSliding && !this._jumpRequested;
    this.canJump = free && this._jumpCooldown <= 0;
    this.canSlide = free && this._slideCooldown <= 0;
  }

  _hasStatus(type) {
    const s = this.player && this.player.status;
    return !!(s && s.has && s.has(type));
  }

  _recomputeSpeedMods() {
    let product = 1;
    for (const m of this._speedMods.values()) product *= m;
    this._speedModProduct = product;
    this._refreshMultipliers();
  }

  _recomputeTractionMods() {
    let min = 1;
    for (const t of this._tractionMods.values()) min = Math.min(min, t);
    this._tractionMin = min;
    this._refreshMultipliers();
  }

  _currentPlanarSpeed() {
    return this.isFrozen ? 0 : Math.hypot(this.velocity.x, this.velocity.z);
  }

  _clearReadouts() {
    this.planarSpeed = 0;
    this.yawRate = 0;
    this.planarAccel.set(0, 0, 0);
    this.maxSpeed = 0;
    this._prevPlanar.set(0, 0, 0);
  }

  _resetMotionState() {
    this._clearReadouts();
    this._jumpRequested = false;
    this._groundIgnore = 0;
    this.airTime = 0;
    this._airMaxSpeed = 0;
    this.control = 1;
    this.setMove(ZERO, 0);
  }
}
