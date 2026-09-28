// ---------------------------------------------------------------------------------------------------------------
// CameraRig - third-person / broadcast camera (contract: Tools/web/WEB_ARCHITECTURE.md §3.5). System, lateUpdate.
//
// Modes (chosen every frame, blended with a smootherstep transition whenever they change):
//   follow     over-the-shoulder camera on `target` (the local player): shoulder pivot 1.6 m up, 0.45 m right,
//              4.3 m back; yaw/pitch from addLook (pitch clamped -35..60 deg); sphere-cast collision against
//              game.arena.colliders with instant pull-in / eased pull-out; dynamic FOV (sprint +7, charging -6 and
//              closer), FOV kicks; follows the ragdoll hips while the target is eliminated.
//   cinematic  slow orbit around a point (round intros, match end) - setCinematic(point|object|null, opts).
//   spectate   no local player (?spectate=1, menus): a "TV director" cycling broadcast shots (sideline rail, high
//              tactical, slow orbit, baseline) with dolly-smooth moves, tracking the ball in play and zooming (FOV) to
//              frame the action.
//
// Outputs used by Input/HUD/combat: planarForward, planarRight, aimRay (from the screen centre, unshaken), aimPoint
// (first hit on floor / players / colliders, or 60 m), aimPlayer. The Juice shake (game.juice.shakeOffset /
// shakeRotation, camera-space) is applied last, AFTER the aim was computed, so shake never disturbs aiming.
// Everything here runs on REAL time (realDt): the camera keeps moving smoothly during hitstop and pause.
// Conventions: yaw 0 looks toward +Z, yaw grows toward +X (same as Player.yaw); pitch > 0 looks DOWN.
// ---------------------------------------------------------------------------------------------------------------
import * as THREE from 'three';
import { game } from '../game.js';
import { EV } from '../core/events.js';
import { COURT } from '../core/constants.js';
import { clamp, clamp01, lerp, dampFactor, smootherstep01, kickEnvelope } from '../juice/juiceMath.js';

const DEG = Math.PI / 180;

/** Camera tuning (metres, degrees, 1/s sharpness for exponential smoothing on real time). */
export const CAMERA = Object.freeze({
  near: 0.05,
  far: 400,
  baseFov: 60,                    // vertical FOV (deg) at 16:9
  minHorizontalFov: 74,           // narrow/portrait screens widen the vertical FOV to keep this much horizontal view...
  maxFov: 82,                     // ...up to this vertical FOV

  follow: Object.freeze({
    pivotHeight: 1.6,             // shoulder height above the feet
    shoulderRight: 0.45,          // over-the-shoulder lateral offset (right)
    distance: 4.3,                // boom length behind the shoulder
    pitchMin: -35,                // looking up
    pitchMax: 60,                 // looking down
    defaultPitch: 10,
    pivotSharpness: 14,           // horizontal follow (lag sells acceleration & slides)
    pivotSharpnessCharging: 22,   // tighter while aiming
    verticalSharpness: 7,         // jumps / landings are smoothed more than planar motion
    lookSharpness: 32,            // yaw/pitch smoothing of raw look input (~30 ms)
    autoYawSharpness: 2.2,        // following a bot (not human): drift behind its facing
    minCameraHeight: 0.3,         // never below the floor: shorten the boom when looking up
    teleportSnap: 4,              // m: a jump bigger than this snaps the pivot (round reset, swaps, outfield moves)
    ragdollLift: 0.7,             // pivot above the ragdoll hips
    ragdollExtraDistance: 1.2,    // pull back to show the tumble
  }),

  collision: Object.freeze({
    radius: 0.25,                 // camera sphere radius for the cast
    marchStep: 0.12,              // ray-march step (m)
    refine: 5,                    // binary refinement iterations after the first blocked sample
    minDistance: 0.7,             // never closer than this to the shoulder
    pullOutSharpness: 4,          // eased return after an obstruction clears (pull-in is instant)
  }),

  fov: Object.freeze({
    sprintBonus: 7,               // deg wider while sprinting (speed sensation)
    chargeZoom: -6,               // deg narrower at full charge (aim zoom)
    sharpness: 6,
    maxKicks: 6,
  }),

  charge: Object.freeze({
    distance: 3.3,                // boom while charging a throw (closer)
    shoulderRight: 0.62,
    blendSharpness: 8,
  }),

  aim: Object.freeze({ maxDistance: 60, playerRadius: 0.35, playerHeight: 1.8 }),

  cinematic: Object.freeze({ radius: 7.5, height: 2.6, lookHeight: 1.1, angularSpeed: 0.18, fov: 42, sharpness: 2.5 }),

  spectate: Object.freeze({
    side: -1,                     // broadcast "180-degree rule": the main camera stays on one sideline
    sidelineOffset: 9,            // m outside the touchline
    sidelineHeight: 6.2,
    highOffset: 5,
    highHeight: 11,
    orbitRadius: 16,
    orbitHeight: 7.5,
    orbitSpeed: 0.07,             // rad/s
    baselineBack: 4,              // m behind the outfield strip
    baselineHeight: 5.5,
    positionSharpness: 1.3,       // dolly smoothness
    focusSharpness: 1.4,          // pan smoothness without a live ball
    focusSharpnessLive: 3.6,      // pan while tracking a live ball
    framing: 13,                  // metres of action to keep in frame
    framingLive: 10,
    fovMin: 26,
    fovMax: 58,
    shotTransition: 2.6,          // seconds of blended move between shots
  }),

  transition: Object.freeze({ mode: 0.9, target: 0.6, cinematicIn: 1.4 }),
});

/** Shot list of the spectate director: [shot, seconds]. */
const SHOTS = Object.freeze([['sideline', 13], ['orbit', 8], ['sideline', 11], ['high', 7], ['sideline', 12], ['baseline', 7]]);

// ------------------------------------------------------------------ module temporaries (no per-frame allocations)
const _UP = new THREE.Vector3(0, 1, 0);
const _v = new THREE.Vector3();
const _v2 = new THREE.Vector3();
const _anchor = new THREE.Vector3();
const _dir = new THREE.Vector3();
const _right = new THREE.Vector3();
const _shoulder = new THREE.Vector3();
const _desired = new THREE.Vector3();
const _hit = new THREE.Vector3();
const _q = new THREE.Quaternion();
const _euler = new THREE.Euler(0, 0, 0, 'YXZ');
const _m = new THREE.Matrix4();

/** True while `p` is mid-elimination (ragdoll) - read defensively from the FSM / avatar. */
function isRagdolled(p) {
  const a = p.avatar;
  return !!((p.fsm && p.fsm.incapReason === 'eliminated')
    || (a && (a.ragdollActive || a.isRagdoll || (a.ragdoll && a.ragdoll.active))));
}

/** Wrap an angle to (-PI, PI]. */
const wrapAngle = (a) => {
  a = (a + Math.PI) % (Math.PI * 2);
  if (a < 0) a += Math.PI * 2;
  return a - Math.PI;
};

export class CameraRig {
  constructor() {
    /** @type {THREE.PerspectiveCamera|null} */
    this.camera = null;
    /** Followed player (local player by default). */
    this.target = null;
    /** 'follow' | 'cinematic' | 'spectate' (null until the first frame: the very first pose is never blended). */
    this.mode = null;
    /** Commanded look angles (radians). pitch > 0 looks down. */
    this.yaw = 0;
    this.pitch = CAMERA.follow.defaultPitch * DEG;
    /** Look sensitivity multiplier applied inside addLook (settings menu). */
    this.sensitivity = 1;
    /** Aim outputs (screen centre, unshaken). */
    this.aimRay = new THREE.Ray(new THREE.Vector3(), new THREE.Vector3(0, 0, 1));
    this.aimPoint = new THREE.Vector3(0, 1, 10);
    this.aimDistance = CAMERA.aim.maxDistance;
    /** Player under the crosshair (or null). */
    this.aimPlayer = null;
    /** Scale of the juice shake applied to this camera (0 disables). */
    this.shakeScale = 1;

    // smoothed follow state
    this._yawS = 0;
    this._pitchS = this.pitch;
    this._pivot = new THREE.Vector3(0, 1.6, 0);
    this._dist = CAMERA.follow.distance;
    this._chargeBlend = 0;
    this._sprintBlend = 0;
    this._fovBase = CAMERA.baseFov;
    this._snapPending = true;     // next follow update jumps its smoothed state straight to the target
    this._cut = false;            // explicit snap requested: skip/cancel the blended transition
    this._recenterPending = false;

    // last unshaken output pose (transition source)
    this._outPos = new THREE.Vector3(0, 6, -16);
    this._outQuat = new THREE.Quaternion();
    this._outFov = CAMERA.baseFov;
    // desired pose of the active mode
    this._desPos = new THREE.Vector3();
    this._desQuat = new THREE.Quaternion();
    this._desFov = CAMERA.baseFov;
    // mode transition
    this._trans = { t: 1, dur: 0, pos: new THREE.Vector3(), quat: new THREE.Quaternion(), fov: CAMERA.baseFov };

    this._kicks = Array.from({ length: CAMERA.fov.maxKicks }, () => ({ deg: 0, dur: 0, t: 0, active: false }));

    // cinematic
    this._cinematic = false;
    this._cinCenter = new THREE.Vector3();
    this._cinFollow = null;      // object with .position (Player / Object3D) to orbit dynamically
    this._cinAngle = 0;
    this._cinOpts = { radius: CAMERA.cinematic.radius, height: CAMERA.cinematic.height, lookHeight: CAMERA.cinematic.lookHeight,
      speed: CAMERA.cinematic.angularSpeed, fov: CAMERA.cinematic.fov };

    // spectate director
    this._shotIndex = 0;
    this._shotTime = 0;
    this._baselineSign = 1;
    this._orbitAngle = -Math.PI / 2;
    this._specPos = new THREE.Vector3(-13, 6.2, 0);
    this._focus = new THREE.Vector3(0, 1, 0);
    this._focusTarget = new THREE.Vector3(0, 1, 0);
    this._specInit = false;
    this._specFovInit = false;

    // collision broad-phase scratch (indices into game.arena.colliders)
    this._candidates = [];
    this._planarYaw = 0;
    this._planarF = new THREE.Vector3(0, 0, 1);
    this._planarR = new THREE.Vector3(-1, 0, 0);
    this._lastFov = -1;
    this._unsubs = [];
  }

  init() {
    this._grabCamera();
    const on = (name, fn) => this._unsubs.push(game.events.on(name, fn));
    on(EV.PlayerSpawned, (e) => {
      if (e && e.player && e.player.isLocal && !game.config.spectate) this.setTarget(e.player, true);
    });
    // Recenters are deferred to lateUpdate so they see the positions/yaws the Match wrote this frame.
    on(EV.MatchPhase, (e) => {
      // Fresh round: players are reset to their spawns - put the camera straight behind the hero.
      if (e && (e.current === 'preRound' || e.current === 'countdown')) this._recenterPending = true;
    });
    on(EV.PlayerZone, (e) => {
      // Sent to / back from the outfield: the hero now faces the other way - recenter behind them.
      if (e && e.player && e.player === this.target) this._recenterPending = true;
    });
  }

  // =================================================================== public API

  /**
   * Planar (XZ) unit forward of the view - the camera-relative movement basis. Returns an internal vector that is
   * rewritten on every access (callers may mutate the returned value; copy it if you keep it across frames).
   */
  get planarForward() { return this._planarF.set(Math.sin(this._planarYaw), 0, Math.cos(this._planarYaw)); }
  /** Planar (XZ) unit right of the view (forward x up). Same ownership rules as planarForward. */
  get planarRight() { return this._planarR.set(-Math.cos(this._planarYaw), 0, Math.sin(this._planarYaw)); }

  /**
   * Look input in degrees (mouse / stick / touch deltas already scaled by the input module).
   * @param {number} dxDeg > 0 turns right
   * @param {number} dyDeg > 0 looks down (mouse moved down)
   */
  addLook(dxDeg, dyDeg) {
    if (!Number.isFinite(dxDeg) || !Number.isFinite(dyDeg)) return;
    const F = CAMERA.follow;
    this.yaw = wrapAngle(this.yaw - dxDeg * this.sensitivity * DEG);
    this.pitch = clamp(this.pitch + dyDeg * this.sensitivity * DEG, F.pitchMin * DEG, F.pitchMax * DEG);
  }

  /**
   * Follow `player` (null = no target -> spectate). `snap` jumps instantly behind the player facing their yaw;
   * otherwise the camera glides over.
   */
  setTarget(player, snap = false) {
    const changed = player !== this.target;
    this.target = player || null;
    if (!this.target) return;
    if (snap) { this._snapFollow(); return; }
    if (changed) {
      this.yaw = wrapAngle(this.target.yaw || 0);
      this._snapPending = true;          // follow state jumps to the new target...
      this._beginTransition(CAMERA.transition.target); // ...while the rendered camera glides there
    }
  }

  /** Snap the follow camera behind the target's current facing. */
  recenter() {
    if (this.target) this._snapFollow();
  }

  /**
   * Cinematic slow orbit. `point` is a Vector3 (copied), or any object with a `.position` (orbits it as it moves),
   * or null to leave cinematic mode. opts: { radius, height, lookHeight, speed (rad/s), fov }.
   */
  setCinematic(point, opts = null) {
    if (!point) {
      if (this._cinematic) { this._cinematic = false; this._cinFollow = null; }
      return;
    }
    const C = CAMERA.cinematic;
    const o = this._cinOpts;
    o.radius = (opts && opts.radius) || C.radius;
    o.height = (opts && opts.height != null) ? opts.height : C.height;
    o.lookHeight = (opts && opts.lookHeight != null) ? opts.lookHeight : C.lookHeight;
    o.speed = (opts && opts.speed != null) ? opts.speed : C.angularSpeed;
    o.fov = (opts && opts.fov) || C.fov;
    if (point.isVector3) { this._cinFollow = null; this._cinCenter.copy(point); }
    else if (point.position && point.position.isVector3) { this._cinFollow = point; this._cinCenter.copy(point.position); }
    else return;
    // Start the orbit where the camera currently is, so entering cinematic is a smooth swing, not a cut.
    const cam = this._outPos;
    this._cinAngle = Math.atan2(cam.x - this._cinCenter.x, cam.z - this._cinCenter.z);
    this._cinematic = true;
  }

  /** Temporary FOV offset (deg, + = wider) with a quick attack and eased return over `duration` real seconds. */
  addFovKick(deg, duration = 0.35) {
    if (!Number.isFinite(deg) || deg === 0 || !(duration > 0)) return;
    let slot = null;
    for (const k of this._kicks) if (!k.active) { slot = k; break; }
    if (!slot) { // replace the one closest to finishing
      slot = this._kicks[0];
      for (const k of this._kicks) if (k.t / k.dur > slot.t / slot.dur) slot = k;
    }
    slot.active = true; slot.deg = clamp(deg, -20, 20); slot.dur = duration; slot.t = 0;
  }

  // =================================================================== frame

  lateUpdate(dt, realDt) {
    if (!this._grabCamera()) return;
    const cam = this.camera;

    // --- target housekeeping: drop disposed players, auto-acquire the local player.
    if (this.target && !game.players.includes(this.target)) this.target = null;
    if (!this.target && !game.config.spectate) {
      const local = game.localPlayer;
      if (local) this.setTarget(local, true);
    }

    if (this._recenterPending) { this._recenterPending = false; this.recenter(); }

    // --- pick the mode; blend when it changes. An explicit snap (setTarget(p, true) / recenter) cuts only while
    // already following; coming out of a cinematic or spectate shot it still glides in (e.g. intro orbit -> shoulder
    // during the countdown), with the follow state itself snapped behind the hero.
    const mode = this._cinematic ? 'cinematic' : this.target ? 'follow' : 'spectate';
    const cut = this._cut && mode === 'follow' && this.mode === 'follow';
    if (mode !== this.mode) {
      // Entering follow after another mode: its smoothed state is stale - jump it; the transition hides the jump.
      if (mode === 'follow') this._snapPending = true;
      if (!cut && this.mode !== null) this._beginTransition(mode === 'cinematic' ? CAMERA.transition.cinematicIn : CAMERA.transition.mode);
      this.mode = mode;
    }
    if (cut) this._trans.t = this._trans.dur;
    this._cut = false;

    if (mode === 'follow') this._updateFollow(realDt);
    else if (mode === 'cinematic') this._updateCinematic(realDt);
    else this._updateSpectate(realDt);

    // --- transition blend (smootherstep between the frozen previous pose and the live desired pose).
    const tr = this._trans;
    let pos = this._desPos, quat = this._desQuat, fov = this._desFov;
    if (tr.t < tr.dur) {
      tr.t += realDt;
      const k = smootherstep01(tr.t / tr.dur);
      _v.lerpVectors(tr.pos, this._desPos, k);
      _q.slerpQuaternions(tr.quat, this._desQuat, k);
      pos = _v; quat = _q; fov = lerp(tr.fov, this._desFov, k);
    }
    this._outPos.copy(pos);
    this._outQuat.copy(quat);
    this._outFov = fov;

    // --- FOV: mode FOV x aspect widening + kicks.
    let kick = 0;
    for (const k of this._kicks) {
      if (!k.active) continue;
      k.t += realDt;
      if (k.t >= k.dur) { k.active = false; continue; }
      kick += k.deg * kickEnvelope(k.t, k.dur);
    }
    const finalFov = clamp(fov * this._aspectBoost(cam.aspect) + kick, 15, 100);

    // --- write the unshaken pose and compute aim from it.
    cam.position.copy(this._outPos);
    cam.quaternion.copy(this._outQuat);
    if (Math.abs(finalFov - this._lastFov) > 1e-3 || cam.near !== CAMERA.near || cam.far !== CAMERA.far) {
      cam.fov = finalFov; cam.near = CAMERA.near; cam.far = CAMERA.far;
      cam.updateProjectionMatrix();
      this._lastFov = finalFov;
    }
    this._updatePlanar();
    _dir.set(0, 0, -1).applyQuaternion(this._outQuat);
    this._updateAim(this._outPos, _dir);

    // --- juice shake last (camera-space offset + rotation), scaled per mode.
    const juice = game.juice;
    const scale = this.shakeScale * (mode === 'follow' ? 1 : mode === 'cinematic' ? 0.5 : 0.8);
    if (juice && juice.shakeOffset && scale > 0) {
      _v2.copy(juice.shakeOffset).multiplyScalar(scale).applyQuaternion(this._outQuat);
      cam.position.add(_v2);
      const r = juice.shakeRotation;
      _euler.set(r.x * scale, r.y * scale, r.z * scale, 'YXZ');
      cam.quaternion.multiply(_q.setFromEuler(_euler));
    }
    cam.updateMatrixWorld();
  }

  dispose() {
    for (const u of this._unsubs) u();
    this._unsubs.length = 0;
    this.target = null;
    this._cinematic = false;
    this._cinFollow = null;
  }

  // =================================================================== follow

  _snapFollow() {
    const p = this.target;
    if (!p) return;
    this.yaw = this._yawS = wrapAngle(p.yaw || 0);
    this.pitch = this._pitchS = CAMERA.follow.defaultPitch * DEG;
    this._snapPending = true;
    this._cut = true; // cut, no blend (ignored while a cinematic owns the camera)
  }

  /** Where the follow camera orbits: shoulder height above the feet, or above the ragdoll hips. */
  _followAnchor(p, out) {
    const hips = this._ragdollHips(p);
    if (hips) {
      hips.getWorldPosition(out);
      out.y += CAMERA.follow.ragdollLift;
      return true;
    }
    out.copy(p.position);
    out.y += CAMERA.follow.pivotHeight;
    return false;
  }

  /** Hips bone while the target is ragdolled (mid-elimination, before the Match moves it to the outfield), else null. */
  _ragdollHips(p) {
    const a = p.avatar;
    if (!a || !a.bones || !a.bones.hips) return null;
    return isRagdolled(p) ? a.bones.hips : null;
  }

  _updateFollow(realDt) {
    const F = CAMERA.follow, CH = CAMERA.charge, FV = CAMERA.fov;
    const p = this.target;

    // 1) State-driven blends: charging (aim zoom, closer boom) and sprinting (wider FOV).
    const combat = p.combat;
    const charging = !!(combat && combat.isCharging);
    const chargeAmt = charging ? clamp01(Number.isFinite(combat.charge) ? 0.35 + 0.65 * combat.charge : 1) : 0;
    this._chargeBlend += (chargeAmt - this._chargeBlend) * dampFactor(CH.blendSharpness, realDt);
    const sprinting = !!(p.fsm && (typeof p.fsm.is === 'function' ? p.fsm.is('sprinting') : p.fsm.current === 'sprinting'));
    this._sprintBlend += ((sprinting ? 1 : 0) - this._sprintBlend) * dampFactor(4, realDt);

    // 2) Look angles: bots are followed "lazily" behind their facing; humans are driven by addLook.
    if (!p.isHuman && !this._snapPending) {
      this.yaw = wrapAngle(this.yaw + wrapAngle((p.yaw || 0) - this.yaw) * dampFactor(F.autoYawSharpness, realDt));
    }
    const ragdolled = this._followAnchor(p, _anchor);
    if (this._snapPending) {
      this._yawS = this.yaw; this._pitchS = this.pitch;
      this._pivot.copy(_anchor);
      this._dist = F.distance;
    } else {
      const kl = dampFactor(F.lookSharpness, realDt);
      this._yawS = wrapAngle(this._yawS + wrapAngle(this.yaw - this._yawS) * kl);
      this._pitchS += (this.pitch - this._pitchS) * kl;
      // 3) Pivot follow: teleports snap, otherwise planar and vertical smoothing (vertical softer: jumps feel floaty).
      if (this._pivot.distanceToSquared(_anchor) > F.teleportSnap * F.teleportSnap) this._pivot.copy(_anchor);
      else {
        const kp = dampFactor(lerp(F.pivotSharpness, F.pivotSharpnessCharging, this._chargeBlend), realDt);
        const ky = dampFactor(F.verticalSharpness, realDt);
        this._pivot.x += (_anchor.x - this._pivot.x) * kp;
        this._pivot.z += (_anchor.z - this._pivot.z) * kp;
        this._pivot.y += (_anchor.y - this._pivot.y) * ky;
      }
    }

    // 4) View basis from the smoothed angles. dir = where the camera looks; right = dir x up on the plane.
    const yaw = this._yawS, pitch = this._pitchS;
    const cp = Math.cos(pitch);
    _dir.set(Math.sin(yaw) * cp, -Math.sin(pitch), Math.cos(yaw) * cp);
    _right.set(-Math.cos(yaw), 0, Math.sin(yaw));

    // 5) Boom: shoulder point to the right of the pivot, camera behind it along -dir.
    let distance = lerp(F.distance, CH.distance, this._chargeBlend) + (ragdolled ? F.ragdollExtraDistance : 0);
    const shoulderRight = lerp(F.shoulderRight, CH.shoulderRight, this._chargeBlend);
    // Looking up would put the camera under the floor: shorten the boom instead.
    const floorY = (game.court ? game.court.floorY : 0) + F.minCameraHeight;
    if (_dir.y > 1e-3) {
      const maxD = (this._pivot.y - floorY) / _dir.y;
      if (maxD < distance) distance = Math.max(CAMERA.collision.minDistance, maxD);
    }
    _shoulder.copy(this._pivot).addScaledVector(_right, shoulderRight);
    // The shoulder offset itself can be blocked (hugging a wall on the right).
    const fs = this._sphereCast(this._pivot, _shoulder);
    if (fs < 1) _shoulder.lerpVectors(this._pivot, _shoulder, fs);
    _desired.copy(_shoulder).addScaledVector(_dir, -distance);

    // 6) Collision: instant pull-in, eased pull-out.
    const free = this._sphereCast(_shoulder, _desired);
    const allowed = Math.max(CAMERA.collision.minDistance, free * distance);
    if (this._snapPending || allowed < this._dist) this._dist = allowed;
    else this._dist += (allowed - this._dist) * dampFactor(CAMERA.collision.pullOutSharpness, realDt);
    this._desPos.copy(_shoulder).addScaledVector(_dir, -this._dist);

    // 7) Orientation looks along dir (camera -Z = dir): Euler YXZ (x = -pitch, y = yaw + PI).
    _euler.set(-pitch, yaw + Math.PI, 0, 'YXZ');
    this._desQuat.setFromEuler(_euler);

    // 8) Dynamic FOV.
    const targetFov = CAMERA.baseFov + FV.sprintBonus * this._sprintBlend + FV.chargeZoom * this._chargeBlend;
    if (this._snapPending) this._fovBase = targetFov;
    else this._fovBase += (targetFov - this._fovBase) * dampFactor(FV.sharpness, realDt);
    this._desFov = this._fovBase;
    this._snapPending = false;
  }

  // =================================================================== cinematic

  _updateCinematic(realDt) {
    const o = this._cinOpts;
    if (this._cinFollow && this._cinFollow.position) this._cinCenter.copy(this._cinFollow.position);
    this._cinAngle += o.speed * realDt;
    _desired.set(
      this._cinCenter.x + Math.sin(this._cinAngle) * o.radius,
      this._cinCenter.y + o.height,
      this._cinCenter.z + Math.cos(this._cinAngle) * o.radius,
    );
    // Keep the orbit inside the arena volume.
    _v2.copy(this._cinCenter); _v2.y += o.lookHeight;
    const f = this._sphereCast(_v2, _desired);
    if (f < 1) _desired.lerpVectors(_v2, _desired, Math.max(0.15, f));
    this._desPos.copy(_desired);
    this._lookAt(this._desPos, _v2, this._desQuat);
    this._desFov = o.fov;
  }

  // =================================================================== spectate (broadcast director)

  _updateSpectate(realDt) {
    const S = CAMERA.spectate;
    const court = game.court;
    const halfW = court ? court.halfW : COURT.width / 2;
    const halfL = court ? court.halfL : COURT.length / 2;
    const outD = court ? court.outfieldDepth : COURT.outfieldDepth;

    // 1) What is the story right now? A live ball > the holder of the ball > a player winding up > the loose ball >
    //    the centroid of the infield players.
    const urgency = this._findFocus(this._focusTarget);
    // Never swing the broadcast toward the bleachers / run-off: a ball out there (ball boy) is framed from the court.
    const oW = court ? court.outerHalfW ?? halfW + 2.5 : halfW + 2.5, oL = halfL + outD;
    this._focusTarget.x = clamp(this._focusTarget.x, -oW, oW);
    this._focusTarget.z = clamp(this._focusTarget.z, -oL, oL);
    if (!this._specInit) this._focus.copy(this._focusTarget);
    const kf = dampFactor(urgency >= 1 ? S.focusSharpnessLive : S.focusSharpness, realDt);
    this._focus.lerp(this._focusTarget, kf);

    // 2) Director: advance through the shot list; each change is a blended dolly move, never a cut.
    this._shotTime += realDt;
    if (this._shotTime >= SHOTS[this._shotIndex][1]) {
      this._shotIndex = (this._shotIndex + 1) % SHOTS.length;
      this._shotTime = 0;
      if (SHOTS[this._shotIndex][0] === 'baseline') this._baselineSign = -this._baselineSign;
      if (SHOTS[this._shotIndex][0] === 'orbit') this._orbitAngle = Math.atan2(this._specPos.x, this._specPos.z);
      this._beginTransition(S.shotTransition);
    }
    const shot = SHOTS[this._shotIndex][0];
    const f = this._focus;
    switch (shot) {
      case 'high':
        _desired.set(S.side * (halfW + S.highOffset), S.highHeight, clamp(f.z * 0.35, -halfL * 0.5, halfL * 0.5));
        break;
      case 'orbit':
        this._orbitAngle += S.orbitSpeed * realDt;
        _desired.set(Math.sin(this._orbitAngle) * S.orbitRadius, S.orbitHeight, Math.cos(this._orbitAngle) * S.orbitRadius);
        break;
      case 'baseline':
        _desired.set(clamp(f.x * 0.25, -2, 2), S.baselineHeight, this._baselineSign * (halfL + outD + S.baselineBack));
        break;
      default: // 'sideline' - the main broadcast rail camera, trucking along the touchline with the play
        _desired.set(S.side * (halfW + S.sidelineOffset), S.sidelineHeight, clamp(f.z * 0.6, -halfL * 0.7, halfL * 0.7));
    }
    // Stay in the open (cast from above the centre of the court toward the camera spot).
    _v2.set(0, Math.max(3, _desired.y), 0);
    const free = this._sphereCast(_v2, _desired);
    if (free < 1) _desired.lerpVectors(_v2, _desired, Math.max(0.2, free));

    if (!this._specInit) { this._specPos.copy(_desired); this._specInit = true; }
    else this._specPos.lerp(_desired, dampFactor(S.positionSharpness, realDt));
    this._desPos.copy(this._specPos);
    _v2.copy(f);
    this._lookAt(this._desPos, _v2, this._desQuat);

    // 3) Zoom to frame the action: FOV that fits `framing` metres at the focus distance.
    const d = Math.max(1, this._desPos.distanceTo(f));
    const framing = urgency >= 1 ? S.framingLive : S.framing;
    const fov = 2 * Math.atan(framing / 2 / d) / DEG;
    const targetFov = clamp(fov, S.fovMin, S.fovMax);
    this._desFov = this._desFov > 0 && this._specFovInit ? lerp(this._desFov, targetFov, dampFactor(1.5, realDt)) : targetFov;
    this._specFovInit = true;
  }

  /** Writes the spectate focus point into `out` and returns its urgency (1 live ball, 0.5 wind-up, 0.3 players, 0 idle). */
  _findFocus(out) {
    const balls = game.balls && (game.balls.active || game.balls.matchBalls);
    let best = null, bestS = -1;
    if (balls) {
      for (const b of balls) {
        if (!b || b.state !== 'live' || !b.position) continue;
        const s = b.velocity ? b.velocity.lengthSq() : 0;
        if (s > bestS) { bestS = s; best = b; }
      }
    }
    if (best) {
      out.copy(best.position);
      if (best.velocity) out.addScaledVector(best.velocity, 0.12); // lead the ball slightly, like a camera operator
      out.y = clamp(out.y, 0.6, 3);
      return 1;
    }
    // Single ball: follow it in the holder's hands (the attack builds from there), else where it lies loose.
    const mb = game.balls && game.balls.ball;
    if (mb && mb.state === 'held' && mb.holder && mb.holder.position) {
      out.copy(mb.holder.chestPosition || mb.holder.position); out.y = 1.2; return 0.6;
    }
    let n = 0;
    out.set(0, 0, 0);
    for (const p of game.players) {
      if (p.combat && p.combat.isCharging && p.zone === 'infield') { out.copy(p.position); out.y = 1.2; return 0.5; }
    }
    if (mb && (mb.state === 'free' || mb.state === 'stasis') && mb.position) {
      out.copy(mb.position); out.y = clamp(out.y, 0.6, 3); return 0.4;
    }
    for (const p of game.players) {
      if (p.zone !== 'infield' || (p.health && p.health.isEliminated) || !p.position) continue;
      out.add(p.position); n++;
    }
    if (n > 0) { out.divideScalar(n); out.y = 1.0; return 0.3; }
    out.set(0, 1, 0);
    return 0;
  }

  // =================================================================== helpers

  _grabCamera() {
    if (game.camera && this.camera !== game.camera) {
      this.camera = game.camera;
      this.camera.near = CAMERA.near;
      this.camera.far = CAMERA.far;
      this.camera.updateProjectionMatrix();
      this._outPos.copy(this.camera.position);
      this._outQuat.copy(this.camera.quaternion);
    }
    return !!this.camera;
  }

  /** Freeze the current output pose as the start of a smootherstep blend lasting `duration` real seconds. */
  _beginTransition(duration) {
    const tr = this._trans;
    tr.pos.copy(this._outPos);
    tr.quat.copy(this._outQuat);
    tr.fov = this._outFov;
    tr.t = 0;
    tr.dur = Math.max(0.01, duration);
  }

  /** Camera orientation looking from `eye` at `target` (world up). */
  _lookAt(eye, target, outQuat) {
    if (eye.distanceToSquared(target) < 1e-6) return outQuat;
    _m.lookAt(eye, target, _UP);
    return outQuat.setFromRotationMatrix(_m);
  }

  /** Widen narrow (portrait / mobile) screens so the horizontal FOV stays >= CAMERA.minHorizontalFov. */
  _aspectBoost(aspect) {
    if (!(aspect > 0)) return 1;
    const needed = 2 * Math.atan(Math.tan((CAMERA.minHorizontalFov * DEG) / 2) / aspect) / DEG;
    return clamp(needed / CAMERA.baseFov, 1, CAMERA.maxFov / CAMERA.baseFov);
  }

  /** Yaw of the view on the ground plane: the smoothed follow yaw, or the rendered camera's heading otherwise. */
  _updatePlanar() {
    if (this.mode === 'follow') { this._planarYaw = this._yawS; return; }
    _v2.set(0, 0, -1).applyQuaternion(this._outQuat);
    if (_v2.x * _v2.x + _v2.z * _v2.z > 1e-8) this._planarYaw = Math.atan2(_v2.x, _v2.z);
  }

  /**
   * Sphere cast approximated by a ray march: samples every `marchStep` metres from `from` to `to` and returns the free
   * fraction (0..1) of the segment for a sphere of CAMERA.collision.radius, refined by bisection at the first contact.
   * Colliders already overlapping `from` are ignored (never trap the camera inside a box it starts in).
   */
  _sphereCast(from, to) {
    const colliders = game.arena && game.arena.colliders;
    if (!colliders || colliders.length === 0) return 1;
    const C = CAMERA.collision;
    const r = C.radius;
    const len = from.distanceTo(to);
    if (len < 1e-4) return 1;
    // Broad phase: boxes whose r-expanded bounds overlap the segment's AABB and that don't contain the start.
    const cand = this._candidates;
    cand.length = 0;
    const minX = Math.min(from.x, to.x) - r, maxX = Math.max(from.x, to.x) + r;
    const minY = Math.min(from.y, to.y) - r, maxY = Math.max(from.y, to.y) + r;
    const minZ = Math.min(from.z, to.z) - r, maxZ = Math.max(from.z, to.z) + r;
    for (let i = 0; i < colliders.length; i++) {
      const b = colliders[i];
      if (!b || !b.min) continue;
      if (b.max.x < minX || b.min.x > maxX || b.max.y < minY || b.min.y > maxY || b.max.z < minZ || b.min.z > maxZ) continue;
      if (b.distanceToPoint(from) < r) continue;
      cand.push(i);
    }
    if (cand.length === 0) return 1;
    const steps = Math.max(1, Math.ceil(len / C.marchStep));
    let prev = 0;
    for (let s = 1; s <= steps; s++) {
      const t = s / steps;
      if (this._blocked(from, to, t, colliders, r)) {
        // Bisection between the last free sample and this blocked one.
        let lo = prev, hi = t;
        for (let k = 0; k < C.refine; k++) {
          const mid = (lo + hi) * 0.5;
          if (this._blocked(from, to, mid, colliders, r)) hi = mid; else lo = mid;
        }
        return lo;
      }
      prev = t;
    }
    return 1;
  }

  _blocked(from, to, t, colliders, r) {
    _hit.lerpVectors(from, to, t);
    const cand = this._candidates;
    for (let i = 0; i < cand.length; i++) if (colliders[cand[i]].distanceToPoint(_hit) < r) return true;
    return false;
  }

  /**
   * Screen-centre aim: nearest of court floor, other players (vertical capsules) and arena colliders beyond the
   * followed player, else CAMERA.aim.maxDistance along the ray.
   */
  _updateAim(origin, dir) {
    const A = CAMERA.aim;
    const ray = this.aimRay;
    ray.origin.copy(origin);
    ray.direction.copy(dir);
    let best = A.maxDistance;
    let bestPlayer = null;
    // Ignore everything between the camera and the hero (walls behind the camera, the hero's own body).
    let tMin = 0;
    if (this.mode === 'follow' && this.target) tMin = Math.max(0, _v2.subVectors(this._pivot, origin).dot(dir));

    // Court floor.
    const floorY = game.court ? game.court.floorY : 0;
    if (dir.y < -1e-5) {
      const t = (floorY - origin.y) / dir.y;
      if (t > tMin && t < best) best = t;
    }
    // Arena colliders.
    const colliders = game.arena && game.arena.colliders;
    if (colliders) {
      for (let i = 0; i < colliders.length; i++) {
        const b = colliders[i];
        if (!b || !b.min || !ray.intersectBox(b, _hit)) continue;
        const t = _hit.sub(origin).dot(dir);
        if (t > tMin && t < best) best = t;
      }
    }
    // Players (vertical capsules): enemies, teammates and clones' owners alike - not the hero, the fallen, or
    // cloaked enemies (the crosshair must not reveal them).
    const self = this.target;
    for (const p of game.players) {
      if (p === self || !p.position) continue;
      if (isRagdolled(p)) continue;
      if (self && game.areEnemies(self, p) && p.status && typeof p.status.has === 'function' && p.status.has('cloaked')) continue;
      const t = this._rayCapsule(origin, dir, p.position, p.height || A.playerHeight, p.radius || A.playerRadius);
      if (t > tMin && t < best) { best = t; bestPlayer = p; }
    }
    this.aimDistance = best;
    this.aimPlayer = bestPlayer;
    ray.at(best, this.aimPoint);
  }

  /** Ray (unit dir) vs vertical capsule standing on `base`. Returns the entry distance or Infinity. */
  _rayCapsule(o, d, base, height, radius) {
    const yA = base.y + radius, yB = base.y + Math.max(height - radius, radius);
    let best = Infinity;
    // Cylinder side (XZ quadratic).
    const ox = o.x - base.x, oz = o.z - base.z;
    const a = d.x * d.x + d.z * d.z;
    if (a > 1e-8) {
      const b = ox * d.x + oz * d.z;
      const c = ox * ox + oz * oz - radius * radius;
      const disc = b * b - a * c;
      if (disc >= 0) {
        const t = (-b - Math.sqrt(disc)) / a;
        if (t >= 0) {
          const y = o.y + d.y * t;
          if (y >= yA && y <= yB) best = t;
        }
      }
    }
    // Hemispherical caps.
    best = Math.min(best, this._raySphere(o, d, base.x, yA, base.z, radius), this._raySphere(o, d, base.x, yB, base.z, radius));
    return best;
  }

  _raySphere(o, d, cx, cy, cz, r) {
    const mx = o.x - cx, my = o.y - cy, mz = o.z - cz;
    const b = mx * d.x + my * d.y + mz * d.z;
    const c = mx * mx + my * my + mz * mz - r * r;
    if (c > 0 && b > 0) return Infinity;
    const disc = b * b - c;
    if (disc < 0) return Infinity;
    return Math.max(0, -b - Math.sqrt(disc));
  }
}
