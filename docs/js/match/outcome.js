// ---------------------------------------------------------------------------------------------------------------
// Pure match-rules helpers (no three.js, no game context) - unit tested by match/rules.test.js.
// Match (match.js) snapshots its players into plain objects and asks these functions who is still "in", who won the
// round, how the score changes, who gets revived first and where a returning player should stand.
// ---------------------------------------------------------------------------------------------------------------
import { TEAM, opponent } from '../core/constants.js';

/** RoundEnded `reason` values (see core/events.js). */
export const ROUND_END_REASON = Object.freeze({ ELIMINATED: 'eliminated', TIME: 'time', DRAW: 'draw' });

/** Winner value for "nobody" (draw / match still running). */
export const NO_WINNER = TEAM.NONE;

/** Default HP difference below which a time-up round with equal head counts is a draw (HP are floats). */
export const HP_TIE_TOLERANCE = 0.5;

/**
 * Does a player still count as standing in the infield for round-end purposes?
 * Players ragdolling toward the outfield do NOT count; players whose elimination is pending (Chrono's Delayed Impact)
 * DO count until it resolves.
 * @param {{zone:string, awaitingOutfield?:boolean, eliminated?:boolean, pending?:boolean}} s
 */
export function countsAsInfield(s) {
  if (!s || s.zone !== 'infield' || s.awaitingOutfield) return false;
  return !s.eliminated || !!s.pending;
}

/**
 * Head count and total HP of the players still in, per team.
 * @param {Array<{team:number, zone:string, awaitingOutfield?:boolean, eliminated?:boolean, pending?:boolean, hp?:number}>} snapshots
 * @returns {{infield:[number,number], hp:[number,number]}}
 */
export function tallyTeams(snapshots, out = { infield: [0, 0], hp: [0, 0] }) {
  out.infield[0] = out.infield[1] = 0;
  out.hp[0] = out.hp[1] = 0;
  for (const s of snapshots) {
    if (s.team !== TEAM.HOME && s.team !== TEAM.AWAY) continue;
    if (!countsAsInfield(s)) continue;
    out.infield[s.team]++;
    out.hp[s.team] += Math.max(0, Number(s.hp) || 0);
  }
  return out;
}

/** True when at least one team has nobody left in the infield. */
export function isWipedOut(homeInfield, awayInfield) {
  return homeInfield <= 0 || awayInfield <= 0;
}

/**
 * Decides a round.
 *  - Not time-up: a team with nobody in the infield loses; both wiped in the same frame = draw; otherwise `null`
 *    (the round goes on).
 *  - Time-up: more infield players wins; equal -> more total infield HP wins; still equal -> draw (no point, replay).
 * @param {{homeInfield:number, awayInfield:number, homeHp?:number, awayHp?:number, timeUp:boolean, hpTolerance?:number}} a
 * @returns {{winner:number, reason:string}|null}
 */
export function decideRound({ homeInfield, awayInfield, homeHp = 0, awayHp = 0, timeUp, hpTolerance = HP_TIE_TOLERANCE }) {
  if (!timeUp) {
    const homeOut = homeInfield <= 0, awayOut = awayInfield <= 0;
    if (homeOut && awayOut) return { winner: NO_WINNER, reason: ROUND_END_REASON.DRAW };
    if (homeOut) return { winner: TEAM.AWAY, reason: ROUND_END_REASON.ELIMINATED };
    if (awayOut) return { winner: TEAM.HOME, reason: ROUND_END_REASON.ELIMINATED };
    return null;
  }
  if (homeInfield !== awayInfield) {
    return { winner: homeInfield > awayInfield ? TEAM.HOME : TEAM.AWAY, reason: ROUND_END_REASON.TIME };
  }
  const diff = homeHp - awayHp;
  if (Math.abs(diff) <= Math.max(0, hpTolerance)) return { winner: NO_WINNER, reason: ROUND_END_REASON.DRAW };
  return { winner: diff > 0 ? TEAM.HOME : TEAM.AWAY, reason: ROUND_END_REASON.TIME };
}

/**
 * Applies a round result to the score (a draw scores nothing).
 * @param {[number,number]} scores current [home, away] (not mutated)
 * @returns {{scores:[number,number], matchWinner:number}} matchWinner = TEAM or NO_WINNER while the match goes on
 */
export function applyRoundResult(scores, winner, roundsToWin) {
  const next = [scores[0] | 0, scores[1] | 0];
  if (winner === TEAM.HOME || winner === TEAM.AWAY) next[winner]++;
  const need = Math.max(1, roundsToWin | 0);
  let matchWinner = NO_WINNER;
  if (next[TEAM.HOME] >= need) matchWinner = TEAM.HOME;
  else if (next[TEAM.AWAY] >= need) matchWinner = TEAM.AWAY;
  return { scores: next, matchWinner };
}

/**
 * Index of the candidate that has waited the longest: smallest `since` (>= 0). Candidates with a negative `since`
 * (unknown) rank last; ties keep the first. Returns -1 for an empty list.
 * @param {Array<{since:number}>} candidates
 */
export function pickLongestWaiting(candidates) {
  let best = -1, bestSince = Infinity;
  for (let i = 0; i < candidates.length; i++) {
    const c = candidates[i];
    if (!c) continue;
    const since = c.since >= 0 ? c.since : Number.MAX_VALUE;
    if (best < 0 || since < bestSince) { best = i; bestSince = since; }
  }
  return best;
}

/** Smallest non-negative integer not in `used` (outfield spot allocation). */
export function lowestFreeIndex(used, max = 32) {
  for (let i = 0; i < max; i++) if (!used.includes(i)) return i;
  return 0;
}

/**
 * Chooses the infield spawn slot for a returning player: the slot farthest from every other infield player, with a
 * small bonus for the player's own slot so line-ups stay familiar.
 * @param {Array<{x:number,z:number}>} slots candidate positions
 * @param {Array<{x:number,z:number}>} occupied positions of other infield players
 * @param {number} preferred index of the player's own slot
 * @param {number} [preferBonus=0.5] metres of extra "distance" granted to the preferred slot
 * @returns {number} slot index (0 when `slots` is empty)
 */
export function pickSpawnSlot(slots, occupied, preferred = 0, preferBonus = 0.5) {
  let best = 0, bestScore = -Infinity;
  for (let i = 0; i < slots.length; i++) {
    const c = slots[i];
    let nearest = 1e6;
    for (const o of occupied) {
      const dx = o.x - c.x, dz = o.z - c.z;
      nearest = Math.min(nearest, Math.sqrt(dx * dx + dz * dz));
    }
    const score = nearest + (i === preferred ? preferBonus : 0);
    if (score > bestScore) { bestScore = score; best = i; }
  }
  return best;
}

/**
 * Countdown number to display after `elapsed` seconds of a `total`-second countdown (3, 2, 1 ...), 0 when done.
 */
export function countdownNumber(elapsed, total) {
  const left = total - elapsed;
  if (left <= 0) return 0;
  return Math.min(Math.ceil(total - 1e-9), Math.ceil(left - 1e-9));
}

// =============================================================================================================
// Single-ball rules: serve order, starting outfielders, possession clock, award receiver
// =============================================================================================================

/**
 * Team that serves round `round` (1-based): round 1 by coin (coin < 0.5 -> HOME); after a won round the LOSER
 * serves; after a draw / replay the team that did not serve last time.
 * @param {number} round
 * @param {number} lastRoundWinner TEAM or NO_WINNER
 * @param {number} lastServeTeam TEAM of the previous serve (TEAM.NONE before the first)
 * @param {number} coin uniform [0, 1) (seeded rng)
 */
export function nextServeTeam(round, lastRoundWinner, lastServeTeam, coin) {
  if (round <= 1 || (lastServeTeam !== TEAM.HOME && lastServeTeam !== TEAM.AWAY &&
      lastRoundWinner !== TEAM.HOME && lastRoundWinner !== TEAM.AWAY)) {
    return coin < 0.5 ? TEAM.HOME : TEAM.AWAY;
  }
  if (lastRoundWinner === TEAM.HOME || lastRoundWinner === TEAM.AWAY) return opponent(lastRoundWinner);
  return opponent(lastServeTeam);
}

/**
 * Slots that start the round in the outfield (元外野): the highest `count` slots that are not the local human
 * (the human always starts in the infield). Returned highest first.
 * @param {Array<{slot:number, isLocal?:boolean}>} lineup
 * @param {number} count
 * @returns {number[]}
 */
export function pickStartingOutfielders(lineup, count) {
  const n = Math.max(0, count | 0);
  if (!n || !Array.isArray(lineup)) return [];
  const slots = [];
  for (const e of lineup) if (e && !e.isLocal) slots.push(e.slot | 0);
  slots.sort((a, b) => b - a);
  return slots.slice(0, Math.min(n, Math.max(0, lineup.length - 1)));
}

/** A fresh possession clock ({ team, elapsed, lastWarn, changed }). */
export function makePossessionClock() {
  return { team: TEAM.NONE, elapsed: 0, lastWarn: 99, changed: false };
}

/**
 * Advances the anti-stall possession clock by one frame.
 *  - ownerTeam set and different from clock.team: the clock switches to it (elapsed 0, warnings re-armed) and
 *    clock.changed is set for this call (the caller emits PossessionChanged).
 *  - ownerTeam NONE: nobody owns the ball this frame (in flight, hidden): the clock pauses.
 *  - running and owners match: elapsed += dt. Returns the warning number (<= warnAt) once per newly crossed second,
 *    -1 once the limit is reached, else 0. limit <= 0 disables the clock (always 0).
 * @param {{team:number, elapsed:number, lastWarn:number, changed:boolean}} clock
 * @returns {number} 0 | warnAt..1 | -1
 */
export function stepPossession(clock, ownerTeam, running, dt, limit, warnAt = 3) {
  clock.changed = false;
  const owned = ownerTeam === TEAM.HOME || ownerTeam === TEAM.AWAY;
  if (owned && ownerTeam !== clock.team) {
    clock.team = ownerTeam;
    clock.elapsed = 0;
    clock.lastWarn = 99;
    clock.changed = true;
  }
  if (!owned || !running || !(limit > 0)) return 0;
  clock.elapsed += Math.max(0, dt || 0);
  const remaining = limit - clock.elapsed;
  if (remaining <= 0) return -1;
  const n = Math.ceil(remaining - 1e-9);
  if (n <= warnAt && n < clock.lastWarn) { clock.lastWarn = n; return n; }
  return 0;
}

/** A throw / pass released the ball: nobody owns the clock until the next possession. */
export function releasePossession(clock) {
  clock.team = TEAM.NONE;
  clock.elapsed = 0;
  clock.lastWarn = 99;
  clock.changed = false;
  return clock;
}

/**
 * Who receives an awarded ball: the eligible INFIELD candidate nearest (x, z), else the nearest eligible outfielder,
 * else -1.
 * @param {Array<{x:number, z:number, infield:boolean, eligible:boolean}>} cands
 * @returns {number} index into cands or -1
 */
export function pickAwardReceiver(cands, x, z) {
  let bestIn = -1, dIn = Infinity, bestOut = -1, dOut = Infinity;
  for (let i = 0; i < cands.length; i++) {
    const c = cands[i];
    if (!c || !c.eligible) continue;
    const dx = c.x - x, dz = c.z - z, d = dx * dx + dz * dz;
    if (c.infield) { if (d < dIn) { dIn = d; bestIn = i; } } else if (d < dOut) { dOut = d; bestOut = i; }
  }
  return bestIn >= 0 ? bestIn : bestOut;
}
