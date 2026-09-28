// ---------------------------------------------------------------------------------------------------------------
// Elsa (Ice Control / Support) - Frost Trail (passive) / Glacier Freeze (skill, CD 13) / Absolute Zero (ultimate).
// Numbers come from roster.js `params` (merged over each class's `static defaults`).
//
// Time: every gameplay timer (patch life, haste refresh, freeze, the 6 s frozen court) runs on SCALED time, so hitstop
// and pause freeze them; purely visual glitter is view-dependent (no clock). Optional systems (vfx, audio, renderer,
// juice) are always guarded and a failure there never breaks the gameplay effect.
// World objects live in ../shared/elsaFrost.js (three.js) and ../shared/elsaFrostMath.js (pure, unit tested).
// ---------------------------------------------------------------------------------------------------------------
import * as THREE from 'three';
import { game, ORDER } from '../../game.js';
import { ZONE, TEAM, opponent } from '../../core/constants.js';
import { AbilityBase, registerAbility, FAIL, INTERRUPT } from '../abilityBase.js';
import { throwAbilityBall } from '../abilityUtil.js';
import { IceTrailField, FrostSheen } from '../shared/elsaFrost.js';
import { TrailStepper, fieldEnvelope } from '../shared/elsaFrostMath.js';

/** Colours of Elsa's effects (cold, desaturated - realistic ice, not neon). */
export const ELSA_FX = Object.freeze({ ice: 0xcdeeff, mist: 0xdcefff, burst: 0xe8f7ff });

const DEG = Math.PI / 180;
const _v = new THREE.Vector3();
const _v2 = new THREE.Vector3();
const _dir = new THREE.Vector3();

// ------------------------------------------------------------------ guarded optional-system helpers

const _warned = new Set();
function warnOnce(key, e) {
  if (_warned.has(key)) return;
  _warned.add(key);
  console.warn(`[elsa] optional ${key} call failed`, e);
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
function setSustained(type, amount) {
  const r = game.renderer;
  if (!r || !r.setSustained) return;
  try { r.setSustained(type, amount); } catch (e) { warnOnce('renderer.setSustained', e); }
}
const floorY = () => (game.court ? game.court.floorY : 0);

// ------------------------------------------------------------------ targeting

/** A live, visible enemy of `self` in the infield within `maxDist`. Cloaked enemies are untargetable unless revealed. */
function isValidEnemyTarget(self, p, maxDist) {
  if (!p || !game.areEnemies(self, p) || !p.isTargetable) return false;
  if (p.status && p.status.has('cloaked') && !p.status.has('revealed')) return false;
  return p.position.distanceToSquared(self.position) <= maxDist * maxDist;
}

/**
 * Throw target: the soft-locked combat target, else the best enemy inside the aim cone (angular error first, distance
 * breaks ties), else (bots only) the nearest valid enemy. Allocation-free.
 */
function resolveAimTarget(self, maxDist, coneDeg, nearestFallback) {
  const cur = self.combat ? self.combat.currentTarget : null;
  if (isValidEnemyTarget(self, cur, maxDist)) return cur;
  const aim = self.intent && self.intent.aimDir && self.intent.aimDir.lengthSq() > 1e-6 ? self.intent.aimDir : self.forward;
  _dir.set(aim ? aim.x : 0, 0, aim ? aim.z : 1);
  if (_dir.lengthSq() < 1e-8) _dir.set(0, 0, 1);
  _dir.normalize();
  const cosMax = Math.cos(coneDeg * DEG);
  let best = null, bestScore = Infinity, nearest = null, nearestD = Infinity;
  for (const p of game.players) {
    if (!isValidEnemyTarget(self, p, maxDist)) continue;
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
// Passive - Frost Trail
// =================================================================================================================

/**
 * Elsa passive [Frost Trail]: every ball Elsa throws leaves a trail of ice on the floor under its flight path; allies
 * standing on the ice move +20% faster.
 *
 * Implementation: a throw modifier on Elsa's Combat. `modify()` never changes the throw; `committed(params, ball)`
 * starts an emitter that walks the live ball's ground track (the trajectory projected onto the floor) with a
 * TrailStepper and lays a patch every `spacing` metres (0.9 m) - frame-rate and speed independent. Each patch is a
 * 0.9 m wide frosted-ice sheet (IceTrailField, one instanced draw call) living `segmentLife` s (3 s): it flash-forms,
 * holds, then melts. The emitter stops the moment the ball stops being live (caught, lands, hits, despawns) or is
 * re-thrown by anybody else. Allies (Elsa included) whose feet are on the ice get Status `haste` 0.2 for
 * `hasteRefresh` s, re-applied while they stay on it (one shared source, so overlapping patches never stack).
 * Passes and ability balls (Glacier Freeze) are thrown balls too, so they lay ice as well.
 */
export class ElsaFrostTrail extends AbilityBase {
  static defaults = {
    haste: 0.2,               // +20% movement speed
    segmentLife: 3,           // s each patch lasts (including the melt)
    spacing: 0.9,             // m between patches along the ground track
    width: 0.9,               // m across the direction of travel
    lengthFactor: 1.45,       // patch length = spacing * factor (overlap -> one continuous sheet of ice)
    hasteRefresh: 0.35,       // s of haste granted per refresh (lingers briefly after stepping off)
    hasteReapply: 0.12,       // s between refreshes for a player standing on the ice
    footMargin: 0.15,         // m of extra reach around a patch (foot size)
    includeCaster: true,      // Elsa benefits from her own ice
    includePasses: true,
    maxSegmentsPerThrow: 48,  // performance guard per throw
    maxSegmentsPerFrame: 8,
    maxEmitters: 6,           // simultaneous balls leaving ice
    maxBallHeight: 8,         // m: no ice under a sky-high lob
    capacity: 120,            // pooled patches
    courtMargin: 1.5,         // m beyond the sidelines where ice may still form (run-off)
    modifierOrder: 900,       // run after speed/radius modifiers (it only observes)
    vfxScale: 0.55,
  };

  onInitialize() {
    const p = this.params;
    this.field = new IceTrailField({ capacity: p.capacity, width: p.width, length: p.spacing * p.lengthFactor });
    this._emitters = [];
    for (let i = 0; i < p.maxEmitters; i++) {
      this._emitters.push({ active: false, ball: null, launchTime: 0, startedAt: 0, vfx: null, stepper: new TrailStepper(p.spacing, p.maxSegmentsPerThrow) });
    }
    this._hasteNext = new Map();   // player -> scaled time of the next allowed haste refresh
    this._registeredOn = null;
    this._curHeight = 0;
    this.patchesLaid = 0;          // stats / tests
    this._modifier = {
      order: p.modifierOrder,
      modify: () => {},            // Frost Trail never alters the throw itself
      committed: (params, ball) => this._onThrowCommitted(params, ball),
    };
    this._emitFn = (x, z, yaw) => this._layPatch(x, z, yaw);
  }

  onEquip() { this._register(); }

  onUnequip() {
    this._unregister();
    this._endAllEmitters();
    this._releaseHaste();
    this.field.dispose();
  }

  onRoundReset() {
    this._endAllEmitters();
    this.field.clear();
    this._releaseHaste();
  }

  /** Passive: never cast. */
  onCast() {}

  onTick(dt) {
    this._register(); // Combat may be created after equip or replaced on respawn
    this._tickEmitters();
    this.field.update(dt);
    this._applyHaste();
  }

  /** Passives are never activated by the AI. */
  evaluateAI() { return 0; }

  // ---------------------------------------------------------------- throw modifier

  _register() {
    const combat = this.owner.combat;
    if (!combat || combat === this._registeredOn || !combat.addThrowModifier) return;
    this._unregister();
    combat.addThrowModifier(this._modifier);
    this._registeredOn = combat;
  }

  _unregister() {
    const c = this._registeredOn;
    if (c && c.removeThrowModifier) c.removeThrowModifier(this._modifier);
    this._registeredOn = null;
  }

  _onThrowCommitted(params, ball) {
    if (!ball || !this._equipped) return;
    if (params && params.isPass && !this.params.includePasses) return;
    if (params && params.thrower && params.thrower !== this.owner) return;
    // Free slot, else recycle the oldest emitter.
    let e = null;
    for (const em of this._emitters) if (!em.active) { e = em; break; }
    if (!e) {
      for (const em of this._emitters) if (!e || em.startedAt < e.startedAt) e = em;
      this._endEmitter(e);
    }
    e.active = true;
    e.ball = ball;
    e.launchTime = ball.launchTime;
    e.startedAt = game.time.now;
    e.stepper.reset(ball.position.x, ball.position.z, this.params.spacing * 0.5);
    // Frost vapour + glitter streaming off the ball while it lays ice.
    e.vfx = fxAttach('iceTrail', ball.root, { duration: this.params.segmentLife, color: ELSA_FX.ice });
  }

  _tickEmitters() {
    const fy = floorY();
    for (const e of this._emitters) {
      if (!e.active) continue;
      const b = e.ball;
      const stillOurs = b && b.state === 'live' && b.launchTime === e.launchTime && (!b.lastThrower || b.lastThrower === this.owner);
      if (!stillOurs || e.stepper.exhausted) { this._endEmitter(e); continue; }
      this._curHeight = b.position.y - fy;
      e.stepper.advance(b.position.x, b.position.z, this._emitFn, this.params.maxSegmentsPerFrame);
    }
  }

  _endEmitter(e) {
    if (!e) return;
    fxStop(e.vfx);
    e.vfx = null;
    e.active = false;
    e.ball = null;
  }

  _endAllEmitters() { for (const e of this._emitters) if (e.active) this._endEmitter(e); }

  /** Lays one patch under the ball (called by the TrailStepper at every spacing boundary). */
  _layPatch(x, z, yaw) {
    const p = this.params;
    if (this._curHeight > p.maxBallHeight) return;
    const c = game.court;
    if (c && (Math.abs(x) > c.halfW + p.courtMargin || Math.abs(z) > c.halfL + c.outfieldDepth)) return;
    this.field.spawn(x, z, yaw, p.segmentLife);
    this.patchesLaid++;
    _v2.set(x, floorY() + 0.03, z);
    _dir.set(Math.sin(yaw), 0, Math.cos(yaw));
    fxPlay('iceTrail', _v2, { scale: p.vfxScale, color: ELSA_FX.ice, direction: _dir });
  }

  // ---------------------------------------------------------------- haste

  _applyHaste() {
    if (this.field.activeCount === 0) return;
    const p = this.params;
    const now = game.time.now;
    const fy = floorY();
    for (const pl of game.players) {
      if (pl === this.owner ? !p.includeCaster : !this.isAlly(pl)) continue;
      if (!pl.status || !pl.position) continue;
      if (pl.position.y - fy > 0.25) continue; // airborne: not standing on the ice
      if (!this.field.containsPoint(pl.position.x, pl.position.z, p.footMargin)) continue;
      const next = this._hasteNext.get(pl);
      if (next !== undefined && now < next) continue;
      pl.status.apply('haste', p.hasteRefresh, p.haste, this);
      this._hasteNext.set(pl, now + p.hasteReapply);
    }
  }

  _releaseHaste() {
    for (const pl of this._hasteNext.keys()) if (pl.status) pl.status.remove('haste', this);
    this._hasteNext.clear();
  }
}

// =================================================================================================================
// Skill - Glacier Freeze
// =================================================================================================================

/** Hit outcomes after which the victim must NOT be frozen (hit did not land, or they are already out / rewound). */
const NO_FREEZE_OUTCOMES = new Set(['negated', 'ignored', 'eliminated', 'prevented']);

/**
 * Payload of the Glacier Freeze ball.
 *  - onHitPlayer: the freezing hit itself deals no damage (hit.damage = 0) and almost no knockback.
 *  - onAfterHitPlayer: unless the hit was negated (shield / invulnerability / dodge) - or the victim is already out -
 *    applies Status `frozen` for freezeTime (2.5 s). Status turns that into: no moving, no catching,
 *    Incapacitated('frozen'), ice-encased avatar. Allies cannot thaw it. Health's rule "frozen + any ball hit =>
 *    forceEliminate" makes the second hit eliminate - including a second Glacier Freeze.
 *  - Presentation: cold vapour streams off the ball, an ice burst shatters at every contact, freeze SFX, and a screen
 *    frost pulse when the local player is the victim.
 */
export class GlacierFreezePayload {
  /** @param {ElsaGlacierFreeze} ability */
  constructor(ability) {
    this.ability = ability;
    this.owner = ability.owner;
    this.params = ability.params;
    this._mist = null;
    this.frozen = null; // victim frozen by this ball (stats / tests)
  }

  onLaunched(ball) {
    this._mist = fxAttach('frozenMist', ball.root, { duration: 4, color: ELSA_FX.mist, scale: 0.5 });
  }

  onHitPlayer(ball, hit) {
    const victim = hit && hit.victim;
    if (!victim || !game.areEnemies(this.owner, victim)) return true; // default behaviour for anything else
    hit.damage = 0;
    const kb = this.params.knockbackScale;
    if (typeof hit.knockback === 'number') hit.knockback *= kb;
    else if (hit.knockback && hit.knockback.isVector3) hit.knockback.multiplyScalar(kb);
    return true; // keep the hit (it must reach Health so filters/shields can negate it)
  }

  onAfterHitPlayer(ball, hit, outcome) {
    const victim = hit && hit.victim;
    if (!victim) return;
    const at = hit.point && hit.point.isVector3 ? hit.point : victim.chestPosition || victim.position;
    fxPlay('iceBurst', at, { scale: this.params.burstScale, color: ELSA_FX.burst, direction: hit.normal });
    sfx('iceCrack', at, 1, 1);
    if (!game.areEnemies(this.owner, victim) || NO_FREEZE_OUTCOMES.has(outcome)) return;
    const h = victim.health;
    if (h && (h.isAlive === false || h.isEliminated)) return;
    if (!victim.status) return;
    victim.status.apply('frozen', this.params.freezeTime, 1, this.ability);
    this.frozen = victim;
    this.ability.freezes++;
    sfx('freeze', at, 1, 1);
    if (victim.isLocal) screenPulse('freeze', 0.8, 0.5);
  }

  onHitSurface(ball, point) {
    if (!point) return;
    fxPlay('iceBurst', point, { scale: this.params.burstScale * 0.55, color: ELSA_FX.burst });
    sfx('iceCrack', point, 0.55, 1.15);
  }

  onCaught(ball, catcher) {
    // A clean catch shrugs the frost off: a small puff in the hands, no freeze.
    if (catcher) fxPlay('iceBurst', catcher.chestPosition || catcher.position, { scale: 0.4, color: ELSA_FX.burst });
  }

  onEnded() {
    fxStop(this._mist);
    this._mist = null;
  }
}

/**
 * Elsa skill [Glacier Freeze] (CD 13 s): conjures and hurls a supercooled ball (style 'freeze', 1.15x a full-charge
 * throw) through the normal throw pipeline (throwAbilityBall: soft-lock / lead targeting, throw modifiers - so Frost
 * Trail lays ice under it - and the BallThrown event). Its GlacierFreezePayload turns a landed hit into a 2.5 s freeze.
 * Target: soft-lock, else the best enemy in the aim cone, else (bots) the nearest enemy. If no ball could be conjured
 * the cooldown is refunded.
 */
export class ElsaGlacierFreeze extends AbilityBase {
  static defaults = {
    freezeTime: 2.5,
    speedMul: 1.15,
    gravityScale: 0.45,     // flatter than a normal throw (supercooled "laser")
    aimRange: 30,           // m
    aimCone: 22,            // deg half-angle
    knockbackScale: 0.1,    // the victim is frozen in place, not shoved
    burstScale: 1.1,
    aiRange: 18,            // bots cast within this distance
  };

  onInitialize() {
    this._refund = false;
    this.thrown = 0;
    this.freezes = 0;
    this.lastBall = null;
  }

  onCast() {
    const o = this.owner;
    const p = this.params;
    const target = resolveAimTarget(o, p.aimRange, p.aimCone, !o.isHuman);
    const ball = throwAbilityBall(o, {
      style: 'freeze', speedMul: p.speedMul, gravityScale: p.gravityScale, target, payload: new GlacierFreezePayload(this),
    });
    if (!ball) { this._refund = true; return; }
    this.thrown++;
    this.lastBall = ball;
    const origin = o.combat && o.combat.getThrowOrigin ? _v.copy(o.combat.getThrowOrigin()) : _v.copy(o.position).setY(1.4);
    fxPlay('iceBurst', origin, { scale: 0.45, color: ELSA_FX.burst });
    sfx('frostCast', origin, 0.9, 1);
    if (ball.velocity && o.avatar && o.avatar.playThrow) {
      _dir.copy(ball.velocity).setY(0);
      if (_dir.lengthSq() > 1e-6) o.avatar.playThrow(_dir.normalize());
    }
  }

  onCooldown() {
    if (!this._refund) return;
    this._refund = false;
    this.resetCooldown(); // nothing was thrown: do not charge the 13 s cooldown
  }

  onRoundReset() { this._refund = false; this.lastBall = null; }

  /** Best on a nearby enemy who is not already frozen; better still when Elsa holds a ball for the follow-up hit. */
  evaluateAI(ctx) {
    const e = ctx && ctx.nearestEnemy;
    if (!e || !(ctx.nearestEnemyDistance <= this.params.aiRange)) return 0;
    if (e.status && e.status.has('frozen')) return 0.05;
    let u = 0.55;
    if (ctx.holdingBall) u += 0.2;              // freeze now, eliminate with the held ball next
    if (ctx.nearestEnemyDistance < 10) u += 0.1;
    if (ctx.incomingBall && ctx.incomingTime < 0.5) u -= 0.3; // dodge/catch first
    const w = this.def.aiWeight ?? 0.5;
    return Math.max(0, Math.min(1, u * (0.5 + w)));
  }
}

// =================================================================================================================
// Ultimate - Absolute Zero
// =================================================================================================================

/**
 * Runtime of one Absolute Zero cast, ticked as a temporary game system (ORDER.ABILITIES) so the frozen court keeps
 * running for its whole window even if Elsa is stunned, frozen or eliminated mid-ultimate (the ability's own phase ends
 * by kernel rules; the court stays committed). Only a round reset, an unequip or a new cast stops it early.
 * Gameplay: every enemy INFIELD player gets `slippery` 0.85 (motor traction loss: heavy inertia, long skids) and
 * `dodgeDisabled` (no jumps / slides), including enemies who enter the infield during the window (revived); anyone who
 * leaves it (eliminated -> outfield) is released at once so the outfield and the ragdoll are never slippery.
 * Statuses use the ability as their source and carry the remaining window as duration, so they can never outlive it.
 */
class AbsoluteZeroField {
  /** @param {ElsaAbsoluteZero} ability */
  constructor(ability) {
    this.ability = ability;
    this.sheen = new FrostSheen({ mistCount: 6 });
    this.affected = new Set();
    this.running = false;
    this.elapsed = 0;
    this.duration = 6;
    this.enemyTeam = TEAM.NONE;
    this._env = { spread: 0, alpha: 0, done: false };
    this._registered = false;
    this._localAffected = false;
    this._local = 0;
    this._frostBeat = 0;
  }

  start(enemyTeam, duration) {
    const p = this.ability.params;
    this._releaseAll();
    this.enemyTeam = enemyTeam;
    this.duration = duration;
    this.elapsed = 0;
    this.running = true;
    const c = game.court;
    const side = c ? c.sideSign(enemyTeam) : (enemyTeam === TEAM.HOME ? -1 : 1);
    this.sheen.place(side, (c ? c.width : 9) + 0.1, (c ? c.halfL : 9) + 0.05, floorY());
    this.sheen.startMist(duration + p.fadeTime, ELSA_FX.mist, p.mistScale);
    if (!this._registered) { game.addSystem(this, ORDER.ABILITIES); this._registered = true; }
    this._tickGameplay(); // the court freezes on the cast frame
  }

  /** System hook: scaled dt (hitstop / pause freeze the window). */
  update(dt) {
    if (!this.running) return;
    const p = this.ability.params;
    this.elapsed += dt;
    if (this.elapsed < this.duration) this._tickGameplay();
    else if (this.affected.size) { this._releaseAll(); this._localAffected = false; }
    fieldEnvelope(this.elapsed, this.duration, p.spreadTime, p.fadeTime, this._env);
    this.sheen.setEnvelope(this._env.spread, this._env.alpha * p.sheenOpacity);
    this.sheen.tickMist(dt);
    this._updateLocalFrost(p, dt);
    if (this._env.done) this.stop();
  }

  _isVictim(pl) {
    if (pl.team !== this.enemyTeam || pl.zone !== ZONE.INFIELD || !pl.status) return false;
    const h = pl.health;
    return !(h && (h.isAlive === false || h.isEliminated));
  }

  _tickGameplay() {
    const p = this.ability.params;
    const remaining = Math.max(0.05, this.duration - this.elapsed) + p.statusPad;
    this._localAffected = false;
    for (const pl of game.players) {
      const victim = this._isVictim(pl);
      if (victim && !this.affected.has(pl)) {
        pl.status.apply('slippery', remaining, p.slippery, this.ability);
        pl.status.apply('dodgeDisabled', remaining, 1, this.ability);
        this.affected.add(pl);
        if (this.elapsed > 0) fxPlay('frozenMist', pl.position, { scale: 0.8, color: ELSA_FX.mist }); // late arrival
      } else if (!victim && this.affected.has(pl)) {
        this._release(pl);
      }
      if (pl.isLocal && victim) this._localAffected = true;
    }
    // Players removed from the match mid-window.
    for (const pl of this.affected) if (!game.players.includes(pl)) this._release(pl);
  }

  _release(pl) {
    if (pl.status) {
      pl.status.remove('slippery', this.ability);
      pl.status.remove('dodgeDisabled', this.ability);
    }
    this.affected.delete(pl);
  }

  _releaseAll() { for (const pl of this.affected) this._release(pl); }

  /**
   * Sustained frost grade on the local player's screen while they skate on Elsa's ice. Status also drives the same
   * 'freeze' grade for a frozen local player (Glacier Freeze), so the value is re-asserted at 4 Hz while affected and
   * never zeroed while the local player is frozen (Status clears it when their freeze ends).
   */
  _updateLocalFrost(p, dt) {
    const target = this._localAffected ? p.localFrost * this._env.alpha : 0;
    this._frostBeat -= dt;
    if (target > 0) {
      if (Math.abs(target - this._local) > 0.02 || this._frostBeat <= 0) {
        this._local = target;
        this._frostBeat = 0.25;
        setSustained('freeze', target);
      }
    } else if (this._local !== 0) {
      this._clearLocalFrost();
    }
  }

  _clearLocalFrost() {
    this._local = 0;
    const lp = game.localPlayer;
    if (!(lp && lp.status && lp.status.has('frozen'))) setSustained('freeze', 0);
  }

  stop() {
    this._releaseAll();
    this._localAffected = false;
    this.sheen.hide();
    if (this._local !== 0) this._clearLocalFrost();
    if (this._registered) { game.removeSystem(this); this._registered = false; }
    this.running = false;
  }

  dispose() {
    this.stop();
    this.sheen.dispose();
  }
}

/**
 * Elsa ultimate [Absolute Zero] (6 s): flash-freezes the entire enemy court - heavy sliding inertia (slippery 0.85)
 * and no dodges for every enemy in their infield, a creeping crystalline frost glaze over their half with rolling cold
 * mist, and a sustained frost screen grade for the local player if they are one of the victims. Refuses to fire
 * (meter kept) when no enemy stands in their infield. See AbsoluteZeroField for the runtime rules.
 */
export class ElsaAbsoluteZero extends AbilityBase {
  static defaults = {
    slippery: 0.85,       // traction loss (0..1)
    statusPad: 0.1,       // s added to status durations (released explicitly at the end anyway)
    spreadTime: 0.45,     // s for the frost to creep from the centre line to the baseline
    fadeTime: 0.7,        // s thaw after the window
    sheenOpacity: 0.72,   // peak opacity of the glaze
    localFrost: 0.55,     // sustained 'freeze' grade for an affected local player
    mistScale: 1.6,
    castPulse: 0.45,
    castTrauma: 0.25,
  };

  onInitialize() {
    this._field = null;
    this.casts = 0;
  }

  canActivateCustom() {
    return this._enemiesInfield() > 0 ? null : FAIL.NO_TARGET;
  }

  onCast() {
    const o = this.owner;
    const p = this.params;
    if (!this._field) this._field = new AbsoluteZeroField(this);
    const enemyTeam = opponent(o.team);
    this._field.start(enemyTeam, this.duration > 0 ? this.duration : 6);
    this.casts++;
    // Cast presentation: frost bursts from Elsa's hands and cracks across the centre line into the enemy half.
    fxPlay('iceBurst', o.chestPosition || o.position, { scale: 1.4, color: ELSA_FX.burst });
    const c = game.court;
    const side = c ? c.sideSign(enemyTeam) : (enemyTeam === TEAM.HOME ? -1 : 1);
    const halfW = c ? c.halfW : 4.5, halfL = c ? c.halfL : 9;
    for (let i = -1; i <= 1; i++) {
      _v.set(i * halfW * 0.6, floorY() + 0.05, side * 0.4);
      fxPlay('iceBurst', _v, { scale: 1.2, color: ELSA_FX.burst });
    }
    _v.set(0, floorY() + 0.1, side * halfL * 0.5);
    fxPlay('frozenMist', _v, { scale: 3, color: ELSA_FX.mist });
    sfx('absoluteZero', _v, 1, 1);
    const local = game.localPlayer;
    screenPulse('freeze', local && local.team === enemyTeam ? 0.85 : p.castPulse, 0.6);
    const j = game.juice;
    if (j && j.addTrauma) { try { j.addTrauma(p.castTrauma); } catch (e) { warnOnce('juice.addTrauma', e); } }
  }

  /** Stun / freeze / elimination end the ability phase but the frozen court is committed; round end / unequip stop it. */
  onInterrupt(reason) {
    if (reason === INTERRUPT.ROUND_ENDED || reason === INTERRUPT.CANCELLED) this._field?.stop();
  }

  onRoundReset() { this._field?.stop(); }

  onUnequip() {
    if (this._field) this._field.dispose();
    this._field = null;
  }

  /** True while the enemy court is frozen (the field outlives the ability phase if Elsa is interrupted). */
  get fieldActive() { return !!(this._field && this._field.running); }

  /** Worth more the more enemies stand on the court, and when Elsa can capitalise with a ball in hand. */
  evaluateAI(ctx) {
    const n = ctx ? ctx.enemiesInfield | 0 : 0;
    if (n <= 0) return 0;
    let u = 0.35 + 0.15 * Math.min(3, n);
    if (ctx.holdingBall) u += 0.15;
    if (ctx.alliesInfield < ctx.enemiesInfield) u += 0.1;
    const w = this.def.aiWeight ?? 0.5;
    return Math.max(0, Math.min(1, u * (0.5 + w)));
  }

  _enemiesInfield() {
    let n = 0;
    for (const p of game.players) if (game.areEnemies(this.owner, p) && p.zone === ZONE.INFIELD && p.isTargetable) n++;
    return n;
  }
}

registerAbility('elsa.frost_trail', ElsaFrostTrail);
registerAbility('elsa.glacier_freeze', ElsaGlacierFreeze);
registerAbility('elsa.absolute_zero', ElsaAbsoluteZero);
