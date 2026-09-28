// node --test docs/js/ui/format.test.js
import test from 'node:test';
import assert from 'node:assert/strict';
import { formatClock, formatCooldown, cssColor, chargeModel, causeLabel, prettyAssetLabel, failLabel } from './format.js';

test('formatClock', () => {
  assert.equal(formatClock(150), '2:30');
  assert.equal(formatClock(59.2), '1:00');
  assert.equal(formatClock(10), '0:10');
  assert.equal(formatClock(9.41), '9.5');
  assert.equal(formatClock(0), '0:00');
  assert.equal(formatClock(-3), '0:00');
  assert.equal(formatClock(NaN), '0:00');
});

test('formatCooldown', () => {
  assert.equal(formatCooldown(0), '');
  assert.equal(formatCooldown(2.44), '2.4');
  assert.equal(formatCooldown(7.2), '8');
});

test('cssColor', () => {
  assert.equal(cssColor(0xff6a2b), '#ff6a2b');
  assert.equal(cssColor(0x00ff), '#0000ff');
  assert.equal(cssColor('red'), 'red');
  assert.equal(cssColor(undefined, '#123'), '#123');
});

test('chargeModel without overcharge', () => {
  const m = chargeModel(0.5, 0.4, 0.75, 0);
  assert.equal(m.base, 0.5);
  assert.equal(m.over, 0);
});

test('chargeModel overcharge fills after the normal charge up to the passive time', () => {
  const mid = chargeModel(1, 1.375, 0.75, 2);
  assert.equal(mid.base, 1);
  assert.ok(Math.abs(mid.over - 0.5) < 1e-9);
  assert.ok(Math.abs(mid.bonus - 0.6875) < 1e-9);
  const full = chargeModel(1, 3, 0.75, 2);
  assert.equal(full.over, 1);
  assert.equal(full.bonus, 1);
  const early = chargeModel(0.4, 0.3, 0.75, 2);
  assert.equal(early.over, 0);
});

test('labels', () => {
  assert.equal(causeLabel('hit'), '');
  assert.match(causeLabel('caught'), /CAUGHT/);
  assert.match(prettyAssetLabel('heroes/rayne/model.glb'), /Rayne/);
  assert.match(prettyAssetLabel('anims/f/walk.glb'), /walk/);
  assert.match(failLabel('cooldown'), /COOLDOWN/);
  assert.match(failLabel('whatever'), /NOT NOW/);
});
