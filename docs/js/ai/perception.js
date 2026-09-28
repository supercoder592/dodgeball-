// ---------------------------------------------------------------------------------------------------------------
// BotPerception - what a bot KNOWS about incoming enemy balls, with human reaction delays.
//
//  every frame      : game.balls.incomingLive(self) -> new throws enter a fixed-size observation queue with a
//                     reaction delay sampled from the difficulty profile (longer for throws from outside the field of
//                     view; some rear throws are only sensed once they are within the peripheral radius)
//  after the delay  : the ball is "noticed" and its impact is predicted with combat's Trajectory.predictImpact
//                     (re-predicted at ~16 Hz or when the ball's velocity changes: bounce, deflection, magnet...)
//  mostUrgent       : the noticed ball that will hit soonest (the bot's threat layer answers it)
//  wind-up reading  : a throw whose charge the bot watched (thrower in view, winding up at it) is noticed sooner -
//                     reaction x (1 - anticipation x watched/windupFullPrime), like a player reading the arm
//  Danger Sense     : Specter's passive (EV.DangerSense) shortens the delay of a locked-on fastball
// Allocation-free: entries are pooled objects recycled in place.
// ---------------------------------------------------------------------------------------------------------------
import * as THREE from 'three';
import { game } from '../game.js';
import * as Solver from '../combat/throwSolver.js';
import { GRAVITY, BALL_RADIUS, ZONE } from '../core/constants.js';
import { jitter, planarAngleDeg, planarDistance, predictCapsuleImpact } from './aiMath.js';
import { BALL_STATE, bodyRadius, bodyHeight, forwardX, forwardZ } from './botWorld.js';

/** Perception tuning (seconds, metres, m/s). */
export const PERCEPTION = Object.freeze({
  capacity: 12,               // simultaneous tracked throws
  repredictInterval: 0.06,    // s between two trajectory predictions of the same ball
  velocityTolerance: 0.75,    // m/s change of a ball's velocity forcing an immediate re-prediction
  seenFromLaunchWindow: 0.15, // a throw first seen within this time of its launch counts its reaction from the launch
  minReaction: 0.02,          // s, fastest possible reaction
  windupFullPrime: 0.6,       // s of watched wind-up that grants the full anticipation bonus
  passedGrace: 0.05,          // s past the predicted impact before an unresolved prediction is dropped
  fallbackGravityScale: 0.65, // thrown gravity scale when a ball does not expose one (BASE_COMBAT.thrownGravityScale)
});

const ZERO = new THREE.Vector3();
let _solverBroken = false;
let _incomingBroken = false;

/** Throw id of a ball: its launch time (a caught-and-rethrown ball is a new throw). */
export function launchTimeOf(ball) { return ball && Number.isFinite(ball.launchTime) ? ball.launchTime : 0; }

/**
 * Seconds until `ball` touches `player`'s body, or -1 when it will not within `maxTime`. Uses combat's
 * Trajectory.predictImpact (same capsule + field model as the hit test) and falls back to an analytic ballistic sweep
 * when the solver is unavailable. Writes the predicted contact point into `outPoint`.
 */
export function predictBallImpact(ball, player, maxTime, outPoint) {
  const T = Solver.Trajectory;
  if (!_solverBroken && T && typeof T.predictImpact === 'function') {
    try {
      const r = T.predictImpact(ball, player, maxTime);
      if (!r) return -1;
      if (Number.isFinite(r.t)) {
        outPoint.copy(r.point || ball.position);
        return Math.max(0, r.t);
      }
    } catch (e) {
      _solverBroken = true;
      console.warn('[ai] Trajectory.predictImpact threw - using the built-in ballistic predictor', e);
    }
  }
  const g = GRAVITY * (Number.isFinite(ball.gravityScale) ? ball.gravityScale : PERCEPTION.fallbackGravityScale);
  const floorY = game.court ? game.court.floorY : 0;
  return predictCapsuleImpact(ball.position, ball.velocity || ZERO, g, ball.radius || BALL_RADIUS, player.position,
    bodyRadius(player), bodyHeight(player), maxTime, outPoint, floorY);
}

/**
 * Live enemy balls flying toward `self` into `out` (cleared first). BallManager.incomingLive when available,
 * else a scan of the active balls (live, thrown by an enemy, closing in).
 */
export function gatherIncoming(self, out) {
  out.length = 0;
  const bm = game.balls;
  if (!bm || !self) return out;
  if (typeof bm.incomingLive === 'function' && !_incomingBroken) {
    try {
      const r = bm.incomingLive(self, out);
      if (Array.isArray(r) && r !== out) for (let i = 0; i < r.length; i++) out.push(r[i]); // returned a new array
      return out;
    } catch (e) {
      _incomingBroken = true;
      out.length = 0;
      console.warn('[ai] BallManager.incomingLive threw - scanning active balls instead', e);
    }
  }
  const list = bm.active || bm.matchBalls;
  if (!list) return out;
  for (let i = 0; i < list.length; i++) {
    const b = list[i];
    if (!b || b.state !== BALL_STATE.LIVE || !b.velocity) continue;
    if (b.lastThrower && !game.areEnemies(self, b.lastThrower)) continue;
    const closing = (self.position.x - b.position.x) * b.velocity.x + (self.position.z - b.position.z) * b.velocity.z;
    if (closing > 0) out.push(b);
  }
  return out;
}

/** A perceived incoming ball predicted to hit the bot (copied out of the perception each frame). */
export class BotThreat {
  constructor() {
    this.ball = null;
    this.launchTime = 0;
    /** Predicted seconds until the ball touches the body capsule. */
    this.timeToImpact = Infinity;
    /** Predicted contact point (ball centre). */
    this.impactPoint = new THREE.Vector3();
    /** Thrown from outside the bot's field of view (outfield throws at its back). */
    this.fromBehind = false;
    this.speedKmh = 0;
    /** game.time.now at which the bot became aware of the ball. */
    this.noticedAt = 0;
  }
  get isValid() { return this.ball !== null; }
  clear() { this.ball = null; this.launchTime = 0; this.timeToImpact = Infinity; this.fromBehind = false; this.speedKmh = 0; this.noticedAt = 0; return this; }
  copyFrom(o) {
    this.ball = o.ball; this.launchTime = o.launchTime; this.timeToImpact = o.timeToImpact; this.impactPoint.copy(o.impactPoint);
    this.fromBehind = o.fromBehind; this.speedKmh = o.speedKmh; this.noticedAt = o.noticedAt;
    return this;
  }
  /** @param {Entry} e */
  fromEntry(e) {
    const b = e.ball;
    this.ball = b; this.launchTime = e.launchTime; this.timeToImpact = e.tti; this.impactPoint.copy(e.impact);
    this.fromBehind = e.fromBehind; this.noticedAt = e.noticeAt;
    this.speedKmh = Number.isFinite(b.speedKmh) ? b.speedKmh : (b.velocity ? b.velocity.length() * 3.6 : 0);
    return this;
  }
}

/** One tracked throw (pooled). */
class Entry {
  constructor() {
    this.impact = new THREE.Vector3();
    this.predVel = new THREE.Vector3();
    this.reset();
  }
  reset() {
    this.ball = null; this.launchTime = 0; this.noticeAt = Infinity; this.reaction = 0;
    this.blind = false; this.fromBehind = false; this.seen = false; this.willHit = false;
    this.predAt = -Infinity; this.predTti = -1; this.tti = Infinity;
  }
}

export class BotPerception {
  constructor(capacity = PERCEPTION.capacity) {
    /** @type {Entry[]} */
    this.entries = [];
    for (let i = 0; i < capacity; i++) this.entries.push(new Entry());
    this.count = 0;
    this.mostUrgent = new BotThreat();
    /** Noticed balls predicted to hit this frame. */
    this.noticedCount = 0;
    this._incoming = [];
  }

  get hasThreat() { return this.mostUrgent.ball !== null; }

  clear() {
    for (let i = 0; i < this.count; i++) this.entries[i].reset();
    this.count = 0;
    this.mostUrgent.clear();
    this.noticedCount = 0;
  }

  /**
   * Per-frame refresh (scaled clock `now`).
   * @param {object} self player
   * @param {import('./difficulty.js').BotProfile} profile
   * @param {import('../core/rng.js').Rng} rng
   * @param {number} now
   * @param {number} lookahead  seconds of trajectory prediction
   * @param {number} peripheralRadius distance at which an unnoticed rear ball is finally sensed
   * @param {{windupPrime:(thrower:object, now:number)=>number}|null} [primer] seconds of the thrower's wind-up watched
   */
  update(self, profile, rng, now, lookahead, peripheralRadius, primer = null) {
    this.mostUrgent.clear();
    this.noticedCount = 0;
    if (!self || self.zone !== ZONE.INFIELD) { this.clear(); return; }

    for (let i = 0; i < this.count; i++) this.entries[i].seen = false;
    const list = gatherIncoming(self, this._incoming);
    for (let b = 0; b < list.length; b++) {
      const ball = list[b];
      if (!ball || ball.state !== BALL_STATE.LIVE || ball.isPass) continue;
      if (ball.lastThrower && !game.areEnemies(self, ball.lastThrower)) continue;

      let e = this._find(ball, launchTimeOf(ball));
      if (!e) e = this._add(ball, self, profile, rng, now, primer);
      if (!e) continue; // queue full: the least important newcomer is ignored
      e.seen = true;

      if (e.blind) {
        // Unnoticed rear throw: sensed only when it is about to arrive (sound / peripheral vision).
        if (planarDistance(ball.position, self.position) > peripheralRadius) continue;
        e.blind = false;
        e.noticeAt = now + e.reaction * 0.5;
      }
      if (now < e.noticeAt) { e.willHit = false; continue; }

      this._predict(e, ball, self, now, lookahead);
      if (!e.willHit) continue;
      this.noticedCount++;
      if (this.mostUrgent.ball === null || e.tti < this.mostUrgent.timeToImpact) this.mostUrgent.fromEntry(e);
    }
    this._compact();
    list.length = 0; // do not keep balls alive through the scratch array
  }

  /** Specter's Danger Sense: notice this ball much faster (and even if it came from behind). */
  notifyDangerSense(ball, self, profile, rng, now) {
    if (!ball || !self || self.zone !== ZONE.INFIELD) return;
    let e = this._find(ball, launchTimeOf(ball));
    if (!e) e = this._add(ball, self, profile, rng, now);
    if (!e) return;
    const fast = Math.max(PERCEPTION.minReaction, jitter(rng, profile.reactionTime, profile.reactionJitter) * profile.dangerSenseReactionMul);
    e.blind = false;
    e.noticeAt = Math.min(e.noticeAt, now + fast);
  }

  /** Current knowledge of a specific throw (for threat hysteresis). Returns false when unknown / not a hit. */
  getThreat(ball, launchTime, out) {
    if (!ball) return false;
    const e = this._find(ball, launchTime);
    if (!e || e.blind || !e.willHit || !Number.isFinite(e.noticeAt)) return false;
    out.fromEntry(e);
    return true;
  }

  isNoticed(ball, now) {
    const e = ball ? this._find(ball, launchTimeOf(ball)) : null;
    return !!e && !e.blind && now >= e.noticeAt;
  }

  // ------------------------------------------------------------------ internals

  _predict(e, ball, self, now, lookahead) {
    const vel = ball.velocity || ZERO;
    if (now - e.predAt >= PERCEPTION.repredictInterval || e.predVel.distanceToSquared(vel) > PERCEPTION.velocityTolerance ** 2) {
      e.predTti = predictBallImpact(ball, self, lookahead, e.impact);
      e.predAt = now;
      e.predVel.copy(vel);
    }
    if (e.predTti < 0) { e.willHit = false; e.tti = Infinity; return; }
    const tti = e.predTti - (now - e.predAt); // extrapolate between predictions
    e.willHit = tti > -PERCEPTION.passedGrace;
    e.tti = Math.max(0, tti);
  }

  _find(ball, launchTime) {
    for (let i = 0; i < this.count; i++) {
      const e = this.entries[i];
      if (e.ball === ball && Math.abs(e.launchTime - launchTime) < 1e-4) return e;
    }
    return null;
  }

  _add(ball, self, profile, rng, now, primer = null) {
    // A re-launch of a tracked ball (caught and thrown back) replaces its old entry.
    for (let i = 0; i < this.count; i++) if (this.entries[i].ball === ball) { this._removeAt(i); break; }
    if (this.count >= this.entries.length) return null;

    let reaction = Math.max(PERCEPTION.minReaction, jitter(rng, profile.reactionTime, profile.reactionJitter));
    // Where did the throw come from, relative to where the bot is looking?
    const src = ball.lastThrower && ball.lastThrower.position ? ball.lastThrower.position : ball.position;
    const angle = planarAngleDeg(forwardX(self), forwardZ(self), src.x - self.position.x, src.z - self.position.z);
    const fromBehind = angle > profile.fieldOfView;
    let blind = false;
    if (fromBehind) {
      reaction *= profile.rearReactionMul;
      blind = rng.chance(profile.rearBlindChance);
    } else if (primer && ball.lastThrower) {
      // Read the wind-up: the longer the bot watched this thrower charge at it, the sooner it reacts to the release.
      const watched = primer.windupPrime(ball.lastThrower, now);
      const k = Math.min(1, Math.max(0, watched / PERCEPTION.windupFullPrime)) * (profile.anticipation || 0);
      reaction = Math.max(PERCEPTION.minReaction, reaction * (1 - k));
    }
    // Reaction counts from the launch when the bot saw it happen; from now when it only just started watching.
    const lt = launchTimeOf(ball);
    const seenFrom = Number.isFinite(ball.launchTime) && now - lt <= PERCEPTION.seenFromLaunchWindow ? lt : now;

    const e = this.entries[this.count++];
    e.reset();
    e.ball = ball;
    e.launchTime = lt;
    e.reaction = reaction;
    e.blind = blind;
    e.fromBehind = fromBehind;
    e.noticeAt = blind ? Infinity : seenFrom + reaction;
    return e;
  }

  /** Removes entry i, keeping order; the Entry object is recycled at the end of the pool. */
  _removeAt(i) {
    const e = this.entries[i];
    for (let k = i; k < this.count - 1; k++) this.entries[k] = this.entries[k + 1];
    this.entries[this.count - 1] = e;
    e.reset();
    this.count--;
  }

  /** Drops entries whose ball was not reported this frame (landed, caught, despawned). Stable, allocation-free. */
  _compact() {
    let w = 0;
    for (let r = 0; r < this.count; r++) {
      const e = this.entries[r];
      if (!e.seen || !e.ball) continue;
      if (w !== r) { this.entries[r] = this.entries[w]; this.entries[w] = e; }
      w++;
    }
    for (let i = w; i < this.count; i++) this.entries[i].reset();
    this.count = w;
  }
}
