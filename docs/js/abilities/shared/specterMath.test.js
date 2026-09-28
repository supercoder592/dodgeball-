// Unit tests for Specter's pure evasion math (node:test, no three.js).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { closestApproach, roomAlong, evadeDirection, dodgeUtility, reversalUtility } from './specterMath.js';

const v = (x, y, z) => ({ x, y, z });

test('closestApproach: ball flying past a point', () => {
  const out = { time: 0, distance: 0 };
  // Ball at z=-10 moving +Z at 20 m/s, point 1 m to the side of the line.
  assert.equal(closestApproach(v(0, 1, -10), v(0, 0, 20), v(1, 1, 0), out), true);
  assert.ok(Math.abs(out.time - 0.5) < 1e-9);
  assert.ok(Math.abs(out.distance - 1) < 1e-9);
});

test('closestApproach: receding or static balls are not approaching', () => {
  const out = { time: 0, distance: 0 };
  assert.equal(closestApproach(v(0, 0, 5), v(0, 0, 10), v(0, 0, 0), out), false);
  assert.equal(out.distance, 5);
  assert.equal(closestApproach(v(3, 4, 0), v(0, 0, 0), v(0, 0, 0), out), false);
  assert.equal(out.distance, 5);
});

test('roomAlong: distance to the confinement edge, capped', () => {
  const b = { minX: -4, maxX: 4, minZ: -9, maxZ: 0 };
  assert.equal(roomAlong(b, 3, -5, 1, 0, 10), 1);
  assert.equal(roomAlong(b, 3, -5, -1, 0, 2.5), 2.5);
  assert.equal(roomAlong(b, 0, -0.5, 0, 1, 10), 0.5);
  assert.equal(roomAlong(null, 0, 0, 1, 0, 3), 3);
  assert.equal(roomAlong(b, 5, -5, 1, 0, 3), 0); // already outside: no room
});

test('evadeDirection: keeps to the side the dodger is already on', () => {
  const b = { minX: -4, maxX: 4, minZ: -9, maxZ: 0 };
  // Ball travelling -Z (toward Home); dodger 1 m to +X of the ball line -> dodge +X.
  const out = evadeDirection(0, -20, 1, -8, b, 1, -5, 2.5);
  assert.ok(out.x > 0.99 && Math.abs(out.z) < 1e-9);
  // Dodger to -X -> dodge -X.
  const out2 = evadeDirection(0, -20, -1, -8, b, -1, -5, 2.5);
  assert.ok(out2.x < -0.99);
});

test('evadeDirection: crosses the line when walled in by the sideline', () => {
  const b = { minX: -4, maxX: 4, minZ: -9, maxZ: 0 };
  // Dodger at x=3.9 (0.1 m from the +X line) slightly to +X of the ball line: the -X side is far roomier.
  const out = evadeDirection(0, -20, 0.2, -8, b, 3.9, -5, 2.5);
  assert.ok(out.x < -0.99);
});

test('evadeDirection: result is a unit planar vector even for a static ball', () => {
  const out = evadeDirection(0, 0, 0, 0, null, 0, 0, 2.5);
  assert.ok(Math.abs(Math.hypot(out.x, out.z) - 1) < 1e-9);
});

test('dodgeUtility: reacts only inside the window and prefers catching slow balls', () => {
  const base = { weight: 1, speedKmh: 120, unblockable: false, holdingBall: true, reactionWindow: 0.55, preferCatchBelowKmh: 65 };
  assert.equal(dodgeUtility({ ...base, timeToImpact: 1.2 }), 0);
  assert.equal(dodgeUtility({ ...base, timeToImpact: 0.01 }), 0);
  const fast = dodgeUtility({ ...base, timeToImpact: 0.3 });
  assert.ok(fast > 0.7 && fast <= 1);
  const slowFreeHands = dodgeUtility({ ...base, timeToImpact: 0.3, speedKmh: 50, holdingBall: false });
  assert.ok(slowFreeHands < fast * 0.5);
  assert.equal(dodgeUtility({ ...base, timeToImpact: 0.3, dodgeDisabled: true }), 0);
});

test('reversalUtility: insurance only under threat, halved when the dodge is ready', () => {
  const base = { weight: 0.9, threatWindow: 1.1, chargingRange: 16, enemyDistance: 30, enemyCharging: false, incomingTime: Infinity };
  assert.equal(reversalUtility(base), 0);
  assert.equal(reversalUtility({ ...base, incomingTime: 0.5 }), 0.9);
  assert.equal(reversalUtility({ ...base, incomingTime: 0.5, dodgeReady: true }), 0.45);
  assert.ok(Math.abs(reversalUtility({ ...base, enemyCharging: true, enemyDistance: 10 }) - 0.72) < 1e-9);
  assert.equal(reversalUtility({ ...base, incomingTime: 0.5, invulnerable: true }), 0);
});
