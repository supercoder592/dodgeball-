// node --test docs/js/world/   (pure U-outfield geometry; must not import three)
import test from 'node:test';
import assert from 'node:assert/strict';
import {
  regionContainsXZ, regionClampXZ, regionDistanceXZ, insetRegion, uPathGeometry, uPointXZ, uParamXZ, zoneAtXZ,
  clampOutOfHole,
} from './courtMath.js';
import { TEAM, ZONE, COURT } from '../core/constants.js';

const close = (a, b, eps = 1e-6) => assert.ok(Math.abs(a - b) <= eps, `${a} != ${b} (eps ${eps})`);
/** Home's U confinement (inset 0.35). */
const HOME_U = () => ({ minX: -6.65, maxX: 6.65, minZ: 0.35, maxZ: 11.65, hole: { minX: -4.85, maxX: 4.85, minZ: -1.35, maxZ: 9.35 } });
const DIMS = { halfW: 4.5, halfL: 9, outerHalfW: 7, outerHalfL: 12 };
const G = () => uPathGeometry(4.5, 9, 2.5, 3, 0.8);

test('regionClamp: hole points go to the nearest exit edge, never through the open side', () => {
  const b = HOME_U(), o = { x: 0, z: 0 };
  regionClampXZ(b, 0, 4.5, o); close(o.x, -4.85); close(o.z, 4.5);
  regionClampXZ(b, 0, 8.9, o); close(o.x, 0); close(o.z, 9.35);
  regionClampXZ(b, 0, -0.5, o); close(o.x, -4.85); close(o.z, 0.35);
  for (const [x, z] of [[-5.75, 4], [5.75, 1], [0, 10.5], [-6.5, 11.5], [6.6, 0.4]]) {
    regionClampXZ(b, x, z, o); close(o.x, x); close(o.z, z);
    assert.ok(regionContainsXZ(b, x, z));
  }
  // Hole edges and outer boundary are inside (strict hole test, inclusive outer box).
  assert.ok(regionContainsXZ(b, -4.85, 5));
  assert.ok(regionContainsXZ(b, 0, 9.35));
  assert.ok(regionContainsXZ(b, 6.65, 11.65));
  assert.ok(!regionContainsXZ(b, 0, 5));
  assert.ok(!regionContainsXZ(b, 7, 5));
  // Beyond the outer box: plain clamp.
  regionClampXZ(b, 9, 13, o); close(o.x, 6.65); close(o.z, 11.65);
  // A plain box (no hole) is a normal clamp.
  regionClampXZ({ minX: -1, maxX: 1, minZ: -1, maxZ: 1, hole: null }, 3, 0.2, o); close(o.x, 1); close(o.z, 0.2);
});

test('regionDistance: 0 inside, exact distance to the U', () => {
  const b = HOME_U();
  close(regionDistanceXZ(b, 0, 4.5), 4.85);
  close(regionDistanceXZ(b, 4.3, 3), 0.55);
  close(regionDistanceXZ(b, 5.5, 3), 0);
  close(regionDistanceXZ(b, 7.65, 3), 1);
});

test('insetRegion: outer box shrinks, hole grows; plain boxes keep hole null', () => {
  const painted = { minX: -7, maxX: 7, minZ: 0, maxZ: 12, hole: { minX: -4.5, maxX: 4.5, minZ: -1, maxZ: 9 } };
  const r = insetRegion(painted, 0.35, {});
  close(r.minX, -6.65); close(r.maxX, 6.65); close(r.minZ, 0.35); close(r.maxZ, 11.65);
  close(r.hole.minX, -4.85); close(r.hole.maxX, 4.85); close(r.hole.minZ, -1.35); close(r.hole.maxZ, 9.35);
  close(painted.hole.minX, -4.5, 0); // source untouched
  const self = insetRegion(painted, 0.35, painted); // in place
  close(self.hole.maxZ, 9.35);
  const box = insetRegion({ minX: -4.5, maxX: 4.5, minZ: -9, maxZ: 0, hole: null }, 0.35, {});
  assert.equal(box.hole, null); close(box.maxZ, -0.35);
});

test('zoneAt: infield halves, U outfields, run-off', () => {
  const o = { team: -1, zone: '' };
  const z = (x, zz, m = 0) => { const r = zoneAtXZ(x, zz, DIMS, o, m); return r ? `${r.team}:${r.zone}` : null; };
  const H = TEAM.HOME, A = TEAM.AWAY, I = ZONE.INFIELD, O = ZONE.OUTFIELD;
  assert.equal(z(0, -3), `${H}:${I}`);
  assert.equal(z(0, 3), `${A}:${I}`);
  assert.equal(z(5.5, 3), `${H}:${O}`);
  assert.equal(z(5.5, -3), `${A}:${O}`);
  assert.equal(z(-6.9, 11.9), `${H}:${O}`);
  assert.equal(z(0, 10), `${H}:${O}`);
  assert.equal(z(0, -10), `${A}:${O}`);
  assert.equal(z(7.3, 0), null);
  assert.equal(z(0, 12.5), null);
  assert.equal(z(0, 0), `${A}:${I}`);
  assert.equal(z(5, 0), `${H}:${O}`);
  assert.equal(z(7.3, 0, 0.5), `${H}:${O}`);
});

test('U path: arc-length parametrisation, corners, mirror, round trip', () => {
  const g = G(), o = { x: 0, z: 0 };
  close(g.length, 30.9);
  uPointXZ(g, 1, 0, o); close(o.x, -5.75); close(o.z, 0.8);
  uPointXZ(g, 1, 9.7 / 30.9, o); close(o.x, -5.75); close(o.z, 10.5);
  uPointXZ(g, 1, 0.3139, o); close(o.x, -5.75, 1e-3); close(o.z, 10.5, 1e-2);
  uPointXZ(g, 1, 0.5, o); close(o.x, 0); close(o.z, 10.5);
  uPointXZ(g, 1, 1, o); close(o.x, 5.75); close(o.z, 0.8);
  uPointXZ(g, -1, 0.5, o); close(o.x, 0); close(o.z, -10.5);
  uPointXZ(g, -1, 0, o); close(o.x, -5.75); close(o.z, -0.8);
  for (let i = 0; i <= 100; i++) {
    const u = i / 100;
    for (const s of [1, -1]) {
      uPointXZ(g, s, u, o);
      close(uParamXZ(g, s, o.x, o.z), u, 1e-9);
      assert.ok(regionContainsXZ(HOME_U(), o.x, s * o.z), `u=${u} inside the confinement`);
    }
  }
  close(uParamXZ(g, 1, 1, 3), 28.7 / 30.9, 1e-9);
  close(uParamXZ(g, 1, 1, 3), 0.929, 1e-3);
  assert.equal(COURT.sideOutfieldWidth, 2.5);
});

/** Motor-like step: fixed wish velocity, predictive clamp, integrate, outer box clamp, resolve. */
function run(state, vx, vz, steps, dt, b, each) {
  for (let i = 0; i < steps; i++) {
    state.vx = vx; state.vz = vz;
    clampOutOfHole(state, b.hole, b, dt);
    state.px += state.vx * dt; state.pz += state.vz * dt;
    state.px = Math.min(b.maxX, Math.max(b.minX, state.px));
    state.pz = Math.min(b.maxZ, Math.max(b.minZ, state.pz));
    clampOutOfHole(state, b.hole, b, 0);
    each(state);
  }
}

test('clampOutOfHole: a fast slide stops on the hole wall', () => {
  const b = HOME_U(), st = { px: -5.5, pz: 5, vx: 0, vz: 0 };
  run(st, 12, 0, 60, 1 / 60, b, (s) => assert.ok(s.px <= -4.85 + 1e-9, `x=${s.px}`));
  close(st.px, -4.85);
});

test('clampOutOfHole: a diagonal run from the back strip slides along z = 9.35 and turns down the arm', () => {
  const b = HOME_U(), st = { px: -3, pz: 10.5, vx: 0, vz: 0 };
  let slid = false;
  run(st, -4, -4, 120, 1 / 60, b, (s) => {
    assert.ok(regionContainsXZ(b, s.px, s.pz), `(${s.px}, ${s.pz}) in the U`);
    if (s.px > -4.85) assert.ok(s.pz >= 9.35 - 1e-9);
    if (Math.abs(s.pz - 9.35) < 1e-9 && s.px < -4) slid = true;
  });
  assert.ok(slid, 'slid along the back wall');
  assert.ok(st.pz < 9, `turned down the arm (z=${st.pz})`);
  assert.ok(st.px <= -4.85);
});

test('clampOutOfHole: resolve pushes out and removes the inward velocity; no hole = no-op', () => {
  const b = HOME_U();
  const st = { px: -4.7, pz: 5, vx: 3, vz: 1 };
  assert.ok(clampOutOfHole(st, b.hole, b, 0));
  close(st.px, -4.85); assert.equal(st.vx, 0); assert.equal(st.vz, 1);
  const st2 = { px: 0, pz: 9.2, vx: 0, vz: -2 };
  clampOutOfHole(st2, b.hole, b, 0);
  close(st2.pz, 9.35); assert.equal(st2.vz, 0);
  const st3 = { px: 0, pz: 5, vx: 1, vz: 1 };
  assert.equal(clampOutOfHole(st3, null, b, 1 / 60), false);
  assert.deepEqual(st3, { px: 0, pz: 5, vx: 1, vz: 1 });
});
