// node --test docs/js/match/   (pure round-outcome logic; must not import three)
import test from 'node:test';
import assert from 'node:assert/strict';
import {
  countsAsInfield, tallyTeams, isWipedOut, decideRound, applyRoundResult, pickLongestWaiting, lowestFreeIndex,
  pickSpawnSlot, countdownNumber, ROUND_END_REASON, NO_WINNER,
} from './outcome.js';
import { TEAM } from '../core/constants.js';

const P = (team, zone = 'infield', extra = {}) => ({ team, zone, eliminated: false, pending: false, awaitingOutfield: false, hp: 100, ...extra });

test('infield membership: ragdolling players are out, pending (delayed) eliminations are still in', () => {
  assert.equal(countsAsInfield(P(TEAM.HOME)), true);
  assert.equal(countsAsInfield(P(TEAM.HOME, 'outfield')), false);
  assert.equal(countsAsInfield(P(TEAM.HOME, 'infield', { eliminated: true })), false);
  assert.equal(countsAsInfield(P(TEAM.HOME, 'infield', { awaitingOutfield: true })), false);
  assert.equal(countsAsInfield(P(TEAM.HOME, 'infield', { eliminated: true, pending: true })), true);
  assert.equal(countsAsInfield(null), false);
});

test('tally counts heads and HP per team', () => {
  const t = tallyTeams([
    P(TEAM.HOME, 'infield', { hp: 60 }), P(TEAM.HOME, 'outfield'), P(TEAM.HOME, 'infield', { hp: 200 }),
    P(TEAM.AWAY, 'infield', { hp: 100 }), P(TEAM.AWAY, 'infield', { eliminated: true, hp: 0 }), P(TEAM.NONE),
  ]);
  assert.deepEqual(t.infield, [2, 1]);
  assert.deepEqual(t.hp, [260, 100]);
  assert.equal(isWipedOut(t.infield[0], t.infield[1]), false);
  assert.equal(isWipedOut(0, 2), true);
});

test('elimination round end: wiped team loses, double knock-out is a draw, otherwise play on', () => {
  assert.equal(decideRound({ homeInfield: 2, awayInfield: 1, timeUp: false }), null);
  assert.deepEqual(decideRound({ homeInfield: 0, awayInfield: 1, timeUp: false }), { winner: TEAM.AWAY, reason: ROUND_END_REASON.ELIMINATED });
  assert.deepEqual(decideRound({ homeInfield: 3, awayInfield: 0, timeUp: false }), { winner: TEAM.HOME, reason: ROUND_END_REASON.ELIMINATED });
  assert.deepEqual(decideRound({ homeInfield: 0, awayInfield: 0, timeUp: false }), { winner: NO_WINNER, reason: ROUND_END_REASON.DRAW });
});

test('time-up: more infield players wins, then more total HP, else draw', () => {
  assert.deepEqual(decideRound({ homeInfield: 2, awayInfield: 1, homeHp: 10, awayHp: 200, timeUp: true }), { winner: TEAM.HOME, reason: ROUND_END_REASON.TIME });
  assert.deepEqual(decideRound({ homeInfield: 1, awayInfield: 3, timeUp: true }), { winner: TEAM.AWAY, reason: ROUND_END_REASON.TIME });
  assert.deepEqual(decideRound({ homeInfield: 2, awayInfield: 2, homeHp: 150, awayHp: 200, timeUp: true }), { winner: TEAM.AWAY, reason: ROUND_END_REASON.TIME });
  assert.deepEqual(decideRound({ homeInfield: 2, awayInfield: 2, homeHp: 200, awayHp: 199.8, timeUp: true }), { winner: NO_WINNER, reason: ROUND_END_REASON.DRAW });
  assert.deepEqual(decideRound({ homeInfield: 2, awayInfield: 2, homeHp: 200, awayHp: 200, timeUp: true, hpTolerance: 0 }), { winner: NO_WINNER, reason: ROUND_END_REASON.DRAW });
});

test('best of 3: two round wins take the match, draws score nothing', () => {
  let r = applyRoundResult([0, 0], TEAM.HOME, 2);
  assert.deepEqual(r, { scores: [1, 0], matchWinner: NO_WINNER });
  r = applyRoundResult(r.scores, NO_WINNER, 2);
  assert.deepEqual(r, { scores: [1, 0], matchWinner: NO_WINNER });
  r = applyRoundResult(r.scores, TEAM.AWAY, 2);
  assert.deepEqual(r, { scores: [1, 1], matchWinner: NO_WINNER });
  r = applyRoundResult(r.scores, TEAM.AWAY, 2);
  assert.deepEqual(r, { scores: [1, 2], matchWinner: TEAM.AWAY });
  const src = [0, 0];
  applyRoundResult(src, TEAM.HOME, 2);
  assert.deepEqual(src, [0, 0], 'input scores are not mutated');
});

test('perfect-catch revive picks the teammate who has waited the longest', () => {
  assert.equal(pickLongestWaiting([]), -1);
  assert.equal(pickLongestWaiting([{ since: 12 }, { since: 4 }, { since: 9 }]), 1);
  assert.equal(pickLongestWaiting([{ since: -1 }, { since: 30 }]), 1);
  assert.equal(pickLongestWaiting([{ since: 5 }, { since: 5 }]), 0);
});

test('outfield spots and infield return slots', () => {
  assert.equal(lowestFreeIndex([]), 0);
  assert.equal(lowestFreeIndex([0, 2]), 1);
  assert.equal(lowestFreeIndex([1, 0, 2]), 3);
  const slots = [{ x: -3, z: -6 }, { x: 0, z: -6.8 }, { x: 3, z: -6 }];
  // Someone stands on the left slot: the right one is farthest away.
  assert.equal(pickSpawnSlot(slots, [{ x: -3, z: -6 }], 1), 2);
  // Empty half: the preferred slot wins the tie.
  assert.equal(pickSpawnSlot(slots, [], 1), 1);
});

test('countdown shows 3, 2, 1 then 0', () => {
  assert.equal(countdownNumber(0, 3), 3);
  assert.equal(countdownNumber(0.4, 3), 3);
  assert.equal(countdownNumber(1.0, 3), 2);
  assert.equal(countdownNumber(2.5, 3), 1);
  assert.equal(countdownNumber(3.0, 3), 0);
});
