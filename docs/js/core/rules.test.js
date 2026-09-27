// node --test docs/js/core/
import test from 'node:test';
import assert from 'node:assert/strict';
import { RallyMath, CatchTiming, CatchQuality, Ballistics } from './rules.js';
import { Cooldown, Meter } from './timers.js';
import { StateMachine } from './fsm.js';
import { MAX_BALL_SPEED_MS, GRAVITY, IRON_MITTS_MULTIPLIER } from './constants.js';

const close = (a, b, eps = 1e-4) => assert.ok(Math.abs(a - b) <= eps, `${a} != ${b}`);

test('rally boost is +10% per rally and capped at 220 km/h', () => {
  close(RallyMath.computeSpeed(20, 0), 20);
  close(RallyMath.multiplier(3), 1.3);
  close(RallyMath.computeSpeed(50, 20), MAX_BALL_SPEED_MS);
  close(MAX_BALL_SPEED_MS * 3.6, 220, 1e-2);
  assert.equal(RallyMath.next(0), 1);
});

test('perfect catch window 0..0.15 s, normal up to 0.4 s', () => {
  assert.equal(CatchTiming.classify(0), CatchQuality.PERFECT);
  assert.equal(CatchTiming.classify(0.15), CatchQuality.PERFECT);
  assert.equal(CatchTiming.classify(0.2), CatchQuality.NORMAL);
  assert.equal(CatchTiming.classify(0.41), CatchQuality.MISS);
  assert.equal(CatchTiming.classify(-0.01), CatchQuality.MISS);
  const mitts = CatchTiming.scaledPerfectWindow(IRON_MITTS_MULTIPLIER);
  close(mitts, 0.225);
  assert.equal(CatchTiming.classify(0.2, mitts), CatchQuality.PERFECT);
});

test('low-arc ballistic solution hits the target', () => {
  for (const [v, x, y] of [[20, 10, 0], [25, 15, -0.4], [55, 17, 0.2]]) {
    const { ok, angle } = Ballistics.solveAngle(v, x, y, GRAVITY, true);
    assert.ok(ok);
    const t = Ballistics.flightTime(v, angle, x);
    close(v * Math.sin(angle) * t - 0.5 * GRAVITY * t * t, y, 1e-2);
  }
  assert.equal(Ballistics.solveAngle(5, 50, 0, GRAVITY).ok, false);
});

test('cooldown and meter', () => {
  const cd = new Cooldown(1); cd.start();
  assert.equal(cd.tick(0.6), false); assert.equal(cd.tick(0.6), true); assert.ok(cd.ready);
  const m = new Meter(1);
  assert.equal(m.add(0.5).becameFull, false); assert.equal(m.add(0.7).becameFull, true);
  assert.ok(m.tryConsume(1)); close(m.value, 0);
});

test('state machine queues transitions requested on enter', () => {
  const log = [];
  const fsm = new StateMachine();
  const mk = (id, onEnter) => ({ id, enter() { log.push('enter ' + id); onEnter && onEnter(); }, exit() { log.push('exit ' + id); } });
  fsm.register(mk('a')); fsm.register(mk('b', () => fsm.change('c'))); fsm.register(mk('c'));
  fsm.start('a'); fsm.change('b');
  assert.ok(fsm.is('c'));
  assert.deepEqual(log, ['enter a', 'exit a', 'enter b', 'exit b', 'enter c']);
});
