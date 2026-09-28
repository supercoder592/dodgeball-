// ---------------------------------------------------------------------------------------------------------------
// BallManager (system, ORDER.BALLS) - owns every ball in play.
//
//   matchBalls   the regulation balls (6 by default) placed on the centre line for the opening rush
//   active       every ball currently simulated (match balls + ability projectiles in flight / fading)
//   fields       ability field effects applied to live balls before gravity ({ apply(ball, dt) })
// Lifecycle rules enforced here:
//   * ability projectiles are recycled into a pool once they stop being live (short shrink-fade), or 1 s after
//     being spawned if nobody launched them;
//   * a ball that leaves the arena emits EV.BallOutOfBounds and respawns at its centre-line slot after 2 s;
//   * a loose ball resting where no player can reach it (run-off, an empty outfield strip, a bleacher step) for
//     2 s is returned to the nearest reachable spot (a ball boy, in broadcast terms);
//   * loose balls collide with each other (equal-mass impulse exchange).
// ---------------------------------------------------------------------------------------------------------------
import * as THREE from 'three';
import { game } from '../game.js';
import { EV } from '../core/events.js';
import { BALL_RADIUS, ZONE } from '../core/constants.js';
import { Ball, BALL_STATE, BALL_PHYS, zoneBounds } from './ball.js';
import { getBallTextures } from './ballTextures.js';

const _tmp = new THREE.Vector3();
const _best = new THREE.Vector3();

export class BallManager {
  /** Tuning (seconds, metres). */
  static defaults = Object.freeze({
    outOfArenaRespawnDelay: 2,
    unreachableRespawnDelay: 2,
    unreachableCheckInterval: 0.25,
    unreachableMaxSpeed: 1.5,     // only slow loose balls are judged unreachable
    reachMargin: BALL_PHYS.pickupZoneReach, // m beyond a player's confinement that still counts as reachable
    relocateHideTime: 0.5,        // s hidden while a ball boy returns it
    relocateInset: 0.3,           // m inside the confinement where returned balls are placed
    abilityFadeTime: 0.35,        // s shrink-out of a spent ability projectile
    abilityLaunchTimeout: 1,      // s a spawned-but-never-launched ability ball survives
    abilityPoolPrewarm: 2,
    ballBallRestitution: 0.7,
    defaultBallCount: 6,
  });

  constructor() {
    this.tuning = { ...BallManager.defaults };
    /** @type {Ball[]} */
    this.matchBalls = [];
    /** @type {Ball[]} every simulated ball */
    this.active = [];
    this.group = new THREE.Group();
    this.group.name = 'Balls';
    this._pool = [];
    this._fields = [];
    this._iter = [];
    this._nextAbilityId = 1;
    this._unsubs = [];
  }

  async init() {
    getBallTextures(); // generate the shared pebble textures during boot, not at the first throw
    this._ensureInScene();
    for (let i = 0; i < this.tuning.abilityPoolPrewarm; i++) {
      const b = this._createAbilityBall();
      this._retire(b);
    }
    const on = (name, fn) => this._unsubs.push(game.events.on(name, fn));
    // Safety net: whoever is eliminated lets go of the ball (states.js normally does this).
    on(EV.PlayerEliminated, (e) => {
      const p = e && e.player;
      if (p && p.combat && p.combat.hasBall && typeof p.combat.dropBall === 'function') p.combat.dropBall();
    });
    // Fallback when no Match set the balls up.
    on(EV.RoundStarted, () => { if (!this.matchBalls.length) this.setupMatchBalls(); });
  }

  _ensureInScene() {
    if (!this.group.parent && game.scene) game.scene.add(this.group);
  }

  // ================================================================== match balls
  /**
   * Creates (or re-uses) the match balls at `positions` (Court.openingBallPositions by default) and resets them.
   * @param {THREE.Vector3[]} [positions]
   */
  setupMatchBalls(positions) {
    this._ensureInScene();
    const list = positions && positions.length ? positions : this._defaultPositions();
    while (this.matchBalls.length < list.length) {
      const b = new Ball(`m${this.matchBalls.length}`, false);
      this._addToScene(b);
      this.matchBalls.push(b);
    }
    while (this.matchBalls.length > list.length) {
      const b = this.matchBalls.pop();
      this._removeActive(b);
      b.dispose();
    }
    this.recycleAllAbilityBalls();
    for (let i = 0; i < this.matchBalls.length; i++) {
      const b = this.matchBalls[i];
      b.homePosition.copy(list[i]);
      b.resetTo(list[i]);
      b.history.clear();
      if (!this.active.includes(b)) this.active.push(b);
    }
  }

  /** New round: every match ball back to its slot (or `positions`), ability projectiles removed. */
  resetForRound(positions) {
    const list = positions && positions.length ? positions : null;
    if (!this.matchBalls.length || (list && list.length !== this.matchBalls.length)) { this.setupMatchBalls(list); return; }
    this.recycleAllAbilityBalls();
    for (let i = 0; i < this.matchBalls.length; i++) {
      const b = this.matchBalls[i];
      if (list) b.homePosition.copy(list[i]);
      b.resetTo(b.homePosition);
      b.history.clear();
      if (!this.active.includes(b)) this.active.push(b);
    }
  }

  _defaultPositions() {
    const count = (game.match && game.match.rules && game.match.rules.ballCount) || this.tuning.defaultBallCount;
    if (game.court && typeof game.court.openingBallPositions === 'function') return game.court.openingBallPositions(count);
    const out = [];
    for (let i = 0; i < count; i++) out.push(new THREE.Vector3((i / Math.max(1, count - 1) - 0.5) * 7.4, BALL_RADIUS, i % 2 ? 0.6 : -0.6));
    return out;
  }

  // ================================================================== ability balls
  /**
   * A temporary projectile at `pos` with `style`, ready to be launched (abilityUtil.throwAbilityBall). Recycled
   * automatically once it stops being live.
   * @returns {Ball}
   */
  spawnAbilityBall(pos, style = 'standard') {
    this._ensureInScene();
    const b = this._pool.pop() || this._createAbilityBall();
    b.recycled = false;
    b.resetTo(pos || _tmp.set(0, 1, 0));
    b.grounded = false;
    b.setStyle(style || 'standard');
    b._awaitLaunch = this.tuning.abilityLaunchTimeout;
    b.history.clear();
    b.root.visible = true;
    this.active.push(b);
    return b;
  }

  _createAbilityBall() {
    const b = new Ball(`a${this._nextAbilityId++}`, true);
    this._addToScene(b);
    return b;
  }

  /**
   * Removes a ball from play: ability balls return to the pool; match balls respawn at their home slot.
   * @param {Ball} ball
   */
  recycle(ball) {
    if (!ball) return;
    if (!ball.isAbilityBall) { ball.resetTo(ball.homePosition); return; }
    if (ball.recycled) return;
    this._retire(ball);
    this._removeActive(ball);
    this._pool.push(ball);
  }

  recycleAllAbilityBalls() {
    for (let i = this.active.length - 1; i >= 0; i--) {
      const b = this.active[i];
      if (b.isAbilityBall) this.recycle(b);
    }
  }

  /** Hide + detach without events (pool storage). */
  _retire(b) {
    if (b.holder) b._detachFromHolder();
    if (b.state === BALL_STATE.LIVE || b.state === BALL_STATE.STASIS) b._endLive('recycled');
    b.state = BALL_STATE.DESPAWNED;
    b._despawnRemaining = Infinity;
    b._despawnDone = false;
    b._fade = 0;
    b._awaitLaunch = 0;
    b.velocity.set(0, 0, 0);
    b.recycled = true;
    b.root.visible = false;
    b.trail.reset();
  }

  _addToScene(b) {
    this.group.add(b.root);
    this.group.add(b.trail.mesh);
  }

  _removeActive(b) {
    const i = this.active.indexOf(b);
    if (i >= 0) this.active.splice(i, 1);
  }

  // ================================================================== queries
  /**
   * Nearest active ball to `pos` accepted by `filter` within `maxDist`.
   * @param {THREE.Vector3} pos
   * @param {(b:Ball)=>boolean} [filter]
   * @param {number} [maxDist]
   * @returns {Ball|null}
   */
  findNearest(pos, filter = null, maxDist = Infinity) {
    let best = null, bestD = maxDist * maxDist;
    for (const b of this.active) {
      if (b.recycled || b.state === BALL_STATE.DESPAWNED) continue;
      if (filter && !filter(b)) continue;
      const d = b.position.distanceToSquared(pos);
      if (d <= bestD) { bestD = d; best = b; }
    }
    return best;
  }

  /**
   * Live hostile balls closing in on `player` (cleared first, not sorted): thrown by an enemy (or nobody), not a
   * pass, moving toward the player in the horizontal plane. Callers refine with Trajectory.predictImpact.
   * @param {object} player
   * @param {Ball[]} [out]
   * @returns {Ball[]}
   */
  incomingLive(player, out = []) {
    out.length = 0;
    if (!player || !player.position) return out;
    for (const b of this.active) {
      if (b.state !== BALL_STATE.LIVE || b.isPass) continue;
      const t = b.lastThrower;
      if (t === player || (t && !game.areEnemies(t, player))) continue;
      const closing = (player.position.x - b.position.x) * b.velocity.x + (player.position.z - b.position.z) * b.velocity.z;
      if (closing > 0) out.push(b);
    }
    return out;
  }

  // ================================================================== fields
  /** @param {{apply(ball:Ball, dt:number):void}} effect */
  registerField(effect) {
    if (effect && typeof effect.apply === 'function' && !this._fields.includes(effect)) this._fields.push(effect);
  }
  unregisterField(effect) {
    const i = this._fields.indexOf(effect);
    if (i >= 0) this._fields.splice(i, 1);
  }
  /** Called by live balls at the start of their step (before payload.onTick and gravity). */
  applyFields(ball, dt) {
    const list = this._fields;
    for (let i = 0; i < list.length && ball.state === BALL_STATE.LIVE; i++) {
      const f = list[i];
      try { f.apply(ball, dt); } catch (e) {
        if (!f.__warned) { f.__warned = true; console.error('[balls] field effect threw', e); }
      }
    }
  }

  // ================================================================== loop
  fixedUpdate(dt) {
    this._ensureInScene();
    // Iterate a snapshot: recycling or spawning during the step must not skip / double-step balls.
    const it = this._iter;
    it.length = 0;
    for (let i = 0; i < this.active.length; i++) it.push(this.active[i]);
    for (let i = 0; i < it.length; i++) {
      const b = it[i];
      if (b.recycled) continue;
      b.fixedUpdate(dt);
      this._lifecycle(b, dt);
    }
    it.length = 0;
    this._collideLooseBalls();
  }

  update(dt, realDt) {
    for (let i = 0; i < this.active.length; i++) this.active[i].update(dt, realDt);
  }

  lateUpdate(dt, realDt) {
    for (let i = 0; i < this.active.length; i++) this.active[i].lateUpdate(dt, realDt);
    // Pooled balls may still be fading their trails out.
    for (let i = 0; i < this._pool.length; i++) {
      const b = this._pool[i];
      if (b.trail.mesh.visible) b.trail.update(dt, b.root.position, 0, false, null);
    }
  }

  _lifecycle(b, dt) {
    const T = this.tuning;
    // --- ability projectiles: fade out once spent, recycle
    if (b.isAbilityBall) {
      if (b.state === BALL_STATE.DESPAWNED && b._despawnDone) { this.recycle(b); return; }
      if (b.state === BALL_STATE.FREE) {
        if (b._awaitLaunch > 0) {
          b._awaitLaunch -= dt;
          if (b._awaitLaunch <= 0) { b._awaitLaunch = 0; b._fade = 1e-4; }
        } else if (b._fade === 0) {
          b._fade = 1e-4;
        }
      }
      if (b._fade > 0) {
        b._fade += dt / Math.max(1e-3, T.abilityFadeTime);
        if (b._fade >= 1) { this.recycle(b); return; }
      }
    }

    // --- left the arena: respawn at the centre line after 2 s
    const court = game.court;
    if ((b.state === BALL_STATE.FREE || b.state === BALL_STATE.LIVE) && this._outOfArena(b.position)) {
      game.events.emit(EV.BallOutOfBounds, { ball: b, position: b.position.clone() });
      if (b.isAbilityBall) { this.recycle(b); return; }
      b.despawn(T.outOfArenaRespawnDelay, b.homePosition);
      return;
    }

    // --- dead ball: resting where nobody can reach it -> ball boy
    if (b.isAbilityBall || b.state !== BALL_STATE.FREE || !court) { b._unreachableTime = 0; return; }
    if (game.match && game.match.isPlaying === false) { b._unreachableTime = 0; return; }
    if (b.velocity.lengthSq() > T.unreachableMaxSpeed * T.unreachableMaxSpeed) { b._unreachableTime = 0; return; }
    b._reachCheck -= dt;
    if (b._reachCheck > 0) { if (b._unreachableTime > 0) b._unreachableTime += dt; return; }
    b._reachCheck = T.unreachableCheckInterval;
    if (this._isReachable(b.position)) { b._unreachableTime = 0; return; }
    b._unreachableTime += dt;
    if (b._unreachableTime >= T.unreachableRespawnDelay) {
      b._unreachableTime = 0;
      if (this._nearestReachableSpot(b.position, _best)) {
        _best.y = court.floorY + BALL_RADIUS;
        b.despawn(T.relocateHideTime, _best);
      } else {
        b.despawn(T.relocateHideTime, b.homePosition);
      }
    }
  }

  _outOfArena(p) {
    const court = game.court;
    if (court && typeof court.isOutOfArena === 'function') return court.isOutOfArena(p);
    return Math.abs(p.x) > 14 || Math.abs(p.z) > 20 || p.y < -2 || p.y > 40;
  }

  /** Inside (or within pickup reach of) some player's movement confinement. */
  _isReachable(pos) {
    const m = this.tuning.reachMargin;
    for (const p of game.players) {
      if (!p || p.team === undefined || p.team < 0) continue;
      const b = zoneBounds(p.team, p.zone || ZONE.INFIELD);
      if (!b) return true;
      if (pos.x >= b.minX - m && pos.x <= b.maxX + m && pos.z >= b.minZ - m && pos.z <= b.maxZ + m) return true;
    }
    return game.players.length === 0; // no players (menus): leave balls alone
  }

  /** Closest point (planar) inside any player's confinement, into `out`. */
  _nearestReachableSpot(pos, out) {
    const inset = this.tuning.relocateInset;
    let bestD = Infinity;
    for (const p of game.players) {
      if (!p || p.team === undefined || p.team < 0) continue;
      const b = zoneBounds(p.team, p.zone || ZONE.INFIELD);
      if (!b) continue;
      const x = Math.min(b.maxX - inset, Math.max(b.minX + inset, pos.x));
      const z = Math.min(b.maxZ - inset, Math.max(b.minZ + inset, pos.z));
      const d = (x - pos.x) * (x - pos.x) + (z - pos.z) * (z - pos.z);
      if (d < bestD) { bestD = d; out.set(x, pos.y, z); }
    }
    return bestD < Infinity;
  }

  /** Equal-mass elastic-ish collisions between loose balls (positional separation + normal impulse). */
  _collideLooseBalls() {
    const list = this.active, e = this.tuning.ballBallRestitution;
    const floorY = game.court ? game.court.floorY : 0;
    for (let i = 0; i < list.length; i++) {
      const a = list[i];
      if (a.state !== BALL_STATE.FREE || a.recycled || a._fade > 0) continue;
      for (let j = i + 1; j < list.length; j++) {
        const b = list[j];
        if (b.state !== BALL_STATE.FREE || b.recycled || b._fade > 0) continue;
        _tmp.subVectors(b.position, a.position);
        const rr = a.radius + b.radius;
        const d2 = _tmp.lengthSq();
        if (d2 >= rr * rr) continue;
        const d = Math.sqrt(d2);
        if (d > 1e-6) _tmp.multiplyScalar(1 / d); else _tmp.set(1, 0, 0);
        const push = (rr - d) * 0.5 + 1e-4;
        a.position.addScaledVector(_tmp, -push);
        b.position.addScaledVector(_tmp, push);
        if (a.position.y < floorY + a.radius) a.position.y = floorY + a.radius;
        if (b.position.y < floorY + b.radius) b.position.y = floorY + b.radius;
        const rel = (b.velocity.x - a.velocity.x) * _tmp.x + (b.velocity.y - a.velocity.y) * _tmp.y + (b.velocity.z - a.velocity.z) * _tmp.z;
        if (rel < 0) {
          const j2 = -(1 + e) * rel * 0.5;
          a.velocity.addScaledVector(_tmp, -j2);
          b.velocity.addScaledVector(_tmp, j2);
          if (a.velocity.y > 0.3) a.grounded = false;
          if (b.velocity.y > 0.3) b.grounded = false;
        }
      }
    }
  }

  dispose() {
    for (const u of this._unsubs) u();
    this._unsubs = [];
    for (const b of [...this.active, ...this._pool]) b.dispose();
    this.active = [];
    this.matchBalls = [];
    this._pool = [];
    this._fields = [];
    this.group.removeFromParent();
  }
}
