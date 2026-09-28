// Unit tests for the empowered-throw recipe (node --test).
import test from 'node:test';
import assert from 'node:assert/strict';
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { applyEmpower, empowerChargeSeconds } from './empowerMath.js';

const base = () => ({ speedMul: 1.1, radiusMul: 1, gravityScale: 0.65, unblockable: false, pierce: false, payload: null,
  isAbility: false, isPass: false, rallyCount: 3, style: 'standard' });

test('applyEmpower: style, multipliers, gravity and flags', () => {
  const payload = {};
  const p = applyEmpower(base(), { style: 'beam', speedMul: 2, radiusMul: 1.3, gravityScale: 0, unblockable: true, pierce: true, payload });
  assert.equal(p.style, 'beam');
  assert.ok(Math.abs(p.speedMul - 2.2) < 1e-12);
  assert.ok(Math.abs(p.radiusMul - 1.3) < 1e-12);
  assert.equal(p.gravityScale, 0);
  assert.equal(p.unblockable, true);
  assert.equal(p.pierce, true);
  assert.equal(p.payload, payload);
  assert.equal(p.isAbility, true);
  assert.equal(p.isPass, false);
});

test('applyEmpower: keeps the rally count unless keepRally is false', () => {
  assert.equal(applyEmpower(base(), { style: 'meteor' }).rallyCount, 3);
  assert.equal(applyEmpower(base(), { style: 'meteor', keepRally: false }).rallyCount, 0);
});

test('applyEmpower: defaults (gravity 0.5, standard style, ignores non-positive multipliers)', () => {
  const p = applyEmpower(base(), { speedMul: 0, radiusMul: -1 });
  assert.equal(p.style, 'standard');
  assert.equal(p.gravityScale, 0.5);
  assert.ok(Math.abs(p.speedMul - 1.1) < 1e-12);
  assert.equal(p.radiusMul, 1);
  assert.equal(p.payload, null);
});

test('empowerChargeSeconds: at least a full charge', () => {
  assert.equal(empowerChargeSeconds(0, 0.75), 0.75);
  assert.equal(empowerChargeSeconds(1.6, 0.75), 1.6);
  assert.equal(empowerChargeSeconds(undefined, undefined), 0.75);
});

// Single-ball rule guard: no hero ability may conjure a ball any more.
test('abilities never call throwAbilityBall( or spawnAbilityBall(', () => {
  const root = join(dirname(fileURLToPath(import.meta.url)), '..');
  const offenders = [];
  const walk = (dir) => {
    for (const name of readdirSync(dir)) {
      const f = join(dir, name);
      if (statSync(f).isDirectory()) { walk(f); continue; }
      if (!name.endsWith('.js') || name.endsWith('.test.js') || name === 'abilityUtil.js') continue;
      const src = readFileSync(f, 'utf8').replace(/\/\/.*$/gm, '').replace(/\/\*[\s\S]*?\*\//g, '');
      if (/\bthrowAbilityBall\s*\(|\bspawnAbilityBall\s*\(/.test(src)) offenders.push(f);
    }
  };
  walk(root);
  assert.deepEqual(offenders, []);
});
