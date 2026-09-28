// ---------------------------------------------------------------------------------------------------------------
// Bear - Magnetic Pull field (磁力牽引). A 5 m field centred on Bear's right palm:
//   * enemy LIVE balls inside it are steered with strong homing into his hand (registered ball field: runs inside the
//     ball's fixed step BEFORE its swept collision test, so a pulled ball never reaches his body first);
//   * within 0.5 m of the palm (predictive: the ball's next step passes within it) the ball is his: combat.giveBall +
//     EV.BallCaught (quality 'normal', rally +1; ability balls too, exactly like a real catch). Hands already full ->
//     the field kills its momentum and drops it at his feet;
//   * with empty hands the nearest FREE match ball in range is lifted into his hand as well;
//   * a hit filter cancels any enemy ball that still touches Bear while the field is up (it is captured instead).
// Visuals: the `magnetField` VFX attached to Bear, a warm tint on his avatar and pulsing energy tethers from the palm to
// every ball being pulled. Gameplay runs on the scaled clock; the tethers animate on real time.
// ---------------------------------------------------------------------------------------------------------------
import * as THREE from 'three';
import { game } from '../../game.js';
import { EV } from '../../core/events.js';
import { TEAM, MS_TO_KMH, GRAVITY } from '../../core/constants.js';
import { RallyMath } from '../../core/rules.js';
import { magneticHoming, segmentPointDistSq } from './screwsGadgetMath.js';
import {
  BALL, FieldStamp, allBalls, matchBalls, throwerTeam, setBallVelocity, registerBallField, unregisterBallField,
  rightHandOf, planarForward, fx, fxAttach, fxStop, sfx, emit, sceneRoot, orientBetween, sharedGeometry,
} from './screwsGadgetKit.js';

/** Tuning (roster `params` override radius / pull). */
export const MAGNET_DEFAULTS = Object.freeze({
  radius: 5,              // field radius around the palm (m) - spec 5 m
  pull: 60,               // homing strength; edge rate = pull*edgeRateK (1/s), core rate = pull*coreRateK (1/s)
  edgeRateK: 0.08,        // -> 4.8/s at the field edge: the ball visibly starts to curve
  coreRateK: 0.32,        // -> 19/s near the palm: the ball snaps onto the hand
  captureRadius: 0.5,     // spec: within 0.5 m of the palm the ball is in Bear's hands
  arrivalSpeed: 9,        // m/s near the palm (a catchable speed; momentum is kept far out)
  minSpeed: 5,            // m/s floor for live balls inside the field (never stalls mid-air)
  freePullSpeed: 8,       // m/s max for a free ball lifted into empty hands
  freeGain: 3.5,          // 1/s: free-ball approach speed = distance * gain (clamped)
  freeMinSpeed: 2.5,
  dropSpeed: 1.6,         // m/s: a live ball arriving while the hands are full drops at his feet
  pendingRange: 2.2,      // m: a ball whose hit on Bear was cancelled is captured if still this close
  maxTethers: 4,
  tetherWidth: 0.03,      // m
  tetherHold: 0.15,       // real s a tether lingers after the last pull
  hitFilterPriority: 300, // runs before damage filters
  color: 0xffc85a,        // warm gold energy (Bear's hero colour family)
  tint: 0.12,             // avatar tint amount while the field is up
});

const _to = new THREE.Vector3();
const _step = new THREE.Vector3();
const _out = new THREE.Vector3();
const _v = new THREE.Vector3();
const _f = new THREE.Vector3();
const _pt = new THREE.Vector3();

const TETHER_VERT = /* glsl */`
varying vec2 vUv;
void main() {
  vUv = uv;
  gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0);
}`;
const TETHER_FRAG = /* glsl */`
uniform vec3 uColor;
uniform float uTime;
uniform float uOpacity;
varying vec2 vUv;
void main() {
  float along = vUv.y;                                  // 0 at the palm, 1 at the ball
  float pulses = pow(fract(along * 5.0 + uTime * 4.0), 5.0); // energy packets travelling toward the palm
  float ends = smoothstep(0.0, 0.08, along) * (1.0 - smoothstep(0.85, 1.0, along));
  float flicker = 0.85 + 0.15 * sin(uTime * 53.0 + along * 20.0);
  float a = (0.22 + 0.78 * pulses) * ends * flicker * uOpacity;
  gl_FragColor = vec4(uColor * 2.2, a);
  #include <tonemapping_fragment>
  #include <colorspace_fragment>
}`;

/**
 * Registered ball field + visuals. Lifecycle: start(duration) -> update(dt) every frame (ability onTick) -> stop();
 * dispose() when the ability is unequipped.
 */
export class BearMagneticField {
  /** @param {import('../../gameplay/player.js').Player} owner @param {object} params */
  constructor(owner, params = {}) {
    this.owner = owner;
    this.p = { ...MAGNET_DEFAULTS, ...params };
    this.active = false;
    /** Balls captured during the current activation. */
    this.captured = 0;
    this._hand = new THREE.Vector3();
    this._stamp = new FieldStamp();
    /** ball -> realNow of its last pull (drives the tethers). */
    this._pulled = new Map();
    /** Balls whose hit on Bear was cancelled by the filter this frame (captured in update). */
    this._pending = new Set();
    this._freeTarget = null;
    this._vfx = null;
    this._tethers = null;
    this._tetherMat = null;
    this._filterOn = null;
    this._filter = { priority: this.p.hitFilterPriority, filter: (hit) => this._filterHit(hit) };
  }

  /** Current palm position (updated every frame while active). */
  get handPosition() { return this._hand; }

  start(duration) {
    if (this.active) return;
    const o = this.owner;
    this.active = true;
    this.captured = 0;
    rightHandOf(o, this._hand);
    registerBallField(this);
    const h = o.health;
    if (h && h.addHitFilter) { h.addHitFilter(this._filter); this._filterOn = h; }
    this._vfx = fxAttach('magnetField', o.root, { duration, color: this.p.color, radius: this.p.radius });
    if (!this._vfx) fx('magnetField', o.position, { scale: 1, color: this.p.color, radius: this.p.radius, duration });
    o.status?.apply?.('magnetized', duration, 1, this);
    o.avatar?.setTint?.(this.p.color, this.p.tint);
    sfx('magnetField', this._hand, 1, 1);
  }

  stop() {
    if (!this.active) return;
    const o = this.owner;
    this.active = false;
    unregisterBallField(this);
    if (this._filterOn) { this._filterOn.removeHitFilter?.(this._filter); this._filterOn = null; }
    fxStop(this._vfx);
    this._vfx = null;
    o.status?.remove?.('magnetized', this);
    o.avatar?.setTint?.(this.p.color, 0);
    this._pulled.clear();
    this._pending.clear();
    this._freeTarget = null;
    this._hideTethers();
  }

  dispose() {
    this.stop();
    if (this._tethers) {
      for (const m of this._tethers) m.parent?.remove(m);
      this._tethers = null;
    }
    this._tetherMat?.dispose();
    this._tetherMat = null;
  }

  // ------------------------------------------------------------------ ball field interface (BallManager)

  /** Called by BallManager for every live ball each fixed step (and possibly free balls). */
  apply(ball, dt) {
    if (!this.active || !ball) return;
    if (ball.state === BALL.LIVE) this._steerLive(ball, dt);
    else if (ball.state === BALL.FREE && ball === this._freeTarget) this._steerFree(ball);
  }

  // ------------------------------------------------------------------ per frame (ability onTick, scaled dt)

  update(dt) {
    if (!this.active) return;
    const o = this.owner;
    rightHandOf(o, this._hand);

    // 1) Enemy balls that touched Bear this frame (hit cancelled by the filter): into the hands.
    if (this._pending.size) {
      const r2 = this.p.pendingRange * this.p.pendingRange;
      for (const b of this._pending) {
        if ((b.state === BALL.LIVE || b.state === BALL.FREE) && b.position.distanceToSquared(this._hand) <= r2) this._capture(b);
      }
      this._pending.clear();
    }

    // 2) Live balls the BallManager did not route through the field (fallback; backwards: captures may recycle).
    const balls = allBalls();
    for (let i = balls.length - 1; i >= 0; i--) {
      const b = balls[i];
      if (b && b.state === BALL.LIVE && !this._stamp.fresh(b)) this._steerLive(b, Math.max(dt, 1e-4));
    }

    // 3) Empty hands: lift the nearest free match ball into them.
    this._pickFreeTarget();
    if (this._freeTarget && !this._stamp.fresh(this._freeTarget)) this._steerFree(this._freeTarget);

    this._updateTethers();
  }

  // ------------------------------------------------------------------ internals

  _isEnemyBall(ball) {
    const t = throwerTeam(ball);
    return t !== TEAM.NONE && t !== this.owner.team;
  }

  _steerLive(ball, dt) {
    if (ball.unblockable || !this._isEnemyBall(ball)) return;
    this._stamp.touch(ball);
    const R = this.p.radius;
    _to.subVectors(this._hand, ball.position);
    const d2 = _to.lengthSq();
    if (d2 > R * R) return;
    const vel = ball.velocity;
    const cr = this.p.captureRadius;
    _step.copy(vel).multiplyScalar(dt);
    // Predictive capture: in the palm now, or passing within the capture radius during this step.
    if (d2 <= cr * cr || segmentPointDistSq(ball.position, _step, this._hand) <= cr * cr) {
      this._capture(ball);
      return;
    }
    const k = this.p.pull;
    if (magneticHoming(vel, _to, R, k * this.p.edgeRateK, k * this.p.coreRateK, this.p.arrivalSpeed, this.p.minSpeed, dt, _out) >= 0) {
      vel.copy(_out);
      if ('lockedTarget' in ball) ball.lockedTarget = null; // the field overrides any aim-assist homing
      this._pulled.set(ball, game.time.realNow);
    }
  }

  _pickFreeTarget() {
    const c = this.owner.combat;
    if (!c || c.hasBall || this.owner.canAct === false) { this._freeTarget = null; return; }
    const cur = this._freeTarget;
    const R2 = this.p.radius * this.p.radius;
    if (cur && cur.state === BALL.FREE && cur.position.distanceToSquared(this._hand) <= R2) return;
    this._freeTarget = null;
    let best = null, bestD = R2;
    const balls = matchBalls();
    for (let i = 0; i < balls.length; i++) {
      const b = balls[i];
      if (!b || b.state !== BALL.FREE || b.isAbilityBall) continue;
      if (b.canBePickedUpBy && b.canBePickedUpBy(this.owner) === false) continue;
      const d = b.position.distanceToSquared(this._hand);
      if (d < bestD) { bestD = d; best = b; }
    }
    this._freeTarget = best;
  }

  _steerFree(ball) {
    this._stamp.touch(ball);
    const c = this.owner.combat;
    if (!c || c.hasBall) { this._freeTarget = null; return; }
    _to.subVectors(this._hand, ball.position);
    const d = _to.length();
    if (d <= this.p.captureRadius) { this._captureFree(ball); return; }
    const speed = Math.min(this.p.freePullSpeed, Math.max(this.p.freeMinSpeed, d * this.p.freeGain));
    _v.copy(_to).multiplyScalar(speed / d);
    _v.y += GRAVITY * game.fixedStep; // cancel one step of gravity so the lift reads as smooth levitation
    setBallVelocity(ball, _v);
    this._pulled.set(ball, game.time.realNow);
  }

  _captureFree(ball) {
    const c = this.owner.combat;
    this._freeTarget = null;
    this._pulled.delete(ball);
    if (!c || c.hasBall || !c.giveBall) return;
    c.giveBall(ball);
    _pt.copy(this._hand);
    fx('catch', _pt, { scale: 0.6, color: this.p.color });
    sfx('pickup', _pt, 0.9, 1);
  }

  /** A live enemy ball reached the palm. */
  _capture(ball) {
    const o = this.owner;
    const c = o.combat;
    this._pulled.delete(ball);
    this._pending.delete(ball);
    _pt.copy(this._hand);
    const thrower = ball.lastThrower || null;
    const speedKmh = ball.speedKmh || (ball.velocity ? ball.velocity.length() * MS_TO_KMH : 0);
    const handsFree = c && !c.hasBall && c.giveBall && o.canAct !== false;

    // An enemy PASS pulled in is an interception (single-ball rule): possession only - rally 0, and Match / HUD see
    // `intercepted` (no ult, no revive), exactly like a hand catch of a pass in Combat.tryResolveCatch.
    const intercepted = !!ball.isPass;
    if (handsFree) {
      // Same order as a real catch (Combat.tryResolveCatch): payload reacts, the hand takes it (ends the flight and
      // runs payload.onEnded), Rally Boost +1 (reset for an intercepted pass).
      const rally = ball.rallyCount | 0;
      if (ball.payload && typeof ball.payload.onCaught === 'function') {
        try { ball.payload.onCaught(ball, o, 'normal'); } catch (e) { console.warn('[bear] payload.onCaught threw', e); }
      }
      c.giveBall(ball);
      ball.setRallyCount?.(intercepted ? 0 : RallyMath.next(rally));
    } else {
      // Hands full: the field kills the momentum and drops the ball at his feet.
      planarForward(o, _f);
      _v.copy(_f).multiplyScalar(this.p.dropSpeed * 0.6);
      _v.y = this.p.dropSpeed;
      ball.makeFree?.(_v, true);
      emit(EV.BallBlocked, { ball, blocker: o, point: _pt.clone(), normal: _f.clone().negate() });
      fx('shieldImpact', _pt, { scale: 0.5, color: this.p.color, direction: _f });
      sfx('magnetClunk', _pt, 0.9, 1);
      return;
    }
    this.captured++;
    o.avatar?.playCatch?.();
    sfx('magnetClunk', _pt, 1, 1.1); // the catch VFX/SFX/juice themselves are driven by EV.BallCaught
    emit(EV.BallCaught, {
      ball, catcher: o, thrower, quality: 'normal', timingQuality: 'normal', secondsBeforeImpact: 0, point: _pt.clone(),
      speedKmh, rallyCount: ball.rallyCount | 0, local: !!o.isLocal, intercepted, isPass: intercepted,
    });
  }

  /** Health hit filter: enemy balls cannot hurt Bear while his field is up - the field takes them. */
  _filterHit(hit) {
    if (!this.active || !hit || hit.cancelled) return;
    const b = hit.ball;
    if (!b || (hit.victim && hit.victim !== this.owner) || b.unblockable || !this._isEnemyBall(b)) return;
    hit.cancelled = true;
    this._pending.add(b);
  }

  // ------------------------------------------------------------------ tethers (visual)

  _ensureTethers() {
    if (this._tethers) return;
    const scene = sceneRoot();
    if (!scene) return;
    const geo = sharedGeometry('bearTether', () => new THREE.CylinderGeometry(1, 1, 1, 6, 1, true).translate(0, 0.5, 0));
    this._tetherMat = new THREE.ShaderMaterial({
      uniforms: { uColor: { value: new THREE.Color(this.p.color) }, uTime: { value: 0 }, uOpacity: { value: 1 } },
      vertexShader: TETHER_VERT, fragmentShader: TETHER_FRAG,
      transparent: true, depthWrite: false, blending: THREE.AdditiveBlending, side: THREE.DoubleSide,
    });
    this._tethers = [];
    for (let i = 0; i < this.p.maxTethers; i++) {
      const m = new THREE.Mesh(geo, this._tetherMat);
      m.visible = false;
      m.frustumCulled = false;
      m.renderOrder = 5;
      scene.add(m);
      this._tethers.push(m);
    }
  }

  _updateTethers() {
    const now = game.time.realNow;
    let n = 0;
    if (this._pulled.size) {
      this._ensureTethers();
      for (const [b, t] of this._pulled) {
        const alive = b.state === BALL.LIVE || b.state === BALL.FREE;
        if (!alive || now - t > this.p.tetherHold) { this._pulled.delete(b); continue; }
        if (this._tethers && n < this._tethers.length) {
          const m = this._tethers[n++];
          orientBetween(m, this._hand, b.position, this.p.tetherWidth);
          m.visible = true;
        }
      }
    }
    if (this._tetherMat) this._tetherMat.uniforms.uTime.value = now;
    if (this._tethers) for (let i = n; i < this._tethers.length; i++) this._tethers[i].visible = false;
  }

  _hideTethers() { if (this._tethers) for (const m of this._tethers) m.visible = false; }
}
