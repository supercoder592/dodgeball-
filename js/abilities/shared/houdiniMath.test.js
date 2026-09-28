// node:test for Houdini's pure geometry (run: cd Tools/web && npm test).
import test from 'node:test';
import assert from 'node:assert/strict';
import { mirroredSwapDestinations, pushAwayPlanar, fanAngle, roundRobin, spreadX, ringPoint } from './houdiniMath.js';

const v = (x, y, z) => ({ x, y, z });

test('mirrored swap keeps both players on their own half', () => {
  // Houdini (Home, z<0) at back-left, enemy (Away, z>0) near the centre line on the right.
  const h = v(-3, 0, -7), e = v(2.5, 0, 1.2);
  const oh = v(0, 0, 0), oe = v(0, 0, 0);
  mirroredSwapDestinations(h, e, -1, +1, 0, oh, oe);
  assert.deepEqual(oh, { x: 2.5, y: 0, z: -1.2 }); // enemy's lane + depth, in Home half
  assert.deepEqual(oe, { x: -3, y: 0, z: 7 });     // Houdini's lane + depth, in Away half
  assert.ok(oh.z <= 0 && oe.z >= 0);
});

test('mirrored swap works for an Away Houdini and preserves heights', () => {
  const h = v(1, 0.4, 3), e = v(-2, 0, -8);
  const oh = v(0, 0, 0), oe = v(0, 0, 0);
  mirroredSwapDestinations(h, e, +1, -1, 0, oh, oe);
  assert.deepEqual(oh, { x: -2, y: 0.4, z: 8 });
  assert.deepEqual(oe, { x: 1, y: 0, z: -3 });
});

test('mirrored swap is an involution (swapping twice restores positions)', () => {
  const h = v(-1.7, 0, -2.2), e = v(3.1, 0, 6.4);
  const a = v(0, 0, 0), b = v(0, 0, 0), c = v(0, 0, 0), d = v(0, 0, 0);
  mirroredSwapDestinations(h, e, -1, 1, 0, a, b);
  mirroredSwapDestinations(a, b, -1, 1, 0, c, d);
  assert.deepEqual(c, h);
  assert.deepEqual(d, e);
});

test('pushAwayPlanar enforces a minimum separation', () => {
  const dest = v(0.2, 0, 0);
  pushAwayPlanar(dest, v(0, 0, 0), 0.8);
  assert.ok(Math.abs(Math.hypot(dest.x, dest.z) - 0.8) < 1e-9);
  const same = v(1, 0, 1);
  pushAwayPlanar(same, v(1, 0, 1), 0.5, 0, 1);
  assert.deepEqual(same, { x: 1, y: 0, z: 1.5 });
  const far = v(5, 0, 5);
  pushAwayPlanar(far, v(0, 0, 0), 0.8);
  assert.deepEqual(far, { x: 5, y: 0, z: 5 });
});

test('fanAngle alternates around straight ahead', () => {
  const s = 0.5;
  assert.deepEqual([0, 1, 2, 3, 4].map((k) => fanAngle(k, s)), [0, 0.5, -0.5, 1, -1]);
});

test('roundRobin distributes items over receivers', () => {
  const out = { receiver: 0, slot: 0 };
  const seq = [];
  for (let i = 0; i < 7; i++) { roundRobin(i, 3, out); seq.push([out.receiver, out.slot]); }
  assert.deepEqual(seq, [[0, 0], [1, 0], [2, 0], [0, 1], [1, 1], [2, 1], [0, 2]]);
  roundRobin(4, 0, out);
  assert.deepEqual(out, { receiver: 0, slot: 4 });
});

test('spreadX spans the width symmetrically inside the margins', () => {
  assert.equal(spreadX(0, 1, 9), 0);
  assert.equal(spreadX(0, 3, 9, 0.8), -3.7);
  assert.equal(spreadX(1, 3, 9, 0.8), 0);
  assert.equal(spreadX(2, 3, 9, 0.8), 3.7);
});

test('ringPoint follows the yaw convention (yaw 0 faces +Z)', () => {
  const o = v(0, 0, 0);
  ringPoint(1, 2, 0, 0.7, o);
  assert.ok(Math.abs(o.x - 1) < 1e-12 && Math.abs(o.z - 2.7) < 1e-12);
  ringPoint(0, 0, Math.PI / 2, 1, o);
  assert.ok(Math.abs(o.x - 1) < 1e-12 && Math.abs(o.z) < 1e-12);
});
