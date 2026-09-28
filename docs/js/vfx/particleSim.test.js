// Unit tests for the CPU particle simulation (no three.js). Run: cd Tools/web && npm test
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { ParticleSim, ParticleSpec, FLOOR } from './particleSim.js';

const out = (cap) => ({ posSize: new Float32Array(cap * 4), color: new Float32Array(cap * 4), vel: new Float32Array(cap * 4), rotFrame: new Float32Array(cap * 2) });

test('particles die at end of life and are swap-removed', () => {
  const sim = new ParticleSim(8), o = out(8), s = new ParticleSpec();
  for (let i = 0; i < 5; i++) { s.reset(); s.life = 0.1 * (i + 1); s.x = i; sim.spawn(s); }
  assert.equal(sim.count, 5);
  sim.update(0.25, 0, 0, o); // lives 0.1, 0.2 die
  assert.equal(sim.count, 3);
  for (let i = 0; i < sim.count; i++) assert.ok(sim.life[i] > 0.25);
  sim.update(1, 0, 0, o);
  assert.equal(sim.count, 0);
});

test('saturated pool recycles slots instead of growing', () => {
  const sim = new ParticleSim(4), s = new ParticleSpec();
  for (let i = 0; i < 10; i++) { s.reset(); s.life = 5; sim.spawn(s); }
  assert.equal(sim.count, 4);
});

test('gravity, bounce restitution and stick on the floor', () => {
  const sim = new ParticleSim(4), o = out(4), s = new ParticleSpec();
  s.reset().at(0, 1, 0); s.grav = 1; s.drag = 0; s.bounce = 0.5; s.life = 10; s.size0 = s.size1 = 0.02; sim.spawn(s);
  s.reset().at(1, 1, 0); s.grav = 1; s.drag = 0; s.bounce = FLOOR.STICK; s.life = 10; s.size0 = s.size1 = 0.02; s.vx = 3; sim.spawn(s);
  let maxBounceY = 0, touched = false;
  for (let k = 0; k < 240; k++) {
    sim.update(1 / 60, k / 60, 0, o);
    const y0 = sim.p[1];
    if (y0 <= 0.0061) touched = true;
    if (touched) maxBounceY = Math.max(maxBounceY, y0);
    assert.ok(sim.p[1] >= 0.005 && sim.p[4] >= 0.005, 'never below the floor');
  }
  assert.ok(touched && maxBounceY > 0.1 && maxBounceY < 0.5, `bounced to ${maxBounceY}`);
  assert.equal(sim.v[3], 0); // stuck particle stopped
});

test('orbit keeps radius when pull is 0 and alpha fades to zero', () => {
  const sim = new ParticleSim(2), o = out(2), s = new ParticleSpec();
  s.reset().at(2, 0.5, 0); s.orbitW = 3; s.cx = 0; s.cz = 0; s.life = 1; s.fadeIn = 0; s.fadePow = 1; s.alpha = 1; sim.spawn(s);
  for (let k = 0; k < 50; k++) sim.update(1 / 60, 0, -10, o);
  assert.ok(Math.abs(Math.hypot(sim.p[0], sim.p[2]) - 2) < 1e-3);
  assert.ok(o.color[3] < 0.2 && o.color[3] > 0);
  assert.ok(Math.hypot(o.vel[0], o.vel[2]) > 5, 'derived orbit velocity for streaks');
});

test('paused update (dt = 0) does not rewrite unless something spawned', () => {
  const sim = new ParticleSim(2), o = out(2), s = new ParticleSpec();
  s.reset(); sim.spawn(s);
  assert.equal(sim.update(0, 0, 0, o), true);
  assert.equal(sim.update(0, 0, 0, o), false);
});
