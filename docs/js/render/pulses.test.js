// Unit tests for the screen pulse model (node:test, no three.js).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { ScreenPulses, pulseEnvelope, PULSE_LIMITS, PULSE_PROFILES } from './pulses.js';
import { QUALITY_PRESETS, qualityId, qualityPreset } from './quality.js';

test('envelope rises quickly, peaks at 1 and decays to 0 at the duration', () => {
  assert.equal(pulseEnvelope(0, 0.3), 0);
  const attack = Math.min(0.035, 0.3 * 0.2);
  assert.ok(Math.abs(pulseEnvelope(attack, 0.3) - 1) < 1e-9);
  assert.ok(pulseEnvelope(0.2, 0.3) < pulseEnvelope(0.1, 0.3));
  assert.equal(pulseEnvelope(0.3, 0.3), 0);
  assert.equal(pulseEnvelope(0.1, 0), 0);
});

test('a hit pulse adds flash and chromatic aberration, then fully decays', () => {
  const p = new ScreenPulses();
  assert.equal(p.pulse('hit', 1, 0.2), true);
  p.update(0.02);
  assert.ok(p.values.flash > 0);
  assert.ok(p.values.ca > 0);
  for (let i = 0; i < 20; i++) p.update(0.02);
  assert.equal(p.values.flash, 0);
  assert.equal(p.activeCount, 0);
});

test('unknown types and invalid arguments are ignored', () => {
  const p = new ScreenPulses();
  assert.equal(p.pulse('nope', 1, 1), false);
  assert.equal(p.pulse('hit', 0, 1), false);
  assert.equal(p.pulse('hit', 1, 0), false);
  assert.equal(p.setSustained('nope', 1), false);
});

test('stacked pulses are clamped per channel', () => {
  const p = new ScreenPulses(64);
  for (let i = 0; i < 60; i++) p.pulse('heavyHit', 3, 1);
  p.update(0.03);
  assert.ok(p.values.flash <= PULSE_LIMITS.flash[1]);
  assert.ok(p.values.ca <= PULSE_LIMITS.ca[1]);
  assert.ok(p.values.saturation >= PULSE_LIMITS.saturation[0]);
});

test('capacity overflow recycles a slot instead of growing', () => {
  const p = new ScreenPulses(4);
  for (let i = 0; i < 10; i++) assert.equal(p.pulse('hit', 1, 1), true);
  assert.equal(p.activeCount, 4);
});

test('sustained danger approaches its target and releases smoothly', () => {
  const p = new ScreenPulses();
  p.setSustained('danger', 1);
  for (let i = 0; i < 60; i++) p.update(1 / 60);
  assert.ok(p.values.danger > 0.99);
  p.setSustained('danger', 0);
  p.update(1 / 60);
  assert.ok(p.values.danger > 0.5 && p.values.danger < 1);
  for (let i = 0; i < 120; i++) p.update(1 / 60);
  assert.equal(p.values.danger, 0);
});

test('every profile only uses known channels and clear() resets everything', () => {
  const p = new ScreenPulses();
  for (const type of Object.keys(PULSE_PROFILES)) {
    for (const ch of Object.keys(PULSE_PROFILES[type])) assert.ok(ch in PULSE_LIMITS, `${type}.${ch}`);
    p.pulse(type, 1, 0.5);
  }
  p.setSustained('freeze', 1);
  p.update(0.05);
  p.clear();
  for (const v of Object.values(p.values)) assert.equal(v, 0);
  assert.equal(p.sustained('freeze'), 0);
});

test('quality presets are complete and ids are normalised', () => {
  assert.equal(qualityId('bogus'), 'high');
  assert.equal(qualityPreset('low'), QUALITY_PRESETS.low);
  assert.equal(QUALITY_PRESETS.low.ao, false);
  assert.equal(QUALITY_PRESETS.low.bloom, false);
  assert.ok(QUALITY_PRESETS.low.shadowMapSize < QUALITY_PRESETS.high.shadowMapSize);
  assert.equal(QUALITY_PRESETS.high.aa, 'smaa');
});
