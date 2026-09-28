// ---------------------------------------------------------------------------------------------------------------
// Health - HP, hit pipeline and elimination of one player (web port of the Unity PlayerHealth).
//
// receiveHit(hit):
//   1. not targetable (outfield, eliminated, pending)            -> 'ignored'
//   2. 'invulnerable' status (unless hit.unblockable) or a hit filter cancels (shields, evasion, damage scaling)
//                                                                -> 'negated'
//   3. Elsa's rule: a hit on a FROZEN player forces elimination (hit.forceEliminate)
//   4. damage; HP left and not forced                            -> 'damaged' (knockback impulse, hit reaction, flinch)
//   5. lethal: elimination interceptors (priority order) may answer 'delayed' (Chrono: pending, HP shown 0) or
//      'prevented' (Specter: rewound); otherwise                  -> 'eliminated'
// Elimination: fsm.incapacitate('eliminated') (drops the ball, interrupts abilities, freezes the motor), timed
// statuses cleared, impulse-driven ragdoll at the hit point, EV.PlayerEliminated.
// All timing on scaled time.
// ---------------------------------------------------------------------------------------------------------------
import * as THREE from 'three';
import { game } from '../game.js';
import { EV } from '../core/events.js';
import { DEFAULT_MAX_HP, STANDARD_HIT_DAMAGE, BALL_MASS } from '../core/constants.js';
import { ragdollImpulseParts } from './player_math.js';

/** receiveHit / applyDamage / eliminate outcomes. */
export const HIT_OUTCOME = Object.freeze({
  IGNORED: 'ignored', NEGATED: 'negated', DAMAGED: 'damaged', ELIMINATED: 'eliminated', DELAYED: 'delayed', PREVENTED: 'prevented',
});

/** Elimination causes used by this module (free strings are accepted from other modules, e.g. Match's 'caught'). */
export const ELIM_CAUSE = Object.freeze({
  BALL_HIT: 'ballHit', ABILITY: 'ability', FROZEN: 'frozen', DELAYED_IMPACT: 'delayedImpact', CAUGHT: 'caught', OTHER: 'other',
});

const _dir = new THREE.Vector3();

export class Health {
  static defaults = {
    nonLethalHitStun: 0.2,        // flinch (s) after a non-lethal hit; interrupts a wind-up / catch
    defaultBallKnockback: 1.4,    // m/s shove of a non-lethal ball hit when combat does not specify one
    ballMass: BALL_MASS,          // kg, regulation foam ball
    ragdollImpulseFactor: 3,      // readability factor on the real momentum transfer (0.35 kg barely moves 75 kg)
    ragdollUpwardBias: 0.15,      // extra upward share so the body lifts slightly off its feet
    maxRagdollImpulse: 90,        // N*s cap
    defaultEliminationImpulse: 22, // N*s when an elimination has no ball (ability, delayed impact): backward stagger
    revivalGraceSeconds: 1,       // invulnerability after a revive (not hit on the spot)
    maxPendingSeconds: 6,         // a delayed elimination nobody resolves is committed after this (safety net)
  };

  /**
   * @param {import('./player.js').Player} player
   * @param {number} maxHp
   */
  constructor(player, maxHp = DEFAULT_MAX_HP) {
    this.player = player;
    this.tuning = { ...Health.defaults };
    this.maxHp = Math.max(1, Number.isFinite(maxHp) ? maxHp : DEFAULT_MAX_HP);
    this.hp = this.maxHp;
    this.isEliminated = false;
    /** Elimination delayed by an interceptor (Chrono): HP reads 0 until committed or cancelled. */
    this.pending = false;
    /** Last elimination context (for delayed commits / UI). */
    this.lastElimination = null;
    this._filters = [];
    this._interceptors = [];
    this._hpBeforePending = this.maxHp;
    this._pendingTime = 0;
    this._eliminating = false;
  }

  /**
   * The body is up and playing: an infield player with HP (not pending), or an eliminated player back on their feet in
   * the outfield (they still pick up, receive passes and throw). Ragdolling / pending players are not alive.
   * "Still in the infield" is `!isEliminated` (and `player.isTargetable` for hittability); Match revives with revive().
   */
  get isAlive() {
    if (this.pending) return false;
    if (!this.isEliminated) return this.hp > 0;
    const p = this.player;
    return !!p && p.zone === 'outfield' && !(p.fsm && p.fsm.incapReason === 'eliminated');
  }
  get normalized() { return this.maxHp > 0 ? this.hp / this.maxHp : 0; }
  get pendingTime() { return this.pending ? this._pendingTime : 0; }

  // ------------------------------------------------------------------ filters & interceptors
  /**
   * @param {{priority?:number, filter:(hit:object)=>any}} f runs before damage; set hit.cancelled = true (or return
   *        true / 'negated') to negate the hit, or edit hit.damage / hit.knockback / hit.forceEliminate.
   */
  addHitFilter(f) {
    if (!f || this._filters.includes(f)) return f;
    insertByPriority(this._filters, f);
    return f;
  }
  removeHitFilter(f) { const i = this._filters.indexOf(f); if (i >= 0) this._filters.splice(i, 1); }

  /**
   * @param {{priority?:number, intercept:(health:Health, ctx:object)=>string}} i asked before a lethal outcome:
   *        return 'delayed' (pending, see commitPending / setPending(false)), 'prevented', or anything else to pass.
   */
  addInterceptor(i) {
    if (!i || this._interceptors.includes(i)) return i;
    insertByPriority(this._interceptors, i);
    return i;
  }
  removeInterceptor(i) { const k = this._interceptors.indexOf(i); if (k >= 0) this._interceptors.splice(k, 1); }

  // ------------------------------------------------------------------ hits
  /**
   * @param {{ball?, attacker?, victim?, point?, normal?, velocity?, damage?, unblockable?, forceEliminate?,
   *          knockback?, isAbility?, cancelled?}} hit (mutated by filters)
   * @returns {string} HIT_OUTCOME
   */
  receiveHit(hit) {
    const p = this.player;
    if (!hit || !p) return HIT_OUTCOME.IGNORED;
    if (!hit.victim) hit.victim = p;
    hit.point = toVector(hit.point) || p.chestPosition.clone();
    if (!p.isTargetable || this.pending) return HIT_OUTCOME.IGNORED;
    if (hit.damage === undefined || hit.damage === null || !Number.isFinite(hit.damage)) hit.damage = STANDARD_HIT_DAMAGE;
    if (hit.knockback === undefined || hit.knockback === null) hit.knockback = hit.ball ? this.tuning.defaultBallKnockback : 0;

    // 1. Filters: invulnerability / evasion, shields, damage scaling.
    if (this._runFilters(hit)) return HIT_OUTCOME.NEGATED;

    // 2. Elsa: a second hit on a frozen player eliminates.
    const frozenKill = !!(p.status && p.status.has('frozen'));
    if (frozenKill) hit.forceEliminate = true;

    // 3. Damage.
    const damage = Math.max(0, hit.damage);
    const hpBefore = this.hp;
    this.hp = Math.max(0, this.hp - damage);
    if (this.hp > 0 && !hit.forceEliminate) {
      if (damage > 0) this._publishDamaged(hit.attacker || null, damage, hit.point);
      this._hitReaction(hit, damage);
      return HIT_OUTCOME.DAMAGED;
    }

    // 4. Lethal: interceptors, then elimination.
    const cause = frozenKill ? ELIM_CAUSE.FROZEN : hit.isAbility ? ELIM_CAUSE.ABILITY : ELIM_CAUSE.BALL_HIT;
    const ctx = {
      victim: p, attacker: hit.attacker || null, cause, impulse: this._ballImpulse(hit), point: hit.point.clone(),
      hit, hpBefore,
    };
    return this._runElimination(ctx, hpBefore);
  }

  /**
   * Ability damage without a ball (shockwaves, turrets...). Filters apply; a ball-type cause on a frozen player
   * follows Elsa's rule too.
   * @returns {string} HIT_OUTCOME
   */
  applyDamage(amount, source = null, cause = ELIM_CAUSE.ABILITY) {
    const p = this.player;
    if (!p || this.isEliminated || !this.isAlive || this.pending) return HIT_OUTCOME.IGNORED;
    if (!p.isTargetable || !(amount > 0)) return HIT_OUTCOME.IGNORED;
    const hit = {
      ball: null, attacker: source, victim: p, point: p.chestPosition.clone(), normal: null, velocity: null,
      damage: amount, unblockable: false, forceEliminate: false, knockback: 0, isAbility: true, cancelled: false,
    };
    if (this._runFilters(hit)) return HIT_OUTCOME.NEGATED;
    const frozenKill = cause === ELIM_CAUSE.BALL_HIT && !!(p.status && p.status.has('frozen'));
    if (frozenKill) hit.forceEliminate = true;
    const damage = Math.max(0, hit.damage);
    const hpBefore = this.hp;
    this.hp = Math.max(0, this.hp - damage);
    if (this.hp > 0 && !hit.forceEliminate) {
      if (damage > 0) this._publishDamaged(source, damage, hit.point);
      this._hitReaction(hit, damage);
      return HIT_OUTCOME.DAMAGED;
    }
    const ctx = {
      victim: p, attacker: source, cause: frozenKill ? ELIM_CAUSE.FROZEN : cause || ELIM_CAUSE.ABILITY,
      impulse: this._defaultImpulse(source, new THREE.Vector3()), point: hit.point.clone(), hit, hpBefore,
    };
    return this._runElimination(ctx, hpBefore);
  }

  /**
   * Direct elimination (rules, tackles, catch-eliminates-thrower). Interceptors apply unless `bypass`.
   * @param {string} cause
   * @param {object|null} attacker
   * @param {THREE.Vector3} [impulse] ragdoll impulse (N*s); a default backward stagger when omitted
   * @param {THREE.Vector3} [point] impulse point (chest when omitted)
   * @param {boolean} [bypass]
   * @returns {string} HIT_OUTCOME
   */
  eliminate(cause = ELIM_CAUSE.OTHER, attacker = null, impulse = null, point = null, bypass = false) {
    const p = this.player;
    if (!p || this.isEliminated) return HIT_OUTCOME.IGNORED;
    const ctx = {
      victim: p, attacker, cause,
      impulse: toVector(impulse) || this._defaultImpulse(attacker, new THREE.Vector3()),
      point: toVector(point) || p.chestPosition.clone(), hit: null,
      hpBefore: this.hp > 0 ? this.hp : (this.pending ? this._hpBeforePending : this.maxHp),
    };
    if (bypass) { this._finalize(ctx); return HIT_OUTCOME.ELIMINATED; }
    return this._runElimination(ctx, ctx.hpBefore);
  }

  /** Commits a delayed (pending) elimination for real (Chrono's delay ran out). */
  commitPending(ctx = null) {
    if (this.isEliminated || !this.pending) return HIT_OUTCOME.IGNORED;
    const p = this.player;
    const last = this.lastElimination;
    const c = {
      victim: p,
      attacker: (ctx && ctx.attacker) || (last && last.attacker) || null,
      cause: ELIM_CAUSE.DELAYED_IMPACT,
      impulse: toVector(ctx && ctx.impulse) || toVector(last && last.impulse) || this._defaultImpulse(null, new THREE.Vector3()),
      point: toVector(ctx && ctx.point) || p.chestPosition.clone(),
      hit: (ctx && ctx.hit) || (last && last.hit) || null,
      hpBefore: this._hpBeforePending,
    };
    // The delay already was the interceptor's say: resolve for real.
    this._finalize(c);
    return HIT_OUTCOME.ELIMINATED;
  }

  /** Marks / clears a pending elimination. Clearing without a revive restores the HP from before the fatal hit. */
  setPending(b) {
    if (b) {
      if (this.isEliminated) return;
      if (!this.pending) { if (this.hp > 0) this._hpBeforePending = this.hp; this._pendingTime = 0; }
      this.pending = true;
      this.hp = 0; // shown as "pending" by the HUD
      return;
    }
    if (!this.pending) return;
    this.pending = false;
    this._pendingTime = 0;
    if (!this.isEliminated && this.hp <= 0) this.hp = clampHp(this._hpBeforePending, this.maxHp);
  }

  /** Brings the player back (HP = frac x maxHp, at least 1). Stands a still-ragdolling player back up. */
  revive(frac = 1) {
    const p = this.player;
    if (!p) return;
    const wasEliminated = this.isEliminated;
    const f = Number.isFinite(frac) ? Math.min(1, Math.max(0, frac)) : 1;
    this.hp = clampHp(this.maxHp * f, this.maxHp);
    this.isEliminated = false;
    this.pending = false;
    this._pendingTime = 0;
    const fsm = p.fsm;
    if (fsm && fsm.is('incapacitated') && fsm.incapReason === 'eliminated') {
      fsm.release('eliminated');
      const a = p.avatar;
      if (a && a.recoverFromRagdoll) { try { a.recoverFromRagdoll(); } catch (e) { console.error('[health] recoverFromRagdoll failed', e); } }
    }
    if (wasEliminated && this.tuning.revivalGraceSeconds > 0 && p.status) {
      p.status.apply('invulnerable', this.tuning.revivalGraceSeconds, 1, this);
    }
  }

  /** Sets HP without events (rewinds). */
  setHpSilently(hp) {
    if (!Number.isFinite(hp)) return;
    this.hp = Math.min(this.maxHp, Math.max(0, hp));
  }

  /** Changes max HP (refill when asked, else clamp). */
  setMaxHp(maxHp, refill = false) {
    if (!(maxHp > 0)) return;
    this.maxHp = maxHp;
    if (refill && !this.isEliminated) this.hp = maxHp;
    else this.hp = Math.min(this.hp, maxHp);
  }

  resetForRound() {
    this.hp = this.maxHp;
    this.isEliminated = false;
    this.pending = false;
    this._pendingTime = 0;
    this._hpBeforePending = this.maxHp;
    this.lastElimination = null;
  }

  /** Scaled dt. Safety net: a pending elimination whose interceptor vanished is committed after maxPendingSeconds. */
  update(dt) {
    if (!this.pending || this.isEliminated || !(dt > 0)) return;
    this._pendingTime += dt;
    if (this._pendingTime >= this.tuning.maxPendingSeconds) this.commitPending(this.lastElimination);
  }

  // ------------------------------------------------------------------ internals
  _runFilters(hit) {
    const p = this.player;
    if (hit.cancelled) return true;
    if (!hit.unblockable && p.status && p.status.has('invulnerable')) {
      hit.cancelled = true;
      if (!hit.cancelReason) hit.cancelReason = 'invulnerable';
      return true;
    }
    if (this._filters.length === 0) return false;
    // Filters may remove themselves (one-shot shields): iterate over a snapshot.
    const list = this._filters.slice();
    for (const f of list) {
      let r;
      try {
        r = typeof f === 'function' ? f(hit) : f.filter ? f.filter(hit) : undefined;
      } catch (e) {
        console.error('[health] hit filter threw', e);
        continue;
      }
      if (r === true || r === 'negated' || r === 'cancel' || r === 'cancelled') hit.cancelled = true;
      if (hit.cancelled) return true;
    }
    return false;
  }

  _runElimination(ctx, hpBefore) {
    this.lastElimination = ctx;
    if (this._interceptors.length > 0) {
      const list = this._interceptors.slice();
      for (const i of list) {
        let decision;
        try {
          decision = typeof i === 'function' ? i(this, ctx) : i.intercept ? i.intercept(this, ctx) : undefined;
        } catch (e) {
          console.error('[health] elimination interceptor threw', e);
          continue;
        }
        if (decision === HIT_OUTCOME.DELAYED) {
          // HP to give back if the delay ends in a cancellation (Chrono: a teammate caught a ball in time).
          const restore = hpBefore > 0 ? hpBefore : (this.pending ? this._hpBeforePending : this.maxHp);
          this.setPending(true); // no-op if the interceptor already marked it
          this._hpBeforePending = restore;
          this.hp = 0;
          this.lastElimination = ctx;
          return HIT_OUTCOME.DELAYED;
        }
        if (decision === HIT_OUTCOME.PREVENTED) {
          // The interceptor (Specter's Time Reversal) normally restores HP itself via setHpSilently.
          if (!this.isEliminated && this.hp <= 0) this.hp = clampHp(hpBefore, this.maxHp);
          return HIT_OUTCOME.PREVENTED;
        }
      }
    }
    this._finalize(ctx);
    return HIT_OUTCOME.ELIMINATED;
  }

  _finalize(ctx) {
    const p = this.player;
    if (this.isEliminated || this._eliminating || !p) return;
    this._eliminating = true;
    try {
      this.isEliminated = true;
      this.pending = false;
      this._pendingTime = 0;
      this.hp = 0;
      this.lastElimination = ctx;
      // Lock first (drops the ball, interrupts abilities, freezes the motor)...
      if (p.fsm) p.fsm.incapacitate('eliminated');
      // ...then clear timed effects and hard control (frozen ice shatters, stuns end; passive markers stay).
      if (p.status && p.status.clearForReset) p.status.clearForReset();
      // Safety: the ball must never stay glued to a ragdoll.
      const c = p.combat;
      if (c && c.hasBall && c.dropBall && p.fsm) c.dropBall(p.fsm.dropBallVelocity());
      // Impulse-driven ragdoll at the hit point (falls back to the defeat animation).
      const a = p.avatar;
      if (a) {
        try {
          if (a.enableRagdoll) a.enableRagdoll(ctx.impulse, ctx.point);
          else if (a.defeat) a.defeat();
        } catch (e) { console.error('[health] ragdoll failed', e); }
      }
      game.events.emit(EV.PlayerEliminated, {
        player: p, attacker: ctx.attacker || null, cause: ctx.cause, impulse: ctx.impulse, point: ctx.point,
      });
    } finally {
      this._eliminating = false;
    }
  }

  /** Knockback shove + hit animation; a flinch stun only when damage actually landed (pure shoves never interrupt). */
  _hitReaction(hit, damage) {
    const p = this.player;
    // Direction of travel of the ball (planar), else away from the contact normal, else backward.
    _dir.set(0, 0, 0);
    if (hit.velocity) _dir.set(hit.velocity.x || 0, 0, hit.velocity.z || 0);
    if (_dir.lengthSq() < 1e-4 && hit.normal) _dir.set(-(hit.normal.x || 0), 0, -(hit.normal.z || 0));
    if (_dir.lengthSq() < 1e-4 && hit.attacker && hit.attacker.position) _dir.set(p.position.x - hit.attacker.position.x, 0, p.position.z - hit.attacker.position.z);
    if (_dir.lengthSq() < 1e-4) _dir.set(-Math.sin(p.yaw || 0), 0, -Math.cos(p.yaw || 0));
    _dir.normalize();
    const kb = Number.isFinite(hit.knockback) ? hit.knockback : 0;
    if (kb > 0 && p.motor) p.motor.addImpulse(_dir.multiplyScalar(kb));
    const a = p.avatar;
    if (a && a.playHit) { try { a.playHit(_dir.clone().normalize()); } catch (e) { console.error('[health] playHit failed', e); } }
    if (damage > 0 && this.tuning.nonLethalHitStun > 0 && p.fsm) p.fsm.stun(this.tuning.nonLethalHitStun);
  }

  _publishDamaged(attacker, damage, point) {
    game.events.emit(EV.PlayerDamaged, { player: this.player, attacker, damage, remainingHp: this.hp, point });
  }

  /** Ragdoll impulse (N*s) from a ball hit: v x m x readability factor + upward share, capped. */
  _ballImpulse(hit) {
    const v = hit.velocity;
    const speed = v ? Math.hypot(v.x || 0, v.y || 0, v.z || 0) : 0;
    if (speed < 1e-2) return this._defaultImpulse(hit.attacker, new THREE.Vector3());
    const t = this.tuning;
    const parts = ragdollImpulseParts(speed, t.ballMass, t.ragdollImpulseFactor, t.ragdollUpwardBias, t.maxRagdollImpulse);
    return new THREE.Vector3(v.x || 0, v.y || 0, v.z || 0).multiplyScalar(parts.planarScale).add(new THREE.Vector3(0, parts.up, 0));
  }

  /** Backward stagger away from the source (or backward from the facing) when there is no ball. */
  _defaultImpulse(source, out) {
    const p = this.player;
    if (source && source.position && source !== p) out.set(p.position.x - source.position.x, 0, p.position.z - source.position.z);
    else out.set(0, 0, 0);
    if (out.lengthSq() < 1e-4) out.set(-Math.sin(p.yaw || 0), 0, -Math.cos(p.yaw || 0));
    out.normalize();
    out.y = this.tuning.ragdollUpwardBias;
    return out.multiplyScalar(this.tuning.defaultEliminationImpulse);
  }
}

/**
 * Copy of a {x,y,z} (THREE.Vector3 or plain object) as a new Vector3, or null when missing / non-finite /
 * zero-length (a zero impulse or an unset point falls back to the caller's default).
 */
function toVector(v) {
  if (!v || !Number.isFinite(v.x) || !Number.isFinite(v.y) || !Number.isFinite(v.z)) return null;
  if (v.x * v.x + v.y * v.y + v.z * v.z < 1e-12) return null;
  return new THREE.Vector3(v.x, v.y, v.z);
}

function clampHp(hp, maxHp) { return Math.min(maxHp, Math.max(1, Number.isFinite(hp) ? hp : maxHp)); }

/** Stable insertion by priority (lower first; equal priorities keep registration order). */
function insertByPriority(list, item) {
  const pr = Number.isFinite(item.priority) ? item.priority : 0;
  let i = list.length;
  while (i > 0 && (Number.isFinite(list[i - 1].priority) ? list[i - 1].priority : 0) > pr) i--;
  list.splice(i, 0, item);
}
