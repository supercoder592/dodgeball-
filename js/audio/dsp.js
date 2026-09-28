// ---------------------------------------------------------------------------------------------------------------
// Offline DSP toolkit for procedural sound synthesis (owner: fx). Pure JS (no Web Audio, no three.js) so every recipe
// can be rendered and checked in node (bank.test.js). Everything here runs once at load, never per frame.
//
// Building blocks: seeded RNG, RBJ biquad filters, white/pink/brown noise, sine partials with exponential pitch glides
// and attack-hold-decay envelopes, swept band-pass noise, Poisson impulse clouds (claps, crackles, ice ticks), formant
// "babble" voices for crowds, soft clipping, normalisation, reversal, seamless loop cross-fades.
// ---------------------------------------------------------------------------------------------------------------

export const SR = 44100;
const TAU = Math.PI * 2;

/** Seeded mulberry32 -> () => [0, 1). Recipes are deterministic: the same seed always sounds the same. */
export function rng(seed) {
  let a = (seed >>> 0) || 1;
  return () => {
    a = (a + 0x6d2b79f5) | 0;
    let t = Math.imul(a ^ (a >>> 15), 1 | a);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}
export const range = (r, a, b) => a + (b - a) * r();

/** Zeroed buffer of `seconds`. */
export function buf(seconds, sr = SR) { return new Float32Array(Math.max(1, Math.ceil(seconds * sr))); }

/** RBJ cookbook biquad (direct form I). Types: lowpass highpass bandpass(0 dB peak) notch peaking lowshelf highshelf. */
export class Biquad {
  constructor(type = 'lowpass', freq = 1000, q = 0.707, gainDb = 0, sr = SR) {
    this.sr = sr;
    this.x1 = 0; this.x2 = 0; this.y1 = 0; this.y2 = 0;
    this.set(type, freq, q, gainDb);
  }
  set(type, freq, q = 0.707, gainDb = 0) {
    const f = Math.min(Math.max(freq, 10), this.sr * 0.45);
    const w0 = TAU * f / this.sr, cw = Math.cos(w0), sw = Math.sin(w0);
    const alpha = sw / (2 * Math.max(q, 1e-4));
    const A = Math.pow(10, gainDb / 40);
    let b0, b1, b2, a0, a1, a2;
    switch (type) {
      case 'highpass': b0 = (1 + cw) / 2; b1 = -(1 + cw); b2 = (1 + cw) / 2; a0 = 1 + alpha; a1 = -2 * cw; a2 = 1 - alpha; break;
      case 'bandpass': b0 = alpha; b1 = 0; b2 = -alpha; a0 = 1 + alpha; a1 = -2 * cw; a2 = 1 - alpha; break;
      case 'notch': b0 = 1; b1 = -2 * cw; b2 = 1; a0 = 1 + alpha; a1 = -2 * cw; a2 = 1 - alpha; break;
      case 'peaking': b0 = 1 + alpha * A; b1 = -2 * cw; b2 = 1 - alpha * A; a0 = 1 + alpha / A; a1 = -2 * cw; a2 = 1 - alpha / A; break;
      case 'lowshelf': {
        const s = 2 * Math.sqrt(A) * alpha;
        b0 = A * ((A + 1) - (A - 1) * cw + s); b1 = 2 * A * ((A - 1) - (A + 1) * cw); b2 = A * ((A + 1) - (A - 1) * cw - s);
        a0 = (A + 1) + (A - 1) * cw + s; a1 = -2 * ((A - 1) + (A + 1) * cw); a2 = (A + 1) + (A - 1) * cw - s; break;
      }
      case 'highshelf': {
        const s = 2 * Math.sqrt(A) * alpha;
        b0 = A * ((A + 1) + (A - 1) * cw + s); b1 = -2 * A * ((A - 1) + (A + 1) * cw); b2 = A * ((A + 1) + (A - 1) * cw - s);
        a0 = (A + 1) - (A - 1) * cw + s; a1 = 2 * ((A - 1) - (A + 1) * cw); a2 = (A + 1) - (A - 1) * cw - s; break;
      }
      default: b0 = (1 - cw) / 2; b1 = 1 - cw; b2 = (1 - cw) / 2; a0 = 1 + alpha; a1 = -2 * cw; a2 = 1 - alpha;
    }
    this.b0 = b0 / a0; this.b1 = b1 / a0; this.b2 = b2 / a0; this.a1 = a1 / a0; this.a2 = a2 / a0;
    return this;
  }
  process(x) {
    const y = this.b0 * x + this.b1 * this.x1 + this.b2 * this.x2 - this.a1 * this.y1 - this.a2 * this.y2;
    this.x2 = this.x1; this.x1 = x; this.y2 = this.y1; this.y1 = y;
    return y;
  }
}

/** Noise sources. */
export class Noise {
  constructor(r, color = 'white') {
    this.r = r; this.color = color;
    this.b0 = 0; this.b1 = 0; this.b2 = 0; this.v = 0;
  }
  next() {
    const w = this.r() * 2 - 1;
    if (this.color === 'pink') {
      // Paul Kellet's economy pink filter (-3 dB/oct)
      this.b0 = 0.99765 * this.b0 + w * 0.099046;
      this.b1 = 0.963 * this.b1 + w * 0.2965164;
      this.b2 = 0.57 * this.b2 + w * 1.0526913;
      return (this.b0 + this.b1 + this.b2 + w * 0.1848) * 0.25;
    }
    if (this.color === 'brown') {
      // leaky integrator (-6 dB/oct), gain compensated
      this.v = (this.v + 0.02 * w) / 1.02;
      return this.v * 3.5;
    }
    return w;
  }
}

/** Attack (linear) - hold - exponential decay envelope. decay = Infinity keeps the level. */
export function ahd(t, attack, hold, decay) {
  if (t < 0) return 0;
  if (t < attack) return t / attack;
  const d = t - attack - hold;
  if (d <= 0) return 1;
  return decay === Infinity ? 1 : Math.exp(-d / decay);
}

/** Release ramp at the end of a segment (avoids clicks when a sound is cut). */
export function releaseGain(t, dur, release) { return release > 0 && t > dur - release ? Math.max(0, (dur - t) / release) : 1; }

/**
 * Adds a sine partial with an exponential pitch glide f0 -> f1 (time constant fTau), AHD envelope, optional vibrato
 * and harmonics [[ratio, gain], ...].
 */
export function addTone(b, o, sr = SR) {
  const t0 = o.t0 || 0, dur = o.dur ?? (b.length / sr - t0);
  const f0 = o.f0, f1 = o.f1 ?? o.f0, fTau = o.fTau ?? 0.05;
  const amp = o.amp ?? 1, attack = Math.max(1e-5, o.attack ?? 0.002), hold = o.hold ?? 0, decay = o.decay ?? Infinity;
  const release = o.release ?? 0.005;
  const vibRate = o.vibRate || 0, vibDepth = o.vibDepth || 0;
  const hr = o.harm ? o.harm.map((h) => h[0]) : null, hg = o.harm ? o.harm.map((h) => h[1]) : null;
  const s0 = Math.floor(t0 * sr), n = Math.min(b.length - s0, Math.floor(dur * sr));
  // incremental exponentials: glide factor and decay envelope (one multiply per sample instead of exp())
  const kGlide = Math.exp(-1 / (Math.max(1e-5, fTau) * sr));
  const kDecay = decay === Infinity ? 1 : Math.exp(-1 / (decay * sr));
  const aS = attack * sr, hS = (attack + hold) * sr, relS = release * sr;
  let ph = o.phase || 0, gl = 1, dv = 1;
  for (let i = 0; i < n; i++) {
    let f = f1 + (f0 - f1) * gl;
    gl *= kGlide;
    if (vibDepth) f *= 1 + vibDepth * Math.sin(TAU * vibRate * i / sr);
    ph += TAU * f / sr;
    let s = Math.sin(ph);
    if (hr) for (let k = 0; k < hr.length; k++) s += hg[k] * Math.sin(ph * hr[k]);
    let e;
    if (i < aS) e = i / aS; else if (i < hS) e = 1; else { e = dv; dv *= kDecay; }
    const rem = n - i;
    if (rem < relS) e *= rem / relS;
    b[s0 + i] += s * amp * e;
  }
  return b;
}

/** Adds filtered noise with an AHD envelope. filters: [[type, freq, q, gainDb?], ...] in series. */
export function addNoise(b, r, o, sr = SR) {
  const t0 = o.t0 || 0, dur = o.dur ?? (b.length / sr - t0);
  const amp = o.amp ?? 1, attack = o.attack ?? 0.001, hold = o.hold ?? 0, decay = o.decay ?? Infinity, release = o.release ?? 0.005;
  const src = new Noise(r, o.color || 'white');
  const fl = (o.filters || []).map(([type, f, q, g]) => new Biquad(type, f, q ?? 0.707, g ?? 0, sr));
  const am = o.am || null; // (t) => gain
  const s0 = Math.floor(t0 * sr), n = Math.min(b.length - s0, Math.floor(dur * sr));
  for (let i = 0; i < n; i++) {
    const t = i / sr;
    let x = src.next();
    for (let k = 0; k < fl.length; k++) x = fl[k].process(x);
    let g = amp * ahd(t, attack, hold, decay) * releaseGain(t, dur, release);
    if (am) g *= am(t);
    b[s0 + i] += x * g;
  }
  return b;
}

/**
 * Adds noise through a time-varying filter. freq(u) gives the centre frequency for u = t/dur in [0,1];
 * env(t, dur) the gain. Coefficients are recomputed every 32 samples (smooth enough for sweeps).
 */
export function addSweep(b, r, o, sr = SR) {
  const t0 = o.t0 || 0, dur = o.dur;
  const type = o.type || 'bandpass', q = o.q ?? 1, amp = o.amp ?? 1;
  const src = new Noise(r, o.color || 'white');
  const bq = new Biquad(type, o.freq(0), q, 0, sr);
  const bq2 = o.twoPole ? new Biquad(type, o.freq(0), q, 0, sr) : null;
  const s0 = Math.floor(t0 * sr), n = Math.min(b.length - s0, Math.floor(dur * sr));
  for (let i = 0; i < n; i++) {
    const t = i / sr;
    if ((i & 31) === 0) { const f = o.freq(t / dur); bq.set(type, f, q); if (bq2) bq2.set(type, f, q); }
    let x = bq.process(src.next());
    if (bq2) x = bq2.process(x);
    b[s0 + i] += x * amp * o.env(t, dur);
  }
  return b;
}

/** Sum of decaying sine modes (struck objects: rubber shell, metal, wood). */
export function addModal(b, t0, freqs, amps, decays, sr = SR) {
  for (let k = 0; k < freqs.length; k++) addTone(b, { t0, f0: freqs[k], amp: amps[k], attack: 0.0006, decay: decays[k], dur: Math.min(b.length / sr - t0, decays[k] * 7) }, sr);
  return b;
}

/**
 * Poisson cloud of short filtered noise bursts (claps, crackles, debris, ice ticks).
 * o: { t0, dur, rate(u) events/s, amp:[lo,hi], filter(r) -> [type,f,q], decay:[lo,hi] }
 */
export function addImpulses(b, r, o, sr = SR) {
  const t0 = o.t0 || 0, dur = o.dur;
  const src = new Noise(r, 'white');
  // Non-homogeneous Poisson process by thinning: candidates at the peak rate, accepted with rate(u) / peak.
  let peak = 0.01;
  for (let k = 0; k <= 64; k++) peak = Math.max(peak, o.rate(k / 64));
  let t = 0;
  while (t < dur) {
    t += -Math.log(1 - r() * 0.999999) / peak;
    if (t >= dur) break;
    if (r() * peak > o.rate(t / dur)) continue;
    const [type, f, q] = o.filter(r);
    const bq = new Biquad(type, f, q, 0, sr);
    const a = range(r, o.amp[0], o.amp[1]);
    const dec = range(r, o.decay[0], o.decay[1]);
    const s0 = Math.floor((t0 + t) * sr), n = Math.min(b.length - s0, Math.floor(dec * 6 * sr));
    const kd = Math.exp(-1 / (dec * sr));
    let e = a;
    for (let i = 0; i < n; i++) { b[s0 + i] += bq.process(src.next()) * e; e *= kd; }
  }
  return b;
}

/**
 * Crowd voices: `voices` band-limited buzzy sources (saw) with random pitches, glides and syllabic gating, summed and
 * shaped by parallel formant band-passes (vowel colour). o: { t0, dur, voices, fLo, fHi, formants:[[f,q,g]...],
 * syllables (Hz, 0 = sustained), glide(u) -> pitch multiplier, env(u) -> gain, amp }
 */
export function addBabble(b, r, o, sr = SR) {
  const t0 = o.t0 || 0, dur = o.dur;
  const n = Math.min(b.length - Math.floor(t0 * sr), Math.floor(dur * sr));
  const tmp = new Float32Array(n);
  const BLOCK = 32; // pitch glide, vibrato and syllable changes are computed at block rate (inaudible, 5x faster)
  const smooth = Math.exp(-1 / (0.035 * sr));
  for (let v = 0; v < o.voices; v++) {
    const f = range(r, o.fLo, o.fHi);
    const vg = range(r, 0.35, 1);
    const vibR = range(r, 4, 6.5), vibD = range(r, 0.004, 0.012);
    const syl = o.syllables || 0;
    let ph = r(), gate = syl > 0 ? 0 : 1, target = r(), nextSwitch = syl > 0 ? range(r, 0.5, 1.5) / syl : Infinity;
    const start = Math.floor((o.stagger ? r() * o.stagger : 0) * sr);
    let inc = 0;
    for (let i = start; i < n; i++) {
      if (((i - start) & (BLOCK - 1)) === 0) {
        const t = i / sr;
        if (t >= nextSwitch) { target = r() < 0.25 ? 0 : range(r, 0.4, 1); nextSwitch = t + range(r, 0.5, 1.5) / syl; }
        inc = f * (o.glide ? o.glide(t / dur) : 1) * (1 + vibD * Math.sin(TAU * vibR * t)) / sr;
      }
      if (syl > 0) gate = target + (gate - target) * smooth;
      ph += inc; if (ph >= 1) ph -= 1;
      tmp[i] += (2 * ph - 1) * vg * gate;
    }
  }
  const fs = o.formants.map(([f, q]) => new Biquad('bandpass', f, q, 0, sr));
  const gs = o.formants.map((x) => x[2]);
  const s0 = Math.floor(t0 * sr);
  const amp = (o.amp ?? 1) / Math.sqrt(o.voices);
  let env = 1;
  for (let i = 0; i < n; i++) {
    const x = tmp[i];
    let y = 0;
    for (let k = 0; k < fs.length; k++) y += fs[k].process(x) * gs[k];
    if (o.env && (i & (BLOCK - 1)) === 0) env = o.env(i / n);
    b[s0 + i] += y * amp * env;
  }
  return b;
}

/** Soft clip (tanh drive) in place. */
export function drive(b, k) { const n = Math.tanh(k); for (let i = 0; i < b.length; i++) b[i] = Math.tanh(b[i] * k) / n; return b; }

/** Scales so the peak equals `peak`; removes DC first. */
export function normalize(b, peak = 0.9) {
  let mean = 0;
  for (let i = 0; i < b.length; i++) mean += b[i];
  mean /= b.length;
  let m = 0;
  for (let i = 0; i < b.length; i++) { b[i] -= mean; const a = Math.abs(b[i]); if (a > m) m = a; }
  if (m > 1e-9) { const g = peak / m; for (let i = 0; i < b.length; i++) b[i] *= g; }
  return b;
}

/** Short fades at both ends (no clicks). */
export function fadeEdges(b, inSec = 0.002, outSec = 0.01, sr = SR) {
  const ni = Math.min(b.length, Math.floor(inSec * sr)), no = Math.min(b.length, Math.floor(outSec * sr));
  for (let i = 0; i < ni; i++) b[i] *= i / ni;
  for (let i = 0; i < no; i++) b[b.length - 1 - i] *= i / no;
  return b;
}

export function reverse(b) { b.reverse(); return b; }

/** Seamless loop: equal-power cross-fade of the tail into the head; returns a shorter buffer. */
export function loopify(b, xfadeSec, sr = SR) {
  const x = Math.min(Math.floor(xfadeSec * sr), Math.floor(b.length / 3));
  const len = b.length - x;
  const out = b.slice(0, len);
  for (let i = 0; i < x; i++) {
    const u = i / x;
    out[i] = b[i] * Math.sin(u * Math.PI / 2) + b[len + i] * Math.cos(u * Math.PI / 2);
  }
  return out;
}

/** Mix src into dst at offset seconds. */
export function mixInto(dst, src, gain = 1, offsetSec = 0, sr = SR) {
  const s0 = Math.floor(offsetSec * sr);
  const n = Math.min(src.length, dst.length - s0);
  for (let i = 0; i < n; i++) dst[s0 + i] += src[i] * gain;
  return dst;
}
