// ---------------------------------------------------------------------------------------------------------------
// Bear (守護者 Guardian) - abilities.
//   PASSIVE  Iron Mitts      (鐵手套)   Perfect Catch window x1.5 (0.225 s instead of 0.15 s).
//   SKILL    Magnetic Pull   (磁力牽引) 1.5 s, 5 m field around his palm pulls enemy balls (and, with empty hands, a
//                                       loose ball) into his hands. CD 15 s.
//   ULTIMATE Aegis Barrier   (神盾屏障) 6 s energy wall at centre court on his side blocking every enemy throw.
// World objects live in ../shared/bearMagneticField.js and ../shared/bearAegisWall.js.
// ---------------------------------------------------------------------------------------------------------------
import { game } from '../../game.js';
import { EV } from '../../core/events.js';
import { IRON_MITTS_MULTIPLIER, TEAM } from '../../core/constants.js';
import { AbilityBase, registerAbility } from '../abilityBase.js';
import { BearMagneticField, MAGNET_DEFAULTS } from '../shared/bearMagneticField.js';
import { BearAegisWall, AEGIS_DEFAULTS } from '../shared/bearAegisWall.js';
import { BALL, allBalls, matchBalls, throwerTeam, fx, sfx, isAlive, isInfield } from '../shared/screwsGadgetKit.js';

// ================================================================== PASSIVE: Iron Mitts

/**
 * Sets owner.combat.perfectWindowMul to the Iron Mitts multiplier while equipped (restores the previous value on
 * unequip). Perfect catches by Bear get a small metallic spark + clang on top of the normal perfect-catch juice.
 */
export class BearIronMitts extends AbilityBase {
  static defaults = {
    multiplier: IRON_MITTS_MULTIPLIER, // 1.5 -> 0.225 s perfect window
    sparkColor: 0xffd27a,
  };

  onInitialize() {
    this._combat = null;   // combat component the multiplier was applied to
    this._prev = 1;        // its value before Iron Mitts
  }

  onEquip() {
    this._apply();
    this.listen(EV.BallCaught, (e) => this._onCaught(e));
  }

  onUnequip() {
    if (this._combat) this._combat.perfectWindowMul = this._prev;
    this._combat = null;
  }

  /** Passive tick: applies as soon as the combat component exists (or was rebuilt). */
  onTick() {
    if ((this.owner.combat || null) !== this._combat) this._apply();
  }

  onRoundReset() { this._apply(); }

  _apply() {
    const c = this.owner.combat;
    if (!c) return;
    if (c !== this._combat) {
      if (this._combat) this._combat.perfectWindowMul = this._prev;
      this._prev = typeof c.perfectWindowMul === 'number' ? c.perfectWindowMul : 1;
      this._combat = c;
    }
    c.perfectWindowMul = this.params.multiplier;
  }

  _onCaught(e) {
    if (!e || e.catcher !== this.owner || e.quality !== 'perfect' || !e.point) return;
    fx('shieldImpact', e.point, { scale: 0.45, color: this.params.sparkColor });
    sfx('ironMitts', e.point, 0.9, 1);
  }

  onCast() { /* passive: never cast */ }
}

// ================================================================== SKILL: Magnetic Pull

/** Thin ability wrapper around BearMagneticField (the field does the steering, capture and visuals). */
export class BearMagneticPull extends AbilityBase {
  static defaults = { ...MAGNET_DEFAULTS };

  onInitialize() { this.field = new BearMagneticField(this.owner, this.params); }

  onCast() {
    this.field.start(this.duration > 0 ? this.duration : 1.5);
  }

  onTick(dt) { this.field.update(dt); }

  onEnd() { this.field.stop(); }

  onRoundReset() { this.field.stop(); }

  onUnequip() { this.field.dispose(); }

  /** Enemy balls about to arrive near Bear or his teammates -> high; a loose ball just out of reach -> low. */
  evaluateAI(ctx) {
    const o = this.owner;
    if (!ctx || !isInfield(o)) return 0;
    const w = this.def.aiWeight ?? 0.9;
    if (ctx.incomingBall && ctx.incomingTime < 0.9) return w;
    // Enemy balls already flying within (field radius + ~0.25 s of flight) of Bear: the field will get them.
    const reach = this.params.radius + 5;
    const balls = allBalls();
    for (let i = 0; i < balls.length; i++) {
      const b = balls[i];
      if (!b || b.state !== BALL.LIVE || b.unblockable) continue;
      const t = throwerTeam(b);
      if (t === TEAM.NONE || t === o.team) continue;
      if (b.position.distanceToSquared(o.position) < reach * reach && b.velocity.dot(o.position) - b.velocity.dot(b.position) > 0) return w * 0.75;
    }
    if (!ctx.holdingBall && ctx.freeBallsNearby > 0) {
      const mb = matchBalls();
      for (let i = 0; i < mb.length; i++) {
        const b = mb[i];
        if (!b || b.state !== BALL.FREE) continue;
        const d = b.position.distanceTo(o.position);
        if (d > 2.5 && d < this.params.radius) return w * 0.25;
      }
    }
    return 0;
  }
}

// ================================================================== ULTIMATE: Aegis Barrier

/** Deploys a BearAegisWall for the ability duration (6 s); it retracts (fade) when the ability ends. */
export class BearAegisBarrier extends AbilityBase {
  static defaults = { ...AEGIS_DEFAULTS };

  onInitialize() { this.wall = null; }

  onCast() {
    this._disposeWall();
    this.wall = new BearAegisWall(this.owner, this.params);
    this.wall.deploy();
    this.owner.avatar?.playThrow?.(this.owner.forward); // arm sweep that "throws up" the barrier
  }

  onTick() {
    if (this.wall && this.wall.state === 'disposed') { this.wall = null; this.endAbility(); }
  }

  onEnd() {
    if (this.wall) this.wall.retract(); // fades out on real time, then disposes itself
    this.wall = null;
  }

  onRoundReset() { this._disposeWall(); }

  onUnequip() { this._disposeWall(); }

  _disposeWall() {
    if (this.wall) this.wall.dispose();
    this.wall = null;
  }

  /** Worth it when several enemies are armed (a volley is coming) or when Bear's side is behind. */
  evaluateAI(ctx) {
    const o = this.owner;
    if (!ctx || !isInfield(o)) return 0;
    const w = this.def.aiWeight ?? 0.9;
    let holders = 0;
    const players = game.players;
    for (let i = 0; i < players.length; i++) {
      const p = players[i];
      if (!p || !game.areEnemies(o, p) || !isInfield(p) || !isAlive(p)) continue;
      if (p.combat && (p.combat.hasBall || p.combat.isCharging)) holders++;
    }
    const losing = ctx.alliesInfield < ctx.enemiesInfield;
    if (holders >= 2) return w;
    if (holders >= 1 && (losing || ctx.alliesInfield <= 1)) return w * 0.8;
    if (losing && ctx.timeLeft !== undefined && ctx.timeLeft < 30) return w * 0.5;
    return 0;
  }
}

registerAbility('bear.iron_mitts', BearIronMitts);
registerAbility('bear.magnetic_pull', BearMagneticPull);
registerAbility('bear.aegis_barrier', BearAegisBarrier);
