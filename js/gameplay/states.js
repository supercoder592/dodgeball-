// ---------------------------------------------------------------------------------------------------------------
// PlayerStateMachine - the 8 behaviour states of a player over core/fsm.js (web port of the Unity design).
//
//   Grounded  <-> Sprinting        (sprint held & moving forward-ish / released or stick centred)
//   Grounded|Sprinting --jump-->  Airborne --land--> Grounded|Sprinting
//   Grounded|Sprinting --slide--> Sliding  --slide ends--> locomotion   (a slide started by an ability is followed)
//   any locomotion --throwPressed & hasBall & combat.beginCharge()--> ChargingThrow --release--> combat.releaseThrow()
//   any locomotion --catchPressed & !hasBall & combat.tryStartCatch()--> Catching (while combat.catchArmed)
//   stun(d)                      --> Stunned (charge/catch cancelled, abilities interrupted) --timer--> locomotion
//   incapacitate(reason[, d])    --> Incapacitated (frozen / eliminated / grabbed / teleporting / channeling /
//                                    rewinding / round) --release(reason) or timer--> locomotion
// States read the player's sampled intent (player.intent) every frame (scaled dt) and drive the Motor.
// Emits EV.PlayerState { player, previous, current } on every transition.
// ---------------------------------------------------------------------------------------------------------------
import * as THREE from 'three';
import { game } from '../game.js';
import { EV } from '../core/events.js';
import { StateMachine } from '../core/fsm.js';
import { INTERRUPT } from '../abilities/abilityBase.js';
import { MOTOR_MODE } from './motor.js';

export const STATE = Object.freeze({
  GROUNDED: 'grounded', AIRBORNE: 'airborne', SPRINTING: 'sprinting', SLIDING: 'sliding',
  CHARGING: 'chargingThrow', CATCHING: 'catching', STUNNED: 'stunned', INCAPACITATED: 'incapacitated',
});

/** Incapacitation reasons (fsm.incapacitate(reason)). */
export const INCAP = Object.freeze({
  ELIMINATED: 'eliminated', FROZEN: 'frozen', GRABBED: 'grabbed', TELEPORTING: 'teleporting',
  CHANNELING: 'channeling', REWINDING: 'rewinding', ROUND: 'round',
});

const DEG = Math.PI / 180;
const _aim = new THREE.Vector3();
const _zero = new THREE.Vector3();

/** Is the player holding a ball / charging / catch-armed, tolerant of partially implemented combat modules. */
const hasBall = (c) => !!(c && c.hasBall);
const isCharging = (c) => !!(c && c.isCharging);
const catchArmed = (c) => !!(c && c.catchArmed);

// =============================================================================================================
// State base
// =============================================================================================================
class PlayerState {
  /** @param {PlayerStateMachine} machine @param {string} id */
  constructor(machine, id) { this.machine = machine; this.id = id; }
  get player() { return this.machine.player; }
  get motor() { return this.machine.player.motor; }
  get combat() { return this.machine.player.combat; }
  get intent() { return this.machine.player.intent; }
  get tuning() { return this.machine.tuning; }
  get timeInState() { return this.machine.timeInState; }

  enter(prev) {}
  exit(next) {}
  update(dt) {}
  fixedUpdate(dt) {}
  canExitTo(next) { return true; }

  // ------------------------------------------------------------------ helpers
  driveMovement(intent) {
    const m = this.motor;
    if (!m) return;
    const mv = intent && intent.move;
    if (!mv) { m.setMove(_zero, 0); return; }
    m.setMove(mv, Math.min(1, Math.hypot(mv.x || 0, mv.z || 0)));
  }
  stopMovement() { if (this.motor) this.motor.setMove(_zero, 0); }
  /** Planar unit aim direction (falls back to the body forward). Returns a shared temporary. */
  planarAim(intent) {
    const a = intent && intent.aimDir;
    if (a) {
      const len = Math.hypot(a.x || 0, a.z || 0);
      if (len > 1e-4) return _aim.set(a.x / len, 0, a.z / len);
    }
    const yaw = this.player.yaw || 0;
    return _aim.set(Math.sin(yaw), 0, Math.cos(yaw));
  }
  faceAim(intent, instant = false) { if (this.motor) this.motor.setFacing(this.planarAim(intent), instant); }
  isMoving(intent, threshold = 0.1) {
    const mv = intent && intent.move;
    return !!mv && (mv.x * mv.x + mv.z * mv.z) >= threshold * threshold;
  }
  /** Sprint needs the button, enough stick and (to start) a direction roughly ahead of the body. */
  wantsSprint(intent, continuing) {
    if (!intent || !intent.sprint) return false;
    const st = this.player.status;
    if (st && st.has && st.has('rooted')) return false;
    const mv = intent.move;
    if (!mv) return false;
    const mag = Math.hypot(mv.x || 0, mv.z || 0);
    const t = this.tuning;
    if (continuing) return mag >= t.sprintStopInput;
    if (mag < t.sprintMinInput) return false;
    const yaw = this.player.yaw || 0;
    return (mv.x * Math.sin(yaw) + mv.z * Math.cos(yaw)) / mag >= t.sprintForwardDot;
  }
  locomotionStateFor(intent) {
    const m = this.motor;
    if (m && !m.isGrounded) return STATE.AIRBORNE;
    return this.wantsSprint(intent, true) ? STATE.SPRINTING : STATE.GROUNDED;
  }
  exitToLocomotion(intent) { this.machine.change(this.locomotionStateFor(intent)); }

  tryBeginThrowCharge(intent) {
    if (!intent || !intent.throwPressed || !this.player.canAct) return false;
    const c = this.combat;
    if (!hasBall(c) || typeof c.beginCharge !== 'function') return false;
    const ok = c.beginCharge();
    if (ok === false || (!ok && !isCharging(c))) return false;
    if (this.machine.change(STATE.CHARGING)) return true;
    if (isCharging(c) && c.cancelCharge) c.cancelCharge();
    return false;
  }
  tryBeginCatch(intent) {
    if (!intent || !intent.catchPressed || !this.player.canAct) return false;
    const c = this.combat;
    if (!c || hasBall(c) || c.catchingBlocked || typeof c.tryStartCatch !== 'function') return false;
    const ok = c.tryStartCatch();
    if (ok === false || (!ok && !catchArmed(c))) return false;
    if (this.machine.change(STATE.CATCHING)) return true;
    if (catchArmed(c) && c.cancelCatch) c.cancelCatch();
    return false;
  }
  tryJump(intent) {
    if (!intent || !intent.jump || !this.player.canAct) return false;
    const m = this.motor;
    if (!m || !m.tryJump()) return false;
    this.machine.change(STATE.AIRBORNE);
    return true;
  }
  trySlide(intent, requireSpeed) {
    if (!intent || !intent.slide || !this.player.canAct) return false;
    const m = this.motor;
    if (!m || !m.canSlide) return false;
    if (requireSpeed && m.planarSpeed < (m.profile.walkSpeed || 4.6) * this.tuning.slideMinSpeedFraction) return false;
    const dir = this.isMoving(intent) ? intent.move : (m.planarSpeed > 0.2 ? m.velocity : this.planarAim(null));
    if (!m.trySlide(dir)) return false;
    this.machine.change(STATE.SLIDING);
    return true;
  }
  /** Shared ground-locomotion tick (Grounded & Sprinting). Returns true when a transition happened. */
  groundCommon(dt, intent) {
    const m = this.motor;
    // An ability started a slide directly on the motor (e.g. an evasive dodge): follow it.
    if (m.isSliding) { this.machine.change(STATE.SLIDING); return true; }
    // Walked off an edge / launched: short coyote time before becoming airborne.
    if (!m.isGrounded) {
      this._ungrounded = (this._ungrounded || 0) + dt;
      if (this._ungrounded >= this.tuning.coyoteTime) { this.machine.change(STATE.AIRBORNE); return true; }
    } else {
      this._ungrounded = 0;
    }
    if (this.tryBeginThrowCharge(intent)) return true;
    if (this.tryBeginCatch(intent)) return true;
    if (this.tryJump(intent)) return true;
    return false;
  }
}

// =============================================================================================================
// Locomotion
// =============================================================================================================
class GroundedState extends PlayerState {
  constructor(m) { super(m, STATE.GROUNDED); }
  enter() { this._ungrounded = 0; const m = this.motor; if (m && !m.isSliding) m.setMode(MOTOR_MODE.WALK); }
  update(dt) {
    const m = this.motor, intent = this.intent;
    if (!m) return;
    if (!m.isSliding) m.setMode(MOTOR_MODE.WALK);
    this.driveMovement(intent);
    // Standing still: keep the chest (and the catch cone) turned toward the action.
    if (!this.isMoving(intent) && this.tuning.faceAimWhileIdle && this.player.canAct) this.faceAim(intent);
    if (this.groundCommon(dt, intent)) return;
    if (this.trySlide(intent, true)) return;
    if (this.wantsSprint(intent, false)) this.machine.change(STATE.SPRINTING);
  }
}

class SprintingState extends PlayerState {
  constructor(m) { super(m, STATE.SPRINTING); }
  enter() { this._ungrounded = 0; const m = this.motor; if (m && !m.isSliding) m.setMode(MOTOR_MODE.SPRINT); }
  update(dt) {
    const m = this.motor, intent = this.intent;
    if (!m) return;
    if (!m.isSliding) m.setMode(MOTOR_MODE.SPRINT);
    this.driveMovement(intent);
    if (this.groundCommon(dt, intent)) return;
    if (this.trySlide(intent, false)) return;
    if (!this.wantsSprint(intent, true)) this.machine.change(STATE.GROUNDED);
  }
  exit(next) {
    // Leaving the sprint regime: the motor brakes back to walk speed along its deceleration curve.
    const m = this.motor;
    if (m && m.mode === MOTOR_MODE.SPRINT && next !== STATE.SLIDING) m.setMode(MOTOR_MODE.WALK);
  }
}

class AirborneState extends PlayerState {
  constructor(m) { super(m, STATE.AIRBORNE); }
  enter() { if (this.motor) this.motor.setMode(MOTOR_MODE.AIR); }
  update() {
    const m = this.motor, intent = this.intent;
    if (!m) return;
    m.setMode(MOTOR_MODE.AIR);
    this.driveMovement(intent);
    if (m.isGrounded && this.timeInState >= this.tuning.minAirTime) { this.exitToLocomotion(intent); return; }
    if (this.tryBeginThrowCharge(intent)) return; // jump throw
    this.tryBeginCatch(intent);                   // air catch
  }
}

class SlidingState extends PlayerState {
  constructor(m) { super(m, STATE.SLIDING); }
  enter() {
    const m = this.motor;
    if (!m || m.isSliding) return;
    // Entered without a running slide (forced by someone else): start one now, or bail out.
    const intent = this.intent;
    const dir = this.isMoving(intent) ? intent.move : this.planarAim(null);
    if (!m.trySlide(dir)) this.machine.change(this.machine.locomotionFallback());
  }
  update() {
    const m = this.motor, intent = this.intent;
    if (!m) return;
    if (!m.isSliding) { this.exitToLocomotion(intent); return; }
    m.setMode(MOTOR_MODE.SLIDE);
    this.driveMovement(intent); // slight steering only
  }
  exit() {
    // Forced out mid-slide (stun, freeze...): stand back up; the remaining momentum is braked normally.
    const m = this.motor;
    if (m && m.isSliding) m.endSlide();
  }
}

// =============================================================================================================
// Combat stances
// =============================================================================================================
class ChargingThrowState extends PlayerState {
  constructor(m) { super(m, STATE.CHARGING); }
  enter() {
    if (this.motor) this.motor.setMode(this.motor.isGrounded ? MOTOR_MODE.CHARGING : MOTOR_MODE.AIR);
    // Entered directly (not through tryBeginThrowCharge): start the charge now or leave.
    const c = this.combat;
    if (!c) return;
    if (!isCharging(c)) {
      const ok = hasBall(c) && typeof c.beginCharge === 'function' ? c.beginCharge() : false;
      if (ok === false || (!ok && !isCharging(c))) this.machine.change(this.machine.locomotionFallback());
    }
  }
  update() {
    const m = this.motor, c = this.combat, intent = this.intent, pl = this.player;
    if (!m || !c) return;
    // In the air the body keeps its jump momentum (air rules); on the ground the wind-up slows the feet.
    m.setMode(m.isGrounded ? MOTOR_MODE.CHARGING : MOTOR_MODE.AIR);
    this.driveMovement(intent);
    this.faceAim(intent);
    // Ball gone (pass, Grand Vanish, stolen) -> abort.
    if (!hasBall(c)) {
      if (isCharging(c) && c.cancelCharge) c.cancelCharge();
      this.exitToLocomotion(intent);
      return;
    }
    // Combat ended the charge itself (auto-release at the hold limit, an ability consumed it...).
    if (!isCharging(c)) { this.exitToLocomotion(intent); return; }
    // Round over / countdown: never auto-throw because the neutral intent reads as "not held".
    if (pl.inputLocked) {
      if (c.cancelCharge) c.cancelCharge();
      this.exitToLocomotion(intent);
      return;
    }
    // Jump throw: leave the ground without losing the charge.
    if (intent.jump && m.isGrounded && pl.canAct) m.tryJump();
    if (intent.throwReleased || !intent.throwHeld) {
      this.release(intent);
      this.exitToLocomotion(intent);
    }
  }
  exit() {
    // Forced out while still winding up (stun, freeze, elimination): the throw is cancelled, the ball kept.
    const c = this.combat;
    if (isCharging(c) && c.cancelCharge) c.cancelCharge();
  }
  release(intent) {
    const c = this.combat, m = this.motor;
    // Square the shoulders to the aim so the release (and the throw IK) never goes over the back.
    if (m) {
      const aim = this.planarAim(intent);
      const yaw = this.player.yaw || 0;
      const dot = aim.x * Math.sin(yaw) + aim.z * Math.cos(yaw);
      if (dot < Math.cos(this.tuning.throwSnapFacingAngle)) m.setFacing(aim, true);
    }
    if (c.releaseThrow) c.releaseThrow();
  }
}

class CatchingState extends PlayerState {
  constructor(m) { super(m, STATE.CATCHING); }
  enter() {
    if (this.motor) this.motor.setMode(MOTOR_MODE.CATCHING);
    // Entered directly (not through tryBeginCatch): arm the stance now or leave.
    const c = this.combat;
    if (!c) return;
    if (!catchArmed(c)) {
      const ok = !hasBall(c) && !c.catchingBlocked && typeof c.tryStartCatch === 'function' ? c.tryStartCatch() : false;
      if (ok === false || (!ok && !catchArmed(c))) this.machine.change(this.machine.locomotionFallback());
    }
  }
  update() {
    const m = this.motor, c = this.combat, intent = this.intent;
    if (!m || !c) return;
    m.setMode(m.isGrounded ? MOTOR_MODE.CATCHING : MOTOR_MODE.AIR);
    this.driveMovement(intent);
    this.faceAim(intent);
    // Caught, whiffed, or blocked (frozen) -> back to moving. A throw pressed on the very frame of the catch starts
    // the counter-throw wind-up straight away (the perfect-catch +20% boost waits in Combat).
    if (!catchArmed(c) || c.catchingBlocked) {
      if (hasBall(c) && this.tryBeginThrowCharge(intent)) return;
      this.exitToLocomotion(intent);
    }
  }
  exit() {
    // Forced out (stun, freeze...): disarm without the whiff penalty.
    const c = this.combat;
    if (catchArmed(c) && c.cancelCatch) c.cancelCatch();
  }
}

// =============================================================================================================
// Control loss
// =============================================================================================================
class StunnedState extends PlayerState {
  constructor(m) { super(m, STATE.STUNNED); }
  enter() {
    const m = this.motor;
    if (!m) return;
    if (m.isSliding) m.endSlide();
    m.setMode(MOTOR_MODE.LOCKED);
    this.stopMovement();
  }
  update() {
    const m = this.motor;
    if (m) { m.setMode(MOTOR_MODE.LOCKED); this.stopMovement(); }
    if (this.machine.stunRemaining <= 0) this.machine.change(this.machine.locomotionFallback(), true);
  }
  canExitTo(next) { return next === STATE.INCAPACITATED || this.machine.stunRemaining <= 0; }
  exit() {
    this.machine.stunRemaining = 0;
    this.machine._clearStunStatus();
  }
}

class IncapacitatedState extends PlayerState {
  constructor(m) {
    super(m, STATE.INCAPACITATED);
    this.appliedReason = null;
    this._frozeMotor = false;
    this._pausedHistory = false;
    this._pendingGroundFreeze = false;
    this._pendingFreezeTime = 0;
  }
  enter() {
    const m = this.motor;
    if (m && m.isSliding) m.endSlide();
    this.applyReason(this.machine.incapReason);
  }
  /** (Re)applies the motor / history rules of an incapacitation reason. */
  applyReason(reason) {
    this.appliedReason = reason;
    const m = this.motor;
    if (m) { m.setMode(MOTOR_MODE.LOCKED); this.stopMovement(); }
    // Motor freeze: the body is owned by someone else (ragdoll, grabber, rewind playback, ice block).
    const freezeNow = reason === INCAP.ELIMINATED || reason === INCAP.GRABBED || reason === INCAP.REWINDING;
    const freezeOnGround = reason === INCAP.FROZEN;
    this._pendingGroundFreeze = false;
    this._pendingFreezeTime = 0;
    if (freezeNow || (freezeOnGround && (!m || m.isGrounded))) this._setMotorFrozen(true);
    else if (freezeOnGround) { this._setMotorFrozen(false); this._pendingGroundFreeze = true; } // fall as an ice block, lock on landing
    else this._setMotorFrozen(false);
    // The rewind history must not record the playback itself.
    this._setHistoryPaused(reason === INCAP.REWINDING);
  }
  update(dt) {
    const m = this.motor;
    if (!m) return;
    if (!m.isFrozen) { m.setMode(MOTOR_MODE.LOCKED); this.stopMovement(); }
    if (this._pendingGroundFreeze) {
      this._pendingFreezeTime += dt;
      if (m.isGrounded || this._pendingFreezeTime >= this.tuning.frozenAirFallTimeout) {
        this._pendingGroundFreeze = false;
        this._setMotorFrozen(true);
      }
    }
  }
  canExitTo() { return false; } // only forced transitions (release / reset) leave this state
  exit() {
    this._pendingGroundFreeze = false;
    this._setMotorFrozen(false);
    this._setHistoryPaused(false);
    this.appliedReason = null;
    const m = this.motor;
    if (m && m.mode === MOTOR_MODE.LOCKED) m.setMode(MOTOR_MODE.WALK);
  }
  _setMotorFrozen(frozen) {
    const m = this.motor;
    if (!m) return;
    if (frozen) { m.setFrozen(true); this._frozeMotor = true; }
    else if (this._frozeMotor) { m.setFrozen(false); this._frozeMotor = false; }
  }
  _setHistoryPaused(paused) {
    const h = this.player.history;
    if (!h) return;
    if (paused) { if (!h.paused) { h.paused = true; this._pausedHistory = true; } }
    else if (this._pausedHistory) { h.paused = false; this._pausedHistory = false; }
  }
}

// =============================================================================================================
// Machine
// =============================================================================================================
export class PlayerStateMachine {
  static defaults = {
    coyoteTime: 0.1,               // s without ground before Grounded/Sprinting fall into Airborne
    minAirTime: 0.05,              // s in the air before a landing is accepted
    sprintMinInput: 0.5,           // stick deflection to start sprinting
    sprintStopInput: 0.25,         // stick deflection below which a sprint stops
    sprintForwardDot: 0.3,         // dot(move, body forward) needed to start a sprint
    slideMinSpeedFraction: 0.6,    // planar speed (x walkSpeed) needed to slide from Grounded
    faceAimWhileIdle: true,        // idle bodies turn toward the aim (keeps the catch cone on the action)
    throwSnapFacingAngle: 100 * DEG, // snap to the aim on release beyond this angle (no over-the-back throws)
    dropBallForwardSpeed: 1.2,     // m/s of a ball dropped on elimination / grab
    dropBallUpSpeed: 1.8,
    frozenAirFallTimeout: 1.5,     // a player frozen mid-air falls as an ice block, locked on landing or after this
  };

  /** @param {import('./player.js').Player} player */
  constructor(player) {
    this.player = player;
    this.tuning = { ...PlayerStateMachine.defaults };
    this.fsm = new StateMachine();
    this.states = {};
    for (const S of [GroundedState, AirborneState, SprintingState, SlidingState, ChargingThrowState, CatchingState, StunnedState, IncapacitatedState]) {
      const s = new S(this);
      this.states[s.id] = s;
      this.fsm.register(s);
    }
    /** Current incapacitation reason (INCAP value) or null. */
    this.incapReason = null;
    this.stunRemaining = 0;
    this.previous = STATE.GROUNDED;
    this._incapTimed = false;
    this._incapRemaining = 0;
    this._mirroringStun = false;
    this.fsm.onChange = (prev, next) => this._onChange(prev, next);
    this.fsm.start(STATE.GROUNDED);
  }

  /** Current state id (STATE value). */
  get current() { return this.fsm.currentId; }
  get timeInState() { return this.fsm.timeInState; }
  /** Not stunned or incapacitated (Player.canAct also checks inputLocked). */
  get canAct() { const c = this.fsm.currentId; return c !== STATE.STUNNED && c !== STATE.INCAPACITATED; }
  get incapRemaining() { return this._incapTimed ? Math.max(0, this._incapRemaining) : 0; }
  /** True while mirroring the stun into Status (Status uses it to avoid re-entrant stuns). */
  get isMirroringStun() { return this._mirroringStun; }
  is(id) { return this.fsm.is(id); }
  /** Requests a transition (respects canExitTo unless forced). */
  change(id, force = false) { return this.fsm.change(id, force); }

  /** Stuns for `d` seconds (extends, never shortens): cancels charge/catch and interrupts interruptible abilities. */
  stun(d) {
    if (!(d > 0) || !Number.isFinite(d)) return false;
    const p = this.player;
    if (p.health && p.health.isEliminated) return false;
    if (this.is(STATE.INCAPACITATED)) {
      // A self-imposed channel is broken by a stun (the ability's onInterrupt releases its lock); harder locks win.
      if (this.incapReason === INCAP.CHANNELING && p.abilities) p.abilities.interruptAll(INTERRUPT.STUNNED);
      if (this.is(STATE.INCAPACITATED)) return false;
    }
    this.stunRemaining = Math.max(this.stunRemaining, d);
    const c = p.combat;
    if (isCharging(c) && c.cancelCharge) c.cancelCharge();
    if (catchArmed(c) && c.cancelCatch) c.cancelCatch();
    if (p.abilities) p.abilities.interruptAll(INTERRUPT.STUNNED);
    this._mirrorStunStatus();
    if (!this.is(STATE.STUNNED)) this.fsm.change(STATE.STUNNED, true);
    return true;
  }

  /**
   * Locks the player. `d` > 0 releases automatically after d seconds (scaled time).
   * Elimination supersedes every other reason and cannot be replaced by a softer one.
   * @param {string} reason INCAP value
   * @param {number} [d]
   */
  incapacitate(reason, d = 0) {
    if (!reason) return false;
    if (this.is(STATE.INCAPACITATED) && this.incapReason === INCAP.ELIMINATED && reason !== INCAP.ELIMINATED) return false;
    const sameReason = this.is(STATE.INCAPACITATED) && this.incapReason === reason;
    this.incapReason = reason;
    this._incapTimed = d > 0;
    this._incapRemaining = d > 0 ? d : 0;
    // A harder lock supersedes any stun.
    if (this.stunRemaining > 0) { this.stunRemaining = 0; this._clearStunStatus(); }
    if (!sameReason) this._applyIncapSideEffects(reason);
    if (this.is(STATE.INCAPACITATED)) this.states[STATE.INCAPACITATED].applyReason(reason);
    else this.fsm.change(STATE.INCAPACITATED, true);
    return true;
  }

  /** Ends the incapacitation if it is for `reason` (any reason when omitted). */
  release(reason) {
    if (!this.is(STATE.INCAPACITATED)) return false;
    if (reason && this.incapReason !== reason) return false;
    this.incapReason = null;
    this._incapTimed = false;
    this._incapRemaining = 0;
    this.fsm.change(this.locomotionFallback(), true);
    return true;
  }

  /** Hard reset (new round, revive): free, grounded, walking. */
  resetToGrounded() {
    this.stunRemaining = 0;
    this._clearStunStatus();
    this.incapReason = null;
    this._incapTimed = false;
    this._incapRemaining = 0;
    if (!this.is(STATE.GROUNDED)) this.fsm.change(STATE.GROUNDED, true);
    const m = this.player.motor;
    if (m) {
      m.setFrozen(false);
      if (m.isSliding) m.endSlide();
      m.setMode(MOTOR_MODE.WALK);
    }
  }

  /** Scaled dt, once per frame (after intent sampling and status). */
  update(dt) {
    if (this.stunRemaining > 0) this.stunRemaining = Math.max(0, this.stunRemaining - dt);
    if (this._incapTimed && this.is(STATE.INCAPACITATED)) {
      this._incapRemaining -= dt;
      if (this._incapRemaining <= 0) this.release(this.incapReason);
    }
    this.fsm.update(dt);
  }

  fixedUpdate(dt) { this.fsm.fixedUpdate(dt); }

  // ------------------------------------------------------------------ helpers
  /** Grounded or Airborne depending on ground contact. */
  locomotionFallback() {
    const m = this.player.motor;
    return !m || m.isGrounded ? STATE.GROUNDED : STATE.AIRBORNE;
  }

  /** Velocity for a ball dropped on elimination / grab (forward pop + half the body's velocity). */
  dropBallVelocity(out = new THREE.Vector3()) {
    const p = this.player, t = this.tuning;
    const yaw = p.yaw || 0;
    out.set(Math.sin(yaw) * t.dropBallForwardSpeed, t.dropBallUpSpeed, Math.cos(yaw) * t.dropBallForwardSpeed);
    if (p.velocity) out.addScaledVector(p.velocity, 0.5);
    return out;
  }

  _applyIncapSideEffects(reason) {
    const p = this.player, c = p.combat, abilities = p.abilities;
    // Nothing can be thrown or caught while incapacitated.
    if (isCharging(c) && c.cancelCharge) c.cancelCharge();
    if (catchArmed(c) && c.cancelCatch) c.cancelCatch();
    switch (reason) {
      case INCAP.FROZEN:
        if (abilities) abilities.interruptAll(INTERRUPT.FROZEN);
        break;
      case INCAP.ELIMINATED:
        if (abilities) abilities.interruptAll(INTERRUPT.ELIMINATED);
        if (hasBall(c) && c.dropBall) c.dropBall(this.dropBallVelocity());
        break;
      case INCAP.GRABBED:
        // A tackled player loses the ball and any interruptible channel.
        if (abilities) abilities.interruptAll(INTERRUPT.STUNNED);
        if (hasBall(c) && c.dropBall) c.dropBall(this.dropBallVelocity());
        break;
      case INCAP.ROUND:
        if (abilities) abilities.interruptAll(INTERRUPT.ROUND_ENDED);
        break;
      default:
        // teleporting / channeling / rewinding are requested by the player's own (or an ally's) ability:
        // interrupting abilities here would cancel the very ability that asked for the lock.
        break;
    }
  }

  _mirrorStunStatus() {
    const s = this.player.status;
    if (!s || !s.apply) return;
    this._mirroringStun = true;
    try { s.apply('stunned', this.stunRemaining, 1, this); } finally { this._mirroringStun = false; }
  }

  _clearStunStatus() {
    const s = this.player.status;
    if (!s || !s.remove) return;
    this._mirroringStun = true;
    try { s.remove('stunned', this); } finally { this._mirroringStun = false; }
  }

  _onChange(prev, next) {
    this.previous = prev;
    if (prev === STATE.INCAPACITATED && next !== STATE.INCAPACITATED) {
      this.incapReason = null;
      this._incapTimed = false;
      this._incapRemaining = 0;
    }
    game.events.emit(EV.PlayerState, { player: this.player, previous: prev, current: next });
  }
}
