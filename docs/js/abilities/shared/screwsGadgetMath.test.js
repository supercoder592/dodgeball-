// Unit tests for the tank heroes' pure gadget math (node:test, no three.js).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  sweptPointVsSphere, sweptPointVsAabb, segmentPointDistSq, expBlend, moveTowards, magneticHoming, depthIntoOwnHalf,
  extendPastCentreLine, inFrontWithin, inChargeVolume, leadTarget, wrapAngle, approachAngle, springStep,
  periodicValueNoise2, blobRadius,
} from './screwsGadgetMath.js';

const v = (x, y, z) => ({ x, y, z });
const close = (a, b, eps = 1e-6) => assert.ok(Math.abs(a - b) <= eps, `${a} !~ ${b}`);

test('swept point vs sphere: hit, miss, inside, moving away', () => {
  close(sweptPointVsSphere(v(-2, 0, 0), v(2, 0, 0), v(0, 0, 0), 1), 0.25);
  assert.equal(sweptPointVsSphere(v(-2, 2, 0), v(2, 2, 0), v(0, 0, 0), 1), -1);
  assert.equal(sweptPointVsSphere(v(0.2, 0, 0), v(3, 0, 0), v(0, 0, 0), 1), 0);
  assert.equal(sweptPointVsSphere(v(2, 0, 0), v(4, 0, 0), v(0, 0, 0), 1), -1);
});

test('swept point vs AABB: entering face, normal, parallel miss, inside recovery', () => {
  const n = v(0, 0, 0);
  const t = sweptPointVsAabb(v(0, 1, -2), v(0, 1, 2), v(-5, 0, -0.1), v(5, 3, 0.1), n);
  close(t, (2 - 0.1) / 4);
  assert.deepEqual([n.x, n.y, n.z], [0, 0, -1]);
  const t2 = sweptPointVsAabb(v(0, 1, 2), v(0, 1, -2), v(-5, 0, -0.1), v(5, 3, 0.1), n);
  close(t2, (2 - 0.1) / 4);
  assert.equal(n.z, 1);
  // Over the top of a 3 m wall: miss.
  assert.equal(sweptPointVsAabb(v(0, 3.5, -2), v(0, 3.5, 2), v(-5, 0, -0.1), v(5, 3, 0.1), n), -1);
  // Starts inside: t = 0, normal of the nearest face (z).
  assert.equal(sweptPointVsAabb(v(0, 1.5, 0.05), v(0, 1.5, 1), v(-5, 0, -0.1), v(5, 3, 0.1), n), 0);
  assert.equal(n.z, 1);
  // Segment ending before the box: miss.
  assert.equal(sweptPointVsAabb(v(0, 1, -3), v(0, 1, -1), v(-5, 0, -0.1), v(5, 3, 0.1), n), -1);
});

test('segment-point distance clamps to the segment', () => {
  close(segmentPointDistSq(v(0, 0, 0), v(2, 0, 0), v(1, 1, 0)), 1);
  close(segmentPointDistSq(v(0, 0, 0), v(2, 0, 0), v(3, 0, 0)), 1);
  close(segmentPointDistSq(v(0, 0, 0), v(0, 0, 0), v(0, 2, 0)), 4);
});

test('expBlend and moveTowards', () => {
  close(expBlend(0, 1), 0);
  assert.ok(expBlend(10, 1 / 60) > 0.1 && expBlend(10, 1 / 60) < 0.2);
  const a = moveTowards(v(0, 0, 0), v(10, 0, 0), 3);
  close(a.x, 3);
  const b = moveTowards(v(0, 0, 0), v(1, 0, 0), 3);
  close(b.x, 1);
});

test('magnetic homing bends toward the hands and slows near them', () => {
  const out = v(0, 0, 0);
  // Ball moving +x, hands straight ahead in -z direction relative to it.
  const c = magneticHoming(v(20, 0, 0), v(0, 0, -2), 5, 5, 20, 9, 5, 1 / 60, out);
  assert.ok(c > 0.5);
  assert.ok(out.z < 0, 'pulled toward the hands');
  assert.ok(Math.hypot(out.x, out.y, out.z) < 20, 'eased toward the arrival speed');
  assert.equal(magneticHoming(v(20, 0, 0), v(0, 0, -6), 5, 5, 20, 9, 5, 1 / 60, out), -1);
});

test('own-half depth and centre-line extension', () => {
  assert.equal(depthIntoOwnHalf(-1, -3), 3);
  assert.equal(depthIntoOwnHalf(-1, 1), -1);
  assert.equal(depthIntoOwnHalf(1, 2), 2);
  const home = extendPastCentreLine({ minX: -4, maxX: 4, minZ: -8.65, maxZ: -0.35 }, -1, 2);
  assert.equal(home.maxZ, 2); assert.equal(home.minZ, -8.65);
  const away = extendPastCentreLine({ minX: -4, maxX: 4, minZ: 0.35, maxZ: 8.65 }, 1, 2);
  assert.equal(away.minZ, -2); assert.equal(away.maxZ, 8.65);
});

test('grab cone and charge volume', () => {
  assert.equal(inFrontWithin(0, 0.8, 0, 1, 1), true);
  assert.equal(inFrontWithin(0, -0.8, 0, 1, 1), false);
  assert.equal(inFrontWithin(0, 1.2, 0, 1, 1), false);
  close(inChargeVolume(v(0, 0, 1), v(0, 0, 1), 1.3, 0.8), 1);
  assert.ok(Number.isNaN(inChargeVolume(v(0, 0, 2), v(0, 0, 1), 1.3, 0.8)));
  assert.ok(Number.isNaN(inChargeVolume(v(1.2, 0, 0.5), v(0, 0, 1), 1.3, 0.8)));
});

test('lead target and angles', () => {
  const out = v(0, 0, 0);
  const t = leadTarget(v(0, 1, 0), v(0, 1, 10), v(2, 0, 0), 25, out);
  assert.ok(t > 0.35 && t < 0.45);
  assert.ok(out.x > 0.7);
  close(wrapAngle(2 * Math.PI + 0.5), 0.5, 1e-9);
  close(wrapAngle(-2 * Math.PI - 0.5), -0.5, 1e-9);
  close(approachAngle(3.0, -3.0, 1, 0.1), 3.1, 1e-9); // shortest way across +-PI
  const s = { x: 1, v: 0 };
  for (let i = 0; i < 120; i++) springStep(s, 0, 20, 1 / 60);
  assert.ok(Math.abs(s.x) < 1e-3);
});

test('periodic noise tiles and blob outline closes', () => {
  for (let i = 0; i < 5; i++) {
    const y = i * 0.37;
    close(periodicValueNoise2(0.2, y, 8, 3), periodicValueNoise2(8.2, y, 8, 3), 1e-9);
    const n = periodicValueNoise2(i * 1.3, y, 8, 3);
    assert.ok(n >= 0 && n <= 1);
  }
  close(blobRadius(0, 2, 0.1, 5), blobRadius(2 * Math.PI, 2, 0.1, 5), 1e-9);
  const r = blobRadius(1.1, 2, 0.1, 5);
  assert.ok(r >= 1.8 && r <= 2.2);
});
