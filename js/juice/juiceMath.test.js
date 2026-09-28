// node --test docs/js/juice/  (pure logic: no three.js)
import test from 'node:test';
import assert from 'node:assert/strict';
import { Perlin1D, fade } from './noise.js';
import {
  intensityForSpeed, hitstopDuration, distanceFalloff, shakeEnvelope, kickEnvelope,
  dampedImpulse, impulsePeakFactor, omegaForSettle, squashScales, dampFactor,
} from './juiceMath.js';

const close = (a, b, eps = 1e-4) => assert.ok(Math.abs(a - b) <= eps, `${a} != ${b}`);

test('perlin noise is bounded, deterministic, continuous and zero on the lattice', () => {
  const a = new Perlin1D(7), b = new Perlin1D(7), c = new Perlin1D(8);
  let differs = false;
  for (let i = 0; i < 2000; i++) {
    const x = i * 0.0137 - 3.3;
    const v = a.noise(x);
    assert.ok(v >= -1 && v <= 1, `out of range ${v}`);
    assert.equal(v, b.noise(x));
    if (Math.abs(v - c.noise(x)) > 1e-3) differs = true;
    // Continuity: a tiny step changes the value by a tiny amount (slope <= 2 per unit in [-1,1] units).
    assert.ok(Math.abs(a.noise(x + 1e-4) - v) < 1e-3);
    const f = a.fbm(x, 3);
    assert.ok(f >= -1 && f <= 1);
  }
  assert.ok(differs, 'different seeds must give different curves');
  close(a.noise(5), 0);
  close(fade(0), 0); close(fade(1), 1); close(fade(0.5), 0.5);
});

test('speed -> intensity curve spans floor..1 between 40 and 220 km/h', () => {
  close(intensityForSpeed(10, 40, 220, 0.25), 0.25);
  close(intensityForSpeed(40, 40, 220, 0.25), 0.25);
  close(intensityForSpeed(220, 40, 220, 0.25), 1);
  close(intensityForSpeed(400, 40, 220, 0.25), 1);
  const mid = intensityForSpeed(130, 40, 220, 0.25);
  assert.ok(mid > 0.25 && mid < 1);
  assert.ok(intensityForSpeed(150, 40, 220) > intensityForSpeed(120, 40, 220));
});

test('hitstop stays inside the 0.03..0.1 s spec window', () => {
  close(hitstopDuration(0, 0.03, 0.1), 0.03);
  close(hitstopDuration(1, 0.03, 0.1), 0.1);
  close(hitstopDuration(0.5, 0.03, 0.1, true, 1.25), 0.065 * 1.25);
  close(hitstopDuration(1, 0.03, 0.1, true, 1.25), 0.1);
  close(hitstopDuration(-2, 0.03, 0.1), 0.03);
});

test('falloff, envelopes and smoothing', () => {
  close(distanceFalloff(2, 4, 28), 1);
  close(distanceFalloff(40, 4, 28, 0.1), 0.1);
  const m = distanceFalloff(16, 4, 28);
  assert.ok(m > 0 && m < 1);
  close(shakeEnvelope(0, 0.3), 1);
  close(shakeEnvelope(0.3, 0.3), 0);
  close(kickEnvelope(0, 0.4), 0);
  close(kickEnvelope(0.06, 0.4, 0.15), 1);
  close(kickEnvelope(0.4, 0.4), 0);
  close(dampFactor(10, 0), 0);
  assert.ok(dampFactor(10, 1) > 0.99);
});

test('squash spring peaks at the requested compression, overshoots into stretch and settles', () => {
  const zeta = 0.35, duration = 0.2, peak = 0.4;
  const w = omegaForSettle(duration, zeta, 0.02);
  const v0 = (peak * w) / impulsePeakFactor(zeta);
  let max = 0, min = 0;
  for (let t = 0; t <= duration; t += 1e-4) {
    const s = dampedImpulse(t, v0, w, zeta);
    max = Math.max(max, s); min = Math.min(min, s);
  }
  close(max, peak, 1e-3);
  assert.ok(min < 0, 'must overshoot into a stretch');
  assert.ok(Math.abs(dampedImpulse(duration * 1.3, v0, w, zeta)) < 0.02 * peak * 2);
  // Volume preservation: axis * perp^2 == 1.
  for (const s of [-0.3, 0, 0.2, 0.55]) {
    const k = squashScales(s);
    close(k.axis * k.perp * k.perp, 1);
  }
});
