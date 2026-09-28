// ---------------------------------------------------------------------------------------------------------------
// Chrono (時間回溯 / Rewind) - agility hero. Ids and spec numbers live in abilities/roster.js.
//   PASSIVE  chrono.delayed_impact  Delayed Impact  elimination delayed 2 s; a teammate's catch in the window cancels it
//   SKILL    chrono.stasis_field    Stasis Field    freeze a flying enemy ball mid-air for 2 s (CD 12)
//   ULTIMATE chrono.temporal_reset  Temporal Reset  5x5 m zone: ball trajectories + player positions rewind 3 s
// Gameplay runs on the scaled clock (this.now / dt); presentation goes through the guarded `fx` helpers so a missing
// or failing optional system (vfx, audio, renderer, avatar, camera) can never break the ability logic.
// ---------------------------------------------------------------------------------------------------------------
import * as THREE from 'three';
import { game } from '../../game.js';
import { EV } from '../../core/events.js';
import { ZONE } from '../../core/constants.js';
import { AbilityBase, registerAbility, FAIL } from '../abilityBase.js';
import {
  findMostThreateningEnemyBall, threatTimeForTeam, isEnemyBall, rewindPlayer, rewindBall, REWIND_RESULT, fx,
} from '../shared/chronoTime.js';
import { insideZone, playAreaBounds, resolveZoneCenter, stasisScore, urgency, heartbeatInterval } from '../shared/chronoMath.js';

/** Emerald time-glow of Chrono's casts (roster colour 0x3be08a, lifted for additive VFX). */
export const CHRONO_TINT = 0x5cf0a8;
/** Warm clockwork gold of the pending Delayed Impact clock (reads as "borrowed time"). */
export const CLOCK_TINT = 0xf2c661;
/** Chest height above the feet (m) for range / threat origins (avoids allocating getters). */
const CHEST_HEIGHT = 1.3;

// Module-scope temporaries (no per-frame allocations).
const _pos = new THREE.Vector3();
const _chest = new THREE.Vector3();
const _origin = new THREE.Vector3();
const _aim = new THREE.Vector3();
const _to = new THREE.Vector3();
const _from = new THREE.Vector3();
const _dest = new THREE.Vector3();
const _xz = { x: 0, z: 0 };

/** Chest point of `player` into `out`. */
function chestOf(player, out) {
  out.copy(player.position);
  out.y += CHEST_HEIGHT;
  return out;
}

// ===============================================================================================================
// PASSIVE - Delayed Impact
// ===============================================================================================================
/**
 * Permanent elimination interceptor on Chrono's Health. An elimination caused by a ball hit, an ability or the
 * frozen second hit returns 'delayed' and opens a 2 s window: health.setPending(true), a gold clock effect on the
 * body and an accelerating heartbeat. Further eliminations during the window are absorbed into it.
 *  - A TEAMMATE (not Chrono) catches a ball in the window (EV.BallCaught): cancelled - setPending(false), revive(1),
 *    EV.PlayerRevived { cause: 'delayedImpactCancelled', reviver: catcher }.
 *  - The window expires: health.commitPending(ctx) with the original context (attacker credit, impulse, point).
 *  - The round ends / resets: cleared silently.
 */
export class ChronoDelayedImpact extends AbilityBase {
  static defaults = {
    /** Spec: seconds the elimination is postponed. */
    delay: 2,
    /** Fraction of max HP restored when a teammate's catch cancels the elimination. */
    restoreHpFraction: 1,
    /** Elimination causes that are delayed (tackles, forfeits, round flow go straight through). */
    delayCauses: ['ballHit', 'ability', 'frozen'],
    /** Interceptor priority (lower runs first): runs late so explicit preventions win. */
    priority: 100,
    /** Heartbeat interval (s) at the start / end of the window (the clock speeds up). */
    pulseStart: 0.5,
    pulseEnd: 0.18,
    pulseVolume: 0.7,
    pulsePitch: 0.6,
    /** Gold tint on the body while pending (0 = off). */
    tintAmount: 0.35,
    /** Local-player 'rewind' pulse when the delay starts / is cancelled. */
    localPulse: 0.6,
    /** Local-player 'danger' pulse on each heartbeat (scaled up as the window closes). */
    localHeartbeatPulse: 0.3,
    /** Local-player sustained 'danger' look at the end of the window (half of it at the start; 0 = off). */
    localSustain: 0.7,
  };

  onInitialize() {
    /** True while an elimination is pending (HUD countdown; teammates should go for a catch). */
    this.pending = false;
    this._started = 0;
    this._until = 0;
    this._ctx = null;
    this._nextBeat = 0;
    this._clock = null;
    this._tinted = false;
    this._sustained = false;
    this._health = null;
    this._committing = false;
    this._causes = new Set(this.params.delayCauses);
    this._interceptor = { priority: this.params.priority, intercept: (health, ctx) => this._intercept(health, ctx) };
  }

  /** Seconds until the pending elimination resolves (0 when none). */
  get pendingRemaining() { return this.pending ? Math.max(0, this._until - this.now) : 0; }
  /** 0..1 progress of the pending window (1 = about to resolve). */
  get pendingProgress() {
    const d = this._until - this._started;
    return this.pending && d > 0 ? Math.min(1, Math.max(0, (this.now - this._started) / d)) : 0;
  }

  onEquip() {
    this._ensureRegistered();
    this.listen(EV.BallCaught, (e) => this._onCaught(e));
    this.listen(EV.RoundEnded, () => this._clear(true));
    this.listen(EV.MatchEnded, () => this._clear(true));
  }
  onUnequip() {
    // Hero swap / despawn: never leave the health stuck in a pending state.
    this._clear(true);
    if (this._health) this._health.removeInterceptor?.(this._interceptor);
    this._health = null;
  }
  onRoundReset() { this._clear(true); }
  /** Passives never cast. */
  onCast() {}
  evaluateAI() { return 0; }

  /** Passive tick (scaled dt): late registration, heartbeat and window expiry. */
  onTick() {
    this._ensureRegistered();
    if (!this.pending) return;
    const o = this.owner, h = o.health, p = this.params;
    // Resolved elsewhere (bypassing elimination, round flow, someone cleared the flag) or moved out: drop our state.
    if (!h || h.isEliminated || h.pending === false || o.zone !== ZONE.INFIELD) { this._clear(false); return; }
    const now = this.now;
    if (now >= this._until) { this._commit(); return; }
    // Local player: a sustained red look that deepens as the borrowed time runs out.
    if (o.isLocal && p.localSustain > 0) {
      fx.sustain('danger', p.localSustain * (0.5 + 0.5 * this.pendingProgress));
      this._sustained = true;
    }
    if (now >= this._nextBeat) {
      const k = this.pendingProgress;
      this._nextBeat = now + heartbeatInterval(k, p.pulseStart, p.pulseEnd);
      fx.sfx('heartbeat', o.position, p.pulseVolume, p.pulsePitch * (1 + 0.25 * k));
      if (o.isLocal && p.localHeartbeatPulse > 0) fx.pulse('danger', p.localHeartbeatPulse * (0.6 + 0.4 * k), 0.18);
    }
  }

  // ------------------------------------------------------------------ interceptor
  _intercept(health, ctx) {
    if (this._committing) return 'eliminated'; // our own resolution must go through
    const cause = ctx && ctx.cause;
    if (!this._causes.has(cause)) return 'eliminated';
    if (this.pending) return 'delayed'; // one pending elimination at a time: absorbed into the running window
    if (game.match && !game.match.isPlaying) return 'eliminated'; // nothing to delay once the round is over
    if (this.owner.zone !== ZONE.INFIELD) return 'eliminated';
    this._begin(health || this.owner.health, ctx);
    return 'delayed';
  }

  _begin(health, ctx) {
    const o = this.owner, p = this.params, now = this.now;
    this.pending = true;
    this._started = now;
    this._until = now + Math.max(0.05, p.delay);
    this._nextBeat = now;
    this._ctx = snapshotContext(ctx);
    health?.setPending?.(true);
    this._clock = fx.attach('rewindTrail', o.root, { duration: p.delay, color: CLOCK_TINT });
    if (p.tintAmount > 0) { fx.tint(o, CLOCK_TINT, p.tintAmount); this._tinted = true; }
    fx.sfx('rewind', o.position, 0.6, 0.7);
    if (o.isLocal) fx.pulse('rewind', p.localPulse, 0.35);
  }

  _onCaught(e) {
    if (!this.pending || !e || !e.catcher) return;
    if (!this.isAlly(e.catcher)) return; // teammates only (isAlly excludes Chrono herself)
    if (e.quality === 'miss') return;
    this._cancel(e.catcher);
  }

  _cancel(catcher) {
    const o = this.owner, h = o.health, p = this.params;
    this._stopPresentation();
    this.pending = false;
    this._ctx = null;
    if (!h) return;
    h.setPending(false);
    h.revive(p.restoreHpFraction);
    // Defensive: a revive() that only acts on eliminated players would leave 0 HP behind.
    if (!(h.hp > 0) && h.maxHp > 0) h.setHpSilently(h.maxHp * p.restoreHpFraction);
    game.events.emit(EV.PlayerRevived, { player: o, reviver: catcher, cause: 'delayedImpactCancelled' });
    fx.play('reviveBeam', o.position, { scale: 1, color: CHRONO_TINT });
    chestOf(o, _pos);
    fx.play('rewindTrail', _pos, { scale: 1.2, color: CHRONO_TINT });
    fx.sfx('rewind', o.position, 0.9, 1.1);
    fx.flash(o, CHRONO_TINT, 0.08);
    if (o.isLocal) fx.pulse('rewind', p.localPulse, 0.5);
  }

  _commit() {
    const h = this.owner.health, ctx = this._ctx;
    this._stopPresentation();
    this.pending = false;
    this._ctx = null;
    if (!h) return;
    this._committing = true;
    try { h.commitPending(ctx); } finally { this._committing = false; }
  }

  /** @param {boolean} restoreHealthFlag also clear the health's pending flag (round flow / unequip) */
  _clear(restoreHealthFlag) {
    const was = this.pending;
    this.pending = false;
    this._ctx = null;
    this._stopPresentation();
    if (was && restoreHealthFlag) this.owner.health?.setPending?.(false);
  }

  _stopPresentation() {
    fx.stop(this._clock);
    this._clock = null;
    if (this._tinted) { fx.tint(this.owner, CLOCK_TINT, 0); this._tinted = false; }
    if (this._sustained) { fx.sustain('danger', 0); this._sustained = false; }
  }

  /** Registers the interceptor once the player's health exists (abilities may be equipped before it). */
  _ensureRegistered() {
    const h = this.owner.health;
    if (!h || this._health === h) return;
    if (this._health) this._health.removeInterceptor?.(this._interceptor);
    h.addInterceptor(this._interceptor);
    this._health = h;
  }
}

/** Private copy of an elimination context (Health may reuse its object / vectors); vectors are cloned. */
function snapshotContext(ctx) {
  const c = Object.assign({}, ctx || {});
  for (const k of ['impulse', 'point']) if (c[k] && typeof c[k].clone === 'function') c[k] = c[k].clone();
  c.delayed = true;
  return c;
}

// ===============================================================================================================
// SKILL - Stasis Field
// ===============================================================================================================
/**
 * Freezes the most threatening live enemy ball within 18 m for 2 s (Ball.enterStasis) so anyone - ideally an ally -
 * can snatch it. Target: the ball closest to the aim ray (camera ray for the local player, intent aim otherwise),
 * strongly preferring balls heading at Chrono's team (always eligible: auto-save) and mildly preferring closer ones;
 * other balls must lie within the aim cone. No target -> FAIL.NO_TARGET (no cooldown spent). A stasis bubble is
 * attached to the ball and removed as soon as it leaves stasis.
 */
export class ChronoStasisField extends AbilityBase {
  static defaults = {
    /** Spec: stasis seconds. */
    duration: 2,
    /** Spec: max distance (m) from Chrono. */
    range: 18,
    /** Aim-cone half angle (deg) for balls that do not threaten the team. */
    maxAimAngle: 60,
    /** Score bonus (deg) for balls heading at Chrono's team. */
    threatPreferenceDeg: 30,
    /** Score penalty (deg per metre) favouring closer balls. */
    distancePenaltyPerMetre: 0.6,
    /** Threat look-ahead (s) and body radius (m) of the team threat test. */
    threatLookAhead: 1.5,
    threatRadius: 1.4,
    bubbleScale: 1,
    sfxVolume: 1,
    /** Bots ignore threats arriving sooner than this (too late to react). */
    aiMinTimeToThreat: 0.12,
    /** Grace (s) before a ball that is not (yet) in stasis counts as released. */
    releaseGrace: 0.1,
  };

  onInitialize() {
    this._candidate = null;
    /** Ball currently held in stasis by this ability (null when none). */
    this.frozenBall = null;
    this._bubble = null;
    this._frozenAt = 0;
    this._refund = false;
  }

  canActivateCustom() {
    this._candidate = this.findTarget();
    return this._candidate ? null : FAIL.NO_TARGET;
  }

  onCast() {
    const o = this.owner, p = this.params;
    let ball = this._candidate;
    this._candidate = null;
    if (!ball || ball.state !== 'live') ball = this.findTarget(); // re-validate (same frame, defensive)
    if (!ball) { this._refund = true; return; }
    this._releaseBubble();
    ball.enterStasis(p.duration);
    this.frozenBall = ball;
    this._frozenAt = this.now;
    this._bubble = fx.attach('stasisBubble', ball.root, { duration: p.duration, color: CHRONO_TINT });
    if (!this._bubble) fx.play('stasisBubble', ball.position, { scale: p.bubbleScale, color: CHRONO_TINT });
    fx.sfx('stasis', ball.position, p.sfxVolume, 1);
    // A short time-warp streak from Chrono's hand toward the ball reads as the cast.
    const hand = o.combat && typeof o.combat.getThrowOrigin === 'function' ? o.combat.getThrowOrigin() : null;
    if (hand) _pos.copy(hand); else chestOf(o, _pos);
    _to.subVectors(ball.position, _pos);
    if (_to.lengthSq() > 1e-4) _to.normalize(); else _to.set(0, 0, 1);
    fx.play('rewindTrail', _pos, { scale: 0.5, color: CHRONO_TINT, direction: _to });
  }

  /** Nothing was frozen (target vanished between validation and cast): do not charge the cooldown. */
  onCooldown() {
    if (!this._refund) return;
    this._refund = false;
    this.resetCooldown();
  }
  /** The skill is instant; the bubble is watched during the cooldown and stopped when the ball leaves stasis. */
  onCooldownTick() { this._watch(); }
  onRoundReset() { this._candidate = null; this._releaseBubble(); }
  onUnequip() { this._releaseBubble(); }

  /** Bot utility (0..1). Scans every enemy ball heading at the TEAM (ctx.incomingBall only covers Chrono herself). */
  evaluateAI() {
    if (!this.isReady) return 0;
    const o = this.owner, p = this.params;
    const f = findMostThreateningEnemyBall(o, chestOf(o, _chest), p.range, p.threatLookAhead, p.threatRadius, p.aiMinTimeToThreat);
    if (!f.ball) return 0;
    // Sooner threats and faster balls are more urgent; a freeze near our side also hands us the ball.
    const u = urgency(f.time, p.threatLookAhead);
    const speed = Math.min(1, (f.ball.speedKmh || 0) / 140);
    return Math.min(1, Math.max(0, (this.def.aiWeight ?? 0.9) * (0.6 + 0.25 * u + 0.15 * speed)));
  }

  /** Best live enemy ball for the stasis (lowest chronoMath.stasisScore), or null. */
  findTarget() {
    const o = this.owner, p = this.params;
    const list = game.balls && game.balls.active;
    if (!Array.isArray(list)) return null;
    // Aim ray: the camera's crosshair ray for the local player, else the intent's aim from the eyes.
    const ray = o.isLocal && game.cameraRig ? game.cameraRig.aimRay : null;
    if (ray && ray.direction && ray.direction.lengthSq() > 1e-6) {
      _origin.copy(ray.origin);
      _aim.copy(ray.direction).normalize();
    } else {
      _origin.copy(o.position);
      _origin.y += (o.height || 1.75) * 0.93;
      const ad = o.intent && o.intent.aimDir;
      if (ad && ad.lengthSq() > 1e-6) _aim.copy(ad).normalize();
      else { const f = o.forward; _aim.set(f ? f.x : 0, 0, f ? f.z : 1).normalize(); }
    }
    chestOf(o, _chest);
    const range2 = p.range * p.range;
    let best = null, bestScore = Infinity;
    for (let i = 0; i < list.length; i++) {
      const ball = list[i];
      if (!ball || ball.state !== 'live' || !isEnemyBall(ball, o)) continue;
      if (ball.position.distanceToSquared(_chest) > range2) continue;
      _to.subVectors(ball.position, _origin);
      const dist = _to.length();
      const angle = dist > 1e-3 ? THREE.MathUtils.radToDeg(_aim.angleTo(_to)) : 0;
      const threat = threatTimeForTeam(ball, o.team, p.threatLookAhead, p.threatRadius) < Infinity;
      const s = stasisScore(angle, dist, threat, p.maxAimAngle, p.distancePenaltyPerMetre, p.threatPreferenceDeg);
      if (s < bestScore) { bestScore = s; best = ball; }
    }
    return best;
  }

  _watch() {
    const b = this.frozenBall;
    if (!b) return;
    if (this.now - this._frozenAt < this.params.releaseGrace) return;
    if (b.state !== 'stasis') this._releaseBubble();
  }

  _releaseBubble() {
    fx.stop(this._bubble);
    this._bubble = null;
    this.frozenBall = null;
  }
}

// ===============================================================================================================
// ULTIMATE - Temporal Reset
// ===============================================================================================================
/**
 * A 5 x 5 m court-aligned zone (6 m tall) centred on the aim point (camera aim for the local player, intent aim
 * otherwise; bots auto-target the most dangerous enemy ball heading at their team), limited to 25 m from Chrono and
 * clamped to the playing area. Everything currently inside is restored from its history of 3 s ago:
 *  - players teleport to their recorded position (clamped to their CURRENT zone) with recorded yaw and momentum;
 *  - live balls get their recorded position + velocity (a throw made after the rewind point is undone: match balls
 *    drop where they were as free balls, ability projectiles are erased); free / stasis balls teleport.
 * Targets are gathered first and applied afterwards, so rewinds cannot cascade. Temporal-zone VFX, rewind trails at
 * each entity's old and new position, renderer.pulse('rewind') and the rewind SFX.
 */
export class ChronoTemporalReset extends AbilityBase {
  static defaults = {
    /** Spec: zone side (m), rewind seconds and cast range (m). */
    size: 5,
    seconds: 3,
    range: 25,
    /** Zone height (m) above the floor (lobbed balls count). */
    height: 6,
    affectEnemies: true,
    affectAllies: true,
    affectSelf: true,
    affectBalls: true,
    /** Players also get their recorded planar momentum back. */
    restorePlayerVelocity: true,
    /** Bots centre the zone on the most dangerous enemy ball heading at their team. */
    botsAutoTarget: true,
    threatLookAhead: 1.5,
    threatRadius: 1.4,
    /** Zone distance (m) along the facing when the aim point is unusable. */
    fallbackDistance: 8,
    pulseIntensity: 0.85,
    remotePulse: 0.4,
    pulseDuration: 0.7,
    /** Negative FOV kick (deg) for the local player: the world "sucks back". */
    fovKick: -5,
    maxTrailBursts: 16,
    aiMinTimeToThreat: 0.15,
  };

  onInitialize() {
    this._players = [];
    this._balls = [];
    this._area = null;
    /** Centre of the most recent zone (HUD / debug). */
    this.lastCenter = new THREE.Vector3();
    /** Players + balls rewound by the most recent cast. */
    this.lastRewoundCount = 0;
  }

  onCast() {
    const o = this.owner, p = this.params, now = this.now;
    const center = this._resolveCenter(this.lastCenter);
    this._gather(center);
    // Zone presentation first so the flash reads as the cause of the snap-back.
    fx.play('temporalZone', center, { scale: p.size / 5, color: CHRONO_TINT });
    fx.sfx('rewind', center, 1, 0.9);

    let bursts = 0, rewound = 0, localAffected = false;
    for (let i = 0; i < this._players.length; i++) {
      const pl = this._players[i];
      if (rewindPlayer(pl, now, p.seconds, p.restorePlayerVelocity, _from, _dest) === REWIND_RESULT.SKIPPED) continue;
      rewound++;
      if (pl.isLocal) localAffected = true;
      bursts = this._trail(_from, _dest, 0.9, bursts);
    }
    for (let i = 0; i < this._balls.length; i++) {
      if (rewindBall(this._balls[i], now, p.seconds, _from, _dest) === REWIND_RESULT.SKIPPED) continue;
      rewound++;
      bursts = this._trail(_from, _dest, 0, bursts);
    }
    this.lastRewoundCount = rewound;
    this._players.length = 0;
    this._balls.length = 0;

    const local = o.isLocal || localAffected;
    fx.pulse('rewind', local ? p.pulseIntensity : p.remotePulse, p.pulseDuration);
    if (local && p.fovKick) fx.fovKick(p.fovKick, 0.4);
  }

  onUnequip() { this._players.length = 0; this._balls.length = 0; }

  /** Bot utility (0..1). Scans every enemy ball heading at the TEAM (ctx.incomingBall only covers Chrono herself). */
  evaluateAI() {
    const o = this.owner, p = this.params;
    // Defensive use: an enemy ball is about to reach the team - rewinding it undoes the throw.
    const f = findMostThreateningEnemyBall(o, chestOf(o, _chest), p.range, p.threatLookAhead, p.threatRadius, p.aiMinTimeToThreat);
    if (!f.ball) return 0;
    const u = urgency(f.time, p.threatLookAhead);
    // The cheaper Stasis Field answers a single ball: keep the ultimate as the back-up while the skill is ready.
    const skill = o.abilities && o.abilities.skill;
    const stasisReady = skill instanceof ChronoStasisField && skill.isReady;
    return Math.min(1, Math.max(0, (this.def.aiWeight ?? 0.7) * (0.55 + 0.35 * u) * (stasisReady ? 0.5 : 1)));
  }

  // ------------------------------------------------------------------ internals
  /** Aim point (or bot auto-target) limited to the cast range and clamped to the playing area, on the floor. */
  _resolveCenter(out) {
    const o = this.owner, p = this.params;
    let ax = NaN, az = NaN;
    if (p.botsAutoTarget && !o.isHuman) {
      const f = findMostThreateningEnemyBall(o, chestOf(o, _chest), p.range, p.threatLookAhead, p.threatRadius, 0);
      if (f.ball) { ax = f.ball.position.x; az = f.ball.position.z; }
    }
    if (!Number.isFinite(ax)) {
      const rigAim = o.isLocal && game.cameraRig ? game.cameraRig.aimPoint : null;
      const aim = rigAim || (o.intent && o.intent.aimPoint);
      if (aim) { ax = aim.x; az = aim.z; }
    }
    if (!this._area && game.court) this._area = playAreaBounds(game.court);
    const area = this._area || playAreaBounds(null);
    const fwd = o.forward;
    resolveZoneCenter(o.position.x, o.position.z, ax, az, fwd ? fwd.x : 0, fwd ? fwd.z : 1, p.range, area, p.fallbackDistance, _xz);
    return out.set(_xz.x, game.court ? game.court.floorY : 0, _xz.z);
  }

  _gather(center) {
    const o = this.owner, p = this.params;
    this._players.length = 0;
    this._balls.length = 0;
    for (const pl of game.players) {
      if (!pl) continue;
      const self = pl === o;
      if (self && !p.affectSelf) continue;
      if (!self && this.isAlly(pl) && !p.affectAllies) continue;
      if (this.isEnemy(pl) && !p.affectEnemies) continue;
      if (!insideZone(pl.position, center, p.size, p.height)) continue;
      this._players.push(pl);
    }
    if (!p.affectBalls) return;
    const list = game.balls && game.balls.active;
    if (!Array.isArray(list)) return;
    for (let i = 0; i < list.length; i++) {
      const b = list[i];
      if (!b || b.state === 'held' || b.state === 'despawned' || !b.position) continue; // held balls travel with their holder
      if (!insideZone(b.position, center, p.size, p.height)) continue;
      this._balls.push(b);
    }
  }

  /** Rewind-trail bursts at an entity's old and new position (capped per cast). */
  _trail(from, to, lift, bursts) {
    const max = this.params.maxTrailBursts;
    if (bursts >= max) return bursts;
    _pos.copy(from); _pos.y += lift;
    fx.play('rewindTrail', _pos, { scale: 0.7, color: CHRONO_TINT });
    bursts++;
    if (bursts >= max || from.distanceToSquared(to) < 0.01) return bursts;
    _pos.copy(to); _pos.y += lift;
    fx.play('rewindTrail', _pos, { scale: 0.9, color: CHRONO_TINT });
    return bursts + 1;
  }
}

registerAbility('chrono.delayed_impact', ChronoDelayedImpact);
registerAbility('chrono.stasis_field', ChronoStasisField);
registerAbility('chrono.temporal_reset', ChronoTemporalReset);
