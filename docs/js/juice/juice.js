// ---------------------------------------------------------------------------------------------------------------
// Juice - the game-feel pipeline (the spec's "JuiceManager"). Contract: Tools/web/WEB_ARCHITECTURE.md §3.5.
//
// Every impact in Dodgeball Ultra is sold by the same ordered pipeline, scaled by ONE number: the intensity of the
// ball's speed on the 40..220 km/h curve (JUICE.intensity). A 45 km/h lob barely taps the screen; a 220 km/h Rally
// cannon freezes the frame for 0.1 s, rattles the camera, flattens the ball against the body and flashes the victim.
//
//   BallHitPlayer ──► 1 HITSTOP   game.time.hitstop(0.03..0.1 s, x1.25 on elimination) + EV.Hitstop   (scaled clock)
//                     2 SHAKE     Perlin trauma shake, distance falloff unless the local player is involved
//                     3 SQUASH    volume-preserving squash of ball.visual along the contact normal + damped spring back
//                     4 FLASH     0.05 s white flash on the victim's materials (player.avatar.flash)
//                     5 PULSE     renderer grade pulse ('hit' / 'heavyHit'), FOV kick when the local player is hit
//   BallCaught perfect ─► same pipeline, gold flash on the catcher, 0.1 s hitstop, 'perfectCatch' pulse, FOV punch-in
//   BallCaught normal  ─► light: shake + squash only
//   PlayerEliminated   ─► heavy pulse (merged with the hit that caused it; standalone juice for delayed/ability KOs)
//   also: BallBounced / BallBlocked squash, local throw & ultimate-cast accents, ability damage without a ball.
//
// TIME: Juice runs entirely on UNSCALED time (realDt / game.time.realNow). The hitstop freezes gameplay (scaled time)
// while the shake, squash spring and flashes keep animating - that contrast is what makes a freeze-frame read as
// "impact" instead of "lag".
//
// DECOUPLING: Juice only observes events (Subject-Observer) and talks to optional systems through `game`
// (game.renderer?.pulse, game.cameraRig?.addFovKick, player.avatar?.flash). Any of them can be missing.
// ---------------------------------------------------------------------------------------------------------------
import * as THREE from 'three';
import { game } from '../game.js';
import { EV } from '../core/events.js';
import { HITSTOP_MIN, HITSTOP_MAX, HIT_FLASH_DURATION, MAX_BALL_SPEED_KMH, MS_TO_KMH } from '../core/constants.js';
import { SLOT } from '../abilities/abilityBase.js';
import { Perlin1D } from './noise.js';
import {
  clamp, clamp01, lerp, intensityForSpeed, hitstopDuration, distanceFalloff, shakeEnvelope,
  dampedImpulse, impulsePeakFactor, omegaForSettle, squashScales,
} from './juiceMath.js';

const DEG = Math.PI / 180;

/**
 * All juice tuning in one place. Amplitudes of shakes are in "trauma units": 1.0 == the full shake at trauma 1
 * (JUICE.shake.maxOffset metres / maxRotationDeg degrees). Times are real (unscaled) seconds.
 */
export const JUICE = Object.freeze({
  /** Ball speed -> 0..1 intensity (ease-in-out from `floor` at minKmh to 1 at the 220 km/h Rally cap). */
  intensity: Object.freeze({ minKmh: 40, maxKmh: MAX_BALL_SPEED_KMH, floor: 0.25 }),

  hitstop: Object.freeze({
    min: HITSTOP_MIN,              // 0.03 s (spec)
    max: HITSTOP_MAX,              // 0.10 s (spec)
    timeScale: 0.02,               // time scale during the freeze (a hair above 0 so physics "creeps" instead of dying)
    eliminationMul: 1.25,          // eliminations hold the frame a little longer (still clamped to max)
    perfectCatch: 0.10,            // perfect catch = the longest freeze: it's the skill moment of the game
    standaloneElimination: 0.08,   // elimination that did not come from a juiced ball hit (Delayed Impact, tackles)
    abilityDamageMin: 0.03,        // ability damage without a ball (shockwaves, tackles) - scaled by damage/100
    abilityDamageMax: 0.06,
  }),

  shake: Object.freeze({
    /**
     * Camera-space translation at shake 1.0 and noise peak (x right, y up, z back), metres. The fBm noise has an RMS
     * of ~0.33 and stays within ~70% of the peak 95% of the time. Small: the camera is only 4.3 m from the hero.
     */
    maxOffset: Object.freeze({ x: 0.09, y: 0.07, z: 0.05 }),
    /** Camera-space rotation at shake 1.0, degrees. Roll sells impacts best; yaw/pitch kept low to preserve aim. */
    maxRotationDeg: Object.freeze({ pitch: 1.6, yaw: 1.4, roll: 3.2 }),
    traumaFrequency: 18,           // Hz of the trauma noise (base octave)
    octaves: 3,                    // fBm octaves (sway + rattle)
    lacunarity: 2.1,
    gain: 0.5,
    traumaDecay: 1.5,              // trauma lost per real second (linear) - trauma 1 fully settles in ~0.67 s
    traumaExponent: 2,             // shake = trauma^2 (small trauma -> almost nothing, big trauma -> violent)
    maxTimedShakes: 8,             // pooled explicit shakes layered on top of trauma
    maxTotal: 1.25,                // cap on the combined (trauma^2 + timed) weight so stacked hits never go insane

    hitAmplitudeMin: 0.25,         // hit shake at intensity 0 ... 1
    hitAmplitudeMax: 0.85,
    hitFrequency: 24,              // Hz: impacts are "tight" shakes
    hitDuration: 0.35,
    localMul: 1.4,                 // local player is attacker/victim/catcher: stronger, no distance falloff
    falloffNear: 4,                // metres from the camera with full strength
    falloffFar: 26,                // ...fading to `falloffFloor` here (whole court is ~20 m from a sideline camera)
    falloffFloor: 0.08,
    perfectCatchAmplitude: 0.8,
    perfectCatchDuration: 0.4,
    normalCatchAmplitude: 0.3,     // x intensity
    normalCatchDuration: 0.22,
    eliminationExtraTrauma: 0.25,  // added on top of the hit shake when it eliminated
    eliminationAmplitude: 0.75,    // standalone elimination
    eliminationDuration: 0.45,
    blockedAmplitude: 0.35,        // shield / clone / turret blocks a live ball
    blockedDuration: 0.25,
    negatedScale: 0.3,             // hit negated by invulnerability / evasion: light juice only
    abilityDamageAmplitude: 0.45,
    localThrowTrauma: 0.18,        // x intensity: a full-power local throw kicks the camera a little
    ultimateTrauma: 0.3,           // local ultimate cast
  }),

  squash: Object.freeze({
    min: 0.2,                      // peak compression along the normal at intensity 0 ... 1 (fraction of the diameter)
    max: 0.55,
    maxAllowed: 0.8,               // hard safety clamp for external callers
    duration: 0.18,                // real seconds for the spring to settle to `settleResidual`
    zeta: 0.35,                    // damping ratio: under-damped -> squash, overshoot into stretch, wobble, settle
    settleResidual: 0.02,
    endAfter: 1.3,                 // x duration: release the pivot once the residual is invisible
    maxActive: 16,                 // pooled pivots
    normalCatchScale: 0.6,         // a normal catch squashes 60% of a hit at the same speed
    perfectCatchScale: 1.0,
    negatedScale: 0.6,
    bounceScale: 0.7,              // court bounces squash 70% of a player hit at the same speed
    minBounceSpeed: 3,             // m/s: slower bounces/rolls do not squash
  }),

  flash: Object.freeze({
    duration: HIT_FLASH_DURATION,  // 0.05 s white hit-flash (spec)
    hitColor: 0xffffff,
    perfectCatchColor: 0xffd66b,   // warm gold
    perfectCatchDuration: 0.1,     // gold reads a bit longer than the hit flash
  }),

  /** Full-screen grade pulses through game.renderer.pulse(type, intensity, duration). */
  pulse: Object.freeze({
    hit: 0.55,                     // x intensity
    hitDuration: 0.28,
    elimination: 1.0,
    eliminationDuration: 0.5,
    perfectCatch: 1.0,
    perfectCatchDuration: 0.45,
    ultimate: 0.8,
    ultimateDuration: 0.6,
    remoteScale: 0.45,             // events that do not involve the local player pulse softer
  }),

  /** Camera FOV kicks (degrees, + = wider) through game.cameraRig.addFovKick(deg, duration). */
  fov: Object.freeze({
    localThrowKick: 1.5,           // x intensity
    localHitKick: 3.5,
    perfectCatchKick: -4,          // punch-in
    ultimateKick: 5,
    kickDuration: 0.35,
  }),

  /** Real seconds in which a PlayerEliminated / PlayerDamaged is considered part of the ball hit just juiced. */
  mergeWindow: 0.35,
});

// ------------------------------------------------------------------ module-scope temporaries (no per-frame allocs)
const _UP = new THREE.Vector3(0, 1, 0);
const _n = new THREE.Vector3();
const _v = new THREE.Vector3();
const _q = new THREE.Quaternion();
const _scales = { axis: 1, perp: 1 };
const WHITE = new THREE.Color(JUICE.flash.hitColor);
const GOLD = new THREE.Color(JUICE.flash.perfectCatchColor);

/**
 * One pooled squash & stretch "pivot rotation trick":
 *
 *     parent ── pivot (position p0, rotation R, scale S) ── counter (rotation R⁻¹, position -R⁻¹·p0) ── object
 *
 * The combined matrix T(p0)·R·S·R⁻¹·T(-p0) scales about the object's origin along R·(+Y) = the contact normal in the
 * parent's space. With S = identity the chain is exactly identity, so the object's own local transform is never
 * touched: other systems (ball spin, hand attachment) keep animating it while it squashes, and releasing the slot
 * re-parents it with its original transform intact.
 */
class SquashSlot {
  constructor() {
    this.pivot = new THREE.Object3D();
    this.pivot.name = 'juice:squashPivot';
    this.counter = new THREE.Object3D();
    this.counter.name = 'juice:squashCounter';
    this.pivot.add(this.counter);
    this.object = null;   // the squashed Object3D
    this.t = 0;           // real seconds since impact
    this.v0 = 0;          // spring impulse (1/s)
    this.omega = 1;       // natural frequency (rad/s)
    this.zeta = 0.35;
    this.duration = 0.18;
  }
  get active() { return this.object !== null; }

  /**
   * Insert the pivot chain above `object` with its +Y aligned to `normalWorld`. Returns false if `object` has no parent.
   * @param {THREE.Object3D} object
   * @param {THREE.Vector3} normalWorld
   */
  attach(object, normalWorld) {
    const parent = object.parent;
    if (!parent) return false;
    this._align(parent, object.position, normalWorld);
    const index = parent.children.indexOf(object);
    parent.add(this.pivot);          // appended at the end ...
    this.counter.add(object);        // ... object leaves `parent` (Object3D.add removes it from its old parent) ...
    moveChild(parent, this.pivot, index); // ... and the pivot takes its old slot so child order is preserved.
    this.pivot.scale.set(1, 1, 1);
    this.object = object;
    return true;
  }

  /** Re-aim an active slot at a new normal (a new impact while still wobbling). */
  realign(normalWorld) {
    const parent = this.pivot.parent;
    if (parent) this._align(parent, this.pivot.position, normalWorld);
  }

  _align(parent, p0, normalWorld) {
    // World normal -> parent space (inverse of the parent's world rotation; parent scale is assumed uniform).
    parent.getWorldQuaternion(_q).invert();
    _n.copy(normalWorld).applyQuaternion(_q);
    if (!(_n.lengthSq() > 1e-8)) _n.copy(_UP); else _n.normalize();
    this.pivot.position.copy(p0);
    this.pivot.quaternion.setFromUnitVectors(_UP, _n);
    this.counter.quaternion.copy(this.pivot.quaternion).invert();
    this.counter.position.copy(p0).applyQuaternion(this.counter.quaternion).negate();
  }

  /** Arm the damped spring so the first compression peak equals `peak` and the wobble settles in `duration`. */
  start(peak, duration, zeta) {
    this.t = 0;
    this.zeta = zeta;
    this.duration = Math.max(0.03, duration);
    this.omega = omegaForSettle(this.duration, zeta, JUICE.squash.settleResidual);
    this.v0 = (peak * this.omega) / impulsePeakFactor(zeta);
  }

  /** Advance on real time. Returns false when finished (slot released). */
  update(realDt) {
    if (!this.object) return false;
    // Someone else re-parented the object mid-squash: step aside without touching it.
    if (this.object.parent !== this.counter) { this.release(); return false; }
    this.t += realDt;
    if (this.t >= this.duration * JUICE.squash.endAfter) { this.release(); return false; }
    const s = dampedImpulse(this.t, this.v0, this.omega, this.zeta); // + = squash, - = stretch (overshoot)
    squashScales(clamp(s, -0.6, JUICE.squash.maxAllowed), _scales);
    this.pivot.scale.set(_scales.perp, _scales.axis, _scales.perp);
    return true;
  }

  /** Restore the object under its original parent (same child index, untouched local transform) and park the pivot. */
  release() {
    const obj = this.object;
    this.object = null;
    const parent = this.pivot.parent;
    if (obj && obj.parent === this.counter) {
      if (parent) {
        const index = parent.children.indexOf(this.pivot);
        parent.add(obj);
        moveChild(parent, obj, index);
      } else {
        this.counter.remove(obj); // the whole chain was detached by its owner - leave the object detached too
      }
    }
    if (parent) parent.remove(this.pivot);
    this.pivot.scale.set(1, 1, 1);
  }
}

/**
 * Flash colours are always handed to the avatar as THREE.Color (callers may pass hex numbers or CSS strings).
 * Cached per value: the avatar may keep the reference for the flash's duration, so a shared scratch colour would be
 * unsafe, and there are only a handful of distinct flash colours in the game.
 */
const _colorCache = new Map();
function toColor(c) {
  if (c && c.isColor) return c;
  const key = c == null ? 0xffffff : c;
  let col = _colorCache.get(key);
  if (!col) {
    col = new THREE.Color(key);
    if (_colorCache.size < 64) _colorCache.set(key, col);
  }
  return col;
}

/** Move `child` (already in parent.children) to `index`, preserving the order of the others. */
function moveChild(parent, child, index) {
  const list = parent.children;
  const cur = list.indexOf(child);
  if (index < 0 || cur < 0 || cur === index) return;
  list.splice(cur, 1);
  list.splice(Math.min(index, list.length), 0, child);
}

/** Explicit timed shake layered on top of trauma. */
class TimedShake {
  constructor() { this.active = false; this.amp = 0; this.freq = 0; this.dur = 0; this.t = 0; this.phase = 0; this.w = 0; }
}

/** Deferred PlayerEliminated / PlayerDamaged record (resolved in update, after the matching BallHitPlayer). */
class PendingEvent {
  constructor() { this.type = ''; this.player = null; this.attacker = null; this.point = new THREE.Vector3(); this.hasPoint = false; this.damage = 0; }
}

/**
 * The juice system. Registered by main.js with game.addSystem(new Juice(), ORDER.JUICE) and exposed as game.juice.
 * Outputs read by the camera rig every frame: `shakeOffset` (camera-space metres) and `shakeRotation` (radians).
 */
export class Juice {
  constructor() {
    /** Camera-space shake translation (x right, y up, z back), metres. Read by CameraRig. */
    this.shakeOffset = new THREE.Vector3();
    /** Camera-space shake rotation (x pitch, y yaw, z roll), radians. Read by CameraRig. */
    this.shakeRotation = new THREE.Euler(0, 0, 0, 'YXZ');
    /** Current combined shake weight (0..JUICE.shake.maxTotal) - handy for UI/post effects. */
    this.shakeAmount = 0;
    /** Trauma 0..1 (decays per real second; shake = trauma^2). */
    this.trauma = 0;
    /** Player-facing comfort settings (?shake=0..1 scales every shake; ?flashes=0 disables flashes/pulses). */
    this.settings = { shakeScale: 1, hitstop: true, flashes: true };

    // One independent noise curve per camera channel: tx, ty, tz, pitch, yaw, roll (different seeds = uncorrelated).
    this._noise = [11, 23, 37, 41, 53, 67].map((s) => new Perlin1D(s));
    this._channel = new Float32Array(6);
    this._shakes = Array.from({ length: JUICE.shake.maxTimedShakes }, () => new TimedShake());
    this._phaseSeed = 0;
    this._squash = Array.from({ length: JUICE.squash.maxActive }, () => new SquashSlot());
    this._pending = [];                 // PendingEvent pool (grows to the peak simultaneous count, then reused)
    this._pendingCount = 0;
    this._recentHits = new Map();       // Player -> { at, eliminated } of the last ball hit juiced on that player
    this._unsubs = [];
  }

  init() {
    const p = game.config && game.config.params;
    if (p && typeof p.get === 'function') {
      const s = Number(p.get('shake'));
      if (p.has('shake') && Number.isFinite(s)) this.settings.shakeScale = clamp(s, 0, 2);
      if (p.get('flashes') === '0') this.settings.flashes = false;
      if (p.get('hitstop') === '0') this.settings.hitstop = false;
    }
    const on = (name, fn) => this._unsubs.push(game.events.on(name, fn));
    on(EV.BallHitPlayer, (e) => this._onBallHit(e));
    on(EV.BallCaught, (e) => this._onCaught(e));
    on(EV.PlayerEliminated, (e) => this._defer('eliminated', e.player, e.attacker, e.point, 0));
    on(EV.PlayerDamaged, (e) => this._defer('damaged', e.player, e.attacker, e.point, e.damage));
    on(EV.BallBounced, (e) => this._onBounce(e));
    on(EV.BallBlocked, (e) => this._onBlocked(e));
    on(EV.BallThrown, (e) => this._onThrown(e));
    on(EV.AbilityCast, (e) => this._onAbilityCast(e));
    on(EV.MatchStarted, () => this._recentHits.clear()); // drop references to the previous match's players
  }

  // =================================================================== public API (contract §3.5)

  /** 0..1 juice intensity for a ball speed in km/h (JUICE.intensity curve). */
  intensityForSpeed(speedKmh) {
    const c = JUICE.intensity;
    return intensityForSpeed(speedKmh, c.minKmh, c.maxKmh, c.floor);
  }

  /**
   * Full ball-hit pipeline. Called for every EV.BallHitPlayer; other modules may call it for custom impacts.
   * @param {{point?:THREE.Vector3, normal?:THREE.Vector3, speedKmh?:number, ball?:object, victim?:object,
   *          attacker?:object, eliminated?:boolean, negated?:boolean, local?:boolean}} req
   */
  playHit(req) {
    const J = JUICE;
    const I = this.intensityForSpeed(req.speedKmh || 0);
    const local = !!req.local;
    const negated = !!req.negated;
    const eliminated = !!req.eliminated && !negated;
    const light = negated ? J.shake.negatedScale : 1;

    // 1) HITSTOP - dynamic 0.03..0.1 s from speed, x1.25 when it eliminated (clamped to the spec window).
    if (!negated) this.hitstop(hitstopDuration(I, J.hitstop.min, J.hitstop.max, eliminated, J.hitstop.eliminationMul));

    // 2) SHAKE - Perlin, stronger & un-attenuated when the local player is involved, distance falloff otherwise.
    const amp = lerp(J.shake.hitAmplitudeMin, J.shake.hitAmplitudeMax, I) * light * (local ? J.shake.localMul : 1);
    this.shake(amp, J.shake.hitFrequency, J.shake.hitDuration, local ? null : req.point);
    if (eliminated) this.addTrauma(J.shake.eliminationExtraTrauma * (local ? 1 : this._falloff(req.point)));

    // 3) SQUASH & STRETCH - the ball flattens against the body along the contact normal, then springs back.
    if (req.ball && req.ball.visual) {
      const n = req.normal || this._fallbackNormal(req.ball, req.point);
      this.squash(req.ball.visual, n, lerp(J.squash.min, J.squash.max, I) * (negated ? J.squash.negatedScale : 1), J.squash.duration);
    }

    if (!negated) {
      // 4) FLASH - 0.05 s white on the victim's materials.
      if (req.victim) this.flash(req.victim, WHITE, J.flash.duration);
      // 5) PULSE - renderer grade (exposure / chromatic aberration / vignette), heavier on eliminations.
      const scale = local ? 1 : J.pulse.remoteScale;
      if (eliminated) this.pulse('heavyHit', J.pulse.elimination * scale, J.pulse.eliminationDuration);
      else this.pulse('hit', J.pulse.hit * (0.4 + 0.6 * I) * scale, J.pulse.hitDuration);
      if (req.victim && req.victim.isLocal) game.cameraRig?.addFovKick?.(J.fov.localHitKick * (0.5 + 0.5 * I), J.fov.kickDuration);
    }

    // Remember it so the PlayerEliminated/PlayerDamaged emitted by the same hit is not juiced twice.
    if (req.victim) {
      let rec = this._recentHits.get(req.victim);
      if (!rec) { rec = { at: 0, eliminated: false }; this._recentHits.set(req.victim, rec); }
      rec.at = game.time.realNow;
      rec.eliminated = eliminated;
    }
  }

  /**
   * Catch pipeline. Perfect catch = full pipeline (0.1 s hitstop, strong shake, gold flash on the catcher, perfect pulse,
   * FOV punch-in for the local catcher); normal catch = light (shake + squash).
   * @param {{point?:THREE.Vector3, normal?:THREE.Vector3, speedKmh?:number, ball?:object, catcher?:object,
   *          thrower?:object, perfect?:boolean, local?:boolean}} req
   */
  playCatch(req) {
    const J = JUICE;
    const I = this.intensityForSpeed(req.speedKmh || 0);
    const local = !!req.local;
    const n = req.normal || this._catchNormal(req);

    if (req.perfect) {
      this.hitstop(J.hitstop.perfectCatch);
      this.shake(J.shake.perfectCatchAmplitude * (local ? J.shake.localMul : 1), J.shake.hitFrequency,
        J.shake.perfectCatchDuration, local ? null : req.point);
      if (req.ball && req.ball.visual) this.squash(req.ball.visual, n, J.squash.max * J.squash.perfectCatchScale, J.squash.duration);
      if (req.catcher) this.flash(req.catcher, GOLD, J.flash.perfectCatchDuration);
      this.pulse('perfectCatch', J.pulse.perfectCatch * (local ? 1 : J.pulse.remoteScale), J.pulse.perfectCatchDuration);
      if (req.catcher && req.catcher.isLocal) game.cameraRig?.addFovKick?.(J.fov.perfectCatchKick, J.fov.kickDuration * 1.3);
    } else {
      this.shake(J.shake.normalCatchAmplitude * I * (local ? J.shake.localMul : 1), J.shake.hitFrequency,
        J.shake.normalCatchDuration, local ? null : req.point);
      if (req.ball && req.ball.visual) {
        this.squash(req.ball.visual, n, lerp(J.squash.min, J.squash.max, I) * J.squash.normalCatchScale, J.squash.duration);
      }
    }
  }

  /**
   * Freeze-frame: gameplay (scaled) time runs at `scale` for `duration` REAL seconds. Clamped to the spec's
   * 0.03..0.1 s unless `allowLong`. Overlapping requests extend (never shorten) - see GameTime.hitstop.
   * @returns {number} the applied duration (0 when disabled)
   */
  hitstop(duration, scale = JUICE.hitstop.timeScale, allowLong = false) {
    if (!this.settings.hitstop || !(duration > 0)) return 0;
    const d = allowLong ? duration : clamp(duration, JUICE.hitstop.min, JUICE.hitstop.max);
    const s = clamp(Number.isFinite(scale) ? scale : 0, 0, 1);
    game.time.hitstop(d, s);
    game.events.emit(EV.Hitstop, { duration: d, scale: s });
    return d;
  }

  /**
   * Explicit timed shake layered on top of the trauma shake.
   * @param {number} amplitude trauma units (1 = full shake), before falloff
   * @param {number} frequency noise frequency in Hz (higher = tighter rattle)
   * @param {number} duration real seconds; envelope (1 - t/d)^2
   * @param {THREE.Vector3|null} sourcePos world position for distance falloff from the camera (null = no falloff)
   */
  shake(amplitude, frequency = JUICE.shake.hitFrequency, duration = JUICE.shake.hitDuration, sourcePos = null) {
    let amp = amplitude * (sourcePos ? this._falloff(sourcePos) : 1);
    if (!(amp > 1e-3) || !(duration > 0)) return;
    // Take a free slot, or steal the weakest remaining one.
    let slot = null, weakest = null, weakestW = Infinity;
    for (const s of this._shakes) {
      if (!s.active) { slot = s; break; }
      const w = s.amp * shakeEnvelope(s.t, s.dur);
      if (w < weakestW) { weakestW = w; weakest = s; }
    }
    slot = slot || weakest;
    amp = Math.min(amp, 2);
    slot.active = true; slot.amp = amp; slot.freq = Math.max(1, frequency); slot.dur = duration; slot.t = 0; slot.w = amp;
    // Golden-ratio phase walk: consecutive shakes sample different stretches of the noise (no identical repeats).
    this._phaseSeed = (this._phaseSeed + 61.803) % 997;
    slot.phase = this._phaseSeed;
  }

  /** Add trauma (0..1, clamped). The trauma shake is trauma^2 and decays at JUICE.shake.traumaDecay per real second. */
  addTrauma(t) {
    if (t > 0) this.trauma = Math.min(1, this.trauma + t);
  }

  /**
   * Volume-preserving squash & stretch of `object3D` along an arbitrary world normal: scale (1 - s) along the normal and
   * 1/sqrt(1 - s) across it, released as a damped spring (squash -> overshoot stretch -> settle) on real time.
   * The object's own transform is never modified and is fully restored when the spring settles.
   * @param {THREE.Object3D} object3D e.g. ball.visual (must have a parent)
   * @param {THREE.Vector3} normalWorld contact normal (either sign; normalised internally)
   * @param {number} intensity peak compression fraction along the normal (0..0.8), e.g. 0.4 = 40% flatter
   * @param {number} duration real seconds for the wobble to settle
   * @returns {boolean} whether a squash was started
   */
  squash(object3D, normalWorld, intensity, duration = JUICE.squash.duration) {
    if (!object3D || !normalWorld) return false;
    const peak = clamp(intensity, 0, JUICE.squash.maxAllowed);
    if (peak < 0.01) return false;
    // Already squashing this object? Re-aim and restart the spring (keeps the stronger of the two impacts).
    for (const s of this._squash) {
      if (s.object === object3D) {
        s.realign(normalWorld);
        s.start(peak, duration, JUICE.squash.zeta);
        return true;
      }
    }
    let slot = null, oldest = null;
    for (const s of this._squash) {
      if (!s.active) { slot = s; break; }
      if (!oldest || s.t > oldest.t) oldest = s;
    }
    if (!slot) { oldest.release(); slot = oldest; }
    if (!slot.attach(object3D, normalWorld)) return false;
    slot.start(peak, duration, JUICE.squash.zeta);
    return true;
  }

  /**
   * Material hit-flash on a player (delegates to player.avatar.flash, which drives the materials' emissive).
   * @param {object} player
   * @param {THREE.Color|number} color defaults to white
   * @param {number} duration defaults to the spec's 0.05 s
   */
  flash(player, color = WHITE, duration = JUICE.flash.duration) {
    if (!this.settings.flashes || !player || !player.avatar || typeof player.avatar.flash !== 'function') return;
    player.avatar.flash(toColor(color), duration > 0 ? duration : JUICE.flash.duration);
  }

  /** Renderer grade pulse (guarded; disabled with ?flashes=0). */
  pulse(type, intensity, duration) {
    if (!this.settings.flashes || !(intensity > 0)) return;
    const r = game.renderer;
    if (r && typeof r.pulse === 'function') r.pulse(type, clamp01(intensity), duration);
  }

  /** Stop every shake/squash immediately (restores all squashed objects). */
  reset() {
    this.trauma = 0;
    for (const s of this._shakes) s.active = false;
    for (const s of this._squash) if (s.active) s.release();
    this._pendingCount = 0;
    this.shakeOffset.set(0, 0, 0);
    this.shakeRotation.set(0, 0, 0);
    this.shakeAmount = 0;
  }

  // =================================================================== frame update (real time)

  update(dt, realDt) {
    this._resolvePending();
    this._updateShake(realDt, game.time.realNow);
    for (const s of this._squash) if (s.active) s.update(realDt);
  }

  dispose() {
    for (const u of this._unsubs) u();
    this._unsubs.length = 0;
    this.reset();
    this._recentHits.clear();
  }

  // =================================================================== internals

  /** Trauma^2 noise + timed shakes -> shakeOffset / shakeRotation. Noise is sampled on realNow (frame-rate independent). */
  _updateShake(realDt, now) {
    const S = JUICE.shake;
    this.trauma = Math.max(0, this.trauma - S.traumaDecay * realDt);
    const traumaW = Math.pow(this.trauma, S.traumaExponent);
    let total = traumaW;
    for (const s of this._shakes) {
      if (!s.active) continue;
      s.t += realDt;
      const env = shakeEnvelope(s.t, s.dur);
      if (env <= 0) { s.active = false; s.w = 0; continue; }
      s.w = s.amp * env;
      total += s.w;
    }
    const scale = this.settings.shakeScale;
    if (total < 1e-4 || scale <= 0) {
      this.shakeOffset.set(0, 0, 0);
      this.shakeRotation.set(0, 0, 0);
      this.shakeAmount = 0;
      return;
    }
    const norm = (total > S.maxTotal ? S.maxTotal / total : 1) * scale;
    const ch = this._channel;
    for (let i = 0; i < 6; i++) {
      const noise = this._noise[i];
      let v = traumaW > 0 ? traumaW * noise.fbm(now * S.traumaFrequency, S.octaves, S.lacunarity, S.gain) : 0;
      for (const s of this._shakes) {
        if (s.active && s.w > 0) v += s.w * noise.fbm(now * s.freq + s.phase, S.octaves, S.lacunarity, S.gain);
      }
      ch[i] = v * norm;
    }
    this.shakeOffset.set(ch[0] * S.maxOffset.x, ch[1] * S.maxOffset.y, ch[2] * S.maxOffset.z);
    this.shakeRotation.set(ch[3] * S.maxRotationDeg.pitch * DEG, ch[4] * S.maxRotationDeg.yaw * DEG, ch[5] * S.maxRotationDeg.roll * DEG);
    this.shakeAmount = total * norm;
  }

  /** Distance attenuation of an event at `pos` relative to the camera. */
  _falloff(pos) {
    const cam = game.camera;
    if (!pos || !cam) return 1;
    const S = JUICE.shake;
    return distanceFalloff(cam.position.distanceTo(pos), S.falloffNear, S.falloffFar, S.falloffFloor);
  }

  _isLocal(p) { return !!(p && p.isLocal); }

  /** Normal for a hit without one: from the impact point back toward where the ball came from. */
  _fallbackNormal(ball, point) {
    if (ball && ball.velocity && ball.velocity.lengthSq() > 1e-4) return _v.copy(ball.velocity).negate().normalize();
    if (ball && ball.position && point) return _v.subVectors(ball.position, point).normalize();
    return _v.copy(_UP);
  }

  /** Catch contact normal = opposite of the incoming direction (thrower -> catch point), else catcher chest -> point. */
  _catchNormal(req) {
    const b = req.ball;
    if (b && b.velocity && b.velocity.lengthSq() > 1) return _v.copy(b.velocity).negate().normalize();
    if (req.thrower && req.point && req.thrower.position) {
      _v.subVectors(req.thrower.position, req.point); _v.y = 0;
      if (_v.lengthSq() > 1e-6) return _v.normalize();
    }
    if (req.catcher && req.point) {
      const chest = req.catcher.chestPosition || req.catcher.position;
      if (chest) { _v.subVectors(req.point, chest); if (_v.lengthSq() > 1e-6) return _v.normalize(); }
    }
    return _v.set(0, 0, 1);
  }

  // ------------------------------------------------------------------ event handlers

  _onBallHit(e) {
    if (!e || e.outcome === 'ignored') return;
    let speedKmh = e.speedKmh;
    if (!(speedKmh > 0) && e.velocity) speedKmh = e.velocity.length() * MS_TO_KMH;
    if (!(speedKmh > 0) && e.ball) speedKmh = e.ball.speedKmh || 0;
    this.playHit({
      point: e.point, normal: e.normal, speedKmh, ball: e.ball, victim: e.victim, attacker: e.attacker,
      eliminated: e.outcome === 'eliminated',
      negated: e.outcome === 'negated',
      local: !!e.local || this._isLocal(e.victim) || this._isLocal(e.attacker),
    });
  }

  _onCaught(e) {
    if (!e || e.quality === 'miss') return;
    this.playCatch({
      point: e.point, ball: e.ball, catcher: e.catcher, thrower: e.thrower, speedKmh: e.speedKmh || (e.ball && e.ball.speedKmh) || 0,
      perfect: e.quality === 'perfect',
      local: !!e.local || this._isLocal(e.catcher) || this._isLocal(e.thrower),
    });
  }

  _onBounce(e) {
    if (!e || !e.ball || !e.ball.visual || !e.normal) return;
    const J = JUICE.squash;
    if (!(e.impactSpeed >= J.minBounceSpeed)) return;
    const I = this.intensityForSpeed(e.impactSpeed * MS_TO_KMH);
    this.squash(e.ball.visual, e.normal, lerp(J.min, J.max, I) * J.bounceScale, J.duration);
  }

  _onBlocked(e) {
    if (!e) return;
    const S = JUICE.shake;
    const I = this.intensityForSpeed((e.ball && e.ball.speedKmh) || 0);
    this.shake(S.blockedAmplitude * (0.5 + 0.5 * I), S.hitFrequency, S.blockedDuration, e.point || null);
    if (e.ball && e.ball.visual) {
      const n = e.normal || this._fallbackNormal(e.ball, e.point);
      this.squash(e.ball.visual, n, lerp(JUICE.squash.min, JUICE.squash.max, I), JUICE.squash.duration);
    }
  }

  _onThrown(e) {
    if (!e || e.isPass || !this._isLocal(e.thrower)) return;
    const I = this.intensityForSpeed(e.speedKmh || 0);
    // Only the upper half of the curve kicks: a soft toss should not move the camera at all.
    const k = clamp01((I - 0.5) * 2);
    if (k <= 0) return;
    this.addTrauma(JUICE.shake.localThrowTrauma * k);
    game.cameraRig?.addFovKick?.(JUICE.fov.localThrowKick * k, JUICE.fov.kickDuration);
  }

  _onAbilityCast(e) {
    if (!e || e.slot !== SLOT.ULTIMATE) return;
    const local = this._isLocal(e.player);
    this.pulse('ultimate', JUICE.pulse.ultimate * (local ? 1 : JUICE.pulse.remoteScale), JUICE.pulse.ultimateDuration);
    if (local) {
      this.addTrauma(JUICE.shake.ultimateTrauma);
      game.cameraRig?.addFovKick?.(JUICE.fov.ultimateKick, JUICE.fov.kickDuration * 1.6);
    }
  }

  /**
   * PlayerEliminated / PlayerDamaged are emitted from inside Health.receiveHit, i.e. BEFORE the BallHitPlayer of the
   * same hit. They are queued and resolved in update(), when we know whether a ball hit already juiced that player.
   */
  _defer(type, player, attacker, point, damage) {
    if (!player) return;
    if (this._pendingCount >= this._pending.length) this._pending.push(new PendingEvent());
    const r = this._pending[this._pendingCount++];
    r.type = type; r.player = player; r.attacker = attacker || null; r.damage = damage || 0;
    r.hasPoint = !!point;
    if (point) r.point.copy(point);
  }

  _resolvePending() {
    if (this._pendingCount === 0) return;
    const now = game.time.realNow;
    const J = JUICE;
    for (let i = 0; i < this._pendingCount; i++) {
      const r = this._pending[i];
      const p = r.player;
      const rec = this._recentHits.get(p);
      const merged = !!rec && now - rec.at <= J.mergeWindow;
      const local = this._isLocal(p) || this._isLocal(r.attacker);
      const pos = r.hasPoint ? r.point : (p.chestPosition || p.position);

      if (r.type === 'eliminated') {
        if (merged && rec.eliminated) {
          // Fully juiced by the ball hit (heavy pulse, x1.25 hitstop, extra trauma) - nothing to add.
        } else if (merged) {
          // The hit was juiced as a normal hit but eliminated after all (e.g. HP interceptors): top up to "heavy".
          this.pulse('heavyHit', J.pulse.elimination * (local ? 1 : J.pulse.remoteScale), J.pulse.eliminationDuration);
          this.addTrauma(J.shake.eliminationExtraTrauma * (local ? 1 : this._falloff(pos)));
          rec.eliminated = true;
        } else {
          // Standalone elimination (Chrono's Delayed Impact resolving, tackles, shockwaves...): its own full beat.
          this.hitstop(J.hitstop.standaloneElimination);
          this.shake(J.shake.eliminationAmplitude * (local ? J.shake.localMul : 1), J.shake.hitFrequency,
            J.shake.eliminationDuration, local ? null : pos);
          this.flash(p, WHITE, J.flash.duration);
          this.pulse('heavyHit', J.pulse.elimination * (local ? 1 : J.pulse.remoteScale), J.pulse.eliminationDuration);
          if (p.isLocal) game.cameraRig?.addFovKick?.(J.fov.localHitKick, J.fov.kickDuration);
        }
      } else if (r.type === 'damaged' && !merged && r.damage > 0) {
        // Ability damage without a ball (Meteor shockwave, Tackle...): a lighter version of the hit pipeline.
        const k = clamp01(r.damage / 100);
        this.hitstop(lerp(J.hitstop.abilityDamageMin, J.hitstop.abilityDamageMax, k));
        this.shake(J.shake.abilityDamageAmplitude * (0.5 + 0.5 * k) * (local ? J.shake.localMul : 1), J.shake.hitFrequency,
          J.shake.hitDuration, local ? null : pos);
        this.flash(p, WHITE, J.flash.duration);
        this.pulse('hit', J.pulse.hit * (0.4 + 0.6 * k) * (local ? 1 : J.pulse.remoteScale), J.pulse.hitDuration);
        // Mark so an elimination from the same ability in this batch is merged instead of doubled.
        let hit = rec;
        if (!hit) { hit = { at: 0, eliminated: false }; this._recentHits.set(p, hit); }
        hit.at = now; hit.eliminated = false;
      }
      r.player = null; r.attacker = null;
    }
    this._pendingCount = 0;
  }
}
