// node --test docs/js/input/inputMath.test.js
import test from 'node:test';
import assert from 'node:assert/strict';
import {
  ActionState, SRC, radialDeadzone, axisCurve, clampMagnitude2, triggerHysteresis, RepeatNav, keyAxes,
} from './inputMath.js';

test('ActionState: press and release inside one frame still reports both edges', () => {
  const a = new ActionState();
  a.setSource(SRC.MOUSE, true);
  a.setSource(SRC.MOUSE, false);
  a.latch();
  assert.equal(a.pressed, true);
  assert.equal(a.released, true);
  assert.equal(a.held, false);
  a.latch();
  assert.equal(a.pressed, false);
  assert.equal(a.released, false);
});

test('ActionState: a second device holding the same action does not re-press', () => {
  const a = new ActionState();
  a.keyDown(); a.latch();
  assert.equal(a.pressed, true);
  a.setSource(SRC.PAD, true); a.latch();
  assert.equal(a.pressed, false);
  assert.equal(a.held, true);
  a.keyUp(); a.latch();
  assert.equal(a.released, false, 'still held by the pad');
  a.setSource(SRC.PAD, false); a.latch();
  assert.equal(a.released, true);
  assert.equal(a.held, false);
});

test('ActionState: two keys bound to one action are counted', () => {
  const a = new ActionState();
  a.keyDown(); a.keyDown(); a.latch();
  a.keyUp(); a.latch();
  assert.equal(a.held, true);
  a.keyUp(); a.latch();
  assert.equal(a.held, false);
  assert.equal(a.released, true);
  a.keyUp(); a.latch(); // extra key-up never underflows
  assert.equal(a.keyCount, 0);
});

test('ActionState.clear drops holds without edges', () => {
  const a = new ActionState();
  a.keyDown(); a.latch();
  a.clear(); a.latch();
  assert.equal(a.held, false);
  assert.equal(a.released, false);
  assert.equal(a.pressed, false);
});

test('radialDeadzone rescales and keeps direction', () => {
  const o = { x: 0, y: 0 };
  radialDeadzone(0.1, 0, 0.15, o);
  assert.deepEqual(o, { x: 0, y: 0 });
  radialDeadzone(1, 0, 0.15, o);
  assert.ok(Math.abs(o.x - 1) < 1e-9 && o.y === 0);
  radialDeadzone(0, -0.575, 0.15, o);
  assert.ok(Math.abs(o.y + 0.5) < 1e-9);
  radialDeadzone(0.6, 0.8, 0, o, 2);
  assert.ok(Math.abs(Math.hypot(o.x, o.y) - 1) < 1e-9);
});

test('axisCurve is sign preserving with a deadzone', () => {
  assert.equal(axisCurve(0.05, 0.1), 0);
  assert.ok(axisCurve(-1, 0.1) === -1);
  assert.ok(Math.abs(axisCurve(0.55, 0.1, 2) - 0.25) < 1e-9);
});

test('clampMagnitude2 and keyAxes', () => {
  const v = clampMagnitude2({ x: 3, y: 4 }, 1);
  assert.ok(Math.abs(Math.hypot(v.x, v.y) - 1) < 1e-9);
  const k = keyAxes(true, false, false, true, { x: 0, y: 0 });
  assert.ok(Math.abs(Math.hypot(k.x, k.y) - 1) < 1e-9 && k.x > 0 && k.y > 0);
  const n = keyAxes(true, true, true, true, { x: 0, y: 0 });
  assert.equal(n.x, 0); assert.equal(n.y, 0);
});

test('triggerHysteresis', () => {
  assert.equal(triggerHysteresis(0.3, false), false);
  assert.equal(triggerHysteresis(0.4, false), true);
  assert.equal(triggerHysteresis(0.25, true), true);
  assert.equal(triggerHysteresis(0.1, true), false);
});

test('RepeatNav fires immediately, after the delay, then at the interval', () => {
  const r = new RepeatNav(0.4, 0.1);
  assert.equal(r.update(true, 0.016), true);
  let fired = 0;
  for (let t = 0; t < 0.39; t += 0.01) if (r.update(true, 0.01)) fired++;
  assert.equal(fired, 0);
  for (let t = 0; t < 0.305; t += 0.01) if (r.update(true, 0.01)) fired++;
  assert.ok(fired >= 3 && fired <= 4, `fired ${fired}`);
  assert.equal(r.update(false, 0.01), false);
  assert.equal(r.update(true, 0.01), true);
});
