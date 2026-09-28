// ---------------------------------------------------------------------------------------------------------------
// Pure match-rules helpers (no three.js, no game context) - unit tested by match/rules.test.js.
// Match (match.js) snapshots its players into plain objects and asks these functions who is still "in", who won the
// round, how the score changes, who gets revived first and where a returning player should stand.
// ---------------------------------------------------------------------------------------------------------------
import { TEAM } from '../core/constants.js';

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
