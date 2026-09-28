// node --test docs/js/abilities/shared/   (pure logic, no three.js)
import test from 'node:test';
import assert from 'node:assert/strict';
import {
  segmentVsVerticalCapsule, sweptPointVsSphere, expSmoothing, smoothstep01, smoothstep01Derivative, deltaAngle,
  flankOffset, clampToBounds, isSlotInsideBounds, chooseInitialSlot, pickPerceivedIndex, slotPosition,
} from './shadowMath.js';

const close = (a, b, eps = 1e-6) => assert.ok(Math.abs(a - b) <= eps, `${a} != ${b}`);
const v = (x, y, z) => ({ x, y, z });

test('swept point vs sphere', () => {
  close(sweptPointVsSphere(v(-2, 0, 0), v(2, 0, 0), v(0, 0, 0), 1), 0.25);
  assert.equal(sweptPointVsSphere(v(-2, 2, 0), v(2, 2, 0), v(0, 0, 0), 1), -1);
  assert.equal(sweptPointVsSphere(v(0.5, 0, 0), v(2, 0, 0), v(0, 0, 0), 1), 0);
});

test('segment vs vertical capsule: side, cap, miss, inside', () => {
  const p = v(0, 0, 0), n = v(0, 0, 0);
  // Horizontal shot at chest height into the straight part (axis at x=0, y 0.3..1.5, radius 0.4).
  const t = segmentVsVerticalCapsule(v(-2, 1, 0), v(2, 1, 0), v(0, 0, 0), 0.3, 1.5, 0.4, p, n);
  close(t, (2 - 0.4) / 4);
  close(p.x, -0.4); close(p.y, 1); close(n.x, -1); close(n.y, 0);
  // Dropping onto the top cap.
  const t2 = segmentVsVerticalCapsule(v(0, 3, 0), v(0, 1.8, 0), v(0, 0, 0), 0.3, 1.5, 0.4, p, n);
  close(p.y, 1.9); close(n.y, 1); assert.ok(t2 > 0 && t2 < 1);
  // Passing overhead: miss.
  assert.equal(segmentVsVerticalCapsule(v(-2, 2.5, 0), v(2, 2.5, 0), v(0, 0, 0), 0.3, 1.5, 0.4, p, n), -1);
  // Passing beside: miss.
  assert.equal(segmentVsVerticalCapsule(v(-2, 1, 1), v(2, 1, 1), v(0, 0, 0), 0.3, 1.5, 0.4, p, n), -1);
  // Too short to reach.
  assert.equal(segmentVsVerticalCapsule(v(-2, 1, 0), v(-1, 1, 0), v(0, 0, 0), 0.3, 1.5, 0.4, p, n), -1);
  // Starting inside.
  assert.equal(segmentVsVerticalCapsule(v(0.1, 1, 0), v(2, 1, 0), v(0, 0, 0), 0.3, 1.5, 0.4, p, n), 0);
});

test('smoothing helpers', () => {
  close(expSmoothing(10, 0), 0);
  assert.ok(expSmoothing(10, 1 / 60) > 0.15 && expSmoothing(10, 1 / 60) < 0.16);
  close(smoothstep01(0), 0); close(smoothstep01(1), 1); close(smoothstep01(0.5), 0.5);
  close(smoothstep01Derivative(0.5), 1.5); close(smoothstep01Derivative(0), 0);
  close(deltaAngle(0.1, -0.1), -0.2);
  close(Math.abs(deltaAngle(Math.PI - 0.1, -Math.PI + 0.1)), 0.2);
});

test('formation slots: flank offsets, clamping, initial slot choice', () => {
  close(flankOffset(0, 1.4), 1.4); close(flankOffset(1, 1.4), -1.4); close(flankOffset(2, 1.4), 2.8);
  const b = { minX: -4, maxX: 4, minZ: -9, maxZ: 0 };
  const q = clampToBounds(b, v(6, 0, 3));
  close(q.x, 4); close(q.z, 0);
  const lateral = v(1, 0, 0);
  // Player hugging the right sideline (x = 3.8): the real body should take the right-most slot so clones fit inside.
  const slot = chooseInitialSlot(b, v(3.8, 0, -5), lateral, 3, 1.4);
  assert.equal(slot, 2);
  // Centre court: middle slot.
  assert.equal(chooseInitialSlot(b, v(0, 0, -5), lateral, 3, 1.4), 1);
  const anchor = v(0, 0, -5);
  assert.equal(isSlotInsideBounds(b, anchor, lateral, 0, 1, 1.4), true);
  assert.equal(isSlotInsideBounds(b, v(3.8, 0, -5), lateral, 2, 1, 1.4), false);
  const out = v(0, 0, 0);
  slotPosition(anchor, lateral, 2, 1, 1.4, out);
  close(out.x, 1.4); close(out.z, -5);
});

test('perceived aim pick is uniform over clones + the real body', () => {
  assert.equal(pickPerceivedIndex(2, 0), 0);
  assert.equal(pickPerceivedIndex(2, 0.4), 1);
  assert.equal(pickPerceivedIndex(2, 0.99), 2);
  assert.equal(pickPerceivedIndex(2, 1), 2);
  assert.equal(pickPerceivedIndex(0, 0.5), 0);
});
