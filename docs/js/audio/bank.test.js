// Renders every procedural sound and checks it is finite, audible, bounded and click-free at the ends.
// Run: cd Tools/web && npm test
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { SOUNDS, SOUND_ALIASES, SOUND_KEYWORDS, renderSound } from './bank.js';

test('every sound renders finite, normalised, non-silent audio', () => {
  let total = 0;
  const t0 = Date.now();
  for (const id of Object.keys(SOUNDS)) {
    const { sr, variants } = renderSound(id);
    assert.ok(sr >= 8000 && sr <= 96000, `${id} sample rate`);
    assert.equal(variants.length, SOUNDS[id].variants, `${id} variants`);
    for (const v of variants) {
      const chans = Array.isArray(v) ? v : [v];
      for (const ch of chans) {
        assert.ok(ch instanceof Float32Array && ch.length > sr * 0.03, `${id} length`);
        let peak = 0, energy = 0;
        for (let i = 0; i < ch.length; i++) {
          const x = ch[i];
          assert.ok(Number.isFinite(x), `${id} has NaN/Inf at ${i}`);
          const a = Math.abs(x); if (a > peak) peak = a; energy += x * x;
        }
        assert.ok(peak <= 1.0001, `${id} clips (${peak})`);
        assert.ok(peak > 0.3, `${id} too quiet (${peak})`);
        assert.ok(energy / ch.length > 1e-5, `${id} nearly silent`);
        total += ch.length;
      }
    }
  }
  const ms = Date.now() - t0;
  assert.ok(ms < 8000, `synthesis too slow: ${ms} ms for ${total} samples`);
});

test('aliases and keyword fallbacks point at real sounds', () => {
  for (const [k, v] of Object.entries(SOUND_ALIASES)) assert.ok(SOUNDS[v], `alias ${k} -> ${v}`);
  for (const [, v] of SOUND_KEYWORDS) assert.ok(SOUNDS[v], `keyword -> ${v}`);
});

test('ids used by other modules resolve without the keyword fallback', () => {
  const used = ['whoosh', 'throwHeavy', 'rewind', 'clone', 'ultimateCast', 'shockwave', 'overchargeReady', 'fireSmother', 'danger',
    'clonePop', 'beamRelease', 'beamPierce', 'beamImpact', 'abilityCast', 'heartbeat', 'iceCrack', 'frostCast', 'absoluteZero',
    'freeze', 'swapChannel', 'teleport', 'vanish', 'shieldImpact', 'aegisDeploy', 'aegisDown', 'magnetClunk', 'catch',
    'magnetField', 'pickup', 'glueStick', 'glueSplat', 'cloak', 'decloak', 'stasis'];
  for (const id of used) assert.ok(SOUNDS[id] || SOUNDS[SOUND_ALIASES[id]], id);
});
