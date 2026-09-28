// ---------------------------------------------------------------------------------------------------------------
// Screws - Auto-Turret unit (自動砲台). A tripod-mounted pneumatic ball launcher built from machined parts (powder-
// coated gunmetal housing, steel barrel and hopper, rubber feet, team enamel side plates, status LED).
//   * Deploy: legs splay out and the body squats onto them (0.55 s, scaled), then it goes live.
//   * Collect: free MATCH balls within 4 m are magnetically lifted into the hopper (up to 3 stored). A stored ball is
//     taken out of play with ball.despawn() (its visual is replaced by a dummy in the hopper) and comes back through
//     ball.resetTo() when fired or released.
//   * Fire: every 1.5 s (scaled) at the nearest targetable, non-cloaked enemy: yaw/pitch servo onto a lead point, then
//     a stored ball is launched through owner.combat.launchBall with params from owner.combat.buildThrowParams
//     (origin = muzzle, ~95 km/h, style 'turret'); recoil, muzzle VFX/SFX. Hits credit Screws.
//   * Body: registered in game.hittables - enemy live balls are blocked, Screws' team's balls pass.
//   * Shutdown: stored balls drop out as free balls, the unit folds and disposes itself.
// ---------------------------------------------------------------------------------------------------------------
import * as THREE from 'three';
import { game } from '../../game.js';
import { KMH_TO_MS, MAX_BALL_SPEED_MS, GRAVITY, TEAM } from '../../core/constants.js';
import { Ballistics } from '../../core/rules.js';
import { sweptPointVsAabb, leadTarget, approachAngle, wrapAngle, springStep } from './screwsGadgetMath.js';
import {
  BALL, matchBalls, throwerTeam, setBallVelocity, fx, sfx, trauma, sceneRoot, addWorldObject, removeWorldObject,
  disposeObject3D, gadgetMaterial, sharedGeometry, teamColor, chestOf, isAlive, hasStatus,
} from './screwsGadgetKit.js';

export const TURRET_DEFAULTS = Object.freeze({
  collectRadius: 4,       // m (spec)
  fireInterval: 1.5,      // s (spec, scaled)
  capacity: 3,            // stored balls
  shotSpeedKmh: 95,       // km/h (spec ~95)
  range: 30,              // m target acquisition
  deployTime: 0.55,       // s (scaled)
  foldTime: 0.45,         // real s
  firstShotDelay: 0.4,    // s after going live
  yawRate: 6,             // rad/s servo
  pitchRate: 4,           // rad/s servo
  aimTolerance: 0.1,      // rad total error to fire
  maxAimWait: 0.45,       // s: fire anyway after this long waiting for the servo
  magnetSpeed: 6.5,       // m/s max for balls lifted into the hopper
  magnetGain: 3.2,        // 1/s
  magnetMinSpeed: 2.2,    // m/s
  loadRadius: 0.3,        // m from the hopper mouth -> stored
  maxMagnetTime: 2.4,     // s: a stuck ball is snapped in after this
  scanInterval: 0.12,     // s
  retargetInterval: 0.2,  // s
  storeSafety: 15,        // s: despawn safety timer (ball respawns by itself if everything else failed)
  recoil: 0.085,          // m barrel kick
  idleSweep: 0.55,        // rad amplitude of the idle scan
});

// Geometry (metres)
const LEG_LEN = 0.74;
const SPLAY_FOLDED = 0.08, SPLAY_DEPLOYED = 0.56; // rad from vertical; deployed feet sit on the floor
const BODY_HALF = new THREE.Vector3(0.3, 0.42, 0.3);
const BODY_CENTER_Y = 0.93;
const BARREL_Z = 0.25;

const _m = new THREE.Vector3();
const _v = new THREE.Vector3();
const _to = new THREE.Vector3();
const _chest = new THREE.Vector3();
const _aim = new THREE.Vector3();
const _pivot = new THREE.Vector3();
const _min = new THREE.Vector3();
const _max = new THREE.Vector3();
const _zero = new THREE.Vector3();
const _n = { x: 0, y: 0, z: 0 };

const LED = Object.freeze({ ready: 0x3dff6a, empty: 0xffa820, fire: 0xff3322, boot: 0x40a0ff });

export class ScrewsAutoTurret {
  /**
   * @param {import('../../gameplay/player.js').Player} owner Screws
   * @param {THREE.Vector3} position floor position (already clamped into Screws' zone)
   * @param {number} yaw initial facing
   * @param {object} params
   */
  constructor(owner, position, yaw, params = {}) {
    this.owner = owner;
    this.team = owner.team;           // hittable contract
    this.p = { ...TURRET_DEFAULTS, ...params };
    this.floorY = game.court ? game.court.floorY : 0;
    this.position = new THREE.Vector3(position.x, this.floorY, position.z);
    this.state = 'idle';              // idle | deploying | active | folding | disposed
    this.stored = [];                 // despawned match balls in the hopper
    this.incoming = new Map();        // ball -> seconds being pulled
    this.shots = 0;
    this.blocks = 0;
    this.hopperMouth = new THREE.Vector3();
    this._k = 0;                      // deploy amount 0..1
    this._yaw = yaw; this._pitch = 0.05; this._baseYaw = yaw;
    this._target = null;
    this._aimError = Math.PI;
    this._aimWait = 0;
    this._fireTimer = 0;
    this._scanT = 0;
    this._retargetT = 0;
    this._ledFlash = 0;
    this._recoil = { x: 0, v: 0 };
    this._wobble = { x: 0, v: 0 };
    this._result = { t: 0, point: new THREE.Vector3(), normal: new THREE.Vector3() };
    this._payload = {
      // The launched MATCH ball goes back to the standard look once its flight ends.
      onEnded: (b) => { if (b && !b.isAbilityBall && b.style === 'turret' && b.setStyle) b.setStyle('standard'); },
    };
  }

  get isOperational() { return this.state === 'active' || (this.state === 'deploying' && this._k > 0.5); }

  deploy() {
    if (this.state !== 'idle') return;
    this._build();
    this._pose(0);
    game.hittables?.add?.(this);
    addWorldObject(this);
    this.state = 'deploying';
    fx('floorDust', this.position, { scale: 0.8 });
    sfx('turretDeploy', this.position, 1, 1);
  }

  /** Ends operation: drops stored balls, stops blocking, folds up and disposes itself. */
  shutdown() {
    if (this.state === 'idle' || this.state === 'folding' || this.state === 'disposed') return;
    this._releaseAll();
    game.hittables?.delete?.(this);
    this.state = 'folding';
    this._target = null;
    sfx('turretFold', this.position, 0.9, 1);
  }

  /** Immediate removal (round reset / unequip). Stored balls are released unless `releaseBalls` is false. */
  dispose(releaseBalls = true) {
    if (this.state === 'disposed') return;
    if (releaseBalls) this._releaseAll(); else { this.stored.length = 0; this.incoming.clear(); }
    game.hittables?.delete?.(this);
    removeWorldObject(this);
    disposeObject3D(this.root);
    this.root = null;
    this.state = 'disposed';
  }

  // ------------------------------------------------------------------ hittable contract

  intersect(from, to, radius) {
    if (!this.isOperational) return null;
    const c = this.position;
    _min.set(c.x - BODY_HALF.x - radius, this.floorY + BODY_CENTER_Y - BODY_HALF.y - radius, c.z - BODY_HALF.z - radius);
    _max.set(c.x + BODY_HALF.x + radius, this.floorY + BODY_CENTER_Y + BODY_HALF.y + radius, c.z + BODY_HALF.z + radius);
    // Quick reject on the segment's bounding box.
    if (Math.max(from.x, to.x) < _min.x || Math.min(from.x, to.x) > _max.x || Math.max(from.z, to.z) < _min.z ||
        Math.min(from.z, to.z) > _max.z || Math.max(from.y, to.y) < _min.y || Math.min(from.y, to.y) > _max.y) return null;
    const t = sweptPointVsAabb(from, to, _min, _max, _n);
    if (t < 0) return null;
    const r = this._result;
    r.t = t;
    r.point.lerpVectors(from, to, t);
    r.normal.set(_n.x, _n.y, _n.z);
    return r;
  }

  onBallHit(ball, hit) {
    const team = throwerTeam(ball);
    if (team === this.team || team === TEAM.NONE || ball.unblockable) return 'pass';
    const point = (hit && hit.point) || ball.position;
    this._wobble.v += 6;
    fx('shieldImpact', point, { direction: hit && hit.normal, scale: 0.6, color: 0xffd9a0 });
    sfx('metalClang', point, 1, 0.9 + Math.random() * 0.2);
    this.blocks++;
    return 'block';
  }

  // ------------------------------------------------------------------ system

  update(dt, realDt) {
    if (this.state === 'disposed' || this.state === 'idle') return;
    if (this.state === 'deploying') {
      this._k = Math.min(1, this._k + dt / this.p.deployTime);
      if (this._k >= 1) {
        this.state = 'active';
        this._fireTimer = this.p.firstShotDelay;
        sfx('turretReady', this.position, 0.7, 1);
      }
    } else if (this.state === 'active') {
      this._tickActive(dt);
    } else if (this.state === 'folding') {
      this._k = Math.max(0, this._k - realDt / this.p.foldTime);
      if (this._k <= 0) { this.dispose(false); return; }
    }
    this._pose(realDt);
  }

  _tickActive(dt) {
    const p = this.p;
    this._scanT -= dt;
    if (this._scanT <= 0) { this._scanT = p.scanInterval; this._scan(); }
    this._steerIncoming(dt);
    this._retargetT -= dt;
    if (this._retargetT <= 0 || !this._validTarget(this._target)) { this._retargetT = p.retargetInterval; this._target = this._findTarget(); }
    this._aimAt(dt);
    this._fireTimer -= dt;
    if (this._fireTimer <= 0 && this.stored.length && this._target) {
      if (this._aimError <= p.aimTolerance || this._aimWait >= p.maxAimWait) {
        if (this._shoot()) this._fireTimer = p.fireInterval;
        this._aimWait = 0;
      } else {
        this._aimWait += dt;
      }
    }
  }

  // ------------------------------------------------------------------ collection

  _scan() {
    const free = this.p.capacity - this.stored.length - this.incoming.size;
    if (free <= 0) return;
    const r2 = this.p.collectRadius * this.p.collectRadius;
    const balls = matchBalls();
    let added = 0;
    for (let i = 0; i < balls.length && added < free; i++) {
      const b = balls[i];
      if (!b || b.state !== BALL.FREE || b.isAbilityBall || this.incoming.has(b)) continue;
      const dx = b.position.x - this.position.x, dz = b.position.z - this.position.z;
      if (dx * dx + dz * dz > r2 || b.position.y > this.floorY + 2.5) continue;
      this.incoming.set(b, 0);
      added++;
      sfx('magnetHum', b.position, 0.5, 1.3);
    }
  }

  _steerIncoming(dt) {
    if (!this.incoming.size) return;
    const p = this.p;
    for (const [b, t] of this.incoming) {
      if (b.state !== BALL.FREE) { this.incoming.delete(b); continue; }
      const tt = t + dt;
      _to.subVectors(this.hopperMouth, b.position);
      const d = _to.length();
      if (d <= p.loadRadius || tt >= p.maxMagnetTime) { this._store(b); continue; }
      this.incoming.set(b, tt);
      const speed = Math.min(p.magnetSpeed, Math.max(p.magnetMinSpeed, d * p.magnetGain));
      _v.copy(_to).multiplyScalar(speed / Math.max(d, 1e-4));
      _v.y += GRAVITY * game.fixedStep; // counter one step of gravity: a smooth magnetic lift
      setBallVelocity(b, _v);
    }
  }

  _store(ball) {
    this.incoming.delete(ball);
    if (this.stored.length >= this.p.capacity || !ball.despawn) return;
    // Safety respawn point on the floor beside the unit (only used if the turret never releases it).
    _m.set(this.position.x + 0.6 * Math.sin(this._baseYaw), this.floorY + 0.12, this.position.z + 0.6 * Math.cos(this._baseYaw));
    ball.despawn(this.p.storeSafety, _m.clone());
    if (ball.state !== BALL.DESPAWNED) return; // refused
    this.stored.push(ball);
    fx('catch', this.hopperMouth, { scale: 0.45, color: teamColor(this.team).getHex() });
    sfx('turretLoad', this.hopperMouth, 0.9, 1);
  }

  _releaseAll() {
    for (let i = 0; i < this.stored.length; i++) {
      const b = this.stored[i];
      if (!b || b.state !== BALL.DESPAWNED) continue;
      const a = this._yaw + Math.PI + (i - 1) * 0.7;
      _m.set(this.hopperMouth.x + Math.sin(a) * 0.25, Math.max(this.floorY + 0.3, this.hopperMouth.y), this.hopperMouth.z + Math.cos(a) * 0.25);
      _v.set(Math.sin(a) * 1.3, 1.2, Math.cos(a) * 1.3);
      this._putBack(b, _m, _v);
    }
    this.stored.length = 0;
    this.incoming.clear();
  }

  /** Brings a despawned ball back into play as a free ball at `pos` with velocity `vel`. */
  _putBack(ball, pos, vel) {
    if (ball.resetTo) ball.resetTo(pos);
    else if (ball.teleport) ball.teleport(pos, vel);
    if (ball.makeFree) ball.makeFree(vel, true);
    else setBallVelocity(ball, vel);
  }

  // ------------------------------------------------------------------ targeting & firing

  _validTarget(p) {
    return !!p && isAlive(p) && p.isTargetable !== false && game.areEnemies(this.owner, p) && !hasStatus(p, 'cloaked') &&
      p.position.distanceToSquared(this.position) <= this.p.range * this.p.range;
  }

  _findTarget() {
    let best = null, bestD = this.p.range * this.p.range;
    const players = game.players;
    for (let i = 0; i < players.length; i++) {
      const p = players[i];
      if (!this._validTarget(p)) continue;
      const dx = p.position.x - this.position.x, dz = p.position.z - this.position.z;
      const d = dx * dx + dz * dz;
      if (d < bestD) { bestD = d; best = p; }
    }
    return best;
  }

  _shotSpeed() { return this.p.shotSpeedKmh * KMH_TO_MS; }

  _gravity() {
    const prof = this.owner.combat && this.owner.combat.profile;
    return GRAVITY * (prof && prof.thrownGravityScale > 0 ? prof.thrownGravityScale : 0.65);
  }

  /** Servo the head (yaw) and barrel (pitch) onto the target's lead point, or scan slowly when idle. */
  _aimAt(dt) {
    let yaw, pitch;
    const t = this._target;
    _pivot.set(this.position.x, this.floorY + BODY_CENTER_Y, this.position.z);
    if (t) {
      chestOf(t, _chest);
      leadTarget(_pivot, _chest, t.velocity || _zero, this._shotSpeed(), _aim);
      const dx = _aim.x - _pivot.x, dz = _aim.z - _pivot.z;
      yaw = Math.atan2(dx, dz);
      const sol = Ballistics.solveAngle(this._shotSpeed(), Math.hypot(dx, dz), _aim.y - _pivot.y, this._gravity(), true);
      pitch = Math.min(0.6, Math.max(-0.3, sol.angle));
    } else {
      yaw = this._baseYaw + Math.sin(game.time.now * 0.6) * this.p.idleSweep;
      pitch = 0.05;
    }
    this._yaw = approachAngle(this._yaw, yaw, this.p.yawRate, dt);
    this._pitch += Math.max(-this.p.pitchRate * dt, Math.min(this.p.pitchRate * dt, pitch - this._pitch));
    this._aimError = t ? Math.abs(wrapAngle(yaw - this._yaw)) + Math.abs(pitch - this._pitch) : Math.PI;
  }

  _shoot() {
    const combat = this.owner.combat;
    const target = this._target;
    if (!combat || !combat.buildThrowParams || !combat.launchBall || !target) return false;
    let ball = null;
    while (this.stored.length && !ball) {
      const b = this.stored.shift();
      if (b && b.state === BALL.DESPAWNED) ball = b;
    }
    if (!ball) return false;

    this.root.updateMatrixWorld(true);
    this.muzzle.getWorldPosition(_m);
    const speed = this._shotSpeed();
    chestOf(target, _chest);
    leadTarget(_m, _chest, target.velocity || _zero, speed, _aim);

    let params = null;
    try {
      params = combat.buildThrowParams(combat.profile?.fullChargeTime ?? 0.75, target, true);
    } catch (e) {
      console.warn('[screws] turret buildThrowParams failed', e);
    }
    if (!params) { this._putBack(ball, _m, _zero); return false; }
    params.thrower = this.owner;
    params.target = target;
    params.origin = _m.clone();
    params.aimPoint = _aim.clone();
    params.aimDir = _aim.clone().sub(_m).normalize();
    // Tolerate a km/h convention for baseSpeed (anything above the 220 km/h cap in m/s cannot be m/s).
    params.baseSpeed = params.baseSpeed > MAX_BALL_SPEED_MS + 1 ? this.p.shotSpeedKmh : speed;
    params.speedMul = 1; params.radiusMul = 1; params.charge = 1; params.rallyCount = 0;
    params.isAbility = true; params.isPass = false; params.isCounter = false; params.reveals = false;
    params.unblockable = false; params.pierce = false;
    params.style = 'turret';
    params.payload = this._payload;

    if (ball.resetTo) ball.resetTo(_m); else ball.teleport?.(_m, _zero);
    try {
      combat.launchBall(ball, params);
    } catch (e) {
      console.warn('[screws] turret launchBall failed', e);
    }
    if (ball.state !== BALL.LIVE) { this._putBack(ball, _m, params.aimDir); return false; }

    this.shots++;
    this._recoil.x = -this.p.recoil;
    this._recoil.v = 0;
    this._ledFlash = 0.14;
    fx('turretMuzzle', _m, { direction: params.aimDir, scale: 1, color: 0xfff1d0 });
    sfx('turretMuzzle', _m, 1, 0.95 + Math.random() * 0.1);
    if (this.owner.isLocal) trauma(0.08);
    return true;
  }

  // ------------------------------------------------------------------ pose / visuals

  _pose(realDt) {
    if (!this.root) return;
    const k = this._k;
    const e = k * k * (3 - 2 * k);
    const splay = SPLAY_FOLDED + (SPLAY_DEPLOYED - SPLAY_FOLDED) * e;
    const hubY = LEG_LEN * Math.cos(splay);
    for (const tilt of this.legTilts) tilt.rotation.x = splay;
    this.hub.position.y = hubY;
    this.head.rotation.y = this._yaw;
    this.head.scale.setScalar(0.7 + 0.3 * e);
    if (realDt > 0) {
      springStep(this._recoil, 0, 26, realDt);
      springStep(this._wobble, 0, 18, realDt);
    }
    this.pitchGroup.rotation.x = -this._pitch;
    this.barrel.position.z = BARREL_Z * (0.55 + 0.45 * e) + this._recoil.x;
    this.head.rotation.z = this._wobble.x * 0.05;
    for (let i = 0; i < this.dummies.length; i++) this.dummies[i].visible = i < this.stored.length;

    // Status LED: boot (blue) -> ready (green) / empty (amber), red flash on fire.
    this._ledFlash = Math.max(0, this._ledFlash - realDt);
    const blink = 0.5 + 0.5 * Math.sin(game.time.realNow * 10);
    let hex = LED.boot, inten = 2 + 2 * blink;
    if (this._ledFlash > 0) { hex = LED.fire; inten = 6; }
    else if (this.state === 'active') { hex = this.stored.length ? LED.ready : LED.empty; inten = this.stored.length ? 3 : 1.5 + 2 * blink; }
    else if (this.state === 'folding') { hex = LED.empty; inten = 1; }
    this.ledMat.emissive.setHex(hex);
    this.ledMat.emissiveIntensity = inten;

    this.root.updateMatrixWorld(true);
    this.mouthMarker.getWorldPosition(this.hopperMouth);
  }

  _build() {
    const root = this.root = new THREE.Group();
    root.name = 'ScrewsAutoTurret';
    root.position.copy(this.position);
    const gun = gadgetMaterial('gunmetal'), steel = gadgetMaterial('steel'), rubber = gadgetMaterial('rubber');
    const paint = gadgetMaterial('paint:' + this.team), glass = gadgetMaterial('glass'), ballMat = gadgetMaterial('ball');
    const steelDouble = steel.clone();
    steelDouble.side = THREE.DoubleSide;
    steelDouble.userData = {}; // per-turret (disposed with it)
    this.ledMat = new THREE.MeshStandardMaterial({ color: 0x111111, emissive: new THREE.Color(LED.boot), emissiveIntensity: 2, roughness: 0.3 });
    const mesh = (geo, mat, cast = true) => { const m = new THREE.Mesh(geo, mat); m.castShadow = cast; m.receiveShadow = true; return m; };

    // Hub + tripod legs (each leg: yaw pivot -> tilt group -> leg tube + rubber foot).
    const hub = this.hub = new THREE.Group();
    root.add(hub);
    hub.add(mesh(sharedGeometry('turretHub', () => new THREE.CylinderGeometry(0.075, 0.09, 0.1, 20)), gun));
    const legGeo = sharedGeometry('turretLeg', () => new THREE.CylinderGeometry(0.02, 0.026, LEG_LEN, 10).translate(0, -LEG_LEN / 2, 0));
    const footGeo = sharedGeometry('turretFoot', () => new THREE.CylinderGeometry(0.035, 0.04, 0.03, 12));
    const hingeGeo = sharedGeometry('turretHinge', () => new THREE.BoxGeometry(0.05, 0.05, 0.05));
    this.legTilts = [];
    for (let i = 0; i < 3; i++) {
      const yawPivot = new THREE.Group();
      yawPivot.rotation.y = (i / 3) * Math.PI * 2 + Math.PI / 3;
      const tilt = new THREE.Group();
      tilt.add(mesh(legGeo, steel));
      tilt.add(mesh(hingeGeo, gun));
      const foot = mesh(footGeo, rubber);
      foot.position.y = -LEG_LEN;
      tilt.add(foot);
      yawPivot.add(tilt);
      hub.add(yawPivot);
      this.legTilts.push(tilt);
    }
    const bearing = mesh(sharedGeometry('turretBearing', () => new THREE.CylinderGeometry(0.11, 0.11, 0.045, 24)), steel);
    bearing.position.y = 0.072;
    hub.add(bearing);

    // Head (yaw): housing, enamel side plates, sensor, LED, barrel (pitch) and hopper.
    const head = this.head = new THREE.Group();
    head.position.y = 0.1;
    hub.add(head);
    const housing = mesh(sharedGeometry('turretHousing', () => new THREE.BoxGeometry(0.32, 0.24, 0.46)), gun);
    housing.position.set(0, 0.14, -0.03);
    head.add(housing);
    const plateGeo = sharedGeometry('turretPlate', () => new THREE.BoxGeometry(0.018, 0.18, 0.34));
    for (const sx of [-1, 1]) {
      const plate = mesh(plateGeo, paint);
      plate.position.set(sx * 0.169, 0.14, -0.03);
      head.add(plate);
    }
    const lens = mesh(sharedGeometry('turretLens', () => new THREE.CylinderGeometry(0.03, 0.03, 0.035, 16).rotateX(Math.PI / 2)), glass, false);
    lens.position.set(-0.1, 0.22, 0.2);
    head.add(lens);
    const led = new THREE.Mesh(sharedGeometry('turretLed', () => new THREE.SphereGeometry(0.016, 12, 8)), this.ledMat);
    led.position.set(0.11, 0.265, 0.19);
    head.add(led);

    const pitchGroup = this.pitchGroup = new THREE.Group();
    pitchGroup.position.set(0, 0.16, 0.06);
    head.add(pitchGroup);
    const barrel = this.barrel = new THREE.Group();
    pitchGroup.add(barrel);
    barrel.add(mesh(sharedGeometry('turretBarrel', () => new THREE.CylinderGeometry(0.135, 0.145, 0.5, 28, 1, true).rotateX(Math.PI / 2)), gun));
    // Dark bore seen from the muzzle (inside faces of an open tube).
    const boreMat = rubber.clone();
    boreMat.side = THREE.DoubleSide;
    boreMat.userData = {}; // per-turret clone (disposed with it)
    barrel.add(new THREE.Mesh(sharedGeometry('turretBore', () => new THREE.CylinderGeometry(0.118, 0.118, 0.49, 20, 1, true).rotateX(Math.PI / 2)), boreMat));
    const collar = mesh(sharedGeometry('turretCollar', () => new THREE.CylinderGeometry(0.155, 0.155, 0.06, 28).rotateX(Math.PI / 2)), steel);
    collar.position.z = 0.25;
    barrel.add(collar);
    const ringGeo = sharedGeometry('turretRing', () => new THREE.CylinderGeometry(0.15, 0.15, 0.02, 28).rotateX(Math.PI / 2));
    for (const z of [-0.12, 0.06]) {
      const ring = mesh(ringGeo, steel);
      ring.position.z = z;
      barrel.add(ring);
    }
    this.muzzle = new THREE.Object3D();
    this.muzzle.position.z = 0.31;
    barrel.add(this.muzzle);

    const hopper = new THREE.Group();
    hopper.position.set(0, 0.26, -0.15);
    head.add(hopper);
    const neck = mesh(sharedGeometry('turretNeck', () => new THREE.CylinderGeometry(0.125, 0.125, 0.12, 20, 1, true)), steelDouble);
    neck.position.y = 0.06;
    hopper.add(neck);
    const funnel = mesh(sharedGeometry('turretFunnel', () => new THREE.CylinderGeometry(0.23, 0.13, 0.24, 28, 1, true)), steelDouble);
    funnel.position.y = 0.24;
    hopper.add(funnel);
    const rim = mesh(sharedGeometry('turretRim', () => new THREE.TorusGeometry(0.23, 0.012, 8, 36).rotateX(Math.PI / 2)), steel);
    rim.position.y = 0.36;
    hopper.add(rim);
    this.mouthMarker = new THREE.Object3D();
    this.mouthMarker.position.y = 0.42;
    hopper.add(this.mouthMarker);
    const ballGeo = sharedGeometry('turretBall', () => new THREE.SphereGeometry(0.105, 24, 16));
    this.dummies = [];
    for (const [x, y, z] of [[0, 0.2, 0], [0.09, 0.3, 0.05], [-0.09, 0.31, -0.04]]) {
      const d = mesh(ballGeo, ballMat);
      d.position.set(x, y, z);
      d.visible = false;
      hopper.add(d);
      this.dummies.push(d);
    }
    sceneRoot()?.add(root);
  }
}
