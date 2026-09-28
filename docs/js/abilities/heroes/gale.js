// ---------------------------------------------------------------------------------------------------------------
// Gale - "Stealth / Assassin" (Attacker). Web port of the Unity reference abilities (Assets/.../Heroes/Gale/*.cs).
//
//   passive   gale.silent_footsteps    GaleSilentFootsteps     permanent 'silentFootsteps' status (audio + minimap read it)
//   skill     gale.optical_camouflage  GaleOpticalCamouflage   4 s cloak +20% speed; a throw from stealth +30% and reveals
//   ultimate  gale.shadow_strike       GaleShadowStrike        teleport behind the loose ball on her side and pick it up
//
// Statuses are the single source of truth other modules read: Status turns 'cloaked' into avatar.setCloaked (near
// invisible for enemies of the local player, translucent for allies) and 'haste' into motor speed; Audio skips footsteps
// of 'silentFootsteps' players and the HUD minimap hides them from enemies.
// ---------------------------------------------------------------------------------------------------------------
import * as THREE from 'three';
import { game } from '../../game.js';
import { EV } from '../../core/events.js';
import { ZONE } from '../../core/constants.js';
import { Court } from '../../world/court.js';
import { AbilityBase, FAIL, SLOT, registerAbility } from '../abilityBase.js';

// Module-scope temporaries.
const _chest = new THREE.Vector3();
const _dest = new THREE.Vector3();
const _ground = new THREE.Vector3();
const _look = new THREE.Vector3();
const _attack = new THREE.Vector3();
const _zero = new THREE.Vector3();

const clamp01 = (x) => (x < 0 ? 0 : x > 1 ? 1 : x);

/** Chest-height point of a player (effects anchor). */
function chestOf(p, out) {
  return out.set(p.position.x, p.position.y + (p.height || 1.7) * 0.72, p.position.z);
}

// ===============================================================================================================
// Passive - Silent Footsteps
// ===============================================================================================================

/**
 * Gale's passive [Silent Footsteps]: "silences movement audio and hides mini-map ping".
 * Applies a permanent 'silentFootsteps' status (duration 0 = until removed, source = this ability) while equipped.
 * Round resets, eliminations and revives may wipe every status, so the passive tick re-applies it whenever it went
 * missing (a cheap `has` check per frame); it is removed on unequip.
 */
export class GaleSilentFootsteps extends AbilityBase {
  static defaults = {};

  onEquip() { this._ensureApplied(); }
  onUnequip() { this.owner?.status?.remove?.('silentFootsteps', this); }
  /** Passives never cast. */
  onCast() {}
  /** Every frame: re-applied after status wipes (round reset, revive...). */
  onTick() { this._ensureApplied(); }
  onRoundReset() { this._ensureApplied(); }
  /** Passives are never activated by the AI. */
  evaluateAI() { return 0; }

  _ensureApplied() {
    const status = this.owner && this.owner.status;
    if (!status || typeof status.has !== 'function' || status.has('silentFootsteps')) return;
    status.apply('silentFootsteps', 0, 1, this);
  }
}

// ===============================================================================================================
// Skill - Optical Camouflage
// ===============================================================================================================

/**
 * Gale's skill [Optical Camouflage]: "cloak 4 s (+20% speed); throws from stealth gain +30% ball speed and reveal
 * location (CD 14 s)".
 * onCast applies 'cloaked' and 'haste' (magnitude 0.2) for the roster duration (4 s). While cloaked, this ability's
 * throw modifier multiplies throw speed by 1.3 and flags the throw with `reveals`. When such a throw is committed Gale
 * decloaks (cloak + haste removed, a short 'revealed' status marks her) and the ability ends, starting the 14 s
 * cooldown. The modifier itself is side-effect free, so throw previews never break the cloak.
 */
export class GaleOpticalCamouflage extends AbilityBase {
  static defaults = {
    haste: 0.2,              // +20% move speed while cloaked (roster param)
    throwBonus: 0.3,         // +30% ball speed for a throw from stealth (roster param)
    revealDuration: 1.5,     // s of 'revealed' after an ambush throw (0 = none)
    passesReveal: false,     // passes from stealth neither gain speed nor reveal
    fallbackDuration: 4,     // used only when the roster duration is 0
    shimmerColor: 0xc7dbff,  // pale refraction shimmer at the cloak / decloak moment
  };

  onInitialize() {
    this._registeredOn = null;
    this._modifier = {
      order: 50, // after the base charge/overcharge modifiers, before perfect-catch style bonuses
      source: this,
      modify: (p) => this._modify(p),
      committed: (p, ball) => this._committed(p, ball),
    };
  }

  /** True while the cloak from this ability is up. */
  get isCloaked() { return this.isActive && !!(this.owner.status && this.owner.status.has('cloaked')); }

  onEquip() {
    this._ensureModifier();
    // Fallback reveal path: a combat build that does not call `committed` still publishes BallThrown.
    this.listen(EV.BallThrown, (e) => {
      if (e && e.thrower === this.owner && (!e.isPass || this.params.passesReveal) && this.isCloaked) this._ambushReveal();
    });
  }

  onUnequip() {
    this._removeCloakStatuses();
    if (this._registeredOn) this._registeredOn.removeThrowModifier?.(this._modifier);
    this._registeredOn = null;
  }

  /** Revealed players cannot cloak (e.g. right after an ambush throw, or revealed by an enemy effect). */
  canActivateCustom() {
    const st = this.owner.status;
    if (!st || st.has('revealed')) return FAIL.CUSTOM;
    return null;
  }

  /** SPEC HOOK: vanish. */
  onCast() {
    this._ensureModifier();
    let duration = this.duration;
    if (duration <= 0) { this.extendActive(this.params.fallbackDuration); duration = this.params.fallbackDuration; }
    const st = this.owner.status;
    st.apply('cloaked', duration, 1, this);
    st.apply('haste', duration, this.params.haste, this);
    // One-shot shimmer at the moment of cloaking (no looping effect: it would give her position away).
    chestOf(this.owner, _chest);
    game.vfx?.play?.('teleport', _chest, { scale: 0.6, color: this.params.shimmerColor });
    game.audio?.play?.('cloak', _chest, 0.8, 1);
  }

  /** SPEC HOOK: stripped by something else (an enemy reveal, status wipe) -> the ability is over. */
  onTick() {
    if (!this.owner.status || !this.owner.status.has('cloaked')) this.endAbility();
  }

  onInterrupt() { this._removeCloakStatuses(); }

  onEnd() {
    const wasCloaked = !!(this.owner.status && this.owner.status.has('cloaked'));
    this._removeCloakStatuses();
    if (wasCloaked) this._decloakFx(0.6); // natural expiry: soft shimmer
  }

  onRoundReset() { this._removeCloakStatuses(); }

  /**
   * Best as an ambush: cloak while holding a ball with an enemy in range (the +30% throw lands before they react).
   * Also used to sneak to loose balls; a weak escape tool against a ball already in the air (it is still locked on).
   */
  evaluateAI(ctx) {
    if (!ctx || !ctx.self) return 0;
    const w = this.def.aiWeight ?? 0.7;
    if (ctx.holdingBall && ctx.nearestEnemy && ctx.nearestEnemyDistance < 14) return clamp01(w * 0.85);
    if (!ctx.holdingBall && ctx.freeBallsNearby > 0) return clamp01(w * 0.4);
    if (ctx.incomingBall && ctx.incomingTime < 0.8) return clamp01(w * 0.25);
    return clamp01(w * 0.1);
  }

  // ------------------------------------------------------------------ throw modifier
  _modify(p) {
    if (!p || !this.isCloaked || (p.thrower && p.thrower !== this.owner)) return;
    if (p.isPass && !this.params.passesReveal) return;
    if (!p.isPass) p.speedMul = (p.speedMul ?? 1) * (1 + this.params.throwBonus);
    p.reveals = true;
  }

  _committed(p) {
    if (!p || !p.reveals || !this.isCloaked) return;
    if (p.thrower && p.thrower !== this.owner) return;
    this._ambushReveal();
  }

  // ------------------------------------------------------------------ helpers
  /** Ambush throw: decloak with a sharp shimmer, mark the position, end the ability (cooldown starts). Idempotent. */
  _ambushReveal() {
    if (!this.isCloaked) return;
    this._removeCloakStatuses();
    if (this.params.revealDuration > 0) this.owner.status?.apply?.('revealed', this.params.revealDuration, 1, this);
    this._decloakFx(1.2);
    this.endAbility();
  }

  _ensureModifier() {
    const combat = this.owner && this.owner.combat;
    if (!combat || typeof combat.addThrowModifier !== 'function') return;
    if (this._registeredOn === combat) return;
    if (this._registeredOn) this._registeredOn.removeThrowModifier?.(this._modifier);
    combat.removeThrowModifier?.(this._modifier); // idempotent: never registered twice
    combat.addThrowModifier(this._modifier);
    this._registeredOn = combat;
  }

  _removeCloakStatuses() {
    const st = this.owner && this.owner.status;
    if (!st) return;
    st.remove('cloaked', this);
    st.remove('haste', this);
  }

  _decloakFx(intensity) {
    chestOf(this.owner, _chest);
    game.vfx?.play?.('teleport', _chest, { scale: 0.6 * intensity, color: this.params.shimmerColor });
    game.audio?.play?.('decloak', _chest, 0.7 * clamp01(intensity), 0.8);
  }
}

// ===============================================================================================================
// Ultimate - Shadow Strike
// ===============================================================================================================

/**
 * Gale's ultimate [Shadow Strike]: "instantly teleports directly behind the nearest unheld ball and picks it up".
 * Target: the nearest match ball nobody holds ('free', or frozen mid-air in Chrono's 'stasis') on HER side: within
 * `zoneExpansion` m (= the regular pickup reach, 0.55 m) of her own zone region - her infield half, or her U outfield
 * when she is an outfielder (hole-aware Court.distanceTo: the enemy half inside the U never counts) - and not reserved
 * for the other team (an awarded ball). Dead-ball rule: a ball resting in an enemy zone is theirs. Gale appears
 * `behindDistance` m behind the ball relative to her attack direction (infield: toward the enemy half; outfield: from
 * the ball toward the centre of the enemy half), clamped into her zone (U-aware Court.clamp), facing the enemy, with a
 * teleport burst at both ends - then the ball is put straight into her hand (combat.giveBall).
 * Fails with 'noTarget' when no ball qualifies and refuses while already holding a ball.
 */
export class GaleShadowStrike extends AbilityBase {
  static defaults = {
    zoneExpansion: 0.55,       // m past Gale's confinement (= BALL_PHYS.pickupZoneReach: her side of the lines only)
    includeStasisBalls: true,  // snatch balls frozen by Chrono's Stasis Field
    behindDistance: 0.7,       // m behind the ball (relative to the attack direction)
    poofColor: 0x383d4d,       // dark smoke of the teleport bursts
    arrivalShake: 0.25,
  };

  /** Hands must be free, and a ball must qualify. */
  canActivateCustom() {
    const combat = this.owner.combat;
    if (!combat || combat.hasBall) return FAIL.CUSTOM;
    if (!this.findTargetBall()) return FAIL.NO_TARGET;
    return null;
  }

  onCastStarted() {
    chestOf(this.owner, _chest);
    game.vfx?.play?.('teleport', _chest, { scale: 0.6, color: this.params.poofColor });
    game.audio?.play?.('ultimateCast', _chest, 0.8, 1.1);
  }

  /** SPEC HOOK: blink behind the ball and take it. */
  onCast() {
    const o = this.owner, combat = o.combat;
    const ball = combat && !combat.hasBall ? this.findTargetBall() : null;
    if (!ball) {
      // The ball was taken during the channel: refund the meter and report the failure to HUD / AI.
      if (this.slot === SLOT.ULTIMATE) o.abilities?.addUltimateCharge?.(this.def.ultCost ?? 1, 'refund');
      game.events.emit(EV.AbilityFailed, { player: o, ability: this, slot: this.slot, reason: FAIL.NO_TARGET });
      this.endAbility();
      return;
    }

    _ground.set(ball.position.x, game.court ? game.court.floorY : 0, ball.position.z);
    this._attackDirection(_attack, _ground);
    // Directly behind the ball: the ball sits between Gale and the enemy. Court rules win over the exact spot.
    _dest.copy(_ground).addScaledVector(_attack, -this.params.behindDistance);
    const bounds = this._zoneBounds();
    if (bounds) Court.clamp(bounds, _dest);
    _dest.y = game.court ? game.court.floorY : 0;
    _look.set(_ground.x - _dest.x, 0, _ground.z - _dest.z);
    if (_look.lengthSq() < 0.01) _look.copy(_attack);
    const yaw = Math.atan2(_look.x, _look.z); // yaw 0 faces +Z

    chestOf(o, _chest);
    game.vfx?.play?.('teleport', _chest, { scale: 1, color: this.params.poofColor });
    game.audio?.play?.('teleport', _chest, 0.9, 1.05);
    if (combat.catchArmed) combat.cancelCatch?.();

    if (typeof o.teleport === 'function') o.teleport(_dest, yaw);
    else { o.position.copy(_dest); o.yaw = yaw; }
    o.motor?.setPlanarVelocity?.(_zero); // arrive planted, not sliding past the ball

    chestOf(o, _chest);
    game.vfx?.play?.('teleport', _chest, { scale: 1, color: this.params.poofColor });
    game.audio?.play?.('teleport', _chest, 1, 0.95);
    if (this.params.arrivalShake > 0) game.juice?.shake?.(this.params.arrivalShake, 20, 0.2, _chest);
    combat.giveBall(ball);
  }

  /**
   * Worth the meter when Gale is empty-handed and the best ball is far away (a long walk she skips), and as an escape
   * blink when a ball is about to hit her. Never when a loose ball is already at her feet.
   */
  evaluateAI(ctx) {
    if (!ctx || !ctx.self || ctx.holdingBall) return 0;
    const ball = this.findTargetBall();
    if (!ball) return 0;
    const w = this.def.aiWeight ?? 0.8;
    const distance = Math.hypot(ball.position.x - this.owner.position.x, ball.position.z - this.owner.position.z);
    let u = w * clamp01((distance - 2.5) / 7);
    if (ctx.incomingBall && ctx.incomingTime < 0.5 && distance > 1.5) u = Math.max(u, w * 0.7);
    if (ctx.enemiesInfield > 0 && ctx.freeBallsNearby === 0) u += 0.15;
    return clamp01(u);
  }

  /** Nearest eligible ball (see class docs) or null. Allocation-free. */
  findTargetBall() {
    const mgr = game.balls, o = this.owner;
    if (!mgr || !o) return null;
    // Match balls are the regular balls; fall back to every active ball (ability balls are filtered below).
    const balls = mgr.matchBalls && mgr.matchBalls.length ? mgr.matchBalls : mgr.active;
    if (!balls) return null;
    const zone = this._zoneBounds();
    const e = this.params.zoneExpansion;
    const now = game.time ? game.time.now : 0;
    let best = null, bestD = Infinity;
    for (const b of balls) {
      if (!b || b.isAbilityBall || b.holder || !b.position) continue;
      const okState = b.state === 'free' || (this.params.includeStasisBalls && b.state === 'stasis');
      if (!okState) continue;
      // An awarded ball reserved for the other team is not hers to take.
      if (b.reservedTeam >= 0 && b.reservedTeam !== o.team && now < (b.reservedUntil || 0)) continue;
      const p = b.position;
      if (zone && Court.distanceTo(zone, p) > e) continue;
      const dx = p.x - o.position.x, dz = p.z - o.position.z;
      const d = dx * dx + dz * dz;
      if (d < bestD) { bestD = d; best = b; }
    }
    return best;
  }

  /** Gale's confinement bounds, cached per (court, team, zone) - Court.confinement allocates and bots query often. */
  _zoneBounds() {
    const court = game.court, o = this.owner;
    if (!court || !(o.team >= 0)) return null;
    if (this._zoneCourt !== court || this._zoneTeam !== o.team || this._zoneName !== o.zone || !this._zone) {
      this._zoneCourt = court; this._zoneTeam = o.team; this._zoneName = o.zone;
      this._zone = court.confinement(o.team, o.zone);
    }
    return this._zone;
  }

  /**
   * Planar unit direction toward the enemy for Gale's current zone, seen from `from` (the ball). Infield: straight at
   * the enemy half. Outfield (the U around the enemy half: side arms and back strip): from the ball toward the centre
   * of the enemy half, i.e. inward along X on an arm, toward the court from the back strip.
   */
  _attackDirection(out, from) {
    const court = game.court, o = this.owner;
    if (!court || !(o.team >= 0)) return out.set(Math.sin(o.yaw || 0), 0, Math.cos(o.yaw || 0));
    if (o.zone === ZONE.OUTFIELD) {
      const cz = -court.sideSign(o.team) * court.halfL * 0.5; // centre of the opponent's half
      out.set(-(from ? from.x : o.position.x), 0, cz - (from ? from.z : o.position.z));
      if (out.lengthSq() > 1e-6) return out.normalize();
    }
    return out.set(0, 0, -court.sideSign(o.team));
  }
}

registerAbility('gale.silent_footsteps', GaleSilentFootsteps);
registerAbility('gale.optical_camouflage', GaleOpticalCamouflage);
registerAbility('gale.shadow_strike', GaleShadowStrike);
