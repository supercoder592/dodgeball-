// node --test docs/js/combat/   (pure math only: no three.js import)
import test from 'node:test';
import assert from 'node:assert/strict';
import {
  chargeCurve, chargeSecondsFor, chargeSpeedMul, computeFinalSpeed, solveLaunch, leadTarget, positionAt,
  firstTimeWithin, knockbackForSpeed, planarAngle, signedPlanarAngle, THROW_MATH, solveAtAngle, pickPassIndex,
  segmentLengthInRect,
} from './throwMath.js';
import {
  makeSweepHit, sweepSphereCapsuleY, sweepSphereSphere, sweepSphereAABB, sweepSpherePlaneY, bounceVelocity,
  distanceToVerticalSegment,
} from './sweep.js';
import { MAX_BALL_SPEED_MS, KMH_TO_MS, GRAVITY, BALL_RADIUS } from '../core/constants.js';

const close = (a, b, eps = 1e-4) => assert.ok(Math.abs(a - b) <= eps, `${a} != ${b} (eps ${eps})`);
const v = (x, y, z) => ({ x, y, z });

// ------------------------------------------------------------------ charge & speed
test('charge curve is monotonic ease-out from 0 to 1 at fullChargeTime', () => {
  close(chargeCurve(0, 0.75), 0);
  close(chargeCurve(0.75, 0.75), 1);
  close(chargeCurve(3, 0.75), 1);
  let prev = -1;
  for (let t = 0; t <= 0.75; t += 0.05) { const c = chargeCurve(t, 0.75); assert.ok(c >= prev); prev = c; }
  assert.ok(chargeCurve(0.375, 0.75) > 0.5, 'ease-out gives more than half the power at half time');
  for (const c of [0, 0.2, 0.5, 0.9, 1]) close(chargeCurve(chargeSecondsFor(c, 0.75), 0.75), c, 1e-9);
});

test('charge speed multiplier spans minChargeMul..1', () => {
  close(chargeSpeedMul(0, 0.72), 0.72);
  close(chargeSpeedMul(1, 0.72), 1);
  close(chargeSpeedMul(0.5, 0.72), 0.86);
});

test('final speed applies +10% per rally and caps at 220 km/h', () => {
  const base = 88 * KMH_TO_MS;
  close(computeFinalSpeed(base, 1, 0), base);
  close(computeFinalSpeed(base, 1.2, 0), base * 1.2);
  close(computeFinalSpeed(base, 1, 3), base * 1.3);
  close(computeFinalSpeed(base, 2, 10), MAX_BALL_SPEED_MS);
  assert.ok(computeFinalSpeed(base, 1, 50) <= MAX_BALL_SPEED_MS + 1e-9);
});

// ------------------------------------------------------------------ ballistics
test('low-arc launch passes through the aim point', () => {
  const o = v(0, 1.5, -6), t = v(2, 1.3, 7);
  const g = GRAVITY * 0.65;
  const out = {};
  solveLaunch(o, t, 24, g, out);
  assert.ok(out.ok);
  close(Math.hypot(out.x, out.y, out.z), 24, 1e-6);
  const p = positionAt(o, out, g, out.time, {});
  close(p.x, t.x, 1e-3); close(p.y, t.y, 1e-3); close(p.z, t.z, 1e-3);
  assert.ok(out.y > 0 && out.y < 5, 'a fast throw is nearly flat');
});

test('out-of-range launch falls back to 45 degrees toward the point', () => {
  const out = {};
  solveLaunch(v(0, 1, 0), v(0, 1, 40), 8, GRAVITY, out);
  assert.equal(out.ok, false);
  close(Math.atan2(out.y, Math.hypot(out.x, out.z)), Math.PI / 4, 1e-6);
  assert.ok(out.z > 0);
});

test('lead intercepts a target moving at constant velocity', () => {
  const o = v(0, 1.6, -5), tp = v(-3, 1.3, 6), tv = v(4, 0, 0);
  const g = GRAVITY * 0.65;
  const out = {};
  leadTarget(o, tp, tv, 25, g, out);
  assert.ok(out.ok);
  const ball = positionAt(o, out, g, out.time, {});
  const target = v(tp.x + tv.x * out.time, tp.y, tp.z + tv.z * out.time);
  assert.ok(Math.hypot(ball.x - target.x, ball.y - target.y, ball.z - target.z) < 0.05, 'ball meets the moving target');
  assert.ok(out.time <= THROW_MATH.maxLeadTime);
});

test('firstTimeWithin finds the approach time of a ballistic path', () => {
  const p = v(0, 1.5, 0), vel = v(0, 0, 20), q = v(0, 1.5 - 0.5 * GRAVITY * 0.25, 10);
  const t = firstTimeWithin(p, vel, GRAVITY, q, 0.3, 2);
  assert.ok(t > 0.4 && t < 0.5, `t=${t}`);
  assert.equal(firstTimeWithin(p, vel, GRAVITY, v(5, 1.5, 10), 0.3, 2), -1);
});

test('knockback grows with speed and is bounded', () => {
  close(knockbackForSpeed(10 * KMH_TO_MS), THROW_MATH.knockbackMin);
  close(knockbackForSpeed(250 * KMH_TO_MS), THROW_MATH.knockbackMax);
  assert.ok(knockbackForSpeed(100 * KMH_TO_MS) > knockbackForSpeed(60 * KMH_TO_MS));
});

test('planar angles: unsigned and signed (right = positive)', () => {
  close(planarAngle(0, 1, 1, 0), Math.PI / 2);
  close(planarAngle(0, 1, 0, 1), 0);
  close(signedPlanarAngle(0, 1, -1, 0), Math.PI / 2);   // yaw 0 faces +Z, right is -X
  close(signedPlanarAngle(0, 1, 1, 0), -Math.PI / 2);
});

// ------------------------------------------------------------------ swept collisions
test('sphere sweep hits a vertical capsule head-on at the right time and normal', () => {
  const hit = makeSweepHit();
  // Capsule at the origin, radius 0.3, feet at 0, height 1.8 -> axis 0.3..1.5. Ball r = 0.105 at chest height.
  const ok = sweepSphereCapsuleY(v(0, 1.2, -2), v(0, 1.2, 2), BALL_RADIUS, 0, 0, 0.3, 1.5, 0.3, hit);
  assert.ok(ok);
  const contactZ = -(0.3 + BALL_RADIUS);
  close(hit.pz, contactZ, 1e-6);
  close(hit.t, (contactZ + 2) / 4, 1e-6);
  close(hit.nz, -1, 1e-6); close(hit.ny, 0, 1e-6);
});

test('capsule sweep: miss to the side, cap hit from above, start inside', () => {
  const hit = makeSweepHit();
  assert.equal(sweepSphereCapsuleY(v(1, 1, -2), v(1, 1, 2), 0.1, 0, 0, 0.3, 1.5, 0.3, hit), false);
  // Straight down onto the head: contact on the top cap.
  assert.ok(sweepSphereCapsuleY(v(0, 3, 0), v(0, 1.6, 0), 0.1, 0, 0, 0.3, 1.5, 0.3, hit));
  close(hit.py, 1.5 + 0.4, 1e-6); close(hit.ny, 1, 1e-6);
  // Already overlapping.
  assert.ok(sweepSphereCapsuleY(v(0.2, 1, 0), v(0.2, 1, 1), 0.1, 0, 0, 0.3, 1.5, 0.3, hit));
  assert.equal(hit.t, 0); close(hit.nx, 1, 1e-6);
  // Grazing just outside the combined radius misses.
  assert.equal(sweepSphereCapsuleY(v(0.41, 1, -2), v(0.41, 1, 2), 0.1, 0, 0, 0.3, 1.5, 0.3, hit), false);
});

test('sphere-sphere sweep (held-ball block / catch reach)', () => {
  const hit = makeSweepHit();
  assert.ok(sweepSphereSphere(v(-3, 1, 0), v(3, 1, 0), 0.1, v(0, 1, 0), 0.5, hit));
  close(hit.px, -0.6, 1e-6); close(hit.nx, -1, 1e-6);
  assert.equal(sweepSphereSphere(v(-3, 2, 0), v(3, 2, 0), 0.1, v(0, 1, 0), 0.5, hit), false);
});

test('AABB sweep: face hit, miss, start inside', () => {
  const hit = makeSweepHit();
  const min = v(5, 0, -10), max = v(6, 3, 10); // a wall at x = 5..6
  assert.ok(sweepSphereAABB(v(0, 1, 0), v(10, 1, 0), 0.1, min, max, hit));
  close(hit.px, 4.9, 1e-6); close(hit.nx, -1); close(hit.t, 0.49, 1e-6);
  assert.equal(sweepSphereAABB(v(0, 5, 0), v(10, 5, 0), 0.1, min, max, hit), false); // over the top
  assert.ok(sweepSphereAABB(v(5.05, 1, 0), v(5.5, 1, 0), 0.1, min, max, hit));
  assert.equal(hit.t, 0); close(hit.nx, -1);
});

test('floor plane sweep: landing, resting and rising balls', () => {
  const hit = makeSweepHit();
  assert.ok(sweepSpherePlaneY(v(0, 1, 0), v(0, -1, 0), 0.1, 0, hit));
  close(hit.py, 0.1, 1e-9); close(hit.t, 0.45, 1e-9); close(hit.ny, 1);
  assert.equal(sweepSpherePlaneY(v(0, 0.1, 0), v(1, 0.1, 0), 0.1, 0, hit), false, 'a rolling ball does not re-hit');
  assert.equal(sweepSpherePlaneY(v(0, 0.05, 0), v(0, 0.3, 0), 0.1, 0, hit), false, 'rising out of the floor');
});

test('bounce keeps restitution along the normal and friction along the surface', () => {
  const vel = v(4, -10, 0);
  const impact = bounceVelocity(vel, v(0, 1, 0), 0.55, 0.85);
  close(impact, 10); close(vel.y, 5.5); close(vel.x, 3.4);
  const sep = v(1, 2, 0);
  assert.equal(bounceVelocity(sep, v(0, 1, 0), 0.5, 0.5), 0);
  close(sep.y, 2);
  close(distanceToVerticalSegment(v(1, 5, 0), 0, 0, 0, 2), Math.hypot(1, 3));
});

// ------------------------------------------------------------------ passes
test('fixed-angle lob passes through the target (14.5 m at 34 degrees -> 12.4 m/s, apex ~2.45 m)', () => {
  const o = v(0, 1.4, -4), t = v(0, 1.4, 10.5), a = 34 * Math.PI / 180;
  const out = {};
  solveAtAngle(o, t, a, GRAVITY, out);
  assert.ok(out.ok);
  close(out.speed, 12.4, 0.05);
  const p = positionAt(o, out, GRAVITY, out.time, {});
  assert.ok(Math.hypot(p.x - t.x, p.y - t.y, p.z - t.z) < 0.01, 'lands on the target');
  const apex = (out.y * out.y) / (2 * GRAVITY);
  close(apex, 2.45, 0.02);
  // Uphill target: still exact.
  const t2 = v(3, 2.2, 6);
  solveAtAngle(o, t2, a, GRAVITY, out);
  const p2 = positionAt(o, out, GRAVITY, out.time, {});
  assert.ok(Math.hypot(p2.x - t2.x, p2.y - t2.y, p2.z - t2.z) < 0.01);
  // Unreachable at that angle (target above the line of fire) -> not ok; callers fall back (speed cap 24 m/s).
  solveAtAngle(o, v(0, 20, -3), a, GRAVITY, out);
  assert.equal(out.ok, false);
  solveAtAngle(o, v(0, 1.4, 60), a, GRAVITY, out);
  assert.ok(out.ok && out.speed > 24, 'a 64 m lob would exceed the 24 m/s pass cap');
});

test('pass receiver by aim: the teammate in the cone wins over a nearer one outside it', () => {
  const cands = [{ x: 3, z: 0 }, { x: 0, z: 12 }, { x: -2, z: -1 }];
  assert.equal(pickPassIndex(0, 0, 0, 1, cands), 1);
  assert.equal(pickPassIndex(0, 0, 1, 0, cands), 0);
  // Nobody in the cone: nearest.
  assert.equal(pickPassIndex(0, 0, 0, -1, [{ x: 5, z: 0 }, { x: 2, z: 1 }]), 1);
  assert.equal(pickPassIndex(0, 0, 0, 1, []), -1);
});

test('segment length inside a rectangle', () => {
  const half = { minX: -4.5, maxX: 4.5, minZ: 0, maxZ: 9 };
  close(segmentLengthInRect(0, -5, 0, 10.5, half), 9);
  close(segmentLengthInRect(-6, 2, 6, 2, half), 9);
  close(segmentLengthInRect(-6, -2, -6, 8, half), 0);
  close(segmentLengthInRect(0, 1, 0, 2, half), 1);
  // Enters at x = -4.5 (t = 1.25 / 5.75), leaves at z = 0 (t = 5 / 8).
  close(segmentLengthInRect(-5.75, 5, 0, -3, half), (5 / 8 - 1.25 / 5.75) * Math.hypot(5.75, 8));
});
