// ---------------------------------------------------------------------------------------------------------------
// Rayne - "Speedball" (Attacker). Web port of the Unity reference abilities (Assets/.../Abilities/Heroes/Rayne/*.cs).
//
//   passive   rayne.overcharge              RayneOvercharge            throw modifier: charge 2 s -> +50% speed, +20% radius
//   skill     rayne.supersonic_meteor       RayneSupersonicMeteor      fire fastball + 3 m knockback shockwave (CD 10 s)
//   ultimate  rayne.hyperbeam_transpierce   RayneHyperbeamTranspierce  unblockable piercing beam-ball
//
// RayneSupersonicMeteor is the REFERENCE IMPLEMENTATION every other web ability follows. Anatomy of an ability:
//   * A class extending AbilityBase, registered by roster id with registerAbility(id, cls).
//   * `static defaults` hold ability-specific tuning; the roster definition's `params` override them (this.params).
//     Cooldown / castTime / duration / ultCost are NOT tuning fields here: they live on the roster definition (this.def)
//     so designers balance every ability from one table (abilities/roster.js).
//   * Runtime state lives on the instance (one instance per player) and is created in onInitialize / onCast.
//   * Gameplay runs on SCALED time (the dt passed to the hooks, game.time.now): hitstop and pause freeze it.
//     Feedback (VFX, SFX, camera shake) goes through the optional systems with `?.` so a missing module never breaks play.
//   * Nothing ever moves a player outside the court rules (motor confinement, clampToPlayerZone).
// ---------------------------------------------------------------------------------------------------------------
import * as THREE from 'three';
import { game } from '../../game.js';
import { AbilityBase, FAIL, registerAbility } from '../abilityBase.js';
import { throwAbilityBall } from '../abilityUtil.js';
import { overchargeFraction, overchargeMultipliers, inCorridor, clamp01 } from '../shared/rayneMath.js';
import { MeteorPayload, BeamPayload, FIRE_COLOR, BEAM_COLOR } from '../shared/raynePayloads.js';

// Module-scope temporaries (no per-frame allocations).
const _hand = new THREE.Vector3();
const _dir = new THREE.Vector3();
const _vel = new THREE.Vector3();
const _mults = { speed: 1, radius: 1 };

/** World position of a player's throwing hand (right-hand socket of the realistic avatar, else chest height). */
function handPosition(player, out) {
  const socket = player.avatar && player.avatar.rightHandSocket;
  if (socket && socket.getWorldPosition) return socket.getWorldPosition(out);
  return out.set(player.position.x, player.position.y + (player.height || 1.8) * 0.75, player.position.z);
}

/** Hand socket Object3D for attached effects (or the player root as a fallback). */
function handObject(player) {
  return (player.avatar && player.avatar.rightHandSocket) || player.root || null;
}

/** Planar unit flight direction of a ball (falls back to the player's facing). */
function flightDirection(ball, player, out) {
  const v = ball && ball.velocity;
  if (v && v.x * v.x + v.z * v.z > 1e-6) return out.set(v.x, 0, v.z).normalize();
  if (player && player.forward) return out.copy(player.forward);
  return out.set(0, 0, 1);
}

/** Targetable enemies of `owner` standing within `radius` (planar) of `target` (excluding the target). Allocation-free. */
function countEnemiesAround(owner, target, radius) {
  let n = 0;
  const r2 = radius * radius;
  for (const p of game.players) {
    if (!p || p === target || !game.areEnemies(owner, p) || !p.isTargetable) continue;
    const dx = p.position.x - target.position.x, dz = p.position.z - target.position.z;
    if (dx * dx + dz * dz <= r2) n++;
  }
  return n;
}

// ===============================================================================================================
// Passive - Overcharge
// ===============================================================================================================

/**
 * Rayne's passive [Overcharge]: "charging throws increase ball velocity up to +50% and radius +20% over 2 s".
 *
 * Implemented as a throw modifier (order 0, i.e. before situational boosts such as stealth or the perfect-catch counter
 * boost, which multiply on top) registered on Rayne's Combat for as long as the passive is equipped:
 *     t = clamp01(chargeSeconds / 2 s);   speedMul *= 1 + 0.5 t;   radiusMul *= 1 + 0.2 t
 * The final speed is still capped at 220 km/h by the throw solver (rally rule). `modify` is side-effect free (it may be
 * evaluated for previews); feedback happens in `committed` and in the passive tick: a heat glow in the throwing hand
 * once the throw is held past a normal full charge, and a distinct cue when the 2 s overcharge is reached.
 * Also guarantees profile.maxChargeTime >= 2 s so the full overcharge is reachable (restored on unequip).
 */
export class RayneOvercharge extends AbilityBase {
  static defaults = {
    fullTime: 2,               // s of charging for the full bonus (spec: 2 s)
    maxSpeedBonus: 0.5,        // +50% at full overcharge
    maxRadiusBonus: 0.2,       // +20% at full overcharge
    applyToAbilityThrows: false, // conjured ability balls do not benefit (Overcharge rewards holding a real throw)
    heatColor: 0xff8529,
    glowScale: 0.35,
    releaseShake: 0.2,         // camera shake amplitude on a fully overcharged release
  };

  onInitialize() {
    this._registeredOn = null;
    this._raisedProfile = null;
    this._originalMaxCharge = 0;
    this._glow = null;
    this._fullCuePlayed = false;
    // Stable modifier object (identity is used by removeThrowModifier).
    this._modifier = {
      order: 0,
      source: this,
      modify: (params) => this._modify(params),
      committed: (params, ball) => this._committed(params, ball),
    };
  }

  /** 0..1 overcharge for a throw charged `chargeSeconds`. */
  fraction(chargeSeconds) { return overchargeFraction(chargeSeconds, this.params.fullTime); }

  onEquip() { this._sync(); }

  onUnequip() {
    this._stopGlow();
    if (this._registeredOn) this._registeredOn.removeThrowModifier?.(this._modifier);
    this._registeredOn = null;
    this._restoreProfile();
  }

  /** Passives never cast. */
  onCast() {}

  /** Passive tick (every frame): keeps the modifier registered on the current Combat and drives the charge feedback. */
  onTick() {
    this._sync();
    const combat = this._registeredOn;
    if (!combat || !combat.isCharging) {
      this._fullCuePlayed = false;
      this._stopGlow();
      return;
    }
    const secs = combat.chargeSeconds || 0;
    const normalFull = (combat.profile && combat.profile.fullChargeTime) || 0.75;
    // Heat builds in the hand once the throw is held past a normal full charge.
    if (secs > normalFull && this._glow == null) {
      const hand = handObject(this.owner);
      if (hand) this._glow = game.vfx?.attach?.('fireTrail', hand, { duration: 4, color: this.params.heatColor, scale: this.params.glowScale }) ?? null;
    }
    if (!this._fullCuePlayed && secs >= this.params.fullTime) {
      this._fullCuePlayed = true;
      handPosition(this.owner, _hand);
      game.vfx?.play?.('fireTrail', _hand, { scale: 0.6, color: this.params.heatColor });
      game.audio?.play?.('overchargeReady', _hand, 0.5, 1.35);
    }
  }

  onRoundReset() { this._fullCuePlayed = false; this._stopGlow(); }

  /** Passives are never activated by the AI. */
  evaluateAI() { return 0; }

  // ------------------------------------------------------------------ throw modifier
  _eligible(p) {
    if (!p || p.isPass) return false;
    if (p.isAbility && !this.params.applyToAbilityThrows) return false;
    return !p.thrower || p.thrower === this.owner;
  }

  _modify(p) {
    if (!this._eligible(p)) return;
    overchargeMultipliers(p.chargeSeconds, this.params.fullTime, this.params.maxSpeedBonus, this.params.maxRadiusBonus, _mults);
    p.speedMul = (p.speedMul ?? 1) * _mults.speed;
    p.radiusMul = (p.radiusMul ?? 1) * _mults.radius;
  }

  _committed(p, ball) {
    this._stopGlow();
    this._fullCuePlayed = false;
    if (!this._eligible(p) || this.fraction(p.chargeSeconds) < 0.999) return;
    // Fully overcharged release: heavy whoosh along the flight direction + a small kick for nearby cameras.
    const origin = p.origin || handPosition(this.owner, _hand);
    flightDirection(ball, this.owner, _dir);
    game.vfx?.play?.('fireTrail', origin, { scale: 1.3, color: this.params.heatColor, direction: _dir });
    game.audio?.play?.('throwHeavy', origin, 0.9, 0.9);
    if (this.params.releaseShake > 0) game.juice?.shake?.(this.params.releaseShake, 18, 0.14, origin);
  }

  // ------------------------------------------------------------------ helpers
  /** Registers the modifier on the current Combat (idempotent) and raises maxChargeTime to the overcharge time. */
  _sync() {
    const combat = this.owner && this.owner.combat;
    if (combat !== this._registeredOn) {
      if (this._registeredOn) this._registeredOn.removeThrowModifier?.(this._modifier);
      if (combat && typeof combat.addThrowModifier === 'function') {
        combat.removeThrowModifier?.(this._modifier);
        combat.addThrowModifier(this._modifier);
        this._registeredOn = combat;
      } else {
        this._registeredOn = null;
      }
    }
    const profile = combat && combat.profile;
    if (profile && profile !== this._raisedProfile) {
      this._restoreProfile();
      this._raisedProfile = profile;
      this._originalMaxCharge = profile.maxChargeTime;
      if (!(profile.maxChargeTime >= this.params.fullTime)) profile.maxChargeTime = this.params.fullTime;
    }
  }

  _restoreProfile() {
    if (this._raisedProfile && this._raisedProfile.maxChargeTime !== this._originalMaxCharge && this._originalMaxCharge > 0) {
      this._raisedProfile.maxChargeTime = this._originalMaxCharge;
    }
    this._raisedProfile = null;
  }

  _stopGlow() {
    if (this._glow != null) { game.vfx?.stop?.(this._glow); this._glow = null; }
  }
}

// ===============================================================================================================
// Skill - Supersonic Meteor (reference implementation)
// ===============================================================================================================

/**
 * Rayne's skill [Supersonic Meteor]: "fire-infused fastball; on impact a 3 m AOE shockwave knocks back nearby enemies
 * (CD 10 s)".
 *
 *   tryActivate ─► (castTime > 0) onCastStarted: fire gathers in Rayne's throwing hand
 *               ─► onCast: conjure the meteor (style 'meteor', x1.6 speed, 0.3 gravity) with throwAbilityBall at the
 *                          soft-lock target, carrying a MeteorPayload; holdActive()
 *               ─► onTick: wait until the payload resolves (hit / surface / catch / obstacle) or `resolveTimeout` (3 s);
 *                          a meteor that burst against a wall is spent and dropped instead of ricocheting on
 *               ─► endAbility() ─► cooldown (roster: 10 s)
 *   MeteorPayload: player hit or court impact ─► 3 m shockwave: radial knockback on ENEMIES (linear falloff, small
 *                  upward lift, 0.35 s stun at the core) + 'shockwave' VFX + SFX + Perlin camera shake. The direct hit is
 *                  still resolved normally by Combat (damage, catch rules, juice pipeline). A clean catch smothers the
 *                  fire: no shockwave - the enemy's counter-play.
 *
 * Because the meteor is launched through the regular throw pipeline it inherits everything a real throw has: throw
 * modifiers (e.g. a perfect-catch counter boost), lead targeting, the 220 km/h cap and a BallThrown event for the HUD,
 * AI dodging and Danger Sense.
 */
export class RayneSupersonicMeteor extends AbilityBase {
  static defaults = {
    // --- the fastball
    speedMul: 1.6,              // x full-charge throw speed (still capped at 220 km/h)
    gravityScale: 0.3,          // 0 = laser-flat, 1 = real gravity: a low value keeps the fastball flat
    radiusMul: 1,               // collision radius multiplier (1 = regulation 0.105 m ball)
    resolveTimeout: 3,          // s the ability waits for the meteor to resolve before its cooldown starts
    spentBallSpeedFraction: 0.25, // speed kept by a meteor that burst against a wall (it drops instead of ricocheting)
    // --- the shockwave (roster params: shockwaveRadius, knockback, coreStun)
    shockwaveRadius: 3,         // m (spec)
    knockback: 7,               // m/s horizontal velocity change at the epicentre
    edgeKnockbackFraction: 0.25,// fraction left at the rim
    upwardSpeed: 1.8,           // m/s lift at the epicentre
    coreStunRadius: 1.25,       // m
    coreStun: 0.35,             // s (spec)
    freeBallPush: 3.5,          // m/s given to loose balls inside the blast
    detonateOnObstacles: true,  // clones / shields stopping the meteor set it off too
    hitstop: 0.06,              // extra freeze frame when the blast catches enemies (spec 0.03-0.1 s)
    // --- feedback
    fireColor: FIRE_COLOR,
    trailScale: 1,
    shakeAmplitude: 0.6,
    shakeFrequency: 20,
    shakeDuration: 0.4,
    // --- AI
    aiMaxRange: 18,             // m: bots only consider the meteor within this distance of the nearest enemy
  };

  onInitialize() {
    this._ball = null;
    this._payload = null;
    this._trackedFor = 0;
    this._windup = null;
  }

  /** The meteor currently tracked (null when none). */
  get activeMeteor() { return this._ball; }
  /** Payload of the tracked meteor (null when none). */
  get activePayload() { return this._payload; }

  /** The meteor is a conjured projectile: without a BallManager / Combat there is nothing to throw. */
  canActivateCustom() {
    if (!game.balls || !this.owner.combat) return FAIL.CUSTOM;
    return null;
  }

  /** Wind-up (only when the roster gives a castTime): flames gather in the throwing hand. */
  onCastStarted() {
    const hand = handObject(this.owner);
    if (hand) this._windup = game.vfx?.attach?.('fireTrail', hand, { duration: this.castTime + 0.15, color: this.params.fireColor, scale: 0.5 }) ?? null;
    game.audio?.play?.('abilityCast', handPosition(this.owner, _hand), 0.9, 1.05);
  }

  /** SPEC HOOK: conjure and launch the meteor. */
  onCast() {
    this._stopWindup();
    const P = this.params;
    this._payload = new MeteorPayload(this.owner, {
      radius: P.shockwaveRadius,
      knockback: P.knockback,
      edgeFraction: P.edgeKnockbackFraction,
      upward: P.upwardSpeed,
      coreStunRadius: P.coreStunRadius,
      coreStun: P.coreStun,
      freeBallPush: P.freeBallPush,
      detonateOnObstacles: P.detonateOnObstacles,
      color: P.fireColor,
      trailScale: P.trailScale,
      trailDuration: P.resolveTimeout + 0.5,
      shakeAmplitude: P.shakeAmplitude,
      shakeFrequency: P.shakeFrequency,
      shakeDuration: P.shakeDuration,
      hitstop: P.hitstop,
    }, this);

    // Target stays null: the throw pipeline uses the thrower's soft-lock target (or the raw aim).
    this._ball = throwAbilityBall(this.owner, {
      style: 'meteor', speedMul: P.speedMul, gravityScale: P.gravityScale, radiusMul: P.radiusMul, payload: this._payload,
    });
    if (!this._ball) {
      // No BallManager / pool exhausted: fail gracefully, the cooldown still applies (the input was consumed).
      this._payload = null;
      this.endAbility();
      return;
    }
    // Some combat builds only call onLaunched for regular throws: make sure the trail is attached exactly once.
    if (!this._payload.ball) this._payload.onLaunched(this._ball);

    // Stay Active (ignoring the roster duration) until the meteor resolves - onTick polls the payload.
    this._trackedFor = 0;
    this.holdActive();

    // Release feedback for the thrower: throw animation, heavy release sound, small recoil shake.
    flightDirection(this._ball, this.owner, _dir);
    this.owner.avatar?.playThrow?.(_dir);
    const origin = this._ball.position || handPosition(this.owner, _hand);
    game.audio?.play?.('throwHeavy', origin, 1, 0.95);
    game.juice?.shake?.(P.shakeAmplitude * 0.3, P.shakeFrequency, 0.15, origin);
  }

  /** SPEC HOOK: while Active, wait for the meteor to resolve (or time out). */
  onTick(dt) {
    this._trackedFor += dt;
    const p = this._payload, b = this._ball;
    if (p && b && !p.resolved) {
      const ours = b.payload === p;
      if (ours && b.state === 'live' && p.detonated) {
        // Burst against a wall but still flying: the spent meteor drops instead of ricocheting on as a live ball.
        // Done here (frame update) rather than inside the payload callbacks, which run in the middle of the ball's sweep.
        b.makeFree?.(_vel.copy(b.velocity).multiplyScalar(this.params.spentBallSpeedFraction), true);
      } else if (ours && b.state !== 'live' && b.state !== 'stasis') {
        // The flight ended without a callback we saw (e.g. an obstacle absorbed it): let the payload decide.
        p.onEnded(b);
      } else if (!ours && b.state === 'live') {
        // The pooled ability ball was recycled into another projectile: our flight is long over.
        p.finish();
      }
    }
    if (!p || p.resolved || this._trackedFor >= this.params.resolveTimeout) this.endAbility();
  }

  /**
   * SPEC HOOK: stunned / frozen / eliminated / round end. A meteor that already left the hand keeps flying (it is a
   * physical projectile now); only the wind-up is cancelled and the ability stops tracking.
   */
  onInterrupt() { this._stopWindup(); }

  onEnd() {
    this._stopWindup();
    this._ball = null;
    this._payload = null;
    this._trackedFor = 0;
  }

  onUnequip() { this._stopWindup(); }

  /**
   * AI utility: worth it with an enemy in range and not while a ball is about to hit Rayne (dodge/catch first).
   * The AOE makes it much more valuable against clustered enemies (each extra enemy within the shockwave radius of the
   * likely target adds utility); slightly less attractive at long range where the target has time to react.
   */
  evaluateAI(ctx) {
    if (!ctx || !ctx.self || !ctx.nearestEnemy) return 0;
    const P = this.params;
    if (ctx.nearestEnemyDistance > P.aiMaxRange) return 0;
    if (ctx.incomingBall && ctx.incomingTime < 0.45) return 0;
    const clustered = countEnemiesAround(this.owner, ctx.nearestEnemy, P.shockwaveRadius);
    const rangeFactor = 1 - 0.5 * clamp01((ctx.nearestEnemyDistance - 6) / Math.max(1, P.aiMaxRange - 6));
    let u = (this.def.aiWeight ?? 0.8) * (0.55 + 0.25 * clustered) * rangeFactor;
    if (ctx.alliesInfield < ctx.enemiesInfield) u += 0.1; // behind on players: use the cooldown aggressively
    return clamp01(u);
  }

  _stopWindup() {
    if (this._windup != null) { game.vfx?.stop?.(this._windup); this._windup = null; }
  }
}

// ===============================================================================================================
// Ultimate - Hyperbeam Transpierce
// ===============================================================================================================

/**
 * Rayne's ultimate [Hyperbeam Transpierce]: "unblockable beam-ball that penetrates all enemies in its path".
 * Conjures a 'beam' ability ball: unblockable (ignores shields, evasion and catches), pierce (keeps flying after each
 * enemy it hits), x2.2 speed (still capped at 220 km/h), zero gravity (laser-straight) and x1.3 radius. A BeamPayload
 * adds the beam trail and heavy juice on every pierce. Stays Active until the beam grounds out (wall/floor) or a timeout.
 */
export class RayneHyperbeamTranspierce extends AbilityBase {
  static defaults = {
    speedMul: 2.2,          // spec (capped at 220 km/h by the throw solver)
    radiusMul: 1.3,         // 30% bigger than a match ball: easier to connect
    gravityScale: 0,        // perfectly straight
    resolveTimeout: 2.5,    // s
    knockback: 5,           // m/s shove for enemies that survive the beam
    heavyHitstop: 0.1,      // s per enemy pierced (Juice clamps to 0.03-0.1)
    recoilSpeed: 1.2,       // m/s pushing Rayne backwards on release
    beamColor: BEAM_COLOR,
    trailScale: 1.3,
    shakeAmplitude: 0.9,
    shakeFrequency: 26,
    shakeDuration: 0.5,
    aiCorridorHalfWidth: 0.9, // m: corridor used to count enemies lined up behind the target
    aiMaxRange: 24,
  };

  onInitialize() {
    this._ball = null;
    this._payload = null;
    this._trackedFor = 0;
    this._charge = null;
  }

  /** The beam currently in flight (null when none). */
  get activeBeam() { return this._ball; }

  canActivateCustom() {
    if (!game.balls || !this.owner.combat) return FAIL.CUSTOM;
    return null;
  }

  /** Charge-up (castTime > 0): plasma gathers in the hand, the screen tightens for the caster. */
  onCastStarted() {
    const hand = handObject(this.owner);
    if (hand) this._charge = game.vfx?.attach?.('beamTrail', hand, { duration: this.castTime + 0.15, color: this.params.beamColor, scale: 0.5 }) ?? null;
    game.audio?.play?.('ultimateCast', handPosition(this.owner, _hand), 1, 1);
    if (this.owner.isLocal) game.renderer?.pulse?.('ultimate', 0.8, Math.max(0.25, this.castTime));
  }

  onCast() {
    this._stopCharge();
    const P = this.params;
    this._payload = new BeamPayload(this.owner, {
      knockback: P.knockback, hitstop: P.heavyHitstop, shakeAmplitude: P.shakeAmplitude, shakeFrequency: P.shakeFrequency,
      shakeDuration: P.shakeDuration, color: P.beamColor, trailScale: P.trailScale, trailDuration: P.resolveTimeout + 0.5,
    });
    this._ball = throwAbilityBall(this.owner, {
      style: 'beam', speedMul: P.speedMul, radiusMul: P.radiusMul, gravityScale: P.gravityScale,
      unblockable: true, pierce: true, payload: this._payload,
    });
    if (!this._ball) {
      this._payload = null;
      this.endAbility();
      return;
    }
    if (!this._payload.ball) this._payload.onLaunched(this._ball);
    this._trackedFor = 0;
    this.holdActive();

    // Release: muzzle flash at the hand, recoil through Rayne's body, heavy rumble, screen pulse for the caster.
    flightDirection(this._ball, this.owner, _dir);
    const origin = this._ball.position || handPosition(this.owner, _hand);
    this.owner.avatar?.playThrow?.(_dir);
    game.vfx?.play?.('hit', origin, { scale: 0.7, color: P.beamColor, direction: _dir });
    game.audio?.play?.('beamRelease', origin, 1, 0.8);
    if (this.owner.isLocal) game.renderer?.pulse?.('ultimate', 0.8, 0.4);
    if (this.owner.motor && P.recoilSpeed > 0) {
      _vel.set(-_dir.x * P.recoilSpeed, 0, -_dir.z * P.recoilSpeed);
      this.owner.motor.addImpulse?.(_vel);
    }
    game.juice?.shake?.(P.shakeAmplitude * 0.5, P.shakeFrequency, 0.25, origin);
  }

  onTick(dt) {
    this._trackedFor += dt;
    const p = this._payload, b = this._ball;
    if (p && b && !p.resolved) {
      const ours = b.payload === p;
      // The beam grounds out on the first wall/floor it strikes: it never ricochets back through players.
      if (ours && p.hasHitSurface && b.state === 'live') b.makeFree?.(_vel.copy(b.velocity).multiplyScalar(0.1), true);
      else if ((ours && b.state !== 'live' && b.state !== 'stasis') || (!ours && b.state === 'live')) p.finish();
    }
    if (!p || p.resolved || this._trackedFor >= this.params.resolveTimeout) this.endAbility();
  }

  onInterrupt() { this._stopCharge(); }

  onEnd() {
    this._stopCharge();
    this._ball = null;
    this._payload = null;
    this._trackedFor = 0;
  }

  onUnequip() { this._stopCharge(); }

  /**
   * The beam cannot be caught or blocked, so any enemy in range is a near-certain hit; premium value when several
   * enemies line up behind the target (counted inside a corridor along the throw line). Never die with a full meter.
   */
  evaluateAI(ctx) {
    if (!ctx || !ctx.self || !ctx.nearestEnemy) return 0;
    const P = this.params;
    if (ctx.nearestEnemyDistance > P.aiMaxRange) return 0;
    if (ctx.incomingBall && ctx.incomingTime < 0.35) return 0;
    const from = this.owner.position, to = ctx.nearestEnemy.position;
    let ax = to.x - from.x, az = to.z - from.z;
    const len = Math.hypot(ax, az);
    let aligned = 1;
    if (len > 0.01) {
      ax /= len; az /= len;
      aligned = 0;
      for (const p of game.players) {
        if (p && game.areEnemies(this.owner, p) && p.isTargetable && inCorridor(from, ax, az, p.position, P.aiCorridorHalfWidth)) aligned++;
      }
    }
    let u = (this.def.aiWeight ?? 1) * (0.6 + 0.25 * Math.max(0, aligned - 1));
    if (ctx.alliesInfield < ctx.enemiesInfield) u += 0.15;                  // comeback tool
    if (ctx.timeLeft > 0 && ctx.timeLeft < 20) u += 0.1;                     // do not die with a full meter
    return clamp01(u);
  }

  _stopCharge() {
    if (this._charge != null) { game.vfx?.stop?.(this._charge); this._charge = null; }
  }
}

registerAbility('rayne.overcharge', RayneOvercharge);
registerAbility('rayne.supersonic_meteor', RayneSupersonicMeteor);
registerAbility('rayne.hyperbeam_transpierce', RayneHyperbeamTranspierce);
