// ---------------------------------------------------------------------------------------------------------------
// Procedural sound bank (owner: fx). Every sound in the game is synthesised here from the dsp.js building blocks at
// load time - no audio files. Pure JS, rendered and sanity-checked in node by bank.test.js.
//
// SOUNDS[id] = { variants, sr?, gen(r, v, sr) -> Float32Array | [L, R],    // recipe (r = seeded rng, v = variant)
//                bus, gain, ref, rolloff, max, jitter, priority, spatial }  // playback metadata (audio.js)
//   bus: impact | sfx | ability | ui | match | crowd     gain: linear     ref: PannerNode refDistance (m)
//   max: concurrent voices of this id                     jitter: random +-pitch fraction per play
//   spatial: false = always 2D (UI, referee, scoreboard, crowd beds)
// Realism notes per recipe: a rubber dodgeball on a body is a low "thwump" (pitch-dropping sine body + skin slap
// band), on maple hardwood a bright thud plus the rubber shell's inharmonic "pang"; a pea whistle is ~2.8-3.2 kHz with
// ~40 Hz pea flutter; crowd beds are formant-filtered babble over pink/brown noise.
// ---------------------------------------------------------------------------------------------------------------
import {
  SR, rng, range, buf, addTone, addNoise, addSweep, addModal, addImpulses, addBabble, drive, normalize, fadeEdges,
  reverse, loopify, mixInto,
} from './dsp.js';

const CROWD_SR = 22050;
const pow = Math.pow, exp = Math.exp, sin = Math.sin, PI = Math.PI;

/** Swell envelope: (t/a)^k rise, then exponential decay. */
const swell = (a, k, tau) => (t) => (t < a ? pow(t / a, k) : exp(-(t - a) / tau));

// ------------------------------------------------------------------------------------------------ balls & bodies
function ballThump(r, v) {
  const k = 1 + (v - 1) * 0.08;
  const b = buf(0.32);
  addTone(b, { f0: 200 * k, f1: 92 * k, fTau: 0.024, amp: 0.95, attack: 0.001, decay: 0.07 });
  addNoise(b, r, { filters: [['bandpass', 1150 * k, 0.9]], amp: 0.6, attack: 0.0005, decay: 0.012 });
  addNoise(b, r, { filters: [['highpass', 3000, 0.7]], amp: 0.25, attack: 0.0003, decay: 0.003 });
  addTone(b, { f0: 430 * k, amp: 0.1, decay: 0.05, dur: 0.25 });
  return normalize(drive(b, 1.3), 0.9);
}

function heavyHit(r, v) {
  const b = buf(0.62);
  const k = 1 + v * 0.05;
  addTone(b, { f0: 210 * k, f1: 68 * k, fTau: 0.03, amp: 1, decay: 0.12 });
  addTone(b, { f0: 50, f1: 44, fTau: 0.1, amp: 0.55, attack: 0.004, decay: 0.2 });
  addNoise(b, r, { filters: [['lowpass', 1800, 0.7]], amp: 0.85, decay: 0.04 });
  addNoise(b, r, { filters: [['bandpass', 900, 1.2]], amp: 0.5, decay: 0.02 });
  addNoise(b, r, { filters: [['highpass', 2500, 0.7]], amp: 0.3, decay: 0.004 });
  return normalize(drive(b, 1.8), 0.95);
}

function floorBounce(r) {
  const b = buf(0.5);
  const f0 = 760 * (1 + (r() - 0.5) * 0.14);
  addNoise(b, r, { filters: [['highpass', 2500, 0.7]], amp: 0.35, decay: 0.0025 });
  addTone(b, { f0: 170, f1: 104, fTau: 0.012, amp: 0.8, decay: 0.05 });
  // rubber shell modes: the characteristic "pang" of a playground/dodge ball
  addModal(b, 0.001, [f0, f0 * 1.47, f0 * 2.09, f0 * 2.91], [0.34, 0.18, 0.08, 0.04], [0.11, 0.065, 0.04, 0.025]);
  // maple floor resonances
  addNoise(b, r, { filters: [['bandpass', 650, 4]], amp: 0.3, decay: 0.04 });
  addNoise(b, r, { filters: [['bandpass', 1900, 5]], amp: 0.13, decay: 0.025 });
  return normalize(b, 0.9);
}

function wallBounce(r, v) {
  const b = buf(0.36);
  addTone(b, { f0: 140 + v * 12, f1: 78, fTau: 0.015, amp: 0.9, decay: 0.06 });
  addNoise(b, r, { filters: [['lowpass', 900, 0.7]], amp: 0.55, decay: 0.025 });
  addModal(b, 0.001, [520 + v * 30], [0.08], [0.05]);
  return normalize(b, 0.85);
}

function catchSlap(r, v) {
  const b = buf(0.3);
  const k = 1 + (v - 1) * 0.07;
  addNoise(b, r, { filters: [['bandpass', 2300 * k, 0.7]], amp: 1, attack: 0.0008, decay: 0.009 });
  addNoise(b, r, { filters: [['bandpass', 1200 * k, 1]], amp: 0.5, decay: 0.022 });
  addTone(b, { f0: 205 * k, f1: 138 * k, fTau: 0.02, amp: 0.55, decay: 0.045 });
  addTone(b, { f0: 480 * k, amp: 0.06, decay: 0.04, dur: 0.2 });
  return normalize(b, 0.9);
}

function perfectCatch(r) {
  const L = buf(1.5), R = buf(1.5);
  const slap = catchSlap(r, 1);
  for (const ch of [L, R]) {
    mixInto(ch, slap, 0.75);
    addNoise(ch, r, { filters: [['highpass', 1500, 0.7]], amp: 0.8, decay: 0.004 });
    addNoise(ch, r, { filters: [['bandpass', 2400, 12]], amp: 1.3, decay: 0.035 }); // woodblock "clack"
  }
  // shimmer: E major-ish bell partials, staggered, slightly detuned between ears
  const partials = [1318.5, 1975.5, 2637, 3322.4, 3951];
  partials.forEach((f, k) => {
    const a = 0.22 * pow(0.86, k), dec = 0.9 - k * 0.08, t0 = 0.015 * k;
    addTone(L, { t0, f0: f - 2.5, amp: a, attack: 0.004, decay: dec, vibRate: 5.5, vibDepth: 0.003 });
    addTone(R, { t0: t0 + 0.004, f0: f + 2.5, amp: a, attack: 0.004, decay: dec, vibRate: 5.9, vibDepth: 0.003 });
  });
  normalize(L, 0.9); normalize(R, 0.9);
  return [L, R];
}

function throwWhoosh(r, v) {
  const T = 0.5;
  const b = buf(T);
  const f0 = 400 + v * 60, f1 = 2000 + v * 250, f2 = 850;
  addSweep(b, r, {
    dur: T, q: 1.4, twoPole: true,
    freq: (u) => (u < 0.55 ? f0 + (f1 - f0) * pow(sin(PI * u / 1.1), 1.5) : f1 + (f2 - f1) * (u - 0.55) / 0.45),
    env: (t) => (t < 0.13 ? pow(t / 0.13, 2) : exp(-(t - 0.13) / 0.08)), amp: 1,
  });
  addNoise(b, r, { color: 'pink', filters: [['lowpass', 380, 0.7]], amp: 0.35, attack: 0.1, decay: 0.08 });
  return normalize(fadeEdges(b), 0.8);
}

function throwHeavy(r) {
  const T = 0.7;
  const b = buf(T);
  addSweep(b, r, {
    dur: T, q: 1.2, twoPole: true, freq: (u) => 300 + 1300 * pow(sin(PI * Math.min(1, u * 1.2)), 1.3),
    env: (t) => (t < 0.18 ? pow(t / 0.18, 2) : exp(-(t - 0.18) / 0.12)) * (1 - 0.25 * (0.5 + 0.5 * sin(2 * PI * 26 * t))), amp: 1,
  });
  addNoise(b, r, { color: 'pink', filters: [['lowpass', 300, 0.7]], amp: 0.5, attack: 0.15, decay: 0.12 });
  return normalize(fadeEdges(b), 0.85);
}

function flyby(r) {
  const b = buf(1.1);
  addNoise(b, r, { color: 'pink', filters: [['bandpass', 1100, 1.1]], amp: 1, am: (t) => 1 - 0.22 * (0.5 + 0.5 * sin(2 * PI * 12 * t)) });
  addNoise(b, r, { color: 'pink', filters: [['bandpass', 2800, 1.8]], amp: 0.45 });
  return normalize(loopify(b, 0.1), 0.8);
}

function footstep(r, v) {
  const b = buf(0.16);
  const k = 1 + (r() - 0.5) * 0.15;
  addTone(b, { f0: 125 * k, f1: 84 * k, fTau: 0.008, amp: 0.7, attack: 0.001, decay: 0.022 });
  addNoise(b, r, { filters: [['bandpass', 900 * k, 0.8]], amp: 0.45, decay: 0.014 });
  addNoise(b, r, { filters: [['highpass', 3500, 0.7]], amp: 0.12, decay: 0.002 });
  if (v % 3 === 0) addTone(b, { t0: 0.02, dur: 0.05, f0: 2600, f1: 2400, fTau: 0.03, amp: 0.05, attack: 0.005, decay: 0.02 });
  return normalize(b, 0.85);
}

function squeak(r, v) {
  const dur = range(r, 0.12, 0.3);
  const b = buf(dur + 0.02);
  const fa = range(r, 1500, 2600), fb = fa * range(r, 0.75, 1.35);
  const chat = range(r, 70, 110);
  let ph = 0, jit = 0;
  for (let i = 0; i < Math.floor(dur * SR); i++) {
    const t = i / SR, u = t / dur;
    jit = jit * 0.995 + (r() - 0.5) * 0.004;             // stick-slip pitch wander
    const f = (fa + (fb - fa) * u) * (1 + jit);
    ph += 2 * PI * f / SR;
    const env = Math.min(1, t / 0.012) * Math.min(1, (dur - t) / 0.035);
    const am = 1 - 0.45 * (0.5 + 0.5 * sin(2 * PI * chat * t));
    b[i] += (sin(ph) + 0.35 * sin(2 * ph) + 0.15 * sin(3 * ph)) * env * am;
  }
  addNoise(b, r, { dur, filters: [['bandpass', 3000 + v * 100, 2]], amp: 0.12, attack: 0.01, release: 0.03 });
  return normalize(b, 0.8);
}

function jumpScuff(r) {
  const b = buf(0.22);
  addNoise(b, r, { filters: [['bandpass', 1500, 0.7]], amp: 0.6, attack: 0.004, decay: 0.045 });
  addTone(b, { f0: 110, f1: 80, amp: 0.35, decay: 0.03 });
  addTone(b, { t0: 0.01, dur: 0.05, f0: 2100, f1: 2500, fTau: 0.03, amp: 0.12, attack: 0.005, decay: 0.03 });
  return normalize(b, 0.8);
}

function land(r, v) {
  const b = buf(0.36);
  addTone(b, { f0: 120 - v * 6, f1: 62, fTau: 0.012, amp: 0.9, decay: 0.055 });
  addTone(b, { t0: 0.028, f0: 115, f1: 60, fTau: 0.012, amp: 0.6, decay: 0.045 });
  addNoise(b, r, { filters: [['lowpass', 1300, 0.7]], amp: 0.35, decay: 0.03 });
  addNoise(b, r, { filters: [['bandpass', 600, 3]], amp: 0.15, decay: 0.05 });
  return normalize(b, 0.9);
}

function slideFriction(r) {
  const T = 0.95;
  const b = buf(T);
  let rough = 0;
  addNoise(b, r, {
    color: 'pink', filters: [['bandpass', 1400, 0.6], ['highpass', 500, 0.7]], amp: 1, attack: 0.03, dur: T,
    am: (t) => { rough = rough * 0.99 + (r() - 0.5) * 0.08; return pow(Math.max(0, 1 - t / T), 1.4) * (1 - 0.3 * Math.abs(rough) * 4); },
  });
  addTone(b, { t0: 0.02, dur: 0.12, f0: 1900, f1: 1600, fTau: 0.1, amp: 0.2, attack: 0.01, decay: 0.06 });
  return normalize(b, 0.8);
}

function pickup(r) {
  const b = buf(0.2);
  addTone(b, { f0: 180, f1: 130, amp: 0.5, decay: 0.03 });
  addNoise(b, r, { filters: [['bandpass', 1800, 1]], amp: 0.3, decay: 0.008 });
  addTone(b, { t0: 0.01, dur: 0.05, f0: 1200, amp: 0.08, attack: 0.005, decay: 0.02 });
  return normalize(b, 0.7);
}

function whiff(r) {
  const b = buf(0.3);
  addSweep(b, r, { dur: 0.3, q: 1.2, freq: (u) => 800 + 1400 * u, env: (t) => (t < 0.08 ? pow(t / 0.08, 2) : exp(-(t - 0.08) / 0.06)), amp: 1 });
  return normalize(fadeEdges(b), 0.6);
}

// ------------------------------------------------------------------------------------------------ referee & arena
function whistle(r, v, dur) {
  const b = buf(dur + 0.05);
  const f1 = 2850 * (1 + (v - 0.5) * 0.02), f2 = f1 * 1.105;
  let p1 = 0, p2 = 0, fr = 42, frPh = 0;
  const n = Math.floor(dur * SR);
  for (let i = 0; i < n; i++) {
    const t = i / SR;
    if ((i & 255) === 0) fr = 38 + r() * 9;             // the pea's rotation rate wanders
    frPh += 2 * PI * fr / SR;
    const rise = 0.97 + 0.03 * Math.min(1, t / 0.04);
    const fm = 1 + 0.012 * sin(frPh);
    p1 += 2 * PI * f1 * rise * fm / SR;
    p2 += 2 * PI * f2 * rise * fm / SR;
    const am = 1 - 0.55 * (0.5 + 0.5 * sin(frPh + 0.8));
    const env = Math.min(1, t / 0.012) * Math.min(1, (dur - t) / 0.04);
    b[i] += (sin(p1) + 0.5 * sin(p2) + 0.1 * sin(2 * p1)) * am * env;
  }
  addNoise(b, r, { dur, filters: [['bandpass', 2800, 3]], amp: 0.14, attack: 0.012, release: 0.04 });
  return normalize(b, 0.8);
}

function beep(r, v, f, dur) {
  const b = buf(dur);
  addTone(b, { f0: f, amp: 1, attack: 0.004, hold: dur * 0.55, decay: 0.05, harm: [[2, 0.3], [3, 0.15]] });
  return normalize(fadeEdges(b, 0.002, 0.02), 0.6);
}

function buzzer(r) {
  const T = 1.0;
  const b = buf(T);
  let p1 = 0, p2 = 0, p3 = 0;
  for (let i = 0; i < Math.floor(T * SR); i++) {
    const t = i / SR;
    p1 = (p1 + 233 / SR) % 1; p2 = (p2 + 235.5 / SR) % 1; p3 = (p3 + 116.5 / SR) % 1;
    const env = Math.min(1, t / 0.01) * Math.min(1, (T - t) / 0.06);
    b[i] = ((2 * p1 - 1) + (2 * p2 - 1) + (p3 < 0.5 ? 0.6 : -0.6)) * env * 0.4;
  }
  // arena horn: band-limit through a lowpass pass (simple one-pole x2)
  let a = 0, c = 0; const k = 1 - exp(-2 * PI * 2200 / SR);
  for (let i = 0; i < b.length; i++) { a += k * (b[i] - a); c += k * (a - c); b[i] = c; }
  return normalize(drive(b, 2), 0.8);
}

// ------------------------------------------------------------------------------------------------ crowd
function crowdLoop(r, v, sr) {
  const D = v === 0 ? 6.4 : 7.9, X = 0.8;
  const out = [];
  for (let ch = 0; ch < 2; ch++) {
    const b = buf(D + X, sr);
    const k1 = 2 + ch, ph = r() * 6;
    addNoise(b, r, { color: 'brown', filters: [['lowpass', 300, 0.7]], amp: 0.35 }, sr);
    addNoise(b, r, { color: 'pink', filters: [['bandpass', 520, 0.6]], amp: 0.55, am: (t) => 0.8 + 0.2 * sin(2 * PI * k1 * t / D + ph) }, sr);
    addBabble(b, r, {
      dur: D + X, voices: 14, fLo: 95, fHi: 240, syllables: 4.5, amp: 0.9,
      formants: [[500, 2, 1], [1500, 3, 0.6], [2500, 4, 0.3]],
    }, sr);
    addImpulses(b, r, {
      dur: D + X, rate: () => 1.2, amp: [0.05, 0.18], decay: [0.006, 0.014],
      filter: (rr) => ['bandpass', range(rr, 1200, 2600), range(rr, 0.8, 1.6)],
    }, sr);
    out.push(normalize(loopify(b, X, sr), 0.7));
  }
  return out;
}

function applauseInto(b, r, dur, envFn, peakRate, sr, amp = 1) {
  addImpulses(b, r, {
    dur, rate: (u) => peakRate * envFn(u), amp: [0.15 * amp, 1 * amp], decay: [0.006, 0.014],
    filter: (rr) => ['bandpass', range(rr, 900, 2600), range(rr, 0.8, 1.6)],
  }, sr);
}

function crowdCheer(r, v, sr) {
  const D = 3.2;
  const env = (u) => (u < 0.1 ? u / 0.1 : u < 0.55 ? 1 : Math.max(0, 1 - (u - 0.55) / 0.45));
  const out = [];
  for (let ch = 0; ch < 2; ch++) {
    const b = buf(D, sr);
    applauseInto(b, r, D, env, 220, sr, 0.8);
    addBabble(b, r, {
      dur: D, voices: 22, fLo: 140, fHi: 330, stagger: 0.25, amp: 2.2,
      glide: (u) => 1 + 0.15 * Math.min(1, u * 6) - 0.1 * u, env,
      formants: [[750, 2.5, 1], [1300, 3, 0.7], [2500, 4, 0.3]],
    }, sr);
    addNoise(b, r, { dur: D, filters: [['bandpass', 1000, 0.8]], amp: 0.3, am: (t) => env(t / D) }, sr);
    const w0 = range(r, 0.4, 0.9);
    addTone(b, { t0: w0, dur: 0.9, f0: range(r, 2000, 2400), f1: range(r, 2800, 3200), fTau: 0.25, amp: 0.12, attack: 0.05, hold: 0.5, decay: 0.2, vibRate: 7, vibDepth: 0.01 }, sr);
    out.push(normalize(b, 0.85));
  }
  return out;
}

function crowdOoh(r, v, sr) {
  const D = 2.0;
  const env = (u) => (u < 0.12 ? u / 0.12 : u < 0.5 ? 1 : Math.max(0, 1 - (u - 0.5) / 0.5));
  const out = [];
  for (let ch = 0; ch < 2; ch++) {
    const b = buf(D, sr);
    addBabble(b, r, {
      dur: D, voices: 22, fLo: 110, fHi: 260, stagger: 0.12, amp: 2.4, glide: (u) => 1.12 - 0.27 * u, env,
      formants: [[320, 3, 1], [800, 4, 0.5], [2300, 5, 0.1]],
    }, sr);
    addNoise(b, r, { dur: D, color: 'pink', filters: [['lowpass', 700, 0.7]], amp: 0.2, am: (t) => env(t / D) }, sr);
    out.push(normalize(b, 0.8));
  }
  return out;
}

function crowdGasp(r, v, sr) {
  const D = 1.0;
  const out = [];
  for (let ch = 0; ch < 2; ch++) {
    const b = buf(D, sr);
    addSweep(b, r, { dur: D, q: 1, freq: (u) => 900 + 900 * Math.min(1, u * 3), env: (t) => (t < 0.04 ? t / 0.04 : exp(-(t - 0.04) / 0.3)), amp: 1 }, sr);
    addBabble(b, r, { dur: 0.45, voices: 14, fLo: 150, fHi: 320, amp: 1.2, env: (u) => (u < 0.1 ? u / 0.1 : 1 - u), formants: [[800, 2.5, 1], [1250, 3, 0.6]] }, sr);
    out.push(normalize(b, 0.7));
  }
  return out;
}

function applause(r, v, sr) {
  const D = 4.2;
  const env = (u) => (u < 0.06 ? u / 0.06 : u < 0.5 ? 1 : Math.max(0, 1 - (u - 0.5) / 0.5));
  const out = [];
  for (let ch = 0; ch < 2; ch++) { const b = buf(D, sr); applauseInto(b, r, D, env, 260, sr); out.push(normalize(b, 0.8)); }
  return out;
}

// ------------------------------------------------------------------------------------------------ abilities
function shockwave(r) {
  const D = 1.8;
  const out = [];
  for (let ch = 0; ch < 2; ch++) {
    const b = buf(D);
    addTone(b, { f0: 72, f1: 36, fTau: 0.08, amp: 1, attack: 0.003, decay: 0.5 });
    addSweep(b, r, { dur: D, type: 'lowpass', q: 0.8, freq: (u) => 4200 * pow(150 / 4200, Math.min(1, u * 2)), env: (t) => Math.min(1, t / 0.004) * exp(-t / 0.35), amp: 0.9 });
    addNoise(b, r, { filters: [['bandpass', 180, 0.7]], amp: 0.6, decay: 0.25 });
    addImpulses(b, r, { dur: 1.0, rate: (u) => 320 * (1 - u), amp: [0.05, 0.3], decay: [0.002, 0.004], filter: () => ['highpass', 2000, 0.7] });
    out.push(normalize(drive(b, 1.5), 0.95));
  }
  return out;
}

function fireWhoosh(r) {
  const D = 0.9;
  const b = buf(D);
  let flick = 0;
  addSweep(b, r, { dur: D, q: 0.8, freq: (u) => (u < 0.35 ? 250 + 650 * u / 0.35 : 900 - 500 * (u - 0.35) / 0.65), env: (t) => (t < 0.15 ? pow(t / 0.15, 1.5) : exp(-(t - 0.15) / 0.3)), amp: 1 });
  addNoise(b, r, { dur: D, filters: [['lowpass', 500, 0.7]], amp: 0.7, attack: 0.08, decay: 0.3, am: () => { flick = flick * 0.998 + (r() - 0.5) * 0.02; return 0.7 + Math.min(0.3, Math.abs(flick) * 6); } });
  addImpulses(b, r, { dur: D, rate: (u) => 60 * (1 - u), amp: [0.05, 0.25], decay: [0.001, 0.003], filter: (rr) => ['bandpass', range(rr, 2000, 5000), 1.5] });
  return normalize(b, 0.85);
}

function fireSmother(r) {
  const b = buf(0.8);
  addNoise(b, r, { filters: [['highpass', 2500, 0.7]], amp: 0.8, attack: 0.01, decay: 0.3 });
  addTone(b, { f0: 90, f1: 60, amp: 0.4, decay: 0.08 });
  addImpulses(b, r, { dur: 0.6, rate: (u) => 90 * (1 - u), amp: [0.05, 0.2], decay: [0.001, 0.003], filter: () => ['highpass', 3000, 0.7] });
  return normalize(b, 0.7);
}

function overchargeReady(r) {
  const b = buf(0.75);
  addSweep(b, r, { dur: 0.4, q: 1, freq: (u) => 300 + 1500 * u, env: (t) => (t < 0.2 ? t / 0.2 : exp(-(t - 0.2) / 0.08)), amp: 0.7 });
  addModal(b, 0.18, [1760, 2640, 4410], [0.5, 0.25, 0.1], [0.3, 0.2, 0.1]);
  addImpulses(b, r, { dur: 0.6, rate: () => 40, amp: [0.03, 0.12], decay: [0.001, 0.003], filter: () => ['highpass', 2500, 0.7] });
  return normalize(b, 0.75);
}

function beamZap(r) {
  const D = 1.1;
  const out = [];
  for (let ch = 0; ch < 2; ch++) {
    const b = buf(D);
    const fc = 150 + (ch ? 1.5 : -1.5);
    let pc = 0, pm = 0;
    for (let i = 0; i < Math.floor(D * SR); i++) {
      const t = i / SR;
      const idx = 1 + 7 * exp(-t / 0.15);
      pm += 2 * PI * fc * 1.5 / SR;
      pc += 2 * PI * fc / SR + idx * sin(pm) * 2 * PI * fc * 1.5 / SR * 0.2;
      b[i] += sin(pc) * Math.min(1, t / 0.01) * exp(-t / 0.35) * 0.8;
    }
    addTone(b, { f0: 3200, f1: 600, fTau: 0.09, amp: 0.25, decay: 0.2, dur: 0.5 });
    addNoise(b, r, { filters: [['highpass', 4000, 0.7]], amp: 0.25, decay: 0.15 });
    addImpulses(b, r, { dur: 0.7, rate: (u) => 120 * (1 - u), amp: [0.05, 0.25], decay: [0.001, 0.002], filter: () => ['bandpass', 3000, 2] });
    out.push(normalize(drive(b, 1.4), 0.9));
  }
  return out;
}

function beamImpact(r) {
  const b = buf(0.65);
  addTone(b, { f0: 190, f1: 70, fTau: 0.03, amp: 0.9, decay: 0.1 });
  addNoise(b, r, { filters: [['highpass', 3000, 0.7]], amp: 0.5, decay: 0.2, am: (t) => 0.6 + 0.4 * sin(2 * PI * 60 * t) });
  addTone(b, { f0: 2000, f1: 500, fTau: 0.05, amp: 0.25, decay: 0.08, dur: 0.3 });
  return normalize(drive(b, 1.3), 0.9);
}

function freezeCrackle(r) {
  const D = 1.3;
  const b = buf(D);
  addImpulses(b, r, { dur: 1.1, rate: (u) => 70 * pow(1 - u, 1.5) + 4, amp: [0.1, 0.7], decay: [0.0008, 0.002], filter: (rr) => ['bandpass', range(rr, 2000, 7000), 20] });
  addNoise(b, r, { filters: [['bandpass', 1200, 0.8]], amp: 0.5, decay: 0.08 });
  addTone(b, { f0: 180, f1: 150, fTau: 0.3, amp: 0.18, attack: 0.02, decay: 0.3, vibRate: 23, vibDepth: 0.05 });
  addNoise(b, r, { filters: [['highpass', 5000, 0.7]], amp: 0.1, decay: 0.5 });
  return normalize(b, 0.85);
}

function iceCrack(r) {
  const b = buf(0.6);
  addNoise(b, r, { filters: [['highpass', 1500, 0.7]], amp: 1, decay: 0.006 });
  addImpulses(b, r, { dur: 0.3, rate: (u) => 90 * (1 - u), amp: [0.1, 0.6], decay: [0.0008, 0.002], filter: (rr) => ['bandpass', range(rr, 2500, 6500), 18] });
  addNoise(b, r, { filters: [['bandpass', 900, 1]], amp: 0.5, decay: 0.05 });
  addTone(b, { f0: 95, f1: 70, amp: 0.3, decay: 0.05 });
  return normalize(b, 0.9);
}

function frostCast(r) {
  const b = buf(0.95);
  addSweep(b, r, { dur: 0.95, q: 2, freq: (u) => 600 + 1800 * u, env: (t) => (t < 0.25 ? t / 0.25 : exp(-(t - 0.25) / 0.25)), amp: 0.8 });
  for (let k = 0; k < 6; k++) addTone(b, { t0: 0.1 + k * 0.07, dur: 0.4, f0: range(r, 4000, 7000), amp: 0.08, attack: 0.002, decay: 0.12 });
  addImpulses(b, r, { dur: 0.8, rate: () => 30, amp: [0.05, 0.25], decay: [0.0008, 0.002], filter: (rr) => ['bandpass', range(rr, 3000, 7000), 18] });
  return normalize(b, 0.8);
}

function absoluteZero(r) {
  const D = 2.6;
  const out = [];
  for (let ch = 0; ch < 2; ch++) {
    const b = buf(D);
    addSweep(b, r, { dur: D, color: 'pink', q: 1.2, freq: (u) => 300 + 600 * sin(PI * u), env: (t) => (t < 0.5 ? t / 0.5 : exp(-(t - 0.5) / 0.9)), amp: 1 });
    addImpulses(b, r, { dur: 2.2, rate: (u) => 90 * (1 - u * 0.8), amp: [0.08, 0.5], decay: [0.0008, 0.002], filter: (rr) => ['bandpass', range(rr, 2000, 7500), 20] });
    addTone(b, { f0: 40, amp: 0.45, attack: 0.3, decay: 1.2 });
    out.push(normalize(b, 0.9));
  }
  return out;
}

function teleport(r) {
  const D = 0.75, hit = 0.45;
  const out = [];
  for (let ch = 0; ch < 2; ch++) {
    const b = buf(D);
    addSweep(b, r, { dur: D, q: 1.5, freq: (u) => 2600 * pow(500 / 2600, Math.min(1, u / 0.6)), env: (t) => (t < hit ? pow(t / hit, 2.2) : exp(-(t - hit) / 0.04)), amp: 1 });
    addTone(b, { t0: hit, f0: 110, f1: 60, amp: 0.6, decay: 0.06 });
    addTone(b, { f0: 700, f1: 1500, fTau: 0.3, amp: 0.12, attack: hit * 0.9, hold: 0, decay: 0.03, dur: hit + 0.1 });
    addNoise(b, r, { t0: hit, filters: [['highpass', 3000, 0.7]], amp: 0.3, decay: 0.12 });
    out.push(normalize(b, 0.85));
  }
  return out;
}

function swapChannel(r) {
  const D = 0.6;
  const b = buf(D);
  for (const d of [0, 3, -4]) addTone(b, { f0: 500 + d, f1: 1500 + d, fTau: 0.4, amp: 0.25, attack: 0.5, decay: 0.03, dur: 0.58 });
  addSweep(b, r, { dur: D, q: 2, freq: (u) => 800 + 2400 * u, env: (t) => pow(Math.min(1, t / 0.55), 2) * (t > 0.55 ? 0 : 1), amp: 0.6 });
  return normalize(fadeEdges(b), 0.7);
}

function vanish(r) {
  const b = buf(0.9);
  addSweep(b, r, { dur: 0.6, type: 'lowpass', q: 0.7, freq: (u) => 6000 * pow(400 / 6000, u), env: (t) => Math.min(1, t / 0.01) * exp(-t / 0.22), amp: 1 });
  addTone(b, { f0: 140, f1: 70, amp: 0.4, decay: 0.05 });
  for (let k = 0; k < 4; k++) addTone(b, { t0: 0.05 + k * 0.05, dur: 0.6, f0: range(r, 2500, 5000), amp: 0.08, attack: 0.003, decay: 0.2 });
  return normalize(b, 0.8);
}

function cloneSpawn(r) {
  const b = buf(0.8);
  for (let k = 0; k < 3; k++) addSweep(b, r, { t0: k * 0.03, dur: 0.7, q: 2, freq: (u) => 400 + 800 * u + k * 40, env: (t) => (t < 0.3 ? t / 0.3 : exp(-(t - 0.3) / 0.12)), amp: 0.5 });
  addTone(b, { f0: 90, f1: 110, fTau: 0.3, amp: 0.3, attack: 0.25, decay: 0.2 });
  addTone(b, { f0: 1200, amp: 0.06, attack: 0.2, decay: 0.2, vibRate: 9, vibDepth: 0.01 });
  return normalize(b, 0.75);
}

function clonePop(r) {
  const b = buf(0.45);
  addImpulses(b, r, { dur: 0.15, rate: () => 260, amp: [0.1, 0.6], decay: [0.001, 0.003], filter: (rr) => ['bandpass', range(rr, 3000, 8000), 15] });
  addNoise(b, r, { filters: [['highpass', 3000, 0.7]], amp: 0.4, decay: 0.03 });
  addTone(b, { f0: 400, f1: 200, amp: 0.3, decay: 0.03 });
  return normalize(b, 0.8);
}

function magnetHum(r) {
  const D = 1.7;
  const b = buf(D);
  const env = (t) => Math.min(1, t / 0.12) * Math.min(1, (D - t) / 0.25);
  [[60, 1], [120, 0.6], [180, 0.35], [240, 0.2]].forEach(([f, a]) => addTone(b, { f0: f, amp: a, attack: 0.12, dur: D, release: 0.25 }));
  let p = 0;
  for (let i = 0; i < Math.floor(D * SR); i++) { const t = i / SR; p = (p + 120 / SR) % 1; b[i] += (2 * p - 1) * 0.12 * env(t); }
  addTone(b, { f0: 400, f1: 900, fTau: 0.8, amp: 0.15, attack: 0.2, dur: D, release: 0.25, vibRate: 30, vibDepth: 0.03 });
  return normalize(b, 0.75);
}

function magnetClunk(r) {
  const b = buf(0.4);
  addModal(b, 0.001, [310, 845, 1520, 2300], [1, 0.5, 0.3, 0.15], [0.12, 0.08, 0.05, 0.03]);
  addNoise(b, r, { filters: [['highpass', 2000, 0.7]], amp: 0.6, decay: 0.004 });
  addTone(b, { f0: 120, amp: 0.5, decay: 0.03 });
  return normalize(b, 0.85);
}

function aegisDeploy(r) {
  const D = 1.8;
  const out = [];
  for (let ch = 0; ch < 2; ch++) {
    const b = buf(D);
    const tmp = buf(D);
    let p1 = 0, p2 = 0, p3 = 0;
    for (let i = 0; i < tmp.length; i++) {
      p1 = (p1 + 55 / SR) % 1; p2 = (p2 + (110.4 + ch * 0.3) / SR) % 1; p3 = (p3 + 165 / SR) % 1;
      tmp[i] = (2 * p1 - 1) + 0.7 * (2 * p2 - 1) + 0.4 * (2 * p3 - 1);
    }
    // opening lowpass sweep, then settle
    let a = 0;
    for (let i = 0; i < tmp.length; i++) {
      const t = i / SR;
      const fc = t < 0.5 ? 300 + 2200 * (t / 0.5) : 2500 - 1300 * Math.min(1, (t - 0.5) / 0.4);
      const k = 1 - exp(-2 * PI * fc / SR);
      a += k * (tmp[i] - a);
      b[i] += a * 0.35 * (t < 0.3 ? t / 0.3 : t < 1.2 ? 1 : exp(-(t - 1.2) / 0.25));
    }
    addTone(b, { f0: 880 + ch * 2, amp: 0.12, attack: 0.3, hold: 0.8, decay: 0.3 });
    addTone(b, { f0: 1320, amp: 0.08, attack: 0.35, hold: 0.7, decay: 0.3 });
    addSweep(b, r, { dur: 0.6, q: 1.5, freq: (u) => 400 + 2000 * u, env: (t) => (t < 0.4 ? t / 0.4 : exp(-(t - 0.4) / 0.06)), amp: 0.4 });
    out.push(normalize(b, 0.85));
  }
  return out;
}

function aegisDown(r) {
  const D = 1.0;
  const b = buf(D);
  let p = 0, a = 0;
  for (let i = 0; i < Math.floor(D * SR); i++) {
    const t = i / SR;
    const f = 165 * pow(60 / 165, t / D);
    p = (p + f / SR) % 1;
    const fc = 2500 * pow(200 / 2500, t / D);
    a += (1 - exp(-2 * PI * fc / SR)) * ((2 * p - 1) - a);
    b[i] += a * Math.min(1, t / 0.02) * (1 - t / D);
  }
  addImpulses(b, r, { dur: 0.7, rate: (u) => 80 * (1 - u), amp: [0.05, 0.3], decay: [0.001, 0.003], filter: () => ['highpass', 2500, 0.7] });
  return normalize(b, 0.8);
}

function shieldImpact(r) {
  const b = buf(0.7);
  addModal(b, 0.001, [180, 497, 972, 1610], [0.8, 0.45, 0.3, 0.15], [0.35, 0.2, 0.12, 0.08]);
  addTone(b, { f0: 90, amp: 0.5, decay: 0.1 });
  addNoise(b, r, { filters: [['highpass', 1500, 0.7]], amp: 0.6, decay: 0.02 });
  addTone(b, { f0: 2200, amp: 0.12, attack: 0.005, decay: 0.25, vibRate: 11, vibDepth: 0.01 });
  return normalize(b, 0.9);
}

function glueSplat(r) {
  const b = buf(0.6);
  addNoise(b, r, { filters: [['lowpass', 1500, 0.7]], amp: 1, attack: 0.002, decay: 0.05 });
  addNoise(b, r, { filters: [['bandpass', 450, 3]], amp: 0.6, decay: 0.07 });
  for (let k = 0; k < 8; k++) {
    const f0 = range(r, 250, 650);
    addTone(b, { t0: range(r, 0.03, 0.4), dur: 0.08, f0, f1: f0 * 1.8, fTau: 0.03, amp: range(r, 0.1, 0.3), attack: 0.002, decay: 0.025 });
  }
  addTone(b, { f0: 100, f1: 70, amp: 0.4, decay: 0.05 });
  return normalize(b, 0.85);
}

function glueStick(r) {
  const b = buf(0.35);
  addNoise(b, r, { filters: [['bandpass', 600, 4]], amp: 1, attack: 0.02, decay: 0.12, am: () => (r() < 0.3 ? 1 : 0.4) });
  addTone(b, { t0: 0.05, dur: 0.08, f0: 350, f1: 600, fTau: 0.03, amp: 0.25, decay: 0.03 });
  return normalize(b, 0.7);
}

function turretServo(r) {
  const D = 0.9;
  const b = buf(D);
  let p = 0, a = 0;
  for (let i = 0; i < Math.floor(0.7 * SR); i++) {
    const t = i / SR;
    const f = 200 + 220 * Math.min(1, t / 0.5);
    p = (p + f / SR) % 1;
    a += (1 - exp(-2 * PI * 1800 / SR)) * ((2 * p - 1) - a);
    b[i] += a * 0.5 * Math.min(1, t / 0.03) * Math.min(1, (0.7 - t) / 0.05);
  }
  addImpulses(b, r, { dur: 0.65, rate: () => 35, amp: [0.1, 0.3], decay: [0.001, 0.002], filter: () => ['highpass', 2000, 0.7] });
  addModal(b, 0.7, [220, 610, 1400], [0.6, 0.3, 0.15], [0.06, 0.04, 0.02]);
  addTone(b, { t0: 0.7, f0: 110, amp: 0.4, decay: 0.04 });
  return normalize(b, 0.8);
}

function turretPop(r) {
  const b = buf(0.4);
  addNoise(b, r, { filters: [['highpass', 700, 0.7]], amp: 1, decay: 0.012 });
  addTone(b, { f0: 150, f1: 80, amp: 0.7, decay: 0.04 });
  addNoise(b, r, { filters: [['bandpass', 3500, 1.5]], amp: 0.3, attack: 0.005, decay: 0.12 });
  return normalize(b, 0.9);
}

function stasis(r) {
  const D = 1.9;
  const out = [];
  for (let ch = 0; ch < 2; ch++) {
    const b = buf(D);
    addSweep(b, r, { dur: 0.35, q: 1.5, freq: (u) => 2000 * pow(200 / 2000, u), env: (t) => exp(-t / 0.12), amp: 0.7 });
    const trem = (t) => 1 - 0.35 * (0.5 + 0.5 * sin(2 * PI * 7 * t));
    for (const [f, a] of [[440 + ch * 0.8, 0.5], [443.5, 0.45], [660, 0.2], [880, 0.1]]) {
      addTone(b, { t0: 0.2, dur: D - 0.2, f0: f, amp: a, attack: 0.15, release: 0.4 });
    }
    for (let i = Math.floor(0.2 * SR); i < b.length; i++) b[i] *= trem(i / SR);
    out.push(normalize(b, 0.75));
  }
  return out;
}

function rewind(r) {
  const D = 1.5;
  const out = [];
  for (let ch = 0; ch < 2; ch++) {
    const b = buf(D);
    // forward: impact + long decay, then reversed -> a swelling "suck" that ends in a sharp transient
    addNoise(b, r, { filters: [['highpass', 3000, 0.7]], amp: 0.8, decay: 0.45 });
    addTone(b, { f0: 90, amp: 0.5, decay: 0.2 });
    addTone(b, { f0: 300, f1: 1200, fTau: 0.3, amp: 0.3, decay: 0.5 });
    addNoise(b, r, { color: 'pink', filters: [['bandpass', 900, 1]], amp: 0.5, decay: 0.3 });
    reverse(b);
    addTone(b, { t0: D - 0.35, dur: 0.35, f0: 600, amp: 0.15, attack: 0.005, decay: 0.12, vibRate: 12, vibDepth: 0.3 });
    out.push(normalize(fadeEdges(b, 0.05, 0.02), 0.85));
  }
  return out;
}

function cloak(r) {
  const D = 1.0;
  const b = buf(D);
  for (let k = 0; k < 6; k++) {
    const f = range(r, 3000, 7000);
    addTone(b, { f0: f, f1: f * 0.9, fTau: 0.8, amp: 0.12, attack: 0.15, decay: 0.35, vibRate: range(r, 18, 26), vibDepth: 0.01 });
  }
  addNoise(b, r, { filters: [['highpass', 6000, 0.7]], amp: 0.25, attack: 0.15, decay: 0.3 });
  return normalize(b, 0.6);
}

function decloak(r) {
  const b = buf(0.6);
  addSweep(b, r, { dur: 0.5, q: 1.2, freq: (u) => 3000 - 2000 * u, env: (t) => Math.min(1, t / 0.01) * exp(-t / 0.15), amp: 0.7 });
  for (let k = 0; k < 4; k++) addTone(b, { t0: k * 0.03, dur: 0.4, f0: range(r, 2500, 6000), amp: 0.1, attack: 0.002, decay: 0.12 });
  return normalize(b, 0.6);
}

function earthquake(r) {
  const D = 2.6;
  const out = [];
  for (let ch = 0; ch < 2; ch++) {
    const b = buf(D);
    addTone(b, { f0: 92, f1: 40, fTau: 0.05, amp: 1, decay: 0.25 });
    addNoise(b, r, { filters: [['lowpass', 800, 0.7]], amp: 0.8, decay: 0.08 });
    addNoise(b, r, { filters: [['highpass', 1200, 0.7]], amp: 0.6, decay: 0.02 });
    let rat = 0;
    addNoise(b, r, {
      color: 'brown', filters: [['lowpass', 110, 0.7]], amp: 1.6, attack: 0.1, hold: 1.1, decay: 0.6,
      am: () => { rat = rat * 0.999 + (r() - 0.5) * 0.01; return 0.75 + Math.min(0.25, Math.abs(rat) * 8); },
    });
    addTone(b, { f0: 34, amp: 0.5, attack: 0.1, hold: 1.1, decay: 0.6 });
    addTone(b, { f0: 29, amp: 0.4, attack: 0.1, hold: 1.1, decay: 0.6 });
    addImpulses(b, r, { dur: 1.9, rate: (u) => 40 * (1 - u), amp: [0.05, 0.35], decay: [0.01, 0.03], filter: (rr) => ['bandpass', range(rr, 900, 3000), 2] });
    out.push(normalize(drive(b, 1.3), 0.95));
  }
  return out;
}

function tackle(r) {
  const b = buf(0.7);
  addSweep(b, r, { dur: 0.7, q: 1, freq: (u) => 350 + 350 * u, env: (t) => (t < 0.2 ? t / 0.2 : exp(-(t - 0.2) / 0.2)), amp: 0.8 });
  addTone(b, { t0: 0.1, f0: 110, f1: 70, amp: 0.6, decay: 0.05 });
  addTone(b, { t0: 0.3, f0: 105, f1: 65, amp: 0.55, decay: 0.05 });
  addNoise(b, r, { filters: [['bandpass', 1200, 1]], amp: 0.2, attack: 0.05, decay: 0.2, am: (t) => 0.5 + 0.5 * sin(2 * PI * 18 * t) });
  return normalize(b, 0.8);
}

function abilityCast(r) {
  const b = buf(0.6);
  addSweep(b, r, { dur: 0.45, q: 2, freq: (u) => 500 + 1500 * u, env: (t) => (t < 0.2 ? t / 0.2 : exp(-(t - 0.2) / 0.08)), amp: 0.6 });
  addTone(b, { t0: 0.12, f0: 880, amp: 0.25, attack: 0.01, decay: 0.3 });
  addTone(b, { t0: 0.14, f0: 1320, amp: 0.18, attack: 0.01, decay: 0.3 });
  return normalize(b, 0.75);
}

function ultimateCast(r) {
  const D = 1.3;
  const out = [];
  for (let ch = 0; ch < 2; ch++) {
    const b = buf(D);
    addTone(b, { f0: 50, amp: 0.6, attack: 0.25, decay: 0.4 });
    addSweep(b, r, { dur: 0.5, q: 1.5, freq: (u) => 300 + 2500 * u, env: (t) => pow(Math.min(1, t / 0.45), 2), amp: 0.5 });
    for (const f of [220, 277.2, 329.6, 440]) addTone(b, { t0: 0.45, f0: f * (1 + ch * 0.002), amp: 0.3, attack: 0.01, decay: 0.8, harm: [[2, 0.35], [3, 0.15]] });
    addTone(b, { t0: 0.45, f0: 1760, amp: 0.08, attack: 0.02, decay: 0.6, vibRate: 6, vibDepth: 0.004 });
    out.push(normalize(drive(b, 1.2), 0.9));
  }
  return out;
}

function ultReady() {
  const b = buf(1.1);
  [1046.5, 1318.5, 1568].forEach((f, k) => addTone(b, { t0: k * 0.09, f0: f, amp: 0.4, attack: 0.005, decay: 0.45, harm: [[2, 0.3]] }));
  addTone(b, { t0: 0.2, f0: 3136, amp: 0.05, attack: 0.1, decay: 0.4 });
  return normalize(b, 0.7);
}

function abilityFail() {
  const b = buf(0.2);
  addTone(b, { f0: 220, amp: 0.8, attack: 0.003, hold: 0.04, decay: 0.01, dur: 0.07, harm: [[3, 0.3]] });
  addTone(b, { t0: 0.09, f0: 196, amp: 0.8, attack: 0.003, hold: 0.04, decay: 0.01, dur: 0.07, harm: [[3, 0.3]] });
  return normalize(b, 0.5);
}

function uiClick(r) {
  const b = buf(0.06);
  addTone(b, { f0: 1800, f1: 1200, fTau: 0.01, amp: 1, attack: 0.0005, decay: 0.012 });
  addNoise(b, r, { filters: [['highpass', 4000, 0.7]], amp: 0.3, decay: 0.002 });
  return normalize(b, 0.5);
}

function uiConfirm() {
  const b = buf(0.36);
  addTone(b, { f0: 880, amp: 0.7, attack: 0.005, hold: 0.06, decay: 0.05, dur: 0.14 });
  addTone(b, { t0: 0.08, f0: 1320, amp: 0.7, attack: 0.005, hold: 0.05, decay: 0.12 });
  return normalize(b, 0.55);
}

function heartbeat() {
  const b = buf(0.7);
  addTone(b, { f0: 62, f1: 45, fTau: 0.04, amp: 1, attack: 0.004, decay: 0.06 });
  addTone(b, { t0: 0.26, f0: 56, f1: 42, fTau: 0.04, amp: 0.7, attack: 0.004, decay: 0.05 });
  return normalize(b, 0.85);
}

function revive(r) {
  const D = 1.3;
  const out = [];
  for (let ch = 0; ch < 2; ch++) {
    const b = buf(D);
    for (const f of [523.25, 659.25, 783.99, 1046.5]) addTone(b, { f0: f * (1 + (ch ? 0.002 : -0.002)), amp: 0.25, attack: 0.25, decay: 0.5 });
    addNoise(b, r, { filters: [['highpass', 3000, 0.7]], amp: 0.12, attack: 0.3, decay: 0.3 });
    out.push(normalize(b, 0.7));
  }
  return out;
}

// ------------------------------------------------------------------------------------------------ registry
const M = (gen, meta) => ({ variants: 1, bus: 'sfx', gain: 1, ref: 3, rolloff: 1, max: 6, jitter: 0.04, priority: 1, spatial: true, gen, ...meta });

/** id -> recipe + playback metadata. */
export const SOUNDS = {
  // balls & bodies
  ballThump: M(ballThump, { variants: 3, bus: 'impact', gain: 1, ref: 4, max: 6, priority: 3 }),
  heavyHit: M(heavyHit, { variants: 2, bus: 'impact', gain: 1.1, ref: 5, max: 4, priority: 4 }),
  floorBounce: M(floorBounce, { variants: 4, bus: 'impact', gain: 0.8, ref: 3.5, max: 8, jitter: 0.06, priority: 2 }),
  wallBounce: M(wallBounce, { variants: 2, bus: 'impact', gain: 0.7, ref: 3.5, max: 4, priority: 2 }),
  catch: M(catchSlap, { variants: 3, bus: 'impact', gain: 0.95, ref: 4, max: 4, priority: 3 }),
  perfectCatch: M(perfectCatch, { bus: 'impact', gain: 1, ref: 6, max: 2, jitter: 0, priority: 5 }),
  throw: M(throwWhoosh, { variants: 3, gain: 0.7, ref: 3, max: 6, jitter: 0.05, priority: 2 }),
  throwHeavy: M(throwHeavy, { gain: 0.85, ref: 4, max: 3, priority: 3 }),
  flyby: M(flyby, { gain: 0.6, ref: 2.5, max: 4, jitter: 0, priority: 1 }),
  footstep: M(footstep, { variants: 6, gain: 0.32, ref: 2, rolloff: 1.3, max: 10, jitter: 0.06, priority: 0 }),
  squeak: M(squeak, { variants: 5, gain: 0.35, ref: 2.5, max: 4, jitter: 0.05, priority: 0 }),
  jump: M(jumpScuff, { gain: 0.4, ref: 2.5, max: 4, priority: 1 }),
  land: M(land, { variants: 2, gain: 0.55, ref: 2.5, max: 4, priority: 1 }),
  slide: M(slideFriction, { gain: 0.5, ref: 3, max: 4, priority: 1 }),
  pickup: M(pickup, { gain: 0.45, ref: 2.5, max: 4, priority: 1 }),
  whiff: M(whiff, { gain: 0.4, ref: 2.5, max: 3, priority: 1 }),
  // referee & arena (2D)
  whistle: M((r, v) => whistle(r, v, 0.45), { variants: 2, bus: 'match', gain: 0.55, spatial: false, max: 2, jitter: 0.01, priority: 6 }),
  whistleLong: M((r, v) => whistle(r, v, 1.15), { bus: 'match', gain: 0.55, spatial: false, max: 2, jitter: 0.01, priority: 6 }),
  beep: M((r, v) => beep(r, v, 880, 0.18), { bus: 'match', gain: 0.45, spatial: false, max: 2, jitter: 0, priority: 6 }),
  beepGo: M((r, v) => beep(r, v, 1320, 0.55), { bus: 'match', gain: 0.5, spatial: false, max: 2, jitter: 0, priority: 6 }),
  buzzer: M(buzzer, { bus: 'match', gain: 0.45, spatial: false, max: 1, jitter: 0, priority: 6 }),
  // crowd (2D beds, 22.05 kHz)
  crowdLoop: M(crowdLoop, { variants: 2, sr: CROWD_SR, bus: 'crowd', gain: 1, spatial: false, max: 2, jitter: 0, priority: 9 }),
  crowdCheer: M(crowdCheer, { variants: 2, sr: CROWD_SR, bus: 'crowd', gain: 0.9, spatial: false, max: 2, jitter: 0.03, priority: 5 }),
  crowdOoh: M(crowdOoh, { variants: 2, sr: CROWD_SR, bus: 'crowd', gain: 0.8, spatial: false, max: 2, jitter: 0.04, priority: 5 }),
  crowdGasp: M(crowdGasp, { sr: CROWD_SR, bus: 'crowd', gain: 0.7, spatial: false, max: 2, jitter: 0.04, priority: 5 }),
  applause: M(applause, { sr: CROWD_SR, bus: 'crowd', gain: 0.9, spatial: false, max: 1, jitter: 0, priority: 5 }),
  // abilities
  shockwave: M(shockwave, { bus: 'ability', gain: 1.1, ref: 7, max: 2, jitter: 0.03, priority: 5 }),
  fireWhoosh: M(fireWhoosh, { bus: 'ability', gain: 0.9, ref: 5, max: 3, priority: 4 }),
  fireSmother: M(fireSmother, { bus: 'ability', gain: 0.7, ref: 4, max: 2, priority: 3 }),
  overchargeReady: M(overchargeReady, { bus: 'ability', gain: 0.7, ref: 4, max: 2, priority: 3 }),
  beamZap: M(beamZap, { bus: 'ability', gain: 1, ref: 7, max: 2, jitter: 0.02, priority: 5 }),
  beamImpact: M(beamImpact, { bus: 'ability', gain: 1, ref: 5, max: 3, priority: 4 }),
  freeze: M(freezeCrackle, { bus: 'ability', gain: 0.9, ref: 5, max: 3, priority: 4 }),
  iceCrack: M(iceCrack, { variants: 2, bus: 'ability', gain: 0.9, ref: 5, max: 3, priority: 4 }),
  frostCast: M(frostCast, { bus: 'ability', gain: 0.8, ref: 5, max: 2, priority: 4 }),
  absoluteZero: M(absoluteZero, { bus: 'ability', gain: 1, ref: 10, max: 1, jitter: 0, priority: 6 }),
  teleport: M(teleport, { variants: 2, bus: 'ability', gain: 0.85, ref: 5, max: 3, priority: 4 }),
  swapChannel: M(swapChannel, { bus: 'ability', gain: 0.7, ref: 5, max: 2, priority: 3 }),
  vanish: M(vanish, { variants: 2, bus: 'ability', gain: 0.8, ref: 5, max: 3, priority: 4 }),
  clone: M(cloneSpawn, { variants: 2, bus: 'ability', gain: 0.7, ref: 4, max: 4, priority: 3 }),
  clonePop: M(clonePop, { variants: 2, bus: 'ability', gain: 0.75, ref: 4, max: 4, priority: 3 }),
  magnetHum: M(magnetHum, { bus: 'ability', gain: 0.75, ref: 6, max: 2, jitter: 0.02, priority: 4 }),
  magnetClunk: M(magnetClunk, { variants: 2, bus: 'ability', gain: 0.85, ref: 4, max: 3, priority: 3 }),
  aegisDeploy: M(aegisDeploy, { bus: 'ability', gain: 0.9, ref: 9, max: 1, jitter: 0, priority: 6 }),
  aegisDown: M(aegisDown, { bus: 'ability', gain: 0.8, ref: 9, max: 1, jitter: 0, priority: 5 }),
  shieldImpact: M(shieldImpact, { variants: 2, bus: 'ability', gain: 0.9, ref: 6, max: 4, priority: 4 }),
  glueSplat: M(glueSplat, { variants: 2, bus: 'ability', gain: 0.85, ref: 4.5, max: 3, priority: 4 }),
  glueStick: M(glueStick, { variants: 2, bus: 'ability', gain: 0.6, ref: 3, max: 4, priority: 2 }),
  turretServo: M(turretServo, { bus: 'ability', gain: 0.75, ref: 5, max: 2, priority: 4 }),
  turretPop: M(turretPop, { variants: 2, bus: 'ability', gain: 0.9, ref: 5, max: 3, priority: 4 }),
  stasis: M(stasis, { bus: 'ability', gain: 0.8, ref: 6, max: 2, jitter: 0, priority: 5 }),
  rewind: M(rewind, { bus: 'ability', gain: 0.85, ref: 6, max: 3, jitter: 0.02, priority: 5 }),
  cloak: M(cloak, { bus: 'ability', gain: 0.6, ref: 4, max: 2, priority: 3 }),
  decloak: M(decloak, { bus: 'ability', gain: 0.6, ref: 4, max: 2, priority: 3 }),
  earthquake: M(earthquake, { bus: 'ability', gain: 1.1, ref: 10, max: 1, jitter: 0.02, priority: 6 }),
  tackle: M(tackle, { bus: 'ability', gain: 0.85, ref: 5, max: 2, priority: 4 }),
  abilityCast: M(abilityCast, { variants: 2, bus: 'ability', gain: 0.6, ref: 4, max: 4, priority: 3 }),
  ultimateCast: M(ultimateCast, { bus: 'ability', gain: 0.9, ref: 7, max: 2, jitter: 0.02, priority: 5 }),
  revive: M(revive, { bus: 'ability', gain: 0.7, ref: 6, max: 2, jitter: 0, priority: 4 }),
  heartbeat: M(heartbeat, { bus: 'ui', gain: 0.8, spatial: false, max: 1, jitter: 0.02, priority: 5 }),
  // UI (2D)
  ultReady: M(ultReady, { bus: 'ui', gain: 0.55, spatial: false, max: 1, jitter: 0, priority: 5 }),
  abilityFail: M(abilityFail, { bus: 'ui', gain: 0.4, spatial: false, max: 1, jitter: 0, priority: 2 }),
  uiClick: M(uiClick, { bus: 'ui', gain: 0.5, spatial: false, max: 3, jitter: 0.03, priority: 2 }),
  uiConfirm: M(uiConfirm, { bus: 'ui', gain: 0.55, spatial: false, max: 2, jitter: 0, priority: 3 }),
};

/** Aliases used by other modules / natural names -> canonical ids. */
export const SOUND_ALIASES = {
  hit: 'ballThump', thump: 'ballThump', ballHit: 'ballThump', impact: 'ballThump', bounce: 'floorBounce', floor: 'floorBounce',
  wall: 'wallBounce', slap: 'catch', catchSlap: 'catch', whoosh: 'throw', throwLight: 'throw', pass: 'throw', swish: 'whiff',
  step: 'footstep', footsteps: 'footstep', landing: 'land', grab: 'pickup', roundStart: 'whistle', whistleShort: 'whistle',
  countdown: 'beep', go: 'beepGo', horn: 'buzzer', cheer: 'crowdCheer', ooh: 'crowdOoh', gasp: 'crowdGasp',
  meteor: 'fireWhoosh', meteorLaunch: 'fireWhoosh', fireball: 'fireWhoosh', overcharge: 'fireWhoosh', explosion: 'shockwave',
  boom: 'shockwave', beam: 'beamZap', beamRelease: 'beamZap', hyperbeam: 'beamZap', zap: 'beamZap', beamPierce: 'beamImpact',
  freezeCrackle: 'freeze', frozen: 'freeze', frost: 'frostCast', swap: 'teleport', blink: 'teleport', poof: 'vanish',
  vanishSmoke: 'vanish', cloneSpawn: 'clone', cloneDissolve: 'clonePop', magnet: 'magnetHum', magnetField: 'magnetHum',
  shield: 'aegisDeploy', shieldHum: 'aegisDeploy', aegis: 'aegisDeploy', glue: 'glueSplat', turretDeploy: 'turretServo',
  turretFire: 'turretPop', turretShot: 'turretPop', stasisField: 'stasis', timeReversal: 'rewind', temporalReset: 'rewind',
  quake: 'earthquake', slam: 'earthquake', earthquakeSlam: 'earthquake', charge: 'tackle', tackleIntercept: 'tackle',
  ultimate: 'ultimateCast', cast: 'abilityCast', skill: 'abilityCast', danger: 'heartbeat', dangerSense: 'heartbeat',
  click: 'uiClick', confirm: 'uiConfirm', fail: 'abilityFail', ultimateReady: 'ultReady', reviveBeam: 'revive',
  // Ability-module ids (heroes/*.js)
  ironMitts: 'magnetClunk', metalClang: 'magnetClunk', slamWindup: 'throwHeavy', tackleCharge: 'tackle',
  tackleDeflect: 'shieldImpact', tackleImpact: 'heavyHit', tackleThrow: 'throwHeavy', turretFold: 'turretServo',
  turretLoad: 'turretServo', turretMuzzle: 'turretPop', turretReady: 'turretServo', glueThrow: 'throw',
};

/** Keyword fallback for unknown ids (first match wins). */
export const SOUND_KEYWORDS = [
  ['perfect', 'perfectCatch'], ['catch', 'catch'], ['whistle', 'whistle'], ['crowd', 'crowdCheer'], ['cheer', 'crowdCheer'],
  ['meteor', 'fireWhoosh'], ['fire', 'fireWhoosh'], ['flame', 'fireWhoosh'], ['shock', 'shockwave'], ['explo', 'shockwave'],
  ['beam', 'beamZap'], ['laser', 'beamZap'], ['absolute', 'absoluteZero'], ['crack', 'iceCrack'], ['ice', 'freeze'],
  ['frost', 'frostCast'], ['freez', 'freeze'], ['glue', 'glueSplat'], ['swap', 'teleport'], ['teleport', 'teleport'],
  ['vanish', 'vanish'], ['clone', 'clone'], ['decoy', 'clone'], ['magnet', 'magnetHum'], ['aegis', 'aegisDeploy'],
  ['shield', 'shieldImpact'], ['turret', 'turretPop'], ['stasis', 'stasis'], ['rewind', 'rewind'], ['time', 'rewind'],
  ['quake', 'earthquake'], ['slam', 'earthquake'], ['tackle', 'tackle'], ['cloak', 'cloak'], ['stealth', 'cloak'],
  ['ult', 'ultimateCast'], ['cast', 'abilityCast'], ['throw', 'throw'], ['whoosh', 'throw'], ['hit', 'ballThump'],
  ['impact', 'ballThump'], ['bounce', 'floorBounce'], ['step', 'footstep'], ['heart', 'heartbeat'], ['click', 'uiClick'],
];

/**
 * Renders every variant of `id`. Seeds are stable per id/variant (the game sounds identical every load).
 * @returns {{sr:number, variants:Array<Float32Array|Float32Array[]>}}
 */
export function renderSound(id) {
  const def = SOUNDS[id];
  const sr = def.sr || SR;
  const variants = [];
  let seed = 2166136261;
  for (let i = 0; i < id.length; i++) seed = Math.imul(seed ^ id.charCodeAt(i), 16777619);
  for (let v = 0; v < def.variants; v++) variants.push(def.gen(rng((seed >>> 0) + v * 7919), v, sr));
  return { sr, variants };
}
