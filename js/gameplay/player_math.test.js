// Unit tests for the pure player helpers (node:test, no three.js). Run: cd Tools/web && npm test
import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  clamp, moveTowards, wrapAngle, deltaAngle, moveTowardsAngle, accelerationCurve, jumpSpeed, jumpAirTime,
  slideStartSpeed, slideFrictionStep, knockbackControl, statusSpeedMultiplier, clampAxis, ragdollImpulseParts,
} from './player_math.js';

const near = (a, b, eps = 1e-6) => assert.ok(Math.abs(a - b) <= eps, `${a} !~ ${b}`);

test('clamp / moveTowards never overshoot', () => {
  assert.equal(clamp(5, 0, 3), 3);
  assert.equal(clamp(-1, 0, 3), 0);
  assert.equal(moveTowards(0, 10, 3), 3);
  assert.equal(moveTowards(9, 10, 3), 10);
  assert.equal(moveTowards(10, -10, 4), 6);
});

test('angles wrap to (-PI, PI] and turn along the shortest arc', () => {
  near(wrapAngle(3 * Math.PI), Math.PI);
  near(wrapAngle(-3 * Math.PI / 2), Math.PI / 2);
  near(deltaAngle(Math.PI - 0.1, -Math.PI + 0.1), 0.2);
  near(moveTowardsAngle(Math.PI - 0.1, -Math.PI + 0.1, 0.05), Math.PI - 0.05);
  near(moveTowardsAngle(0, 1, 5), 1);
  assert.equal(wrapAngle(NaN), 0);
});

test('acceleration curve is strongest from standstill and tapers near top speed', () => {
  const a0 = accelerationCurve(0), a5 = accelerationCurve(0.5), a1 = accelerationCurve(1);
  assert.ok(a0 > a5 && a5 > a1);
  near(a0, 1.3);
  near(a1, 0.4);
  near(accelerationCurve(2), a1); // clamped
});

test('jump: v = sqrt(2 g h), air time 2v/g', () => {
  const g = 9.81 * 1.8;
  near(jumpSpeed(g, 1.05), Math.sqrt(2 * g * 1.05));
  near(jumpAirTime(g, 1.05), (2 * Math.sqrt(2 * g * 1.05)) / g);
  assert.equal(jumpSpeed(g, -1), 0);
});

test('slide preserves momentum and loses speed to friction (less on ice)', () => {
  near(slideStartSpeed(7.4, 4.6, 1, 1.2), 7.4 * 1.2);      // sprinting: keeps the faster speed
  near(slideStartSpeed(0, 4.6, 1, 1.2), 4.6 * 1.2);        // standing: walk-speed dodge
  near(slideStartSpeed(0, 4.6, 0.4, 1.2), 4.6 * 0.4 * 1.2); // slowed
  const grip = slideFrictionStep(8, 6.5, 1, 0.3, 0.1);
  const ice = slideFrictionStep(8, 6.5, 0, 0.3, 0.1);
  near(grip, 8 - 0.65);
  assert.ok(ice > grip);
  assert.equal(slideFrictionStep(0.1, 6.5, 1, 0.3, 1), 0);
});

test('knockback removes control proportionally and never restores it', () => {
  near(knockbackControl(1, 0, 5, 0.1), 1);
  near(knockbackControl(1, 5, 5, 0.1), 0.1);
  near(knockbackControl(1, 2.5, 5, 0.1), 0.55);
  near(knockbackControl(0.2, 1, 5, 0.1), 0.2);
});

test('status speed multiplier: strongest slow x strongest haste, clamped', () => {
  near(statusSpeedMultiplier(0.6, 0), 0.4);
  near(statusSpeedMultiplier(0, 0.2), 1.2);
  near(statusSpeedMultiplier(0.6, 0.2), 0.4 * 1.2);
  near(statusSpeedMultiplier(1, 0), 0.1); // a slow is never a root
  near(statusSpeedMultiplier(0, 5), 2);
});

test('clampAxis snaps back outside and clips velocity inside', () => {
  const s = { p: -5, v: -3 };
  assert.equal(clampAxis(s, -4, 4, 1 / 60), true);
  assert.deepEqual(s, { p: -4, v: 0 });
  const t = { p: 3.95, v: 6 };
  assert.equal(clampAxis(t, -4, 4, 1 / 60), false);
  near(t.p + t.v / 60, 4);
  const u = { p: 0, v: 2 };
  clampAxis(u, -4, 4, 1 / 60);
  assert.equal(u.v, 2);
});

test('ragdoll impulse: momentum x readability factor with upward share, capped', () => {
  const r = ragdollImpulseParts(25, 0.35, 3, 0.15, 90);
  near(r.planarScale, 0.35 * 3);
  near(r.up, 25 * 0.35 * 3 * 0.15);
  const capped = ragdollImpulseParts(200, 0.35, 3, 0.15, 90);
  near(Math.hypot(200 * capped.planarScale, capped.up), 90, 1e-6);
});
