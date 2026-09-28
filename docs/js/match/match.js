// ---------------------------------------------------------------------------------------------------------------
// Match (system, owner: match) - 4v4 best-of-3 flow for Dodgeball Ultra (web). Contract: WEB_ARCHITECTURE.md §3.6.
//
//   startMatch(setup) ─► spawn 8 Players (await init) ─► preRound ─► countdown (3,2,1) ─► playing ─► roundEnd ─┐
//                                                          ▲                                                  │
//                                                          └──────────── next round (no match winner yet) ◄───┤
//                                                    matchEnd (MatchEnded) ─► 6 s ─► hero select / new spectate match
//
// Teams: 3 INFIELD players + 1 starting OUTFIELDER (元外野) each. A team's outfield is the U around the OPPONENT's
// half (both sideline strips + the strip behind its baseline). The starting outfielder stays out there all round:
// never returns on a hit, is never revived by a Perfect Catch, and does not count as "in".
// Infield / outfield (Taiwanese 內場 / 外場): an eliminated player ragdolls for rules.ragdollTime (scaled time, so
// hitstop and pause hold the ragdoll) and is then moved to the U, from where they keep throwing. A Perfect Catch
// brings back the ELIMINATED teammate who has waited the longest (reviveOneOutfield, called by Combat and
// de-duplicated here); an eliminated outfielder who lands a hit returns to the infield. A round ends the moment a
// team has nobody left in the infield (players whose elimination is pending - Chrono - still count), or at time-up
// (more infield players, then more total infield HP, else a draw that scores nothing and is replayed).
//
// ONE ball (rules.ballCount = 1). Every round one team serves (一方持球開球): round 1 by seeded coin, then the team
// that lost the previous round, after a draw the team that did not serve last time; the server holds the ball
// through preRound and the countdown. Anti-stall possession clock (rules.possessionLimit, 10 s): holding the ball -
// or leaving it loose in your own zone (dead-ball rule) - that long without a throw or pass hands it to the
// opponents (BallManager.awardBall). A throw / pass releases the clock; a change of owning team restarts it.
//
// Players are NOT game systems: Match drives them through a tiny PlayerDriver registered at ORDER.PLAYERS, so every
// Player updates after Input/AI and before abilities, balls (held balls follow the hand socket the same frame),
// ragdoll physics and the camera. The round flow itself runs at ORDER.MATCH on SCALED time (pause/hitstop freeze
// the clock); only the round-deciding slow-motion recovery uses real time.
// ---------------------------------------------------------------------------------------------------------------
import * as THREE from 'three';
import { game, ORDER } from '../game.js';
import { EV } from '../core/events.js';
import { TEAM, ZONE, TEAM_COLORS, HERO_IDS, opponent } from '../core/constants.js';
import { heroById } from '../abilities/roster.js';
import { INTERRUPT } from '../abilities/abilityBase.js';
// Namespace imports: a sibling module exporting its class as default instead of the named export (or not yet at
// all) must not break linking of the whole game; the classes are resolved lazily at spawn time.
import * as PlayerModule from '../gameplay/player.js';
import * as InputModule from '../input/input.js';
import * as BotModule from '../ai/bot.js';
import {
  tallyTeams, decideRound, applyRoundResult, pickLongestWaiting, lowestFreeIndex, pickSpawnSlot, countdownNumber,
  ROUND_END_REASON, NO_WINNER, HP_TIE_TOLERANCE, nextServeTeam, pickStartingOutfielders, makePossessionClock,
  stepPossession, releasePossession, pickAwardReceiver,
} from './outcome.js';

/** Match phases (`Match.phase`). */
export const MATCH_PHASE = Object.freeze({
  IDLE: 'idle', SELECT: 'select', PRE_ROUND: 'preRound', COUNTDOWN: 'countdown', PLAYING: 'playing',
  ROUND_END: 'roundEnd', MATCH_END: 'matchEnd',
});

/** `cause` values of EV.PlayerRevived emitted by the match. */
export const REVIVE_CAUSE = Object.freeze({
  PERFECT_CATCH: 'perfectCatch', OUTFIELD_HIT: 'outfieldHit', ROUND_RESET: 'roundReset', ABILITY: 'ability',
});

/** Presentation tuning of the match flow (camera framing, broadcast slow motion, revive effect). */
export const MATCH_TUNING = Object.freeze({
  /** Height (m) of the cinematic camera focus above the floor (chest height of the line-up). */
  cinematicFocusHeight: 1.15,
  /** Round-deciding elimination: game clock drops to `scale` for `realDuration` real seconds ... */
  deciderSlowMoScale: 0.3,
  deciderSlowMoDuration: 1.1,
  /** ... and eases back to 1 over the last `deciderSlowMoRecover` fraction of it. */
  deciderSlowMoRecover: 0.4,
  /** Scale of the reviveBeam VFX. */
  reviveVfxScale: 1,
  /** Throw-back impulse (N·s) on a thrower eliminated by a catch when rules.catchEliminatesThrower is on. */
  caughtThrowerImpulse: 28,
  /** Minimum ragdoll time (s) before the outfield move, whatever the rules say (lets the fall read). */
  minRagdollTime: 0.2,
});

/** Debuffs cleared on every zone change (passives such as Silent Footsteps must survive). */
const ZONE_CHANGE_DEBUFFS = Object.freeze(['frozen', 'slow', 'stunned', 'rooted', 'slippery', 'dodgeDisabled']);

/** Player driver: updates every Player at ORDER.PLAYERS (players are not systems). Named for kernel error logs. */
class PlayerDriver {
  /** @param {Match} match */
  constructor(match) { this.match = match; }
  fixedUpdate(dt) { this.match._forEachPlayer('fixedUpdate', dt); }
  update(dt, realDt) { this.match._forEachPlayer('update', dt, realDt); }
}

/**
 * Per-player match bookkeeping.
 * @typedef {object} MatchRecord
 * @property {any} player            the Player
 * @property {number} team           TEAM.HOME / TEAM.AWAY
 * @property {number} slot           spawn slot in the team line-up
 * @property {number} eliminatedAt   scaled time of the elimination (-1 = none this round)
 * @property {number} transferAt     scaled time the ragdoll ends and the outfield move happens (-1 = none pending)
 * @property {number} outfieldSince  scaled time of arrival in the outfield (-1 = not in the outfield)
 * @property {number} outfieldSpot   outfield spot index (-1 = none)
 * @property {boolean} startsOutfield starting outfielder (元外野): in the outfield all round, never revived / returned
 * @property {number} infieldIndex   index among the team's infield starters (spawn line), -1 for starting outfielders
 * @property {number} starterIndex   index among the team's starting outfielders (outfield spot), -1 otherwise
 */

export class Match {
  /** Default rules (contract §3.6). Copied into `this.rules` at every startMatch (URL params / setup.rules override). */
  static defaults = Object.freeze({
    playersPerTeam: 4,         // 3 infield + 1 starting outfielder
    startingOutfielders: 1,    // 元外野 per team, clamped to [0, playersPerTeam - 1]
    roundsToWin: 2,            // best of 3
    roundTime: 150,            // s
    preRound: 2.5,             // s of line-up / cinematic intro
    countdown: 3,              // 3, 2, 1
    roundEnd: 4,               // s of celebration between rounds
    matchEndDelay: 6,          // s on the result screen before hero select (or the next spectate match)
    ballCount: 1,              // single-ball rules (forced to 1)
    possessionLimit: 10,       // s (scaled) of possession before the ball goes to the opponents (0 = off; ?possession=N)
    possessionWarning: 3,      // s: last seconds announced (EV.PossessionWarning, HUD countdown)
    deadBallClock: true,       // a loose ball resting in a team's zone runs that team's clock
    awardDelay: 0.6,           // s the ball is hidden while handed over after a violation
    outOfArenaAwardDelay: 1.5, // s the ball is hidden after leaving the arena
    outfieldHitRevives: true,  // an ELIMINATED outfield player who hits an infield enemy returns to the infield
    catchEliminatesThrower: false,
    ragdollTime: 2,            // s between elimination and the outfield move
    reviveHp: 1,               // HP fraction restored when returning from the outfield
    hpTieTolerance: HP_TIE_TOLERANCE,
    // Ultimate meter gains (0..1). The +0.15 Perfect Catch gain is granted by Combat.
    ultPerSecond: 0.008,       // passive, while infield during live play (AbilityController.passiveUltPerSecond)
    ultOnHit: 0.12,            // thrower of a landed hit
    ultOnEliminate: 0.08,      // extra when that hit eliminates
    ultOnCatch: 0.06,          // normal catch
    keepUltimateBetweenRounds: true,
    loopSpectate: true,        // spectate (attract) mode starts a new AI match after each one
  });

  constructor() {
    /** Active rules (see Match.defaults). */
    this.rules = { ...Match.defaults };
    /** @type {'idle'|'select'|'preRound'|'countdown'|'playing'|'roundEnd'|'matchEnd'} */
    this.phase = MATCH_PHASE.IDLE;
    /** 1-based round number (0 before the first round). */
    this.round = 0;
    /** Seconds left in the current round (scaled time). */
    this.timeLeft = 0;
    /** Rounds won [home, away] (mutated in place so references stay valid). */
    this.scores = [0, 0];
    /** Match winner (TEAM) once decided, else -1. */
    this.winner = NO_WINNER;
    /** Winner / reason of the last finished round. */
    this.lastRoundWinner = NO_WINNER;
    this.lastRoundReason = null;
    /** Number currently shown by the countdown (3, 2, 1; 0 otherwise). */
    this.countdownLeft = 0;
    /** All players of the current match (home line-up first). */
    this.players = [];
    /** The human-controlled local player (null when spectating). */
    this.local = null;
    /** Normalised setup of the current match. */
    this.setup = null;

    /** @type {Map<any, MatchRecord>} player -> record (lookups from events) */
    this._records = new Map();
    /** @type {MatchRecord[]} same records as an array (allocation-free iteration in per-frame code) */
    this._recs = [];
    this._phaseTime = 0;
    this._startToken = 0;
    this._matchCount = 0;
    this._driver = new PlayerDriver(this);
    this._driverAdded = false;
    this._slowMoLeft = 0;          // real seconds of decider slow motion left (0 = not ours)
    this._reviveDedupe = { frame: -1, reviver: null, revived: null };
    this._errReported = new WeakMap();
    // Reused round-state snapshots (no per-frame allocations).
    this._snaps = [];
    this._tally = { infield: [0, 0], hp: [0, 0] };

    /** Team serving the current round (TEAM.NONE before the first preRound). */
    this.serveTeam = TEAM.NONE;
    /** Player who held the serve this round. */
    this.server = null;
    this._lastServeTeam = TEAM.NONE;
    /**
     * Possession clock readout (reused object; HUD / AI read it): owning team (TEAM.NONE = nobody), holder (null for a
     * dead ball), seconds elapsed / limit / remaining, running (the clock advances this frame), deadBall (loose ball).
     */
    this.possession = { team: TEAM.NONE, holder: null, elapsed: 0, limit: 0, remaining: Infinity, running: false, deadBall: false };
    this._clock = makePossessionClock();
    this._announcedTeam = TEAM.NONE;
    this._zoneOut = { team: TEAM.NONE, zone: ZONE.INFIELD };
    // Reused award-receiver candidates.
    this._awardCands = [];
    this._awardPlayers = [];

    const ev = game.events;
    this._unsubs = [
      ev.on(EV.PlayerEliminated, (e) => this._onPlayerEliminated(e)),
      ev.on(EV.PlayerRevived, (e) => this._onPlayerRevived(e)),
      ev.on(EV.BallHitPlayer, (e) => this._onBallHitPlayer(e)),
      ev.on(EV.BallCaught, (e) => this._onBallCaught(e)),
      ev.on(EV.BallThrown, () => this._onBallReleased()),
      ev.on(EV.BallPassed, (e) => { if (e && e.teleported) this._onBallReleased(); }),
    ];
  }

  async init() { this._ensureDriver(); }

  // ================================================================== public API

  /** True while the round is live (abilities, passive ult gain and timers run). */
  get isPlaying() { return this.phase === MATCH_PHASE.PLAYING; }
  /** Round length of the active rules (s). */
  get roundDuration() { return this.rules.roundTime; }

  /**
   * Starts a new match, replacing any running one.
   * @param {{localHero:string|null, localTeam:number, homeHeroes:string[], awayHeroes:string[], difficulty:string,
   *          spectate:boolean, rules?:object}} setup
   */
  async startMatch(setup) {
    const token = ++this._startToken;
    this._ensureDriver();
    this._teardown();
    this.rules = this._resolveRules(setup && setup.rules);
    this.setup = this._normalizeSetup(setup || {});
    this.scores[0] = this.scores[1] = 0;
    this.round = 0;
    this.timeLeft = this.rules.roundTime;
    this.winner = NO_WINNER;
    this.lastRoundWinner = NO_WINNER;
    this.lastRoundReason = null;
    this.countdownLeft = 0;
    this.serveTeam = TEAM.NONE;
    this.server = null;
    this._lastServeTeam = TEAM.NONE;
    this._resetPossession();
    this._matchCount++;
    this._setPhase(MATCH_PHASE.IDLE);

    if (!game.court) { console.error('[match] startMatch: no court (arena not built)'); return; }

    // ---- create the eight players (construction is synchronous; avatars load in parallel)
    const created = [];
    let id = 1;
    for (const team of [TEAM.HOME, TEAM.AWAY]) {
      const heroes = team === TEAM.HOME ? this.setup.homeHeroes : this.setup.awayHeroes;
      const localSlot = !this.setup.spectate && team === this.setup.localTeam ? Math.max(0, heroes.indexOf(this.setup.localHero)) : -1;
      // Starting outfielders: the highest slots that are not the human (the human always starts infield).
      const lineup = [];
      for (let slot = 0; slot < heroes.length; slot++) lineup.push({ slot, isLocal: slot === localSlot });
      const starters = pickStartingOutfielders(lineup, this.rules.startingOutfielders);
      for (let slot = 0; slot < heroes.length; slot++) {
        const player = this._createPlayer(id++, team, slot, heroes[slot], slot === localSlot);
        if (!player) continue;
        const startsOutfield = starters.includes(slot);
        player.isStartingOutfielder = startsOutfield;
        created.push({ player, team, slot, startsOutfield });
      }
    }

    const results = await Promise.allSettled(created.map((c) => Promise.resolve().then(() => c.player.init())));
    if (token !== this._startToken) {
      // A newer startMatch/endMatch superseded this one while avatars were loading.
      for (const c of created) this._disposePlayer(c.player);
      return;
    }

    const court = game.court;
    for (let i = 0; i < created.length; i++) {
      const { player, team, slot, startsOutfield } = created[i];
      if (results[i].status === 'rejected') {
        console.error(`[match] player ${player.id} (${player.hero && player.hero.id}) failed to initialise`, results[i].reason);
        this._disposePlayer(player);
        continue;
      }
      const record = {
        player, team, slot, eliminatedAt: -1, transferAt: -1, outfieldSince: -1, outfieldSpot: -1,
        startsOutfield: !!startsOutfield, infieldIndex: -1, starterIndex: -1,
      };
      this._records.set(player, record);
      this._recs.push(record);
      this.players.push(player);
      if (typeof game.registerPlayer === 'function') game.registerPlayer(player);
      try {
        player.inputLocked = true;
        if (player.abilities) player.abilities.passiveUltPerSecond = this.rules.ultPerSecond;
        if (player.motor && player.motor.setConfinement) player.motor.setConfinement(court.confinement(team, ZONE.INFIELD));
      } catch (e) { console.error('[match] player setup failed', e); }
    }
    this.local = this.players.find((p) => p.isLocal) || null;
    if (!this.players.length) { console.error('[match] no player could be spawned'); return; }
    this._assignLineIndices();

    // Camera follows the local player (spectate: null -> the rig's orbit over the court).
    const rig = game.cameraRig;
    if (rig && rig.setTarget) rig.setTarget(this.local, true);

    // The match ball (one): created at the Home serve point; every preRound hands it to the server.
    if (game.balls && game.balls.setupMatchBalls) {
      try {
        if (game.balls.tuning) {
          game.balls.tuning.awardDelay = this.rules.awardDelay;
          game.balls.tuning.outOfArenaAwardDelay = this.rules.outOfArenaAwardDelay;
        }
        game.balls.setupMatchBalls([court.servePoint(TEAM.HOME)]);
      } catch (e) { console.error('[match] setupMatchBalls failed', e); }
    }

    this._beginPreRound();
  }

  /** Aborts the current match (pause menu "quit", before opening hero select): removes players, back to idle. */
  endMatch() {
    this._startToken++; // cancels a startMatch still loading avatars
    this._teardown();
    this.round = 0;
    this.timeLeft = 0;
    this.scores[0] = this.scores[1] = 0;
    this.winner = NO_WINNER;
    this.countdownLeft = 0;
    this._setPhase(MATCH_PHASE.IDLE);
    const rig = game.cameraRig;
    if (rig) {
      if (rig.setCinematic) rig.setCinematic(null);
      if (rig.setTarget) rig.setTarget(null, false);
    }
  }

  /** Players of `team` still in the round (infield, not ragdolling; pending delayed eliminations count). */
  countInfield(team) {
    return this._computeTally().infield[team] || 0;
  }

  /** Total HP of `team`'s players still in the infield. */
  infieldHp(team) {
    return this._computeTally().hp[team] || 0;
  }

  /** Players of `team` standing in the outfield U (starting outfielders included). */
  countOutfield(team) {
    let n = 0;
    for (const p of this.players) if (p.team === team && p.zone === ZONE.OUTFIELD) n++;
    return n;
  }

  /** ELIMINATED players of `team` standing in the outfield (starting outfielders excluded). */
  countEliminatedOutfield(team) {
    let n = 0;
    for (const r of this._recs) if (r.team === team && !r.startsOutfield && r.player.zone === ZONE.OUTFIELD) n++;
    return n;
  }

  /** Is `player` a starting outfielder (元外野) of this match? */
  isStartingOutfielder(player) {
    const r = player && this._records.get(player);
    return !!(r && r.startsOutfield);
  }

  /**
   * Best player of `team` to hand an awarded ball to (BallManager.awardBall): the eligible INFIELD player nearest
   * `nearPos` (can receive, can act, not ragdolling), else the nearest eligible outfielder, else null.
   * @param {number} team
   * @param {{x:number,z:number}} [nearPos]
   */
  pickAwardReceiver(team, nearPos) {
    const cands = this._awardCands, players = this._awardPlayers;
    let n = 0;
    for (const r of this._recs) {
      if (r.team !== team) continue;
      const p = r.player;
      let c = cands[n];
      if (!c) c = cands[n] = { x: 0, z: 0, infield: true, eligible: false };
      c.x = p.position.x; c.z = p.position.z;
      c.infield = p.zone === ZONE.INFIELD;
      c.eligible = r.transferAt < 0 && p.canReceive !== false && p.canAct !== false;
      players[n] = p;
      n++;
    }
    cands.length = n; players.length = n;
    const x = nearPos ? nearPos.x : 0, z = nearPos ? nearPos.z : 0;
    const i = pickAwardReceiver(cands, x, z);
    return i >= 0 ? players[i] : null;
  }

  /** True while `player` ragdolls between elimination and the outfield move. */
  isAwaitingOutfield(player) {
    const r = this._records.get(player);
    return !!r && r.transferAt >= 0;
  }

  /** Seconds (scaled) `player` has spent in the outfield this round (0 when infield). */
  outfieldTime(player) {
    const r = this._records.get(player);
    if (!r || r.outfieldSince < 0 || player.zone !== ZONE.OUTFIELD) return 0;
    return Math.max(0, game.time.now - r.outfieldSince);
  }

  /**
   * Moves an eliminated player to the outfield right now (normally called automatically after rules.ragdollTime).
   * @returns {boolean} true if moved
   */
  sendToOutfield(p) {
    const r = this._records.get(p);
    if (!r || !game.court) return false;
    if (p.zone === ZONE.OUTFIELD && r.transferAt < 0) return false; // already out there
    this._moveToOutfield(r);
    return true;
  }

  /**
   * Returns an outfield player (or one still ragdolling toward it - a last-second save) to the infield with
   * rules.reviveHp. Only during live play, except for REVIVE_CAUSE.ROUND_RESET.
   * @param {any} p player
   * @param {string} [cause] REVIVE_CAUSE value (free string accepted for abilities)
   * @param {any} [reviver] player responsible (perfect catcher...) or null
   * @returns {boolean} true if revived
   */
  reviveFromOutfield(p, cause = REVIVE_CAUSE.ABILITY, reviver = null) {
    const r = this._records.get(p);
    const court = game.court;
    if (!r || !court) return false;
    if (cause !== REVIVE_CAUSE.ROUND_RESET && !this.isPlaying) return false;
    // The starting outfielder stays out there for the whole round.
    if (r.startsOutfield && cause !== REVIVE_CAUSE.ROUND_RESET) return false;
    const inOutfield = p.zone === ZONE.OUTFIELD;
    const awaiting = r.transferAt >= 0;
    if (!inOutfield && !awaiting) return false;

    r.transferAt = -1;
    const pos = this._freeInfieldSpawn(r);
    const yaw = court.spawnYaw(r.team);
    try {
      if (p.avatar && p.avatar.resetVisual) p.avatar.resetVisual(); // un-ragdoll / clear the outfield pose
      if (p.motor && p.motor.setConfinement) p.motor.setConfinement(court.confinement(r.team, ZONE.INFIELD));
      if (p.zone !== ZONE.INFIELD) p.setZone(ZONE.INFIELD);
      p.teleport(pos, yaw);
      this._clearDebuffs(p);
      if (p.health && p.health.revive) p.health.revive(this.rules.reviveHp);
      if (p.fsm) {
        if (p.fsm.release) p.fsm.release('eliminated');
        if (p.fsm.resetToGrounded) p.fsm.resetToGrounded();
      }
      if (p.history && p.history.clear) p.history.clear(); // rewinds must never pull a player back across zones
      p.inputLocked = !this.isPlaying;
    } catch (e) {
      console.error('[match] reviveFromOutfield failed', e);
    }
    r.eliminatedAt = -1;
    r.outfieldSince = -1;
    r.outfieldSpot = -1;

    game.events.emit(EV.PlayerRevived, { player: p, reviver, cause });
    if (game.vfx && game.vfx.play) {
      game.vfx.play('reviveBeam', pos, { color: TEAM_COLORS[r.team], scale: MATCH_TUNING.reviveVfxScale });
    }
    return true;
  }

  /**
   * Perfect Catch reward: revives the ELIMINATED teammate of `team` who has been in the outfield the longest (or, if
   * nobody is out there yet, the one ragdolling the longest). Starting outfielders are never revived. De-duplicated
   * per reviver and frame, because both Combat and the BallCaught observer below may report the same catch.
   * @returns {any|null} the revived player
   */
  reviveOneOutfield(team, cause = REVIVE_CAUSE.PERFECT_CATCH, reviver = null) {
    if (team !== TEAM.HOME && team !== TEAM.AWAY) return null;
    if (!this.isPlaying) return null;
    const dd = this._reviveDedupe;
    if (reviver) {
      if (dd.frame === game.time.frame && dd.reviver === reviver) return dd.revived;
      dd.frame = game.time.frame; dd.reviver = reviver; dd.revived = null;
    }

    // 1) the teammate standing in the outfield the longest
    const outfield = [];
    const falling = [];
    for (const r of this._recs) {
      if (r.team !== team || r.player === reviver || r.startsOutfield) continue;
      if (r.transferAt >= 0) falling.push({ r, since: r.eliminatedAt });
      else if (r.player.zone === ZONE.OUTFIELD) outfield.push({ r, since: r.outfieldSince });
    }
    let list = outfield;
    let i = pickLongestWaiting(outfield);
    // 2) nobody out there yet: the teammate eliminated the earliest gets a last-second save
    if (i < 0) { list = falling; i = pickLongestWaiting(falling); }
    if (i < 0) return null;

    const target = list[i].r.player;
    if (!this.reviveFromOutfield(target, cause, reviver)) return null;
    if (reviver) dd.revived = target;
    return target;
  }

  // ================================================================== system hooks

  fixedUpdate() {
    // Round end is checked right after the fixed step in which the last elimination happened (Match runs after
    // balls & physics), i.e. immediately in simulation terms, without re-entering the ball's collision code.
    if (this.isPlaying) this._checkRoundEnd();
  }

  update(dt, realDt) {
    this._updateSlowMo(realDt);
    this._processOutfieldTransfers();

    this._phaseTime += dt;
    const rules = this.rules;
    switch (this.phase) {
      case MATCH_PHASE.PRE_ROUND:
        if (this._phaseTime >= rules.preRound) this._beginCountdown();
        break;
      case MATCH_PHASE.COUNTDOWN: {
        const n = countdownNumber(this._phaseTime, rules.countdown);
        if (n > 0 && n !== this.countdownLeft) this._emitCountdown(n);
        this.countdownLeft = n;
        if (this._phaseTime >= rules.countdown) this._beginPlaying();
        break;
      }
      case MATCH_PHASE.PLAYING:
        this.timeLeft = Math.max(0, this.timeLeft - dt);
        if (this._checkRoundEnd()) break;
        if (this.timeLeft <= 0) { this._endRound(this._decide(true)); break; }
        this._updatePossession(dt);
        break;
      case MATCH_PHASE.ROUND_END:
        if (this._phaseTime >= rules.roundEnd) {
          if (this.winner === TEAM.HOME || this.winner === TEAM.AWAY) this._beginMatchEnd();
          else this._beginPreRound();
        }
        break;
      case MATCH_PHASE.MATCH_END:
        if (this._phaseTime >= rules.matchEndDelay) this._afterMatchEnd();
        break;
      default:
        break;
    }
  }

  dispose() {
    this.endMatch();
    for (const u of this._unsubs) u();
    this._unsubs = [];
    if (this._driverAdded) { game.removeSystem(this._driver); this._driverAdded = false; }
  }

  // ================================================================== round flow

  _beginPreRound() {
    // The previous round's result decides who serves (lastRoundWinner is cleared below).
    const prevWinner = this.round > 0 ? this.lastRoundWinner : NO_WINNER;
    this.round++;
    this.timeLeft = this.rules.roundTime;
    this.countdownLeft = 0;
    this.lastRoundWinner = NO_WINNER;
    this.lastRoundReason = null;
    this._restoreSlowMo();
    this._resetPossession();
    this._setPhase(MATCH_PHASE.PRE_ROUND);

    // Serve: round 1 by (seeded) coin, then the loser of the previous round, after a draw the other team.
    this.serveTeam = nextServeTeam(this.round, prevWinner, this._lastServeTeam, game.rng.next());
    this._lastServeTeam = this.serveTeam;

    this._resetPlayersForRound();
    const court = game.court;
    const servePos = court.servePoint(this.serveTeam);
    if (game.balls && game.balls.resetForRound) {
      try { game.balls.resetForRound([servePos]); } catch (e) { console.error('[match] balls.resetForRound failed', e); }
    }

    if (this.round === 1) {
      const r = this.rules;
      game.events.emit(EV.MatchStarted, {
        roundsToWin: r.roundsToWin, playersPerTeam: r.playersPerTeam, infieldPerTeam: r.playersPerTeam - r.startingOutfielders,
        startingOutfielders: r.startingOutfielders, ballCount: r.ballCount, possessionLimit: r.possessionLimit,
      });
    }
    this._serveBall(servePos);

    // Broadcast intro: the camera frames the centre line (both line-ups and the server).
    const rig = game.cameraRig;
    if (rig && rig.setCinematic) rig.setCinematic(new THREE.Vector3(0, court.floorY + MATCH_TUNING.cinematicFocusHeight, 0));
  }

  /** Puts the ball in the server's hand at the serve point (HELD through preRound and the countdown). */
  _serveBall(servePos) {
    const server = this._pickServer(this.serveTeam);
    this.server = server;
    const ball = game.balls && game.balls.ball;
    if (!server || !ball) return;
    try {
      server.teleport(servePos, game.court.spawnYaw(this.serveTeam));
      if (server.combat && server.combat.giveBall) server.combat.giveBall(ball);
    } catch (e) {
      console.error('[match] serve failed', e);
    }
    game.events.emit(EV.ServeReady, { round: this.round, team: this.serveTeam, player: server, ball });
  }

  /** The local human if on the serving team (always infield at the start), else infield starters in rotation. */
  _pickServer(team) {
    if (this.local && this.local.team === team && this.local.zone === ZONE.INFIELD) return this.local;
    const list = [];
    for (const r of this._recs) if (r.team === team && !r.startsOutfield) list.push(r);
    if (!list.length) return null;
    list.sort((a, b) => a.infieldIndex - b.infieldIndex);
    return list[(Math.max(1, this.round) - 1) % list.length].player;
  }

  _beginCountdown() {
    this._setPhase(MATCH_PHASE.COUNTDOWN);
    this._focusForPlay();
    const n = countdownNumber(0, this.rules.countdown);
    this.countdownLeft = n;
    if (n > 0) this._emitCountdown(n);
    else this._beginPlaying();
  }

  _emitCountdown(n) {
    this.countdownLeft = n;
    game.events.emit(EV.RoundCountdown, { round: this.round, secondsLeft: n });
  }

  _beginPlaying() {
    this.countdownLeft = 0;
    this.timeLeft = this.rules.roundTime;
    this._setPhase(MATCH_PHASE.PLAYING);
    // Unlock everyone except players still ragdolling toward the outfield.
    for (const r of this._recs) r.player.inputLocked = r.transferAt >= 0;
    // The serving team's clock starts at the whistle.
    this._resetPossession();
    const ball = game.balls && game.balls.ball;
    const holder = ball && ball.state === 'held' ? ball.holder : this.server;
    const team = holder && (holder.team === TEAM.HOME || holder.team === TEAM.AWAY) ? holder.team : this.serveTeam;
    this._clock.team = team;
    this._announcePossession(team, holder || null, 'serve');
    this._fillPossession(team, holder || null, true, false);
    game.events.emit(EV.RoundStarted, { round: this.round, duration: this.rules.roundTime, serveTeam: this.serveTeam, server: this.server });
  }

  // ================================================================== possession clock

  /** Clears the clock (round start / end). */
  _resetPossession() {
    releasePossession(this._clock);
    this._announcedTeam = TEAM.NONE;
    this._fillPossession(TEAM.NONE, null, false, false);
  }

  /** A throw / pass (or Houdini's teleport pass) released the ball: nobody owns the clock until the next possession. */
  _onBallReleased() {
    if (!this.isPlaying) return;
    releasePossession(this._clock);
    this._fillPossession(TEAM.NONE, null, false, false);
  }

  _fillPossession(team, holder, running, deadBall) {
    const o = this.possession, limit = this.rules.possessionLimit > 0 ? this.rules.possessionLimit : 0;
    o.team = team;
    o.holder = holder;
    o.elapsed = team === TEAM.NONE ? 0 : this._clock.elapsed;
    o.limit = limit;
    o.remaining = limit > 0 ? Math.max(0, limit - o.elapsed) : Infinity;
    o.running = !!running && team !== TEAM.NONE;
    o.deadBall = !!deadBall;
  }

  _announcePossession(team, holder, cause) {
    if (team === this._announcedTeam || (team !== TEAM.HOME && team !== TEAM.AWAY)) return;
    const previous = this._announcedTeam;
    this._announcedTeam = team;
    game.events.emit(EV.PossessionChanged, { team, previous, player: holder, cause });
  }

  /**
   * Owner of the single ball this frame and the clock step (ORDER.MATCH, scaled dt):
   *   held by a player -> that team (runs while the holder can act); stored by a team's gadget (custodyTeam) -> that
   *   team, paused; loose -> the team whose zone it rests in (dead-ball rule; runs); live / stasis / hidden -> nobody.
   */
  _updatePossession(dt) {
    const rules = this.rules;
    const ball = game.balls && game.balls.ball;
    if (!ball) { this._fillPossession(TEAM.NONE, null, false, false); return; }
    let owner = TEAM.NONE, holder = null, running = false, deadBall = false, cause = 'held';
    const st = ball.state;
    if (st === 'held' && ball.holder) {
      holder = ball.holder;
      owner = holder.team;
      running = holder.canAct !== false;
    } else if (st === 'despawned' && (ball.custodyTeam === TEAM.HOME || ball.custodyTeam === TEAM.AWAY)) {
      owner = ball.custodyTeam;
    } else if (st === 'free' && rules.deadBallClock && game.court && game.court.zoneAt) {
      const z = game.court.zoneAt(ball.position, this._zoneOut);
      if (z) { owner = z.team; running = true; deadBall = true; cause = 'loose'; }
    }
    if ((owner === TEAM.HOME || owner === TEAM.AWAY) && ball.isReservedFor && ball.isReservedFor(owner)) cause = 'award';
    const clock = this._clock;
    const r = stepPossession(clock, owner, running, dt, rules.possessionLimit, rules.possessionWarning);
    if (clock.changed) this._announcePossession(clock.team, holder, cause);
    const team = owner === TEAM.NONE ? TEAM.NONE : clock.team;
    this._fillPossession(team, holder, running, deadBall);
    if (r > 0) {
      game.events.emit(EV.PossessionWarning, { team: clock.team, player: holder, secondsLeft: r });
    } else if (r < 0) {
      this._possessionViolation(ball, clock.team, holder);
    }
  }

  /** The clock ran out: the ball goes to the opponents (hidden for rules.awardDelay, then at their feet). */
  _possessionViolation(ball, team, holder) {
    const heldFor = this._clock.elapsed;
    const awardedTo = opponent(team);
    try {
      if (holder && holder.combat && holder.combat.cancelCharge) holder.combat.cancelCharge();
      if (game.balls && game.balls.awardBall) game.balls.awardBall(ball, awardedTo, 'possession', ball.position, this.rules.awardDelay);
    } catch (e) {
      console.error('[match] possession award failed', e);
    }
    releasePossession(this._clock);
    this._fillPossession(TEAM.NONE, null, false, false);
    game.events.emit(EV.PossessionViolation, { team, player: holder, ball, heldFor, awardedTo });
  }

  /** Polls the head counts; ends the round when a team is wiped out. @returns {boolean} true if the round ended */
  _checkRoundEnd() {
    if (!this.isPlaying) return false;
    const result = this._decide(false);
    if (!result) return false;
    this._endRound(result);
    return true;
  }

  /** @returns {{winner:number, reason:string}|null} */
  _decide(timeUp) {
    const t = this._computeTally();
    return decideRound({
      homeInfield: t.infield[TEAM.HOME], awayInfield: t.infield[TEAM.AWAY],
      homeHp: t.hp[TEAM.HOME], awayHp: t.hp[TEAM.AWAY], timeUp, hpTolerance: this.rules.hpTieTolerance,
    });
  }

  /** Scores the round, freezes the players, plays the celebrations and announces it. */
  _endRound(result) {
    if (!result || !this.isPlaying) return;
    const applied = applyRoundResult(this.scores, result.winner, this.rules.roundsToWin);
    this.scores[0] = applied.scores[0];
    this.scores[1] = applied.scores[1];
    this.winner = applied.matchWinner;
    this.lastRoundWinner = result.winner;
    this.lastRoundReason = result.reason;

    this._setPhase(MATCH_PHASE.ROUND_END);
    this._resetPossession();
    this._freezePlayersForBreak();
    this._playCelebrations(result.winner);
    // Broadcast touch: the deciding elimination plays out in slow motion (real-time recovery, see _updateSlowMo).
    if (result.reason === ROUND_END_REASON.ELIMINATED) this._startSlowMo();

    game.events.emit(EV.RoundEnded, {
      round: this.round, winner: result.winner, reason: result.reason, scores: [this.scores[0], this.scores[1]],
    });
  }

  _beginMatchEnd() {
    this._restoreSlowMo();
    this._setPhase(MATCH_PHASE.MATCH_END);
    this._freezePlayersForBreak();
    this._playCelebrations(this.winner);
    this._focusOnTeam(this.winner);
    game.events.emit(EV.MatchEnded, { winner: this.winner, scores: [this.scores[0], this.scores[1]] });
  }

  _afterMatchEnd() {
    if (this.setup && this.setup.spectate && this.rules.loopSpectate) {
      // Attract mode: keep AI matches going with a fresh random line-up.
      this.startMatch(this._makeSpectateSetup()).catch((e) => console.error('[match] spectate restart failed', e));
      return;
    }
    this._setPhase(MATCH_PHASE.SELECT);
    this._restoreSlowMo();
    try {
      if (typeof document !== 'undefined' && document.pointerLockElement && document.exitPointerLock) document.exitPointerLock();
    } catch { /* pointer lock is optional */ }
    if (typeof game.showHeroSelect === 'function') game.showHeroSelect();
  }

  // ================================================================== infield / outfield

  _processOutfieldTransfers() {
    if (!this._recs.length) return;
    const now = game.time.now;
    const recs = this._recs;
    for (let i = 0; i < recs.length; i++) {
      const r = recs[i];
      const p = r.player;
      if (r.transferAt < 0) {
        // Safety net: an elimination we did not hear about, or one that was still pending (Chrono) when reported and
        // has committed since -> start the ragdoll timer now so nobody lies in the infield forever.
        const h = p.health;
        if (h && h.isEliminated && !h.pending && p.zone === ZONE.INFIELD && this.phase !== MATCH_PHASE.PRE_ROUND) {
          this._scheduleOutfield(r, p, now);
        }
        continue;
      }
      if (now < r.transferAt) continue;
      r.transferAt = -1;
      // Saved while ragdolling (e.g. an ability revived them without going through the match): stay infield.
      if (p.zone === ZONE.INFIELD && p.health && !p.health.isEliminated) { r.eliminatedAt = -1; continue; }
      this._moveToOutfield(r);
    }
  }

  /** @param {MatchRecord} r */
  _moveToOutfield(r) {
    const p = r.player;
    const court = game.court;
    r.transferAt = -1;
    const spot = this._freeOutfieldSpot(r);
    const pos = court.outfieldSpot(r.team, spot);
    // Outfield players face the centre of the enemy half they surround (arm spots face sideways).
    const yaw = court.outfieldYaw(r.team, pos);
    try {
      if (p.avatar && p.avatar.resetVisual) p.avatar.resetVisual(); // end the ragdoll, back to animation
      if (p.motor && p.motor.setConfinement) p.motor.setConfinement(court.confinement(r.team, ZONE.OUTFIELD));
      p.setZone(ZONE.OUTFIELD);
      p.teleport(pos, yaw);
      this._clearDebuffs(p);
      if (p.fsm) {
        if (p.fsm.release) p.fsm.release('eliminated');
        // Eliminated while frozen/grabbed: that other lock has no meaning in the outfield.
        if (p.fsm.is && p.fsm.is('incapacitated') && p.fsm.resetToGrounded) p.fsm.resetToGrounded();
      }
      if (p.history && p.history.clear) p.history.clear();
      p.inputLocked = !this.isPlaying;
    } catch (e) {
      console.error('[match] sendToOutfield failed', e);
    }
    const now = game.time.now;
    if (r.eliminatedAt < 0) r.eliminatedAt = now;
    r.outfieldSince = now;
    r.outfieldSpot = spot;
    if (this.isPlaying) this._checkRoundEnd();
  }

  /** Smallest outfield spot index not used by a teammate already out there. */
  _freeOutfieldSpot(target) {
    const used = [];
    for (const r of this._recs) {
      if (r !== target && r.team === target.team && r.outfieldSpot >= 0 && r.player.zone === ZONE.OUTFIELD) used.push(r.outfieldSpot);
    }
    return lowestFreeIndex(used);
  }

  /** Spawn-line slot farthest from every other infield player (small preference for the player's own slot). */
  _freeInfieldSpawn(target) {
    const court = game.court;
    const count = this._infieldSlots(target.team);
    const slots = [];
    for (let s = 0; s < count; s++) slots.push(court.spawnPoint(target.team, s, count));
    const occupied = [];
    for (const r of this._recs) {
      if (r !== target && r.player.zone === ZONE.INFIELD) occupied.push(r.player.position);
    }
    const own = target.infieldIndex >= 0 ? target.infieldIndex : target.slot;
    return slots[pickSpawnSlot(slots, occupied, own % count)];
  }

  /** Infield spawn-line size of `team`: its infield starters (3 by default). */
  _infieldSlots(team) {
    let n = 0;
    for (const r of this._recs) if (r.team === team && !r.startsOutfield) n++;
    return Math.max(1, n, this.rules.playersPerTeam - this.rules.startingOutfielders);
  }

  /** Infield spawn indices and starting-outfield indices per team, in slot order (after failed spawns are dropped). */
  _assignLineIndices() {
    for (const team of [TEAM.HOME, TEAM.AWAY]) {
      const list = this._recs.filter((r) => r.team === team).sort((a, b) => a.slot - b.slot);
      let inf = 0, out = 0;
      // Never leave a team without an infield player (e.g. everyone else failed to load).
      if (list.length && list.every((r) => r.startsOutfield)) { list[0].startsOutfield = false; list[0].player.isStartingOutfielder = false; }
      for (const r of list) {
        if (r.startsOutfield) { r.starterIndex = out++; r.infieldIndex = -1; } else { r.infieldIndex = inf++; r.starterIndex = -1; }
      }
    }
  }

  _clearDebuffs(p) {
    const status = p.status;
    if (!status || !status.remove) return;
    for (const type of ZONE_CHANGE_DEBUFFS) {
      try { if (!status.has || status.has(type)) status.remove(type); } catch (e) { console.error('[match] status.remove failed', e); }
    }
  }

  // ================================================================== event observers

  _onPlayerEliminated(e) {
    const p = e && e.player;
    const r = p && this._records.get(p);
    if (!r) return;
    if (p.zone === ZONE.OUTFIELD || r.transferAt >= 0) return; // already out / duplicate report
    if (p.health && p.health.pending) return; // delayed elimination (Chrono): still in until it commits
    this._scheduleOutfield(r, p, game.time.now);
    // The round-end check runs in fixedUpdate right after this fixed step (see fixedUpdate).
  }

  /** Starts the ragdoll timer that ends with the outfield move. */
  _scheduleOutfield(r, p, now) {
    r.eliminatedAt = now;
    r.transferAt = now + Math.max(MATCH_TUNING.minRagdollTime, this.rules.ragdollTime);
    p.inputLocked = true;
    // Health incapacitates on elimination; make sure the victim can never act while ragdolling.
    try {
      if (p.fsm && p.fsm.is && !p.fsm.is('incapacitated') && p.fsm.incapacitate) p.fsm.incapacitate('eliminated');
    } catch (err) { console.error('[match] incapacitate failed', err); }
  }

  _onPlayerRevived(e) {
    // Revivals performed by abilities (not through reviveFromOutfield) while the player was still ragdolling toward
    // the outfield: cancel the pending move so the player counts as infield again right away.
    const p = e && e.player;
    const r = p && this._records.get(p);
    if (!r || r.transferAt < 0) return;
    if (p.zone !== ZONE.INFIELD || !p.health || p.health.isEliminated) return;
    r.transferAt = -1;
    r.eliminatedAt = -1;
    p.inputLocked = !this.isPlaying;
  }

  _onBallHitPlayer(e) {
    if (!this.isPlaying || !e) return;
    const attacker = e.attacker;
    if (!attacker || !this._records.has(attacker)) return;
    const landed = e.outcome === 'damaged' || e.outcome === 'eliminated';
    if (!landed || !game.areEnemies(attacker, e.victim)) return;

    const gain = this.rules.ultOnHit + (e.outcome === 'eliminated' ? this.rules.ultOnEliminate : 0);
    if (gain > 0 && attacker.abilities && attacker.abilities.addUltimateCharge) attacker.abilities.addUltimateCharge(gain, 'hit');

    // Taiwanese outfield rule: an ELIMINATED outfield player who hits an infield enemy returns to the infield (the
    // starting outfielder stays out).
    const rec = this._records.get(attacker);
    if (this.rules.outfieldHitRevives && attacker.zone === ZONE.OUTFIELD && !(rec && rec.startsOutfield)) {
      this.reviveFromOutfield(attacker, REVIVE_CAUSE.OUTFIELD_HIT, null);
    }
  }

  _onBallCaught(e) {
    if (!this.isPlaying || !e) return;
    const catcher = e.catcher;
    if (!catcher || !this._records.has(catcher)) return;
    // Intercepting an enemy pass only takes possession: no revive, no ult, no catch-elimination.
    if (e.intercepted) return;

    if (e.quality === 'perfect') {
      // Combat calls reviveOneOutfield itself; the de-duplication makes this a safety net, never a double revive.
      this.reviveOneOutfield(catcher.team, REVIVE_CAUSE.PERFECT_CATCH, catcher);
    } else if (e.quality === 'normal' && this.rules.ultOnCatch > 0 && catcher.abilities && catcher.abilities.addUltimateCharge) {
      catcher.abilities.addUltimateCharge(this.rules.ultOnCatch, 'catch');
    }

    // Optional classic rule: a catch eliminates its (infield) thrower.
    const thrower = e.thrower;
    if (this.rules.catchEliminatesThrower && e.quality !== 'miss' && thrower && thrower.health && thrower.health.eliminate &&
        game.areEnemies(catcher, thrower) && thrower.isTargetable) {
      const away = new THREE.Vector3().subVectors(thrower.position, catcher.position).setY(0);
      if (away.lengthSq() < 1e-6) away.copy(thrower.forward).negate();
      away.normalize().multiplyScalar(MATCH_TUNING.caughtThrowerImpulse);
      const point = thrower.chestPosition ? thrower.chestPosition.clone() : thrower.position.clone();
      thrower.health.eliminate('caught', catcher, away, point);
    }
  }

  // ================================================================== players

  /** Builds one Player (not yet initialised). */
  _createPlayer(id, team, slot, heroId, isHuman) {
    const PlayerCls = PlayerModule.Player || PlayerModule.default;
    if (typeof PlayerCls !== 'function') { console.error('[match] gameplay/player.js exports no Player class'); return null; }
    let hero = heroById(heroId);
    if (!hero) {
      console.warn(`[match] unknown hero "${heroId}", using a random one`);
      hero = heroById(game.rng.pick(HERO_IDS));
    }
    let intentSource = null;
    try {
      intentSource = isHuman ? this._createHumanSource() : this._createBotSource(id);
    } catch (e) {
      console.error('[match] intent source creation failed', e);
    }
    if (!intentSource) intentSource = this._neutralSource();
    try {
      return new PlayerCls({ id, name: hero.id, team, hero, isHuman, isLocal: isHuman, intentSource, slot });
    } catch (e) {
      console.error(`[match] could not create player ${id} (${hero.id})`, e);
      return null;
    }
  }

  _createHumanSource() {
    const Cls = InputModule.HumanController || (InputModule.default && InputModule.default.HumanController);
    if (typeof Cls !== 'function') { console.error('[match] input/input.js exports no HumanController'); return null; }
    return new Cls();
  }

  _createBotSource(id) {
    const Cls = BotModule.Bot || BotModule.default;
    if (typeof Cls !== 'function') { console.error('[match] ai/bot.js exports no Bot class'); return null; }
    // Deterministic per ?seed, distinct per player and per match.
    const base = Number(game.config && game.config.seed) || 1;
    const seed = (Math.imul(base | 0, 0x9e3779b1) ^ Math.imul(id, 40503) ^ Math.imul(this._matchCount, 97531)) >>> 0;
    return new Cls(this.setup.difficulty, seed || 1);
  }

  /** Last-resort intent source (stands still) so a missing module never crashes the match. */
  _neutralSource() {
    const neutral = PlayerModule.neutralIntent;
    return { sample: (player) => (typeof neutral === 'function' ? neutral(player) : null) };
  }

  _resetPlayersForRound() {
    const court = game.court;
    const rules = this.rules;
    for (const r of this._recs) {
      const p = r.player;
      const wasDown = r.transferAt >= 0 || r.eliminatedAt >= 0;
      r.eliminatedAt = -1; r.transferAt = -1; r.outfieldSince = -1; r.outfieldSpot = -1;
      // Infield starters on the spawn line (infield index / count); starting outfielders on their U spot.
      const zone = r.startsOutfield ? ZONE.OUTFIELD : ZONE.INFIELD;
      let pos, yaw;
      if (r.startsOutfield) {
        r.outfieldSpot = Math.max(0, r.starterIndex);
        pos = court.outfieldSpot(r.team, r.outfieldSpot);
        yaw = court.outfieldYaw(r.team, pos);
      } else {
        pos = court.spawnPoint(r.team, Math.max(0, r.infieldIndex), this._infieldSlots(r.team));
        yaw = court.spawnYaw(r.team);
      }
      try {
        if (p.motor && p.motor.setConfinement) p.motor.setConfinement(court.confinement(r.team, zone));
        if (zone === ZONE.INFIELD && p.zone !== ZONE.INFIELD) p.setZone(ZONE.INFIELD);
        if (wasDown && p.avatar && p.avatar.resetVisual) p.avatar.resetVisual(); // still ragdolled from last round
        p.resetForRound(pos, yaw, zone);
        // Defensive: the round always starts with everybody alive and able to move.
        if (p.health && p.health.isEliminated && p.health.resetForRound) p.health.resetForRound();
        if (p.fsm && p.fsm.is && p.fsm.is('incapacitated') && p.fsm.resetToGrounded) p.fsm.resetToGrounded();
        if (p.history && p.history.clear) p.history.clear();
        if (p.abilities) {
          p.abilities.passiveUltPerSecond = rules.ultPerSecond;
          if (!rules.keepUltimateBetweenRounds && this.round > 1 && p.abilities.setUltimateCharge) p.abilities.setUltimateCharge(0);
        }
      } catch (e) {
        console.error(`[match] resetForRound failed for player ${p.id}`, e);
      }
      p.inputLocked = true;
    }
  }

  /** Locks input and cancels anything that could fire after the whistle (a neutral intent reads as "released"). */
  _freezePlayersForBreak() {
    for (const p of this.players) {
      p.inputLocked = true;
      try {
        const c = p.combat;
        if (c) {
          if (c.isCharging && c.cancelCharge) c.cancelCharge();
          if (c.catchArmed && c.cancelCatch) c.cancelCatch();
        }
        if (p.abilities && p.abilities.interruptAll) p.abilities.interruptAll(INTERRUPT.ROUND_ENDED);
      } catch (e) {
        console.error(`[match] freeze failed for player ${p.id}`, e);
      }
    }
  }

  /** Winners cheer, losers hang their heads (ragdolling players are skipped). */
  _playCelebrations(winner) {
    if (winner !== TEAM.HOME && winner !== TEAM.AWAY) return;
    for (const r of this._recs) {
      if (r.transferAt >= 0) continue;
      const a = r.player.avatar;
      if (!a) continue;
      try {
        if (r.team === winner) { if (a.cheer) a.cheer(); } else if (a.defeat) a.defeat();
      } catch (e) {
        console.error('[match] celebration failed', e);
      }
    }
  }

  /** Calls `method` on every player with per-player error isolation (one broken player must not freeze the rest). */
  _forEachPlayer(method, a, b) {
    const list = this.players;
    for (let i = 0; i < list.length; i++) {
      if (list !== this.players) return; // torn down mid-loop (new match started)
      const p = list[i];
      if (!p || typeof p[method] !== 'function') continue;
      try {
        p[method](a, b);
      } catch (e) {
        let reported = this._errReported.get(p);
        if (!reported) { reported = new Set(); this._errReported.set(p, reported); }
        if (!reported.has(method)) {
          reported.add(method);
          console.error(`[match] player ${p.id} (${p.hero && p.hero.id}).${method} threw`, e);
        }
      }
    }
  }

  _disposePlayer(p) {
    if (!p) return;
    try {
      if (p.combat && p.combat.hasBall && p.combat.dropBall) p.combat.dropBall(new THREE.Vector3());
    } catch (e) { console.error('[match] dropBall on teardown failed', e); }
    try { if (p.dispose) p.dispose(); } catch (e) { console.error('[match] player.dispose failed', e); }
    if (typeof game.unregisterPlayer === 'function') game.unregisterPlayer(p);
  }

  _teardown() {
    const old = this.players;
    this.players = [];
    for (const p of old) this._disposePlayer(p);
    this._records.clear();
    this._recs.length = 0;
    this._snaps.length = 0;
    this.local = null;
    this._reviveDedupe.frame = -1; this._reviveDedupe.reviver = null; this._reviveDedupe.revived = null;
    this._restoreSlowMo();
  }

  /** Fills the reused snapshots and returns the per-team tally (no allocations after the first call). */
  _computeTally() {
    const snaps = this._snaps;
    const recs = this._recs;
    let i = 0;
    for (; i < recs.length; i++) {
      const r = recs[i];
      let s = snaps[i];
      if (!s) { s = snaps[i] = { team: 0, zone: ZONE.INFIELD, awaitingOutfield: false, eliminated: false, pending: false, hp: 0 }; }
      const p = r.player, h = p.health;
      s.team = r.team;
      s.zone = p.zone;
      s.awaitingOutfield = r.transferAt >= 0;
      s.eliminated = !!(h && h.isEliminated);
      s.pending = !!(h && h.pending);
      s.hp = h ? h.hp : 0;
    }
    snaps.length = i;
    return tallyTeams(snaps, this._tally);
  }

  // ================================================================== camera / time helpers

  _focusForPlay() {
    const rig = game.cameraRig;
    if (!rig) return;
    if (rig.setCinematic) rig.setCinematic(null);
    // Local player: over-the-shoulder; spectate: null target -> the rig's broadcast orbit.
    if (rig.setTarget) rig.setTarget(this.local, true);
  }

  _focusOnTeam(team) {
    const rig = game.cameraRig;
    const court = game.court;
    if (!rig || !rig.setCinematic || !court) return;
    const focus = new THREE.Vector3();
    let n = 0;
    if (team === TEAM.HOME || team === TEAM.AWAY) {
      for (const p of this.players) if (p.team === team && p.zone === ZONE.INFIELD) { focus.add(p.position); n++; }
    }
    if (n > 0) focus.multiplyScalar(1 / n);
    focus.y = court.floorY + MATCH_TUNING.cinematicFocusHeight;
    rig.setCinematic(focus);
  }

  _startSlowMo() {
    this._slowMoLeft = MATCH_TUNING.deciderSlowMoDuration;
    game.time.slowMo = MATCH_TUNING.deciderSlowMoScale;
  }

  /** Real-time recovery of the decider slow motion (eases back to 1 over the last part). */
  _updateSlowMo(realDt) {
    if (this._slowMoLeft <= 0) return;
    this._slowMoLeft -= realDt;
    if (this._slowMoLeft <= 0) { this._restoreSlowMo(); return; }
    const T = MATCH_TUNING;
    const k = this._slowMoLeft / (T.deciderSlowMoDuration * T.deciderSlowMoRecover); // 1 -> 0 during recovery
    const t = Math.min(1, k);
    const ease = t * t * (3 - 2 * t);
    game.time.slowMo = 1 + (T.deciderSlowMoScale - 1) * ease;
  }

  _restoreSlowMo() {
    if (this._slowMoLeft > 0 || game.time.slowMo !== 1) game.time.slowMo = 1;
    this._slowMoLeft = 0;
  }

  // ================================================================== setup / rules

  _setPhase(next) {
    this._phaseTime = 0;
    if (this.phase === next) return;
    const previous = this.phase;
    this.phase = next;
    game.events.emit(EV.MatchPhase, { previous, current: next });
  }

  _ensureDriver() {
    if (this._driverAdded) return;
    game.addSystem(this._driver, ORDER.PLAYERS);
    this._driverAdded = true;
  }

  /** Defaults, then URL overrides (?roundTime=60&rounds=1&ragdollTime=1.5), then setup.rules. */
  _resolveRules(overrides) {
    const rules = { ...Match.defaults };
    const params = game.config && game.config.params;
    if (params && typeof params.get === 'function') {
      const num = (key, min, max) => {
        const raw = params.get(key);
        if (raw === null || raw === '') return undefined;
        const v = Number(raw);
        return Number.isFinite(v) ? Math.min(max, Math.max(min, v)) : undefined;
      };
      const roundTime = num('roundTime', 10, 900);
      if (roundTime !== undefined) rules.roundTime = roundTime;
      const rounds = num('rounds', 1, 5);
      if (rounds !== undefined) rules.roundsToWin = Math.round(rounds);
      const ragdoll = num('ragdollTime', 0.2, 6);
      if (ragdoll !== undefined) rules.ragdollTime = ragdoll;
      // ?possession=N: 0 disables the anti-stall clock, otherwise 3..30 s.
      const possession = num('possession', 0, 30);
      if (possession !== undefined) rules.possessionLimit = possession <= 0 ? 0 : Math.max(3, possession);
    }
    if (overrides && typeof overrides === 'object') Object.assign(rules, overrides);
    rules.playersPerTeam = Math.max(1, Math.round(rules.playersPerTeam) || 4);
    rules.startingOutfielders = Math.min(rules.playersPerTeam - 1, Math.max(0, Math.round(rules.startingOutfielders) || 0));
    if (rules.ballCount !== 1) {
      if (rules.ballCount > 1) console.warn(`[match] single-ball rules: ballCount ${rules.ballCount} ignored, using 1`);
      rules.ballCount = 1;
    }
    rules.possessionLimit = Number.isFinite(rules.possessionLimit) && rules.possessionLimit > 0 ? rules.possessionLimit : 0;
    return rules;
  }

  /** Validates the hero lists (fills gaps with unused heroes) and derives spectate mode. */
  _normalizeSetup(setup) {
    const n = this.rules.playersPerTeam;
    const localTeam = setup.localTeam === TEAM.AWAY ? TEAM.AWAY : TEAM.HOME;
    const spectate = !!setup.spectate || !setup.localHero || !heroById(setup.localHero);
    const home = (Array.isArray(setup.homeHeroes) ? setup.homeHeroes : []).filter((h) => heroById(h)).slice(0, n);
    const away = (Array.isArray(setup.awayHeroes) ? setup.awayHeroes : []).filter((h) => heroById(h)).slice(0, n);
    if (!spectate) {
      const mine = localTeam === TEAM.HOME ? home : away;
      if (!mine.includes(setup.localHero)) { mine.unshift(setup.localHero); if (mine.length > n) mine.length = n; }
    }
    const used = new Set([...home, ...away]);
    const pool = game.rng.shuffle(HERO_IDS.filter((h) => !used.has(h)));
    for (const list of [home, away]) {
      while (list.length < n) list.push(pool.length ? pool.pop() : game.rng.pick(HERO_IDS));
    }
    return {
      localHero: spectate ? null : setup.localHero, localTeam, homeHeroes: home, awayHeroes: away,
      difficulty: setup.difficulty || 'normal', spectate,
    };
  }

  /** Fresh random 4v4 line-up (8 distinct heroes) for attract mode (same difficulty). */
  _makeSpectateSetup() {
    const n = this.rules.playersPerTeam;
    const pool = game.rng.shuffle(HERO_IDS.slice());
    return {
      localHero: null, localTeam: TEAM.HOME, homeHeroes: pool.slice(0, n), awayHeroes: pool.slice(n, 2 * n),
      difficulty: (this.setup && this.setup.difficulty) || 'normal', spectate: true,
    };
  }
}
