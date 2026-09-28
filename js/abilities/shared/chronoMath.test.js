// Unit tests for Chrono's pure time-manipulation math (node:test, no three.js).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  BALL_REWIND, insideZone, playAreaBounds, resolveZoneCenter, classifyBallRewind, snapshotPredatesLaunch,
  stasisScore, urgency, heartbeatInterval,
} from './chronoMath.js';

test('insideZone: 5x5 m square, 1 m below to 6 m above the centre', () => {
  const c = { x: 0, y: 0, z: -4 };
  assert.equal(insideZone({ x: 2.4, y: 1, z: -6.4 }, c, 5, 6), true);
  assert.equal(insideZone({ x: 2.6, y: 1, z: -4 }, c, 5, 6), false);
  assert.equal(insideZone({ x: 0, y: 6.5, z: -4 }, c, 5, 6), false);
  assert.equal(insideZone({ x: 0, y: -0.5, z: -4 }, c, 5, 6), true);
});

test('playAreaBounds: both halves + outfield strips of an 18x9 court', () => {
  const b = playAreaBounds({ width: 9, length: 18, outfieldDepth: 3 });
  assert.deepEqual(b, { minX: -6, maxX: 6, minZ: -12, maxZ: 12 });
});

test('playAreaBounds: U outfields use the court side-band width (2.5 m -> +/-7 x +/-12)', () => {
  const b = playAreaBounds({ width: 9, length: 18, outfieldDepth: 3, sideOutfieldWidth: 2.5 });
  assert.deepEqual(b, { minX: -7, maxX: 7, minZ: -12, maxZ: 12 });
});

test('resolveZoneCenter: limits range, clamps to the area, falls back to forward', () => {
  const area = { minX: -6, maxX: 6, minZ: -12, maxZ: 12 };
  const a = resolveZoneCenter(0, -6, 0, 40, 0, 1, 25, area);
  assert.equal(a.x, 0);
  assert.equal(a.z, 12); // 25 m cap -> z=19, clamped to the far outfield edge
  const b = resolveZoneCenter(0, -6, 3, 0, 0, 1, 25, area);
  assert.deepEqual([b.x, b.z], [3, 0]);
  const c = resolveZoneCenter(1, -6, NaN, NaN, 0, 1, 25, area, 8);
  assert.deepEqual([c.x, c.z], [1, 2]);
  const d = resolveZoneCenter(0, 0, 0, 0, 1, 0, 25, area, 8); // aim point on the caster
  assert.deepEqual([d.x, d.z], [6, 0]);
});

test('classifyBallRewind: decision table', () => {
  const now = 100, secondsAgo = 3;
  assert.equal(classifyBallRewind({ state: 'held', now, secondsAgo }), BALL_REWIND.SKIP);
  assert.equal(classifyBallRewind({ state: 'despawned', now, secondsAgo }), BALL_REWIND.SKIP);
  assert.equal(classifyBallRewind({ state: 'free', now, secondsAgo }), BALL_REWIND.TELEPORT);
  assert.equal(classifyBallRewind({ state: 'stasis', now, secondsAgo }), BALL_REWIND.TELEPORT);
  assert.equal(classifyBallRewind({ state: 'live', launchTime: 96, now, secondsAgo }), BALL_REWIND.REWIND_FLIGHT);
  assert.equal(classifyBallRewind({ state: 'live', launchTime: 99, now, secondsAgo }), BALL_REWIND.UNDO_THROW);
  assert.equal(classifyBallRewind({ state: 'live', launchTime: 99, isAbilityBall: true, now, secondsAgo }), BALL_REWIND.ERASE);
  // No launch time known: treat the flight as old (rewind along it rather than erase anything).
  assert.equal(classifyBallRewind({ state: 'live', launchTime: undefined, now, secondsAgo }), BALL_REWIND.REWIND_FLIGHT);
});

test('snapshotPredatesLaunch', () => {
  assert.equal(snapshotPredatesLaunch(97, 99), true);
  assert.equal(snapshotPredatesLaunch(99, 99), false);
  assert.equal(snapshotPredatesLaunch(NaN, 99), false);
});

test('stasisScore: threats are always eligible and preferred', () => {
  assert.equal(stasisScore(70, 5, false, 60, 0.6, 30), Infinity);
  assert.ok(Number.isFinite(stasisScore(70, 5, true, 60, 0.6, 30)));
  assert.ok(stasisScore(10, 5, true, 60, 0.6, 30) < stasisScore(10, 5, false, 60, 0.6, 30));
  assert.ok(stasisScore(10, 4, false, 60, 0.6, 30) < stasisScore(10, 12, false, 60, 0.6, 30));
});

test('urgency and heartbeat curves', () => {
  assert.equal(urgency(0, 1.5), 1);
  assert.equal(urgency(1.5, 1.5), 0);
  assert.equal(urgency(Infinity, 1.5), 0);
  assert.equal(heartbeatInterval(0, 0.5, 0.18), 0.5);
  assert.equal(heartbeatInterval(1, 0.5, 0.18), 0.18);
  assert.equal(heartbeatInterval(2, 0.5, 0.18), 0.18);
});
