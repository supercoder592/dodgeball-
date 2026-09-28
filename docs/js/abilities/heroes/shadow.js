// ---------------------------------------------------------------------------------------------------------------
// Shadow - "Clones" (Attacker). Web port of the Unity reference abilities (Assets/.../Abilities/Heroes/Shadow/*.cs).
//
//   passive   shadow.decoy_dash        ShadowDecoyDash         sprinting leaves a 1 s fading, ball-absorbing illusion
//   skill     shadow.night_parade      ShadowNightParade       2 flanking clones mirroring throws/catches (6 s, CD 12 s)
//   ultimate  shadow.mirage_formation  ShadowMirageFormation   2 clones per living infield teammate + shell game (5 s)
//
// The illusions themselves (hittable capsules, animated duplicates, harmless mirrored balls, AI registry) live in
// ../shared/shadowClones.js; the pure formation math in ../shared/shadowMath.js (unit tested).
// All gameplay runs on scaled time (hook dt); positions are always clamped into the mimicked player's own zone.
// ---------------------------------------------------------------------------------------------------------------
import * as THREE from 'three';
import { game } from '../../game.js';
import { EV } from '../../core/events.js';
import { ZONE } from '../../core/constants.js';
import { AbilityBase, INTERRUPT, registerAbility } from '../abilityBase.js';
import {
  ShadowClone, CLONE_KIND, SHADOW_CLONE_TUNING, dissolveClonesOf,
} from '../shared/shadowClones.js';
import {
  flankOffset, clampToBounds, smoothstep01, smoothstep01Derivative, isSlotInsideBounds, chooseInitialSlot,
} from '../shared/shadowMath.js';

// Registry + AI helpers re-exported for convenience (bots: import from here or from the shared module).
export {
  activeClones, countClonesOf, clonesOf, pickPerceivedAimPoint, findNearestClone, isShadowClone,
} from '../shared/shadowClones.js';

// Module-scope temporaries (no per-frame allocations).
const _right = new THREE.Vector3();
const _slot = new THREE.Vector3();
const _spawn = new THREE.Vector3();
const _anchor = new THREE.Vector3();
const _lat = new THREE.Vector3();
const _fwd = new THREE.Vector3();
const _pos = new THREE.Vector3();
const _planar = new THREE.Vector3();

const clamp01 = (x) => (x < 0 ? 0 : x > 1 ? 1 : x);
const lerp = (a, b, t) => a + (b - a) * t;
const rand01 = () => (game.rng ? game.rng.next() : Math.random());

/** Cached zone confinement bounds per (court, team, zone): Court.confinement allocates, formations query it per frame. */
const _boundsCache = { court: null, entries: new Map() };
function zoneBounds(player) {
  const court = game.court;
  if (!court || !player) return null;
  if (_boundsCache.court !== court) { _boundsCache.court = court; _boundsCache.entries.clear(); }
  const key = player.team * 2 + (player.zone === ZONE.OUTFIELD ? 1 : 0); // numeric key: no string allocation
  let b = _boundsCache.entries.get(key);
  if (!b) { b = court.confinement(player.team, player.zone); _boundsCache.entries.set(key, b); }
  return b;
}

/** The player's planar right vector from its yaw (yaw 0 faces +Z, so right is -X). */
function planarRight(player, out) {
  const y = player.yaw || 0;
  return out.set(-Math.cos(y), 0, Math.sin(y));
}

/** True when `p` is an infield, targetable, living player. */
function isEligible(p) {
  return !!p && p.zone === ZONE.INFIELD && p.isTargetable !== false && !(p.health && p.health.isAlive === false);
}

/** Enemies of `owner` currently holding a ball (armed threats). */
function countEnemiesHoldingBalls(owner) {
  let n = 0;
  for (const p of game.players) if (p && game.areEnemies(owner, p) && p.combat && p.combat.hasBall) n++;
  return n;
}

/** Gravity scale of a thrown ball for the mirrored illusion balls. */
function gravityScaleOf(e, thrower) {
  const g = e.ball && typeof e.ball.gravityScale === 'number' ? e.ball.gravityScale : null;
  if (g !== null) return g;
  return (thrower && thrower.combat && thrower.combat.profile && thrower.combat.profile.thrownGravityScale) ?? 0.65;
}

// ===============================================================================================================
// Passive - Decoy Dash
// ===============================================================================================================

/**
 * Shadow's passive [Decoy Dash]: "sprinting leaves a 1 s fading illusion clone".
 * While Shadow is Sprinting (and actually moving at speed) an illusion peels off every `interval` seconds:
 * avatar.spawnAfterimage bakes the current mocap pose into a translucent, fading afterimage and ShadowClone.spawnDecoy
 * gives it a short-lived hittable body, so for its 1 s life it can swallow an enemy ball (pops with cloneDissolve) and
 * fool bots (activeClones). The decoy only arms once Shadow has run clear of it, so it never shields the real body.
 */
export class ShadowDecoyDash extends AbilityBase {
  static defaults = {
    interval: 0.9,          // s between two illusions while sprinting
    lifetime: 1,            // s: the afterimage fades over this time and the decoy body lives as long (spec: 1 s)
    firstSpawnDelay: 0.15,  // s after the sprint starts before the first illusion peels off
    minimumSpeed: 3.5,      // m/s: a standing sprint input leaves nothing
    armDistance: 0.8,       // m: the decoy becomes hittable once Shadow is this far from it
    afterimageColor: SHADOW_CLONE_TUNING.afterimageColor,
    spawnVolume: 0.25,      // soft whoosh when an illusion peels off (0 = silent)
  };

  onInitialize() { this._timer = 0; this._wasSprinting = false; }

  /** Passives never cast. */
  onCast() {}

  onTick(dt) {
    if (!this._isSprintingFast()) { this._wasSprinting = false; return; }
    if (!this._wasSprinting) {
      // Sprint just started: the first illusion peels off almost immediately.
      this._wasSprinting = true;
      this._timer = this.params.firstSpawnDelay;
    }
    this._timer -= dt;
    if (this._timer > 0) return;
    this._timer = this.params.interval;
    this._spawnIllusion();
  }

  onRoundReset() { this._wasSprinting = false; dissolveClonesOf(this.owner, CLONE_KIND.AFTERIMAGE, false); }
  onUnequip() { dissolveClonesOf(this.owner, CLONE_KIND.AFTERIMAGE, false); }

  /** Passives are never activated by the AI. */
  evaluateAI() { return 0; }

  _isSprintingFast() {
    const o = this.owner;
    if (!o || o.zone !== ZONE.INFIELD) return false;
    if (game.match && !game.match.isPlaying) return false;
    const fsm = o.fsm;
    const sprinting = fsm ? (typeof fsm.is === 'function' ? fsm.is('sprinting') : fsm.current === 'sprinting') : !!(o.intent && o.intent.sprint);
    if (!sprinting) return false;
    return !o.motor || (o.motor.planarSpeed ?? 0) >= this.params.minimumSpeed;
  }

  _spawnIllusion() {
    const P = this.params;
    const decoy = ShadowClone.spawnDecoy(this.owner, P.lifetime, P.armDistance, P.afterimageColor);
    if (decoy && P.spawnVolume > 0) game.audio?.play?.('clone', this.owner.position, P.spawnVolume, 1.2 + 0.15 * rand01());
  }
}

// ===============================================================================================================
// Skill - Night Parade
// ===============================================================================================================

/**
 * Shadow's skill [Night Parade]: "spawns 2 active clones performing identical throw/catch animations; clones vanish on
 * ball impact (CD 12 s)".
 * Two animated clones flank Shadow at +-`spacing` m along his right vector. The formation follows with a slight lag
 * (exponential easing), always clamped to Shadow's zone, at Shadow's height (mirrors jumps). The duplicates mirror his
 * animation; when Shadow throws, every clone throws a harmless illusion ball alongside the real one (vanishes after
 * 0.6 s) - catchers have to guess which ball is real. Enemy balls burst a clone (absorb + cloneDissolve).
 * Duration (6 s) and cooldown (12 s) come from the roster; the ability ends early when every clone popped.
 */
export class ShadowNightParade extends AbilityBase {
  static defaults = {
    count: 2,                   // clones (one per flank); roster param
    spacing: 1.4,               // m lateral distance between Shadow and each flanking clone; roster param
    followSharpness: 9,         // 1/s: lower = more lag
    spawnLerp: 0.35,            // clones step out of Shadow's body this far toward their slot, then ease in
    illusionBallLifetime: 0.6,  // s (spec)
    mirrorCatches: true,
    fallbackDuration: 6,        // used only when the roster duration is 0 (misconfigured)
    aiEngageRange: 16,          // m
  };

  onInitialize() {
    this._clones = [];
    this._slots = [];    // formation slot index of each clone (parallel to _clones)
    this._offsets = [];  // lateral offset (m) of each slot
  }

  /** Live clones of the current parade (read-only view for UI / AI). */
  get clones() { return this._clones; }

  onEquip() {
    this.listen(EV.BallThrown, (e) => this._onBallThrown(e));
    this.listen(EV.CatchAttempt, (e) => this._onCatchAttempt(e));
  }

  onUnequip() { this._dissolveAll(false); }

  /** SPEC HOOK: summon the flanking clones. */
  onCast() {
    const P = this.params, o = this.owner;
    this._dissolveAll(false);
    if (this.duration <= 0) this.extendActive(P.fallbackDuration);
    const count = Math.max(1, Math.min(4, Math.round(P.count)));
    planarRight(o, _right);
    const bounds = zoneBounds(o);
    for (let i = 0; i < count; i++) {
      // Alternate sides: +1, -1, +2, -2 ... flank spacings.
      this._offsets[i] = flankOffset(i, P.spacing);
      this._slotPosition(_right, this._offsets[i], bounds, _slot);
      _spawn.lerpVectors(o.position, _slot, P.spawnLerp);
      const clone = ShadowClone.spawnAnimated(o, o, _spawn, o.yaw || 0, 0);
      if (!clone) continue;
      clone.followSharpness = P.followSharpness;
      clone.setDesiredPose(_slot, o.yaw || 0);
      this._clones.push(clone);
      this._slots.push(i);
    }
    if (this._clones.length === 0) {
      this.endAbility(); // no visual to copy (headless test / missing model)
      return;
    }
    game.audio?.play?.('abilityCast', o.position, 0.8, 0.9);
  }

  /** SPEC HOOK: keep the formation on Shadow's flanks; end when every clone popped. */
  onTick() {
    this._pruneDead();
    if (this._clones.length === 0) { this.endAbility(); return; }
    const o = this.owner;
    planarRight(o, _right);
    const bounds = zoneBounds(o);
    for (let i = 0; i < this._clones.length; i++) {
      // Each clone keeps its own flank even after a sibling popped.
      this._slotPosition(_right, this._offsets[this._slots[i]], bounds, _slot);
      this._clones[i].setDesiredPose(_slot, o.yaw || 0, this.params.followSharpness);
    }
  }

  /** SPEC HOOK: round end removes the clones silently; stun/elimination dissolve them in smoke (onEnd). */
  onInterrupt(reason) { if (reason === INTERRUPT.ROUND_ENDED) this._dissolveAll(false); }

  onEnd() { this._dissolveAll(true); }

  onRoundReset() { this._dissolveAll(false); }

  /**
   * Most valuable when Shadow is about to throw at a nearby enemy (two decoy balls make the catch a guess) or when
   * enemies hold balls and Shadow needs bodies to soak throws.
   */
  evaluateAI(ctx) {
    if (!ctx || !ctx.self || !ctx.nearestEnemy) return 0;
    const w = this.def.aiWeight ?? 0.6;
    let u = 0;
    if (ctx.holdingBall && ctx.nearestEnemyDistance <= this.params.aiEngageRange) u = w * 0.8;
    const armed = countEnemiesHoldingBalls(this.owner);
    if (armed > 0) u = Math.max(u, w * (0.4 + 0.15 * armed));
    if (ctx.incomingBall && ctx.incomingTime > 0.3 && ctx.incomingTime < 1.2) u = Math.max(u, w * 0.5);
    return clamp01(u);
  }

  // ------------------------------------------------------------------ events
  _onBallThrown(e) {
    if (!this.isActive || !e || e.thrower !== this.owner || e.isAbility || e.isPass || !e.ball) return;
    const origin = e.origin || e.ball.position, vel = e.velocity || e.ball.velocity;
    const g = gravityScaleOf(e, this.owner);
    for (const c of this._clones) if (c.alive) c.mirrorThrow(origin, vel, g, this.params.illusionBallLifetime);
  }

  _onCatchAttempt(e) {
    if (!this.params.mirrorCatches || !this.isActive || !e || e.player !== this.owner) return;
    for (const c of this._clones) if (c.alive) c.mirrorCatch();
  }

  // ------------------------------------------------------------------ helpers
  /** Formation slot for a lateral offset, clamped to Shadow's zone, at Shadow's height (mirrors jumps). */
  _slotPosition(right, lateralOffset, bounds, out) {
    const o = this.owner;
    out.copy(o.position).addScaledVector(right, lateralOffset);
    clampToBounds(bounds, out);
    out.y = o.position.y;
    return out;
  }

  _pruneDead() {
    for (let i = this._clones.length - 1; i >= 0; i--) {
      if (this._clones[i].alive) continue;
      this._clones.splice(i, 1);
      this._slots.splice(i, 1);
    }
  }

  _dissolveAll(effects) {
    for (const c of this._clones) c.dissolve(effects);
    this._clones.length = 0;
    this._slots.length = 0;
  }
}

// ===============================================================================================================
// Ultimate - Mirage Formation
// ===============================================================================================================

/**
 * Shadow's ultimate [Mirage Formation]: "clones all living teammates simultaneously for 5 s, obscuring real targets".
 * Every living infield teammate (and Shadow) gets `clonesPerPlayer` animated clones and the 'obscured' status for the
 * ability's duration. Each player and their clones stand in a line of formation slots across the court (perpendicular
 * to the attack direction):
 *
 *     slot:      0        1        2
 *              [clone]  [REAL]  [clone]        (slots 1.4 m apart; the real player may occupy any slot)
 *
 * Shell game: every ~1.2 s (jittered, staggered per player) the real player swaps slots with one of their clones - the
 * real body side-steps into the clone's slot while the clone runs the other way on an arc, so their paths cross. All
 * bodies are the same realistic model with the same mirrored animation, so after the swap the real target has changed
 * places in the line. When a player cannot be moved (stunned, airborne, sliding, frozen...) two clones swap instead.
 * The side-step only overrides the LATERAL velocity for `transitDuration` s (forward/back control is never taken away)
 * and every position is clamped to the player's own zone.
 * Clones mirror their player's throws (illusion balls) and catches, and pop when an enemy ball touches them.
 */
export class ShadowMirageFormation extends AbilityBase {
  static defaults = {
    clonesPerPlayer: 2,       // roster param (1..3)
    slotSpacing: 1.4,         // m between formation slots
    followSharpness: 10,      // 1/s: clones easing toward their slot
    transitSharpness: 28,     // 1/s: tighter easing while a swap is running (clones must keep up with the curve)
    shuffleInterval: 1.2,     // s between swaps (roster param)
    shuffleJitter: 0.3,       // +-30% randomisation so the three formations never swap in sync
    transitDuration: 0.45,    // s a swap takes
    crossingArc: 0.7,         // m forward/back bulge of a swapping clone so the paths visibly cross
    moveRealPlayers: true,    // side-step the real players (else only clones shuffle)
    moveHumanPlayers: true,   // also side-step human-controlled players (lateral only, 0.45 s)
    illusionBallLifetime: 0.6,
    mirrorCatches: true,
    fallbackDuration: 5,      // used only when the roster duration is 0
  };

  onInitialize() { this._groups = []; }

  /** Number of players currently obscured by this ultimate. */
  get obscuredPlayerCount() { let n = 0; for (const g of this._groups) if (g.active) n++; return n; }

  onEquip() {
    this.listen(EV.BallThrown, (e) => this._onBallThrown(e));
    this.listen(EV.CatchAttempt, (e) => this._onCatchAttempt(e));
  }

  onUnequip() { this._endAllGroups(false); }

  onCastStarted() { game.audio?.play?.('ultimateCast', this.owner.position, 1, 0.9); }

  /** SPEC HOOK: clone every living infield teammate (including Shadow). */
  onCast() {
    const P = this.params, o = this.owner;
    this._endAllGroups(false);
    let duration = this.duration;
    if (duration <= 0) { this.extendActive(P.fallbackDuration); duration = P.fallbackDuration; }
    const clones = Math.max(1, Math.min(3, Math.round(P.clonesPerPlayer)));
    for (const p of game.players) {
      if (!p || p.team !== o.team || !isEligible(p)) continue;
      const g = this._createGroup(p, clones, duration);
      if (g) this._groups.push(g);
    }
    if (this._groups.length === 0) { this.endAbility(); return; }
    // Feedback: a wave of smoke from every obscured player (cloneSpawn per clone) and a rumble for the caster.
    game.audio?.play?.('clone', o.position, 1, 0.8);
    game.juice?.shake?.(0.25, 14, 0.3, o.position);
    const local = game.localPlayer;
    if (local && local.team === o.team) game.renderer?.pulse?.('ultimate', 0.6, 0.4);
  }

  onTick(dt) {
    let active = 0;
    for (const g of this._groups) {
      if (!g.active) continue;
      if (!isEligible(g.player) || this._countAlive(g) === 0) { this._endGroup(g, true); continue; } // out / all popped
      this._tickGroup(g, dt);
      active++;
    }
    if (active === 0) this.endAbility();
  }

  onInterrupt(reason) { if (reason === INTERRUPT.ROUND_ENDED) this._endAllGroups(false); }
  onEnd() { this._endAllGroups(true); }
  onRoundReset() { this._endAllGroups(false); }

  /** Scales with how many teammates it protects and how many enemy balls threaten the team; a comeback tool. */
  evaluateAI(ctx) {
    if (!ctx || !ctx.self) return 0;
    let protectedPlayers = 0, armedEnemies = 0;
    for (const p of game.players) {
      if (!p) continue;
      if (p.team === this.owner.team && isEligible(p)) protectedPlayers++;
      else if (game.areEnemies(this.owner, p) && p.combat && p.combat.hasBall) armedEnemies++;
    }
    if (protectedPlayers === 0) return 0;
    let u = (this.def.aiWeight ?? 0.9) * (0.25 + 0.15 * protectedPlayers + 0.1 * armedEnemies);
    if (ctx.incomingBall && ctx.incomingTime > 0.3) u += 0.1;
    if (ctx.alliesInfield < ctx.enemiesInfield) u += 0.1;
    return clamp01(u);
  }

  // ------------------------------------------------------------------ groups
  _createGroup(player, clones, duration) {
    const P = this.params;
    const g = {
      player, active: true, slotCount: clones + 1,
      clones: new Array(clones).fill(null), cloneSlot: new Array(clones).fill(0), fromSlot: new Array(clones).fill(0),
      arcSign: new Array(clones).fill(0),
      playerSlot: 0, nextShuffleIn: 0, inTransit: false, transitT: 0, movePlayer: false, fromPlayerSlot: 0, toPlayerSlot: 0,
    };
    this._axes(player, _lat, _fwd);
    const centre = (g.slotCount - 1) * 0.5;
    const bounds = zoneBounds(player);
    // Start with the real player in the slot that keeps the most of the line inside their zone.
    g.playerSlot = chooseInitialSlot(bounds, player.position, _lat, g.slotCount, P.slotSpacing);
    _anchor.copy(player.position).addScaledVector(_lat, -(g.playerSlot - centre) * P.slotSpacing);
    let spawned = 0;
    for (let j = 0, slot = 0; j < clones; j++, slot++) {
      if (slot === g.playerSlot) slot++;
      g.cloneSlot[j] = slot;
      g.fromSlot[j] = slot;
      // Clones burst out of the real body and fan out to their slots.
      const clone = ShadowClone.spawnAnimated(player, this.owner, player.position, player.yaw || 0, 0);
      if (!clone) continue;
      clone.followSharpness = P.followSharpness;
      this._slotWorld(player, _anchor, _lat, slot, centre, bounds, _pos);
      clone.setDesiredPose(_pos, player.yaw || 0);
      g.clones[j] = clone;
      spawned++;
    }
    if (spawned === 0) return null;
    player.status?.apply?.('obscured', duration, 1, this);
    // Staggered first shuffle so the formations never swap in sync.
    g.nextShuffleIn = lerp(0.35, Math.max(0.4, P.shuffleInterval), rand01());
    return g;
  }

  _tickGroup(g, dt) {
    const P = this.params, player = g.player;
    this._axes(player, _lat, _fwd);
    const centre = (g.slotCount - 1) * 0.5;
    const bounds = zoneBounds(player);
    let t = 1, eased = 1;
    if (g.inTransit) {
      g.transitT += dt / Math.max(0.05, P.transitDuration);
      t = clamp01(g.transitT);
      eased = smoothstep01(t); // zero velocity at both ends
    }
    // The anchor is derived from the REAL position every frame, so the formation always follows the player.
    const playerSlotPos = g.inTransit && g.movePlayer ? lerp(g.fromPlayerSlot, g.toPlayerSlot, eased) : g.playerSlot;
    _anchor.copy(player.position).addScaledVector(_lat, -(playerSlotPos - centre) * P.slotSpacing);
    const sharpness = g.inTransit ? P.transitSharpness : P.followSharpness;
    for (let j = 0; j < g.clones.length; j++) {
      const c = g.clones[j];
      if (!c || !c.alive) continue;
      const slotPos = g.inTransit ? lerp(g.fromSlot[j], g.cloneSlot[j], eased) : g.cloneSlot[j];
      _pos.copy(_anchor).addScaledVector(_lat, (slotPos - centre) * P.slotSpacing);
      if (g.inTransit && g.arcSign[j] !== 0) _pos.addScaledVector(_fwd, P.crossingArc * Math.sin(Math.PI * t) * g.arcSign[j]);
      clampToBounds(bounds, _pos);
      _pos.y = player.position.y;
      c.setDesiredPose(_pos, player.yaw || 0, sharpness);
    }
    if (g.inTransit) {
      if (g.movePlayer) this._driveSideStep(g, _lat, t);
      if (t >= 1) {
        g.inTransit = false;
        if (g.movePlayer) g.playerSlot = g.toPlayerSlot;
        g.arcSign.fill(0);
        g.nextShuffleIn = this._nextInterval();
      }
      return;
    }
    g.nextShuffleIn -= dt;
    if (g.nextShuffleIn <= 0) this._startShuffle(g, _anchor, _lat, centre, bounds);
  }

  /** Overrides only the lateral component of the real player's velocity to follow the swap curve. */
  _driveSideStep(g, lateral, t) {
    const player = g.player;
    if (!this._canBeMoved(player)) return;
    const T = Math.max(0.05, this.params.transitDuration);
    // d/dt of the smoothstep-eased slot position: (to - from) * spacing * 6t(1-t) / T
    const lateralSpeed = (g.toPlayerSlot - g.fromPlayerSlot) * this.params.slotSpacing * smoothstep01Derivative(t) / T;
    const v = player.velocity;
    _planar.set(v ? v.x : 0, 0, v ? v.z : 0);
    _planar.addScaledVector(lateral, -_planar.dot(lateral));
    _planar.addScaledVector(lateral, lateralSpeed);
    player.motor.setPlanarVelocity(_planar);
  }

  _startShuffle(g, anchor, lateral, centre, bounds) {
    const P = this.params, player = g.player;
    for (let j = 0; j < g.fromSlot.length; j++) { g.fromSlot[j] = g.cloneSlot[j]; g.arcSign[j] = 0; }
    // Preferred: the real player swaps with a random clone whose slot is inside the player's zone.
    let chosen = -1;
    if (P.moveRealPlayers && this._canBeMoved(player)) {
      let candidates = 0;
      for (let j = 0; j < g.clones.length; j++) {
        const c = g.clones[j];
        if (!c || !c.alive) continue;
        if (!isSlotInsideBounds(bounds, anchor, lateral, g.cloneSlot[j], centre, P.slotSpacing)) continue;
        candidates++;
        if (Math.floor(rand01() * candidates) === 0) chosen = j; // reservoir sampling, no allocation
      }
    }
    if (chosen >= 0) {
      g.movePlayer = true;
      g.fromPlayerSlot = g.playerSlot;
      g.toPlayerSlot = g.cloneSlot[chosen];
      g.cloneSlot[chosen] = g.playerSlot;
      g.arcSign[chosen] = rand01() < 0.5 ? 1 : -1;
    } else {
      // Fallback: two clones cross paths (the player is left alone).
      let a = -1, b = -1;
      for (let j = 0; j < g.clones.length; j++) {
        const c = g.clones[j];
        if (!c || !c.alive) continue;
        if (a < 0) a = j;
        else if (b < 0 || rand01() < 0.5) b = j;
      }
      if (a < 0 || b < 0) { g.nextShuffleIn = this._nextInterval(); return; }
      g.movePlayer = false;
      const tmp = g.cloneSlot[a];
      g.cloneSlot[a] = g.cloneSlot[b];
      g.cloneSlot[b] = tmp;
      g.arcSign[a] = 1;
      g.arcSign[b] = -1;
    }
    g.inTransit = true;
    g.transitT = 0;
  }

  _endGroup(g, effects) {
    if (!g.active) return;
    g.active = false;
    for (let j = 0; j < g.clones.length; j++) {
      if (g.clones[j]) g.clones[j].dissolve(effects);
      g.clones[j] = null;
    }
    g.player?.status?.remove?.('obscured', this);
  }

  _endAllGroups(effects) {
    for (const g of this._groups) this._endGroup(g, effects);
    this._groups.length = 0;
  }

  // ------------------------------------------------------------------ events
  _onBallThrown(e) {
    if (!this.isActive || !e || e.isAbility || e.isPass || !e.ball) return;
    const g = this._findGroup(e.thrower);
    if (!g) return;
    const origin = e.origin || e.ball.position, vel = e.velocity || e.ball.velocity;
    const gs = gravityScaleOf(e, e.thrower);
    for (const c of g.clones) if (c && c.alive) c.mirrorThrow(origin, vel, gs, this.params.illusionBallLifetime);
  }

  _onCatchAttempt(e) {
    if (!this.params.mirrorCatches || !this.isActive || !e) return;
    const g = this._findGroup(e.player);
    if (!g) return;
    for (const c of g.clones) if (c && c.alive) c.mirrorCatch();
  }

  // ------------------------------------------------------------------ helpers
  _findGroup(player) {
    if (!player) return null;
    for (const g of this._groups) if (g.active && g.player === player) return g;
    return null;
  }

  _countAlive(g) { let n = 0; for (const c of g.clones) if (c && c.alive) n++; return n; }

  /** Only players in a controllable ground state are side-stepped (never mid-air, sliding, stunned or frozen). */
  _canBeMoved(p) {
    if (!p || !p.motor || typeof p.motor.setPlanarVelocity !== 'function') return false;
    if (p.inputLocked) return false;
    if (!this.params.moveHumanPlayers && p.isHuman) return false;
    const st = p.fsm && p.fsm.current;
    if (st && st !== 'grounded' && st !== 'sprinting' && st !== 'chargingThrow' && st !== 'catching') return false;
    return !(p.status && (p.status.has('frozen') || p.status.has('rooted')));
  }

  /** Formation line runs across the court (perpendicular to the attack direction) so it always faces the enemy. */
  _axes(player, lateral, forward) {
    const court = game.court;
    if (court && typeof court.sideSign === 'function' && player.team >= 0) forward.set(0, 0, -court.sideSign(player.team));
    else forward.set(Math.sin(player.yaw || 0), 0, Math.cos(player.yaw || 0));
    // The outfield strip lies behind the OPPONENT's baseline: from there the enemy is the other way.
    if (player.zone === ZONE.OUTFIELD) forward.negate();
    lateral.set(forward.z, 0, -forward.x); // up x forward
  }

  _slotWorld(player, anchor, lateral, slot, centre, bounds, out) {
    out.copy(anchor).addScaledVector(lateral, (slot - centre) * this.params.slotSpacing);
    clampToBounds(bounds, out);
    out.y = player.position.y;
    return out;
  }

  _nextInterval() {
    const P = this.params;
    return Math.max(0.2, P.shuffleInterval * (1 + lerp(-P.shuffleJitter, P.shuffleJitter, rand01())));
  }
}

registerAbility('shadow.decoy_dash', ShadowDecoyDash);
registerAbility('shadow.night_parade', ShadowNightParade);
registerAbility('shadow.mirage_formation', ShadowMirageFormation);
