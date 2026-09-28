// ---------------------------------------------------------------------------------------------------------------
// AbilityBase (kernel) - web port of the Unity AbilityBase. Data-driven: each ability is a class registered by id;
// its tuning comes from `static defaults` merged with the roster definition's `params`.
//
//            tryActivate()                castTime elapsed              duration elapsed / endAbility()
//  ready ─────────────────► casting ─────────────────────► active ─────────────────────────────► cooldown ──► ready
//          onCastStarted()           onCast()               onTick(dt) every frame     onEnd() + onCooldown()   onReady()
//                     interrupt(): onInterrupt() + onEnd(true) + cooldown
//  Passives: equip() -> onEquip() once, then onTick(dt) every frame; never cast.
// Spec hooks: onCast / onTick / onInterrupt / onCooldown.
// ---------------------------------------------------------------------------------------------------------------
import { game } from '../game.js';
import { Cooldown } from '../core/timers.js';
import { EV } from '../core/events.js';
import { ZONE } from '../core/constants.js';

export const SLOT = Object.freeze({ PASSIVE: 'passive', SKILL: 'skill', ULTIMATE: 'ultimate' });
export const PHASE = Object.freeze({ READY: 'ready', CASTING: 'casting', ACTIVE: 'active', COOLDOWN: 'cooldown', DISABLED: 'disabled' });
export const FAIL = Object.freeze({
  COOLDOWN: 'cooldown', ULT_NOT_READY: 'ultNotReady', CANNOT_ACT: 'cannotAct', SILENCED: 'silenced', REQUIRES_BALL: 'requiresBall',
  NO_TARGET: 'noTarget', NOT_INFIELD: 'notInfield', BUSY: 'busy', NOT_PLAYING: 'notPlaying', PASSIVE: 'passive', CUSTOM: 'custom',
});
export const INTERRUPT = Object.freeze({ STUNNED: 'stunned', ELIMINATED: 'eliminated', FROZEN: 'frozen', ROUND_ENDED: 'roundEnded', CANCELLED: 'cancelled' });
export const AI_HINT = Object.freeze({
  NEVER: 'never', ANYTIME: 'anytime', HOLDING_BALL: 'holdingBall', THREATENED: 'threatened', ENEMY_IN_RANGE: 'enemyInRange',
  TEAMMATE_OUTFIELD: 'teammateOutfield', BALLS_LOOSE: 'ballsLoose', LOSING: 'losing',
});

/** id -> class registry, filled by abilities/heroes/*.js via registerAbility(). */
export const abilityRegistry = new Map();
export function registerAbility(id, cls) { abilityRegistry.set(id, cls); return cls; }

export class AbilityBase {
  /** Override in subclasses: tuning defaults (merged with roster `params`). */
  static defaults = {};

  /**
   * @param {import('../gameplay/player.js').Player} owner
   * @param {object} def roster ability definition {id,name,nameZh,desc,descZh,slot,cooldown,castTime,duration,ultCost,requiresBall,usableFromOutfield,interruptible,aiHint,aiWeight,params}
   */
  constructor(owner, def) {
    this.owner = owner;
    this.def = def;
    this.params = { ...this.constructor.defaults, ...(def.params || {}) };
    this.cooldown = new Cooldown(def.cooldown || 0);
    this.phase = this.isPassive ? PHASE.DISABLED : PHASE.READY;
    this._castRemaining = 0;
    this._activeRemaining = 0;
    this._hold = false;
    this._equipped = false;
    this._unsubs = [];
  }

  get id() { return this.def.id; }
  get name() { return this.def.name; }
  get slot() { return this.def.slot; }
  get isPassive() { return this.def.slot === SLOT.PASSIVE; }
  get isReady() { return this.phase === PHASE.READY; }
  get isCasting() { return this.phase === PHASE.CASTING; }
  get isActive() { return this.phase === PHASE.ACTIVE; }
  get isBusy() { return this.phase === PHASE.CASTING || this.phase === PHASE.ACTIVE; }
  get castTime() { return this.def.castTime || 0; }
  get duration() { return this.def.duration || 0; }
  get cooldownDuration() { return this.def.cooldown || 0; }
  get cooldownRemaining() { return this.cooldown.remaining; }
  get cooldownNormalized() { return this.cooldown.normalized; }
  get activeRemaining() { return this.phase === PHASE.ACTIVE ? Math.max(0, this._activeRemaining) : 0; }
  get castProgress() { return this.phase === PHASE.CASTING && this.castTime > 0 ? 1 - Math.min(1, Math.max(0, this._castRemaining / this.castTime)) : 0; }
  /** Scaled gameplay clock. */
  get now() { return game.time.now; }

  initialize() { this.onInitialize(); this.equip(); }

  equip() { if (this._equipped) return; this._equipped = true; this.onEquip(); }

  unequip() {
    if (!this._equipped) return;
    if (this.isBusy) this.interrupt(INTERRUPT.CANCELLED);
    this.onUnequip();
    for (const u of this._unsubs) u();
    this._unsubs = [];
    this._equipped = false;
  }

  /** @returns {string|null} failure reason or null when allowed */
  canActivate() {
    const o = this.owner;
    if (this.isPassive) return FAIL.PASSIVE;
    if (this.isBusy) return FAIL.BUSY;
    if (this.phase === PHASE.COOLDOWN) return FAIL.COOLDOWN;
    if (game.match && !game.match.isPlaying) return FAIL.NOT_PLAYING;
    if (!o.canAct) return FAIL.CANNOT_ACT;
    if (o.status && o.status.has('silenced')) return FAIL.SILENCED;
    if (o.zone !== ZONE.INFIELD && !this.def.usableFromOutfield) return FAIL.NOT_INFIELD;
    if (this.def.requiresBall && !(o.combat && o.combat.hasBall)) return FAIL.REQUIRES_BALL;
    if (this.slot === SLOT.ULTIMATE && !(o.abilities && o.abilities.isUltimateReady(this.def.ultCost ?? 1))) return FAIL.ULT_NOT_READY;
    return this.canActivateCustom();
  }

  tryActivate() {
    const reason = this.canActivate();
    if (reason) {
      if (reason !== FAIL.PASSIVE) game.events.emit(EV.AbilityFailed, { player: this.owner, ability: this, slot: this.slot, reason });
      return false;
    }
    if (this.slot === SLOT.ULTIMATE) this.owner.abilities.consumeUltimate(this.def.ultCost ?? 1);
    this._hold = false;
    if (this.castTime > 0) {
      this.phase = PHASE.CASTING;
      this._castRemaining = this.castTime;
      this.onCastStarted();
    } else {
      this._fire();
    }
    return true;
  }

  tick(dt) {
    switch (this.phase) {
      case PHASE.DISABLED:
        if (this.isPassive && this._equipped) this.onTick(dt);
        break;
      case PHASE.CASTING:
        this._castRemaining -= dt;
        this.onCastTick(dt, this.castProgress);
        if (this.phase === PHASE.CASTING && this._castRemaining <= 0) this._fire();
        break;
      case PHASE.ACTIVE:
        this.onTick(dt);
        if (this.phase !== PHASE.ACTIVE) break;
        if (!this._hold) { this._activeRemaining -= dt; if (this._activeRemaining <= 0) this._end(false); }
        break;
      case PHASE.COOLDOWN: {
        const done = this.cooldown.tick(dt);
        this.onCooldownTick(dt);
        if (done) { this.phase = PHASE.READY; this.onReady(); }
        break;
      }
      default: break;
    }
  }

  /** Stun/freeze are ignored by non-interruptible abilities; elimination/round end always stop them. */
  interrupt(reason) {
    if (!this.isBusy) return;
    const soft = reason === INTERRUPT.STUNNED || reason === INTERRUPT.FROZEN;
    if (soft && this.def.interruptible === false) return;
    this.onInterrupt(reason);
    this._end(true);
  }

  resetCooldown() { this.cooldown.reset(); if (this.phase === PHASE.COOLDOWN) { this.phase = PHASE.READY; this.onReady(); } }

  resetForRound() {
    if (this.isBusy) this.interrupt(INTERRUPT.ROUND_ENDED);
    if (this.isBusy) this._end(true);
    this.cooldown.reset();
    if (!this.isPassive) this.phase = PHASE.READY;
    this.onRoundReset();
  }

  // ------------------------------------------------------------------ hooks
  onInitialize() {}
  onEquip() {}
  onUnequip() {}
  /** @returns {string|null} */
  canActivateCustom() { return null; }
  onCastStarted() {}
  onCastTick(dt, progress) {}
  /** SPEC HOOK (required): the effect fires. */
  onCast() { throw new Error(`${this.constructor.name}.onCast not implemented`); }
  /** SPEC HOOK: every frame while active (and every frame for passives). */
  onTick(dt) {}
  /** SPEC HOOK: channel/active interrupted. */
  onInterrupt(reason) {}
  /** SPEC HOOK: cooldown started. */
  onCooldown() {}
  onCooldownTick(dt) {}
  onReady() {}
  onEnd(interrupted) {}
  onRoundReset() {}

  /** AI utility 0..1 (default from aiHint). ctx: see ai/bot.js buildAbilityContext(). */
  evaluateAI(ctx) {
    const w = this.def.aiWeight ?? 0.5;
    switch (this.def.aiHint) {
      case AI_HINT.NEVER: return 0;
      case AI_HINT.ANYTIME: return w * 0.5;
      case AI_HINT.HOLDING_BALL: return ctx.holdingBall && ctx.nearestEnemy ? w : 0;
      case AI_HINT.THREATENED: return ctx.incomingBall && ctx.incomingTime < 0.6 ? w : 0;
      case AI_HINT.ENEMY_IN_RANGE: return ctx.nearestEnemy && ctx.nearestEnemyDistance < 8 ? w : 0;
      case AI_HINT.TEAMMATE_OUTFIELD: return ctx.teammatesOutfield > 0 ? w : 0;
      case AI_HINT.BALLS_LOOSE: return ctx.freeBallsNearby > 0 && !ctx.holdingBall ? w : 0;
      case AI_HINT.LOSING: return ctx.alliesInfield < ctx.enemiesInfield ? w : 0;
      default: return 0;
    }
  }

  // ------------------------------------------------------------------ helpers
  endAbility() { if (this.isBusy) this._end(false); }
  /** Stay active after onCast until endAbility() (e.g. while a projectile flies). */
  holdActive() { this._hold = true; }
  extendActive(seconds) { this._activeRemaining += seconds; }
  /** Subscribe to a game event while equipped (auto-removed on unequip). */
  listen(eventName, fn) { this._unsubs.push(game.events.on(eventName, fn)); }
  isEnemy(p) { return game.areEnemies(this.owner, p); }
  isAlly(p) { return game.areTeammates(this.owner, p); }

  _fire() {
    this.phase = PHASE.ACTIVE;
    this._activeRemaining = this.duration;
    game.events.emit(EV.AbilityCast, { player: this.owner, ability: this, slot: this.slot });
    this.onCast();
    if (this.phase === PHASE.ACTIVE && !this._hold && this._activeRemaining <= 0) this._end(false);
  }

  _end(interrupted) {
    if (!this.isBusy) return;
    this._hold = false;
    this._activeRemaining = 0;
    this._castRemaining = 0;
    this.phase = PHASE.COOLDOWN;
    this.onEnd(interrupted);
    game.events.emit(EV.AbilityEnded, { player: this.owner, ability: this, slot: this.slot, interrupted });
    const cd = this.cooldownDuration;
    if (cd > 0) { this.cooldown.start(cd); this.onCooldown(); }
    else { this.cooldown.reset(); this.phase = PHASE.READY; this.onReady(); }
  }
}
