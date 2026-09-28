// ---------------------------------------------------------------------------------------------------------------
// Bot - utility AI that plays Dodgeball Ultra by producing the SAME intent a human produces (contract §3.7):
// `new Bot(difficulty, seed)`, `bot.sample(player, dt) -> intent`. Every button goes through the player's state
// machine: the bot holds throwHeld for its chosen charge time and then sends throwReleased, presses catchPressed at a
// chosen moment before the predicted impact, presses jump/slide/pickup/pass/skill/ultimate for one frame, and steers
// with a planar move vector. It never teleports, never reads hidden state a player could not see (cloaked enemies,
// unnoticed rear throws) and never acts faster than its difficulty's reaction time.
//
//   perception    : incoming enemy balls enter a reaction-delayed observation queue; impacts are predicted with
//                   Trajectory.predictImpact (perception.js). Specter reacts faster through EV.DangerSense.
//   threat layer  : a newly noticed hit pre-empts everything -> catch (timed catchPressed) or dodge (sidestep
//                   perpendicular to the path, jump a low ball, slide under a high one while sprinting) or brace.
//   decision tick : every decisionInterval +/- jitter (or when forced): refresh target / ball / pass receiver, score
//                   Retrieve / Attack / Pass / Position with hysteresis + minimum dwell, evaluate Skill / Ultimate
//                   through buildAbilityContext() + AbilityBase.evaluateAI() against a threshold.
//   execution     : steering (destination, strafe, spacing, holder avoidance, separation, confinement), aim, and the
//                   human-like button sequences.
// Randomness is seeded per bot (derived from game.rng unless a seed is given), so ?seed=N matches are reproducible.
// Time: everything runs on the scaled gameplay clock (game.time.now), so hitstop and pause freeze the bots too.
// Hot path is allocation-free (module-scope temporaries, double-buffered intent objects, pooled perception).
// ---------------------------------------------------------------------------------------------------------------
import * as THREE from 'three';
import { game } from '../game.js';
import { EV } from '../core/events.js';
import { Rng } from '../core/rng.js';
import { ZONE, BALL_RADIUS, STANDARD_HIT_DAMAGE, opponent } from '../core/constants.js';
import { SLOT, AI_HINT } from '../abilities/abilityBase.js';
import { BOT_DIFFICULTY, DIFFICULTY_IDS, createProfile, heroTraits, normalizeDifficulty } from './difficulty.js';
import {
  DEG2RAD, clamp, clamp01, lerp, inverseLerp, moveTowards, jitter, randomSign, perlin1D, planarDistance,
  planarAngleDeg, shrinkBounds, copyBounds, clampPlanar, planarDistanceToBounds, edgeProximity, keepInside, seek,
  timeToCover, interceptPoint, planCatchLead, chooseSidestep, weightedPick,
} from './aiMath.js';
import {
  STATE, BALL_STATE, CENTER_Z, stateIs, hasStatus, holdsBall, isCharging, bodyRadius, bodyHeight, chestOf, forwardX,
  forwardZ, sideSign, movementProfile, combatProfile, perfectWindowOf, isPerceivable, isCommitted, countTargetable,
  roundTimeLeft, anyTeammatePending, confinementOf, scoreTarget, hasLineOfSight, hittablePosition, estimateThrowSpeed,
} from './botWorld.js';
import { BotPerception, BotThreat, predictBallImpact, gatherIncoming } from './perception.js';

export { BOT_DIFFICULTY, DIFFICULTY_IDS };

/** High-level behaviour the bot is executing. THREAT pre-empts everything as soon as an incoming ball is perceived. */
export const BOT_BEHAVIOUR = Object.freeze({
  IDLE: 'idle', THREAT: 'threatResponse', RETRIEVE: 'retrieve', ATTACK: 'attack', PASS: 'pass', POSITION: 'position',
});
/** How the bot answers one perceived incoming ball. */
export const BOT_RESPONSE = Object.freeze({
  NONE: 'none', CATCH: 'catch', SIDESTEP: 'sidestep', JUMP: 'jump', SLIDE: 'slide', BRACE: 'brace',
});
const THROW_NONE = 0, THROW_PRESSING = 1, THROW_HOLDING = 2;

/** Default cloak awareness for buildAbilityContext() when no bot profile supplies one (m). */
const DEFAULT_CLOAK_RADIUS = 2.5;
/** Look-ahead (s) of the omniscient incoming-ball query in buildAbilityContext(). */
const CONTEXT_LOOKAHEAD = 2.5;
/** Gap (s) between two samples after which whatever the bot was doing is considered stale. */
const STALE_SAMPLE_GAP = 0.3;
/** Seconds a watched wind-up is remembered after the charge ends (covers the release frame + ball spawn). */
const WINDUP_MEMORY = 0.35;
/** Deg: an enemy whose body faces the bot within this angle is winding up AT the bot. */
const WINDUP_FACING_ANGLE = 35;
/** A visible enemy winding up at the bot within this distance (m) makes a positioning bot stop and square up. */
const WINDUP_BRACE_RANGE = 16;
/** m/s: a free ball rolling faster than this may be refused by canBePickedUpBy() now but is still worth chasing. */
const FAST_ROLL_SPEED = 3;
/** Seconds an ability whose evaluateAI()/canActivate() threw is left alone. */
const BROKEN_ABILITY_RETRY = 15;

// ------------------------------------------------------------------ module-scope temporaries (no per-frame allocation)
const _move = new THREE.Vector3();
const _look = new THREE.Vector3();
const _spot = new THREE.Vector3();
const _chest = new THREE.Vector3();
const _origin = new THREE.Vector3();
const _aim = new THREE.Vector3();
const _lead = new THREE.Vector3();
const _lock = new THREE.Vector3();
const _dir = new THREE.Vector3();
const _sep = new THREE.Vector3();
const _pushA = new THREE.Vector3();
const _pushB = new THREE.Vector3();
const _v1 = new THREE.Vector3();
const _v2 = new THREE.Vector3();
const _ctxHit = new THREE.Vector3();
const _ZERO = new THREE.Vector3();
const _ctxIncoming = [];

// ------------------------------------------------------------------ bot registry + event hub
/** Bound bots (team coordination: ball claims) - array for allocation-free iteration. */
const _bots = [];
let _hubBus = null;
const _hubOff = [];
let _orderCounter = 0;

function _live(bot) { return !!bot.player && game.players.includes(bot.player); }
/** Drops bots whose player left the game (Match disposes players, not their intent sources). */
function _pruneBots() { for (let i = _bots.length - 1; i >= 0; i--) if (!_live(_bots[i])) _bots.splice(i, 1); }
/** Calls `method` on every bound bot whose player still exists; prunes stale bots (players disposed without dispose()). */
function _broadcast(method, payload) {
  for (let i = _bots.length - 1; i >= 0; i--) {
    const bot = _bots[i];
    if (!_live(bot)) { _bots.splice(i, 1); continue; }
    try { bot[method](payload); } catch (e) { console.error(`[ai] Bot.${method} threw`, e); }
  }
}
const _handlers = [
  [EV.RoundStarted, (e) => _broadcast('_onRoundStarted', e)],
  [EV.RoundEnded, (e) => _broadcast('_onRoundEnded', e)],
  [EV.DangerSense, (e) => _broadcast('_onDangerSense', e)],
  [EV.AbilityFailed, (e) => _broadcast('_onAbilityFailed', e)],
  [EV.BallCaught, (e) => _broadcast('_onBallCaught', e)],
  [EV.PlayerZone, (e) => _broadcast('_onPlayerZone', e)],
  [EV.PlayerEliminated, (e) => _broadcast('_onPlayerEliminated', e)],
];
/** One set of listeners for all bots (re-subscribes if game.events is replaced). */
function _ensureHub() {
  const bus = game.events;
  if (!bus || _hubBus === bus) return;
  _teardownHub();
  _hubBus = bus;
  for (const [name, fn] of _handlers) _hubOff.push(bus.on(name, fn));
}
function _teardownHub() {
  for (const off of _hubOff) { try { off(); } catch (e) { /* bus gone */ } }
  _hubOff.length = 0;
  _hubBus = null;
}

// ------------------------------------------------------------------ intents
/** A fresh intent object (contract shape, see gameplay/player.js). */
function createIntent() {
  return {
    move: new THREE.Vector3(), aimDir: new THREE.Vector3(0, 0, 1), aimPoint: new THREE.Vector3(), target: null,
    sprint: false, jump: false, slide: false, throwPressed: false, throwHeld: false, throwReleased: false,
    catchPressed: false, pass: false, pickup: false, skill: false, ultimate: false, cycleTarget: false,
    /** Additive: lets other systems tell bot intents from human ones. */
    isBot: true,
  };
}
/** Neutral intent facing the player's forward (no buttons, no movement). */
function resetIntent(intent, player) {
  intent.move.set(0, 0, 0);
  intent.target = null;
  intent.sprint = intent.jump = intent.slide = false;
  intent.throwPressed = intent.throwHeld = intent.throwReleased = false;
  intent.catchPressed = intent.pass = intent.pickup = intent.skill = intent.ultimate = intent.cycleTarget = false;
  if (player && player.position) {
    const fx = forwardX(player), fz = forwardZ(player);
    intent.aimDir.set(fx, 0, fz);
    chestOf(player, intent.aimPoint);
    intent.aimPoint.x += fx * 10; intent.aimPoint.z += fz * 10;
  } else {
    intent.aimDir.set(0, 0, 1);
    intent.aimPoint.set(0, 1.3, 10);
  }
  return intent;
}

// ------------------------------------------------------------------ ability context (exported, contract §3.7)
/** Empty ability context object (reuse it with buildAbilityContext(player, ctx)). */
export function createAbilityContext() {
  return {
    self: null, nearestEnemy: null, nearestEnemyDistance: Infinity, incomingBall: null, incomingTime: Infinity,
    teammatesOutfield: 0, enemiesInfield: 0, alliesInfield: 0, holdingBall: false, freeBallsNearby: 0, ultCharge: 0,
    timeLeft: Infinity,
    // Additive extras for hero-specific evaluateAI() overrides.
    incomingSpeedKmh: 0, teammatePending: false, hpFraction: 1, nearestFreeBall: null, nearestFreeBallDistance: Infinity,
    looseBalls: 0, isOutfield: false,
  };
}

/**
 * Situation summary consumed by AbilityBase.evaluateAI(ctx).
 * @param {object} player
 * @param {object} [out] context object to fill (allocation-free reuse); a new one is created when omitted
 * @param {object} [opts] { incomingBall, incomingTime } perceived threat (bots pass what they have NOTICED; when
 *   omitted the query is omniscient), { cloakDetectionRadius } cloaked enemies farther away are ignored,
 *   { freeBallRadius } radius of freeBallsNearby (default 8 m)
 * @returns {{self, nearestEnemy, nearestEnemyDistance, incomingBall, incomingTime, teammatesOutfield, enemiesInfield,
 *   alliesInfield, holdingBall, freeBallsNearby, ultCharge, timeLeft}}
 */
export function buildAbilityContext(player, out = null, opts = null) {
  const ctx = out || createAbilityContext();
  ctx.self = player || null;
  ctx.nearestEnemy = null; ctx.nearestEnemyDistance = Infinity;
  ctx.incomingBall = null; ctx.incomingTime = Infinity; ctx.incomingSpeedKmh = 0;
  ctx.teammatesOutfield = 0; ctx.enemiesInfield = 0; ctx.alliesInfield = 0;
  ctx.holdingBall = false; ctx.freeBallsNearby = 0; ctx.ultCharge = 0; ctx.timeLeft = roundTimeLeft();
  ctx.teammatePending = false; ctx.hpFraction = 1; ctx.nearestFreeBall = null; ctx.nearestFreeBallDistance = Infinity;
  ctx.looseBalls = 0; ctx.isOutfield = false;
  if (!player || !player.position) return ctx;

  const cloakR = opts && Number.isFinite(opts.cloakDetectionRadius) ? opts.cloakDetectionRadius : DEFAULT_CLOAK_RADIUS;
  const freeR = opts && opts.freeBallRadius > 0 ? opts.freeBallRadius : 8;
  const pos = player.position;
  const players = game.players;
  for (let i = 0; i < players.length; i++) {
    const p = players[i];
    if (game.areEnemies(player, p)) {
      if (!p.isTargetable) continue;
      ctx.enemiesInfield++;
      if (!isPerceivable(player, p, cloakR)) continue;
      const d = pos.distanceTo(p.position);
      if (d < ctx.nearestEnemyDistance) { ctx.nearestEnemyDistance = d; ctx.nearestEnemy = p; }
    } else if (p.team === player.team) {
      if (p.isTargetable) ctx.alliesInfield++;
      if (p !== player && p.zone === ZONE.OUTFIELD) ctx.teammatesOutfield++;
    }
  }

  if (opts && opts.incomingBall !== undefined) {
    ctx.incomingBall = opts.incomingBall || null;
    ctx.incomingTime = ctx.incomingBall && Number.isFinite(opts.incomingTime) ? opts.incomingTime : Infinity;
  } else if (player.zone === ZONE.INFIELD) {
    const list = gatherIncoming(player, _ctxIncoming);
    for (let i = 0; i < list.length; i++) {
      const b = list[i];
      if (!b || b.state !== BALL_STATE.LIVE || b.isPass) continue;
      const t = predictBallImpact(b, player, CONTEXT_LOOKAHEAD, _ctxHit);
      if (t >= 0 && t < ctx.incomingTime) { ctx.incomingTime = t; ctx.incomingBall = b; }
    }
    list.length = 0;
  }
  if (ctx.incomingBall) ctx.incomingSpeedKmh = Number.isFinite(ctx.incomingBall.speedKmh) ? ctx.incomingBall.speedKmh : 0;

  ctx.holdingBall = holdsBall(player);
  ctx.ultCharge = player.abilities && Number.isFinite(player.abilities.ultimateCharge) ? player.abilities.ultimateCharge : 0;
  ctx.isOutfield = player.zone === ZONE.OUTFIELD;
  ctx.teammatePending = anyTeammatePending(player);
  const h = player.health;
  if (h && h.maxHp > 0) ctx.hpFraction = clamp01(h.hp / h.maxHp);

  const balls = game.balls && game.balls.matchBalls;
  if (balls) {
    const r2 = freeR * freeR;
    for (let i = 0; i < balls.length; i++) {
      const b = balls[i];
      if (!b || b.state !== BALL_STATE.FREE) continue;
      ctx.looseBalls++;
      const d2 = b.position.distanceToSquared(pos);
      if (d2 <= r2) ctx.freeBallsNearby++;
      if (d2 < ctx.nearestFreeBallDistance * ctx.nearestFreeBallDistance) { ctx.nearestFreeBallDistance = Math.sqrt(d2); ctx.nearestFreeBall = b; }
    }
  }
  return ctx;
}

// ------------------------------------------------------------------ the bot
export class Bot {
  /** Structural tuning shared by every difficulty (metres, seconds). Per-tier skill lives in difficulty.js. */
  static defaults = Object.freeze({
    // movement
    arriveRadius: 0.35,        // destination reached
    slowRadius: 1.4,           // ease off the stick inside this distance
    sprintDistance: 3,         // sprint when the destination is farther
    edgeMargin: 0.9,           // margin from the confinement edges while positioning (never get cornered)
    settleRadius: 0.9,         // once settled, only move again when the spot drifts farther than this
    separationRadius: 1.1,     // short-range push away from nearby bodies
    awarenessRadius: 2.5,      // enemies closer than this are noticed even with Silent Footsteps
    // attacking
    idealRangeMin: 6,          // preferred throwing distance band (spec: approach 6-12 m)
    idealRangeMax: 12,
    maxThrowRange: 17,         // never throws at infield targets farther than this
    outfieldMaxThrowRange: 21, // from the outfield strip (behind the enemies' backs)
    attackLineDepthMin: 0.8,   // closest attack position to the centre line (aggressive)
    attackLineDepthMax: 2.8,   // farthest (cautious)
    maxHoldTime: 6,            // holding without a clear shot: pass or throw at the barrier anyway
    chargeStartTimeout: 0.45,  // abort the throw sequence if charging never started
    minEarlyReleaseCharge: 0.15, // minimum charge before an opportunistic / panic release
    postThrowDelay: 0.45,      // pause after a throw before another charge
    maxLead: 12,               // cap of the intercept lead (m)
    lockKeepRadius: 0.6,       // release aim within this of the target -> keep the lock (aim assist); else free aim
    // threat response
    threatLookahead: 2,        // seconds of trajectory prediction
    peripheralRadius: 2.5,     // unnoticed rear balls are sensed at this distance
    lowBallHeight: 0.7,        // impact height (above the feet) below which a ball is jumped
    jumpPressLead: 0.24,       // ideal seconds before impact to press Jump (feet ~0.9 m up by then)
    slidePressLead: 0.34,      // ideal seconds before impact to press Slide
    slideHeightFrac: 0.55,     // sliding body height / standing height
    sidestepClearance: 0.3,    // lateral clearance beyond body radius + ball radius
    threatSwitchMargin: 0.15,  // another ball replaces the answered one only if it arrives this much sooner
    panicReleaseTime: 0.3,     // while charging, release at once when a ball hits within this time
    squareUpAngle: 30,         // deg: step toward a ball to be caught that is farther off the chest than this
    squareUpStep: 0.2,         // stick magnitude of that step
    // retrieving
    stasisReachHeight: 2.3,    // highest ball (above the feet) that can be snatched from Chrono's stasis
    stasisJumpHeight: 1.9,     // leap for stasis balls higher than this
    openingRushDuration: 4,    // seconds after RoundStarted of sprinting for the centre-line balls
    maxChaseTime: 6,           // give up on a ball chased longer than this (ignored for 3 s)
    pickupRepressInterval: 0.2,
    freeBallAwarenessRadius: 8, // freeBallsNearby radius of the ability context
  });

  /**
   * @param {'easy'|'normal'|'hard'|'pro'|object} [difficulty] tier id, or a partial profile ({ base:'hard', aimErrorDeg: 3 })
   * @param {number} [seed] per-bot seed; derived from game.rng when omitted
   */
  constructor(difficulty = 'normal', seed) {
    this.difficulty = normalizeDifficulty(difficulty && typeof difficulty === 'object' ? difficulty.base : difficulty);
    /** @type {import('./difficulty.js').BotProfile} */
    this.profile = createProfile(difficulty);
    this.tuning = { ...Bot.defaults };
    const s = Number.isFinite(seed) ? Math.floor(seed) : Math.floor(game.rng.next() * 0x7fffffff);
    this.seed = s >>> 0;
    // Decorrelate from game.rng's own sequence (mulberry32 seeds that differ by 1 are fine, this is belt and braces).
    this.rng = new Rng((Math.imul(this.seed ^ 0x9e3779b9, 0x85ebca6b) >>> 0) || 1);
    this._noiseSeedX = this.rng.range(0, 1000);
    this._noiseSeedZ = this.rng.range(0, 1000);

    /** Player this brain drives (bound on the first sample). */
    this.player = null;
    this.traits = heroTraits(null);
    this._order = 0;

    this.perception = new BotPerception();
    /** The threat currently answered. */
    this.threat = new BotThreat();
    this._committedThreat = new BotThreat();
    this.dodgeDir = new THREE.Vector3();
    this.decoyOffset = new THREE.Vector3();
    /** Where the bot is heading (debug overlays). */
    this.destination = new THREE.Vector3();
    this._pickupPoint = new THREE.Vector3();

    this._laneCount = 0;
    this._conf = { minX: -4, maxX: 4, minZ: -8, maxZ: -1 };
    this._inner = { minX: -3, maxX: 3, minZ: -7, maxZ: -2 };
    this._confTeam = null; this._confZone = null; this._hasConf = false;

    this._intents = [createIntent(), createIntent()];
    this._intentIndex = 0;
    this._lastIntent = null;
    this._lastFrame = -1;
    this._lastSampleTime = -Infinity;
    this._lastDt = 1 / 60;
    this._wasLive = false;
    this._lastRound = null;
    this._errorLogged = false;

    this._ctx = createAbilityContext();
    this._calmCtx = createAbilityContext();
    this._ctxOpts = { incomingBall: null, incomingTime: Infinity, cloakDetectionRadius: DEFAULT_CLOAK_RADIUS, freeBallRadius: 8 };
    // Wind-up reading: per watched enemy, seconds of charge observed (fixed slots, no allocation).
    this._windupPlayer = [null, null, null, null, null, null, null, null];
    this._windupTime = new Float64Array(8);
    this._windupSeenAt = new Float64Array(8).fill(-Infinity);
    this._cand = [null, null, null, null, null, null, null, null];
    this._candScore = new Float64Array(8);
    /** Utility scores of the last decision (debugging / HUD). */
    this.scores = { threat: 0, retrieve: 0, attack: 0, pass: 0, position: 0, skill: 0, ultimate: 0 };

    this.reset();
  }

  // ================================================================== public API

  /**
   * Produces this frame's intent (contract §3.7). Idempotent within a rendered frame. The returned object is owned
   * by the bot and double-buffered: it stays valid until the next-but-one sample.
   * @param {object} player
   * @param {number} dt scaled seconds since the last sample
   */
  sample(player, dt = 0) {
    if (!player) return resetIntent(this._intents[0], null);
    if (this.player !== player) this._bind(player);
    _ensureHub();

    const frame = game.time.frame;
    if (frame === this._lastFrame && this._lastIntent) return this._lastIntent;
    this._lastFrame = frame;
    this._intentIndex ^= 1;
    const intent = resetIntent(this._intents[this._intentIndex], player);
    this._lastIntent = intent;

    try {
      this._think(intent, player, dt);
    } catch (e) {
      // A bug in the brain must never take the match loop down: log once, stand still, keep going.
      if (!this._errorLogged) { this._errorLogged = true; console.error('[ai] Bot.sample threw', e); }
      resetIntent(intent, player);
      this._resetTransient(game.time.now);
    }
    return intent;
  }

  /** Switches to another difficulty tier (or custom partial profile) without resetting the brain. */
  setDifficulty(difficulty) {
    this.difficulty = normalizeDifficulty(difficulty && typeof difficulty === 'object' ? difficulty.base : difficulty);
    this.profile = createProfile(difficulty);
  }

  /** Forgets everything (round reset / re-spawn). Keeps player, difficulty and seed. */
  reset() {
    const now = game.time.now;
    this._goReactUntil = -Infinity;
    this._resetTransient(now);
    this._skillRetryAt = 0; this._ultRetryAt = 0;
    this.lastSkillUtility = 0; this.lastUltUtility = 0;
    this.claimedBall = null; this._claimedBallTime = now;
    this._ignoredBall = null; this._ignoredBallUntil = -Infinity;
    this._nextPickupPressAt = -Infinity;
    this.currentTarget = null; this.currentTargetScore = 0; this._targetClear = false;
    this._decoyFor = null; this.decoyOffset.set(0, 0, 0);
    this._passReceiver = null; this._passInclination = false; this._nextPassAllowedAt = -Infinity;
    this._hadBall = false; this._counterAttack = false; this._possessionStart = now;
    this._throwReadyAt = -Infinity; this._lastThrowTime = -Infinity;
    this._lastCatchTime = -Infinity; this._lastCatchPerfect = false;
    this._openingRushUntil = -Infinity;
    this._aggression = this.profile.aggression;
    this.behaviour = BOT_BEHAVIOUR.IDLE;
    for (const k in this.scores) this.scores[k] = 0;
  }

  /** Unbinds from the player and the shared event hub. */
  dispose() {
    const i = _bots.indexOf(this);
    if (i >= 0) _bots.splice(i, 1);
    if (_bots.length === 0) _teardownHub();
    this._resetTransient(game.time.now);
    this.claimedBall = null; this.currentTarget = null; this._passReceiver = null; this._ignoredBall = null;
    this.player = null;
    this._lastIntent = null;
  }

  /** Current answer to the most urgent perceived ball (debug). */
  get response() { return this._response; }
  get isThreatened() { return this._threatActive; }

  // ================================================================== frame

  _think(intent, player, dt) {
    const now = game.time.now;
    if (now - this._lastSampleTime > STALE_SAMPLE_GAP) this._resetTransient(now); // not sampled for a while
    this._lastSampleTime = now;
    if (dt > 0) this._lastDt = dt;

    const match = game.match;
    const live = (!match || match.isPlaying) && !player.inputLocked;
    if (!live) {
      if (this._wasLive) this._resetTransient(now);
      this._wasLive = false;
      this.behaviour = BOT_BEHAVIOUR.IDLE;
      this._faceEnemyHalf(intent);
      return;
    }
    if (!this._wasLive) this._onBecameLive(now);
    this._wasLive = true;

    this._refreshConfinement(player);
    if (player.canAct === false) {
      // Stunned / frozen / ragdoll / channeling: buttons do nothing - drop half-done sequences, keep watching.
      this._abortSequences();
      this._chooseWatchPoint(_look);
      this._setAim(intent, _look, null);
      return;
    }

    this._trackPossession(now);
    this._trackWindups(now, Math.max(0, dt));
    this._updateThreat(now);
    if (this._forceDecision || now >= this._nextDecisionTime) this._decide(now);
    this._execute(intent, now, Math.max(0, dt));
  }

  _bind(player) {
    _pruneBots();
    const i = _bots.indexOf(this);
    if (i < 0) _bots.push(this);
    this.player = player;
    this._order = ++_orderCounter;
    this.traits = heroTraits(player.hero && player.hero.id);
    this._hasConf = false;
    this._lastRound = null;
    this.reset();
  }

  _refreshConfinement(p) {
    if (this._hasConf && p.team === this._confTeam && p.zone === this._confZone) return;
    const b = confinementOf(p.team, p.zone);
    if (!b) return;
    copyBounds(b, this._conf);
    this._confTeam = p.team; this._confZone = p.zone; this._hasConf = true;
  }

  _onBecameLive(now) {
    const round = game.match ? game.match.round : null;
    if (round !== this._lastRound) { this._lastRound = round; this._startRound(now); }
  }

  /** "GO!": sprint for the centre-line balls after a human-like reaction delay. */
  _startRound(now) {
    const P = this.profile;
    this._resetTransient(now);
    this.claimedBall = null;
    this._openingRushUntil = now + this.tuning.openingRushDuration;
    const react = Math.max(0.02, jitter(this.rng, P.reactionTime, P.reactionJitter));
    this._nextDecisionTime = now + react;
    this._goReactUntil = now + react;
    this._forceDecision = false;
    this._lastSampleTime = now;
  }

  // ================================================================== events (via the shared hub)

  _onRoundStarted() {
    const now = game.time.now;
    this._lastRound = game.match ? game.match.round : this._lastRound;
    this._startRound(now);
  }
  _onRoundEnded() {
    this._resetTransient(game.time.now);
    this._openingRushUntil = -Infinity;
  }
  _onDangerSense(e) {
    if (!e || e.player !== this.player || !e.active || !e.ball) return;
    // The event is Specter's passive; any hero receiving it gets the faster notice (traits make Specter dodge better).
    this.perception.notifyDangerSense(e.ball, this.player, this.profile, this.rng, game.time.now);
  }
  _onAbilityFailed(e) {
    if (!e || e.player !== this.player) return;
    const until = game.time.now + this.profile.abilityFailBackoff;
    if (e.slot === SLOT.ULTIMATE) this._ultRetryAt = Math.max(this._ultRetryAt, until);
    else if (e.slot === SLOT.SKILL) this._skillRetryAt = Math.max(this._skillRetryAt, until);
  }
  _onBallCaught(e) {
    if (!e || e.catcher !== this.player) return;
    this._lastCatchTime = game.time.now;
    this._lastCatchPerfect = e.quality === 'perfect';
  }
  _onPlayerZone(e) {
    if (!e) return;
    if (e.player === this.player) this._resetTransient(game.time.now);
    else if (e.player === this.currentTarget || e.player === this._throwTarget) this._forceDecision = true;
  }
  _onPlayerEliminated(e) {
    if (!e) return;
    if (e.player === this.player) this._resetTransient(game.time.now);
    else if (e.player === this.currentTarget || e.player === this._throwTarget || e.player === this._passReceiver) this._forceDecision = true;
  }

  // ================================================================== state resets

  /** Drops everything short-lived: button sequences, threat response, perception, pending presses. */
  _resetTransient(now) {
    this._throwPhase = THROW_NONE; this._throwTarget = null; this._plannedCharge = 0; this._throwPressTime = -Infinity;
    this._opportunityRolled = false;
    this._pendingPass = false; this._pendingSkill = false; this._pendingUltimate = false;
    this._abilityTarget = null; this._abilityBall = null;
    this._clearThreat();
    this.perception.clear();
    this._strafeTarget = 0; this._strafeCurrent = 0; this._strafeSwitchAt = -Infinity;
    this._settled = false;
    this._forceDecision = !(now < this._goReactUntil); // never skip the reaction delay to "GO!"
    if (!Number.isFinite(this._nextDecisionTime)) this._nextDecisionTime = now;
    this._behaviourSince = now;
    this.behaviour = BOT_BEHAVIOUR.IDLE;
  }

  _abortSequences() {
    this._throwPhase = THROW_NONE; this._throwTarget = null;
    this._pendingPass = false; this._pendingSkill = false; this._pendingUltimate = false;
    this._abilityTarget = null; this._abilityBall = null;
    this._clearThreat();
  }

  _clearThreat() {
    this._threatActive = false;
    this.threat.clear();
    this._response = BOT_RESPONSE.NONE;
    this._respondingTo = null; this._respondingLaunch = 0;
    this._threatExpectedAt = -Infinity;
    this._catchPressed = false; this._maneuverPressed = false;
    this._plannedCatchLead = 0; this._maneuverLead = 0; this._responseStartAt = 0; this._dodgeFeasible = false;
    if (this.behaviour === BOT_BEHAVIOUR.THREAT) this.behaviour = BOT_BEHAVIOUR.POSITION;
  }

  // ================================================================== possession

  _trackPossession(now) {
    const P = this.profile;
    const hasBall = holdsBall(this.player);
    if (hasBall && !this._hadBall) {
      this._possessionStart = now;
      const caught = now - this._lastCatchTime < 0.35;
      let hesitation = this.rng.range(P.throwHesitationMin, P.throwHesitationMax);
      if (caught) hesitation *= this._lastCatchPerfect ? 0.35 : 0.6; // strike back while the counter boost is hot
      this._throwReadyAt = Math.max(this._throwReadyAt, now + hesitation);
      this._passInclination = this.rng.chance(clamp01(P.passChance + this.traits.passBias) * (caught ? 0.5 : 1));
      this._counterAttack = caught;
      this.claimedBall = null;
      this._forceDecision = true;
    } else if (!hasBall && this._hadBall) {
      this._counterAttack = false;
      if (this._throwPhase === THROW_PRESSING) this._throwPhase = THROW_NONE;
      this._forceDecision = true;
    }
    this._hadBall = hasBall;
  }

  // ================================================================== decisions

  _decide(now) {
    const P = this.profile, T = this.tuning, self = this.player;
    this._forceDecision = false;
    this._nextDecisionTime = now + Math.max(0.05, jitter(this.rng, P.decisionInterval, P.decisionJitter));
    shrinkBounds(this._conf, self.zone === ZONE.INFIELD ? T.edgeMargin : 0.4, this._inner);
    this._aggression = this._computeAggression();

    const hasBall = holdsBall(self);
    let timeToBall = Infinity;
    if (hasBall) {
      this.claimedBall = null;
      this._selectAttackTarget(now);
    } else {
      this.currentTarget = null;
      this._decoyFor = null;
      timeToBall = this._selectBall(now);
    }

    const s = this.scores;
    s.threat = this._threatActive ? 10 : 0;
    s.retrieve = this._scoreRetrieve(now, hasBall, timeToBall);
    s.attack = this._scoreAttack(now, hasBall);
    s.pass = this._scorePass(now, hasBall);
    s.position = 0.35;

    // Hysteresis: the running behaviour gets a bonus so near-equal options do not flip-flop.
    switch (this.behaviour) {
      case BOT_BEHAVIOUR.RETRIEVE: if (s.retrieve > 0) s.retrieve += P.hysteresis; break;
      case BOT_BEHAVIOUR.ATTACK: if (s.attack > 0) s.attack += P.hysteresis; break;
      case BOT_BEHAVIOUR.POSITION: s.position += P.hysteresis; break;
      default: break;
    }

    let next = BOT_BEHAVIOUR.POSITION, best = s.position;
    if (s.retrieve > best) { best = s.retrieve; next = BOT_BEHAVIOUR.RETRIEVE; }
    if (s.attack > best) { best = s.attack; next = BOT_BEHAVIOUR.ATTACK; }
    if (s.pass > best) { best = s.pass; next = BOT_BEHAVIOUR.PASS; }
    if (s.threat > best) next = BOT_BEHAVIOUR.THREAT;

    if (next !== this.behaviour) {
      const dwellOver = now - this._behaviourSince >= P.minBehaviourDwell;
      if (next === BOT_BEHAVIOUR.THREAT || dwellOver || !this._isBehaviourValid(this.behaviour, hasBall)) this._switchBehaviour(next, now);
    }

    if (this._throwPhase === THROW_NONE && !this._pendingSkill && !this._pendingUltimate) this._evaluateAbilities(now, false);
    s.skill = this.lastSkillUtility;
    s.ultimate = this.lastUltUtility;
  }

  _isBehaviourValid(b, hasBall) {
    switch (b) {
      case BOT_BEHAVIOUR.RETRIEVE: return !hasBall;
      case BOT_BEHAVIOUR.ATTACK: case BOT_BEHAVIOUR.PASS: return hasBall;
      case BOT_BEHAVIOUR.IDLE: case BOT_BEHAVIOUR.THREAT: return false;
      default: return true;
    }
  }

  _switchBehaviour(next, now) {
    if (next === this.behaviour) return;
    this.behaviour = next;
    this._behaviourSince = now;
    this._settled = false;
    if (next === BOT_BEHAVIOUR.PASS) this._pendingPass = true;
    if (next === BOT_BEHAVIOUR.ATTACK) { this._strafeTarget = 0; this._strafeCurrent = 0; this._strafeSwitchAt = -Infinity; }
  }

  _computeAggression() {
    const self = this.player;
    let a = this.profile.aggression + this.traits.aggressionBias;
    const allies = countTargetable(self.team), enemies = countTargetable(opponent(self.team));
    if (allies < enemies) a += 0.1;       // behind: take more risks
    else if (allies > enemies) a -= 0.05; // ahead: protect the lead
    if (roundTimeLeft() < 20 && allies <= enemies) a += 0.15;
    return clamp01(a);
  }

  _scoreRetrieve(now, hasBall, timeToBall) {
    const ball = this.claimedBall;
    if (hasBall || !ball) return 0;
    let s = 0.55 + 0.35 * (1 - clamp01(timeToBall / 3));
    if (now < this._openingRushUntil) s += 1;               // opening rush to the centre line
    if (ball.state === BALL_STATE.STASIS) s += 0.25;        // snatch Chrono's frozen balls
    // A run right under an enemy ball holder's nose is risky for cautious bots.
    const players = game.players;
    for (let i = 0; i < players.length; i++) {
      const e = players[i];
      if (!game.areEnemies(this.player, e) || !holdsBall(e)) continue;
      if (planarDistance(e.position, ball.position) < 6) { s -= 0.25 * (1 - this._aggression); break; }
    }
    return s;
  }

  _scoreAttack(now, hasBall) {
    if (!hasBall) return 0;
    if (!this.currentTarget) return 0.3; // holding with no shot: repositioning (0.35) wins until a target shows
    let s = 0.75 + 0.3 * clamp01(this.currentTargetScore);
    if (this._counterAttack) s += 0.3;
    if (now - this._possessionStart > this.tuning.maxHoldTime * 0.5) s += 0.2;
    return s;
  }

  _scorePass(now, hasBall) {
    this._passReceiver = null;
    if (!hasBall || this._throwPhase !== THROW_NONE) return 0;
    const self = this.player;
    // Combat.tryPass() sends the ball to the nearest teammate who can receive: evaluate exactly that player.
    const receiver = game.nearestTeammate(self, self.position);
    if (!receiver || holdsBall(receiver)) return 0;
    this._passReceiver = receiver;
    if (now < this._nextPassAllowedAt) return 0;

    // Held too long without a clear shot (everyone cloaked / behind Aegis Barrier): give it to someone who has one.
    const stuck = now - this._possessionStart > this.tuning.maxHoldTime && (!this.currentTarget || !this._targetClear);
    if (stuck) return 1.6;
    if (!this._passInclination || this._counterAttack) return 0;

    const mine = this.currentTarget ? this.currentTargetScore : 0;
    let theirs = this._evaluateOpportunity(receiver);
    if (receiver.zone === ZONE.OUTFIELD) theirs += 0.1; // outfield teammates throw at the enemies' backs
    const margin = theirs - mine;
    if (margin < 0.25) return 0;
    return 0.9 + margin + this.traits.passBias * 0.5;
  }

  /** Best target score `thrower` would have (as far as this bot can see). */
  _evaluateOpportunity(thrower) {
    const T = this.tuning;
    chestOf(thrower, _v2);
    const maxRange = thrower.zone === ZONE.OUTFIELD ? T.outfieldMaxThrowRange : T.maxThrowRange;
    let best = 0;
    const players = game.players;
    for (let i = 0; i < players.length; i++) {
      const e = players[i];
      if (!game.areEnemies(thrower, e) || !e.isTargetable) continue;
      if (!isPerceivable(this.player, e, this.profile.cloakDetectionRadius)) continue;
      const s = scoreTarget(_v2, e, maxRange);
      if (s > best) best = s;
    }
    return best;
  }

  // ------------------------------------------------------------------ target selection

  _selectAttackTarget(now) {
    const self = this.player, P = this.profile, T = this.tuning;
    const requireLos = now - this._possessionStart < T.maxHoldTime; // after that, throw at the barrier anyway
    chestOf(self, _origin);
    const maxRange = self.zone === ZONE.OUTFIELD ? T.outfieldMaxThrowRange : T.maxThrowRange;
    const cand = this._cand, score = this._candScore;
    let n = 0;
    const players = game.players;
    for (let i = 0; i < players.length && n < cand.length; i++) {
      const e = players[i];
      if (!game.areEnemies(self, e) || !e.isTargetable) continue;
      if (!isPerceivable(self, e, P.cloakDetectionRadius)) continue;
      let s = scoreTarget(_origin, e, maxRange);
      if (s <= 0) continue;
      if (requireLos && !hasLineOfSight(self, _origin, chestOf(e, _v1), BALL_RADIUS)) continue;
      if (e === this.currentTarget) s += P.hysteresis;
      cand[n] = e; score[n] = s; n++;
    }

    let target = null, targetScore = 0;
    if (n > 0) {
      let best = 0;
      for (let i = 1; i < n; i++) if (score[i] > score[best]) best = i;
      let pick = best;
      // Score-weighted random pick for less skilled bots: sensible most of the time, not always optimal.
      if (n > 1 && !this.rng.chance(P.targetSelectionSkill)) pick = weightedPick(score, n, this.rng.next());
      target = cand[pick]; targetScore = score[pick];
    }
    for (let i = 0; i < cand.length; i++) cand[i] = null; // do not keep references alive

    this.currentTarget = target;
    this.currentTargetScore = targetScore;
    this._targetClear = !!target && (requireLos || hasLineOfSight(self, _origin, chestOf(target, _v1), BALL_RADIUS));

    // Mirage Formation / Night Parade: the real body hides among clones. Decide once per target whether the bot is
    // fooled; if so it aims at a clone (or a plausible clone position) beside it.
    if (target && hasStatus(target, 'obscured')) {
      if (this._decoyFor !== target) {
        this._decoyFor = target;
        if (this.rng.chance(P.obscuredMistargetChance)) this._chooseDecoyOffset(target, this.decoyOffset);
        else this.decoyOffset.set(0, 0, 0);
      }
    } else {
      this._decoyFor = null;
      this.decoyOffset.set(0, 0, 0);
    }
  }

  /** Offset from `target` to the decoy the bot believes is real: a nearby clone hittable, else a random side step. */
  _chooseDecoyOffset(target, out) {
    const tp = target.position;
    let found = 0;
    const set = game.hittables;
    if (set && set.size) {
      for (const h of set) {
        if (!h || h.team !== target.team) continue;
        const hp = hittablePosition(h);
        if (!hp) continue;
        const dx = hp.x - tp.x, dz = hp.z - tp.z, d2 = dx * dx + dz * dz;
        if (d2 < 0.25 || d2 > 3.5 * 3.5) continue;
        found++;
        if (this.rng.chance(1 / found)) out.set(dx, 0, dz); // reservoir pick among the clones
      }
    }
    if (found > 0) return out;
    const self = this.player;
    let fx = tp.x - self.position.x, fz = tp.z - self.position.z;
    const l = Math.hypot(fx, fz) || 1; fx /= l; fz /= l;
    const side = randomSign(this.rng) * this.rng.range(1.2, 2.4), along = this.rng.range(-0.8, 0.8);
    return out.set(fz * side + fx * along, 0, -fx * side + fz * along);
  }

  // ------------------------------------------------------------------ ball selection

  /** Picks the free / stasis ball to run for (sets claimedBall) and returns the time to reach it (s). */
  _selectBall(now) {
    const self = this.player, T = this.tuning, conf = this._conf;
    const prev = this.claimedBall;
    const balls = game.balls && game.balls.matchBalls;
    let best = null, bestScore = Infinity, bestTime = Infinity;
    if (balls && balls.length) {
      const reach = (combatProfile(self).manualPickupRadius || 1.6) * 0.85;
      const runSpeed = Math.max(0.5, (movementProfile(self).sprintSpeed || 7.4) * 0.85);
      const ignored = now < this._ignoredBallUntil ? this._ignoredBall : null;
      for (let i = 0; i < balls.length; i++) {
        const ball = balls[i];
        if (!ball || ball.isAbilityBall || ball === ignored) continue;
        const st = ball.state;
        if (st !== BALL_STATE.FREE && st !== BALL_STATE.STASIS) continue;
        if (!this._isRetrievable(ball, st)) continue;
        const pos = ball.position;
        if (st === BALL_STATE.STASIS && pos.y - self.position.y > T.stasisReachHeight) continue;

        // Where a rolling ball will be when we get there (capped look-ahead, floor friction ignored).
        _v1.copy(pos);
        if (st === BALL_STATE.FREE && ball.velocity) {
          const k = Math.min(planarDistance(self.position, pos) / runSpeed, 1);
          _v1.x += ball.velocity.x * k; _v1.z += ball.velocity.z * k;
        }
        if (planarDistanceToBounds(_v1, conf) > reach) {
          if (planarDistanceToBounds(pos, conf) > reach) continue; // outside our half entirely
          _v1.copy(pos);
        }
        clampPlanar(_v1, conf, _v2);
        const dist = planarDistance(self.position, _v2);
        let t = dist / runSpeed;
        if (this._isLeftToTeammate(ball, pos, dist)) continue;

        // Centre-line contest: an enemy who gets there first makes the run pointless (and dangerous).
        const enemyTime = this._nearestEnemyTime(pos, reach, runSpeed);
        if (enemyTime < t) t += 0.5 + (t - enemyTime) * 0.75;
        if (st === BALL_STATE.STASIS) t -= 0.6; // frozen enemy ball: snatch it before it resumes
        if (ball === prev) t -= this.profile.hysteresis * 2;
        if (t < bestScore) { bestScore = t; best = ball; bestTime = dist / runSpeed; this._pickupPoint.copy(_v2); }
      }
    }
    this.claimedBall = best;
    if (best !== prev) this._claimedBallTime = now;
    return bestTime;
  }

  /**
   * Worth running for: a free ball the combat would let us pick up (or one only rejected because it still rolls
   * fast - it will have slowed by the time we arrive), or a ball hovering in Chrono's stasis (Ball.canBePickedUpBy
   * only accepts FREE balls; the snatch itself is up to Combat.tryPickup).
   */
  _isRetrievable(ball, st) {
    const self = this.player;
    if (holdsBall(self)) return false;
    if (st === BALL_STATE.STASIS) return true;
    if (typeof ball.canBePickedUpBy !== 'function' || ball.canBePickedUpBy(self)) return true;
    const v = ball.velocity;
    return !!v && v.x * v.x + v.z * v.z > FAST_ROLL_SPEED * FAST_ROLL_SPEED;
  }

  /** A teammate is a better candidate for this ball (bot claims resolved by distance, ties by bind order). */
  _isLeftToTeammate(ball, ballPos, myDist) {
    const self = this.player;
    for (let i = 0; i < _bots.length; i++) {
      const other = _bots[i];
      if (other === this || other.claimedBall !== ball || !other.player) continue;
      const mate = other.player;
      if (!game.areTeammates(self, mate) || holdsBall(mate) || !_live(other)) continue;
      const md = planarDistance(mate.position, ballPos);
      if (md < myDist - 0.25 || (Math.abs(md - myDist) <= 0.25 && other._order < this._order)) return true;
    }
    // Human teammates cannot announce claims: assume they take a ball they are much closer to.
    const players = game.players;
    for (let i = 0; i < players.length; i++) {
      const mate = players[i];
      if (!mate.isHuman || !game.areTeammates(self, mate) || holdsBall(mate)) continue;
      const d = planarDistance(mate.position, ballPos);
      if (d < 3 && d < myDist * 0.6) return true;
    }
    return false;
  }

  _nearestEnemyTime(ballPos, reach, runSpeed) {
    let best = Infinity;
    const self = this.player, players = game.players;
    for (let i = 0; i < players.length; i++) {
      const e = players[i];
      if (!game.areEnemies(self, e) || holdsBall(e) || e.canAct === false) continue;
      if (e.health && e.health.isAlive === false) continue;
      const b = confinementOf(e.team, e.zone);
      if (!b || planarDistanceToBounds(ballPos, b) > reach) continue;
      clampPlanar(ballPos, b, _v1);
      const t = planarDistance(e.position, _v1) / runSpeed;
      if (t < best) best = t;
    }
    return best;
  }

  // ------------------------------------------------------------------ abilities

  _evaluateAbilities(now, threatTriggered) {
    const self = this.player, P = this.profile;
    const ab = self.abilities;
    if (!ab) { this.lastSkillUtility = this.lastUltUtility = 0; return; }
    const opts = this._ctxOpts;
    opts.incomingBall = this._threatActive ? this.threat.ball : null; // only what the bot has NOTICED
    opts.incomingTime = this._threatActive ? this.threat.timeToImpact : Infinity;
    opts.cloakDetectionRadius = P.cloakDetectionRadius;
    opts.freeBallRadius = this.tuning.freeBallAwarenessRadius;
    const ctx = buildAbilityContext(self, this._ctx, opts);

    let skill = this._evalAbility(ab.skill, ctx, now, true);
    let ult = this._evalAbility(ab.ultimate, ctx, now, false);
    if (threatTriggered) {
      // Only abilities that the threat makes (more) useful are considered on a threat trigger.
      const calm = Object.assign(this._calmCtx, ctx);
      calm.incomingBall = null; calm.incomingTime = Infinity; calm.incomingSpeedKmh = 0;
      if (skill > 0 && skill <= this._evalAbility(ab.skill, calm, now, true) + 0.05) skill = 0;
      if (ult > 0 && ult <= this._evalAbility(ab.ultimate, calm, now, false) + 0.05) ult = 0;
      calm.self = null; calm.nearestEnemy = null; calm.nearestFreeBall = null;
    }
    this.lastSkillUtility = skill;
    this.lastUltUtility = ult;

    const threshold = P.abilityUtilityThreshold;
    const useUlt = ult >= threshold && ult >= skill;
    const best = useUlt ? ult : skill;
    if (best < threshold) return;
    // The further above the threshold, the more certain the press (marginal cases are sometimes skipped).
    const pressChance = lerp(0.35, 1, inverseLerp(threshold, Math.max(threshold + 0.01, 1), best));
    if (!this.rng.chance(pressChance)) return;

    if (useUlt) { this._pendingUltimate = true; this._ultRetryAt = Math.max(this._ultRetryAt, now + P.abilityRetryInterval); }
    else { this._pendingSkill = true; this._skillRetryAt = Math.max(this._skillRetryAt, now + P.abilityRetryInterval); }
    this._abilityTarget = this.currentTarget || ctx.nearestEnemy;
    this._abilityBall = this._threatActive ? this.threat.ball : null;
  }

  /** Utility (0..1 x usage factor) of pressing `a` now; 0 when it cannot be activated. */
  _evalAbility(a, ctx, now, isSkill) {
    if (!a || a.isPassive || !a.isReady) return 0;
    if (now < (isSkill ? this._skillRetryAt : this._ultRetryAt)) return 0;
    let u = 0;
    try {
      if (a.canActivate()) return 0; // cooldown, silence, ult meter, requires ball, outfield... (no event published)
      u = a.evaluateAI(ctx);
    } catch (e) {
      // A broken ability must not take the bot down, nor spam the log every tick.
      console.warn(`[ai] ${a.id || 'ability'} evaluateAI/canActivate threw - ignored for ${BROKEN_ABILITY_RETRY} s`, e);
      if (isSkill) this._skillRetryAt = now + BROKEN_ABILITY_RETRY; else this._ultRetryAt = now + BROKEN_ABILITY_RETRY;
      return 0;
    }
    if (!Number.isFinite(u) || u <= 0) return 0;
    return clamp01(u) * this.profile.abilityUsageFactor;
  }

  // ================================================================== wind-up reading

  /**
   * Accumulates how long each enemy has visibly been winding up a throw at this bot (charging, inside the field of
   * view, body or lock aimed at us). Perception uses it to react sooner to that thrower's release.
   */
  _trackWindups(now, dt) {
    const self = this.player, P = this.profile;
    const fx = forwardX(self), fz = forwardZ(self);
    const players = game.players;
    for (let i = 0; i < players.length; i++) {
      const e = players[i];
      if (!game.areEnemies(self, e)) continue;
      const slot = this._windupSlot(e, now);
      if (slot < 0) continue;
      const charging = isCharging(e) || stateIs(e, STATE.CHARGING);
      let watching = false;
      if (charging && self.zone === ZONE.INFIELD && isPerceivable(self, e, P.cloakDetectionRadius)) {
        const dx = e.position.x - self.position.x, dz = e.position.z - self.position.z;
        const inView = planarAngleDeg(fx, fz, dx, dz) <= P.fieldOfView;
        const atMe = (e.intent && e.intent.target === self) ||
          planarAngleDeg(forwardX(e), forwardZ(e), -dx, -dz) <= WINDUP_FACING_ANGLE;
        watching = inView && atMe;
      }
      if (watching) { this._windupTime[slot] += dt; this._windupSeenAt[slot] = now; }
      else if (now - this._windupSeenAt[slot] > WINDUP_MEMORY) this._windupTime[slot] = 0;
    }
  }

  /** Nearest perceivable enemy currently winding up a throw at this bot (within WINDUP_BRACE_RANGE), else null. */
  _windupAtMe() {
    const self = this.player, P = this.profile;
    let best = null, bestD = WINDUP_BRACE_RANGE;
    const players = game.players;
    for (let i = 0; i < players.length; i++) {
      const e = players[i];
      if (!game.areEnemies(self, e) || !(isCharging(e) || stateIs(e, STATE.CHARGING))) continue;
      if (!isPerceivable(self, e, P.cloakDetectionRadius)) continue;
      const dx = e.position.x - self.position.x, dz = e.position.z - self.position.z;
      const d = Math.hypot(dx, dz);
      if (d >= bestD) continue;
      const atMe = (e.intent && e.intent.target === self) || planarAngleDeg(forwardX(e), forwardZ(e), -dx, -dz) <= WINDUP_FACING_ANGLE;
      if (atMe) { bestD = d; best = e; }
    }
    return best;
  }

  /** Slot of enemy `e` in the wind-up table (claims a free / stale slot). -1 when full. */
  _windupSlot(e, now) {
    const list = this._windupPlayer;
    let free = -1;
    for (let i = 0; i < list.length; i++) {
      if (list[i] === e) return i;
      if (free < 0 && (list[i] === null || now - this._windupSeenAt[i] > 5)) free = i;
    }
    if (free >= 0) { list[free] = e; this._windupTime[free] = 0; this._windupSeenAt[free] = -Infinity; }
    return free;
  }

  /** Seconds of `thrower`'s wind-up this bot watched (0 when not seen recently). Called by BotPerception. */
  windupPrime(thrower, now) {
    const list = this._windupPlayer;
    for (let i = 0; i < list.length; i++) {
      if (list[i] === thrower) return now - this._windupSeenAt[i] <= WINDUP_MEMORY ? this._windupTime[i] : 0;
    }
    return 0;
  }

  // ================================================================== threat layer

  _updateThreat(now) {
    const self = this.player, T = this.tuning;
    if (self.zone !== ZONE.INFIELD) {
      if (this._threatActive) this._clearThreat();
      this.perception.clear();
      return;
    }
    const perception = this.perception;
    perception.update(self, this.profile, this.rng, now, T.threatLookahead, T.peripheralRadius, this);

    if (!perception.hasThreat) {
      if (!this._threatActive) return;
      // Keep a committed dodge going until the ball has actually gone past (prevents stepping back into it).
      const r = this._response;
      const dodging = r === BOT_RESPONSE.SIDESTEP || r === BOT_RESPONSE.SLIDE || r === BOT_RESPONSE.JUMP;
      if (dodging && this._respondingTo && this._respondingTo.state === BALL_STATE.LIVE && now < this._threatExpectedAt + 0.12) return;
      this._clearThreat();
      this._forceDecision = true;
      return;
    }

    let t = perception.mostUrgent;
    // Threat hysteresis: keep answering the committed ball unless another one is clearly more urgent.
    if (this._threatActive && t.ball !== this._respondingTo &&
      perception.getThreat(this._respondingTo, this._respondingLaunch, this._committedThreat) &&
      this._committedThreat.timeToImpact <= t.timeToImpact + T.threatSwitchMargin) t = this._committedThreat;
    const isNew = !this._threatActive || t.ball !== this._respondingTo || Math.abs(t.launchTime - this._respondingLaunch) > 1e-4;
    this.threat.copyFrom(t);
    this._threatExpectedAt = now + t.timeToImpact;

    if (isNew) {
      this._threatActive = true;
      this._respondingTo = this.threat.ball;
      this._respondingLaunch = this.threat.launchTime;
      this._decideThreatResponse(now);
      this._switchBehaviour(BOT_BEHAVIOUR.THREAT, now);
      this._forceDecision = true;
      // Abilities that answer threats (Precognition Dodge, Magnetic Pull, Stasis Field...) get a say right now.
      if (this._throwPhase === THROW_NONE && !this._pendingSkill && !this._pendingUltimate) this._evaluateAbilities(now, true);
    } else if (this._response === BOT_RESPONSE.CATCH && !this._catchPressed && !this._canAttemptCatch()) {
      // Situation changed (picked up a ball, got blocked from catching...): fall back to a dodge.
      const dodge = this._chooseDodge(now);
      this._response = dodge !== BOT_RESPONSE.NONE ? dodge : BOT_RESPONSE.BRACE;
    }
  }

  _decideThreatResponse(now) {
    const self = this.player;
    this._catchPressed = false;
    this._maneuverPressed = false;
    this._responseStartAt = now;
    if (hasStatus(self, 'invulnerable')) { this._response = BOT_RESPONSE.BRACE; return; } // e.g. Precognition Dodge

    const canCatch = this._canAttemptCatch();
    const willingness = this._catchWillingness();
    const dodge = this._chooseDodge(now);
    const feasible = this._dodgeFeasible;
    const rng = this.rng;
    if (canCatch && (rng.chance(willingness) || (!feasible && rng.chance(0.5 + 0.5 * willingness)))) {
      this._response = BOT_RESPONSE.CATCH;
      this._plannedCatchLead = this._planCatchLead();
      this._responseStartAt = now;
    } else if (dodge !== BOT_RESPONSE.NONE) {
      this._response = dodge;
    } else if (canCatch) {
      this._response = BOT_RESPONSE.CATCH; // desperate catch: nothing else can work
      this._plannedCatchLead = this._planCatchLead();
      this._responseStartAt = now;
    } else {
      this._response = BOT_RESPONSE.BRACE;
    }
  }

  /** Hands free, catching allowed, ball catchable and the bot can square up to it in time. */
  _canAttemptCatch() {
    const self = this.player, combat = self.combat, ball = this.threat.ball;
    if (!combat || !ball || combat.hasBall || combat.catchingBlocked) return false;
    if (hasStatus(self, 'frozen')) return false;
    if (ball.unblockable || ball.pierce || ball.style === 'beam') return false; // Hyperbeam cannot be caught
    if (stateIs(self, STATE.SLIDING)) return false;
    const cone = combatProfile(self).catchConeAngle || 75;
    const angle = planarAngleDeg(forwardX(self), forwardZ(self), ball.position.x - self.position.x, ball.position.z - self.position.z);
    if (angle > cone * 0.8) {
      const turnRate = Math.max(90, movementProfile(self).turnSpeed || 720);
      const turnTime = (angle - cone * 0.6) / turnRate;
      if (turnTime > this.threat.timeToImpact - this.profile.idealCatchLead) return false;
    }
    return true;
  }

  /** Probability of choosing to catch rather than dodge. */
  _catchWillingness() {
    const self = this.player, t = this.threat;
    let p = this.profile.catchAttemptProbability + this.traits.catchBias;
    p *= lerp(1, 0.6, inverseLerp(70, 200, t.speedKmh)); // fastballs are scarier to catch
    if (t.fromBehind) p *= 0.35;                          // no time to square up
    if (anyTeammatePending(self)) p += 0.35;              // Chrono's Delayed Impact: a catch saves them
    if (this._countOutfield(self.team) > 0) p += 0.05;    // a perfect catch revives a teammate
    if (hasStatus(self, 'slippery') || hasStatus(self, 'dodgeDisabled') || hasStatus(self, 'rooted')) p += 0.25; // dodging impaired
    if (self.health && self.health.hp > STANDARD_HIT_DAMAGE + 0.5) p += 0.1; // Thick Hide can afford a miss
    return clamp01(p);
  }

  _countOutfield(team) {
    let n = 0;
    const players = game.players;
    for (let i = 0; i < players.length; i++) if (players[i].team === team && players[i].zone === ZONE.OUTFIELD) n++;
    return n;
  }

  /**
   * Seconds before impact at which Catch will be pressed: ~0.08 s (scaled by Iron Mitts' wider window) plus the
   * difficulty's Gaussian timing noise. Negative = the ball arrives first (late reaction); too early = whiff.
   */
  _planCatchLead() {
    const self = this.player;
    return planCatchLead(this.rng, this.profile.idealCatchLead, this.profile.catchTimingSigma, perfectWindowOf(self),
      combatProfile(self).catchWindow || 0.4);
  }

  /**
   * Evasive manoeuvre: jump over low balls, slide under chest/head-high balls while sprinting, otherwise a sidestep
   * perpendicular to the ball path toward the side needing the least movement that has room. Weaker bots sometimes
   * pick the wrong side or hesitate. Sets this.dodgeDir, _maneuverLead, _responseStartAt and _dodgeFeasible.
   */
  _chooseDodge(now) {
    this._dodgeFeasible = false;
    const self = this.player, T = this.tuning, P = this.profile, t = this.threat, ball = t.ball, rng = this.rng;
    if (!ball || hasStatus(self, 'rooted') || hasStatus(self, 'frozen')) return BOT_RESPONSE.NONE;

    const dodgeDisabled = hasStatus(self, 'dodgeDisabled');
    const skill = clamp01(P.dodgeSkill + this.traits.dodgeBias);
    const impactHeight = t.impactPoint.y - self.position.y;
    const motor = self.motor;

    const clearance = bodyRadius(self) + (ball.radius || BALL_RADIUS) + T.sidestepClearance;
    const need = chooseSidestep(ball.position, ball.velocity || _ZERO, self.position, forwardX(self), forwardZ(self), clearance, this._conf, this.dodgeDir);
    const cover = this._timeToCover(need, this.dodgeDir);

    // Jump over a low ball.
    const canJump = !motor || motor.canJump !== false;
    if (!dodgeDisabled && impactHeight < T.lowBallHeight && canJump && !stateIs(self, STATE.AIRBORNE) &&
      t.timeToImpact > 0.1 && rng.chance(0.35 + 0.65 * skill)) {
      this._maneuverLead = Math.max(0.05, rng.gaussian(T.jumpPressLead, P.maneuverTimingSigma));
      this._responseStartAt = now;
      this._dodgeFeasible = true;
      return BOT_RESPONSE.JUMP;
    }

    // Slide under a chest/head-high ball when already sprinting (momentum carries the slide).
    const slideTop = bodyHeight(self) * T.slideHeightFrac;
    const canSlide = !!motor && motor.canSlide !== false;
    if (!dodgeDisabled && canSlide && stateIs(self, STATE.SPRINTING) && impactHeight > slideTop + 0.15 &&
      t.timeToImpact > 0.12 && rng.chance(skill)) {
      const v = self.velocity;
      const sp = v ? Math.hypot(v.x, v.z) : 0;
      if (sp > 1) this.dodgeDir.set(v.x / sp, 0, v.z / sp);
      this._maneuverLead = Math.max(0.05, rng.gaussian(T.slidePressLead, P.maneuverTimingSigma));
      this._responseStartAt = now;
      this._dodgeFeasible = true;
      return BOT_RESPONSE.SLIDE;
    }

    // Sidestep. Mistakes: wrong side, or a moment of hesitation.
    if (!rng.chance(0.5 + 0.5 * skill)) this.dodgeDir.multiplyScalar(-1);
    const hesitation = rng.chance(skill) ? 0 : (1 - skill) * 0.25 * rng.next();
    this._responseStartAt = now + hesitation;
    this._dodgeFeasible = cover + hesitation <= t.timeToImpact;
    return BOT_RESPONSE.SIDESTEP;
  }

  /** Seconds to move `distance` along the unit planar `dir` from the current velocity (traction / slows aware). */
  _timeToCover(distance, dir) {
    const self = this.player, mp = movementProfile(self), motor = self.motor;
    const traction = motor && Number.isFinite(motor.traction) ? motor.traction : 1;
    const speedMul = motor && Number.isFinite(motor.speedMul) ? motor.speedMul : 1;
    const accel = (mp.acceleration || 34) * Math.max(0.15, traction);
    let vmax = (mp.sprintSpeed || 7.4) * Math.max(0.1, speedMul);
    if (isCharging(self)) vmax *= mp.chargingSpeedMul || 0.6;
    const v = self.velocity;
    const v0 = v ? v.x * dir.x + v.z * dir.z : 0;
    return timeToCover(distance, v0, accel, vmax);
  }

  // ================================================================== execution

  _execute(intent, now, dt) {
    const self = this.player;
    _move.set(0, 0, 0);
    chestOf(self, _look);
    _look.x += forwardX(self) * 10; _look.z += forwardZ(self) * 10;
    let sprint = false;

    if (this._threatActive) sprint = this._execThreat(intent, now);
    else {
      switch (this.behaviour) {
        case BOT_BEHAVIOUR.RETRIEVE: sprint = this._execRetrieve(intent, now); break;
        case BOT_BEHAVIOUR.ATTACK: sprint = this._execAttack(intent, now, dt); break;
        case BOT_BEHAVIOUR.PASS: sprint = this._execPass(intent, now); break;
        case BOT_BEHAVIOUR.IDLE:
          // Before the first decision (reacting to "GO!"): stand and watch.
          this.destination.copy(self.position);
          this._chooseWatchPoint(_look);
          break;
        default: sprint = this._execPosition(intent, now); break;
      }
    }

    this._setAim(intent, _look, null);
    // The throw button sequence continues whatever the behaviour (a bot may keep charging while side-stepping).
    this._updateThrowSequence(intent, now);
    this._applyPendingAbility(intent);

    // Final stick: body separation, confinement guard, 0..1 magnitude.
    this._separation(_sep);
    _move.x += _sep.x * 0.6; _move.z += _sep.z * 0.6; _move.y = 0;
    const l2 = _move.lengthSq();
    if (l2 > 1) _move.multiplyScalar(1 / Math.sqrt(l2));
    keepInside(self.position, _move, this._conf, 0.1);
    intent.move.copy(_move);
    intent.sprint = sprint && _move.lengthSq() > 0.25;
  }

  _execThreat(intent, now) {
    const self = this.player, T = this.tuning, t = this.threat, ball = t.ball;
    let sprint = false;
    if (ball) _look.copy(ball.position);
    const perceiving = this.perception.hasThreat;

    switch (this._response) {
      case BOT_RESPONSE.CATCH: {
        // Square up and stand in (a short step toward the ball if it is off the chest); press Catch at the planned lead.
        _move.set(0, 0, 0);
        if (ball) {
          const dx = ball.position.x - self.position.x, dz = ball.position.z - self.position.z;
          if (planarAngleDeg(forwardX(self), forwardZ(self), dx, dz) > T.squareUpAngle) {
            const l = Math.hypot(dx, dz);
            if (l > 1e-3) _move.set((dx / l) * T.squareUpStep, 0, (dz / l) * T.squareUpStep);
          }
        }
        // Press on the frame closest to the planned moment (frames are ~16 ms apart).
        if (!this._catchPressed && perceiving && t.timeToImpact <= this._plannedCatchLead + this._lastDt * 0.5) {
          intent.catchPressed = true;
          this._catchPressed = true;
        }
        break;
      }
      case BOT_RESPONSE.SIDESTEP:
        if (now >= this._responseStartAt) {
          _move.copy(this.dodgeDir);
          sprint = !isCharging(self);
        }
        break;
      case BOT_RESPONSE.JUMP:
        _move.copy(this.dodgeDir).multiplyScalar(0.5); // drift off the line while hopping over it
        if (!this._maneuverPressed && perceiving && t.timeToImpact <= this._maneuverLead) {
          intent.jump = true;
          this._maneuverPressed = true;
        }
        break;
      case BOT_RESPONSE.SLIDE:
        _move.copy(this.dodgeDir);
        sprint = true;
        if (!this._maneuverPressed && perceiving && t.timeToImpact <= this._maneuverLead) {
          intent.slide = true;
          this._maneuverPressed = true;
        }
        break;
      default: // BRACE: face the ball and take it
        _move.set(0, 0, 0);
        break;
    }
    this.destination.set(self.position.x + _move.x * 1.5, self.position.y, self.position.z + _move.z * 1.5);
    return sprint;
  }

  _execRetrieve(intent, now) {
    const self = this.player, T = this.tuning, ball = this.claimedBall;
    if (!ball || (ball.state !== BALL_STATE.FREE && ball.state !== BALL_STATE.STASIS) || holdsBall(self)) {
      this._forceDecision = true;
      return this._execPosition(intent, now);
    }
    const pos = ball.position;
    // Run onto the ball (auto pick-up), leading a rolling ball a little.
    _spot.copy(pos);
    if (ball.state === BALL_STATE.FREE && ball.velocity) { _spot.x += ball.velocity.x * 0.25; _spot.z += ball.velocity.z * 0.25; }
    clampPlanar(_spot, this._conf, _spot);
    _spot.y = self.position.y;
    this.destination.copy(_spot);
    seek(self.position, _spot, 0.05, 0.6, _move);
    const planar = planarDistance(self.position, pos);
    const sprint = planar > T.sprintDistance || now < this._openingRushUntil;
    _look.copy(pos);

    // Give up on a ball chased for too long (stuck against someone, keeps rolling away): ignore it for a while.
    if (now - this._claimedBallTime > T.maxChaseTime) {
      this._ignoredBall = ball;
      this._ignoredBallUntil = now + 3;
      this.claimedBall = null;
      this._forceDecision = true;
    }

    const manual = combatProfile(self).manualPickupRadius || 1.6;
    if (planar <= manual * 0.9 && now >= this._nextPickupPressAt) {
      const height = pos.y - self.position.y;
      if (height <= T.stasisReachHeight) {
        // Leap for a ball frozen high in Chrono's stasis field.
        const motor = self.motor;
        if (height > T.stasisJumpHeight && (!motor || motor.canJump !== false) && !hasStatus(self, 'dodgeDisabled') &&
          !stateIs(self, STATE.AIRBORNE)) intent.jump = true;
        intent.pickup = true;
        this._nextPickupPressAt = now + T.pickupRepressInterval;
      }
    }
    return sprint;
  }

  _execAttack(intent, now, dt) {
    const self = this.player, T = this.tuning, target = this.currentTarget;
    if (!target || !target.isTargetable) {
      this._forceDecision = true;
      return this._execPosition(intent, now);
    }
    const strafe = this._updateStrafe(now, dt);
    this._attackSpot(target, strafe, _spot);
    this.destination.copy(_spot);
    const distance = seek(self.position, _spot, T.arriveRadius * 0.5, T.slowRadius, _move);
    const sprint = distance > T.sprintDistance && this._throwPhase === THROW_NONE;

    const fooled = this._isFooledBy(target);
    chestOf(target, _look);
    if (fooled) _look.add(this.decoyOffset);
    intent.target = fooled ? null : target; // lets the combat lock the right enemy (not a decoy)

    if (this._throwPhase === THROW_NONE) this._tryBeginThrow(now, target, distance);
    return sprint;
  }

  _execPass(intent, now) {
    const sprint = this._execPosition(intent, now);
    if (!this._pendingPass) return sprint;
    this._pendingPass = false;
    this._forceDecision = true;
    const receiver = this._passReceiver;
    if (this._throwPhase !== THROW_NONE || !holdsBall(this.player) || !receiver) return sprint;
    intent.pass = true;
    intent.target = receiver;
    chestOf(receiver, _look);
    this._passInclination = false;          // one considered pass per possession
    this._nextPassAllowedAt = now + 1.5;    // and never pass spam if the pass could not happen
    return sprint;
  }

  _execPosition(intent, now) {
    const self = this.player, T = this.tuning;
    if (self.zone === ZONE.OUTFIELD) this._outfieldSpot(now, _spot); else this._infieldSpot(now, _spot);
    _spot.y = self.position.y;
    this.destination.copy(_spot);
    // An opponent visibly winding up at us: stop wandering and square up (an idle body turns its chest - and the
    // catch cone - to the aim), exactly what a player does instead of showing the thrower their back.
    const thrower = self.zone === ZONE.INFIELD ? this._windupAtMe() : null;
    if (thrower) {
      _move.set(0, 0, 0);
      this._settled = true;
      chestOf(thrower, _look);
      return false;
    }
    // Settle hysteresis: once on the spot, stand and watch (the body keeps its catch cone on the threats) until the
    // wandering spot has drifted clearly away.
    let distance = planarDistance(self.position, _spot);
    if (this._settled && distance < T.settleRadius) _move.set(0, 0, 0);
    else {
      distance = seek(self.position, _spot, T.arriveRadius, T.slowRadius, _move);
      this._settled = distance <= T.arriveRadius;
    }
    this._chooseWatchPoint(_look);
    return distance > T.sprintDistance * 1.5;
  }

  // ------------------------------------------------------------------ destinations

  /** Rank of the bot among teammates in its zone (stable registry order) -> lane; writes count into this._laneCount. */
  _laneRank() {
    const self = this.player, players = game.players;
    let rank = 0, count = 0;
    for (let i = 0; i < players.length; i++) {
      const p = players[i];
      if (p.team !== self.team || p.zone !== self.zone) continue;
      if (p === self) rank = count;
      count++;
    }
    this._laneCount = count;
    return rank;
  }

  _infieldSpot(now, out) {
    const self = this.player, P = this.profile, inner = this._inner;
    const court = game.court;
    const halfL = court ? court.halfL : 9, width = court ? court.width : 9;
    const s = sideSign(self.team);
    const rank = this._laneRank(), count = this._laneCount;
    const lane = count > 1 ? (rank - (count - 1) * 0.5) * (width / (count + 0.5)) : 0;
    const wx = perlin1D(this._noiseSeedX, now * 0.22) * 0.9;
    const wz = perlin1D(this._noiseSeedZ, now * 0.22) * 0.7;
    const depth = clamp(P.preferredDepth - (this._aggression - 0.5) * 2 + wz, 1.2, halfL - 0.8);
    out.set(lane + wx, self.position.y, CENTER_Z + s * depth);
    this._holderRepulsion(out, _pushA); out.x += _pushA.x; out.z += _pushA.z;
    this._teammateRepulsion(out, P.teammateSpacing, _pushB); out.x += _pushB.x; out.z += _pushB.z;
    // Avoid being cornered: the closer to an edge, the stronger the pull back toward the middle of the half.
    const edge = edgeProximity(out.x, out.z, inner);
    if (edge > 0.7) {
      const k = Math.min(1, (edge - 0.7) * 0.8);
      out.x = lerp(out.x, (inner.minX + inner.maxX) * 0.5, k);
      out.z = lerp(out.z, (inner.minZ + inner.maxZ) * 0.5, k);
    }
    return clampPlanar(out, inner, out);
  }

  _outfieldSpot(now, out) {
    const self = this.player, P = this.profile, inner = this._inner;
    // Line up behind the visible enemies (their backs), spread across the strip.
    let sumX = 0, n = 0;
    const players = game.players;
    for (let i = 0; i < players.length; i++) {
      const e = players[i];
      if (!game.areEnemies(self, e) || !e.isTargetable || !isPerceivable(self, e, P.cloakDetectionRadius)) continue;
      sumX += e.position.x; n++;
    }
    let x = n > 0 ? sumX / n : (inner.minX + inner.maxX) * 0.5;
    const rank = this._laneRank(), count = this._laneCount;
    if (count > 1) x += (rank - (count - 1) * 0.5) * 2.5;
    const wx = perlin1D(this._noiseSeedX, now * 0.3) * 0.8;
    const wz = perlin1D(this._noiseSeedZ, now * 0.3) * 0.3;
    const cz = (inner.minZ + inner.maxZ) * 0.5;
    const courtSideZ = Math.abs(inner.maxZ - CENTER_Z) < Math.abs(inner.minZ - CENTER_Z) ? inner.maxZ : inner.minZ;
    out.set(x + wx, self.position.y, lerp(cz, courtSideZ, 0.5) + wz);
    this._teammateRepulsion(out, P.teammateSpacing, _pushB); out.x += _pushB.x; out.z += _pushB.z;
    return clampPlanar(out, inner, out);
  }

  /**
   * Throwing position: 6-12 m from the target, depth from the centre line by aggression, halfway across toward the
   * target's lane plus a lateral strafe. From the outfield: behind the target at the court-side edge of the strip.
   */
  _attackSpot(target, strafe, out) {
    const self = this.player, P = this.profile, T = this.tuning, inner = this._inner, pos = self.position;
    if (self.zone === ZONE.OUTFIELD) {
      const cz = (inner.minZ + inner.maxZ) * 0.5;
      const courtSideZ = Math.abs(inner.maxZ - CENTER_Z) < Math.abs(inner.minZ - CENTER_Z) ? inner.maxZ : inner.minZ;
      out.set(target.position.x + strafe * 0.6, pos.y, lerp(cz, courtSideZ, 0.7));
      this._teammateRepulsion(out, P.teammateSpacing, _pushB); out.x += _pushB.x; out.z += _pushB.z;
      return clampPlanar(out, inner, out);
    }
    const s = sideSign(self.team);
    let depth = lerp(T.attackLineDepthMax, T.attackLineDepthMin, clamp01(this._aggression));
    if (planarDistance(pos, target.position) > T.idealRangeMax) depth = T.attackLineDepthMin;
    // Keep at least the preferred minimum range from a target hugging the centre line.
    const targetDepth = Math.abs(target.position.z - CENTER_Z);
    depth = Math.max(depth, Math.min(T.idealRangeMin - targetDepth, T.attackLineDepthMax + 2));
    out.set(lerp(pos.x, target.position.x, 0.5) + strafe, pos.y, CENTER_Z + s * depth);
    // Cautious bots still respect other enemy ball holders while attacking.
    this._holderRepulsion(out, _pushA);
    const k = 0.5 * (1 - clamp01(this._aggression));
    out.x += _pushA.x * k; out.z += _pushA.z * k;
    this._teammateRepulsion(out, P.teammateSpacing, _pushB); out.x += _pushB.x * 0.5; out.z += _pushB.z * 0.5;
    return clampPlanar(out, inner, out);
  }

  _updateStrafe(now, dt) {
    const rng = this.rng;
    if (now >= this._strafeSwitchAt) {
      let next = randomSign(rng) * this.profile.strafeAmplitude * rng.range(0.35, 1);
      if (Math.sign(next) === Math.sign(this._strafeCurrent) && rng.chance(0.6)) next = -next;
      this._strafeTarget = next;
      this._strafeSwitchAt = now + rng.range(0.35, 1.1);
    }
    this._strafeCurrent = moveTowards(this._strafeCurrent, this._strafeTarget, 3.5 * dt);
    return this._strafeCurrent;
  }

  // ------------------------------------------------------------------ steering forces (planar, into `out`)

  /** Push away from perceivable enemies holding a ball (Silent Footsteps holders sneaking behind are missed). */
  _holderRepulsion(spot, out) {
    const self = this.player, P = this.profile, T = this.tuning;
    out.set(0, 0, 0);
    const avoid = P.holderAvoidDistance;
    const fx = forwardX(self), fz = forwardZ(self);
    const players = game.players;
    for (let i = 0; i < players.length; i++) {
      const e = players[i];
      if (!game.areEnemies(self, e) || !holdsBall(e) || !isPerceivable(self, e, P.cloakDetectionRadius)) continue;
      if (hasStatus(e, 'silentFootsteps')) {
        const ex = e.position.x - self.position.x, ez = e.position.z - self.position.z;
        const el = Math.hypot(ex, ez) || 1;
        if ((fx * ex + fz * ez) / el < -0.2 && el > T.awarenessRadius) continue;
      }
      let ax = spot.x - e.position.x, az = spot.z - e.position.z;
      const d = Math.hypot(ax, az);
      if (d >= avoid) continue;
      if (d < 1e-3) { ax = -fx; az = -fz; } else { ax /= d; az /= d; }
      const w = (avoid - d) * 0.6;
      out.x += ax * w; out.z += az * w;
    }
    return out;
  }

  /** Push away from teammates in the same zone (spreading makes double kills harder). */
  _teammateRepulsion(spot, spacing, out) {
    const self = this.player;
    out.set(0, 0, 0);
    const players = game.players;
    for (let i = 0; i < players.length; i++) {
      const m = players[i];
      if (!game.areTeammates(self, m) || m.zone !== self.zone) continue;
      let ax = spot.x - m.position.x, az = spot.z - m.position.z;
      const d = Math.hypot(ax, az);
      if (d >= spacing) continue;
      if (d < 1e-3) { ax = forwardZ(self); az = -forwardX(self); } else { ax /= d; az /= d; }
      const w = (spacing - d) * 0.5;
      out.x += ax * w; out.z += az * w;
    }
    return out;
  }

  /** Short-range push away from any nearby body. */
  _separation(out) {
    const self = this.player, r = this.tuning.separationRadius;
    out.set(0, 0, 0);
    const players = game.players;
    for (let i = 0; i < players.length; i++) {
      const p = players[i];
      if (p === self || !p.position) continue;
      const ax = self.position.x - p.position.x, az = self.position.z - p.position.z;
      const d = Math.hypot(ax, az);
      if (d >= r || d < 1e-4) continue;
      const w = (1 - d / r) / d;
      out.x += ax * w; out.z += az * w;
    }
    return out;
  }

  /** What an idle bot keeps its eyes (and catch cone) on: the nearest enemy ball holder, else the nearest enemy. */
  _chooseWatchPoint(out) {
    const self = this.player, P = this.profile;
    let holder = null, nearest = null, holderD = Infinity, nearestD = Infinity;
    const players = game.players;
    for (let i = 0; i < players.length; i++) {
      const e = players[i];
      if (!game.areEnemies(self, e) || !isPerceivable(self, e, P.cloakDetectionRadius)) continue;
      if (e.health && e.health.isAlive === false) continue;
      const d = planarDistance(self.position, e.position);
      if (holdsBall(e) && d < holderD) { holderD = d; holder = e; }
      if (e.isTargetable && d < nearestD) { nearestD = d; nearest = e; }
    }
    if (holder) return chestOf(holder, out);
    if (nearest) return chestOf(nearest, out);
    return this._enemyHalfPoint(out);
  }

  /** Centre of the enemy half at chest height (where an idle player looks). */
  _enemyHalfPoint(out) {
    const self = this.player, court = game.court;
    const halfL = court ? court.halfL : 9;
    chestOf(self, out);
    out.x = 0;
    out.z = CENTER_Z + sideSign(opponent(self.team)) * halfL * 0.5;
    // Never look straight down / at our own feet: push the point at least 3 m away along the facing line.
    const dx = out.x - self.position.x, dz = out.z - self.position.z;
    if (dx * dx + dz * dz < 9) { out.x = self.position.x + forwardX(self) * 10; out.z = self.position.z + forwardZ(self) * 10; }
    return out;
  }

  _faceEnemyHalf(intent) {
    this._enemyHalfPoint(_look);
    this._setAim(intent, _look, null);
  }

  // ================================================================== throw sequence

  _tryBeginThrow(now, target, distanceToSpot) {
    const self = this.player, T = this.tuning;
    if (!holdsBall(self) || now < this._throwReadyAt || now - this._lastThrowTime < T.postThrowDelay) return;
    if (stateIs(self, STATE.AIRBORNE) || stateIs(self, STATE.SLIDING) || stateIs(self, STATE.CATCHING)) return;
    const range = planarDistance(self.position, target.position);
    const maxRange = self.zone === ZONE.OUTFIELD ? T.outfieldMaxThrowRange : T.maxThrowRange;
    if (range > maxRange) return;
    // Still closing in on a distant target: keep approaching unless the ball has been held for a while.
    if (range > T.idealRangeMax && distanceToSpot > 1.5 && now - this._possessionStart < T.maxHoldTime * 0.5) return;
    this._plannedCharge = this._chooseChargeTime(now, range, target);
    this._throwTarget = target;
    this._opportunityRolled = false;
    this._throwPhase = THROW_PRESSING;
  }

  /**
   * Charge time: short for close targets (surprise), long for distant ones (spec 0.2-1.2 s; Rayne holds up to 2 s for
   * Overcharge); committed targets are punished quickly; a perfect-catch counter boost is spent before it expires.
   */
  _chooseChargeTime(now, range, target) {
    const self = this.player, P = this.profile, T = this.tuning, tr = this.traits;
    const lo = tr.chargeTimeMax > 0 ? tr.chargeTimeMin : P.chargeTimeMin;
    const hi = tr.chargeTimeMax > 0 ? tr.chargeTimeMax : P.chargeTimeMax;
    const rangeT = inverseLerp(T.idealRangeMin * 0.6, T.idealRangeMax * 1.2, range);
    let charge = jitter(this.rng, lerp(lo, hi, rangeT), 0.25);
    if (isCommitted(target)) charge *= 0.6;
    const combat = self.combat;
    if (combat && combat.counterBoostUntil > now) charge = Math.min(charge, Math.max(lo, combat.counterBoostUntil - now - 0.15));
    const maxCharge = combatProfile(self).maxChargeTime || 2.5;
    return clamp(charge, 0.05, Math.max(0.05, maxCharge - 0.05));
  }

  /**
   * Drives the throw button like a human: throwPressed (+throwHeld) on the first frame, throwHeld while the state
   * machine charges, then throwReleased once the planned charge is reached - or earlier when the target commits
   * (opportunism), a ball is about to hit us (panic) or the charge cap is near.
   */
  _updateThrowSequence(intent, now) {
    if (this._throwPhase === THROW_NONE) return;
    const self = this.player, T = this.tuning, combat = self.combat;
    if (!combat) { this._throwPhase = THROW_NONE; return; }

    if (this._throwPhase === THROW_PRESSING) {
      if (!combat.hasBall) { this._throwPhase = THROW_NONE; return; }
      intent.throwPressed = true;
      intent.throwHeld = true;
      this._throwPhase = THROW_HOLDING;
      this._throwPressTime = now;
      this._aimAtThrowTarget(intent, this._resolveThrowTarget());
      return;
    }

    // Holding.
    const charging = !!combat.isCharging;
    if (!charging && !combat.hasBall) {
      this._throwPhase = THROW_NONE; // ball knocked out of our hands (Earthquake Slam, Grand Vanish, stun...)
      this._forceDecision = true;
      return;
    }
    if (!charging && now - this._throwPressTime > T.chargeStartTimeout) {
      // The state machine never entered ChargingThrow (e.g. we were airborne): let go and retry later.
      intent.throwHeld = false;
      intent.throwReleased = true;
      this._throwPhase = THROW_NONE;
      this._throwReadyAt = now + 0.25;
      return;
    }

    const target = this._resolveThrowTarget();
    const charged = charging ? (combat.chargeSeconds || 0) : 0;
    const cap = combatProfile(self).maxChargeTime || 2.5;
    let release = charging && (charged >= this._plannedCharge || charged >= cap - 0.05);

    // Panic release: a ball will hit us before we can finish the charge.
    if (!release && charging && this._threatActive && this._response !== BOT_RESPONSE.CATCH &&
      this.threat.timeToImpact <= T.panicReleaseTime && charged >= T.minEarlyReleaseCharge) release = true;

    // Opportunism: the target just committed (jumped, started charging, got stunned) - punish it now.
    if (!release && charging && target && !this._opportunityRolled && charged >= T.minEarlyReleaseCharge && isCommitted(target)) {
      this._opportunityRolled = true;
      if (this.rng.chance(this.profile.opportunismSkill)) release = true;
    }

    if (!release) {
      intent.throwHeld = true;
      this._aimAtThrowTarget(intent, target);
      return;
    }
    intent.throwHeld = false;
    intent.throwReleased = true;
    this._applyReleaseAim(intent, target, charged, now);
    this._throwPhase = THROW_NONE;
    this._throwTarget = null;
    this._lastThrowTime = now;
    this._counterAttack = false;
    this._forceDecision = true;
  }

  _resolveThrowTarget() {
    if (this.currentTarget && this.currentTarget.isTargetable) return this.currentTarget;
    if (this._throwTarget && this._throwTarget.isTargetable) return this._throwTarget;
    return null;
  }

  _isFooledBy(target) {
    return !!target && this._decoyFor === target && this.decoyOffset.lengthSq() > 0;
  }

  /** While charging: face the target (rough lead, no noise) so the body winds up toward it. */
  _aimAtThrowTarget(intent, target) {
    if (!target) return;
    const fooled = this._isFooledBy(target);
    chestOf(target, _aim);
    const v = target.velocity;
    if (v) { _aim.x += v.x * 0.25; _aim.z += v.z * 0.25; }
    if (fooled) _aim.add(this.decoyOffset);
    this._setAim(intent, _aim, null);
    intent.target = fooled ? null : target;
  }

  /**
   * Release aim: intercept lead blended by leadAccuracy + Gaussian angular error (yaw sigma = aimErrorDeg, pitch half
   * of it), plus the decoy offset when fooled by clones. The lock (intent.target) is kept only when the noisy aim
   * still points at the target, so aim assist never corrects a throw the bot actually pulled. No target: straight
   * into the enemy half.
   */
  _applyReleaseAim(intent, target, charged, now) {
    const self = this.player, P = this.profile, T = this.tuning, combat = self.combat;
    const o = combat && typeof combat.getThrowOrigin === 'function' ? combat.getThrowOrigin() : null;
    if (o && Number.isFinite(o.x)) _origin.copy(o); else chestOf(self, _origin);

    if (!target) {
      this._enemyHalfPoint(_aim);
      intent.target = null;
      this._setAim(intent, _aim, _origin);
      return;
    }
    const fooled = this._isFooledBy(target);
    chestOf(target, _v1);
    const speed = estimateThrowSpeed(self, charged, now);
    interceptPoint(_origin, _v1, target.velocity || _ZERO, speed, _lead, T.maxLead);
    _aim.lerpVectors(_v1, _lead, P.leadAccuracy);
    if (fooled) _aim.add(this.decoyOffset);
    _lock.copy(_aim);

    if (P.aimErrorDeg > 0) {
      _dir.subVectors(_aim, _origin);
      const dist = _dir.length();
      if (dist > 0.1) {
        _dir.divideScalar(dist);
        let rx = _dir.z, rz = -_dir.x; // right = cross(up, dir)
        const rl = Math.hypot(rx, rz);
        if (rl < 1e-4) { rx = 1; rz = 0; } else { rx /= rl; rz /= rl; }
        const yaw = this.rng.gaussian(0, P.aimErrorDeg) * DEG2RAD;
        const pitch = this.rng.gaussian(0, P.aimErrorDeg * 0.5) * DEG2RAD; // vertical error is smaller for a practised arm
        const lateral = Math.tan(clamp(yaw, -1.2, 1.2)) * dist, vertical = Math.tan(clamp(pitch, -1.2, 1.2)) * dist;
        _aim.x += rx * lateral; _aim.z += rz * lateral; _aim.y += vertical;
      }
    }
    intent.target = !fooled && _aim.distanceTo(_lock) <= T.lockKeepRadius ? target : null;
    this._setAim(intent, _aim, _origin);
  }

  // ================================================================== one-shot presses / aim

  _applyPendingAbility(intent) {
    if (!this._pendingSkill && !this._pendingUltimate) return;
    // Never mix an ability press into the frame that presses or releases a throw; try again next frame.
    if (intent.throwPressed || intent.throwReleased) return;
    if (this._throwPhase !== THROW_NONE) {
      this._pendingSkill = this._pendingUltimate = false;
      return;
    }
    const self = this.player, ab = self.abilities;
    const ability = ab ? (this._pendingUltimate ? ab.ultimate : ab.skill) : null;
    if (this._pendingUltimate) intent.ultimate = true; else intent.skill = true;
    this._pendingSkill = this._pendingUltimate = false;

    // Threat answers (Stasis Field, Temporal Reset, Tackle Intercept...) aim at the incoming ball; everything else
    // (Supersonic Meteor, Glacier Freeze, Swap Places...) uses the explicit target and aim of this frame.
    const hint = ability && ability.def ? ability.def.aiHint : null;
    const ball = this._abilityBall;
    if (hint === AI_HINT.THREATENED && ball && ball.state === BALL_STATE.LIVE) {
      _aim.copy(ball.position);
      this._setAim(intent, _aim, null);
      intent.target = ball.lastThrower && game.areEnemies(self, ball.lastThrower) && ball.lastThrower.isTargetable ? ball.lastThrower : null;
    } else {
      const target = this._abilityTarget && this._abilityTarget.isTargetable ? this._abilityTarget : this.currentTarget;
      if (target && isPerceivable(self, target, this.profile.cloakDetectionRadius)) {
        intent.target = target;
        chestOf(target, _aim);
        this._setAim(intent, _aim, null);
      }
    }
    this._abilityTarget = null;
    this._abilityBall = null;
  }

  /** aimPoint = point, aimDir = unit vector from `origin` (default: the chest) to it. */
  _setAim(intent, point, origin) {
    const o = origin || chestOf(this.player, _chest);
    _dir.subVectors(point, o);
    const l2 = _dir.lengthSq();
    if (l2 < 1e-4) return;
    intent.aimPoint.copy(point);
    intent.aimDir.copy(_dir).multiplyScalar(1 / Math.sqrt(l2));
  }
}
