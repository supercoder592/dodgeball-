// Unit tests for the three-free character math (run: cd Tools/web && npm test).
import test from 'node:test';
import assert from 'node:assert/strict';
import {
  clamp, damp, wrapAngle, frac, locomotionBlend, gaitRate, leanFromAcceleration, springStep,
  twoBoneInteriorAngle, strafeLegYaw, oneShotEnvelope, normalizeWeights, ragdollDeltaV,
} from './avatarMath.js';

const STRIDES = { walk: { speed: 1.0, duration: 1.17 }, run: { speed: 2.9, duration: 0.71 }, sprint: { speed: 5.9, duration: 0.58 } };
const near = (a, b, eps = 1e-6) => assert.ok(Math.abs(a - b) <= eps, `${a} != ${b}`);

test('clamp/damp/wrap/frac basics', () => {
  near(clamp(5, 0, 1), 1); near(clamp(-1, 0, 1), 0);
  near(damp(0, 10, 5, 0), 0);
  assert.ok(damp(0, 10, 5, 0.1) > 0 && damp(0, 10, 5, 0.1) < 10);
  near(damp(0, 10, 5, 100), 10, 1e-6);
  near(Math.abs(wrapAngle(3 * Math.PI)), Math.PI, 1e-9);
  near(wrapAngle(-Math.PI / 2), -Math.PI / 2, 1e-9);
  near(frac(-0.25), 0.75);
});

test('locomotion blend brackets the speed and weights sum to 1', () => {
  for (const s of [0, 0.1, 0.5, 1, 2, 2.9, 4.6, 5.9, 7.4, 12]) {
    const b = locomotionBlend(s, STRIDES, 0.18);
    near(b.idle + b.walk + b.run + b.sprint, 1, 1e-9);
  }
  const standing = locomotionBlend(0, STRIDES);
  near(standing.idle, 1);
  const jog = locomotionBlend(4.6, STRIDES);
  assert.ok(jog.run > 0 && jog.sprint > 0 && jog.walk === 0);
  // feet planted: blended stride speed times rate equals ground speed when inside the clamp
  near(jog.strideSpeed * gaitRate(4.6, jog.strideSpeed), 4.6, 1e-9);
  const fast = locomotionBlend(7.4, STRIDES);
  near(fast.sprint, 1);
  near(gaitRate(7.4, fast.strideSpeed, 0.5, 1.5), 7.4 / 5.9, 1e-9);
  near(gaitRate(100, 1, 0.5, 1.5), 1.5);
});

test('lean follows the physics of a runner (tan theta = a/g) with sign conventions', () => {
  const l = leanFromAcceleration(9.81, 0, 9.81, 1, 1, 1, 1);
  near(l.pitch, Math.PI / 4, 1e-9);
  near(l.roll, 0, 1e-12);
  const left = leanFromAcceleration(0, 5, 9.81, 1, 1, 1, 1);
  assert.ok(left.roll < 0, 'accelerating to the left rolls left (negative)');
  const capped = leanFromAcceleration(1000, -1000, 9.81, 1, 1, 0.2, 0.3);
  near(capped.pitch, 0.2); near(capped.roll, 0.3);
});

test('damped spring settles', () => {
  const s = { x: 0, v: 5 };
  for (let i = 0; i < 300; i++) springStep(s, 15, 0.4, 1 / 60);
  assert.ok(Math.abs(s.x) < 1e-3 && Math.abs(s.v) < 1e-2);
});

test('two-bone IK interior angle', () => {
  near(twoBoneInteriorAngle(1, 1, 2), Math.PI, 1e-2); // straight (clamped just inside reach)
  near(twoBoneInteriorAngle(1, 1, Math.SQRT2), Math.PI / 2, 1e-9);
  assert.ok(twoBoneInteriorAngle(1, 1, 0) < 0.01);
});

test('strafe leg yaw + backpedal hysteresis', () => {
  const o = strafeLegYaw(0.5, 1, 0.9, 1.85, 1.65, false);
  near(o.legYaw, 0.5); assert.equal(o.backwards, false);
  const side = strafeLegYaw(Math.PI / 2, 1, 0.9, 1.85, 1.65, false);
  near(side.legYaw, 1);
  const back = strafeLegYaw(Math.PI, 1, 0.9, 1.85, 1.65, false);
  assert.equal(back.backwards, true); near(back.legYaw, 0, 1e-9);
  // hysteresis: at 1.75 rad we stay in whatever mode we were
  assert.equal(strafeLegYaw(1.75, 1, 0.9, 1.85, 1.65, true).backwards, true);
  assert.equal(strafeLegYaw(1.75, 1, 0.9, 1.85, 1.65, false).backwards, false);
});

test('one-shot envelope, weight normalisation, ragdoll delta-v', () => {
  near(oneShotEnvelope(0, 2, 0.2, 0.3), 0);
  near(oneShotEnvelope(1, 2, 0.2, 0.3), 1);
  near(oneShotEnvelope(2.5, 2, 0.2, 0.3), 0);
  const w = { a: 1, b: 3 };
  near(normalizeWeights(w, ['a', 'b']), 4);
  near(w.a + w.b, 1);
  near(ragdollDeltaV(0, 75, 20, 1.2, 6.5), 1.2);
  near(ragdollDeltaV(1e6, 75, 20, 1.2, 6.5), 6.5);
  near(ragdollDeltaV(10.5, 75, 20, 1.2, 6.5), 2.8);
});
