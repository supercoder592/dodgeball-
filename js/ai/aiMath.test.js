// node --test docs/js/ai/  (pure logic: no three.js, no game state)
import test from 'node:test';
import assert from 'node:assert/strict';
import { Rng } from '../core/rng.js';
import {
  timeToCover, predictCapsuleImpact, interceptPoint, planCatchLead, chooseSidestep, roomAlong, planarDistanceToBounds,
  shrinkBounds, clampPlanar, keepInside, seek, perlin1D, weightedPick, planarAngleDeg, edgeProximity, jitter,
} from './aiMath.js';
import { BOT_DIFFICULTY, DIFFICULTY_IDS, createProfile, heroTraits, normalizeDifficulty } from './difficulty.js';

const close = (a, b, eps = 1e-3) => assert.ok(Math.abs(a - b) <= eps, `${a} != ${b} (eps ${eps})`);
const v = (x, y, z) => ({ x, y, z });
const HOME_INFIELD = { minX: -4.15, maxX: 4.15, minZ: -8.65, maxZ: -0.35 };

test('difficulty presets follow the spec ranges and get monotonically better', () => {
  assert.deepEqual([...DIFFICULTY_IDS], ['easy', 'normal', 'hard', 'pro']);
  close(BOT_DIFFICULTY.easy.reactionTime, 0.45);
  close(BOT_DIFFICULTY.pro.reactionTime, 0.12);
  close(BOT_DIFFICULTY.easy.catchTimingSigma, 0.12);
  close(BOT_DIFFICULTY.pro.catchTimingSigma, 0.03);
  const tiers = DIFFICULTY_IDS.map((id) => BOT_DIFFICULTY[id]);
  for (let i = 1; i < tiers.length; i++) {
    const a = tiers[i - 1], b = tiers[i];
    assert.ok(b.reactionTime < a.reactionTime, 'reaction');
    assert.ok(b.aimErrorDeg < a.aimErrorDeg, 'aim error');
    assert.ok(b.catchTimingSigma < a.catchTimingSigma, 'catch sigma');
    assert.ok(b.catchAttemptProbability > a.catchAttemptProbability, 'catch attempts');
    assert.ok(b.dodgeSkill > a.dodgeSkill, 'dodge');
    assert.ok(b.abilityUsageFactor > a.abilityUsageFactor, 'ability usage');
    assert.ok(b.decisionInterval < a.decisionInterval, 'decision interval');
  }
  for (const t of tiers) close(t.idealCatchLead, 0.08);
});

test('profiles are mutable copies; unknown ids fall back to normal; partial custom profiles layer over a base', () => {
  const p = createProfile('HARD');
  p.aimErrorDeg = 99;
  assert.notEqual(BOT_DIFFICULTY.hard.aimErrorDeg, 99);
  assert.equal(normalizeDifficulty('nightmare'), 'normal');
  assert.equal(normalizeDifficulty(undefined), 'normal');
  const c = createProfile({ base: 'pro', aimErrorDeg: 3, bogus: 1 });
  assert.equal(c.aimErrorDeg, 3);
  assert.equal(c.reactionTime, BOT_DIFFICULTY.pro.reactionTime);
  assert.equal(c.bogus, undefined);
});

test('hero traits: Rayne charges longer, Bear catches more, Specter has Danger Sense', () => {
  assert.ok(heroTraits('Rayne').chargeTimeMax >= 2);
  assert.ok(heroTraits('Bear').catchBias > 0.2);
  assert.equal(heroTraits('Specter').hasDangerSense, true);
  assert.equal(heroTraits('Nobody').chargeTimeMax, 0);
});

test('catch lead: pro presses almost always inside the perfect window, easy far less often', () => {
  const rate = (tier, n = 4000) => {
    const rng = new Rng(42);
    const prof = BOT_DIFFICULTY[tier];
    let perfect = 0;
    for (let i = 0; i < n; i++) {
      const lead = planCatchLead(rng, prof.idealCatchLead, prof.catchTimingSigma, 0.15, 0.4);
      if (lead >= 0 && lead <= 0.15) perfect++;
    }
    return perfect / n;
  };
  const pro = rate('pro'), easy = rate('easy');
  assert.ok(pro > 0.95, `pro perfect rate ${pro}`);
  assert.ok(easy < 0.6 && easy > 0.3, `easy perfect rate ${easy}`);
  // Iron Mitts (0.225 s) centres the press in the wider window.
  const rng = new Rng(3);
  let sum = 0;
  for (let i = 0; i < 2000; i++) sum += planCatchLead(rng, 0.08, 0, 0.225, 0.4);
  close(sum / 2000, 0.12, 1e-6);
});

test('timeToCover follows a trapezoid velocity profile', () => {
  close(timeToCover(0, 0, 34, 7.4), 0);
  // From rest, 0.5 m at 34 m/s^2 (still accelerating: top speed needs 0.8 m): t = sqrt(2d/a)
  close(timeToCover(0.5, 0, 34, 7.4), Math.sqrt(1 / 34));
  // From rest, 1 m: accelerate 0.2176 s over 0.805 m, then cruise the rest at 7.4 m/s.
  close(timeToCover(1, 0, 34, 7.4), 7.4 / 34 + (1 - 0.5 * 7.4 * 7.4 / 34) / 7.4);
  // Already at top speed: pure cruise.
  close(timeToCover(7.4, 7.4, 34, 7.4), 1);
});

test('capsule impact prediction: straight fastball hits the chest at the analytic time', () => {
  const out = v(0, 0, 0);
  // Ball 10 m in front of the body at chest height flying at 25 m/s, no gravity.
  const t = predictCapsuleImpact(v(0, 1.3, 10), v(0, 0, -25), 0, 0.105, v(0, 0, 0), 0.32, 1.8, 2, out);
  close(t, (10 - 0.32 - 0.105) / 25, 1e-3);
  close(out.z, 0.425, 0.03);
  // Passing 1 m to the side: no hit.
  assert.equal(predictCapsuleImpact(v(1, 1.3, 10), v(0, 0, -25), 0, 0.105, v(0, 0, 0), 0.32, 1.8, 2, out), -1);
  // Lobbed into the floor before reaching the body: no hit (a floor bounce ends a live throw).
  assert.equal(predictCapsuleImpact(v(0, 0.3, 10), v(0, -3, -5), 9.81, 0.105, v(0, 0, 0), 0.32, 1.8, 3, out), -1);
  // Low ball at shin height still hits (capsule bottom is rounded but reaches the floor region).
  assert.ok(predictCapsuleImpact(v(0, 0.3, 6), v(0, 0, -20), 0, 0.105, v(0, 0, 0), 0.32, 1.8, 2, out) > 0);
});

test('intercept point leads a moving target and caps the lead', () => {
  const out = v(0, 0, 0);
  const t = interceptPoint(v(0, 1.3, 0), v(0, 1.3, 10), v(4, 0, 0), 20, out);
  assert.ok(out.x > 1.5 && out.x < 2.5, `lead x ${out.x}`);
  close(t, Math.hypot(out.x, 10) / 20, 1e-3);
  interceptPoint(v(0, 1.3, 0), v(0, 1.3, 10), v(100, 0, 0), 5, out, 12);
  assert.ok(Math.abs(out.x) <= 12 + 1e-6);
});

test('sidestep picks the side needing less movement, and the side with room near a wall', () => {
  const dir = v(0, 0, 0);
  // Ball flying toward -Z along x = 0.1; body at x = 0.3 is right of... compute: path (0,0,-1), right = (-1,0,0).
  // Body offset along right = (0.3-0.1)*-1 = -0.2 -> left of the path -> stepping left (+X) needs less.
  const need = chooseSidestep(v(0.1, 1.3, -1), v(0, 0, -20), v(0.3, 0, -5), 0, 1, 0.75, HOME_INFIELD, dir);
  assert.ok(dir.x > 0.99, `dir.x ${dir.x}`);
  close(need, 0.55);
  // Same geometry pinned against the +X sideline: must go the other way.
  const wall = { ...HOME_INFIELD, maxX: 0.4 };
  chooseSidestep(v(0.1, 1.3, -1), v(0, 0, -20), v(0.3, 0, -5), 0, 1, 0.75, wall, dir);
  assert.ok(dir.x < -0.99, `dir.x ${dir.x}`);
});

test('planar bounds helpers', () => {
  close(roomAlong(0, -4, 1, 0, HOME_INFIELD), 4.15);
  close(roomAlong(0, -4, 0, 1, HOME_INFIELD), 3.65);
  close(planarDistanceToBounds(v(0, 0, 0.6), HOME_INFIELD), 0.95);
  assert.equal(planarDistanceToBounds(v(0, 0, -3), HOME_INFIELD), 0);
  const s = shrinkBounds(HOME_INFIELD, 0.9, {});
  close(s.maxZ, -1.25); close(s.minX, -3.25);
  const tiny = shrinkBounds({ minX: 0, maxX: 1, minZ: 0, maxZ: 1 }, 5, {});
  close(tiny.minX, 0.5); close(tiny.maxX, 0.5);
  const c = clampPlanar(v(9, 2, 3), HOME_INFIELD, v(0, 0, 0));
  assert.deepEqual([c.x, c.y, c.z], [4.15, 2, -0.35]);
  const m = keepInside(v(4.1, 0, -5), v(1, 0, 0.5), HOME_INFIELD, 0.1);
  assert.equal(m.x, 0); assert.equal(m.z, 0.5);
  close(edgeProximity(0, -4.5, HOME_INFIELD), 0);
  close(edgeProximity(4.15, -4.5, HOME_INFIELD), 1);
});

test('seek eases off near the destination and stops inside the arrive radius', () => {
  const out = v(0, 0, 0);
  const d = seek(v(0, 0, 0), v(0, 0, 10), 0.35, 1.4, out);
  close(d, 10); close(out.z, 1);
  seek(v(0, 0, 0), v(0, 0, 0.2), 0.35, 1.4, out);
  assert.equal(out.z, 0);
  seek(v(0, 0, 0), v(0, 0, 0.5), 0.35, 1.4, out);
  close(out.z, 0.3);
});

test('noise, jitter, roulette and angles', () => {
  for (let x = 0; x < 50; x += 0.37) { const n = perlin1D(12.3, x); assert.ok(n >= -1 && n <= 1); }
  close(perlin1D(5, 3), perlin1D(5, 3)); // deterministic
  assert.ok(Math.abs(perlin1D(5, 3.001) - perlin1D(5, 3)) < 0.01); // smooth
  const rng = new Rng(9);
  for (let i = 0; i < 200; i++) { const j = jitter(rng, 1, 0.3); assert.ok(j >= 0.7 && j <= 1.3); }
  assert.equal(weightedPick([0, 5, 0], 3, 0.99), 1);
  assert.equal(weightedPick([1, 1], 2, 0.2), 0);
  assert.equal(weightedPick([1, 1], 2, 0.8), 1);
  close(planarAngleDeg(0, 1, 1, 0), 90);
  close(planarAngleDeg(0, 1, 0, -3), 180);
});
