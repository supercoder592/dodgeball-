// ---------------------------------------------------------------------------------------------------------------
// Combat - one player's ball handling: pickup, charge & throw, catch, pass, aim assist.
//
//   pickup     auto within 0.9 m (profile.autoPickupRadius), manual (E) within 1.6 m; one ball at a time (touch
//              players get the manual reach automatically: the Input's touch assist raises intent.pickup)
//   throw      hold to charge (ease-out curve to fullChargeTime, held up to maxChargeTime for Overcharge), release
//              -> buildThrowParams: base km/h -> m/s, x charge (minChargeMul..1), x1.2 counter boost (once, after a
//              Perfect Catch), rally from the ball, throw modifiers (sorted by order) -> ThrowSolver (lead + low arc)
//   catch      pressing catch arms the hands for catchWindow (0.4 s); a ball reaching the hands / body in the frontal
//              cone is classified by CatchTiming (perfect <= 0.15 s x perfectWindowMul before impact, else normal);
//              nothing arrives -> whiff (EV.CatchWhiff) and whiffRecovery (0.5 s) before the next attempt
//   perfect    revive the longest-waiting ELIMINATED outfield teammate (Match), +0.15 ult, +20% on the counter throw
//   pass       receiver: explicit `to` > intent.passTarget (bots) > the teammate nearest the aim direction (humans,
//              aim cone) > the nearest teammate who can receive; passHandler first (Houdini's Hat Trick), else a
//              flat pass, or a 34-degree LOB when the path crosses the opponents' half (infield <-> outfield)
//   intercept  an ENEMY catching a pass only takes possession: rally 0, no revive / ult / counter boost; BallCaught
//              reports quality 'normal' with intercepted:true (timingQuality keeps the real classification)
// The PlayerStateMachine (gameplay/states.js) drives beginCharge / releaseThrow / tryStartCatch from the intent;
// Combat only reads intents for that when no state machine exists. Pass / pickup / target cycling are read here
// (idempotent - harmless if the Player already handled them this frame).
// Gameplay time: scaled (game.time.now / dt).
// ---------------------------------------------------------------------------------------------------------------
import * as THREE from 'three';
import { game } from '../game.js';
import { EV } from '../core/events.js';
import { CatchTiming, CatchQuality, RallyMath } from '../core/rules.js';
import { KMH_TO_MS, MS_TO_KMH, PERFECT_CATCH_ULT_GAIN, PERFECT_CATCH_COUNTER_BOOST, GRAVITY, opponent } from '../core/constants.js';
import { BASE_COMBAT } from '../abilities/roster.js';
import { ThrowSolver, Targeting, finalSpeed, chestOf, bodyRadiusOf, isTargetable, isHiddenFromAim, matchGrantsUlt } from './throwSolver.js';
import { chargeCurve, chargeSpeedMul, chargeSecondsFor, planarAngle, pickPassIndex, segmentLengthInRect } from './throwMath.js';

const DEG = Math.PI / 180;

/** Combat tuning that is not per-hero (per-hero numbers live in roster.js BASE_COMBAT / hero.combat). */
export const COMBAT_TUNING = Object.freeze({
  normalCatchUltGain: 0.06,             // granted here only when no Match grants it (Match.rules.ultOnCatch)
  perfectCatchUltGain: PERFECT_CATCH_ULT_GAIN,
  counterBoost: PERFECT_CATCH_COUNTER_BOOST,
  minCatchHeight: 0.25,                 // m above the feet: lower balls are scooped only by the body (hit)
  catchHeightAboveHead: 0.6,            // m above the head the hands still reach
  pickupLockAfterThrow: 0.3,            // s before the thrower can grab a ball again
  pickupLockAfterDrop: 0.45,
  maxPickupHeight: 1.7,                 // m above the feet for auto / manual pickup
  passGravityScale: 1,
  passMaxSpeed: 24,                     // m/s
  passArcFactor: 1.18,                  // pass speed >= this x the 45-degree minimum for the distance
  passLobAngleDeg: 34,                  // elevation of a pass whose path crosses the opponents' half (lob)
  passLobMinCross: 1,                   // m of the path over the opponents' half that makes a pass a lob
  passAimMinCos: 0.5,                   // human passes: receivers within 60 degrees of the aim are preferred
  targetStickiness: 1.6,                // keep the current lock inside this x the assist cone
  manualLockTime: 2.5,                  // s a cycled target is kept against the automatic assist
  targetRefreshInterval: 0.08,          // s between aim-assist re-evaluations
  throwOriginForward: 0.22,             // m ahead of the chest when no hand socket is available
  throwOriginSide: 0.2,
  throwOriginUp: 0.12,
  handSocketMaxDistance: 1.1,           // m from the chest; farther hand sockets are ignored (IK glitch guard)
});

const _a = new THREE.Vector3();
const _b = new THREE.Vector3();
const _c = new THREE.Vector3();
const _aim = new THREE.Vector3();
const _solveOut = { velocity: new THREE.Vector3(), aimPoint: new THREE.Vector3(), flightTime: 0, ok: false };
// Pass receiver candidates (reused; at most a handful of teammates).
const _passPlayers = [];
const _passXZ = [];
const _passPool = [];
const _half = { minX: 0, maxX: 0, minZ: 0, maxZ: 0 };

export class Combat {
  /**
   * @param {object} player gameplay/player.js Player
   * @param {object} [profile] heroCombat(hero) - merged over roster BASE_COMBAT
   */
  constructor(player, profile) {
    this.player = player;
    /** Throw / catch tuning (a copy: abilities may raise e.g. maxChargeTime). */
    this.profile = { ...BASE_COMBAT, ...(profile || {}) };
    /** @type {import('./ball.js').Ball|null} */
    this.heldBall = null;
    this.isCharging = false;
    this.chargeSeconds = 0;
    this.catchArmed = false;
    /** Scaled time of the last catch press (-Infinity = never). */
    this.lastCatchInput = -Infinity;
    /** Perfect window multiplier (Bear's Iron Mitts 1.5). */
    this.perfectWindowMul = this.profile.perfectWindowMul > 0 ? this.profile.perfectWindowMul : 1;
    /** Frozen players cannot catch (Status sets this). */
    this.catchingBlocked = false;
    /** Aim-assist lock (Player) - HUD lock marker, default throw target. */
    this.currentTarget = null;
    /** Scaled time until which the next throw gets the +20% counter boost. */
    this.counterBoostUntil = 0;
    /**
     * Optional pass override (Houdini's Hat Trick): an object { tryHandlePass(ball, from, to) -> boolean } or a
     * function (ball, to, from) -> boolean. true = handled (the handler emits EV.BallPassed); false = default lob.
     */
    this.passHandler = null;
    /** km/h of the last throw (HUD). */
    this.lastThrowSpeedKmh = 0;

    this._modifiers = [];
    this._catchArmedUntil = 0;
    this._whiffUntil = 0;
    this._pickupLockUntil = 0;
    this._nextThrowAt = 0;
    this._manualLockUntil = 0;
    this._targetRefresh = 0;
  }

  // ================================================================== readouts
  get hasBall() { return !!this.heldBall; }
  /** Charge 0..1 (ease-out curve of chargeSeconds over fullChargeTime). */
  get charge() { return this.isCharging ? chargeCurve(this.chargeSeconds, this.profile.fullChargeTime) : 0; }
  /** Perfect catch window (s): 0.15 x perfectWindowMul. */
  get perfectWindow() { return CatchTiming.scaledPerfectWindow(this.perfectWindowMul, this.profile.perfectCatchWindow); }
  get catchWindow() { return Math.max(this.profile.catchWindow, this.perfectWindow); }
  get counterBoostActive() { return this.counterBoostUntil > game.time.now; }
  get whiffRecovering() { return this._whiffUntil > game.time.now; }

  // ================================================================== modifiers
  /** @param {{order?:number, modify?(params):void, committed?(params, ball):void}} mod */
  addThrowModifier(mod) {
    if (!mod || this._modifiers.includes(mod)) return;
    this._modifiers.push(mod);
    this._modifiers.sort((x, y) => (x.order || 0) - (y.order || 0));
  }
  removeThrowModifier(mod) {
    const i = this._modifiers.indexOf(mod);
    if (i >= 0) this._modifiers.splice(i, 1);
  }

  // ================================================================== pickup
  /** Picks up `ball` if allowed (no distance check - callers decide reach). */
  tryPickup(ball) {
    const p = this.player;
    if (!ball || this.heldBall || !ball.canBePickedUpBy || !ball.canBePickedUpBy(p)) return false;
    ball.attachTo(p);
    if (this.heldBall !== ball) this._acceptBall(ball);
    game.events.emit(EV.BallPickedUp, { ball, player: p });
    return true;
  }

  /** Nearest ball this player may grab right now within `radius` (default: manual reach), or null (HUD / touch UI). */
  nearestPickable(radius = this.profile.manualPickupRadius) {
    return this.heldBall ? null : this._nearestPickable(radius);
  }

  /** Nearest pickable ball within manualPickupRadius (E). */
  tryPickupNearest(radius = this.profile.manualPickupRadius) {
    if (this.heldBall || !game.balls) return false;
    const ball = this._nearestPickable(radius);
    return ball ? this.tryPickup(ball) : false;
  }

  /** Nearest ball this player may grab within `radius` (planar) and a sensible height band. Allocation-free. */
  _nearestPickable(radius) {
    const p = this.player, pos = p.position, list = game.balls && game.balls.active;
    if (!list) return null;
    const maxH = COMBAT_TUNING.maxPickupHeight;
    let best = null, bestD = radius * radius;
    for (let i = 0; i < list.length; i++) {
      const b = list[i];
      if (b.state !== 'free' && b.state !== 'stasis') continue;
      const dy = b.position.y - pos.y;
      if (dy < -0.3 || dy > maxH) continue;
      const dx = b.position.x - pos.x, dz = b.position.z - pos.z;
      const d = dx * dx + dz * dz;
      if (d > bestD || !b.canBePickedUpBy(p)) continue;
      bestD = d; best = b;
    }
    return best;
  }

  /** Hands `ball` to this player (abilities, passes). Drops a ball already held. */
  giveBall(ball) {
    if (!ball) return false;
    if (this.heldBall === ball) return true;
    if (this.heldBall) this.dropBall();
    ball.attachTo(this.player);
    if (this.heldBall !== ball) this._acceptBall(ball);
    game.events.emit(EV.BallPickedUp, { ball, player: this.player });
    return true;
  }

  /** Internal: Ball.attachTo keeps us in sync through this. */
  _acceptBall(ball) {
    const old = this.heldBall;
    this.heldBall = ball;
    this.cancelCatch();
    if (old && old !== ball && old.holder === this.player) old.makeFree(this.player.velocity || null, false);
  }

  /** Internal: the ball left our hand (thrown, vanished, stolen). */
  _forgetBall(ball) {
    if (this.heldBall !== ball) return;
    this.heldBall = null;
    this.isCharging = false;
    this.chargeSeconds = 0;
  }

  /**
   * Lets go of the held ball as a loose ball.
   * @param {THREE.Vector3} [velocity] default: a small forward pop + the body's velocity
   * @returns {import('./ball.js').Ball|null}
   */
  dropBall(velocity) {
    const ball = this.heldBall;
    if (!ball) return null;
    this.cancelCharge();
    if (!velocity) {
      const p = this.player, yaw = p.yaw || 0;
      velocity = _a.set(Math.sin(yaw) * 1.2, 1.5, Math.cos(yaw) * 1.2);
      if (p.velocity) velocity.addScaledVector(p.velocity, 0.5);
    }
    this.heldBall = null;
    ball.makeFree(velocity, false);
    ball.lastHolder = this.player;
    this._pickupLockUntil = game.time.now + COMBAT_TUNING.pickupLockAfterDrop;
    return ball;
  }

  // ================================================================== throwing
  /** Starts the wind-up (needs a ball). Returns true while charging. */
  beginCharge() {
    const p = this.player;
    if (this.isCharging) return true;
    if (!this.heldBall || p.canAct === false) return false;
    if (this.catchArmed) this.cancelCatch();
    this.isCharging = true;
    this.chargeSeconds = 0;
    game.events.emit(EV.ChargeStarted, { player: p, ball: this.heldBall });
    return true;
  }

  cancelCharge() {
    this.isCharging = false;
    this.chargeSeconds = 0;
  }

  /** Releases the charged throw at the current target. @returns {Ball|null} */
  releaseThrow() {
    if (!this.isCharging) return null;
    const secs = this.chargeSeconds;
    this.isCharging = false;
    this.chargeSeconds = 0;
    if (!this.heldBall || this.player.canAct === false) return null;
    const params = this.buildThrowParams(secs, this._throwTarget(), false);
    return this.launchBall(this.heldBall, params);
  }

  /**
   * Immediate throw (AI, clones, abilities) with `charge` 0..1 at `target` (current target when omitted).
   * @returns {Ball|null}
   */
  throwNow(charge = 1, target) {
    if (!this.heldBall || this.player.canAct === false) return null;
    this.isCharging = false;
    this.chargeSeconds = 0;
    const secs = chargeSecondsFor(charge, this.profile.fullChargeTime);
    const params = this.buildThrowParams(secs, target === undefined ? this._throwTarget() : target, false);
    return this.launchBall(this.heldBall, params);
  }

  /** Target for a throw now: the intent's (bots), else the aim-assist lock. */
  _throwTarget() {
    const it = this.player.intent;
    const t = it && it.target;
    if (t && t !== this.player && game.areEnemies(this.player, t) && isTargetable(t)) return t;
    const c = this.currentTarget;
    return c && isTargetable(c) && game.areEnemies(this.player, c) ? c : null;
  }

  /**
   * ThrowParams for a throw charged `chargeSeconds` at `target` (throw modifiers applied, sorted by order).
   * @returns {object} { thrower, target, origin, aimDir, aimPoint, baseSpeed, charge, chargeSeconds, speedMul,
   *   radiusMul, rallyCount, gravityScale, unblockable, pierce, isAbility, isPass, isCounter, reveals, style, payload }
   */
  buildThrowParams(chargeSeconds = 0, target = null, isAbility = false) {
    const p = this.player, prof = this.profile;
    const secs = Math.max(0, Math.min(chargeSeconds || 0, Math.max(prof.maxChargeTime || 0, prof.fullChargeTime || 0)));
    const charge = chargeCurve(secs, prof.fullChargeTime);
    const intent = p.intent;
    const aimDir = new THREE.Vector3();
    if (intent && intent.aimDir && intent.aimDir.lengthSq() > 1e-8) aimDir.copy(intent.aimDir).normalize();
    else aimDir.set(Math.sin(p.yaw || 0), 0, Math.cos(p.yaw || 0));
    const aimPoint = intent && intent.aimPoint && intent.aimPoint.isVector3 && Number.isFinite(intent.aimPoint.x) ? intent.aimPoint.clone() : null;
    const params = {
      thrower: p,
      target: target && isTargetable(target) ? target : null,
      origin: this.getThrowOrigin(),
      aimDir,
      aimPoint,
      baseSpeed: prof.throwSpeedKmh * KMH_TO_MS,
      charge,
      chargeSeconds: secs,
      speedMul: chargeSpeedMul(charge, prof.minChargeMul),
      radiusMul: 1,
      rallyCount: this.heldBall ? this.heldBall.rallyCount | 0 : 0,
      gravityScale: prof.thrownGravityScale,
      unblockable: false,
      pierce: false,
      isAbility: !!isAbility,
      isPass: false,
      isCounter: false,
      reveals: false,
      style: 'standard',
      payload: null,
    };
    if (this.counterBoostActive) {
      params.isCounter = true;
      params.speedMul *= 1 + COMBAT_TUNING.counterBoost;
    }
    for (const m of this._modifiers) {
      if (typeof m.modify !== 'function') continue;
      try { m.modify(params); } catch (e) { console.error('[combat] throw modifier threw', e); }
    }
    return params;
  }

  /**
   * Launches `ball` with `params` (solves the velocity, fires the ball, runs committed hooks, EV.BallThrown).
   * @returns {Ball|null}
   */
  launchBall(ball, params) {
    if (!ball || !params) return null;
    const p = this.player;
    if (!params.origin) params.origin = this.getThrowOrigin();
    // A held ball leaves from where the hand really is (no visual jump), unless that hand is somewhere odd.
    if (ball === this.heldBall && ball.position && chestOf(p, _c).distanceTo(ball.position) < COMBAT_TUNING.handSocketMaxDistance) {
      params.origin.copy(ball.position);
    }
    const sol = ThrowSolver.solve(params, _solveOut);
    const velocity = sol.velocity.clone();
    if (ball === this.heldBall) { this.heldBall = null; }
    this.isCharging = false;
    this.chargeSeconds = 0;
    ball.launch(params, velocity);
    if (params.isCounter) this.counterBoostUntil = 0;
    const now = game.time.now;
    this._pickupLockUntil = now + COMBAT_TUNING.pickupLockAfterThrow;
    this._nextThrowAt = now + (this.profile.throwCooldown || 0);
    const speedKmh = velocity.length() * MS_TO_KMH;
    if (!params.isPass) this.lastThrowSpeedKmh = speedKmh;
    for (const m of this._modifiers) {
      if (typeof m.committed !== 'function') continue;
      try { m.committed(params, ball); } catch (e) { console.error('[combat] throw modifier committed() threw', e); }
    }
    // Throw animation only when the ball really leaves this body (not e.g. from Screws' turret muzzle).
    if (params.origin.distanceTo(chestOf(p, _c)) < COMBAT_TUNING.handSocketMaxDistance + 0.4) {
      _a.copy(velocity);
      if (_a.lengthSq() > 1e-8) _a.normalize();
      p.avatar?.playThrow?.(_a.clone());
    }
    game.events.emit(EV.BallThrown, {
      ball, thrower: p, target: params.target || null, origin: params.origin.clone(), velocity: velocity.clone(),
      speedKmh, rallyCount: params.rallyCount | 0, charge: params.charge || 0, isAbility: !!params.isAbility,
      isPass: !!params.isPass, isCounter: !!params.isCounter,
    });
    return ball;
  }

  /**
   * Where a throw leaves the hand: the avatar's right-hand socket when sensible, else a point ahead-right of the
   * chest.
   * @param {THREE.Vector3} [out]
   */
  getThrowOrigin(out = new THREE.Vector3()) {
    const p = this.player;
    chestOf(p, _c);
    const sock = p.avatar && p.avatar.rightHandSocket;
    if (sock && typeof sock.getWorldPosition === 'function') {
      sock.getWorldPosition(out);
      if (Number.isFinite(out.x) && out.distanceTo(_c) < COMBAT_TUNING.handSocketMaxDistance) return out;
    }
    const yaw = p.yaw || 0;
    const fx = Math.sin(yaw), fz = Math.cos(yaw), rx = -Math.cos(yaw), rz = Math.sin(yaw);
    const fwd = bodyRadiusOf(p) + COMBAT_TUNING.throwOriginForward;
    return out.set(
      _c.x + fx * fwd + rx * COMBAT_TUNING.throwOriginSide,
      _c.y + COMBAT_TUNING.throwOriginUp,
      _c.z + fz * fwd + rz * COMBAT_TUNING.throwOriginSide,
    );
  }

  /** The next throw gets +20% speed (Perfect Catch reward) within profile.counterBoostDuration. */
  grantCounterBoost() {
    this.counterBoostUntil = game.time.now + (this.profile.counterBoostDuration || 3);
  }

  // ================================================================== catching
  /** Arms the hands for catchWindow seconds. @returns {boolean} armed */
  tryStartCatch() {
    const p = this.player, now = game.time.now;
    if (this.catchArmed) return true;
    if (this.heldBall || this.catchingBlocked || p.canAct === false || now < this._whiffUntil) return false;
    if (p.status && p.status.has && p.status.has('frozen')) return false;
    if (this.isCharging) this.cancelCharge();
    this.catchArmed = true;
    this.lastCatchInput = now;
    this._catchArmedUntil = now + this.catchWindow;
    game.events.emit(EV.CatchAttempt, { player: p });
    return true;
  }

  /** Disarms without the whiff penalty (stun, state change). */
  cancelCatch() {
    this.catchArmed = false;
    this._catchArmedUntil = 0;
  }

  /** Is `pos` inside the frontal catch cone (and at a catchable height)? */
  inCatchCone(pos) {
    const p = this.player;
    const h = pos.y - p.position.y;
    const height = p.height > 0 ? p.height : 1.8;
    if (h < COMBAT_TUNING.minCatchHeight || h > height + COMBAT_TUNING.catchHeightAboveHead) return false;
    const dx = pos.x - p.position.x, dz = pos.z - p.position.z;
    if (dx * dx + dz * dz < 0.15 * 0.15) return true;
    const yaw = p.yaw || 0;
    return planarAngle(Math.sin(yaw), Math.cos(yaw), dx, dz) <= (this.profile.catchConeAngle || 75) * DEG;
  }

  /**
   * A live ball reached this player's hands / body at scaled time `impactTime`. Classifies the attempt and, on
   * success, takes the ball (rally +1) and pays the rewards.
   * @returns {string} CatchQuality 'miss'|'normal'|'perfect'
   */
  tryResolveCatch(ball, point, impactTime = game.time.now) {
    const p = this.player;
    if (!ball || ball.unblockable) return CatchQuality.MISS;
    // Hands must be up: a stance cancelled by a stun, a dodge or a state change catches nothing.
    if (!this.catchArmed || this.heldBall || this.catchingBlocked || p.canAct === false) return CatchQuality.MISS;
    if (p.status && p.status.has && p.status.has('frozen')) return CatchQuality.MISS;
    if (!Number.isFinite(this.lastCatchInput)) return CatchQuality.MISS;
    const pos = point || ball.position;
    if (!this.inCatchCone(pos)) return CatchQuality.MISS;
    const secondsBeforeImpact = impactTime - this.lastCatchInput;
    const quality = CatchTiming.classify(secondsBeforeImpact, this.perfectWindow, this.catchWindow);
    if (quality === CatchQuality.MISS) return quality;

    // --- success
    const thrower = ball.lastThrower || null;
    const isPass = !!ball.isPass;
    // Catching an enemy PASS is an interception: possession only (no rally, revive, ult or counter boost).
    const intercepted = isPass && !!thrower && game.areEnemies(thrower, p);
    const speedKmh = ball.velocity.length() * MS_TO_KMH;
    const rally = intercepted ? 0 : RallyMath.next(ball.rallyCount | 0);
    const payload = ball.payload;
    const at = pos.clone();
    this.catchArmed = false;
    this._catchArmedUntil = 0;
    this.cancelCharge();
    if (payload && typeof payload.onCaught === 'function') {
      try { payload.onCaught(ball, p, quality); } catch (e) { console.error('[combat] payload.onCaught threw', e); }
    }
    ball.attachTo(p);                 // ends the live flight (payload.onEnded)
    if (this.heldBall !== ball) this._acceptBall(ball);
    ball.setRallyCount(rally);

    const reported = intercepted ? CatchQuality.NORMAL : quality;
    if (intercepted) {
      // possession only
    } else if (quality === CatchQuality.PERFECT) {
      game.match?.reviveOneOutfield?.(p.team, 'perfectCatch', p);
      p.abilities?.addUltimateCharge?.(COMBAT_TUNING.perfectCatchUltGain, 'perfectCatch');
      this.grantCounterBoost();
    } else if (!matchGrantsUlt()) {
      p.abilities?.addUltimateCharge?.(COMBAT_TUNING.normalCatchUltGain, 'catch');
    }
    p.avatar?.playCatch?.();
    game.events.emit(EV.BallCaught, {
      ball, catcher: p, thrower, quality: reported, timingQuality: quality, secondsBeforeImpact, point: at, speedKmh,
      rallyCount: rally, local: !!(p.isLocal || (thrower && thrower.isLocal)), intercepted, isPass,
    });
    return reported;
  }

  // ================================================================== passing
  /**
   * Who a pass would go to right now: `to` when it is a teammate able to receive, else intent.passTarget (bots),
   * else - for humans - the teammate best aligned with the aim (pickPassIndex), else the nearest able teammate.
   * @param {object|null} [to]
   * @returns {object|null} Player
   */
  _pickPassReceiver(to = null) {
    const p = this.player;
    if (this._canPassTo(to)) return to;
    const it = p.intent;
    if (it && this._canPassTo(it.passTarget)) return it.passTarget;
    if (!p.isHuman) return game.nearestTeammate(p, p.position);
    const list = game.players;
    _passPlayers.length = 0;
    _passXZ.length = 0;
    for (let i = 0; i < list.length; i++) {
      const r = list[i];
      if (!this._canPassTo(r)) continue;
      const n = _passPlayers.length;
      let c = _passPool[n];
      if (!c) c = _passPool[n] = { x: 0, z: 0 };
      c.x = r.position.x; c.z = r.position.z;
      _passPlayers.push(r);
      _passXZ.push(c);
    }
    let aimX = Math.sin(p.yaw || 0), aimZ = Math.cos(p.yaw || 0);
    if (it && it.aimDir && it.aimDir.lengthSq() > 1e-8) { aimX = it.aimDir.x; aimZ = it.aimDir.z; }
    const i = pickPassIndex(p.position.x, p.position.z, aimX, aimZ, _passXZ, COMBAT_TUNING.passAimMinCos);
    const r = i >= 0 ? _passPlayers[i] : null;
    _passPlayers.length = 0;
    return r;
  }

  /** A teammate (not us) who can receive a pass. */
  _canPassTo(r) {
    const p = this.player;
    return !!r && r !== p && game.areTeammates(p, r) && r.canReceive !== false;
  }

  /** The teammate a pass would reach right now (HUD pass marker), or null. */
  previewPassReceiver() {
    if (!this.heldBall) return null;
    return this._pickPassReceiver(null);
  }

  /**
   * Pass the held ball (passHandler first). Receiver order: explicit `to` > intent.passTarget > aim cone (humans) >
   * nearest teammate who can receive.
   * @param {object|null} [to]
   * @returns {boolean}
   */
  tryPass(to = null) {
    const p = this.player;
    const ball = this.heldBall;
    if (!ball || p.canAct === false) return false;
    to = this._pickPassReceiver(to);
    if (!to) return false;
    const h = this.passHandler;
    if (h) {
      // Either an object { tryHandlePass(ball, from, to) } (Houdini's Hat Trick) or a function (ball, to, from).
      // A handler that returns true owns the hand-off and its presentation (including EV.BallPassed).
      let handled = false;
      try {
        if (typeof h.tryHandlePass === 'function') handled = h.tryHandlePass(ball, p, to) === true;
        else if (typeof h === 'function') handled = h(ball, to, p) === true;
      } catch (e) { console.error('[combat] passHandler threw', e); }
      if (handled) {
        if (this.heldBall === ball && ball.holder !== p) this.heldBall = null;
        this.cancelCharge();
        return true;
      }
      if (this.heldBall !== ball) return false; // declined but the ball moved anyway: nothing left to lob
    }
    const params = this._buildPassParams(to);
    this.launchBall(ball, params);
    game.events.emit(EV.BallPassed, { ball, from: p, to, teleported: false, lob: Number.isFinite(params.launchAngle) });
    return true;
  }

  /** Does the straight path from `a` to `b` run over the opponents' half for more than passLobMinCross metres? */
  _crossesEnemyHalf(a, b) {
    const court = game.court;
    if (!court) return false;
    const s = court.sideSign(opponent(this.player.team));
    _half.minX = -court.halfW; _half.maxX = court.halfW;
    _half.minZ = s < 0 ? -court.halfL : 0; _half.maxZ = s < 0 ? 0 : court.halfL;
    return segmentLengthInRect(a.x, a.z, b.x, b.z, _half) > COMBAT_TUNING.passLobMinCross;
  }

  _buildPassParams(to) {
    const p = this.player, prof = this.profile;
    const origin = this.getThrowOrigin();
    if (this.heldBall && chestOf(p, _c).distanceTo(this.heldBall.position) < COMBAT_TUNING.handSocketMaxDistance) origin.copy(this.heldBall.position);
    chestOf(to, _aim);
    const g = GRAVITY * COMBAT_TUNING.passGravityScale;
    const dist = Math.hypot(_aim.x - origin.x, _aim.z - origin.z) + Math.max(0, _aim.y - origin.y);
    // Enough speed for a comfortable arc over the distance (45 deg minimum speed x factor), capped.
    const needed = Math.sqrt(g * Math.max(1, dist)) * COMBAT_TUNING.passArcFactor;
    const speed = Math.min(COMBAT_TUNING.passMaxSpeed, Math.max((prof.passSpeedKmh || 45) * KMH_TO_MS, needed));
    const rally = this.heldBall ? this.heldBall.rallyCount | 0 : 0;
    const params = {
      thrower: p, target: to, origin, aimDir: _b.subVectors(_aim, origin).normalize().clone(), aimPoint: _aim.clone(),
      // The ball keeps its rally count through a pass, but a pass is never rally-boosted: pre-divide the multiplier.
      baseSpeed: speed / RallyMath.multiplier(rally), charge: 0, chargeSeconds: 0, speedMul: 1, radiusMul: 1,
      rallyCount: rally, gravityScale: COMBAT_TUNING.passGravityScale,
      unblockable: false, pierce: false, isAbility: false, isPass: true, isCounter: false, reveals: false,
      style: 'standard', payload: null,
    };
    // Over the opponents' heads (infield <-> outfield crossfire): a high lob they can only reach near either end.
    if (this._crossesEnemyHalf(origin, _aim)) {
      params.launchAngle = COMBAT_TUNING.passLobAngleDeg * DEG;
      params.maxSpeed = COMBAT_TUNING.passMaxSpeed;
    }
    return params;
  }

  /**
   * A pass (or a teammate's throw while our hands are armed) reached us. @returns {boolean} taken
   */
  receivePass(ball, from) {
    const p = this.player;
    if (!ball || this.heldBall || p.canAct === false || this.catchingBlocked) return false;
    if (p.health && p.health.isAlive === false) return false;
    const rally = ball.rallyCount | 0;
    this.cancelCatch();
    ball.attachTo(p);
    if (this.heldBall !== ball) this._acceptBall(ball);
    ball.setRallyCount(rally);
    p.avatar?.playCatch?.();
    game.events.emit(EV.BallPickedUp, { ball, player: p, from: from || null, pass: true });
    return true;
  }

  // ================================================================== per frame
  /** Scaled dt. Called by Player.update after the state machine. */
  update(dt) {
    const p = this.player, now = game.time.now;
    // A ball that left our hand without telling us (despawned, stolen) is forgotten.
    if (this.heldBall && this.heldBall.holder !== p) { this.heldBall = null; this.cancelCharge(); }

    // Catch window elapsed without a ball -> whiff.
    if (this.catchArmed && now > this._catchArmedUntil) {
      this.catchArmed = false;
      this._whiffUntil = now + (this.profile.whiffRecovery || 0);
      game.events.emit(EV.CatchWhiff, { player: p });
    }
    if (this.catchingBlocked && this.catchArmed) this.cancelCatch();

    if (p.canAct === false) {
      if (this.isCharging) this.cancelCharge();
      return;
    }

    const intent = p.intent;
    this._updateTargeting(dt, intent);

    // Charge accumulates up to maxChargeTime (Rayne's Overcharge reads chargeSeconds past full charge).
    if (this.isCharging) {
      if (!this.heldBall) this.cancelCharge();
      else this.chargeSeconds = Math.min(this.chargeSeconds + dt, Math.max(this.profile.maxChargeTime || 0, this.profile.fullChargeTime || 0));
    }

    if (intent) {
      // Without a state machine Combat reads the throw / catch buttons itself.
      if (!p.fsm) {
        if (intent.catchPressed && !this.heldBall) this.tryStartCatch();
        if (intent.throwPressed && this.heldBall && !this.isCharging) this.beginCharge();
        if (this.isCharging && (intent.throwReleased || !intent.throwHeld)) this.releaseThrow();
      }
      if (intent.pass && this.heldBall && !this.isCharging) this.tryPass();
      if (intent.pickup && !this.heldBall) this.tryPickupNearest();
    }

    // Auto pickup of a ball at the feet.
    if (!this.heldBall && game.balls && now >= this._pickupLockUntil) {
      const ball = this._nearestPickable(this.profile.autoPickupRadius);
      if (ball) this.tryPickup(ball);
    }
  }

  /** Aim assist: intent target (bots) > manual cycle lock > sticky best-in-cone. */
  _updateTargeting(dt, intent) {
    const p = this.player, now = game.time.now, prof = this.profile;
    const valid = (t) => !!t && t !== p && game.areEnemies(p, t) && isTargetable(t) && !isHiddenFromAim(t);
    // A bot's chosen target wins. A human's intent.target only mirrors currentTarget (HumanController), so it is no
    // override: target cycling (Tab / R3 / touch tap) and the automatic soft lock below keep working for humans.
    if (!p.isHuman && intent && intent.target && valid(intent.target)) { this.currentTarget = intent.target; return; }

    const dir = intent && intent.aimDir && intent.aimDir.lengthSq() > 1e-8 ? intent.aimDir : _b.set(Math.sin(p.yaw || 0), 0, Math.cos(p.yaw || 0));
    if (intent && intent.cycleTarget) {
      const origin = this.getThrowOrigin(_a);
      const next = Targeting.cycle(p, valid(this.currentTarget) ? this.currentTarget : null, origin, dir, prof.aimAssistRange * 1.3);
      if (next) { this.currentTarget = next; this._manualLockUntil = now + COMBAT_TUNING.manualLockTime; }
      return;
    }
    if (valid(this.currentTarget) && now < this._manualLockUntil) return;

    this._targetRefresh -= dt;
    if (this._targetRefresh > 0 && valid(this.currentTarget)) return;
    this._targetRefresh = COMBAT_TUNING.targetRefreshInterval;
    const origin = this.getThrowOrigin(_a);
    // Keep the current lock while it stays inside a wider cone (no flicker between two close targets).
    if (valid(this.currentTarget)) {
      const t = this.currentTarget;
      chestOf(t, _c);
      const ang = planarAngle(dir.x, dir.z, _c.x - origin.x, _c.z - origin.z);
      if (ang <= prof.aimAssistAngle * COMBAT_TUNING.targetStickiness * DEG && _c.distanceTo(origin) <= prof.aimAssistRange * 1.1) return;
    }
    this.currentTarget = Targeting.findBest(p, origin, dir, prof.aimAssistAngle, prof.aimAssistRange);
  }

  /** New round: empty hands, no charge / catch / boosts (BallManager resets the balls). */
  resetForRound() {
    if (this.heldBall && this.heldBall.holder === this.player) this.heldBall._detachFromHolder();
    this.heldBall = null;
    this.isCharging = false;
    this.chargeSeconds = 0;
    this.catchArmed = false;
    this.lastCatchInput = -Infinity;
    this.currentTarget = null;
    this.counterBoostUntil = 0;
    this._catchArmedUntil = 0;
    this._whiffUntil = 0;
    this._pickupLockUntil = 0;
    this._nextThrowAt = 0;
    this._manualLockUntil = 0;
  }

  dispose() {
    if (this.heldBall) this.dropBall();
    this._modifiers.length = 0;
    this.passHandler = null;
  }
}

export { finalSpeed };
