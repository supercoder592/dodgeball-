// ---------------------------------------------------------------------------------------------------------------
// Houdini (Trickster / Support) - Hat Trick (passive) / Swap Places (skill, 0.5 s channel, CD 16) /
// Grand Vanish (ultimate). Numbers come from roster.js `params` (merged over each class's `static defaults`).
//
// Court rules always hold: every relocation (swap destinations, summoned balls) is clamped into the owning team's zone
// with the Court confinement bounds. Gameplay timers run on SCALED time; optional systems (vfx, audio, renderer,
// juice) are guarded and never able to break the trick itself. Pure geometry lives in ../shared/houdiniMath.js
// (unit tested).
// ---------------------------------------------------------------------------------------------------------------
import * as THREE from 'three';
import { game, ORDER } from '../../game.js';
import { EV } from '../../core/events.js';
import { ZONE } from '../../core/constants.js';
import { Court } from '../../world/court.js';
import { AbilityBase, registerAbility, FAIL, INTERRUPT } from '../abilityBase.js';
import { mirroredSwapDestinations, pushAwayPlanar, fanAngle, roundRobin, spreadX, ringPoint } from '../shared/houdiniMath.js';

/** Stage-magic palette: neutral grey smoke with a faint violet cast, pale violet flashes (no neon). */
export const HOUDINI_FX = Object.freeze({ smoke: 0x9d93ab, flash: 0xbda3f2, channelTint: 0xa77cf0 });

const DEG = Math.PI / 180;
const _v = new THREE.Vector3();
const _v2 = new THREE.Vector3();
const _dir = new THREE.Vector3();
const _zero = new THREE.Vector3();
const _hFrom = new THREE.Vector3();
const _eFrom = new THREE.Vector3();
const _hTo = new THREE.Vector3();
const _eTo = new THREE.Vector3();

// ------------------------------------------------------------------ guarded optional-system helpers

const _warned = new Set();
function warnOnce(key, e) {
  if (_warned.has(key)) return;
  _warned.add(key);
  console.warn(`[houdini] optional ${key} call failed`, e);
}
function fxPlay(id, pos, opts) {
  const vfx = game.vfx;
  if (!vfx || !vfx.play) return;
  try { vfx.play(id, pos, opts); } catch (e) { warnOnce(`vfx.play(${id})`, e); }
}
function fxAttach(id, obj, opts) {
  const vfx = game.vfx;
  if (!vfx || !vfx.attach || !obj) return null;
  try { return vfx.attach(id, obj, opts) || null; } catch (e) { warnOnce(`vfx.attach(${id})`, e); return null; }
}
function fxStop(handle) {
  const vfx = game.vfx;
  if (!handle || !vfx || !vfx.stop) return;
  try { vfx.stop(handle); } catch (e) { warnOnce('vfx.stop', e); }
}
function sfx(id, pos, volume = 1, pitch = 1) {
  const audio = game.audio;
  if (!audio || !audio.play) return;
  try { audio.play(id, pos, volume, pitch); } catch (e) { warnOnce(`audio.play(${id})`, e); }
}
function screenPulse(type, intensity, duration) {
  const r = game.renderer;
  if (!r || !r.pulse) return;
  try { r.pulse(type, intensity, duration); } catch (e) { warnOnce('renderer.pulse', e); }
}
function juice(method, a, b) {
  const j = game.juice;
  if (!j || typeof j[method] !== 'function') return;
  try { j[method](a, b); } catch (e) { warnOnce(`juice.${method}`, e); }
}
function setTint(player, color, amount) {
  const av = player && player.avatar;
  if (!av || !av.setTint) return;
  try { av.setTint(color, amount); } catch (e) { warnOnce('avatar.setTint', e); }
}
const floorY = () => (game.court ? game.court.floorY : 0);

/** World position of a ball (held balls are parented to a hand socket, free/live balls live in the scene). */
function ballWorldPosition(ball, out) {
  const root = ball && ball.root;
  if (root && root.parent && root.parent !== game.scene && root.getWorldPosition) return root.getWorldPosition(out);
  return out.copy(ball.position);
}

/** Clamps `pos` (in place) into the confinement of `team`/`zone` - court rules must always hold. */
function clampToZone(team, zone, pos) {
  if (game.court) Court.clamp(game.court.confinement(team, zone), pos);
  return pos;
}

// ------------------------------------------------------------------ targeting

function isValidEnemyTarget(self, p, maxDist, allowCloaked) {
  if (!p || !game.areEnemies(self, p) || !p.isTargetable) return false;
  if (!allowCloaked && p.status && p.status.has('cloaked') && !p.status.has('revealed')) return false;
  return p.position.distanceToSquared(self.position) <= maxDist * maxDist;
}

/**
 * The soft-locked combat target, else the best enemy inside the aim cone (angular error first, distance breaks ties),
 * else (bots only) the nearest valid enemy. Allocation-free.
 */
function resolveAimTarget(self, maxDist, coneDeg, allowCloaked, nearestFallback) {
  const cur = self.combat ? self.combat.currentTarget : null;
  if (isValidEnemyTarget(self, cur, maxDist, allowCloaked)) return cur;
  const aim = self.intent && self.intent.aimDir && self.intent.aimDir.lengthSq() > 1e-6 ? self.intent.aimDir : self.forward;
  _dir.set(aim ? aim.x : 0, 0, aim ? aim.z : 1);
  if (_dir.lengthSq() < 1e-8) _dir.set(0, 0, 1);
  _dir.normalize();
  const cosMax = Math.cos(coneDeg * DEG);
  let best = null, bestScore = Infinity, nearest = null, nearestD = Infinity;
  for (const p of game.players) {
    if (!isValidEnemyTarget(self, p, maxDist, allowCloaked)) continue;
    _v.subVectors(p.position, self.position); _v.y = 0;
    const d = _v.length();
    if (d < 1e-3) continue;
    if (d < nearestD) { nearestD = d; nearest = p; }
    const c = _v.dot(_dir) / d;
    if (c < cosMax) continue;
    const score = (1 - c) * 12 + d * 0.08;
    if (score < bestScore) { bestScore = score; best = p; }
  }
  return best || (nearestFallback ? nearest : null);
}

// =================================================================================================================
// Passive - Hat Trick
// =================================================================================================================

/**
 * Houdini passive [Hat Trick]: pressing Pass makes the held ball vanish from Houdini's hand and reappear directly in a
 * teammate's hands - no lob, nothing for the enemy to intercept.
 *
 * Implemented as `owner.combat.passHandler = { tryHandlePass(ball, from, to) }` (installed on equip, re-installed if
 * Combat is recreated/cleared, removed on unequip; never steals a handler somebody else installed). Receiver: the
 * nearest teammate able to take the ball right now (free hands, can act, not frozen), strictly preferring Houdini's own
 * zone (infield->infield, outfield->outfield); the Combat's suggested `to` wins ties. When nobody can receive - or the
 * receiver refuses the ball - the handler returns false and the default lob pass runs.
 * Presentation: a smoke puff at both ends, teleport SFX, and EV.BallPassed with `teleported: true`.
 */
export class HoudiniHatTrick extends AbilityBase {
  static defaults = {
    maxRange: 0,                 // m to the receiver; 0 = anywhere in the arena
    preferSameZone: true,
    allowOutfieldReceivers: true,
    poofScale: 0.55,
    sfxVolume: 0.85,
  };

  onInitialize() {
    this._handler = { tryHandlePass: (ball, from, to) => this.tryHandlePass(ball, from, to) };
    this._registeredOn = null;
    this.teleportedPasses = 0;
  }

  onEquip() { this._register(); }
  onUnequip() { this._unregister(); }

  onTick() {
    const c = this.owner.combat;
    if (c && (c !== this._registeredOn || c.passHandler == null)) this._register();
  }

  /** Passive: never cast. */
  onCast() {}
  evaluateAI() { return 0; }

  /**
   * Pass handler. Teleports `ball` into the best receiver's hands.
   * @returns {boolean} true when fully handled (the default lob pass is then skipped)
   */
  tryHandlePass(ball, from, to) {
    if (!ball || !from || !from.combat) return false;
    const fc = from.combat;
    if (fc.heldBall !== ball && ball.holder !== from) return false;
    const receiver = this._selectReceiver(from, to);
    if (!receiver) return false;
    const tc = receiver.combat;
    ballWorldPosition(ball, _v);
    // Release from Houdini's hand first so both Combats never reference the ball at once.
    if (fc.isCharging && fc.cancelCharge) fc.cancelCharge();
    if (fc.heldBall === ball && fc.dropBall) fc.dropBall(_zero.set(0, 0, 0));
    tc.giveBall(ball);
    if (tc.heldBall !== ball) {
      // Refused (state changed during the hand-off): back into Houdini's hand, decline -> default lob pass.
      if (!fc.hasBall && ball.state !== 'held' && fc.giveBall) fc.giveBall(ball);
      return false;
    }
    const p = this.params;
    if (tc.getThrowOrigin) _v2.copy(tc.getThrowOrigin()); else _v2.copy(receiver.chestPosition || receiver.position);
    fxPlay('teleport', _v, { scale: p.poofScale, color: HOUDINI_FX.smoke });
    fxPlay('teleport', _v2, { scale: p.poofScale, color: HOUDINI_FX.smoke });
    sfx('teleport', _v, p.sfxVolume, 1);
    sfx('teleport', _v2, p.sfxVolume, 1.08);
    this.teleportedPasses++;
    game.events.emit(EV.BallPassed, { ball, from, to: receiver, teleported: true });
    return true;
  }

  _selectReceiver(from, suggested) {
    const p = this.params;
    const maxSq = p.maxRange > 0 ? p.maxRange * p.maxRange : Infinity;
    let best = null, bestScore = Infinity;
    for (const pl of game.players) {
      if (!this._canReceive(from, pl)) continue;
      const dx = pl.position.x - from.position.x, dz = pl.position.z - from.position.z;
      const d2 = dx * dx + dz * dz;
      if (d2 > maxSq) continue;
      let score = Math.sqrt(d2);
      if (p.preferSameZone && pl.zone !== from.zone) score += 10000; // any same-zone receiver beats any cross-zone one
      if (pl === suggested) score -= 0.01;
      if (score < bestScore) { bestScore = score; best = pl; }
    }
    return best;
  }

  _canReceive(from, pl) {
    if (!pl || pl === from || !game.areTeammates(from, pl)) return false;
    if (!pl.combat || pl.combat.hasBall || !pl.combat.giveBall) return false;
    if (pl.canReceive === false || pl.canAct === false) return false;
    if (!this.params.allowOutfieldReceivers && pl.zone === ZONE.OUTFIELD) return false;
    if (pl.status && pl.status.has('frozen')) return false;
    return true;
  }

  _register() {
    const combat = this.owner.combat;
    if (!combat) return;
    if (this._registeredOn && this._registeredOn !== combat) this._unregister();
    if (combat.passHandler == null || combat.passHandler === this._handler) {
      combat.passHandler = this._handler;
      this._registeredOn = combat;
    }
  }

  _unregister() {
    const c = this._registeredOn;
    if (c && c.passHandler === this._handler) c.passHandler = null;
    this._registeredOn = null;
  }
}

// =================================================================================================================
// Skill - Swap Places
// =================================================================================================================

/**
 * Houdini skill [Swap Places] (0.5 s channel, CD 16 s): target an enemy and trade places with them.
 *
 * Targeting: `combat.currentTarget`, else the best enemy inside the aim cone (bots: nearest enemy as a last resort);
 * none => FAIL.NO_TARGET (no cooldown spent). Cloaked enemies cannot be targeted unless revealed.
 * Channel: Houdini is rooted (Status `rooted`) while a swap shimmer plays on both players and both avatars take a
 * rising violet tint - a readable tell. Stun / freeze / elimination interrupt it (full cooldown, kernel rules); if the
 * target stops being valid (eliminated, cloaked, out of range) the channel is cancelled with a 75% cooldown refund.
 *
 * DESIGN CHOICE - the mirrored, court-relative swap. A literal swap would put Houdini inside the enemy half and the
 * enemy inside Houdini's half, breaking the rule that each team stays on its own side (the motor confinement would
 * then shove both back with a jarring pop). Instead each takes the OTHER's spot relative to the court:
 *     Houdini -> (enemy.x,   |enemy.z|   mirrored into Houdini's half)
 *     enemy   -> (Houdini.x, |Houdini.z| mirrored into the enemy half)
 * Lane and depth are exchanged, so it still reads as a swap - an aggressive enemy at the centre line is flung back to
 * their baseline while Houdini takes the front, lanes cross over - but both stay legal. Both destinations are clamped
 * to each player's zone and nudged 0.8 m clear of bystanders; facing is preserved. The enemy suffers a 0.3 s
 * disorientation stun (cancels their charge / catch stance).
 */
export class HoudiniSwapPlaces extends AbilityBase {
  static defaults = {
    range: 25,               // m to the swap target
    stun: 0.3,               // s disorientation on the swapped enemy
    aimCone: 25,             // deg half-angle searched when nothing is soft-locked
    allowCloakedTargets: false,
    minSeparation: 0.8,      // m kept from other players at the arrival points
    lostTargetRefund: 0.75,  // fraction of the cooldown refunded when the target slips away
    lostTargetRangeMul: 1.25,
    rootPad: 0.1,            // s the root outlasts the channel (no gap)
    flashScale: 1,
    channelTint: 0.35,       // peak avatar tint during the channel
    swapTrauma: 0.3,         // camera trauma when the local player is involved
    localWarp: 0.45,         // 'rewind' screen warp for an involved local player
    aiDodgeMargin: 0.15,     // bots swap away from balls arriving after channel + margin ...
    aiDodgeWindow: 1.4,      // ... and before this (s)
  };

  onInitialize() {
    this._target = null;
    this._tinted = null;
    this._ownerFx = null;
    this._targetFx = null;
    this._rooted = false;
    this._refundOnCooldown = false;
    this.swaps = 0;
  }

  /** Pending / current swap target. */
  get target() { return this._target; }

  canActivateCustom() {
    this._target = this._resolveTarget();
    return this._target ? null : FAIL.NO_TARGET;
  }

  onCastStarted() {
    const o = this.owner;
    const p = this.params;
    if (!this._isValid(this._target, 1)) this._target = this._resolveTarget();
    const channel = this.castTime + p.rootPad;
    if (o.status) { o.status.apply('rooted', channel, 1, this); this._rooted = true; }
    if (o.motor && o.motor.setPlanarVelocity) o.motor.setPlanarVelocity(_zero.set(0, 0, 0));
    this._ownerFx = fxAttach('swapFlash', o.root, { duration: channel, color: HOUDINI_FX.flash, scale: p.flashScale * 0.8 });
    if (this._target) {
      this._targetFx = fxAttach('swapFlash', this._target.root, { duration: channel, color: HOUDINI_FX.flash, scale: p.flashScale * 0.8 });
      this._tinted = this._target;
    }
    sfx('swapChannel', o.position, 0.8, 1);
  }

  onCastTick(dt, progress) {
    const o = this.owner;
    const p = this.params;
    // Fallbacks in case the owner's interrupt path did not run this frame.
    if (o.status && o.status.has('frozen')) { this.interrupt(INTERRUPT.FROZEN); return; }
    if (o.fsm && o.fsm.is && o.fsm.is('stunned')) { this.interrupt(INTERRUPT.STUNNED); return; }
    if (o.zone !== ZONE.INFIELD || (o.health && o.health.isAlive === false)) { this.interrupt(INTERRUPT.ELIMINATED); return; }
    if (!this._isValid(this._target, p.lostTargetRangeMul)) {
      this.interrupt(INTERRUPT.CANCELLED);
      this.cooldown.reduce(this.cooldownDuration * p.lostTargetRefund);
      return;
    }
    const k = p.channelTint * progress;
    setTint(o, HOUDINI_FX.channelTint, k);
    setTint(this._target, HOUDINI_FX.channelTint, k);
  }

  onCast() {
    this._clearChannel();
    if (!this._isValid(this._target, this.params.lostTargetRangeMul)) this._target = this._resolveTarget();
    if (!this._target) { this._refundOnCooldown = true; return; } // duration 0 -> ends now; refund in onCooldown
    this._performSwap(this.owner, this._target);
  }

  onCooldown() {
    if (!this._refundOnCooldown) return;
    this._refundOnCooldown = false;
    this.cooldown.reduce(this.cooldownDuration * this.params.lostTargetRefund);
  }

  onInterrupt() { this._clearChannel(); }
  onEnd() { this._clearChannel(); this._target = null; }
  onRoundReset() { this._clearChannel(); this._target = null; this._refundOnCooldown = false; }
  onUnequip() { this._clearChannel(); }

  /**
   * Bots swap (a) to leave the lane of a ball that arrives after the channel completes, (b) to break an enemy's
   * charged throw (the stun cancels it), (c) occasionally to reshuffle lanes when an enemy is close.
   */
  evaluateAI(ctx) {
    const p = this.params;
    const enemy = ctx && ctx.nearestEnemy;
    if (!enemy || !ctx.self || !(ctx.nearestEnemyDistance <= p.range)) return 0;
    let u = 0;
    const minTime = this.castTime + p.aiDodgeMargin;
    if (ctx.incomingBall && ctx.incomingTime >= minTime && ctx.incomingTime <= p.aiDodgeWindow
      && Math.abs(enemy.position.x - ctx.self.position.x) > 1.2) u = 0.75;
    if (enemy.combat && enemy.combat.isCharging) u = Math.max(u, 0.6);
    if (u <= 0 && ctx.nearestEnemyDistance < 9) u = 0.15;
    const w = this.def.aiWeight ?? 0.5;
    return Math.max(0, Math.min(1, u * (0.5 + w)));
  }

  // ---------------------------------------------------------------- internals

  _resolveTarget() {
    const p = this.params;
    return resolveAimTarget(this.owner, p.range, p.aimCone, p.allowCloakedTargets, !this.owner.isHuman);
  }

  _isValid(t, rangeMul) {
    return isValidEnemyTarget(this.owner, t, this.params.range * rangeMul, this.params.allowCloakedTargets);
  }

  _clearChannel() {
    fxStop(this._ownerFx); this._ownerFx = null;
    fxStop(this._targetFx); this._targetFx = null;
    if (this._rooted) {
      if (this.owner.status) this.owner.status.remove('rooted', this);
      this._rooted = false;
    }
    setTint(this.owner, HOUDINI_FX.channelTint, 0);
    if (this._tinted) { setTint(this._tinted, HOUDINI_FX.channelTint, 0); this._tinted = null; }
  }

  /** Executes the mirrored, court-relative swap (see class docs). */
  _performSwap(h, e) {
    const p = this.params;
    _hFrom.copy(h.position);
    _eFrom.copy(e.position);
    this._computeDestinations(h, e, _hTo, _eTo);
    const hYaw = h.yaw, eYaw = e.yaw;
    // Departure puffs.
    fxPlay('teleport', _v.copy(_hFrom).setY(_hFrom.y + 1), { scale: p.flashScale, color: HOUDINI_FX.smoke });
    fxPlay('teleport', _v.copy(_eFrom).setY(_eFrom.y + 1), { scale: p.flashScale, color: HOUDINI_FX.smoke });
    h.teleport(_hTo, hYaw);
    e.teleport(_eTo, eYaw);
    // Arrival flashes + sound at both ends.
    fxPlay('swapFlash', _v.copy(_hTo).setY(_hTo.y + 1), { scale: p.flashScale, color: HOUDINI_FX.flash });
    fxPlay('swapFlash', _v.copy(_eTo).setY(_eTo.y + 1), { scale: p.flashScale, color: HOUDINI_FX.flash });
    sfx('teleport', _hTo, 1, 1);
    sfx('teleport', _eTo, 1, 0.94);
    // Disorientation: cancels the enemy's charge / catch stance.
    if (p.stun > 0 && e.fsm && e.fsm.stun) e.fsm.stun(p.stun);
    if (h.isLocal || e.isLocal) {
      juice('addTrauma', p.swapTrauma);
      screenPulse('rewind', p.localWarp, 0.3);
    }
    this.swaps++;
  }

  /** Mirrored destinations, clamped to each player's zone and separated from bystanders. */
  _computeDestinations(h, e, outH, outE) {
    const c = game.court;
    if (c && h.team >= 0 && e.team >= 0) {
      mirroredSwapDestinations(h.position, e.position, c.sideSign(h.team), c.sideSign(e.team), 0, outH, outE);
    } else {
      // No court (sandbox): plain swap, each keeping its own height.
      outH.set(e.position.x, h.position.y, e.position.z);
      outE.set(h.position.x, e.position.y, h.position.z);
    }
    clampToZone(h.team, h.zone, outH);
    clampToZone(e.team, e.zone, outE);
    this._separate(outH, h, e, outE);
    clampToZone(h.team, h.zone, outH);
    this._separate(outE, e, h, outH);
    clampToZone(e.team, e.zone, outE);
  }

  /** One relaxation pass pushing `dest` out of other players' personal space (toward the court's long axis). */
  _separate(dest, mover, partner, partnerDest) {
    const min = this.params.minSeparation;
    const fx = dest.x > 0 ? -1 : 1; // push toward the middle of the court when positions coincide
    pushAwayPlanar(dest, partnerDest, min, fx, 0);
    for (const pl of game.players) {
      if (pl === mover || pl === partner || !pl.position) continue;
      if (pl.team !== mover.team || pl.zone !== mover.zone) continue; // only players sharing the mover's area
      pushAwayPlanar(dest, pl.position, min, fx, 0);
    }
    return dest;
  }
}

// =================================================================================================================
// Ultimate - Grand Vanish
// =================================================================================================================

/**
 * Watches balls vanished by Grand Vanish and puffs them back into existence (smoke + sound) when they return on the
 * centre line. A temporary game system (ORDER.ABILITIES) that removes itself once every ball is back or on timeout,
 * so it keeps working after the (instant) ultimate has ended.
 */
class VanishReturnWatcher {
  /** @param {HoudiniGrandVanish} ability */
  constructor(ability) {
    this.ability = ability;
    this.balls = [];
    this.elapsed = 0;
    this.timeout = 0;
    this._registered = false;
  }

  watch(ball) {
    if (!ball || ball.state !== 'despawned') return;
    if (!this.balls.includes(ball)) this.balls.push(ball);
    const p = this.ability.params;
    this.elapsed = 0;
    this.timeout = p.vanishTime + p.returnTimeout;
    if (!this._registered) { game.addSystem(this, ORDER.ABILITIES); this._registered = true; }
  }

  /** System hook (scaled dt). */
  update(dt) {
    this.elapsed += dt;
    const p = this.ability.params;
    for (let i = this.balls.length - 1; i >= 0; i--) {
      const b = this.balls[i];
      if (b.state === 'despawned') continue;
      this.balls.splice(i, 1);
      ballWorldPosition(b, _v);
      fxPlay('vanishSmoke', _v, { scale: p.smokeScale * 0.8, color: HOUDINI_FX.smoke });
      sfx('vanish', _v, 0.6, 1.1);
    }
    if (this.balls.length === 0 || this.elapsed > this.timeout) this.stop();
  }

  stop() {
    this.balls.length = 0;
    if (this._registered) { game.removeSystem(this); this._registered = false; }
  }
}

/**
 * Houdini ultimate [Grand Vanish] - the great disappearing act:
 *  1. Every ball held by an enemy (infield or outfield) vanishes from their hands in a puff of smoke: any charge is
 *     cancelled, the ball is released and `despawn(4 s, respawn)`ed; it reappears ON the centre line (spread across the
 *     width like the opening rush, dropping from 0.6 m - neutral, whoever gets there first).
 *  2. Every free ball on the court teleports to the feet of Houdini's infield team (Houdini included), round-robin
 *     over players without a ball first, on a small fan in front of each receiver (inside auto-pickup reach) and
 *     clamped into their own infield, so the balls always land in friendly territory.
 * Smoke at both ends of every relocation, heavy juice on cast. Refuses to fire (FAIL.NO_TARGET, meter kept) when
 * there is nothing to vanish and nothing to summon.
 */
export class HoudiniGrandVanish extends AbilityBase {
  static defaults = {
    vanishTime: 4,          // s enemy-held balls stay gone
    respawnHeight: 0.6,     // m above the floor where vanished balls reappear
    includeHoudini: true,   // Houdini's own feet also receive balls
    ringRadius: 0.7,        // m from a receiver's feet (below the 0.9 m auto-pickup reach)
    ringSpacingDeg: 55,     // deg between balls placed around the same receiver
    dropHeight: 0.25,       // m: summoned balls pop in slightly above the floor and bounce once
    smokeScale: 0.8,
    castHitstop: 0.05,      // s (juice, unscaled)
    castTrauma: 0.35,
    castPulse: 0.6,
    aiMinEnemyHeld: 2,      // bots cast once enemies hold this many balls
    returnTimeout: 3,       // s after vanishTime the return watcher gives up
  };

  onInitialize() {
    this._enemyHeld = [];
    this._free = [];
    this._receivers = [];
    this._rr = { receiver: 0, slot: 0 };
    this._watcher = new VanishReturnWatcher(this);
    this._cntEnemyHeld = 0;
    this._cntFree = 0;
    this.lastVanished = 0;
    this.lastSummoned = 0;
  }

  canActivateCustom() {
    if (!game.balls) return FAIL.CUSTOM;
    this._countBalls();
    return this._cntEnemyHeld + this._cntFree > 0 ? null : FAIL.NO_TARGET;
  }

  onCast() {
    this.lastVanished = 0;
    this.lastSummoned = 0;
    const balls = this._ballList();
    // Snapshot first: dropping an enemy's ball momentarily makes it free, and it must not be summoned.
    this._enemyHeld.length = 0;
    this._free.length = 0;
    for (const b of balls) {
      if (!b || b.isAbilityBall) continue;
      if (b.state === 'held' && b.holder && this.isEnemy(b.holder)) this._enemyHeld.push(b);
      else if (b.state === 'free') this._free.push(b);
    }
    this._vanishEnemyBalls();
    this._summonFreeBalls();
    this._castJuice();
    this._enemyHeld.length = 0;
    this._free.length = 0;
    this._receivers.length = 0;
  }

  onRoundReset() { this._watcher.stop(); } // the ball manager re-places every match ball for the new round
  onUnequip() { this._watcher.stop(); }

  /** Scores enemy ball control: best when the enemies are loaded up, bonus when behind. */
  evaluateAI(ctx) {
    if (!game.balls || !ctx) return 0;
    this._countBalls();
    const p = this.params;
    const held = this._cntEnemyHeld, free = this._cntFree;
    let u = 0;
    if (held >= p.aiMinEnemyHeld) u = 0.6 + 0.15 * (held - p.aiMinEnemyHeld);
    else if (held > 0 && free > 0 && !ctx.holdingBall) u = 0.35;
    else if (free >= 3 && !ctx.holdingBall) u = 0.25;
    if (u > 0 && ctx.alliesInfield < ctx.enemiesInfield) u += 0.15;
    const w = this.def.aiWeight ?? 0.5;
    return Math.max(0, Math.min(1, u * (0.5 + w)));
  }

  // ---------------------------------------------------------------- internals

  _ballList() {
    const bm = game.balls;
    return (bm && (bm.matchBalls || bm.active)) || [];
  }

  /** Counts enemy-held and free match balls (allocation-free). */
  _countBalls() {
    let held = 0, free = 0;
    for (const b of this._ballList()) {
      if (!b || b.isAbilityBall) continue;
      if (b.state === 'held' && b.holder && this.isEnemy(b.holder)) held++;
      else if (b.state === 'free') free++;
    }
    this._cntEnemyHeld = held;
    this._cntFree = free;
  }

  _vanishEnemyBalls() {
    const p = this.params;
    const count = this._enemyHeld.length;
    const width = game.court ? game.court.width : 9;
    for (let i = 0; i < count; i++) {
      const ball = this._enemyHeld[i];
      const holder = ball.holder;
      ballWorldPosition(ball, _v);
      // Out of their hands: cancel a charge in progress and release the ball cleanly first.
      if (holder && holder.combat) {
        if (holder.combat.isCharging && holder.combat.cancelCharge) holder.combat.cancelCharge();
        if (holder.combat.heldBall === ball && holder.combat.dropBall) holder.combat.dropBall(_zero.set(0, 0, 0));
      }
      // Fresh vector: the ball keeps its respawn point for 4 s.
      const respawn = new THREE.Vector3(spreadX(i, count, width), floorY() + p.respawnHeight, 0);
      ball.despawn(p.vanishTime, respawn);
      this._watcher.watch(ball);
      fxPlay('vanishSmoke', _v, { scale: p.smokeScale, color: HOUDINI_FX.smoke });
      sfx('vanish', _v, 0.8, 0.9);
      this.lastVanished++;
    }
  }

  _summonFreeBalls() {
    const p = this.params;
    if (this._free.length === 0) return;
    const receivers = this._receivers;
    receivers.length = 0;
    for (const pl of game.players) {
      if (pl.team !== this.owner.team || pl.zone !== ZONE.INFIELD || !pl.isTargetable) continue;
      if (pl === this.owner && !p.includeHoudini) continue;
      receivers.push(pl);
    }
    if (receivers.length === 0) return;
    // Empty-handed teammates first, then Houdini, then players who already hold a ball.
    const rank = (pl) => (pl.combat && pl.combat.hasBall ? 2 : 0) + (pl === this.owner ? 1 : 0);
    receivers.sort((a, b) => rank(a) - rank(b));
    const spacing = p.ringSpacingDeg * DEG;
    for (let i = 0; i < this._free.length; i++) {
      const ball = this._free[i];
      roundRobin(i, receivers.length, this._rr);
      const r = receivers[this._rr.receiver];
      ballWorldPosition(ball, _v);
      fxPlay('vanishSmoke', _v, { scale: p.smokeScale * 0.8, color: HOUDINI_FX.smoke });
      // Fresh vectors: Ball.teleport may keep references (one-shot cast, not a hot path).
      const to = new THREE.Vector3();
      ringPoint(r.position.x, r.position.z, (r.yaw || 0) + fanAngle(this._rr.slot, spacing), p.ringRadius, to);
      to.y = floorY() + (ball.radius || 0.105) + p.dropHeight;
      clampToZone(r.team, ZONE.INFIELD, to);
      ball.teleport(to, new THREE.Vector3());
      fxPlay('teleport', to, { scale: p.smokeScale * 0.7, color: HOUDINI_FX.smoke });
      sfx('teleport', to, 0.6, 1.05);
      this.lastSummoned++;
    }
  }

  _castJuice() {
    const p = this.params;
    const o = this.owner;
    const at = o.chestPosition || o.position;
    fxPlay('vanishSmoke', at, { scale: p.smokeScale * 1.6, color: HOUDINI_FX.smoke });
    sfx('vanish', at, 1, 1);
    juice('hitstop', p.castHitstop);
    juice('addTrauma', p.castTrauma);
    screenPulse('ultimate', p.castPulse, 0.5);
  }
}

registerAbility('houdini.hat_trick', HoudiniHatTrick);
registerAbility('houdini.swap_places', HoudiniSwapPlaces);
registerAbility('houdini.grand_vanish', HoudiniGrandVanish);
