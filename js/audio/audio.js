// ---------------------------------------------------------------------------------------------------------------
// Audio (system, owner: fx) - Web Audio sound for Dodgeball Ultra. Contract: WEB_ARCHITECTURE.md §3.9
//
//   game.audio.play(id, position|null, volume = 1, pitch = 1)   3D (PannerNode, HRTF on high quality) or 2D
//   game.audio.play2D(id, volume = 1, pitch = 1)
//   game.audio.unlock()                                          (also automatic on the first pointer/key/touch)
//   game.audio.setVolume('master'|'sfx'|'music'|'crowd'|'ui', 0..1), setMuted(b), toggleMute() (M key)
//
// Every sound is synthesised procedurally (bank.js) in a module Worker at boot - no audio files. Ids: see SOUNDS in
// bank.js (+ SOUND_ALIASES, keyword fallback for unknown ids).
//
// Graph:  voice(src -> gain -> panner) -> bus { impact | sfx(ducked) | ability | match } -> sfxGroup -> master
//         ui -> master;  crowd -> duck -> lowpass -> crowdVol -> master;  music -> master (reserved)
//         buses -> sends -> Convolver (procedural gym impulse response, ~2 s) -> master
//         master -> gentle limiter -> destination
// Event driven: throws (whoosh by speed), hits (thump / heavy), catches (slap / perfect clack+shimmer), bounces
// (hardwood / padded wall), pickups, whiffs, jumps/lands/slides, footsteps + sneaker squeaks polled from players
// (skipping status 'silentFootsteps'), whooshing fly-bys of fast live balls with Doppler, referee whistle, countdown
// beeps, buzzer, crowd ambience + reactions, hitstop ducking (EV.Hitstop), ability fallbacks when an ability did not
// play its own sound. Real time only (realDt / AudioContext clock); footstep cadence follows scaled movement.
// ---------------------------------------------------------------------------------------------------------------
import { game } from '../game.js';
import { EV } from '../core/events.js';
import { SOUNDS, SOUND_ALIASES, SOUND_KEYWORDS, renderSound } from './bank.js';

/** Tuning. */
export const AUDIO_TUNING = Object.freeze({
  voices: 40,
  maxDistance: 70,
  volumes: { master: 0.9, sfx: 1, music: 0.8, crowd: 0.75, ui: 0.9 },
  reverb: { seconds: 2.1, decay: 0.3, preDelay: 0.018, returnGain: 0.5,
    send: { impact: 0.22, sfx: 0.14, ability: 0.24, match: 0.32, crowd: 0.06 } },
  duck: { sfx: 0.45, crowd: 0.5, lowpass: 900, attack: 0.012, release: 0.12, maxHold: 0.25 },
  crowd: { idle: 0.16, select: 0.2, preRound: 0.26, countdown: 0.3, playing: 0.3, roundEnd: 0.42, matchEnd: 0.5, paused: 0.12,
    exciteGain: 0.35, exciteTau: 2.5, reactGap: 0.6 },
  footstep: { minSpeed: 0.6, cadenceBase: 1.7, cadencePerMs: 0.32, cadenceMin: 1.6, cadenceMax: 4.3 },
  squeak: { minSpeed: 3.5, turn: 9, brake: 16, chance: 0.55, cooldown: 0.5 },
  flyby: { slots: 4, minSpeed: 14, fullSpeed: 55, maxDistance: 32 },
  bounceMin: 0.8,
  dedupe: { window: 0.05, radius: 2 },
  /** Ability sounds that other modules normally play themselves; played by Audio only if nothing was heard. */
  fallbackDelay: 0.12, fallbackRadius: 5,
  storageKey: 'du.audio.v1',
});

/** Ability id -> fallback sound when the ability module stays silent. */
const ABILITY_FALLBACK = {
  'rayne.supersonic_meteor': 'fireWhoosh', 'rayne.hyperbeam_transpierce': 'beamZap',
  'shadow.night_parade': 'clone', 'shadow.mirage_formation': 'clone',
  'gale.optical_camouflage': 'cloak', 'gale.shadow_strike': 'teleport',
  'bear.magnetic_pull': 'magnetHum', 'bear.aegis_barrier': 'aegisDeploy',
  'gouki.tackle_intercept': 'tackle', 'gouki.earthquake_slam': 'earthquake',
  'screws.glue_trap_ball': 'throw', 'screws.auto_turret': 'turretServo',
  'houdini.swap_places': 'teleport', 'houdini.grand_vanish': 'vanish',
  'elsa.glacier_freeze': 'frostCast', 'elsa.absolute_zero': 'absoluteZero',
  'specter.precognition_dodge': 'throw', 'specter.time_reversal': 'stasis',
  'chrono.stasis_field': 'stasis', 'chrono.temporal_reset': 'rewind',
};
/** Ball style -> extra impact layer (fallback). */
const STYLE_IMPACT = { meteor: 'shockwave', freeze: 'freeze', glue: 'glueSplat', beam: 'beamImpact' };

/** Synthesis order: what the first seconds of a match need comes first. */
const PRIORITY = ['floorBounce', 'ballThump', 'catch', 'throw', 'footstep', 'whistle', 'beep', 'beepGo', 'uiClick', 'uiConfirm',
  'crowdLoop', 'heavyHit', 'perfectCatch', 'wallBounce', 'pickup', 'squeak', 'land', 'jump', 'slide', 'flyby', 'whiff',
  'throwHeavy', 'whistleLong', 'buzzer', 'crowdCheer', 'crowdOoh', 'crowdGasp', 'applause'];

const clamp = (x, a, b) => (x < a ? a : x > b ? b : x);

class Voice {
  constructor(ctx) {
    this.gain = ctx.createGain();
    this.panner = ctx.createPanner();
    this.panner.distanceModel = 'inverse';
    this.panner.maxDistance = AUDIO_TUNING.maxDistance;
    this.src = null; this.busy = false; this.id = ''; this.priority = 0; this.start = 0; this.end = 0; this.loop = false;
  }
}

export class Audio {
  static defaults = AUDIO_TUNING;

  constructor() {
    this.ctx = null;
    this.enabled = true;
    this.unlocked = false;
    this.volumes = { ...AUDIO_TUNING.volumes };
    this._muted = false;
    this.buffers = new Map();   // id -> AudioBuffer[]
    this._pcm = new Map();      // id -> { sr, variants: Float32Array[][] } (kept until a context exists)
    this._voices = [];
    this._buses = null;
    this._unsubs = [];
    this._domUnsubs = [];
    this._warned = new Set();
    this._resolved = new Map();
    this._hrtf = false;
    this._synthDone = false;
    this._ambience = [];
    this._crowdLevel = -1;
    this._crowdBase = AUDIO_TUNING.crowd.idle;
    this._excite = 0;
    this._lastReact = -10;
    this._lastFail = -10;
    this._crowdTimer = 0;
    this._pstate = new Map();   // player -> { phase, squeakCd }
    this._flyby = [];
    this._listener = { x: 0, y: 1.7, z: 8, fx: 0, fy: 0, fz: -1 };
    // dedupe ring (id, x, y, z, t); ability-bus ring (x, y, z, t); pending fallbacks
    this._dId = new Array(16).fill(''); this._dPos = new Float32Array(16 * 4); this._dHead = 0;
    this._aPos = new Float32Array(16 * 4).fill(-1e6); this._aHead = 0;
    this._pending = Array.from({ length: 16 }, () => ({ active: false, id: '', x: 0, y: 0, z: 0, spatial: true, vol: 1, pitch: 1, since: 0, due: 0 }));
    this._pos = { x: 0, y: 0, z: 0 };
  }

  // ------------------------------------------------------------------------------------------------ lifecycle
  async init() {
    this._loadSettings();
    const quality = (game.renderer && game.renderer.quality) || (game.config && game.config.quality) || 'high';
    this._hrtf = quality === 'high';
    this._startSynthesis();
    this._installUnlock();
    const on = (name, fn) => this._unsubs.push(game.events.on(name, fn));
    on(EV.BallThrown, (e) => this._onThrown(e));
    on(EV.BallHitPlayer, (e) => this._onHit(e));
    on(EV.BallCaught, (e) => this._onCaught(e));
    on(EV.BallBounced, (e) => this._onBounced(e));
    on(EV.BallPickedUp, (e) => e && e.player && this.play('pickup', e.player.position, 0.6));
    on(EV.CatchWhiff, (e) => e && e.player && this.play('whiff', e.player.chestPosition || e.player.position, 0.5));
    on(EV.BallBlocked, (e) => this._onBlocked(e));
    on(EV.BallPassed, (e) => { if (e && e.teleported && e.to) this._fallback('teleport', e.to.position, 0.5, 1.15); });
    on(EV.PlayerState, (e) => this._onPlayerState(e));
    on(EV.PlayerEliminated, (e) => this._onEliminated(e));
    on(EV.PlayerRevived, (e) => { if (e && e.player) { this.play('revive', e.player.position, 0.8); this._crowd('applause', 0.35, 0.3); } });
    on(EV.RoundCountdown, (e) => { if (e && e.secondsLeft > 0) this.play2D('beep', 1, 1); });
    on(EV.RoundStarted, () => { this.play2D('whistle', 1, 1); this._excite = Math.min(1, this._excite + 0.4); });
    on(EV.RoundEnded, (e) => this._onRoundEnded(e));
    // Single-ball possession: referee whistle on a shot-clock violation / out-of-play award, beeps for the local 3-2-1.
    on(EV.PossessionViolation, () => { this.play2D('whistle', 1, 0.95); this._crowd('crowdOoh', 0.5, 0.3); });
    on(EV.BallAwarded, (e) => { if (e && e.cause === 'outOfArena') this.play2D('whistle', 1, 0.95); });
    on(EV.PossessionWarning, (e) => {
      const lp = game.localPlayer;
      if (e && lp && e.team === lp.team) this.play2D('beep', 0.6, 1 + 0.1 * (3 - (e.secondsLeft | 0)));
    });
    on(EV.MatchEnded, () => { this.play2D('whistleLong', 1, 1.02); this._crowd('crowdCheer', 1, 1, true); this._crowd('applause', 0.9, 0, true); });
    on(EV.MatchStarted, () => { this._startAmbience(); this._excite = 0.3; });
    on(EV.MatchPhase, (e) => { if (e && e.current) this._crowdBase = AUDIO_TUNING.crowd[e.current] ?? AUDIO_TUNING.crowd.playing; });
    on(EV.AbilityCast, (e) => this._onAbilityCast(e));
    on(EV.AbilityFailed, (e) => this._onAbilityFailed(e));
    on(EV.UltCharge, (e) => { if (e && e.becameReady && e.player && e.player.isLocal) this.play2D('ultReady', 1, 1); });
    on(EV.Status, (e) => this._onStatus(e));
    on(EV.Hitstop, (e) => this._duck(e && e.duration));
    // Browsers without a gesture requirement (autoplay flag, test harness) or pages already interacted with start now.
    const activated = typeof navigator !== 'undefined' && navigator.userActivation && navigator.userActivation.hasBeenActive;
    if (activated || (game.config && game.config.autoplay)) this._createContext();
  }

  dispose() {
    for (const u of this._unsubs) u();
    for (const u of this._domUnsubs) u();
    this._unsubs = []; this._domUnsubs = [];
    if (this._worker) { this._worker.terminate(); this._worker = null; }
    if (this.ctx) { try { this.ctx.close(); } catch (e) { /* already closed */ } }
    this.ctx = null; this._voices = []; this.buffers.clear(); this._pcm.clear();
  }

  /** Resumes (or creates) the AudioContext. Must run inside a user gesture on most browsers. */
  unlock() {
    if (!this.ctx) this._createContext();
    const ctx = this.ctx;
    if (!ctx) return;
    if (ctx.state !== 'running') {
      try {
        // iOS Safari only unlocks when a node starts inside the gesture
        const b = ctx.createBuffer(1, 1, ctx.sampleRate);
        const s = ctx.createBufferSource(); s.buffer = b; s.connect(ctx.destination); s.start(0);
      } catch (e) { /* ignore */ }
      const p = ctx.resume();
      if (p && p.then) p.then(() => this._onRunning(), () => {});
    } else this._onRunning();
  }

  // ------------------------------------------------------------------------------------------------ settings
  get muted() { return this._muted; }
  set muted(b) { this.setMuted(b); }
  setMuted(b) { this._muted = !!b; this._applyVolumes(); this._saveSettings(); }
  toggleMute() { this.setMuted(!this._muted); return this._muted; }
  /** @param {'master'|'sfx'|'music'|'crowd'|'ui'} bus @param {number} v 0..1 */
  setVolume(bus, v) {
    if (!(bus in this.volumes) || !Number.isFinite(v)) return;
    this.volumes[bus] = clamp(v, 0, 1);
    this._applyVolumes(); this._saveSettings();
  }
  getVolume(bus) { return this.volumes[bus] ?? 0; }
  setMasterVolume(v) { this.setVolume('master', v); }

  _loadSettings() {
    try {
      const raw = globalThis.localStorage && localStorage.getItem(AUDIO_TUNING.storageKey);
      if (!raw) return;
      const s = JSON.parse(raw);
      for (const k of Object.keys(this.volumes)) if (Number.isFinite(s[k])) this.volumes[k] = clamp(s[k], 0, 1);
      this._muted = !!s.muted;
    } catch (e) { /* storage unavailable: defaults */ }
  }
  _saveSettings() {
    try { globalThis.localStorage && localStorage.setItem(AUDIO_TUNING.storageKey, JSON.stringify({ ...this.volumes, muted: this._muted })); } catch (e) { /* ignore */ }
  }
  _applyVolumes() {
    const B = this._buses;
    if (!B) return;
    const t = this.ctx.currentTime;
    const set = (param, v) => { param.cancelScheduledValues(t); param.setTargetAtTime(v, t, 0.03); };
    set(B.master.gain, this._muted ? 0 : this.volumes.master);
    set(B.sfxGroup.gain, this.volumes.sfx);
    set(B.music.gain, this.volumes.music);
    set(B.crowdVol.gain, this.volumes.crowd);
    set(B.ui.gain, this.volumes.ui);
  }

  // ------------------------------------------------------------------------------------------------ playback API
  /**
   * Plays a sound. `position` (world metres) makes it 3D relative to the camera; null plays 2D.
   * @param {string} id @param {{x:number,y:number,z:number}|null} position @param {number} volume @param {number} pitch
   * @returns {boolean} started
   */
  play(id, position = null, volume = 1, pitch = 1) {
    const key = this._resolve(id);
    if (!key || !this.enabled) return false;
    const def = SOUNDS[key];
    const hasPos = !!position && def.spatial && Number.isFinite(position.x);
    if (def.bus === 'ability' && hasPos) this._recordAbility(position);
    const ctx = this.ctx;
    if (!ctx || ctx.state !== 'running' || !this._buses) return false; // never queue sounds on a suspended clock
    const list = this.buffers.get(key);
    if (!list || !list.length) return false;
    if (hasPos) {
      const L = this._listener;
      const dx = position.x - L.x, dy = position.y - L.y, dz = position.z - L.z;
      if (dx * dx + dy * dy + dz * dz > AUDIO_TUNING.maxDistance * AUDIO_TUNING.maxDistance) return false;
    }
    if (this._isDuplicate(key, hasPos ? position : null)) return false;
    const v = this._acquire(key, def);
    if (!v) return false;
    const buffer = list[(Math.random() * list.length) | 0];
    const rate = clamp((Number.isFinite(pitch) && pitch > 0 ? pitch : 1) * (1 + (Math.random() * 2 - 1) * def.jitter), 0.25, 4);
    const vol = (Number.isFinite(volume) ? Math.max(0, volume) : 1) * def.gain;
    const when = ctx.currentTime;
    this._route(v, def, hasPos, hasPos ? position : null);
    const src = ctx.createBufferSource();
    src.buffer = buffer;
    src.playbackRate.value = rate;
    src.connect(v.gain);
    v.gain.gain.cancelScheduledValues(when);
    v.gain.gain.setValueAtTime(vol, when);
    try { src.start(when); } catch (e) { src.disconnect(); v.busy = false; return false; }
    v.src = src; v.busy = true; v.id = key; v.priority = def.priority; v.start = when; v.loop = false;
    v.end = when + buffer.duration / rate;
    return true;
  }

  /** 2D (non positional) sound: UI, referee, announcer-style cues. */
  play2D(id, volume = 1, pitch = 1) { return this.play(id, null, volume, pitch); }

  /** Is the sound bank rendered (all ids available)? */
  get loaded() { return this._synthDone; }

  // ------------------------------------------------------------------------------------------------ frame
  update(dt, realDt) {
    const ctx = this.ctx;
    if (!ctx || !this._buses || ctx.state !== 'running') return;
    const now = ctx.currentTime;
    // reclaim finished voices (no onended closures)
    for (const v of this._voices) {
      if (v.busy && !v.loop && now > v.end + 0.05) this._free(v);
    }
    this._updatePending();
    if (dt > 0) this._pollPlayers(dt);
    this._updateFlyby(now);
    this._updateCrowd(realDt, now);
  }

  lateUpdate() {
    const ctx = this.ctx, cam = game.camera;
    if (!ctx || !cam) return;
    cam.updateMatrixWorld();
    const e = cam.matrixWorld.elements;
    const L = this._listener;
    L.x = e[12]; L.y = e[13]; L.z = e[14];
    L.fx = -e[8]; L.fy = -e[9]; L.fz = -e[10];
    const ux = e[4], uy = e[5], uz = e[6];
    const l = ctx.listener;
    if (l.positionX) {
      l.positionX.value = L.x; l.positionY.value = L.y; l.positionZ.value = L.z;
      l.forwardX.value = L.fx; l.forwardY.value = L.fy; l.forwardZ.value = L.fz;
      l.upX.value = ux; l.upY.value = uy; l.upZ.value = uz;
    } else if (l.setPosition) {
      l.setPosition(L.x, L.y, L.z);
      l.setOrientation(L.fx, L.fy, L.fz, ux, uy, uz);
    }
  }

  // ------------------------------------------------------------------------------------------------ context & graph
  _createContext() {
    if (this.ctx) return;
    const AC = globalThis.AudioContext || globalThis.webkitAudioContext;
    if (!AC) { this.enabled = false; return; }
    try { this.ctx = new AC({ latencyHint: 'interactive' }); } catch (e) { this.enabled = false; return; }
    const ctx = this.ctx;
    this._buildGraph();
    for (const [id, pcm] of this._pcm) this._toBuffers(id, pcm);
    this._pcm.clear();
    ctx.onstatechange = () => { if (ctx.state === 'running') this._onRunning(); };
    if (ctx.state === 'running') this._onRunning();
  }

  _onRunning() {
    if (this.unlocked) return;
    this.unlocked = true;
    for (const u of this._domUnsubs.splice(0)) u();
    this._installUiClicks();
    this._startAmbience();
  }

  _buildGraph() {
    const ctx = this.ctx, T = AUDIO_TUNING;
    const G = (v = 1) => { const g = ctx.createGain(); g.gain.value = v; return g; };
    const B = {};
    B.master = G(this._muted ? 0 : this.volumes.master);
    const lim = ctx.createDynamicsCompressor();
    lim.threshold.value = -6; lim.knee.value = 6; lim.ratio.value = 8; lim.attack.value = 0.003; lim.release.value = 0.15;
    B.master.connect(lim); lim.connect(ctx.destination);
    B.sfxGroup = G(this.volumes.sfx); B.sfxGroup.connect(B.master);
    B.impact = G(); B.impact.connect(B.sfxGroup);
    B.sfx = G(); B.duckSfx = G(); B.sfx.connect(B.duckSfx); B.duckSfx.connect(B.sfxGroup);
    B.ability = G(); B.ability.connect(B.sfxGroup);
    B.match = G(); B.match.connect(B.sfxGroup);
    B.ui = G(this.volumes.ui); B.ui.connect(B.master);
    B.music = G(this.volumes.music); B.music.connect(B.master);
    B.crowd = G(0); B.crowdDuck = G(); B.crowdLP = ctx.createBiquadFilter();
    B.crowdLP.type = 'lowpass'; B.crowdLP.frequency.value = 18000; B.crowdLP.Q.value = 0.5;
    B.crowdVol = G(this.volumes.crowd);
    B.crowd.connect(B.crowdDuck); B.crowdDuck.connect(B.crowdLP); B.crowdLP.connect(B.crowdVol); B.crowdVol.connect(B.master);
    // gym reverb
    try {
      B.reverb = ctx.createConvolver();
      B.reverb.buffer = this._makeImpulse();
      B.reverbReturn = G(T.reverb.returnGain);
      B.reverb.connect(B.reverbReturn); B.reverbReturn.connect(B.master);
      for (const [bus, level] of Object.entries(T.reverb.send)) {
        const s = G(level); B[bus].connect(s); s.connect(B.reverb);
      }
    } catch (e) { this._warn('reverb', e); }
    this._buses = B;
    for (let i = 0; i < T.voices; i++) this._voices.push(new Voice(ctx));
  }

  /** Stereo impulse response of a large gym: early reflections + exponentially decaying, darkening diffuse tail. */
  _makeImpulse() {
    const ctx = this.ctx, R = AUDIO_TUNING.reverb;
    const sr = ctx.sampleRate, n = Math.floor(R.seconds * sr);
    const ir = ctx.createBuffer(2, n, sr);
    let seed = 12345;
    const rnd = () => { seed = (seed * 1664525 + 1013904223) >>> 0; return seed / 4294967296 * 2 - 1; };
    for (let c = 0; c < 2; c++) {
      const d = ir.getChannelData(c);
      const pre = Math.floor(R.preDelay * sr);
      // early reflections: floor, side walls, bleachers
      const taps = [0.011, 0.019, 0.027, 0.041, 0.053, 0.067, 0.083];
      taps.forEach((t, k) => { const i = pre + Math.floor((t + c * 0.0017 * (k % 3)) * sr); if (i < n) d[i] += (k % 2 ? -1 : 1) * 0.55 * Math.pow(0.8, k); });
      let lp = 0;
      for (let i = pre; i < n; i++) {
        const t = (i - pre) / sr;
        const cutoff = 8000 * Math.exp(-t / 0.7) + 1200;         // air + absorption darken the tail
        const k = 1 - Math.exp(-2 * Math.PI * cutoff / sr);
        lp += k * (rnd() - lp);
        d[i] += lp * Math.exp(-t / R.decay) * 0.9;
      }
    }
    return ir;
  }

  _route(v, def, spatial, position) {
    const bus = this._buses[def.bus] || this._buses.sfx;
    try { v.gain.disconnect(); } catch (e) { /* not connected */ }
    if (spatial) {
      const p = v.panner;
      try { p.disconnect(); } catch (e) { /* not connected */ }
      p.panningModel = this._hrtf ? 'HRTF' : 'equalpower';
      p.refDistance = def.ref; p.rolloffFactor = def.rolloff;
      if (p.positionX) { p.positionX.value = position.x; p.positionY.value = position.y; p.positionZ.value = position.z; }
      else p.setPosition(position.x, position.y, position.z);
      v.gain.connect(p); p.connect(bus);
    } else v.gain.connect(bus);
  }

  _acquire(key, def) {
    let free = null, sameCount = 0, sameOldest = null, victim = null;
    for (const v of this._voices) {
      if (!v.busy) { if (!free) free = v; continue; }
      if (v.id === key) { sameCount++; if (!sameOldest || v.start < sameOldest.start) sameOldest = v; }
      if (!v.loop && (!victim || v.priority < victim.priority || (v.priority === victim.priority && v.start < victim.start))) victim = v;
    }
    if (sameCount >= def.max && sameOldest) { this._free(sameOldest); return sameOldest; } // retrigger the oldest instance
    if (free) return free;
    if (victim && victim.priority <= def.priority) { this._free(victim); return victim; }
    return null;
  }

  _free(v) {
    if (v.src) {
      try { v.src.stop(); } catch (e) { /* already stopped */ }
      try { v.src.disconnect(); } catch (e) { /* ignore */ }
    }
    v.src = null; v.busy = false; v.loop = false; v.id = '';
  }

  // ------------------------------------------------------------------------------------------------ synthesis
  _startSynthesis() {
    const ids = [...PRIORITY.filter((id) => SOUNDS[id]), ...Object.keys(SOUNDS).filter((id) => !PRIORITY.includes(id))];
    this._synthQueue = ids;
    this._received = new Set();
    let worker = null;
    try {
      worker = new Worker(new URL('./synthWorker.js', import.meta.url), { type: 'module' });
    } catch (e) { worker = null; }
    if (!worker) { this._synthMainThread(); return; }
    this._worker = worker;
    worker.onmessage = (e) => {
      const m = e.data || {};
      if (m.done) { worker.terminate(); this._worker = null; this._synthDone = true; return; }
      if (m.error) { this._warn(`synth:${m.id}`, m.error); this._received.add(m.id); return; }
      this._received.add(m.id);
      this._accept(m.id, { sr: m.sr, variants: m.variants });
    };
    worker.onerror = (e) => {
      if (e && e.preventDefault) e.preventDefault();
      worker.terminate(); this._worker = null;
      this._synthMainThread(); // module workers unsupported: render the rest on the main thread in slices
    };
    worker.postMessage({ ids });
  }

  async _synthMainThread() {
    for (const id of this._synthQueue) {
      if (this._received.has(id)) continue;
      await new Promise((r) => setTimeout(r, 0)); // yield between sounds (keeps the loading screen responsive)
      try {
        const { sr, variants } = renderSound(id);
        this._received.add(id);
        this._accept(id, { sr, variants: variants.map((v) => (Array.isArray(v) ? v : [v])) });
      } catch (e) { this._warn(`synth:${id}`, e); }
    }
    this._synthDone = true;
  }

  _accept(id, pcm) {
    if (this.ctx) this._toBuffers(id, pcm);
    else this._pcm.set(id, pcm);
  }

  _toBuffers(id, pcm) {
    const ctx = this.ctx;
    const list = [];
    for (const chans of pcm.variants) {
      try {
        const ab = ctx.createBuffer(chans.length, chans[0].length, pcm.sr);
        for (let c = 0; c < chans.length; c++) ab.copyToChannel(chans[c], c);
        list.push(ab);
      } catch (e) { this._warn(`buffer:${id}`, e); }
    }
    this.buffers.set(id, list);
    if (id === 'crowdLoop' && this.unlocked) this._startAmbience();
  }

  // ------------------------------------------------------------------------------------------------ helpers
  _resolve(id) {
    if (typeof id !== 'string' || !id) return null;
    if (SOUNDS[id]) return id;
    if (this._resolved.has(id)) return this._resolved.get(id);
    let key = SOUND_ALIASES[id] || null;
    if (!key) {
      const low = id.toLowerCase();
      for (const [kw, k] of SOUND_KEYWORDS) if (low.includes(kw.toLowerCase())) { key = k; break; }
      if (!this._warned.has(`id:${id}`)) {
        this._warned.add(`id:${id}`);
        console.warn(`[audio] unknown sound id '${id}'${key ? ` -> using '${key}'` : ' (ignored)'}`);
      }
    }
    this._resolved.set(id, key);
    return key;
  }

  _isDuplicate(key, p) {
    const now = game.time.realNow, D = AUDIO_TUNING.dedupe;
    const r2 = D.radius * D.radius;
    for (let i = 0; i < this._dId.length; i++) {
      if (this._dId[i] !== key) continue;
      const k = i * 4;
      if (now - this._dPos[k + 3] > D.window) continue;
      if (!p) { if (!Number.isFinite(this._dPos[k])) return true; continue; }
      const dx = this._dPos[k] - p.x, dy = this._dPos[k + 1] - p.y, dz = this._dPos[k + 2] - p.z;
      if (dx * dx + dy * dy + dz * dz <= r2) return true;
    }
    const h = this._dHead;
    this._dId[h] = key;
    this._dPos[h * 4] = p ? p.x : NaN; this._dPos[h * 4 + 1] = p ? p.y : NaN; this._dPos[h * 4 + 2] = p ? p.z : NaN;
    this._dPos[h * 4 + 3] = now;
    this._dHead = (h + 1) % this._dId.length;
    return false;
  }

  _recordAbility(p) {
    const h = this._aHead;
    this._aPos[h * 4] = p.x; this._aPos[h * 4 + 1] = p.y; this._aPos[h * 4 + 2] = p.z; this._aPos[h * 4 + 3] = game.time.realNow;
    this._aHead = (h + 1) % 16;
  }

  /** Queues `id` to play shortly unless an ability sound is heard nearby in the meantime. */
  _fallback(id, position, vol = 1, pitch = 1, delay = AUDIO_TUNING.fallbackDelay) {
    if (!position || !this.ctx) return;
    let slot = null;
    for (const s of this._pending) if (!s.active) { slot = s; break; }
    if (!slot) return;
    const now = game.time.realNow;
    slot.active = true; slot.id = id; slot.x = position.x; slot.y = position.y; slot.z = position.z;
    slot.vol = vol; slot.pitch = pitch; slot.since = now - 0.05; slot.due = now + delay;
  }

  _updatePending() {
    const now = game.time.realNow, r2 = AUDIO_TUNING.fallbackRadius ** 2;
    for (const s of this._pending) {
      if (!s.active || now < s.due) continue;
      s.active = false;
      let heard = false;
      for (let i = 0; i < 16; i++) {
        const k = i * 4;
        if (this._aPos[k + 3] < s.since) continue;
        const dx = this._aPos[k] - s.x, dy = this._aPos[k + 1] - s.y, dz = this._aPos[k + 2] - s.z;
        if (dx * dx + dy * dy + dz * dz <= r2) { heard = true; break; }
      }
      if (!heard) { this._pos.x = s.x; this._pos.y = s.y; this._pos.z = s.z; this.play(s.id, this._pos, s.vol, s.pitch); }
    }
  }

  _warn(key, e) {
    if (this._warned.has(key)) return;
    this._warned.add(key);
    console.warn(`[audio] ${key}`, e);
  }

  _pst(p) {
    let st = this._pstate.get(p);
    if (!st) { st = { phase: Math.random(), squeakCd: 0 }; this._pstate.set(p, st); }
    return st;
  }

  static _silent(p) { return !!(p && p.status && typeof p.status.has === 'function' && p.status.has('silentFootsteps')); }

  // ------------------------------------------------------------------------------------------------ gesture unlock + UI
  _installUnlock() {
    if (typeof window === 'undefined') return;
    const fn = () => this.unlock();
    const opts = { capture: true, passive: true };
    for (const ev of ['pointerdown', 'keydown', 'touchend', 'mousedown']) {
      window.addEventListener(ev, fn, opts);
      this._domUnsubs.push(() => window.removeEventListener(ev, fn, opts));
    }
    const vis = () => {
      if (!this.ctx || !this.unlocked) return;
      if (document.hidden) this.ctx.suspend().catch(() => {}); else this.ctx.resume().catch(() => {});
    };
    document.addEventListener('visibilitychange', vis);
    this._unsubs.push(() => document.removeEventListener('visibilitychange', vis));
    const key = (e) => {
      if (e.code !== 'KeyM' || e.repeat || e.ctrlKey || e.metaKey || e.altKey) return;
      const t = e.target;
      if (t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.isContentEditable)) return;
      this.toggleMute();
    };
    window.addEventListener('keydown', key);
    this._unsubs.push(() => window.removeEventListener('keydown', key));
  }

  /** Menu button clicks (outside live play) get a subtle UI click; data-sfx="confirm" / "none" override. */
  _installUiClicks() {
    if (typeof document === 'undefined' || this._uiClicks) return;
    this._uiClicks = true;
    const fn = (e) => {
      const el = e.target && e.target.closest ? e.target.closest('button, [role="button"], [data-sfx]') : null;
      if (!el) return;
      const sfx = el.getAttribute('data-sfx');
      if (sfx === 'none') return;
      const inPlay = game.match && game.match.phase === 'playing' && !game.time.paused;
      if (inPlay && !sfx) return;
      this.play2D(sfx === 'confirm' ? 'uiConfirm' : 'uiClick', 1, 1);
    };
    document.addEventListener('pointerdown', fn, true);
    this._unsubs.push(() => document.removeEventListener('pointerdown', fn, true));
  }

  // ------------------------------------------------------------------------------------------------ crowd & ducking
  _startAmbience() {
    const ctx = this.ctx;
    if (!ctx || ctx.state !== 'running' || !this._buses || this._ambience.length) return;
    const list = this.buffers.get('crowdLoop');
    if (!list || !list.length) return;
    // two loops of different length summed: the repetition period becomes their LCM (minutes)
    for (let i = 0; i < list.length; i++) {
      const src = ctx.createBufferSource();
      src.buffer = list[i]; src.loop = true;
      const g = ctx.createGain(); g.gain.value = 0.6;
      src.connect(g); g.connect(this._buses.crowd);
      src.start(ctx.currentTime + 0.05, Math.random() * list[i].duration);
      this._ambience.push({ src, g });
    }
  }

  _updateCrowd(realDt, now) {
    const C = AUDIO_TUNING.crowd;
    this._excite *= Math.exp(-realDt / C.exciteTau);
    // tension builds with long rallies and in the final seconds of a round
    const m = game.match;
    let tension = 0;
    if (m && m.isPlaying && Number.isFinite(m.timeLeft) && m.timeLeft < 15) tension = 0.15 * (1 - m.timeLeft / 15);
    const base = game.time.paused ? C.paused : this._crowdBase;
    const level = base + (this._excite + tension) * C.exciteGain;
    this._crowdTimer -= realDt;
    if (this._crowdTimer <= 0 && Math.abs(level - this._crowdLevel) > 0.005) {
      this._crowdTimer = 0.1;
      this._crowdLevel = level;
      this._buses.crowd.gain.setTargetAtTime(level, now, 0.35);
    }
  }

  /** Crowd reaction (2D). Throttled so a chain of events does not stack walls of cheering. */
  _crowd(id, vol, excite = 0.3, force = false) {
    this._excite = Math.min(1, this._excite + excite);
    const now = game.time.realNow;
    if (!force && now - this._lastReact < AUDIO_TUNING.crowd.reactGap) return;
    this._lastReact = now;
    this.play2D(id, vol, 1);
  }

  /** Hitstop: dip the continuous beds and filter the crowd so the impact that follows lands with weight. */
  _duck(duration) {
    const ctx = this.ctx, B = this._buses;
    if (!ctx || !B || ctx.state !== 'running') return;
    const D = AUDIO_TUNING.duck;
    const t = ctx.currentTime, hold = clamp(Number.isFinite(duration) ? duration : 0.06, 0.02, D.maxHold);
    const ramp = (param, down, up) => {
      if (param.cancelAndHoldAtTime) param.cancelAndHoldAtTime(t); else param.cancelScheduledValues(t);
      param.setTargetAtTime(down, t, D.attack);
      param.setTargetAtTime(up, t + hold, D.release);
    };
    ramp(B.duckSfx.gain, D.sfx, 1);
    ramp(B.crowdDuck.gain, D.crowd, 1);
    ramp(B.crowdLP.frequency, D.lowpass, 18000);
  }

  // ------------------------------------------------------------------------------------------------ polling
  _pollPlayers(dt) {
    const F = AUDIO_TUNING.footstep, Q = AUDIO_TUNING.squeak;
    for (const p of game.players) {
      const m = p.motor;
      if (!m || !p.position) continue;
      const st = this._pst(p);
      st.squeakCd -= dt;
      const grounded = m.isGrounded !== undefined ? m.isGrounded : p.isGrounded;
      const state = p.fsm && p.fsm.current;
      const speed = Number.isFinite(m.planarSpeed) ? m.planarSpeed : (p.velocity ? Math.hypot(p.velocity.x, p.velocity.z) : 0);
      if (!grounded || m.isSliding || state === 'incapacitated' || state === 'stunned' || speed < F.minSpeed || Audio._silent(p)) {
        if (st.phase > 0.6) st.phase = 0.6; // first step comes quickly when movement resumes
        continue;
      }
      const cadence = clamp(F.cadenceBase + F.cadencePerMs * speed, F.cadenceMin, F.cadenceMax);
      st.phase += cadence * dt;
      if (st.phase >= 1) {
        st.phase -= Math.floor(st.phase);
        this.play('footstep', p.position, 0.35 + 0.65 * Math.min(1, speed / 7.5), 0.94 + Math.min(0.1, speed * 0.012));
      }
      // sneaker squeaks on hard cuts and braking
      if (st.squeakCd <= 0 && speed > Q.minSpeed) {
        const turn = Math.abs(m.yawRate || 0) * speed;
        let brake = 0;
        const a = m.planarAccel, v = p.velocity;
        if (a && v) brake = -(a.x * v.x + a.z * v.z) / Math.max(0.1, speed);
        if ((turn > Q.turn || brake > Q.brake) && Math.random() < Q.chance) {
          this.play('squeak', p.position, 0.5 + Math.min(0.5, (turn + brake) / 60), 0.9 + Math.random() * 0.25);
          st.squeakCd = Q.cooldown;
        } else if (turn > Q.turn || brake > Q.brake) st.squeakCd = Q.cooldown * 0.5;
      }
    }
  }

  /** Looped air whoosh following fast live balls, with distance gain and Doppler pitch. */
  _updateFlyby(now) {
    const ctx = this.ctx, F = AUDIO_TUNING.flyby;
    const list = this.buffers.get('flyby');
    if (!list || !list.length) return;
    if (!this._flyby.length) {
      for (let i = 0; i < F.slots; i++) {
        const g = ctx.createGain(); g.gain.value = 0;
        const p = ctx.createPanner(); p.distanceModel = 'inverse'; p.refDistance = SOUNDS.flyby.ref; p.rolloffFactor = 1.2;
        p.maxDistance = AUDIO_TUNING.maxDistance; p.panningModel = this._hrtf ? 'HRTF' : 'equalpower';
        g.connect(p); p.connect(this._buses.sfx);
        this._flyby.push({ ball: null, src: null, g, p, stopAt: 0, seen: false });
      }
    }
    for (const s of this._flyby) s.seen = false;
    const balls = game.balls && game.balls.active;
    const L = this._listener;
    const paused = game.time.paused || game.time.scale < 0.2;
    if (balls && !paused) {
      for (const b of balls) {
        if (!b || b.state !== 'live' || !b.velocity || !b.position) continue;
        const v = b.velocity, speed = Math.hypot(v.x, v.y, v.z);
        if (speed < F.minSpeed) continue;
        const dx = L.x - b.position.x, dy = L.y - b.position.y, dz = L.z - b.position.z;
        const dist = Math.hypot(dx, dy, dz);
        if (dist > F.maxDistance) continue;
        let s = null;
        for (const x of this._flyby) if (x.ball === b) { s = x; break; }
        if (!s) for (const x of this._flyby) if (!x.ball && !x.src) { s = x; break; }
        if (!s) continue;
        if (!s.src) {
          const src = ctx.createBufferSource();
          src.buffer = list[0]; src.loop = true; src.connect(s.g);
          src.start(now, Math.random() * list[0].duration);
          s.src = src; s.g.gain.cancelScheduledValues(now); s.g.gain.setValueAtTime(0, now);
        }
        s.ball = b; s.seen = true; s.stopAt = 0;
        const approach = (v.x * dx + v.y * dy + v.z * dz) / Math.max(0.1, dist); // m/s toward the listener
        const doppler = clamp(343 / (343 - clamp(approach, -150, 150)), 0.7, 1.5);
        const k = clamp((speed - F.minSpeed) / (F.fullSpeed - F.minSpeed), 0, 1);
        s.g.gain.setTargetAtTime(SOUNDS.flyby.gain * (0.15 + 0.85 * k), now, 0.03);
        s.src.playbackRate.setTargetAtTime((0.8 + 0.5 * k) * doppler, now, 0.03);
        if (s.p.positionX) { s.p.positionX.value = b.position.x; s.p.positionY.value = b.position.y; s.p.positionZ.value = b.position.z; }
        else s.p.setPosition(b.position.x, b.position.y, b.position.z);
      }
    }
    for (const s of this._flyby) {
      if (s.seen || !s.src) continue;
      if (!s.stopAt) { s.g.gain.setTargetAtTime(0, now, 0.05); s.stopAt = now + 0.3; s.ball = null; }
      else if (now >= s.stopAt) {
        try { s.src.stop(); } catch (e) { /* ignore */ }
        try { s.src.disconnect(); } catch (e) { /* ignore */ }
        s.src = null; s.stopAt = 0;
      }
    }
  }

  // ------------------------------------------------------------------------------------------------ events
  _onThrown(e) {
    if (!e || !e.origin) return;
    const kmh = e.speedKmh || 60;
    const k = clamp((kmh - 40) / 130, 0, 1);
    if (e.isPass) this.play('throw', e.origin, 0.4, 0.9);
    else if (kmh > 120 || e.isCounter) this.play('throwHeavy', e.origin, 0.6 + 0.4 * k, 0.9 + 0.2 * k);
    else this.play('throw', e.origin, 0.35 + 0.65 * k, 0.85 + 0.35 * k);
    const style = e.ball && e.ball.style;
    if (style === 'turret') this._fallback('turretPop', e.origin, 1, 1, 0.02);
    if (e.rallyCount >= 3) this._excite = Math.min(1, this._excite + 0.12);
  }

  _onHit(e) {
    if (!e) return;
    const point = e.point || (e.victim && e.victim.chestPosition);
    if (!point) return;
    const kmh = e.speedKmh || 70;
    if (kmh > 110 || (e.outcome === 'eliminated' && kmh > 85)) this.play('heavyHit', point, clamp(kmh / 130, 0.6, 1.2), 1);
    else this.play('ballThump', point, clamp(0.4 + kmh / 150, 0.4, 1), 1);
    const extra = e.ball && STYLE_IMPACT[e.ball.style];
    if (extra) this._fallback(extra, point, 0.9);
    if (e.outcome === 'damaged' || e.outcome === 'prevented' || e.outcome === 'delayed') this._crowd('crowdGasp', 0.7, 0.35);
  }

  _onCaught(e) {
    if (!e) return;
    const point = e.point || (e.catcher && (e.catcher.chestPosition || e.catcher.position));
    if (!point) return;
    if (e.quality === 'perfect') {
      this.play('perfectCatch', point, 1, 1);
      this._crowd('crowdOoh', 0.75, 0.5);
    } else {
      this.play('catch', point, clamp(0.5 + (e.speedKmh || 60) / 160, 0.5, 1), 1);
      if ((e.rallyCount || 0) >= 3) this._excite = Math.min(1, this._excite + 0.15);
    }
  }

  _onBounced(e) {
    if (!e || !e.point) return;
    const s = e.impactSpeed || 0;
    if (s < AUDIO_TUNING.bounceMin) return;
    if (e.floor) this.play('floorBounce', e.point, clamp(Math.pow(s / 9, 1.2), 0.05, 1), 0.93 + Math.min(0.14, s * 0.01));
    else this.play('wallBounce', e.point, clamp(s / 12, 0.05, 1), 1);
    const style = e.ball && e.ball.style;
    if (style === 'glue' && s > 2) this._fallback('glueSplat', e.point, 0.8);
  }

  _onBlocked(e) {
    if (!e || !e.point) return;
    const b = e.blocker;
    if (b && b.hero !== undefined) this.play('ballThump', e.point, 0.55, 1.15); // ball deflected off a held ball / arms
    else this._fallback('shieldImpact', e.point, 0.9);
  }

  _onPlayerState(e) {
    const p = e && e.player;
    if (!p || !p.position || Audio._silent(p)) return;
    const prev = e.previous, cur = e.current;
    if (cur === 'airborne' && (prev === 'grounded' || prev === 'sprinting' || prev === 'chargingThrow' || prev === 'catching')) {
      this.play('jump', p.position, 0.7, 1);
    } else if (prev === 'airborne' && cur !== 'airborne' && cur !== 'incapacitated') {
      this.play('land', p.position, 0.8, 1);
    }
    if (cur === 'sliding') {
      const sp = p.motor && Number.isFinite(p.motor.planarSpeed) ? p.motor.planarSpeed : 6;
      this.play('slide', p.position, clamp(0.4 + sp / 12, 0.4, 1), 0.95 + Math.random() * 0.1);
      if (Math.random() < 0.5) this.play('squeak', p.position, 0.6, 1);
    }
  }

  _onEliminated(e) {
    const p = e && e.player;
    if (!p) return;
    if (Math.random() < 0.6) this._crowd('crowdCheer', 0.75, 0.6, true);
    else { this._crowd('crowdOoh', 0.7, 0.5, true); this.play2D('applause', 0.4, 1); }
  }

  _onRoundEnded(e) {
    if (e && e.reason === 'time') this.play2D('buzzer', 1, 1);
    this.play2D('whistleLong', 1, 1);
    this._crowd('crowdCheer', 0.9, 0.8, true);
  }

  _onAbilityCast(e) {
    const p = e && e.player;
    if (!p || !p.position || !e.ability) return;
    const id = e.ability.id || (e.ability.def && e.ability.def.id);
    const fb = ABILITY_FALLBACK[id];
    if (fb) this._fallback(fb, p.chestPosition || p.position, 0.9);
    if (e.slot === 'ultimate') this._crowd('crowdOoh', 0.5, 0.4);
  }

  _onAbilityFailed(e) {
    if (!e || !e.player || !e.player.isLocal) return;
    if (e.reason === 'notPlaying' || e.reason === 'passive' || e.reason === 'busy') return;
    const now = game.time.realNow;
    if (now - this._lastFail < 0.3) return;
    this._lastFail = now;
    this.play2D('abilityFail', 1, 1);
  }

  _onStatus(e) {
    if (!e || !e.player || !e.applied) return;
    const pos = e.player.chestPosition || e.player.position;
    if (e.type === 'frozen') this._fallback('freeze', pos, 0.9);
    else if (e.type === 'cloaked') this._fallback('cloak', pos, 0.7);
  }
}
