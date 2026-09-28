// ---------------------------------------------------------------------------------------------------------------
// Specter (閃避 / Evasion) - agility hero. Ids and spec numbers live in abilities/roster.js.
//   PASSIVE  specter.danger_sense       Danger Sense       red screen edges when a >= 90 km/h enemy ball locks on
//   SKILL    specter.precognition_dodge Precognition Dodge 0.8 s invulnerable sliding dodge that auto-evades (CD 8)
//   ULTIMATE specter.time_reversal      Time Reversal      eliminated within 3 s of casting -> rewind position + HP
// Gameplay runs on the scaled clock (this.now / dt); presentation goes through the guarded `fx` helpers so a missing
// or failing optional system (vfx, audio, renderer, avatar) can never break the ability logic.
// ---------------------------------------------------------------------------------------------------------------
import * as THREE from 'three';
import { game } from '../../game.js';
import { EV } from '../../core/events.js';
import { KMH_TO_MS } from '../../core/constants.js';
import { AbilityBase, registerAbility, FAIL, INTERRUPT } from '../abilityBase.js';
import { clampToPlayerZone } from '../abilityUtil.js';
import { findMostThreateningBall, computeEvadeDirection, predictImpactTime, fx } from '../shared/specterThreat.js';
import { dodgeUtility, reversalUtility } from '../shared/specterMath.js';

/** Cool silver-blue used by every Specter effect (roster colour 0xe8e8e8, pushed toward blue so it reads on wood). */
export const SPECTER_TINT = 0xc4d8ff;

// Module-scope temporaries (no per-frame allocations).
const _vel = new THREE.Vector3();
const _pos = new THREE.Vector3();
const _from = new THREE.Vector3();
const _burstDir = new THREE.Vector3();
const _zero = new THREE.Vector3();
/** Reused Motor.trySlide options: explicit speed/duration, `force` ignores the slide cooldown (ability slide). */
const _slideOpts = { speed: 0, duration: 0, force: true };

// ===============================================================================================================
// PASSIVE - Danger Sense
// ===============================================================================================================
/**
 * Watches every enemy throw (EV.BallThrown). A non-pass throw of at least `minSpeedKmh` that targets Specter (the
 * thrower's target or the ball's soft lock) - or, optionally, an untargeted one predicted to hit him - is tracked and
 * EV.DangerSense { active:true, timeToImpact, speedKmh } is published (the HUD draws the red screen edges for the
 * local player; the renderer gets a short 'danger' pulse). When no tracked ball is live any more (caught, blocked,
 * bounced, frozen in stasis, re-thrown by someone else) EV.DangerSense { active:false } follows.
 */
export class SpecterDangerSense extends AbilityBase {
  static defaults = {
    /** Spec: "high-speed ball" threshold (km/h). */
    minSpeedKmh: 90,
    /** Prediction horizon (s) for timeToImpact and untargeted detection. */
    predictHorizon: 2.5,
    /** Also warn for untargeted throws whose trajectory is predicted to hit Specter. */
    detectUntargeted: true,
    /** Safety net (s): a tracked ball is dropped after this long even if still live. */
    maxTrackTime: 4,
    /** Local-player 'danger' screen pulse. */
    localPulse: 0.45,
    pulseDuration: 0.35,
    /** Local-player warning cue volume (2D). */
    cueVolume: 0.45,
    /** Local-player sustained 'danger' look: base amount, rising to 1 as the soonest impact approaches ... */
    sustainBase: 0.45,
    /** ... over this many seconds before impact. */
    sustainRamp: 1.2,
  };

  onInitialize() {
    /** @type {{ball:any, launchTime:number|undefined, since:number, impactAt:number}[]} tracked incoming balls */
    this._tracked = [];
    this._lastBall = null;
    this._sustained = false;
    /** True while at least one locked-on high-speed ball is in flight (HUD / AI can read it). */
    this.active = false;
  }

  onEquip() {
    this.listen(EV.BallThrown, (e) => this._onThrown(e));
    this.listen(EV.RoundEnded, () => this._clear());
    this.listen(EV.PlayerEliminated, (e) => { if (e && e.player === this.owner) this._clear(); });
  }
  onUnequip() { this._clear(); }
  onRoundReset() { this._clear(); }
  /** Passives never cast. */
  onCast() {}
  evaluateAI() { return 0; }

  /** Passive tick (scaled dt): drop balls that stopped being live; publish active:false when none remain. */
  onTick() {
    const list = this._tracked;
    if (list.length === 0) return;
    const o = this.owner, p = this.params, now = this.now;
    const targetable = o.isTargetable !== false;
    let soonest = Infinity;
    for (let i = list.length - 1; i >= 0; i--) {
      const it = list[i], b = it.ball;
      const relaunched = it.launchTime !== undefined && b.launchTime !== it.launchTime;
      if (!targetable || b.state !== 'live' || relaunched || now - it.since > p.maxTrackTime) {
        this._lastBall = b;
        list.splice(i, 1);
      } else if (it.impactAt < soonest) {
        soonest = it.impactAt;
      }
    }
    if (list.length === 0) { this._emit(false, this._lastBall, 0, 0); return; }
    // Local player: the sustained red look intensifies as the soonest predicted impact approaches.
    if (o.isLocal) {
      const remaining = Math.max(0, soonest - now);
      const k = 1 - Math.min(1, remaining / Math.max(0.1, p.sustainRamp));
      fx.sustain('danger', p.sustainBase + (1 - p.sustainBase) * k);
      this._sustained = true;
    }
  }

  _onThrown(e) {
    const o = this.owner, ball = e && e.ball, p = this.params;
    if (!ball || e.isPass || !e.thrower || !this.isEnemy(e.thrower)) return;
    const kmh = Number.isFinite(e.speedKmh) ? e.speedKmh : (ball.speedKmh || 0);
    if (kmh < p.minSpeedKmh) return;
    if (o.isTargetable === false) return;
    const locked = e.target === o || ball.lockedTarget === o;
    let t = locked || p.detectUntargeted ? predictImpactTime(ball, o, p.predictHorizon) : null;
    if (!locked && t === null) return;
    if (t === null) {
      // Locked on but curving/too far for the horizon: straight-line estimate.
      const speed = Math.max(1, kmh * KMH_TO_MS);
      t = ball.position ? ball.position.distanceTo(o.position) / speed : 0;
    }
    let entry = null;
    for (const it of this._tracked) if (it.ball === ball) { entry = it; break; }
    if (!entry) { entry = { ball, launchTime: ball.launchTime, since: this.now, impactAt: 0 }; this._tracked.push(entry); }
    else { entry.launchTime = ball.launchTime; entry.since = this.now; }
    entry.impactAt = this.now + t;
    this._emit(true, ball, t, kmh);
  }

  _clear() {
    const had = this._tracked.length > 0 || this.active;
    const last = this._tracked.length ? this._tracked[this._tracked.length - 1].ball : this._lastBall;
    this._tracked.length = 0;
    if (had) this._emit(false, last, 0, 0);
  }

  _emit(active, ball, timeToImpact, speedKmh) {
    const o = this.owner, p = this.params;
    this.active = active;
    game.events.emit(EV.DangerSense, { player: o, ball: ball || null, timeToImpact, speedKmh, active });
    if (active && o.isLocal) {
      fx.pulse('danger', p.localPulse, p.pulseDuration);
      fx.sfx2D('danger', p.cueVolume, 1);
    }
    if (!active && this._sustained) { fx.sustain('danger', 0); this._sustained = false; }
  }
}

// ===============================================================================================================
// SKILL - Precognition Dodge
// ===============================================================================================================
/**
 * 0.8 s invulnerable sliding dodge. On cast: status 'invulnerable' for the window + a hit filter that cancels every
 * non-unblockable hit (Rayne's Hyperbeam is unblockable by definition and still connects), the catch stance is
 * dropped, and the body slides along the move input - or, without input (and always for bots), sideways out of the
 * flight line of the most threatening ball, toward the side with room inside the court. The slide goes through
 * Motor.trySlide (momentum-preserving, slide friction bleeds it); when the motor refuses (slide cooldown, airborne)
 * the ability emulates the slide by holding a friction-decaying planar velocity. Fading body afterimages trail it.
 * Unusable while 'dodgeDisabled' (Elsa's Absolute Zero) or 'rooted'.
 */
export class SpecterPrecognitionDodge extends AbilityBase {
  static defaults = {
    /** Planar kick (m/s). With 6.5 m/s² slide friction this covers ~4 m in 0.8 s. */
    dodgeSpeed: 7.5,
    /** Planar kick (m/s) when cast in the air (no floor to slide on). */
    airDodgeSpeed: 5,
    /** Deceleration (m/s²) of the emulated slide when the motor refuses a real one. */
    slideFriction: 6.5,
    /** Emulation stops below this speed (m/s). */
    minEmulatedSpeed: 0.5,
    /** Move-input magnitude above which a human's dodge follows the stick instead of auto-evading. */
    inputThreshold: 0.25,
    /** How far ahead (m) the dodge direction is checked against the court lines. */
    roomProbe: 2.5,
    /** Prediction horizon (s) when looking for the ball to dodge away from. */
    threatHorizon: 1.5,
    cancelCatchStance: true,
    /** Unblockable balls ignore evasion (spec). */
    unblockablePierces: true,
    /** Hit-filter priority (lower runs first): evasion before shields / damage scaling. */
    filterPriority: -100,
    afterimageInterval: 0.09,
    afterimageLifetime: 0.4,
    /** Scale of the burst where an evaded ball would have struck. */
    evadeBurstScale: 0.6,
    localPulse: 0.25,
    pulseDuration: 0.3,
    sfxVolume: 0.9,
    /** Bots dodge when the incoming ball is at most this many seconds out. */
    aiReactionWindow: 0.55,
    /** Below this speed (km/h) bots with free hands would rather catch. */
    aiPreferCatchBelowKmh: 65,
  };

  onInitialize() {
    /** Balls evaded during the current / last dodge (HUD, stats). */
    this.evaded = 0;
    this._dir = new THREE.Vector3(0, 0, 1);
    this._elapsed = 0;
    this._kick = 0;
    this._emulate = false;
    this._nextAfterimage = 0;
    this._health = null;
    this._lastEvadedBall = null;
    this._lastEvadedAt = -Infinity;
    this._filter = { priority: this.params.filterPriority, filter: (hit) => this._filterHit(hit) };
    this._ai = {
      weight: 1, timeToImpact: Infinity, speedKmh: 0, unblockable: false, holdingBall: false, invulnerable: false,
      dodgeDisabled: false, reactionWindow: 0, preferCatchBelowKmh: 0,
    };
  }

  onEquip() {
    // Evasion feedback when the health pipeline negated the hit through the 'invulnerable' status before our filter.
    this.listen(EV.BallHitPlayer, (e) => {
      if (!e || e.victim !== this.owner || !this.isActive) return;
      if (e.outcome === 'negated' || e.outcome === 'ignored') this._onEvaded(e.ball, e.point, e.velocity);
    });
  }
  onUnequip() { this._unregisterFilter(); }

  canActivateCustom() {
    const o = this.owner, s = o.status;
    if (s && (s.has('dodgeDisabled') || s.has('rooted'))) return FAIL.CANNOT_ACT;
    if (!o.motor) return FAIL.CANNOT_ACT;
    return null;
  }

  onCast() {
    const o = this.owner, p = this.params;
    const window = this.duration > 0 ? this.duration : 0.8;
    this.evaded = 0;
    this._elapsed = 0;
    // 1) Invulnerability: status for HUD/AI/Health + our own filter so evasion never depends on who reads the status.
    o.status?.apply('invulnerable', window, 1, this);
    this._registerFilter();
    if (p.cancelCatchStance && o.combat && o.combat.catchArmed) o.combat.cancelCatch();
    // 2) Sliding dodge.
    this._resolveDirection(this._dir);
    this._startMotion(this._dir, window);
    // 3) Presentation.
    fx.play('slideDust', o.position, { scale: 1, direction: this._dir, color: SPECTER_TINT });
    fx.sfx('whoosh', o.position, p.sfxVolume, 1.1);
    fx.afterimage(o, p.afterimageLifetime, SPECTER_TINT);
    if (o.isLocal) fx.pulse('rewind', p.localPulse, p.pulseDuration);
    this._nextAfterimage = this.now + p.afterimageInterval;
  }

  onTick(dt) {
    const o = this.owner, p = this.params, m = o.motor, d = this._dir;
    this._elapsed += dt;
    // Emulated slide: friction-decaying speed along the dodge direction, never slower than current momentum there.
    if (this._emulate && m && o.canAct !== false) {
      const target = this._kick - p.slideFriction * this._elapsed;
      if (target > p.minEmulatedSpeed) {
        const v = o.velocity;
        const along = v ? v.x * d.x + v.z * d.z : 0;
        if (along < target) m.setPlanarVelocity(_vel.set(d.x * target, 0, d.z * target));
      } else {
        this._emulate = false;
      }
    }
    if (p.afterimageInterval > 0 && this.now >= this._nextAfterimage) {
      this._nextAfterimage = this.now + p.afterimageInterval;
      fx.afterimage(o, p.afterimageLifetime, SPECTER_TINT);
    }
  }

  onInterrupt(reason) {
    this._emulate = false;
    // An eliminated / round-reset body must not keep gliding.
    const m = this.owner.motor;
    if ((reason === INTERRUPT.ELIMINATED || reason === INTERRUPT.ROUND_ENDED) && m && m.isSliding) m.endSlide();
  }

  onEnd() {
    this._emulate = false;
    this._unregisterFilter();
    this.owner.status?.remove('invulnerable', this);
  }

  /** @param {object} ctx ai/bot.js buildAbilityContext() */
  evaluateAI(ctx) {
    const b = ctx && ctx.incomingBall;
    if (!b) return 0;
    const s = this.owner.status, p = this.params, a = this._ai;
    a.weight = this.def.aiWeight ?? 1;
    a.timeToImpact = Number.isFinite(ctx.incomingTime) ? ctx.incomingTime : Infinity;
    a.speedKmh = b.speedKmh || 0;
    a.unblockable = !!b.unblockable;
    a.holdingBall = !!ctx.holdingBall;
    a.invulnerable = !!(s && s.has('invulnerable'));
    a.dodgeDisabled = !!(s && (s.has('dodgeDisabled') || s.has('rooted')));
    a.reactionWindow = p.aiReactionWindow;
    a.preferCatchBelowKmh = p.aiPreferCatchBelowKmh;
    return dodgeUtility(a);
  }

  // ------------------------------------------------------------------ internals
  /** Human stick input when deflected enough, else a side-step away from the most threatening ball. */
  _resolveDirection(out) {
    const o = this.owner, p = this.params;
    const move = o.intent && o.intent.move;
    if (o.isHuman !== false && move) {
      const len = Math.hypot(move.x, move.z);
      if (len >= p.inputThreshold) return out.set(move.x / len, 0, move.z / len);
    }
    const threat = findMostThreateningBall(o, p.threatHorizon);
    computeEvadeDirection(o, threat.ball, p.roomProbe, out);
    if (out.lengthSq() < 1e-6) out.set(1, 0, 0);
    return out.normalize();
  }

  /**
   * Grounded: a forced ability slide on the motor (ignores the slide cooldown, keeps higher momentum, lasts the whole
   * window) and the state machine follows into Sliding so a throw wind-up / catch stance cannot cut it short.
   * Airborne or refused: a planar air-dodge / emulated slide driven from onTick.
   */
  _startMotion(dir, window) {
    const o = this.owner, p = this.params, m = o.motor;
    const grounded = m.isGrounded !== false;
    const v = o.velocity;
    const carried = v ? Math.max(0, v.x * dir.x + v.z * dir.z) : 0;
    this._kick = Math.max(grounded ? p.dodgeSpeed : p.airDodgeSpeed, carried);
    let sliding = false;
    if (grounded && typeof m.trySlide === 'function') {
      _slideOpts.speed = this._kick;
      _slideOpts.duration = window;
      sliding = !!m.trySlide(dir, _slideOpts);
    }
    if (sliding) {
      const f = o.fsm;
      if (f && f.canAct && typeof f.is === 'function' && !f.is('sliding') && typeof f.change === 'function') f.change('sliding', true);
    } else {
      m.setPlanarVelocity(_vel.set(dir.x * this._kick, 0, dir.z * this._kick));
    }
    this._emulate = !sliding;
  }

  _filterHit(hit) {
    if (!this.isActive || !hit || hit.cancelled) return;
    if (hit.unblockable && this.params.unblockablePierces) return;
    hit.cancelled = true;
    this._onEvaded(hit.ball, hit.point, hit.velocity);
  }

  /** Counts an evaded ball and shows a silver burst where it would have struck (deduped filter + event). */
  _onEvaded(ball, point, velocity) {
    const now = this.now, o = this.owner, p = this.params;
    if (ball && ball === this._lastEvadedBall && now - this._lastEvadedAt < 0.25) return;
    this._lastEvadedBall = ball || null;
    this._lastEvadedAt = now;
    this.evaded++;
    if (p.evadeBurstScale <= 0) return;
    if (point) _pos.copy(point); else { _pos.copy(o.position); _pos.y += 1.2; }
    if (velocity && velocity.lengthSq() > 1e-4) _burstDir.copy(velocity).normalize();
    else _burstDir.copy(this._dir);
    fx.play('rewindTrail', _pos, { scale: p.evadeBurstScale, color: SPECTER_TINT, direction: _burstDir });
    fx.sfx('whoosh', _pos, 0.5, 1.4);
  }

  _registerFilter() {
    const h = this.owner.health;
    if (!h || this._health) return;
    h.addHitFilter(this._filter);
    this._health = h;
  }
  _unregisterFilter() {
    if (!this._health) return;
    this._health.removeHitFilter?.(this._filter);
    this._health = null;
  }
}

// ===============================================================================================================
// ULTIMATE - Time Reversal
// ===============================================================================================================
/** Elimination causes that are not combat and must never be reversed (round flow, forfeits). */
const NON_REVERSIBLE_CAUSES = new Set(['forfeit', 'round', 'roundEnd', 'timeUp', 'matchEnd']);

/**
 * On cast an anchor (position, yaw, HP, time) is taken and an elimination interceptor is armed for the 3 s window.
 * If Specter would be eliminated meanwhile, the interceptor returns 'prevented' and: HP returns to the anchor value
 * (revive first if already flagged eliminated), frozen / stun gained since are cleared, Specter teleports back to the
 * anchor (clamped to his current zone), a rewind trail replays his recorded path, renderer.pulse('rewind') fires and
 * he gets 0.4 s of invulnerability (status + grace filter) so the same volley cannot finish the job; the ability then
 * ends. If nothing happens the window simply expires.
 */
export class SpecterTimeReversal extends AbilityBase {
  static defaults = {
    /** Invulnerability (s) after the rewind. */
    postRewindInvulnerability: 0.4,
    /** Minimum HP restored (guards an anchor taken at very low HP). */
    minRestoredHp: 1,
    /** Remove frozen / stunned gained after the cast (they did not exist at the anchor moment). */
    clearDisables: true,
    /** Unblockable balls ignore the post-rewind grace (consistent with evasion rules). */
    unblockableIgnoresGrace: true,
    /** Interceptor / grace-filter priority (lower runs first): claims eliminations before other interceptors. */
    priority: -50,
    /** Rewind-trail bursts replayed along the recorded path. */
    trailSamples: 6,
    /** Faint echo at the anchor while armed (s between bursts, 0 = off). */
    anchorEchoInterval: 0.75,
    localPulse: 1,
    remotePulse: 0.35,
    pulseDuration: 0.6,
    /** Bots cast when a ball will arrive within this many seconds ... */
    aiThreatWindow: 1.1,
    /** ... or when an enemy this close (m) is charging a throw. */
    aiChargingEnemyRange: 16,
  };

  onInitialize() {
    this._anchor = { t: 0, position: new THREE.Vector3(), yaw: 0, hp: 0 };
    this._triggered = false;
    this._graceEnd = 0;
    this._nextEcho = 0;
    this._restoreHp = 0;
    this._reassert = false;
    this._health = null;
    /** Number of successful rewinds this match (stats). */
    this.rewinds = 0;
    this._interceptor = { priority: this.params.priority, intercept: (health, ctx) => this._intercept(health, ctx) };
    this._filter = { priority: this.params.priority, filter: (hit) => this._graceFilter(hit) };
    this._ai = {
      weight: 1, invulnerable: false, targetable: true, incomingTime: Infinity, threatWindow: 0, enemyCharging: false,
      enemyDistance: Infinity, chargingRange: 0, dodgeReady: false,
    };
  }

  /** True while the window is open and unused. */
  get isArmed() { return this.isActive && !this._triggered; }
  /** Where Specter will snap back to (valid while armed). */
  get anchorPosition() { return this._anchor.position; }

  onCast() {
    const o = this.owner, p = this.params, a = this._anchor;
    a.t = this.now;
    a.position.copy(o.position);
    a.yaw = Number.isFinite(o.yaw) ? o.yaw : 0;
    a.hp = o.health ? o.health.hp : 0;
    this._triggered = false;
    this._graceEnd = 0;
    this._reassert = false;
    this._nextEcho = this.now + p.anchorEchoInterval;
    this._register();
    _pos.copy(o.position); _pos.y += 0.9;
    fx.play('rewindTrail', _pos, { scale: 0.8, color: SPECTER_TINT });
    fx.sfx('rewind', o.position, 0.45, 1.3);
    if (o.isLocal) fx.pulse('rewind', 0.3, 0.35);
  }

  onTick() {
    const now = this.now, p = this.params;
    if (this._triggered) {
      if (this._reassert) {
        // Health may finalise hp after our interceptor returned: re-assert the restored value once.
        this._reassert = false;
        const h = this.owner.health;
        if (h && !h.isEliminated && h.hp < this._restoreHp) h.setHpSilently(this._restoreHp);
      }
      if (now >= this._graceEnd) this.endAbility();
      return;
    }
    // Faint echo at the anchor so everyone can read where Specter will snap back to.
    if (p.anchorEchoInterval > 0 && now >= this._nextEcho) {
      this._nextEcho = now + p.anchorEchoInterval;
      _pos.copy(this._anchor.position); _pos.y += 0.9;
      fx.play('rewindTrail', _pos, { scale: 0.45, color: SPECTER_TINT });
    }
  }

  /**
   * The reversal is an insurance window, not a channel: the flinch stun of a non-lethal hit (Health) or a freeze must
   * not cancel it - a frozen Specter hit a second time is exactly what it rewinds. Elimination / round end still do.
   */
  interrupt(reason) {
    if (reason === INTERRUPT.STUNNED || reason === INTERRUPT.FROZEN) return;
    super.interrupt(reason);
  }

  onEnd() {
    this._unregister();
    this.owner.status?.remove('invulnerable', this);
  }
  onUnequip() { this._unregister(); }

  /** @param {object} ctx ai/bot.js buildAbilityContext() */
  evaluateAI(ctx) {
    if (!ctx) return 0;
    const o = this.owner, s = o.status, p = this.params, a = this._ai;
    const skill = o.abilities && o.abilities.skill;
    const enemy = ctx.nearestEnemy;
    a.weight = this.def.aiWeight ?? 0.9;
    a.invulnerable = !!(s && s.has('invulnerable'));
    a.targetable = o.isTargetable !== false;
    a.incomingTime = ctx.incomingBall && Number.isFinite(ctx.incomingTime) ? ctx.incomingTime : Infinity;
    a.threatWindow = p.aiThreatWindow;
    a.enemyCharging = !!(enemy && enemy.combat && enemy.combat.isCharging);
    a.enemyDistance = Number.isFinite(ctx.nearestEnemyDistance) ? ctx.nearestEnemyDistance : Infinity;
    a.chargingRange = p.aiChargingEnemyRange;
    a.dodgeReady = !!(skill instanceof SpecterPrecognitionDodge && skill.isReady && !(s && s.has('dodgeDisabled')));
    return reversalUtility(a);
  }

  // ------------------------------------------------------------------ interceptor / filter
  _intercept(health, ctx) {
    if (!this.isActive || this._triggered) return 'eliminated';
    const cause = ctx && ctx.cause;
    if (cause && NON_REVERSIBLE_CAUSES.has(cause)) return 'eliminated';
    if (game.match && !game.match.isPlaying) return 'eliminated';
    this._performRewind(health || this.owner.health);
    return 'prevented';
  }

  /** Post-rewind grace: negates non-unblockable hits until the grace ends. */
  _graceFilter(hit) {
    if (!this._triggered || !hit || hit.cancelled || this.now >= this._graceEnd) return;
    if (hit.unblockable && this.params.unblockableIgnoresGrace) return;
    hit.cancelled = true;
  }

  _performRewind(health) {
    const o = this.owner, p = this.params, a = this._anchor;
    this._triggered = true;
    this._graceEnd = this.now + p.postRewindInvulnerability;
    this.holdActive(); // the grace may outlive the 3 s window; onTick ends the ability (never inside Health's loop)
    this.rewinds++;

    _from.copy(o.position);
    const to = clampToPlayerZone(o, a.position);
    const floorY = game.court ? game.court.floorY : 0;
    if (to.y < floorY) to.y = floorY;

    // 1) HP back to the anchor value.
    if (health) {
      const max = health.maxHp > 0 ? health.maxHp : Math.max(1, a.hp);
      const hp = Math.min(max, Math.max(p.minRestoredHp, a.hp));
      if (health.isEliminated) health.revive(hp / max);
      health.setHpSilently(hp);
      this._restoreHp = hp;
      this._reassert = true;
    }
    // 2) Disables that did not exist at the anchor moment.
    if (p.clearDisables) this._clearDisables();
    // 3) Replay the recorded path (before teleporting, while the history is continuous).
    this._spawnPathTrail(_from, to);
    // 4) Snap back, dead still.
    o.teleport(to, a.yaw);
    o.motor?.setPlanarVelocity(_zero);
    if (p.postRewindInvulnerability > 0) o.status?.apply('invulnerable', p.postRewindInvulnerability, 1, this);
    // 5) Presentation (the rewind is visible to everyone; strongest for Specter's own screen).
    _pos.copy(to); _pos.y += 0.9;
    fx.play('rewindTrail', _pos, { scale: 1.2, color: SPECTER_TINT });
    fx.play('teleport', to, { scale: 0.8, color: SPECTER_TINT });
    fx.sfx('rewind', to, 1, 1);
    fx.pulse('rewind', o.isLocal ? p.localPulse : p.remotePulse, p.pulseDuration);
    fx.flash(o, SPECTER_TINT, 0.08);
  }

  _clearDisables() {
    const o = this.owner, s = o.status, f = o.fsm;
    if (s) {
      if (s.has('frozen')) s.remove('frozen');
      if (s.has('stunned')) s.remove('stunned');
    }
    if (!f || typeof f.is !== 'function') return;
    if (f.is('incapacitated') && f.incapReason === 'frozen') f.release?.('frozen');
    if (f.is('stunned')) f.resetToGrounded?.();
  }

  /** Rewind-trail bursts along the path Specter took since the cast (recorded history, else a straight line). */
  _spawnPathTrail(from, to) {
    const p = this.params, n = p.trailSamples | 0;
    if (n <= 0) return;
    const h = this.owner.history;
    const start = this._anchor.t, end = this.now;
    const canSample = !!h && typeof h.sample === 'function' && end > start;
    for (let i = 0; i < n; i++) {
      const k = (i + 0.5) / n;
      const s = canSample ? h.sample(end, (end - start) * k) : null;
      if (s && s.position) _pos.copy(s.position); else _pos.lerpVectors(from, to, k);
      _pos.y += 0.9;
      fx.play('rewindTrail', _pos, { scale: 0.9 - 0.4 * k, color: SPECTER_TINT });
    }
  }

  _register() {
    const h = this.owner.health;
    if (!h || this._health) return;
    h.addInterceptor(this._interceptor);
    h.addHitFilter(this._filter);
    this._health = h;
  }
  _unregister() {
    if (!this._health) return;
    this._health.removeInterceptor?.(this._interceptor);
    this._health.removeHitFilter?.(this._filter);
    this._health = null;
  }
}

registerAbility('specter.danger_sense', SpecterDangerSense);
registerAbility('specter.precognition_dodge', SpecterPrecognitionDodge);
registerAbility('specter.time_reversal', SpecterTimeReversal);
