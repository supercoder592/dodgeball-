// node --test docs/js/input/inputMath.test.js
import test from 'node:test';
import assert from 'node:assert/strict';
import {
  ActionState, SRC, radialDeadzone, axisCurve, clampMagnitude2, triggerHysteresis, RepeatNav, keyAxes,
  TOUCH_PRIMARY, touchPrimaryMode, DODGE, dodgeGesture, dodgeAction, isTap, ballApproaches,
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

test('touchPrimaryMode: throw with the ball, pickup only when nothing is incoming, catch otherwise', () => {
  assert.equal(touchPrimaryMode(true, true, true), TOUCH_PRIMARY.THROW);
  assert.equal(touchPrimaryMode(true, false, false), TOUCH_PRIMARY.THROW);
  assert.equal(touchPrimaryMode(false, true, false), TOUCH_PRIMARY.PICKUP);
  assert.equal(touchPrimaryMode(false, true, true), TOUCH_PRIMARY.CATCH);
  assert.equal(touchPrimaryMode(false, false, false), TOUCH_PRIMARY.CATCH);
});

test('dodgeGesture: still = jump now, moving = slide on tap / after the wait, swipe up = jump', () => {
  assert.equal(dodgeGesture(false, 0, 0, 0, false), DODGE.JUMP);
  assert.equal(dodgeGesture(true, 0, 0, 20, false), DODGE.NONE);
  assert.equal(dodgeGesture(true, 2, 3, 60, true), DODGE.SLIDE, 'tap while moving');
  assert.equal(dodgeGesture(true, 0, 0, 110, false), DODGE.SLIDE, 'held past the wait');
  assert.equal(dodgeGesture(true, 4, -20, 50, false), DODGE.JUMP, 'quick upward swipe');
  assert.equal(dodgeGesture(true, 30, -20, 50, false), DODGE.NONE, 'mostly sideways is not a swipe up');
  assert.equal(dodgeGesture(true, 0, 20, 50, false), DODGE.NONE, 'downward drag is not a swipe up');
});

test('dodgeAction: slide only when the state machine would start one, otherwise jump', () => {
  assert.equal(dodgeAction(false, 'grounded', true, 5, 2.76), DODGE.JUMP, 'standing-still gesture');
  assert.equal(dodgeAction(true, 'grounded', true, 4.6, 2.76), DODGE.SLIDE, 'walking fast enough');
  assert.equal(dodgeAction(true, 'grounded', true, 1.2, 2.76), DODGE.JUMP, 'too slow to slide');
  assert.equal(dodgeAction(true, 'sprinting', true, 0.5, 2.76), DODGE.SLIDE, 'sprint slides at any speed');
  assert.equal(dodgeAction(true, 'sprinting', false, 7, 2.76), DODGE.JUMP, 'slide cooldown');
  assert.equal(dodgeAction(true, 'grounded', false, 5, 2.76), DODGE.JUMP, 'slide cooldown');
  assert.equal(dodgeAction(true, 'chargingThrow', true, 2.76, 2.76), DODGE.JUMP, 'wind-up -> jump throw');
  assert.equal(dodgeAction(true, 'airborne', false, 5, 2.76), DODGE.JUMP);
  assert.equal(dodgeAction(true, 'catching', true, 3, 2.76), DODGE.JUMP);
});

test('isTap: short and still', () => {
  assert.equal(isTap(120, 4), true);
  assert.equal(isTap(260, 4), false);
  assert.equal(isTap(120, 20), false);
  assert.equal(isTap(220, 12), true);
});

test('ballApproaches: closest approach within radius and horizon, never when moving away', () => {
  // Ball 10 m in front, flying straight at the player at 20 m/s.
  assert.equal(ballApproaches(0, 0, 0, 10, 0, -20, 2, 1.5), true);
  // Same ball flying away.
  assert.equal(ballApproaches(0, 0, 0, 10, 0, 20, 2, 1.5), false);
  // Passing 4 m to the side.
  assert.equal(ballApproaches(0, 0, 4, 10, 0, -20, 2, 1.5), false);
  // Too far away for the horizon (40 m at 20 m/s = 2 s).
  assert.equal(ballApproaches(0, 0, 0, 40, 0, -20, 2, 1.5), false);
  // A resting ball never approaches.
  assert.equal(ballApproaches(0, 0, 0, 1, 0, 0, 2, 1.5), false);
});
