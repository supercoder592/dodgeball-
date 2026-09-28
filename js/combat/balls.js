// ---------------------------------------------------------------------------------------------------------------
// BallManager (system, ORDER.BALLS) - owns the ball in play. Single-ball rules (WEB_ARCHITECTURE.md §3.2):
//
//   matchBalls   the regulation ball(s): exactly ONE by default (`ball`), handed to the server every preRound
//   active       every ball currently simulated (the match ball; legacy ability projectiles are no longer created -
//                abilities empower the held ball through abilityUtil.throwEmpoweredBall)
//   fields       ability field effects applied to live balls before gravity ({ apply(ball, dt) })
// Lifecycle rules enforced here:
//   * awardBall(ball, team, cause, nearPos, delay): hides the ball for `delay`, then drops it at the feet of the
//     receiving team's best player (Match.pickAwardReceiver), reserved for that team for a moment (possession
//     violations, out of arena, Grand Vanish) - EV.BallAwarded;
//   * a ball that leaves the arena emits EV.BallOutOfBounds and is awarded to the opponents of its last thrower;
//   * a loose ball resting where no eligible player can reach it (run-off beyond the U, a bleacher step, beyond the
//     outer corners) for 2 s is returned by a ball boy to the nearest reachable spot (EV.BallAwarded 'ballBoy');
//   * loose balls collide with each other (equal-mass impulse exchange; kept for custom multi-ball setups).
// ---------------------------------------------------------------------------------------------------------------
import * as THREE from 'three';
import { game } from '../game.js';
import { EV } from '../core/events.js';
import { BALL_RADIUS, ZONE, TEAM, opponent } from '../core/constants.js';
import { Ball, BALL_STATE, BALL_PHYS, zoneBounds } from './ball.js';
import { getBallTextures } from './ballTextures.js';
import { regionDistanceXZ, regionClampXZ, insetRegion } from '../world/courtMath.js';

const _tmp = new THREE.Vector3();
const _best = new THREE.Vector3();
const _spot = new THREE.Vector3();
const _xz = { x: 0, z: 0 };
const _zone = { team: TEAM.NONE, zone: ZONE.INFIELD };
const _region = { minX: 0, maxX: 0, minZ: 0, maxZ: 0, hole: null };
const _regionHole = { minX: 0, maxX: 0, minZ: 0, maxZ: 0 };

/** Region `b` inset by `m` into the shared scratch region (hole object preserved, allocation-free). */
function insetScratch(b, m) {
  _region.hole = b.hole ? _regionHole : null;
  return insetRegion(b, m, _region);
}

/** A player that counts for reach / ball-boy purposes: standing (not ragdolling) with a team. */
function eligible(p) {
  if (!p || p.team === undefined || p.team < 0) return false;
  if (p.health && p.health.isAlive === false) return false;
  return !(game.match && game.match.isAwaitingOutfield && game.match.isAwaitingOutfield(p));
}

export class BallManager {
  /** Tuning (seconds, metres). */
  static defaults = Object.freeze({
    outOfArenaAwardDelay: 1.5,    // s hidden before a ball that left the arena reappears with the opponents
    awardDelay: 0.6,              // s hidden during a possession hand-over (awardBall default)
    reserveTime: 2.0,             // s an awarded ball stays reserved for its team after it reappears
    unreachableRespawnDelay: 2,
    unreachableCheckInterval: 0.25,
    unreachableMaxSpeed: 1.5,     // only slow loose balls are judged unreachable
    unownedMaxSpeed: 3.5,         // ... except outside every zone (run-off / bleachers), judged while rolling faster
    reachMargin: BALL_PHYS.pickupZoneReach, // m beyond a player's confinement that still counts as reachable
    relocateHideTime: 0.5,        // s hidden while a ball boy returns it
    relocateInset: 0.3,           // m inside the confinement where returned balls are placed
    abilityFadeTime: 0.35,        // s shrink-out of a spent ability projectile
    abilityLaunchTimeout: 1,      // s a spawned-but-never-launched ability ball survives
    abilityPoolPrewarm: 0,        // single-ball rule: no ability balls are ever conjured
    ballBallRestitution: 0.7,
    defaultBallCount: 1,
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
    this._warnedAbility = false;
  }

  /** The match ball (single-ball rules), or null before a match. */
  get ball() { return this.matchBalls[0] || null; }

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
   * Creates (or re-uses) the match balls at `positions` (one ball at the Home serve point by default) and resets them.
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

  /**
   * New round: every match ball back to its slot (or `positions` - the serve point), reservation and custody cleared,
   * ability projectiles removed. Match then hands the ball to the server.
   */
  resetForRound(positions) {
    const list = positions && positions.length ? positions : null;
    if (!this.matchBalls.length || (list && list.length !== this.matchBalls.length)) { this.setupMatchBalls(list); }
    else {
      this.recycleAllAbilityBalls();
      for (let i = 0; i < this.matchBalls.length; i++) {
        const b = this.matchBalls[i];
        if (list) b.homePosition.copy(list[i]);
        b.resetTo(b.homePosition);
        b.history.clear();
        if (!this.active.includes(b)) this.active.push(b);
      }
    }
    for (const b of this.matchBalls) { b.clearReservation(); b.custodyTeam = TEAM.NONE; b._unreachableTime = 0; }
  }

  _defaultPositions() {
    const count = Math.max(1, (game.match && game.match.rules && game.match.rules.ballCount) || this.tuning.defaultBallCount);
    if (count === 1 && game.court && typeof game.court.servePoint === 'function') return [game.court.servePoint(TEAM.HOME)];
    if (game.court && typeof game.court.openingBallPositions === 'function') return game.court.openingBallPositions(count);
    const out = [];
    for (let i = 0; i < count; i++) out.push(new THREE.Vector3((i / Math.max(1, count - 1) - 0.5) * 7.4, BALL_RADIUS, i % 2 ? 0.6 : -0.6));
    return out;
  }

  // ================================================================== ability balls
  /**
   * Single-ball rule: abilities never conjure a second dodgeball - they empower the held match ball
   * (abilityUtil.throwEmpoweredBall). Always returns null (warns once). The pool / recycle code below only serves
   * legacy callers of _spawnAbilityBallUnchecked (none in the game).
   * @returns {null}
   */
  spawnAbilityBall(pos, style = 'standard') { // eslint-disable-line no-unused-vars
    if (!this._warnedAbility) {
      this._warnedAbility = true;
      console.warn('[balls] single-ball rule: abilities empower the held ball (abilityUtil.throwEmpoweredBall)');
    }
    return null;
  }

  /** @deprecated legacy temporary projectile (never used by the single-ball game). */
  _spawnAbilityBallUnchecked(pos, style = 'standard') {
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

    // --- left the arena: the opponents of its last thrower get the ball (no more centre-line respawn)
    const court = game.court;
    if ((b.state === BALL_STATE.FREE || b.state === BALL_STATE.LIVE) && this._outOfArena(b.position)) {
      if (b.isAbilityBall) {
        game.events.emit(EV.BallOutOfBounds, { ball: b, position: b.position.clone(), awardedTo: TEAM.NONE });
        this.recycle(b);
        return;
      }
      const team = this._outOfArenaTeam(b);
      const position = b.position.clone();
      game.events.emit(EV.BallOutOfBounds, { ball: b, position, awardedTo: team });
      this.awardBall(b, team, 'outOfArena', position, T.outOfArenaAwardDelay);
      return;
    }

    // --- dead ball: resting outside every zone or where no eligible player can reach it -> ball boy
    if (b.isAbilityBall || b.state !== BALL_STATE.FREE || !court) { b._unreachableTime = 0; return; }
    if (game.match && game.match.isPlaying === false) { b._unreachableTime = 0; return; }
    // A ball outside every zone (run-off, bleachers) is judged while still rolling a little faster: it would only
    // trickle along the pads for seconds otherwise.
    const owned = court.zoneAt ? !!court.zoneAt(b.position, _zone) : true;
    const maxV = owned ? T.unreachableMaxSpeed : T.unownedMaxSpeed;
    if (b.velocity.lengthSq() > maxV * maxV) { b._unreachableTime = 0; return; }
    b._reachCheck -= dt;
    if (b._reachCheck > 0) { if (b._unreachableTime > 0) b._unreachableTime += dt; return; }
    b._reachCheck = T.unreachableCheckInterval;
    // Owned (resting in some team's zone on the painted lines) and reachable: that team retrieves it. A ball resting
    // outside every zone (just past the outer line, run-off, bleachers) belongs to nobody - the ball boy returns it
    // even when a player could technically lean over the line for it (the AI claims by zone only).
    if (owned && this._isReachable(b.position)) { b._unreachableTime = 0; return; }
    b._unreachableTime += dt;
    if (b._unreachableTime >= T.unreachableRespawnDelay) {
      b._unreachableTime = 0;
      if (!this.nearestReachableSpot(b.position, _best)) _best.copy(b.homePosition);
      const spot = _best;
      spot.y = court.floorY + BALL_RADIUS;
      b.despawn(T.relocateHideTime, spot);
      const owner = court.zoneAt ? court.zoneAt(spot, _zone) : null;
      game.events.emit(EV.BallAwarded, {
        ball: b, team: owner ? owner.team : TEAM.NONE, player: null, cause: 'ballBoy', position: spot.clone(), delay: T.relocateHideTime,
      });
    }
  }

  /** Team that gets a ball which left the arena: opponents of the last thrower, else of the last holder, else the
   * team whose half is nearer the exit point. */
  _outOfArenaTeam(b) {
    const t = b.lastThrower, h = b.lastHolder;
    if (t && (t.team === TEAM.HOME || t.team === TEAM.AWAY)) return opponent(t.team);
    if (h && (h.team === TEAM.HOME || h.team === TEAM.AWAY)) return opponent(h.team);
    return b.position.z < 0 ? TEAM.HOME : TEAM.AWAY;
  }

  _outOfArena(p) {
    const court = game.court;
    if (court && typeof court.isOutOfArena === 'function') return court.isOutOfArena(p);
    return Math.abs(p.x) > 14 || Math.abs(p.z) > 20 || p.y < -2 || p.y > 40;
  }

  /** Within pickup reach of some eligible player's zone region (hole-aware). */
  _isReachable(pos) {
    const m = this.tuning.reachMargin;
    let any = false;
    for (const p of game.players) {
      if (!eligible(p)) continue;
      any = true;
      const b = zoneBounds(p.team, p.zone || ZONE.INFIELD);
      if (!b) return true;
      if (regionDistanceXZ(b, pos.x, pos.z) <= m) return true;
    }
    return !any; // no eligible players (menus, everyone ragdolling): leave the ball alone
  }

  /**
   * Closest point (planar) of any eligible player's zone region, inset by relocateInset, into `out` (y kept).
   * @returns {boolean} false when nobody is eligible
   */
  nearestReachableSpot(pos, out) {
    const inset = this.tuning.relocateInset;
    let bestD = Infinity;
    for (const p of game.players) {
      if (!eligible(p)) continue;
      const b = zoneBounds(p.team, p.zone || ZONE.INFIELD);
      if (!b) continue;
      regionClampXZ(insetScratch(b, inset), pos.x, pos.z, _xz);
      const d = (_xz.x - pos.x) * (_xz.x - pos.x) + (_xz.z - pos.z) * (_xz.z - pos.z);
      if (d < bestD) { bestD = d; out.set(_xz.x, pos.y, _xz.z); }
    }
    return bestD < Infinity;
  }

  /** Zone owning `pos` ({team, zone} into `out`, or null when unreachable) - the dead-ball rule. */
  ownerOf(pos, out = { team: TEAM.NONE, zone: ZONE.INFIELD }) {
    const court = game.court;
    return court && court.zoneAt ? court.zoneAt(pos, out) : null;
  }

  /**
   * Hands `ball` to `team`: it disappears for `delay` s, then reappears at the feet of that team's best receiver
   * (Match.pickAwardReceiver near `nearPos`: an infield player first), reserved for the team for delay + reserveTime
   * so the other side cannot snatch it. Without a receiver it reappears at the team's serve point.
   * @param {Ball} ball
   * @param {number} team TEAM.HOME | TEAM.AWAY
   * @param {'possession'|'outOfArena'|'ballBoy'|'vanish'|string} cause
   * @param {THREE.Vector3} [nearPos]
   * @param {number} [delay] s hidden (tuning.awardDelay)
   * @returns {object|null} the receiving player (or null)
   */
  awardBall(ball, team, cause = 'possession', nearPos = null, delay = this.tuning.awardDelay) {
    if (!ball || (team !== TEAM.HOME && team !== TEAM.AWAY)) return null;
    const court = game.court;
    const T = this.tuning;
    const d = Math.max(0.05, Number.isFinite(delay) ? delay : T.awardDelay);
    const near = nearPos || ball.position;
    const match = game.match;
    const receiver = match && typeof match.pickAwardReceiver === 'function' ? match.pickAwardReceiver(team, near) : null;
    const floorY = court ? court.floorY : 0;
    if (receiver && receiver.position) {
      const yaw = receiver.yaw || 0;
      _spot.set(receiver.position.x + Math.sin(yaw) * 0.45, floorY + BALL_RADIUS + 0.05, receiver.position.z + Math.cos(yaw) * 0.45);
      const b = zoneBounds(receiver.team, receiver.zone || ZONE.INFIELD);
      if (b) { regionClampXZ(insetScratch(b, 0.1), _spot.x, _spot.z, _xz); _spot.x = _xz.x; _spot.z = _xz.z; }
    } else if (court && typeof court.servePoint === 'function') {
      _spot.copy(court.servePoint(team));
      _spot.y = floorY + BALL_RADIUS + 0.05;
    } else {
      _spot.set(0, floorY + BALL_RADIUS + 0.05, team === TEAM.HOME ? -3.6 : 3.6);
    }
    const holder = ball.holder;
    if (holder && holder.combat && holder.combat.isCharging && holder.combat.cancelCharge) holder.combat.cancelCharge();
    ball.reserve(team, d + T.reserveTime);
    ball.custodyTeam = TEAM.NONE;
    ball.despawn(d, _spot);
    ball._unreachableTime = 0;
    game.events.emit(EV.BallAwarded, { ball, team, player: receiver || null, cause, position: _spot.clone(), delay: d });
    return receiver || null;
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
