// node:test for Elsa's pure frost math (run: cd Tools/web && npm test).
import test from 'node:test';
import assert from 'node:assert/strict';
import { TrailStepper, pointInPatch, patchEnvelope, fieldEnvelope, valueNoise, fbm, hash2 } from './elsaFrostMath.js';

test('TrailStepper emits evenly spaced points regardless of step size', () => {
  for (const step of [0.05, 0.37, 1.9]) {
    const s = new TrailStepper(0.9, 100);
    s.reset(0, 0, 0.45);
    const pts = [];
    for (let z = step; z <= 10 + 1e-9; z += step) s.advance(0, z, (x, pz) => pts.push(pz), 100);
    // Points at 0.45, 1.35, 2.25, ... up to 10.
    const expected = [];
    for (let d = 0.45; d <= 10 - 1e-9; d += 0.9) expected.push(d);
    assert.equal(pts.length, expected.length, `step ${step}`);
    pts.forEach((p, i) => assert.ok(Math.abs(p - expected[i]) < 1e-6, `step ${step} point ${i}: ${p} vs ${expected[i]}`));
  }
});

test('TrailStepper reports the travel yaw and honours caps', () => {
  const s = new TrailStepper(1, 3);
  s.reset(0, 0, 0.5);
  const yaws = [];
  const n = s.advance(10, 0, (x, z, yaw) => yaws.push(yaw), 8);
  assert.equal(n, 3);
  assert.ok(s.exhausted);
  assert.ok(yaws.every((y) => Math.abs(y - Math.PI / 2) < 1e-12)); // travelling +X => yaw 90deg
  const t = new TrailStepper(1, 100);
  t.reset(0, 0, 0);
  assert.equal(t.advance(0, 20, () => {}, 4), 4); // per-call cap
  assert.equal(t.advance(0, 20, () => {}, 4), 0); // no backlog carried over
});

test('pointInPatch handles orientation and margin', () => {
  // Patch travelling along +X (yaw 90deg): length along X, width along Z.
  const yaw = Math.PI / 2;
  assert.ok(pointInPatch(0.5, 0.1, 0, 0, yaw, 0.45, 0.54));
  assert.ok(!pointInPatch(0.1, 0.5, 0, 0, yaw, 0.45, 0.54));
  assert.ok(pointInPatch(0.1, 0.5, 0, 0, yaw, 0.45, 0.54, 0.1));
  assert.ok(pointInPatch(3, 3, 3, 3, 0.3, 0.1, 0.1));
});

test('patchEnvelope grows, holds and melts to zero', () => {
  assert.equal(patchEnvelope(-0.1, 3), 0);
  assert.ok(patchEnvelope(0.06, 3) > 0.5 && patchEnvelope(0.06, 3) < 1);
  assert.equal(patchEnvelope(1.5, 3), 1);
  assert.ok(patchEnvelope(2.8, 3) < 0.5);
  assert.equal(patchEnvelope(3, 3), 0);
});

test('fieldEnvelope creeps in, holds and thaws', () => {
  const o = {};
  fieldEnvelope(0, 6, 0.45, 0.7, o);
  assert.equal(o.spread, 0); assert.equal(o.alpha, 0); assert.equal(o.done, false);
  fieldEnvelope(3, 6, 0.45, 0.7, o);
  assert.equal(o.spread, 1); assert.equal(o.alpha, 1);
  fieldEnvelope(6.35, 6, 0.45, 0.7, o);
  assert.ok(Math.abs(o.alpha - 0.5) < 1e-9);
  fieldEnvelope(6.7, 6, 0.45, 0.7, o);
  assert.equal(o.done, true); assert.equal(o.alpha, 0);
});

test('value noise is periodic and bounded', () => {
  for (let i = 0; i < 50; i++) {
    const x = hash2(i, 1) * 8, y = hash2(i, 2) * 8;
    const a = valueNoise(x, y, 8, 3), b = valueNoise(x + 8, y - 16, 8, 3);
    assert.ok(Math.abs(a - b) < 1e-12);
    assert.ok(a >= 0 && a <= 1);
    const f = fbm(x / 8, y / 8, 4, 4, 1);
    assert.ok(f >= 0 && f <= 1);
    assert.ok(Math.abs(fbm(x / 8, y / 8, 4, 4, 1) - fbm(x / 8 + 1, y / 8, 4, 4, 1)) < 1e-9);
  }
});
