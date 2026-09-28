// ---------------------------------------------------------------------------------------------------------------
// Gouki (格鬥家 Brawler) - abilities.
//   PASSIVE  Thick Hide        (厚皮)     200 HP (roster maxHp) and half knockback from every hit (health hit filter).
//   SKILL    Tackle Intercept  (擒抱攔截) 0.55 s charge at 11 m/s up to 2 m past the centre line: swats live balls in
//                                         his path, grabs enemies he runs into and hurls them over his shoulder into
//                                         the outfield (health.eliminate('tackle') - interceptors still apply), then
//                                         backs into his half. Unstoppable while charging. CD 11 s.
//   ULTIMATE Earthquake Slam   (震地猛擊) A short hop and a ground slam: every grounded infield enemy is thrown into
//                                         the air (~5 m/s) and fumbles the ball he holds.
// ---------------------------------------------------------------------------------------------------------------
import * as THREE from 'three';
import { game } from '../../game.js';
import { TEAM, ZONE } from '../../core/constants.js';
import { EV } from '../../core/events.js';
import { Court } from '../../world/court.js';
import { AbilityBase, registerAbility, FAIL, INTERRUPT } from '../abilityBase.js';
import { heroMovement } from '../roster.js';
import { depthIntoOwnHalf, extendPastCentreLine, inFrontWithin, inChargeVolume } from '../shared/screwsGadgetMath.js';
import {
  BALL, FieldStamp, allBalls, matchBalls, throwerTeam, setBallVelocity, registerBallField, unregisterBallField,
  planarForward, planarRight, chestOf, fx, sfx, trauma, shake, hitstop, pulse, emit, isAlive, isInfield, isGrounded,
  hasStatus, scaleKnockback,
} from '../shared/screwsGadgetKit.js';

const DEG = Math.PI / 180;
const _v = new THREE.Vector3();
const _v2 = new THREE.Vector3();
const _r = new THREE.Vector3();
const _rel = new THREE.Vector3();
const _pt = new THREE.Vector3();
const _c2 = new THREE.Vector3();
const _back = new THREE.Vector3();
const _home = new THREE.Vector3();
const _imp = new THREE.Vector3();

/** Per-team INFIELD confinement cache (Court.confinement allocates): carried victims never leave their own half. */
const _infieldConf = { court: null, bounds: [null, null] };
function clampIntoInfield(team, p) {
  const court = game.court;
  if (!court || !(team === TEAM.HOME || team === TEAM.AWAY)) return p;
  if (_infieldConf.court !== court) { _infieldConf.court = court; _infieldConf.bounds[0] = _infieldConf.bounds[1] = null; }
  const i = team === TEAM.HOME ? 0 : 1;
  const b = _infieldConf.bounds[i] || (_infieldConf.bounds[i] = court.confinement(team, ZONE.INFIELD));
  return Court.clamp(b, p);
}

// ================================================================== PASSIVE: Thick Hide

/** Halves the knockback of every hit Gouki takes (200 HP comes from the roster's maxHp). */
export class GoukiThickHide extends AbilityBase {
  static defaults = { knockbackMul: 0.5, priority: 40 };

  onInitialize() {
    this._health = null;
    this._filter = { priority: this.params.priority, filter: (hit) => this._filterHit(hit) };
  }

  onEquip() { this._attach(); }

  /** Passive tick: (re)attach when the health component appears or is rebuilt. */
  onTick() { if ((this.owner.health || null) !== this._health) this._attach(); }

  onUnequip() { this._detach(); }

  _attach() {
    this._detach();
    const h = this.owner.health;
    if (h && h.addHitFilter) { h.addHitFilter(this._filter); this._health = h; }
  }

  _detach() {
    if (this._health) this._health.removeHitFilter?.(this._filter);
    this._health = null;
  }

  _filterHit(hit) {
    if (!hit || hit.cancelled || (hit.victim && hit.victim !== this.owner)) return;
    scaleKnockback(hit, this.params.knockbackMul);
  }

  onCast() { /* passive: never cast */ }
}

// ================================================================== SKILL: Tackle Intercept

const STAGE = Object.freeze({ NONE: 0, CHARGING: 1, CARRYING: 2, RETURNING: 3 });

export class GoukiTackleIntercept extends AbilityBase {
  static defaults = {
    speed: 11,               // m/s charge (spec)
    chargeDuration: 0.55,    // s, used when the roster has no duration (spec 0.55)
    crossLine: 2,            // m past the centre line he may charge (spec)
    deflectRadius: 1.3,      // m ahead of his chest where live balls are swatted (spec)
    deflectHalfWidth: 0.85,  // m half-width of the swat volume
    deflectSpeed: 9,         // m/s of a swatted ball (becomes free)
    deflectLift: 0.3,        // upward share of the swat direction
    grabRadius: 1,           // m (spec)
    maxGrabHeight: 1.1,      // m: an enemy jumping clear escapes
    maxGrabs: 2,
    carryTime: 0.2,          // s the victim is held before the throw
    carryDistance: 0.8,      // m in front of Gouki
    carryLift: 0.25,         // m off the floor
    throwImpulseH: 110,      // N*s horizontal ragdoll impulse (over the shoulder, toward the victim's outfield)
    throwImpulseV: 95,       // N*s vertical
    survivorKnockback: 4,    // m/s when the elimination was prevented/delayed (Time Reversal, Delayed Impact)
    survivorStun: 0.8,       // s
    fumbleSpeed: 2,          // m/s of a ball a grabbed enemy drops
    postChargeSpeed: 2,      // m/s right after the charge
    returnSpeed: 5.5,        // m/s pushing him back into his half
    maxReturnTime: 0.8,      // s, then he is placed inside directly
    insideMargin: 0.35,      // m inside his half before the normal confinement is restored (= court inset)
    humanSnapDeg: 25,        // aim cone that snaps the charge onto an enemy (humans)
    botSnapDeg: 55,          // (bots)
    blockedSpeedFrac: 0.3,   // charge ends when his measured speed drops below this share (ran into a wall)
    dustInterval: 0.08,      // s
    grabHitstop: 0.05,       // s (juice clamps to 0.03-0.1)
    grabShake: 0.4,          // trauma units
    filterPriority: 250,
  };

  onInitialize() {
    this._stage = STAGE.NONE;
    this._dir = new THREE.Vector3(0, 0, 1);
    this._chest = new THREE.Vector3();
    this._lastPos = new THREE.Vector3();
    this._victims = [];
    this._deflects = new Map();   // ball -> contact point
    this._stamp = new FieldStamp();
    this._field = { apply: (ball, dt) => this._fieldApply(ball, dt) };
    this._fieldOn = false;
    this._filter = { priority: this.params.filterPriority, filter: (hit) => this._filterHit(hit) };
    this._filterOn = null;
    this._speedMod = false;
    this._extended = false;
    this._t = 0; this._blkT = 0; this._dust = 0; this._returnT = 0;
  }

  get chargeTime() { return this.duration > 0 ? this.duration : this.params.chargeDuration; }
  /** True while unstoppable (charging or carrying a victim). */
  get isCharging() { return this._stage === STAGE.CHARGING || this._stage === STAGE.CARRYING; }

  canActivateCustom() {
    if (!isGrounded(this.owner)) return FAIL.CUSTOM; // needs footing to charge
    return null;
  }

  onCast() {
    const o = this.owner;
    this._stage = STAGE.CHARGING;
    this._t = 0; this._blkT = 0; this._dust = 0; this._returnT = 0;
    this._victims.length = 0;
    this._deflects.clear();
    this._chooseDirection(this._dir);

    const c = o.combat;
    if (c) {
      if (c.isCharging) c.cancelCharge?.();
      if (c.catchArmed) c.cancelCatch?.();
    }
    this._extendConfinement();
    const m = o.motor;
    if (m) {
      if (m.isSliding) m.endSlide?.();
      const sprint = heroMovement(o.hero || {}).sprintSpeed || 7;
      m.setSpeedModifier?.(this, this.params.speed / Math.max(1, sprint));
      this._speedMod = true;
    }
    this._drive(this.params.speed);
    const h = o.health;
    if (h && h.addHitFilter) { h.addHitFilter(this._filter); this._filterOn = h; }
    this._fieldOn = registerBallField(this._field);
    this._lastPos.copy(o.position);
    chestOf(o, this._chest);
    _back.copy(this._dir).negate();
    fx('tackleDust', o.position, { direction: _back, scale: 1.1 });
    sfx('tackleCharge', this._chest, 1, 0.8);
    this.holdActive(); // the stages end the ability
  }

  onTick(dt) {
    const o = this.owner;
    switch (this._stage) {
      case STAGE.CHARGING: {
        this._t += dt;
        this._shrugOffStun();
        this._drive(this.params.speed);
        chestOf(o, this._chest);
        this._deflectSweep(dt);
        this._flushDeflects();
        this._checkGrabs();
        this._dust -= dt;
        if (this._dust <= 0) {
          this._dust = this.params.dustInterval;
          fx('tackleDust', o.position, { direction: _back.copy(this._dir).negate(), scale: 0.55 });
        }
        if (this._victims.length) { this._stage = STAGE.CARRYING; this._t = 0; break; }
        if (this._t >= this.chargeTime || this._isBlocked(dt) || hasStatus(o, 'frozen')) {
          this._stopCharge();
          this._stage = STAGE.RETURNING;
          this._returnT = 0;
        }
        break;
      }
      case STAGE.CARRYING:
        this._t += dt;
        this._shrugOffStun();
        chestOf(o, this._chest);
        this._carry();
        this._flushDeflects();
        if (this._t >= this.params.carryTime) {
          this._throwVictims();
          this._stopCharge();
          this._stage = STAGE.RETURNING;
          this._returnT = 0;
        }
        break;
      case STAGE.RETURNING:
        if (this._tickReturn(dt)) {
          this._finishReturn();
          this._stage = STAGE.NONE;
          this.endAbility();
        }
        break;
      default:
        this.endAbility();
    }
  }

  onEnd() {
    this._releaseVictims();
    this._stopCharge();
    this._finishReturn();
    this._stage = STAGE.NONE;
  }

  onRoundReset() { this.onEnd(); }

  onUnequip() { this.onEnd(); }

  // ------------------------------------------------------------------ charge

  _chooseDirection(out) {
    const o = this.owner;
    const aim = o.intent && o.intent.aimDir;
    if (aim && aim.x * aim.x + aim.z * aim.z > 1e-4) out.set(aim.x, 0, aim.z).normalize();
    else planarForward(o, out);
    const cone = (o.isHuman ? this.params.humanSnapDeg : this.params.botSnapDeg) * DEG;
    const reach = this.params.speed * this.chargeTime + this.params.grabRadius;
    let best = null, bestScore = Infinity, bx = 0, bz = 0, nearest = null, nearestD = Infinity, nx = 0, nz = 0;
    const players = game.players;
    for (let i = 0; i < players.length; i++) {
      const p = players[i];
      if (!p || !game.areEnemies(o, p) || !p.isTargetable || !isAlive(p)) continue;
      const dx = p.position.x - o.position.x, dz = p.position.z - o.position.z;
      const dist = Math.hypot(dx, dz);
      if (dist < 0.05 || dist > reach) continue;
      if (dist < nearestD) { nearestD = dist; nearest = p; nx = dx / dist; nz = dz / dist; }
      const ang = Math.acos(Math.max(-1, Math.min(1, (dx * out.x + dz * out.z) / dist)));
      if (ang > cone) continue;
      const score = ang / cone + dist / reach;
      if (score < bestScore) { bestScore = score; best = p; bx = dx / dist; bz = dz / dist; }
    }
    if (best) out.set(bx, 0, bz);
    else if (!o.isHuman && nearest) out.set(nx, 0, nz); // a bot decided to tackle: go for the reachable enemy
    return out;
  }

  /** Abilities tick after the state machine, so these motor commands win this frame. */
  _drive(speed) {
    const m = this.owner.motor;
    if (!m) return;
    m.setMode?.('sprint');
    m.setMove?.(this._dir, 1);
    m.setFacing?.(this._dir, true);
    _v.copy(this._dir).multiplyScalar(speed);
    m.setPlanarVelocity?.(_v);
  }

  /** Measured over >= 0.1 s windows (frames without a fixed step would otherwise read as "stopped"). */
  _isBlocked(dt) {
    this._blkT += dt;
    if (this._blkT < 0.1) return false;
    const p = this.owner.position;
    const moved = Math.hypot(p.x - this._lastPos.x, p.z - this._lastPos.z);
    const rate = moved / this._blkT;
    this._lastPos.copy(p);
    this._blkT = 0;
    return this._t > 0.15 && rate < this.params.speed * this.params.blockedSpeedFrac;
  }

  _shrugOffStun() {
    const o = this.owner;
    if (hasStatus(o, 'stunned')) o.status.remove?.('stunned');
    if (o.fsm && o.fsm.is && o.fsm.is('stunned')) o.fsm.resetToGrounded?.();
  }

  // ------------------------------------------------------------------ deflection

  _fieldApply(ball, dt) {
    if (!this.isCharging || !ball || ball.state !== BALL.LIVE) return;
    this._stamp.touch(ball);
    this._tryDeflect(ball, dt);
  }

  /** Per-frame fallback for live balls the BallManager did not route through the field. */
  _deflectSweep(dt) {
    const balls = allBalls();
    for (let i = balls.length - 1; i >= 0; i--) {
      const b = balls[i];
      if (b && b.state === BALL.LIVE && !this._stamp.fresh(b)) this._tryDeflect(b, Math.max(dt, 1e-4));
    }
  }

  _tryDeflect(ball, dt) {
    if (ball.unblockable || this._deflects.has(ball)) return;
    const team = throwerTeam(ball);
    if (team === TEAM.NONE || team === this.owner.team) return;
    _rel.subVectors(ball.position, this._chest);
    // Predictive reach: the gap closes at (ball speed toward him + his charge speed) during this step.
    const closing = Math.max(0, -ball.velocity.dot(this._dir)) + this.params.speed;
    const reach = this.params.deflectRadius + closing * dt;
    if (Number.isNaN(inChargeVolume(_rel, this._dir, reach, this.params.deflectHalfWidth))) return;
    this._deflectVelocity(_rel, _v);
    ball.velocity.copy(_v); // stays live for the rest of this step, flying away from him
    this._deflects.set(ball, ball.position.clone());
  }

  /** Swat direction: back along the charge (toward the enemy), off to the side the ball was on, with lift. */
  _deflectVelocity(rel, out) {
    planarRight(this._dir, _r);
    const side = rel.dot(_r) >= 0 ? 1 : -1;
    out.copy(this._dir).addScaledVector(_r, side * 0.55);
    out.y = this.params.deflectLift;
    return out.normalize().multiplyScalar(this.params.deflectSpeed);
  }

  /** Ends the flight of every swatted ball (outside the ball's own fixed step) and publishes BallBlocked. */
  _flushDeflects() {
    if (!this._deflects.size) return;
    const o = this.owner;
    for (const [ball, point] of this._deflects) {
      _rel.subVectors(ball.position, this._chest);
      this._deflectVelocity(_rel, _v2);
      if (ball.state === BALL.LIVE) ball.makeFree?.(_v2, true);
      else if (ball.state === BALL.FREE) setBallVelocity(ball, _v2);
      else continue; // caught / held meanwhile
      emit(EV.BallBlocked, { ball, blocker: o, point, normal: this._dir.clone() });
      fx('hit', point, { direction: this._dir, scale: 0.7 });
      sfx('tackleDeflect', point, 1, 0.9 + (ball.radius || 0.1));
    }
    this._deflects.clear();
  }

  /** Unstoppable: no knockback while charging; balls met head-on are swatted instead of hitting him. */
  _filterHit(hit) {
    if (!hit || hit.cancelled || !this.isCharging || (hit.victim && hit.victim !== this.owner)) return;
    scaleKnockback(hit, 0);
    const b = hit.ball;
    if (!b || b.unblockable || !b.velocity || throwerTeam(b) === this.owner.team) return;
    if (b.velocity.dot(this._dir) < -0.5) {
      hit.cancelled = true;
      if (!this._deflects.has(b)) this._deflects.set(b, (hit.point || b.position).clone());
    }
  }

  // ------------------------------------------------------------------ grab & throw

  _checkGrabs() {
    const o = this.owner, p = this.params;
    if (this._victims.length >= p.maxGrabs) return;
    const players = game.players;
    for (let i = 0; i < players.length && this._victims.length < p.maxGrabs; i++) {
      const v = players[i];
      if (!v || v === o || !game.areEnemies(o, v) || !v.isTargetable || !isAlive(v) || this._victims.includes(v)) continue;
      if (hasStatus(v, 'invulnerable')) continue; // evading (Precognition Dodge) slips the grab
      if (Math.abs(v.position.y - o.position.y) > p.maxGrabHeight) continue;
      const dx = v.position.x - o.position.x, dz = v.position.z - o.position.z;
      if (!inFrontWithin(dx, dz, this._dir.x, this._dir.z, p.grabRadius)) continue;
      this._grab(v);
    }
  }

  _grab(v) {
    const p = this.params;
    this._victims.push(v);
    v.fsm?.incapacitate?.('grabbed', p.carryTime + 0.6);
    const c = v.combat;
    if (c) {
      if (c.isCharging) c.cancelCharge?.();
      if (c.catchArmed) c.cancelCatch?.();
      if (c.hasBall) { _v.copy(this._dir).multiplyScalar(p.fumbleSpeed); _v.y = 2; c.dropBall?.(_v); }
    }
    chestOf(v, _c2);
    _pt.lerpVectors(this._chest, _c2, 0.5);
    fx('hit', _pt, { direction: _back.copy(this._dir).negate(), scale: 1.2 });
    fx('tackleDust', v.position, { direction: this._dir, scale: 1 });
    sfx('tackleImpact', _pt, 1, 0.65);
    hitstop(p.grabHitstop);
    shake(p.grabShake, 20, 0.3, (this.owner.isLocal || v.isLocal) ? null : _pt);
    v.avatar?.playHit?.(this._dir);
  }

  _carry() {
    const o = this.owner, p = this.params;
    this._drive(p.postChargeSpeed);
    planarRight(this._dir, _r);
    const n = this._victims.length;
    const yaw = Math.atan2(-this._dir.x, -this._dir.z); // victims face Gouki
    for (let i = 0; i < n; i++) {
      const v = this._victims[i];
      if (!isAlive(v)) continue;
      const lateral = n > 1 ? (i - (n - 1) * 0.5) * 0.55 : 0;
      _pt.copy(o.position).addScaledVector(this._dir, p.carryDistance).addScaledVector(_r, lateral);
      // A sideways / diagonal charge near a sideline would otherwise carry the victim into Gouki's team's U outfield
      // arm (or a backward one into Gouki's half): keep them in their own infield (they survive a prevented
      // elimination standing there).
      clampIntoInfield(v.team, _pt);
      _pt.y += p.carryLift;
      v.teleport?.(_pt, yaw);
    }
  }

  _throwVictims() {
    const o = this.owner, p = this.params;
    _back.copy(this._dir).negate(); // over his shoulder: toward his baseline = the victims' outfield side
    for (const v of this._victims) {
      v.fsm?.release?.('grabbed');
      if (!isAlive(v) || !isInfield(v) || hasStatus(v, 'invulnerable')) continue;
      _imp.copy(_back).multiplyScalar(p.throwImpulseH);
      _imp.y = p.throwImpulseV;
      chestOf(v, _pt);
      let outcome;
      try {
        outcome = v.health?.eliminate?.('tackle', o, _imp.clone(), _pt.clone());
      } catch (e) {
        console.warn('[gouki] eliminate threw', e);
      }
      const eliminated = outcome === 'eliminated' || !!(v.health && v.health.isEliminated);
      if (!eliminated) {
        // Prevented / delayed (Time Reversal, Delayed Impact): still a brutal shove.
        _v.copy(_back).multiplyScalar(p.survivorKnockback);
        _v.y = p.survivorKnockback * 0.5;
        v.motor?.addImpulse?.(_v);
        if (isAlive(v) && p.survivorStun > 0) v.fsm?.stun?.(p.survivorStun);
      }
      fx('tackleDust', v.position, { direction: _back, scale: 1.3 });
    }
    this._victims.length = 0;
    o.avatar?.playThrow?.(_back);
    sfx('tackleThrow', this._chest, 1, 0.8);
  }

  _releaseVictims() {
    for (const v of this._victims) v?.fsm?.release?.('grabbed');
    this._victims.length = 0;
  }

  _stopCharge() {
    const o = this.owner;
    if (this._speedMod) {
      o.motor?.removeSpeedModifier?.(this);
      this._speedMod = false;
      if (this.isCharging) { _v.copy(this._dir).multiplyScalar(this.params.postChargeSpeed); o.motor?.setPlanarVelocity?.(_v); }
    }
    if (this._filterOn) { this._filterOn.removeHitFilter?.(this._filter); this._filterOn = null; }
    if (this._fieldOn) { unregisterBallField(this._field); this._fieldOn = false; }
    this._flushDeflects();
  }

  // ------------------------------------------------------------------ confinement

  _extendConfinement() {
    const o = this.owner, court = game.court;
    if (!court || !o.motor || !o.motor.setConfinement || !isInfield(o)) return;
    const bounds = extendPastCentreLine(court.confinement(o.team, ZONE.INFIELD), court.sideSign(o.team), this.params.crossLine);
    o.motor.setConfinement(bounds);
    this._extended = true;
  }

  /** Walks him back into his half; true when done. */
  _tickReturn(dt) {
    const o = this.owner, court = game.court;
    if (!court || !isInfield(o) || !isAlive(o)) return true;
    const side = court.sideSign(o.team);
    const depth = depthIntoOwnHalf(side, o.position.z);
    if (depth >= this.params.insideMargin) return true;
    this._returnT += dt;
    if (this._returnT >= this.params.maxReturnTime) {
      // Could not walk back (frozen, blocked): place him just inside his half.
      _pt.copy(o.position);
      _pt.z = side * (this.params.insideMargin + 0.05);
      o.teleport?.(_pt, o.yaw);
      return true;
    }
    const m = o.motor;
    if (m) {
      _home.set(0, 0, side);
      m.setMove?.(_home, 1);
      _v.copy(_home).multiplyScalar(this.params.returnSpeed);
      m.setPlanarVelocity?.(_v);
    }
    return false;
  }

  _finishReturn() {
    if (!this._extended) return;
    this._extended = false;
    const o = this.owner, court = game.court;
    if (court && o.motor && o.motor.setConfinement) o.motor.setConfinement(court.confinement(o.team, o.zone || ZONE.INFIELD));
  }

  // ------------------------------------------------------------------ AI

  /** Best: an enemy close to the centre line within charge reach (grab & throw). Fallback: bulldoze an imminent ball. */
  evaluateAI(ctx) {
    const o = this.owner, p = this.params;
    if (!ctx || !isInfield(o) || !isGrounded(o)) return 0;
    const w = this.def.aiWeight ?? 0.6;
    const court = game.court;
    const reach = p.speed * this.chargeTime + p.grabRadius;
    let best = 0;
    const players = game.players;
    for (let i = 0; i < players.length; i++) {
      const e = players[i];
      if (!e || !game.areEnemies(o, e) || !e.isTargetable || !isAlive(e) || hasStatus(e, 'invulnerable')) continue;
      const dist = Math.hypot(e.position.x - o.position.x, e.position.z - o.position.z);
      if (dist > reach) continue;
      if (court && depthIntoOwnHalf(court.sideSign(e.team), e.position.z) > p.crossLine + p.grabRadius * 0.5) continue;
      best = Math.max(best, w * (1 - 0.35 * dist / reach));
    }
    if (best > 0) return Math.min(1, best);
    if (ctx.incomingBall && ctx.incomingTime < 0.35) return w * 0.4;
    return 0;
  }
}

// ================================================================== ULTIMATE: Earthquake Slam

export class GoukiEarthquakeSlam extends AbilityBase {
  static defaults = {
    jumpSpeed: 5,            // m/s vertical launch of grounded enemies (spec ~5)
    horizontalSpeed: 1.2,    // m/s away from the slam
    hopSpeed: 3.2,           // m/s of Gouki's own hop before the slam
    minHopTime: 0.12,        // s before a landing counts as the slam
    maxHopTime: 0.5,         // s: slam at the latest now
    dropSpeed: 3.2,          // m/s horizontal of fumbled balls (lands ~2 m away: out of auto-pickup reach)
    dropLift: 2.6,           // m/s vertical of fumbled balls
    freeBallPop: 2.6,        // m/s loose balls on the floor jump
    shakeAmp: 1,             // trauma units (heavy)
    shakeFreq: 13,           // Hz: a low rumble
    shakeDuration: 0.9,      // real s
    localTrauma: 0.45,
    hitstop: 0.06,           // s
    pulse: 0.6,
    fxRadius: 11,            // m: the dust front crosses the whole enemy half
  };

  onInitialize() {
    this._t = 0;
    this._slammed = true;
    this._wasAirborne = false;
  }

  onCast() {
    const o = this.owner;
    this._t = 0;
    this._slammed = false;
    this._wasAirborne = false;
    this.holdActive();
    const c = o.combat;
    if (c && c.isCharging) c.cancelCharge?.();
    o.motor?.addImpulse?.(_v.set(0, this.params.hopSpeed, 0));
    chestOf(o, _pt);
    sfx('slamWindup', _pt, 0.9, 1);
  }

  onTick(dt) {
    if (this._slammed) { this.endAbility(); return; }
    this._t += dt;
    const grounded = isGrounded(this.owner);
    if (!grounded) this._wasAirborne = true;
    const p = this.params;
    if ((this._t >= p.minHopTime && grounded && this._wasAirborne) || this._t >= p.maxHopTime) {
      this._slam();
      this.endAbility();
    }
  }

  /** Stunned/frozen mid-hop: he comes down early and the slam still lands. */
  onInterrupt(reason) {
    if (!this._slammed && (reason === INTERRUPT.STUNNED || reason === INTERRUPT.FROZEN)) this._slam();
    this._slammed = true;
  }

  onRoundReset() { this._slammed = true; }

  _slam() {
    if (this._slammed) return;
    this._slammed = true;
    const o = this.owner, p = this.params;
    const floorY = game.court ? game.court.floorY : 0;
    const center = _c2.set(o.position.x, floorY, o.position.z);
    const players = game.players;
    for (let i = 0; i < players.length; i++) {
      const e = players[i];
      if (!e || !game.areEnemies(o, e) || !isAlive(e) || !isInfield(e) || !isGrounded(e)) continue;
      _r.set(e.position.x - center.x, 0, e.position.z - center.z);
      if (_r.lengthSq() < 1e-6) planarForward(o, _r); else _r.normalize();
      const c = e.combat;
      if (c) {
        if (c.isCharging) c.cancelCharge?.();
        if (c.catchArmed) c.cancelCatch?.();
        if (c.hasBall) {
          // Fumbled back toward the slam (opposite to his own launch) so it lands out of his auto-pickup reach.
          _v.copy(_r).multiplyScalar(-p.dropSpeed);
          _v.y = p.dropLift;
          c.dropBall?.(_v);
        }
      }
      _v.copy(_r).multiplyScalar(p.horizontalSpeed);
      _v.y = p.jumpSpeed;
      e.motor?.addImpulse?.(_v);
      e.avatar?.playHit?.(_r);
      fx('floorDust', e.position, { scale: 0.9 });
    }
    // Loose balls lying on the floor jump with the shock.
    const balls = matchBalls();
    for (let i = 0; i < balls.length; i++) {
      const b = balls[i];
      if (!b || b.state !== BALL.FREE || b.position.y > floorY + 0.45) continue;
      _v.set(b.position.x - center.x, 0, b.position.z - center.z);
      const d = _v.length();
      if (d > 1e-3) _v.multiplyScalar(0.6 / d);
      _v.y = p.freeBallPop * (0.8 + 0.2 * Math.max(0, 1 - d / 12));
      setBallVelocity(b, _v);
    }
    // VFX 'earthquake' = dust shock ring racing over the court, crack decal, debris and a rolling dust cloud.
    fx('earthquake', center, { scale: 1, radius: p.fxRadius });
    fx('shockwave', center, { scale: 1.4, color: 0xe0d2b8 });
    sfx('earthquake', center, 1, 1);
    sfx('slam', center, 1, 0.8);
    shake(p.shakeAmp, p.shakeFreq, p.shakeDuration, null); // felt everywhere in the arena
    trauma(o.isLocal ? p.localTrauma : p.localTrauma * 0.5);
    hitstop(p.hitstop);
    pulse('heavyHit', p.pulse, 0.35);
  }

  /**
   * Best when a grounded enemy infielder holds the ball (they fumble it) or charges a throw. The slam is global, so it
   * is castable from the outfield too (a Gouki starting outfielder); enemy OUTFIELD holders are immune.
   */
  evaluateAI(ctx) {
    const o = this.owner;
    if (!ctx || !isGrounded(o)) return 0;
    const w = this.def.aiWeight ?? 0.8;
    let grounded = 0, armed = 0;
    const players = game.players;
    for (let i = 0; i < players.length; i++) {
      const e = players[i];
      if (!e || !game.areEnemies(o, e) || !isAlive(e) || !isInfield(e) || !isGrounded(e)) continue;
      grounded++;
      if (e.combat && e.combat.hasBall) armed += e.combat.isCharging ? 1.5 : 1;
    }
    if (armed >= 2) return w;
    if (armed >= 1 && grounded >= 2) return w * 0.85;
    if (armed >= 1) return w * 0.6;
    return 0;
  }
}

registerAbility('gouki.thick_hide', GoukiThickHide);
registerAbility('gouki.tackle_intercept', GoukiTackleIntercept);
registerAbility('gouki.earthquake_slam', GoukiEarthquakeSlam);
