// ---------------------------------------------------------------------------------------------------------------
// Screws (工程師 Engineer) - abilities.
//   PASSIVE  Magnetic Recycle (磁力回收)  For 4 s after any elimination, the free ball within 3 m of that spot rolls
//                                         back toward Screws' half (a registered ball field + per-frame fallback) -
//                                         unless it already rests in one of his team's zones (infield or U outfield).
//   SKILL    Glue Trap Ball   (黏膠陷阱球) The HELD match ball is coated in glue and thrown (speed x0.9, gravity x0.8);
//                                         wherever it hits (player, floor, wall, a catch or a block) it leaves a
//                                         2 m / 4 s glue puddle slowing enemies by 60%. Needs the ball. CD 10 s.
//   ULTIMATE Auto-Turret      (自動砲台)  8 s tripod turret 1.5 m ahead inside his zone: sucks up the free ball within
//                                         4 m that rests in Screws' OWN infield (hopper, 1) and fires it at the nearest
//                                         enemy every 1.5 s. A stored ball is in his team's custody (possession clock
//                                         paused); with nothing to shoot at for a while it is dropped again.
// World objects: ../shared/screwsGluePuddle.js, ../shared/screwsAutoTurret.js.
// ---------------------------------------------------------------------------------------------------------------
import * as THREE from 'three';
import { game } from '../../game.js';
import { EV } from '../../core/events.js';
import { AbilityBase, registerAbility } from '../abilityBase.js';
import { clampToPlayerZone } from '../abilityUtil.js';
import { throwHeldBall } from '../shared/empowerThrow.js';
import { depthIntoOwnHalf } from '../shared/screwsGadgetMath.js';
import {
  BALL, FieldStamp, matchBalls, setBallVelocity, registerBallField, unregisterBallField, planarForward, chestOf, fx,
  sfx, hasStatus, teamColor, inTeamZone, inTeamInfield,
} from '../shared/screwsGadgetKit.js';
import { ScrewsGluePuddle, GLUE_DEFAULTS } from '../shared/screwsGluePuddle.js';
import { ScrewsAutoTurret as TurretUnit, TURRET_DEFAULTS } from '../shared/screwsAutoTurret.js';

const _v = new THREE.Vector3();
const _p = new THREE.Vector3();
const _f = new THREE.Vector3();
const _dir = new THREE.Vector3();

// ================================================================== PASSIVE: Magnetic Recycle

export class ScrewsMagneticRecycle extends AbilityBase {
  static defaults = {
    radius: 3,               // m around the elimination spot (spec)
    force: 1.5,              // m/s^2 rolling acceleration toward Screws' half
    duration: 4,             // s the spot stays magnetised (spec)
    maxRollSpeed: 3,         // m/s along the pull direction
    startSpeed: 0.35,        // m/s nudge that gets a resting ball rolling (beats the resting-speed dead band)
    arriveDepth: 1.5,        // m inside Screws' half: the ball has come home
    targetDepthFrac: 0.45,   // aim point depth as a share of the half length
    rollHeight: 0.25,        // m above resting height: only rolling/low balls are pulled
    maxZones: 6,
  };

  onInitialize() {
    this._zones = [];               // { x, z, until }
    this._tracked = new Map();      // ball -> until (scaled time)
    this._stamp = new FieldStamp();
    this._field = { apply: (ball, dt) => this._fieldApply(ball, dt) };
    this._fieldOn = false;
  }

  onEquip() {
    this.listen(EV.PlayerEliminated, (e) => this._onEliminated(e));
    this.listen(EV.RoundEnded, () => this._clear());
  }

  onUnequip() { this._clear(); }

  onRoundReset() { this._clear(); }

  onCast() { /* passive: never cast */ }

  _onEliminated(e) {
    if (game.match && !game.match.isPlaying) return;
    const victim = e && e.player;
    const at = (victim && victim.position) || (e && e.point);
    if (!at) return;
    if (this._zones.length >= this.params.maxZones) this._zones.shift();
    this._zones.push({ x: at.x, z: at.z, until: game.time.now + this.params.duration });
    if (!this._fieldOn) this._fieldOn = registerBallField(this._field);
    _p.set(at.x, (game.court ? game.court.floorY : 0) + 0.05, at.z);
    fx('magnetField', _p, { radius: this.params.radius, duration: 1.2, color: teamColor(this.owner.team).getHex() });
    sfx('magnetHum', _p, 0.6, 0.8);
  }

  /** Passive tick (every frame): expire zones, pick up balls near active zones, roll the tracked ones home. */
  onTick(dt) {
    if (!this._zones.length && !this._tracked.size) return;
    const now = game.time.now;
    for (let i = this._zones.length - 1; i >= 0; i--) if (this._zones[i].until <= now) this._zones.splice(i, 1);

    if (this._zones.length) {
      const r2 = this.params.radius * this.params.radius;
      const balls = matchBalls();
      for (let i = 0; i < balls.length; i++) {
        const b = balls[i];
        if (!b || b.state !== BALL.FREE || b.isAbilityBall) continue;
        if (inTeamZone(b.position, this.owner.team)) continue; // already ours: never drag it away from our outfielder
        for (const z of this._zones) {
          const dx = b.position.x - z.x, dz = b.position.z - z.z;
          if (dx * dx + dz * dz <= r2) {
            const prev = this._tracked.get(b);
            if (prev === undefined || prev < z.until) this._tracked.set(b, z.until);
            break;
          }
        }
      }
    }

    for (const [b, until] of this._tracked) {
      if (until <= now || b.state !== BALL.FREE) { this._tracked.delete(b); continue; }
      if (this._stamp.fresh(b)) continue; // the ball field already rolled it this frame
      if (!this._roll(b, dt)) this._tracked.delete(b);
    }

    if (!this._zones.length && !this._tracked.size && this._fieldOn) {
      unregisterBallField(this._field);
      this._fieldOn = false;
    }
  }

  _fieldApply(ball, dt) {
    if (!ball || ball.state !== BALL.FREE || !this._tracked.has(ball)) return;
    this._stamp.touch(ball);
    if (!this._roll(ball, dt)) this._tracked.delete(ball);
  }

  /**
   * Accelerates a rolling ball toward Screws' half. @returns {boolean} false once it has arrived - or once it rests in
   * any of his team's zones (the U outfield around the enemy half included: his outfielder retrieves it there).
   */
  _roll(ball, dt) {
    const court = game.court;
    if (!court) return false;
    const p = this.params;
    const side = court.sideSign(this.owner.team);
    if (depthIntoOwnHalf(side, ball.position.z) >= p.arriveDepth) return false;
    if (inTeamZone(ball.position, this.owner.team)) return false;
    if (ball.position.y > court.floorY + (ball.radius || 0.105) + p.rollHeight) return true; // airborne: wait for it to land
    const lim = court.halfW - 1;
    _dir.set(Math.max(-lim, Math.min(lim, ball.position.x)) - ball.position.x, 0, side * court.halfL * p.targetDepthFrac - ball.position.z);
    const len = _dir.length();
    if (len < 1e-3) return false;
    _dir.multiplyScalar(1 / len);
    const along = ball.velocity.x * _dir.x + ball.velocity.z * _dir.z;
    if (along >= p.maxRollSpeed) return true;
    // A resting ball first gets a nudge above the rolling-resistance dead band, then accelerates smoothly.
    const target = Math.min(p.maxRollSpeed, Math.max(along + p.force * dt, p.startSpeed));
    _v.copy(ball.velocity).addScaledVector(_dir, target - along);
    setBallVelocity(ball, _v);
    return true;
  }

  _clear() {
    this._zones.length = 0;
    this._tracked.clear();
    if (this._fieldOn) { unregisterBallField(this._field); this._fieldOn = false; }
  }
}

// ================================================================== SKILL: Glue Trap Ball

/** Payload riding on the glue ball: spawns exactly one puddle wherever its flight ends. */
class GluePayload {
  constructor(ability) {
    this.ability = ability;
    this.spawned = false;
  }
  onHitPlayer() { return true; } // the glue ball still hits like a ball
  onAfterHitPlayer(ball, hit, outcome) {
    if (outcome === 'ignored' || !hit || !hit.victim) return;
    this._spawn(hit.victim.position);
  }
  onHitSurface(ball, point, normal, isFloor) {
    if (!point) return;
    if (isFloor || !normal) this._spawn(point);
    else this._spawn(_p.copy(point).addScaledVector(normal, 0.3)); // step off a wall before dropping to the floor
  }
  onCaught(ball, catcher) { if (catcher) this._spawn(catcher.position); } // the glue bursts in the catcher's hands
  onEnded(ball) { if (!this.spawned && ball) this._spawn(ball.position); } // blocked, absorbed, despawned...
  _spawn(pos) {
    if (this.spawned) return;
    this.spawned = true;
    this.ability.spawnPuddle(pos);
  }
}

export class ScrewsGlueTrapBall extends AbilityBase {
  static defaults = {
    ...GLUE_DEFAULTS,
    puddleRadius: 2,         // m (spec)
    puddleDuration: 4,       // s (spec)
    slow: 0.6,               // 60% (spec)
    speedMul: 0.9,           // viscous ball: a bit slower than a normal throw
    gravityScale: 0.8,       // and heavier
    maxPuddles: 4,
    aiRange: 16,             // m
  };

  onInitialize() { this._puddles = new Set(); this._refund = false; }

  onEquip() { this.listen(EV.RoundEnded, () => this._clearPuddles()); }

  /** Throws the HELD match ball coated in glue (roster requiresBall). Throw animation/event come from Combat. */
  onCast() {
    const o = this.owner, p = this.params;
    const target = (o.combat && o.combat.currentTarget) || game.nearestEnemy(o, o.position, 30);
    const ball = throwHeldBall(o, {
      style: 'glue', speedMul: p.speedMul, gravityScale: p.gravityScale, payload: new GluePayload(this), target,
    });
    if (!ball) this._refund = true; // the ball left his hand before the release: no cooldown
    chestOf(o, _p);
    sfx(ball ? 'glueThrow' : 'abilityFail', _p, 0.9, 1);
  }

  onCooldown() {
    if (!this._refund) return;
    this._refund = false;
    this.resetCooldown();
  }

  /** Called by the payload: creates a puddle on the floor below `pos` (inside the arena). */
  spawnPuddle(pos) {
    const court = game.court;
    _p.set(pos.x, court ? court.floorY : 0, pos.z);
    if (court && court.isOutOfArena(_p)) return null;
    if (this._puddles.size >= this.params.maxPuddles) {
      const oldest = this._puddles.values().next().value;
      oldest?.expire();
    }
    const p = this.params;
    const puddle = new ScrewsGluePuddle(this.owner, _p, {
      ...GLUE_DEFAULTS, radius: p.puddleRadius, duration: p.puddleDuration, slow: p.slow, color: p.color,
    });
    puddle.onExpired = (x) => this._puddles.delete(x);
    this._puddles.add(puddle);
    return puddle;
  }

  onRoundReset() { this._clearPuddles(); this._refund = false; }

  onUnequip() { this._clearPuddles(); }

  _clearPuddles() {
    for (const pd of Array.from(this._puddles)) pd.expire();
    this._puddles.clear();
  }

  /** Only with the ball in hand (the skill IS the throw): at an enemy in range who is not already stuck. */
  evaluateAI(ctx) {
    if (!ctx || !ctx.holdingBall) return 0;
    const w = this.def.aiWeight ?? 0.6;
    const e = ctx.nearestEnemy;
    if (!e || !(ctx.nearestEnemyDistance < this.params.aiRange)) return 0;
    if (hasStatus(e, 'slow')) return w * 0.3;
    return w;
  }
}

// ================================================================== ULTIMATE: Auto-Turret

export class ScrewsAutoTurret extends AbilityBase {
  static defaults = { ...TURRET_DEFAULTS, placeDistance: 1.5 };

  onInitialize() { this.turret = null; }

  onCast() {
    this._disposeTurret(true);
    const o = this.owner;
    planarForward(o, _f);
    _p.copy(o.position).addScaledVector(_f, this.params.placeDistance);
    const spot = clampToPlayerZone(o, _p); // court rules: inside Screws' own zone
    spot.y = game.court ? game.court.floorY : 0;
    this.turret = new TurretUnit(o, spot, Math.atan2(_f.x, _f.z), this.params);
    this.turret.deploy();
    o.avatar?.playThrow?.(_f); // tosses the folded unit onto the floor
  }

  onTick() {
    if (this.turret && this.turret.state === 'disposed') { this.turret = null; this.endAbility(); }
  }

  onEnd() {
    if (this.turret) this.turret.shutdown(); // releases stored balls, folds, disposes itself
    this.turret = null;
  }

  onRoundReset() { this._disposeTurret(true); }

  onUnequip() { this._disposeTurret(true); }

  _disposeTurret(releaseBalls) {
    if (this.turret) this.turret.dispose(releaseBalls);
    this.turret = null;
  }

  /**
   * Single ball: worth it when the ball lies loose in Screws' own half - high when the turret will reach it at once,
   * lower when it lies elsewhere in his half. Never when it is held, flying or in an enemy zone (the turret only
   * collects from his own infield), nor without enemies on the court.
   */
  evaluateAI(ctx) {
    const o = this.owner;
    if (!ctx || !(ctx.enemiesInfield > 0) || o.zone !== 'infield') return 0;
    const w = this.def.aiWeight ?? 0.8;
    const reach = this.params.collectRadius + this.params.placeDistance + 1;
    const balls = matchBalls();
    let best = 0;
    for (let i = 0; i < balls.length; i++) {
      const b = balls[i];
      if (!b || b.state !== BALL.FREE || b.isAbilityBall || !inTeamInfield(b.position, o.team)) continue;
      best = Math.max(best, b.position.distanceToSquared(o.position) <= reach * reach ? 0.85 : 0.5);
    }
    return Math.min(1, w * best);
  }
}

registerAbility('screws.magnetic_recycle', ScrewsMagneticRecycle);
registerAbility('screws.glue_trap_ball', ScrewsGlueTrapBall);
registerAbility('screws.auto_turret', ScrewsAutoTurret);
