// ---------------------------------------------------------------------------------------------------------------
// Physics (cannon-es world, game system) + Ragdoll (impulse-driven elimination ragdoll for a realistic avatar).
//
//  * Physics owns ONE cannon-es World: gravity -9.81, fixed step (the kernel's 60 Hz scaled clock, so hitstop and
//    pause freeze ragdolls too), 10 solver iterations, a static floor plane at the court floor and static boxes that
//    mirror `game.arena.colliders` (walls, bleachers). It only steps while at least one ragdoll is active.
//  * Ragdoll is built from the avatar's Biped skeleton: 12 bodies (pelvis, abdomen, chest, head, upper/lower arms,
//    thighs, calves) approximated by boxes and sphere chains ("capsules"), joined by ConeTwistConstraints with
//    anatomical limits (flexion biased cones: knees bend back, elbows forward, hips flex forward...).
//    Body frames: Y = bone segment direction, X = the character's lateral axis (projected), Z = X x Y. With these
//    frames cannon's twist tangents coincide at the bind pose, so the twist limit measures real twist.
//    Activation takes the CURRENT animated pose and the per-part velocities tracked from the animation (momentum
//    preservation), adds the hit impulse at the hit point, and every rendered frame the (interpolated) body
//    transforms are converted back into bone-local rotations so the skinned mesh stays glued to the bodies.
// ---------------------------------------------------------------------------------------------------------------
import * as THREE from 'three';
import * as CANNON from 'cannon-es';
import { game } from '../game.js';
import { ragdollDeltaV, clamp } from './avatarMath.js';

/** Physics world tuning. */
export const PHYSICS = Object.freeze({
  gravity: -9.81,
  solverIterations: 10,
  solverTolerance: 1e-4,
  floorFriction: 0.55,
  floorRestitution: 0.05,
  wallFriction: 0.35,
  wallRestitution: 0.15,
});

/** Collision groups (bit masks). Ragdoll parts only collide with static geometry (no self-collision explosions). */
export const COLLISION = Object.freeze({ STATIC: 1, RAGDOLL: 2 });

/** Ragdoll tuning (masses in kg for a 1.0-scale ~75 kg adult; scaled by model scale^3). */
export const RAGDOLL = Object.freeze({
  linearDamping: 0.08,
  angularDamping: 0.35,      // limp-muscle joint friction approximation
  sleepSpeedLimit: 0.12,
  sleepTimeLimit: 0.8,
  maxLinearSpeed: 18,
  maxAngularSpeed: 22,
  maxInheritedSpeed: 12,     // clamp on velocities inherited from the animation
  impulseGain: 6,            // |J| / mass * gain -> whole-body delta-v: Health sends ~20-90 N*s -> ~1.6-6.5 m/s
  minDeltaV: 1.1,
  maxDeltaV: 6.5,
  strikeShare: 0.45,         // extra share of the impulse given to the struck body part (spin)
  upward: 0.28,              // fraction of delta-v added upward so bodies lift off instead of skidding
  limitRelaxRate: 1.5,       // rad/s: temporarily widened joint limits shrink back to anatomical ones
  limitMargin: 0.06,
});

/**
 * Body part table. `bone`/`end` are Avatar.bones keys. Shapes are in the body frame (X lateral, Y along the segment,
 * Z anterior), sizes in metres at model scale 1. cone/twist = anatomical half-angles (rad); bias rotates the cone
 * centre toward the anterior (+) or posterior (-) direction (flexion side), measured on the bind pose.
 */
const PARTS = [
  { name: 'pelvis', bone: 'hips', end: 'spine', parent: null, mass: 11.0, box: [0.15, 0.1, 0.11], centerT: 0 },
  { name: 'spine', bone: 'spine', end: 'chest', parent: 'pelvis', mass: 10.0, box: [0.14, 0, 0.1], cone: 0.3, twist: 0.3, bias: 0.1 },
  { name: 'chest', bone: 'chest', end: 'neck', parent: 'spine', mass: 15.0, box: [0.17, 0.03, 0.11], cone: 0.3, twist: 0.3, bias: 0.05 },
  { name: 'head', bone: 'head', endLocal: [0.21, 0, 0], parent: 'chest', mass: 5.5, sphere: 0.105, cone: 0.6, twist: 0.7, bias: 0.15 },
  { name: 'lUpperArm', bone: 'lUpperArm', end: 'lForearm', parent: 'chest', mass: 2.2, capsule: 0.055, cone: 1.3, twist: 0.9, bias: 0.35 },
  { name: 'rUpperArm', bone: 'rUpperArm', end: 'rForearm', parent: 'chest', mass: 2.2, capsule: 0.055, cone: 1.3, twist: 0.9, bias: 0.35 },
  { name: 'lForearm', bone: 'lForearm', end: 'lHand', extend: 0.09, parent: 'lUpperArm', mass: 1.6, capsule: 0.045, cone: 1.1, twist: 0.4, bias: 1.05 },
  { name: 'rForearm', bone: 'rForearm', end: 'rHand', extend: 0.09, parent: 'rUpperArm', mass: 1.6, capsule: 0.045, cone: 1.1, twist: 0.4, bias: 1.05 },
  { name: 'lThigh', bone: 'lThigh', end: 'lCalf', parent: 'pelvis', mass: 8.5, capsule: 0.075, cone: 0.85, twist: 0.35, bias: 0.45 },
  { name: 'rThigh', bone: 'rThigh', end: 'rCalf', parent: 'pelvis', mass: 8.5, capsule: 0.075, cone: 0.85, twist: 0.35, bias: 0.45 },
  { name: 'lCalf', bone: 'lCalf', end: 'lFoot', toe: 'lToe', parent: 'lThigh', mass: 4.5, capsule: 0.055, cone: 0.75, twist: 0.15, bias: -0.8 },
  { name: 'rCalf', bone: 'rCalf', end: 'rFoot', toe: 'rToe', parent: 'rThigh', mass: 4.5, capsule: 0.055, cone: 0.75, twist: 0.15, bias: -0.8 },
];

// ------------------------------------------------------------------ module temporaries (no per-frame allocation)
const _m = new THREE.Matrix4();
const _m2 = new THREE.Matrix4();
const _v = new THREE.Vector3();
const _v2 = new THREE.Vector3();
const _v3 = new THREE.Vector3();
const _s = new THREE.Vector3();
const _q = new THREE.Quaternion();
const _q2 = new THREE.Quaternion();
const _q3 = new THREE.Quaternion();
const _cv = new CANNON.Vec3();
const _cv2 = new CANNON.Vec3();
const _cq = new CANNON.Quaternion();
const _ANTERIOR = new THREE.Vector3(0, 0, 1);
const _LATERAL = new THREE.Vector3(1, 0, 0);

/** Decompose a world matrix into position + rotation (uniform scale assumed). */
function decomposeTR(matrix, pos, quat) { matrix.decompose(pos, quat, _s); return _s.x; }

// =================================================================================================== Physics system
/** cannon-es world wrapper registered as a game system (ORDER.PHYSICS). */
export class Physics {
  static defaults = PHYSICS;

  constructor() {
    const P = PHYSICS;
    this.world = new CANNON.World({ gravity: new CANNON.Vec3(0, P.gravity, 0) });
    this.world.broadphase = new CANNON.SAPBroadphase(this.world);
    this.world.allowSleep = true;
    this.world.solver.iterations = P.solverIterations;
    this.world.solver.tolerance = P.solverTolerance;

    this.staticMaterial = new CANNON.Material('static');
    this.ragdollMaterial = new CANNON.Material('ragdoll');
    this.world.addContactMaterial(new CANNON.ContactMaterial(this.staticMaterial, this.ragdollMaterial, {
      friction: P.floorFriction, restitution: P.floorRestitution,
    }));
    this.world.defaultContactMaterial.friction = P.wallFriction;
    this.world.defaultContactMaterial.restitution = P.wallRestitution;

    // Court floor: infinite plane (cannon planes face +Z, rotate to +Y).
    this.floor = new CANNON.Body({
      type: CANNON.Body.STATIC, mass: 0, material: this.staticMaterial,
      collisionFilterGroup: COLLISION.STATIC, collisionFilterMask: COLLISION.RAGDOLL,
    });
    this.floor.addShape(new CANNON.Plane());
    this.floor.quaternion.setFromEuler(-Math.PI / 2, 0, 0);
    this.world.addBody(this.floor);

    /** @type {CANNON.Body[]} static boxes mirroring game.arena.colliders */
    this.staticBodies = [];
    this._colliderSource = null;
    this._colliderCount = -1;
    /** @type {Set<Ragdoll>} */
    this.ragdolls = new Set();
    this.steps = 0;
  }

  /** Number of active ragdolls (for stats/debug). */
  get activeCount() { return this.ragdolls.size; }

  /** Re-creates static colliders when the arena (or its collider list) changes. Cheap to call every step. */
  syncStatic() {
    const floorY = game.court ? game.court.floorY : 0;
    if (this.floor.position.y !== floorY) { this.floor.position.y = floorY; this.floor.aabbNeedsUpdate = true; }
    const colliders = game.arena && Array.isArray(game.arena.colliders) ? game.arena.colliders : null;
    if (colliders === this._colliderSource && (!colliders || colliders.length === this._colliderCount)) return;
    for (const b of this.staticBodies) this.world.removeBody(b);
    this.staticBodies.length = 0;
    this._colliderSource = colliders;
    this._colliderCount = colliders ? colliders.length : -1;
    if (!colliders) return;
    for (const box of colliders) this.addStaticBox(box);
  }

  /** Adds a static axis-aligned box (THREE.Box3) and returns its body. */
  addStaticBox(box3) {
    if (!box3 || box3.isEmpty()) return null;
    box3.getSize(_v).multiplyScalar(0.5);
    box3.getCenter(_v2);
    if (_v.x <= 0 || _v.y <= 0 || _v.z <= 0) return null;
    const body = new CANNON.Body({
      type: CANNON.Body.STATIC, mass: 0, material: this.staticMaterial,
      collisionFilterGroup: COLLISION.STATIC, collisionFilterMask: COLLISION.RAGDOLL,
    });
    body.addShape(new CANNON.Box(new CANNON.Vec3(_v.x, _v.y, _v.z)));
    body.position.set(_v2.x, _v2.y, _v2.z);
    this.world.addBody(body);
    this.staticBodies.push(body);
    return body;
  }

  addRagdoll(r) { this.ragdolls.add(r); }
  removeRagdoll(r) { this.ragdolls.delete(r); }

  /** Fixed 60 Hz step on SCALED time (called by the kernel loop; hitstop/pause simply stop calling it). */
  fixedUpdate(dt) {
    if (this.ragdolls.size === 0 || !(dt > 0)) return;
    this.syncStatic();
    for (const r of this.ragdolls) r.beforeStep();
    this.world.step(dt);
    this.steps++;
    for (const r of this.ragdolls) r.afterStep(dt);
  }

  dispose() {
    for (const r of [...this.ragdolls]) r.deactivate();
    for (const b of [...this.world.bodies]) this.world.removeBody(b);
    this.staticBodies.length = 0;
    this._colliderSource = null;
  }
}

/** Returns game.physics, creating + registering one if the boot sequence did not (graceful degradation). */
export function ensurePhysics() {
  if (!game.physics) {
    game.physics = new Physics();
    if (typeof game.addSystem === 'function') game.addSystem(game.physics, 50 /* ORDER.PHYSICS */);
  }
  return game.physics;
}

// =================================================================================================== Ragdoll
/**
 * Ragdoll for one Avatar. Construct while the avatar's model is still in its bind pose (Avatar.load does this):
 * the constructor measures the skeleton and pre-computes body frames, shape layout and joint axes.
 */
export class Ragdoll {
  static defaults = RAGDOLL;

  /** @param {import('./avatar.js').Avatar} avatar */
  constructor(avatar) {
    this.avatar = avatar;
    this.active = false;
    this.physics = null;
    /** Per-part runtime + bind data, parents always before children. */
    this.parts = [];
    this.constraints = [];
    this.totalMass = 0;
    this._hasTrack = false;
    this._prepare();
  }

  /** Measures the bind pose (model space) and builds the part/joint templates. */
  _prepare() {
    const bones = this.avatar.bones;
    const model = this.avatar.model;
    model.updateMatrixWorld(true);
    const modelInv = new THREE.Matrix4().copy(model.matrixWorld).invert();
    const bindPos = (bone, out) => out.setFromMatrixPosition(_m.multiplyMatrices(modelInv, bone.matrixWorld));
    const bindQuat = (bone, out) => { _m.multiplyMatrices(modelInv, bone.matrixWorld).decompose(_v3, out, _s); return out; };

    const byName = new Map();
    for (const def of PARTS) {
      const bone = bones[def.bone];
      if (!bone) continue;
      const qBind = bindQuat(bone, new THREE.Quaternion());
      const pBind = bindPos(bone, new THREE.Vector3());
      const qBindInv = qBind.clone().invert();

      // Segment (bone-local, model units).
      const seg = new THREE.Vector3();
      if (def.endLocal) seg.fromArray(def.endLocal);
      else if (bones[def.end]) seg.copy(bindPos(bones[def.end], _v)).sub(pBind).applyQuaternion(qBindInv);
      else seg.set(0.2, 0, 0);
      let len = seg.length();
      const yAxis = seg.clone().normalize();
      if (def.extend) len += def.extend;

      // Body frame inside the bone frame: Y along the segment, X = lateral projected, Z = X x Y.
      const lateral = _LATERAL.clone().applyQuaternion(qBindInv);
      const xAxis = lateral.sub(_v.copy(yAxis).multiplyScalar(lateral.dot(yAxis)));
      if (xAxis.lengthSq() < 1e-6) xAxis.copy(_ANTERIOR).applyQuaternion(qBindInv).cross(yAxis);
      xAxis.normalize();
      const zAxis = new THREE.Vector3().crossVectors(xAxis, yAxis).normalize();
      const qBodyInBone = new THREE.Quaternion().setFromRotationMatrix(new THREE.Matrix4().makeBasis(xAxis, yAxis, zAxis));
      const centerT = def.centerT !== undefined ? def.centerT : 0.5;
      const centerLocal = yAxis.clone().multiplyScalar(len * centerT); // bone-local (model units)

      const part = {
        def, name: def.name, bone, len, centerLocal, qBodyInBone, qBoneInBody: qBodyInBone.clone().invert(),
        qBind, pBind, parent: def.parent ? byName.get(def.parent) || null : null,
        toeLocal: null, axisA: null, body: null,
        // tracking (animated pose -> initial velocities)
        pos: new THREE.Vector3(), quat: new THREE.Quaternion(), vel: new THREE.Vector3(), ang: new THREE.Vector3(),
        // interpolation
        prevPos: new THREE.Vector3(), prevQuat: new THREE.Quaternion(), curPos: new THREE.Vector3(), curQuat: new THREE.Quaternion(),
      };
      if (def.toe && bones[def.toe]) {
        // Toe position expressed in the body frame (model units), relative to the body centre.
        part.toeLocal = bindPos(bones[def.toe], new THREE.Vector3()).sub(pBind).applyQuaternion(qBindInv)
          .sub(centerLocal).applyQuaternion(part.qBoneInBody);
      }
      byName.set(def.name, part);
      this.parts.push(part);
    }

    // Joint cone axes in the PARENT body frame, from the bind pose + flexion bias.
    for (const part of this.parts) {
      if (!part.parent) continue;
      const P = part.parent;
      const dir = new THREE.Vector3(0, 1, 0).applyQuaternion(part.qBodyInBone).applyQuaternion(part.qBind); // model space
      const bias = part.def.bias || 0;
      if (bias) {
        const ax = _v.crossVectors(dir, _ANTERIOR);
        if (ax.lengthSq() > 1e-8) dir.applyQuaternion(_q.setFromAxisAngle(ax.normalize(), bias));
      }
      const qParentBody = _q2.copy(P.qBind).multiply(P.qBodyInBone);
      part.axisA = dir.applyQuaternion(qParentBody.invert()).normalize();
    }
    this.partByName = byName;
  }

  /** World position of the hips body (camera follow), or null. */
  getHipsPosition(out) {
    const p = this.parts[0];
    if (!p || !p.body) return null;
    return out.set(p.body.position.x, p.body.position.y, p.body.position.z);
  }

  /**
   * Samples the animated pose (call once per rendered frame while NOT ragdolled, after the final pose is computed and
   * world matrices are fresh). Keeps world-space centres/orientations and finite-difference velocities per part.
   */
  track(dt, teleported = false) {
    for (const part of this.parts) {
      const scale = decomposeTR(part.bone.matrixWorld, _v, _q);
      // centre = bone origin + R_bone * centerLocal * scale ; body orientation = R_bone * qBodyInBone
      _v2.copy(part.centerLocal).multiplyScalar(scale).applyQuaternion(_q).add(_v);
      _q2.copy(_q).multiply(part.qBodyInBone);
      if (this._hasTrack && dt > 1e-4 && !teleported) {
        part.vel.subVectors(_v2, part.pos).divideScalar(dt);
        if (part.vel.length() > RAGDOLL.maxInheritedSpeed) part.vel.setLength(RAGDOLL.maxInheritedSpeed);
        // angular velocity from the delta rotation dq = q * prev^-1
        _q3.copy(part.quat).invert().premultiply(_q2);
        if (_q3.w < 0) { _q3.x = -_q3.x; _q3.y = -_q3.y; _q3.z = -_q3.z; _q3.w = -_q3.w; }
        const sinHalf = Math.sqrt(Math.max(0, 1 - _q3.w * _q3.w));
        if (sinHalf > 1e-6) {
          const angle = 2 * Math.atan2(sinHalf, _q3.w);
          part.ang.set(_q3.x / sinHalf, _q3.y / sinHalf, _q3.z / sinHalf).multiplyScalar(angle / dt);
          if (part.ang.length() > RAGDOLL.maxAngularSpeed) part.ang.setLength(RAGDOLL.maxAngularSpeed);
        } else part.ang.set(0, 0, 0);
      } else if (teleported || !this._hasTrack) {
        part.vel.set(0, 0, 0); part.ang.set(0, 0, 0);
      }
      part.pos.copy(_v2);
      part.quat.copy(_q2);
    }
    this._hasTrack = true;
  }

  /** Forget tracked motion (teleports, round resets). */
  resetTracking() { this._hasTrack = false; for (const p of this.parts) { p.vel.set(0, 0, 0); p.ang.set(0, 0, 0); } }

  /**
   * Switch from animation to physics. World matrices of the avatar must be fresh.
   * @param {THREE.Vector3|null} impulse  hit impulse (N*s, any magnitude; mapped to a plausible delta-v)
   * @param {THREE.Vector3|null} point    world hit point (the nearest body part receives extra spin)
   */
  activate(impulse = null, point = null) {
    if (this.active) { this.applyImpulse(impulse, point); return; }
    if (this.parts.length === 0) return;
    const physics = ensurePhysics();
    this.physics = physics;
    physics.syncStatic();
    const world = physics.world;
    const scale = this.avatar.worldScale || 1;
    const massScale = scale * scale * scale;
    this.totalMass = 0;
    const floorY = game.court ? game.court.floorY : 0;
    let minClearance = Infinity;

    for (const part of this.parts) {
      const def = part.def;
      const s = decomposeTR(part.bone.matrixWorld, _v, _q);
      // Current pose -> body transform.
      _v2.copy(part.centerLocal).multiplyScalar(s).applyQuaternion(_q).add(_v);
      _q2.copy(_q).multiply(part.qBodyInBone);

      const body = new CANNON.Body({
        mass: def.mass * massScale,
        material: physics.ragdollMaterial,
        linearDamping: RAGDOLL.linearDamping,
        angularDamping: RAGDOLL.angularDamping,
        collisionFilterGroup: COLLISION.RAGDOLL,
        collisionFilterMask: COLLISION.STATIC,
        allowSleep: true,
        sleepSpeedLimit: RAGDOLL.sleepSpeedLimit,
        sleepTimeLimit: RAGDOLL.sleepTimeLimit,
      });
      const L = part.len * s;
      let lowest = 0; // lowest extent below the centre, for floor clearance
      if (def.box) {
        const hy = def.box[1] > 0 && def.centerT === 0 ? def.box[1] * s : L * 0.5 + def.box[1] * s;
        body.addShape(new CANNON.Box(new CANNON.Vec3(def.box[0] * s, Math.max(0.04, hy), def.box[2] * s)));
        lowest = Math.max(def.box[0], def.box[2]) * s;
      } else if (def.sphere) {
        body.addShape(new CANNON.Sphere(def.sphere * s));
        lowest = def.sphere * s;
      } else if (def.capsule) {
        // Capsule approximated by a chain of three spheres (robust contacts in cannon-es).
        const r = def.capsule * s;
        const half = Math.max(0, L * 0.5 - r * 0.35);
        body.addShape(new CANNON.Sphere(r * 1.05), new CANNON.Vec3(0, -half, 0));
        body.addShape(new CANNON.Sphere(r), new CANNON.Vec3(0, 0, 0));
        body.addShape(new CANNON.Sphere(r * 0.9), new CANNON.Vec3(0, half, 0));
        if (part.toeLocal) {
          const t = part.toeLocal;
          body.addShape(new CANNON.Sphere(r * 0.8), new CANNON.Vec3(t.x * s, t.y * s, t.z * s));
        }
        lowest = r;
      }
      body.position.set(_v2.x, _v2.y, _v2.z);
      body.quaternion.set(_q2.x, _q2.y, _q2.z, _q2.w);
      body.velocity.set(part.vel.x, part.vel.y, part.vel.z);
      body.angularVelocity.set(part.ang.x, part.ang.y, part.ang.z);
      part.body = body;
      part.curPos.copy(_v2); part.prevPos.copy(_v2);
      part.curQuat.copy(_q2); part.prevQuat.copy(_q2);
      this.totalMass += body.mass;
      minClearance = Math.min(minClearance, _v2.y - lowest - floorY);
    }

    // Never start inside the floor (crouch/slide poses): lift everything by the penetration.
    if (minClearance < 0.005) {
      const lift = 0.005 - minClearance;
      for (const part of this.parts) { part.body.position.y += lift; part.curPos.y += lift; part.prevPos.y += lift; }
    }
    for (const part of this.parts) world.addBody(part.body);

    // Joints.
    this.constraints.length = 0;
    for (const part of this.parts) {
      if (!part.parent || !part.parent.body) continue;
      const A = part.parent.body, B = part.body;
      const jointWorld = _v.setFromMatrixPosition(part.bone.matrixWorld);
      // pivots in body-local frames
      const pivotA = A.pointToLocalFrame(_cv.set(jointWorld.x, jointWorld.y, jointWorld.z), new CANNON.Vec3());
      const pivotB = B.pointToLocalFrame(_cv.set(jointWorld.x, jointWorld.y, jointWorld.z), new CANNON.Vec3());
      const axisA = new CANNON.Vec3(part.axisA.x, part.axisA.y, part.axisA.z);
      const axisB = new CANNON.Vec3(0, 1, 0);
      const def = part.def;

      // Effective twist limit: cannon's twist tangents are not unit length for oblique axes; compensate.
      const tA = new CANNON.Vec3(), tB = new CANNON.Vec3();
      axisA.tangents(tA, tA);
      axisB.tangents(tB, tB);
      const tAmag = tA.length() * tB.length();
      const twistLimit = Math.acos(clamp(tAmag * Math.cos(def.twist), -1, 1));
      const coneLimit = def.cone;

      // Current (animated) configuration: widen limits so the solver never snaps the initial pose.
      A.vectorToWorldFrame(axisA, _cv); B.vectorToWorldFrame(axisB, _cv2);
      const coneNow = Math.acos(clamp(_cv.dot(_cv2), -1, 1));
      A.vectorToWorldFrame(tA, _cv); B.vectorToWorldFrame(tB, _cv2);
      const twistNow = Math.acos(clamp(_cv.dot(_cv2), -1, 1));
      const c = new CANNON.ConeTwistConstraint(A, B, {
        pivotA, pivotB, axisA, axisB,
        angle: Math.max(coneLimit, coneNow + RAGDOLL.limitMargin),
        twistAngle: Math.max(twistLimit, twistNow + RAGDOLL.limitMargin),
        collideConnected: false,
        maxForce: 1e6,
      });
      c._duConeLimit = coneLimit;
      c._duTwistLimit = twistLimit;
      world.addConstraint(c);
      this.constraints.push(c);
    }

    this.active = true;
    physics.addRagdoll(this);
    this.applyImpulse(impulse, point);
  }

  /** Adds a hit impulse to an active ragdoll (subsequent hits while down, shockwaves...). */
  applyImpulse(impulse, point) {
    if (!this.active || this.parts.length === 0) return;
    const mag = impulse ? impulse.length() : 0;
    const dv = ragdollDeltaV(mag, this.totalMass, RAGDOLL.impulseGain, RAGDOLL.minDeltaV, RAGDOLL.maxDeltaV);
    // Direction: along the impulse, else backwards from where the avatar faces (collapse).
    if (mag > 1e-6) _v.copy(impulse).divideScalar(mag);
    else this.avatar.getForward(_v).negate();
    _v.y = Math.max(_v.y, 0);
    _v.normalize();

    // Whole-body share (keeps the body together) + upward lift.
    const bodyDv = dv * (1 - RAGDOLL.strikeShare);
    for (const part of this.parts) {
      const b = part.body;
      b.velocity.x += _v.x * bodyDv;
      b.velocity.y += _v.y * bodyDv + dv * RAGDOLL.upward;
      b.velocity.z += _v.z * bodyDv;
      b.wakeUp();
    }
    // Struck part: extra impulse applied at the hit point -> spin.
    let struck = this.parts[1] || this.parts[0];
    if (point) {
      let best = Infinity;
      for (const part of this.parts) {
        const p = part.body.position;
        const d = (p.x - point.x) ** 2 + (p.y - point.y) ** 2 + (p.z - point.z) ** 2;
        if (d < best) { best = d; struck = part; }
      }
    }
    const J = this.totalMass * dv * RAGDOLL.strikeShare;
    const b = struck.body;
    const rel = point ? _cv2.set(point.x - b.position.x, point.y - b.position.y, point.z - b.position.z) : _cv2.set(0, 0, 0);
    // Keep the lever arm inside the body part so tiny parts do not spin absurdly.
    const rl = rel.length();
    if (rl > 0.25) rel.scale(0.25 / rl, rel);
    b.applyImpulse(_cv.set(_v.x * J, _v.y * J, _v.z * J), rel);
    this._clampVelocities();
  }

  /** Physics step hooks (called by Physics.fixedUpdate). */
  beforeStep() {
    for (const part of this.parts) {
      const b = part.body;
      if (!b) continue;
      part.prevPos.set(b.position.x, b.position.y, b.position.z);
      part.prevQuat.set(b.quaternion.x, b.quaternion.y, b.quaternion.z, b.quaternion.w);
    }
  }

  afterStep(dt) {
    let bad = false;
    for (const part of this.parts) {
      const b = part.body;
      if (!b) continue;
      if (!Number.isFinite(b.position.x + b.position.y + b.position.z + b.quaternion.w)) { bad = true; break; }
      part.curPos.set(b.position.x, b.position.y, b.position.z);
      part.curQuat.set(b.quaternion.x, b.quaternion.y, b.quaternion.z, b.quaternion.w);
    }
    if (bad) {
      // Numerical blow-up (should not happen): freeze the last good pose instead of propagating NaNs to the mesh.
      console.warn('[ragdoll] unstable simulation - freezing pose');
      this._freezeInPlace();
      return;
    }
    this._clampVelocities();
    // Relax temporarily widened limits back to anatomical values.
    const shrink = RAGDOLL.limitRelaxRate * dt;
    for (const c of this.constraints) {
      if (c.angle > c._duConeLimit) c.angle = Math.max(c._duConeLimit, c.angle - shrink);
      if (c.twistAngle > c._duTwistLimit) c.twistAngle = Math.max(c._duTwistLimit, c.twistAngle - shrink);
    }
  }

  _clampVelocities() {
    for (const part of this.parts) {
      const b = part.body;
      if (!b) continue;
      const v = b.velocity.length();
      if (v > RAGDOLL.maxLinearSpeed) b.velocity.scale(RAGDOLL.maxLinearSpeed / v, b.velocity);
      const w = b.angularVelocity.length();
      if (w > RAGDOLL.maxAngularSpeed) b.angularVelocity.scale(RAGDOLL.maxAngularSpeed / w, b.angularVelocity);
    }
  }

  _freezeInPlace() {
    for (const part of this.parts) {
      const b = part.body;
      if (!b) continue;
      b.position.set(part.prevPos.x, part.prevPos.y, part.prevPos.z);
      b.quaternion.set(part.prevQuat.x, part.prevQuat.y, part.prevQuat.z, part.prevQuat.w);
      b.velocity.set(0, 0, 0); b.angularVelocity.set(0, 0, 0);
      b.mass = 0; b.type = CANNON.Body.STATIC; b.updateMassProperties();
      part.curPos.copy(part.prevPos); part.curQuat.copy(part.prevQuat);
    }
  }

  /** True when every part is asleep (the body has settled). */
  get settled() {
    if (!this.active) return true;
    for (const p of this.parts) if (p.body && p.body.sleepState !== CANNON.Body.SLEEPING) return false;
    return true;
  }

  /**
   * Writes the (interpolated) body transforms into the bones. Parents are processed before children, so each bone's
   * parent world matrix is refreshed first and the world -> local conversion is exact; only the pelvis receives a
   * position (every other bone keeps its local offset so the skin never stretches).
   * @param {number} alpha interpolation between the previous and the current physics step (game.alpha)
   */
  applyToBones(alpha = 1) {
    if (!this.active) return;
    const a = clamp(alpha, 0, 1);
    for (let i = 0; i < this.parts.length; i++) {
      const part = this.parts[i];
      const bone = part.bone;
      const parent = bone.parent;
      if (!parent) continue;
      // interpolated body transform
      _v.lerpVectors(part.prevPos, part.curPos, a);
      _q.slerpQuaternions(part.prevQuat, part.curQuat, a);
      // bone world rotation = body rotation * (body-in-bone)^-1
      _q.multiply(part.qBoneInBody);
      parent.updateWorldMatrix(true, false);
      const ps = decomposeTR(parent.matrixWorld, _v2, _q2);
      _q3.copy(_q2).invert();
      bone.quaternion.copy(_q3).multiply(_q);
      if (i === 0) {
        // pelvis position: body centre - R_bone * centerLocal * scale, expressed in the parent's space
        const s = this.avatar.worldScale || 1;
        _v3.copy(part.centerLocal).multiplyScalar(s).applyQuaternion(_q);
        _v.sub(_v3).sub(_v2).applyQuaternion(_q3).divideScalar(ps || 1);
        bone.position.copy(_v);
      }
      bone.updateMatrix();
      bone.updateWorldMatrix(false, false);
    }
  }

  /** Removes bodies/constraints from the world (the bones keep their last pose). */
  deactivate() {
    if (!this.active) return;
    const world = this.physics && this.physics.world;
    if (world) {
      for (const c of this.constraints) world.removeConstraint(c);
      for (const part of this.parts) if (part.body) world.removeBody(part.body);
    }
    for (const part of this.parts) part.body = null;
    this.constraints.length = 0;
    this.physics && this.physics.removeRagdoll(this);
    this.active = false;
    this.resetTracking();
  }

  dispose() { this.deactivate(); this.parts.length = 0; }
}
