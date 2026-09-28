// node --test docs/js/abilities/shared/   (pure logic, no three.js)
import test from 'node:test';
import assert from 'node:assert/strict';
import { overchargeFraction, overchargeMultipliers, shockwaveFalloff, shockwaveImpulse, inCorridor } from './rayneMath.js';

const close = (a, b, eps = 1e-6) => assert.ok(Math.abs(a - b) <= eps, `${a} != ${b}`);

test('Overcharge: +50% speed / +20% radius reached after 2 s of charge, linear before', () => {
  close(overchargeFraction(0, 2), 0);
  close(overchargeFraction(1, 2), 0.5);
  close(overchargeFraction(5, 2), 1);
  const m = overchargeMultipliers(2, 2, 0.5, 0.2);
  close(m.speed, 1.5); close(m.radius, 1.2);
  const h = overchargeMultipliers(1, 2, 0.5, 0.2);
  close(h.speed, 1.25); close(h.radius, 1.1);
  const z = overchargeMultipliers(-1, 2, 0.5, 0.2);
  close(z.speed, 1); close(z.radius, 1);
});

test('shockwave falloff: 1 at the epicentre, edge fraction at the rim, 0 outside', () => {
  close(shockwaveFalloff(0, 3, 0.25), 1);
  close(shockwaveFalloff(3, 3, 0.25), 0.25);
  close(shockwaveFalloff(1.5, 3, 0.25), 0.625);
  close(shockwaveFalloff(3.01, 3, 0.25), 0);
  close(shockwaveFalloff(1, 0, 0.25), 0);
});

test('shockwave impulse is radial + upward and uses the flight direction on the epicentre', () => {
  const out = { x: 0, y: 0, z: 0 };
  const f = shockwaveImpulse(1.5, 0, 3, 7, 0.25, 1.8, 0, 1, out);
  close(f, 0.625);
  close(out.x, 7 * 0.625); close(out.z, 0); close(out.y, 1.8 * 0.625);
  shockwaveImpulse(0, 0, 3, 7, 0.25, 1.8, 0, -2, out);
  close(out.x, 0); close(out.z, -7); close(out.y, 1.8);
  const none = shockwaveImpulse(4, 0, 3, 7, 0.25, 1.8, 0, 1, out);
  close(none, 0); close(out.x, 0); close(out.y, 0);
});

test('corridor test for lined-up enemies', () => {
  const from = { x: 0, z: 0 };
  assert.equal(inCorridor(from, 0, 1, { x: 0.5, z: 6 }, 0.9), true);
  assert.equal(inCorridor(from, 0, 1, { x: 1.5, z: 6 }, 0.9), false);
  assert.equal(inCorridor(from, 0, 1, { x: 0, z: -2 }, 0.9), false);
});
