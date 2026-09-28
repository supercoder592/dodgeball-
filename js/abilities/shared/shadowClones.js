// ---------------------------------------------------------------------------------------------------------------
// Shadow's illusions (shared world objects for heroes/shadow.js): web port of the Unity ShadowClone +
// ShadowIllusionBall (Assets/.../Abilities/Shared/Shadow*.cs).
//
//   ShadowClone   a realistic body that looks exactly like the player it mimics, registered in `game.hittables` as a
//                 vertical capsule. Enemy balls that touch it make it pop (cloneDissolve smoke) and are absorbed (they
//                 drop dead at the contact point); piercing balls pop it and keep going; friendly balls and passes
//                 pass through. Two kinds:
//                   'animated'   avatar.createClone(): an animated duplicate that mirrors the mimic's animation, follows
//                                a formation slot with exponential lag (Night Parade, Mirage Formation)
//                   'afterimage' avatar.spawnAfterimage(): a fading pose snapshot with a short-lived hittable body
//                                (Decoy Dash); it only arms once the real body has run clear of it
//   Illusion ball the harmless ball a clone "throws" when its mimic throws: purely visual (never registered with the
//                 BallManager), same look as a match ball, flies ballistically and vanishes after 0.6 s or on the floor.
//
// AI: `activeClones` lists every live illusion so bots can be fooled the same way humans are - use
// `pickPerceivedAimPoint(target, rng01, out)` when aiming at a player that has clones, `findNearestClone` to shoot
// decoys, `countClonesOf` to reason about obscured targets.
//
// Everything here runs on SCALED time: a tiny system (registered lazily at ORDER.MATCH + 1, i.e. right after the players
// and their avatars updated this frame) advances clones and illusion balls, so hitstop and pause freeze them too.
// ---------------------------------------------------------------------------------------------------------------
import * as THREE from 'three';
import { game, ORDER } from '../../game.js';
import { EV } from '../../core/events.js';
import { BALL_RADIUS, GRAVITY } from '../../core/constants.js';
import { segmentVsVerticalCapsule, expSmoothing, deltaAngle, pickPerceivedIndex } from './shadowMath.js';

/** What a clone is made of. */
export const CLONE_KIND = Object.freeze({ ANIMATED: 'animated', AFTERIMAGE: 'afterimage' });

/** Shared tuning of every Shadow illusion. */
export const SHADOW_CLONE_TUNING = {
  followSharpness: 10,        // 1/s: how tightly animated clones follow their desired pose (lower = more lag)
  turnSharpness: 12,          // 1/s: yaw easing
  dissolveColor: 0x4d475f,    // smoky violet-grey of the pop / dissolve puff
  afterimageColor: 0x2a2c38,  // dark, smoky translucent silhouette of Decoy Dash afterimages
  popVolume: 0.8,
  popShake: 0.12,             // Perlin shake amplitude when a ball bursts an illusion
  chestHeightFraction: 0.72,  // chest point = feet + height * this (what an aiming thrower targets)
  radiusFallback: 0.32,       // m, capsule radius when the mimic does not expose one
  maxAbilityControlledLifetime: 20, // s safety net: owner-controlled clones never outlive this (longest ability: 6 s)
  illusionBallVanishScale: 0.35,
};

/** Every live Shadow illusion (animated clones and Decoy Dash afterimages). Read-only for other modules. */
export const activeClones = [];

// Module-scope temporaries.
const UP = new THREE.Vector3(0, 1, 0);
const _rel = new THREE.Vector3();
const _origin = new THREE.Vector3();
const _vel = new THREE.Vector3();
const _dir = new THREE.Vector3();
const _tmp = new THREE.Vector3();

/**
 * A Shadow illusion on the court. Implements the `game.hittables` interface:
 *   { team, intersect(from, to, radius) -> {t, point, normal}|null, onBallHit(ball, hit) -> 'pass'|'block'|'absorb'|'handled' }
 */
export class ShadowClone {
  /**
   * @param {object} mimic Player this illusion looks like (its team's balls pass through)
   * @param {object} caster the Shadow who created it
   * @param {string} kind CLONE_KIND
   * @param {number} lifetime s (<= 0: the owning ability dissolves it explicitly)
   */
  constructor(mimic, caster, kind, lifetime = 0) {
    this.mimic = mimic;
    this.caster = caster || mimic;
    this.kind = kind;
    this.team = mimic ? mimic.team : -1;
    this.alive = true;
    this.armed = true;
    this.armDistance = 0;
    this.age = 0;
    this.lifetime = lifetime;
    this.position = new THREE.Vector3();
    this.yaw = 0;
    this.desired = new THREE.Vector3();
    this.desiredYaw = 0;
    this.followSharpness = SHADOW_CLONE_TUNING.followSharpness;
    this._sharpnessOverride = -1;
    this.planarSpeed = 0;
    this.radius = Math.max(0.2, (mimic && mimic.radius) || SHADOW_CLONE_TUNING.radiusFallback);
    this.height = Math.max(1.2, (mimic && mimic.height) || (mimic && mimic.hero && mimic.hero.height) || 1.8);
    /** { root, update(dt), dispose() } from avatar.createClone() (animated clones). */
    this.visual = null;
    /** Whatever avatar.spawnAfterimage() returned (afterimage decoys). */
    this.afterimage = null;
    /** Optional callback (clone, poppedByBall) raised once when the illusion disappears for any reason. */
    this.onVanished = null;
    this._chest = new THREE.Vector3();
    this._hit = { t: 0, point: new THREE.Vector3(), normal: new THREE.Vector3() };
  }

  /** True while enemy balls can burst it. */
  get isHittable() { return this.alive && this.armed; }
  /** Chest-height point (what an aiming thrower would target). Returns an internal vector: copy it. */
  get chestPosition() { return this._chest.set(this.position.x, this.position.y + this.height * SHADOW_CLONE_TUNING.chestHeightFraction, this.position.z); }
  /** Remaining life in seconds (+Infinity when the owning ability controls it). */
  get remainingLifetime() { return this.lifetime > 0 ? Math.max(0, this.lifetime - this.age) : Infinity; }

  // ------------------------------------------------------------------ formation control (animated clones)
  /** Where the clone should be (it eases there with `followSharpness`, or `sharpness` when given). */
  setDesiredPose(position, yaw, sharpness = -1) {
    this.desired.copy(position);
    this.desiredYaw = yaw;
    this._sharpnessOverride = sharpness;
  }

  /** Teleports the clone (no easing). */
  snapTo(position, yaw) {
    this.position.copy(position);
    this.desired.copy(position);
    this.yaw = this.desiredYaw = yaw;
    this.planarSpeed = 0;
    this._applyVisualTransform();
  }

  // ------------------------------------------------------------------ mimicry
  /**
   * Mirrors a throw of the real player: plays the throw on the clone and launches a harmless illusion ball from the
   * same release point relative to the body, with the same velocity relative to facing (yaw only).
   */
  mirrorThrow(realOrigin, realVelocity, gravityScale, lifetime) {
    if (!this.alive || this.kind !== CLONE_KIND.ANIMATED || !this.mimic || !realOrigin || !realVelocity) return;
    const dyaw = this.yaw - (this.mimic.yaw || 0);
    _rel.copy(realOrigin).sub(this.mimic.position).applyAxisAngle(UP, dyaw);
    _origin.copy(this.position).add(_rel);
    _vel.copy(realVelocity).applyAxisAngle(UP, dyaw);
    _dir.set(_vel.x, 0, _vel.z);
    if (_dir.lengthSq() > 1e-6) _dir.normalize();
    this.visual?.playThrow?.(_dir);
    spawnIllusionBall(_origin, _vel, gravityScale, lifetime, this.mimic);
  }

  /** Mirrors a catch attempt (the clone's animation otherwise mirrors the mimic's pose). */
  mirrorCatch() {
    if (!this.alive) return;
    this.visual?.playCatch?.();
  }

  // ------------------------------------------------------------------ hittable interface
  /** Swept sphere (ball centre from -> to, radius) vs the body capsule. Per-instance result object (no allocation). */
  intersect(from, to, radius) {
    if (!this.isHittable) return null;
    const r = this.radius;
    const R = r + (radius || BALL_RADIUS);
    const y0 = this.position.y + r, y1 = this.position.y + this.height - r;
    const hit = this._hit;
    const t = segmentVsVerticalCapsule(from, to, this.position, y0, Math.max(y0, y1), R, hit.point, hit.normal);
    if (t < 0) return null;
    hit.t = t;
    // Report the contact on the body surface (the ball centre sits `radius` outside it along the normal).
    hit.point.addScaledVector(hit.normal, -(radius || BALL_RADIUS));
    return hit;
  }

  /** Enemy balls burst the illusion; friendly balls, passes and thrower-less balls pass through. */
  onBallHit(ball, hit) {
    if (!this.isHittable || !ball || ball.isPass) return 'pass';
    const thrower = ball.lastThrower;
    if (!thrower || thrower.team === this.team) return 'pass';
    this.pop((hit && hit.point) || this.chestPosition);
    // Piercing balls (Rayne's beam) burst the illusion and keep flying; everything else is swallowed and drops.
    return ball.pierce ? 'pass' : 'absorb';
  }

  // ------------------------------------------------------------------ lifetime
  /** A ball burst the illusion: smoke, pop sound, tiny shake, gone. */
  pop(point) {
    if (!this.alive) return;
    const at = point || this.chestPosition;
    game.vfx?.play?.('cloneDissolve', this.chestPosition, { scale: 1, color: SHADOW_CLONE_TUNING.dissolveColor });
    game.audio?.play?.('clonePop', at, SHADOW_CLONE_TUNING.popVolume, 1.1 + 0.15 * rand01());
    if (SHADOW_CLONE_TUNING.popShake > 0) game.juice?.shake?.(SHADOW_CLONE_TUNING.popShake, 18, 0.12, at);
    this._kill(true);
  }

  /** The owning ability ended: fade into smoke (optionally silent, e.g. round reset). */
  dissolve(effects = true) {
    if (!this.alive) return;
    if (effects && this.kind === CLONE_KIND.ANIMATED) {
      game.vfx?.play?.('cloneDissolve', this.chestPosition, { scale: 0.8, color: SHADOW_CLONE_TUNING.dissolveColor });
      game.audio?.play?.('clone', this.position, SHADOW_CLONE_TUNING.popVolume * 0.4, 0.9);
    }
    this._kill(false);
  }

  /** Per-frame update (scaled dt) - called by the shared system. */
  update(dt) {
    if (!this.alive) return;
    this.age += dt;
    // The mimic left the match (disposed): nothing to look like any more.
    if (this.mimic && !game.players.includes(this.mimic)) { this._kill(false); return; }
    if (this.lifetime > 0 && this.age >= this.lifetime) { this._kill(false); return; } // afterimage already faded
    if (this.lifetime <= 0 && this.age >= SHADOW_CLONE_TUNING.maxAbilityControlledLifetime) { this.dissolve(true); return; }

    // Decoys only arm once the real body ran clear of them, so they never shield it.
    if (!this.armed && this.mimic) {
      const dx = this.mimic.position.x - this.position.x, dz = this.mimic.position.z - this.position.z;
      if (dx * dx + dz * dz >= this.armDistance * this.armDistance) this.armed = true;
    }

    if (this.kind !== CLONE_KIND.ANIMATED) return;
    if (dt > 0) {
      const k = expSmoothing(this._sharpnessOverride > 0 ? this._sharpnessOverride : this.followSharpness, dt);
      const px = this.position.x, pz = this.position.z;
      this.position.lerp(this.desired, k);
      this.planarSpeed = Math.hypot(this.position.x - px, this.position.z - pz) / dt;
      this.yaw += deltaAngle(this.yaw, this.desiredYaw) * expSmoothing(SHADOW_CLONE_TUNING.turnSharpness, dt);
    }
    if (this.visual) {
      // Advance the duplicate's animation (it mirrors the mimic's avatar), then place it in the world.
      if (typeof this.visual.update === 'function') this.visual.update(dt);
      this._applyVisualTransform();
    }
  }

  _applyVisualTransform() {
    const root = this.visual && this.visual.root;
    if (!root) return;
    root.position.copy(this.position);
    root.rotation.set(0, this.yaw, 0);
  }

  _kill(popped) {
    if (!this.alive) return;
    this.alive = false;
    this.armed = false;
    const i = activeClones.indexOf(this);
    if (i >= 0) activeClones.splice(i, 1);
    game.hittables?.delete?.(this);
    if (this.visual) {
      const root = this.visual.root;
      try { this.visual.dispose?.(); } catch (e) { console.warn('[shadow] clone dispose failed', e); }
      if (root && root.parent) root.parent.remove(root);
      this.visual = null;
    }
    if (this.afterimage && popped) {
      // A popped decoy's afterimage vanishes at once instead of finishing its fade.
      const a = this.afterimage;
      if (typeof a.dispose === 'function') { try { a.dispose(); } catch (e) { /* already gone */ } }
      else if (a.isObject3D) a.visible = false;
      else if (a.root && a.root.isObject3D) a.root.visible = false;
    }
    this.afterimage = null;
    const cb = this.onVanished;
    this.onVanished = null;
    if (cb) cb(this, popped);
  }

  // ------------------------------------------------------------------ factories
  /**
   * Spawns an animated copy of `mimic` at `position` facing `yaw`. Returns null when the mimic has no avatar clone
   * support (headless tests / missing model) - an invisible hittable would be unfair.
   */
  static spawnAnimated(mimic, caster, position, yaw, lifetime = 0, effects = true) {
    const avatar = mimic && mimic.avatar;
    if (!avatar || typeof avatar.createClone !== 'function') return null;
    let visual = null;
    try { visual = avatar.createClone(); } catch (e) { console.warn('[shadow] avatar.createClone failed', e); return null; }
    if (!visual || !visual.root) { visual?.dispose?.(); return null; }
    ensureShadowSystem();
    // The clone lives in world space (not under the mimic's root): the formation places it.
    if (game.scene && visual.root.parent !== game.scene) game.scene.add(visual.root);
    const clone = new ShadowClone(mimic, caster, CLONE_KIND.ANIMATED, lifetime);
    clone.visual = visual;
    clone.snapTo(position, yaw);
    register(clone);
    if (effects) {
      game.vfx?.play?.('cloneSpawn', clone.chestPosition, { scale: 1, color: SHADOW_CLONE_TUNING.dissolveColor });
      game.audio?.play?.('clone', position, 0.7, 0.95 + 0.1 * rand01());
    }
    return clone;
  }

  /**
   * Leaves a fading afterimage of `mimic` (avatar.spawnAfterimage) with a hittable decoy body that lives `lifetime`
   * seconds and arms once the mimic is `armDistance` metres away. Returns null without an avatar to snapshot.
   */
  static spawnDecoy(mimic, lifetime, armDistance, color = SHADOW_CLONE_TUNING.afterimageColor) {
    const avatar = mimic && mimic.avatar;
    if (!avatar || typeof avatar.spawnAfterimage !== 'function') return null;
    let after = null;
    try { after = avatar.spawnAfterimage(lifetime, color); } catch (e) { console.warn('[shadow] avatar.spawnAfterimage failed', e); return null; }
    ensureShadowSystem();
    const clone = new ShadowClone(mimic, mimic, CLONE_KIND.AFTERIMAGE, Math.max(0.05, lifetime));
    clone.afterimage = after || null;
    clone.position.copy(mimic.position);
    clone.desired.copy(mimic.position);
    clone.yaw = clone.desiredYaw = mimic.yaw || 0;
    clone.armDistance = Math.max(0, armDistance || 0);
    clone.armed = clone.armDistance <= 0;
    register(clone);
    return clone;
  }
}

function register(clone) {
  activeClones.push(clone);
  game.hittables?.add?.(clone);
}

function rand01() { return game.rng ? game.rng.next() : Math.random(); }

// ===============================================================================================================
// AI / query helpers (allocation-free unless an output array is passed)
// ===============================================================================================================

/** Number of live, hittable illusions that look like `player`. */
export function countClonesOf(player) {
  if (!player) return 0;
  let n = 0;
  for (const c of activeClones) if (c.isHittable && c.mimic === player) n++;
  return n;
}

/** Fills `out` with the live illusions of `player` (optionally of one kind). Returns `out`. */
export function clonesOf(player, out = [], kind = null) {
  out.length = 0;
  if (!player) return out;
  for (const c of activeClones) if (c.alive && c.mimic === player && (!kind || c.kind === kind)) out.push(c);
  return out;
}

/**
 * What an observer who cannot tell the bodies apart would aim at: the real chest or one of the illusions', chosen
 * uniformly by `r01` (0..1). Writes the aim point into `out` (THREE.Vector3).
 * @returns {ShadowClone|null} the illusion picked, or null when the real player was picked (or has no clones)
 */
export function pickPerceivedAimPoint(target, r01, out) {
  if (!target) return null;
  if (out) out.set(target.position.x, target.position.y + (target.height || 1.8) * SHADOW_CLONE_TUNING.chestHeightFraction, target.position.z);
  const count = countClonesOf(target);
  if (count === 0) return null;
  const pick = pickPerceivedIndex(count, r01);
  if (pick >= count) return null; // the real one
  let n = 0;
  for (const c of activeClones) {
    if (!c.isHittable || c.mimic !== target) continue;
    if (n++ === pick) { if (out) out.copy(c.chestPosition); return c; }
  }
  return null;
}

/** Nearest live, hittable illusion belonging to `team` within `maxDist` of `pos` (or null). */
export function findNearestClone(pos, team, maxDist = Infinity) {
  let best = null, bestD = maxDist * maxDist;
  for (const c of activeClones) {
    if (!c.isHittable || c.team !== team) continue;
    const d = c.position.distanceToSquared(pos);
    if (d < bestD) { bestD = d; best = c; }
  }
  return best;
}

/** True when `obj` is a Shadow illusion (e.g. a hittable returned by a sweep). */
export function isShadowClone(obj) { return obj instanceof ShadowClone; }

/** Dissolves every illusion of `mimic` (optionally only one kind). */
export function dissolveClonesOf(mimic, kind = null, effects = true) {
  for (let i = activeClones.length - 1; i >= 0; i--) {
    const c = activeClones[i];
    if (c && c.mimic === mimic && (!kind || c.kind === kind)) c.dissolve(effects);
  }
}

/** Dissolves every illusion (match start/end safety net). */
export function dissolveAllClones(effects = false, kind = null) {
  for (let i = activeClones.length - 1; i >= 0; i--) {
    const c = activeClones[i];
    if (c && (!kind || c.kind === kind)) c.dissolve(effects);
  }
}

// ===============================================================================================================
// Illusion balls (purely visual)
// ===============================================================================================================

const ILLUSION_POOL_MAX = 16;
const _illusionLive = [];
const _illusionPool = [];
let _fallbackGeometry = null;
let _fallbackMaterial = null;

/** Mesh parts that look like a real match ball: borrowed from a live match ball, else a matte red foam ball. */
function illusionBallLook() {
  const balls = game.balls && game.balls.matchBalls;
  if (balls) {
    for (const b of balls) {
      const src = b && (b.visual || b.root);
      if (!src || typeof src.traverse !== 'function') continue;
      let mesh = null;
      src.traverse((o) => { if (!mesh && o.isMesh && o.geometry) mesh = o; });
      if (!mesh) continue;
      const g = mesh.geometry;
      if (!g.boundingSphere) g.computeBoundingSphere();
      const r = g.boundingSphere ? g.boundingSphere.radius : 0;
      // Scale from the geometry itself so a squash & stretch in progress on the source ball is never copied.
      return { geometry: g, material: mesh.material, scale: r > 1e-4 ? BALL_RADIUS / r : 1 };
    }
  }
  if (!_fallbackGeometry) {
    _fallbackGeometry = new THREE.SphereGeometry(BALL_RADIUS, 32, 20);
    // Matte red rubber dodgeball (physically plausible roughness, no metal).
    _fallbackMaterial = new THREE.MeshStandardMaterial({ color: 0x9c1a12, roughness: 0.62, metalness: 0 });
  }
  return { geometry: _fallbackGeometry, material: _fallbackMaterial, scale: 1 };
}

/**
 * Launches a harmless illusion ball (visual only) at `origin` with `velocity`; it follows gravity x `gravityScale` and
 * vanishes in a puff after `lifetime` seconds, on reaching the floor, or the moment it touches anything solid: any
 * player's body (except `owner`, the real Shadow it mirrors), any game.hittables object (clones, shields, turrets -
 * intersect() only, never onBallHit) or an arena collider. Single-ball rule: it never reads as a second real ball -
 * it cannot hit, be caught or picked up, and it never flies through bodies or walls.
 */
export function spawnIllusionBall(origin, velocity, gravityScale = 0.65, lifetime = 0.6, owner = null) {
  if (!game.scene) return null;
  ensureShadowSystem();
  let item = _illusionPool.pop();
  const look = illusionBallLook();
  if (!item) {
    if (_illusionLive.length >= ILLUSION_POOL_MAX) return null; // cap: never flood the scene
    const mesh = new THREE.Mesh(look.geometry, look.material);
    mesh.castShadow = true;
    mesh.name = 'ShadowIllusionBall';
    item = { mesh, velocity: new THREE.Vector3(), gravity: 0, remaining: 0, age: 0, owner: null, spin: 0, axis: new THREE.Vector3(1, 0, 0) };
  } else {
    item.mesh.geometry = look.geometry;
    item.mesh.material = look.material;
  }
  item.mesh.scale.setScalar(look.scale);
  item.mesh.position.copy(origin);
  item.mesh.visible = true;
  item.velocity.copy(velocity);
  item.gravity = GRAVITY * Math.max(0, gravityScale);
  item.remaining = Math.max(0.05, lifetime);
  item.age = 0;
  item.owner = owner || null;
  // Backspin-free realistic roll: spin axis perpendicular to the flight, rate = speed / radius.
  item.axis.set(velocity.z, 0, -velocity.x);
  if (item.axis.lengthSq() < 1e-6) item.axis.set(1, 0, 0);
  item.axis.normalize();
  item.spin = Math.min(60, velocity.length() / BALL_RADIUS * 0.15);
  if (item.mesh.parent !== game.scene) game.scene.add(item.mesh);
  _illusionLive.push(item);
  return item.mesh;
}

function vanishIllusion(i, effects) {
  const item = _illusionLive[i];
  _illusionLive.splice(i, 1);
  if (effects) game.vfx?.play?.('cloneDissolve', item.mesh.position, { scale: SHADOW_CLONE_TUNING.illusionBallVanishScale, color: SHADOW_CLONE_TUNING.dissolveColor });
  item.mesh.visible = false;
  item.owner = null;
  if (item.mesh.parent) item.mesh.parent.remove(item.mesh);
  if (_illusionPool.length < ILLUSION_POOL_MAX) _illusionPool.push(item);
}

/** Grace (s) after the release before contacts count (the ball leaves a clone's hand next to bodies). */
const ILLUSION_CONTACT_GRACE = 0.04;
const _illFrom = new THREE.Vector3();
const _illPoint = new THREE.Vector3();
const _illNormal = new THREE.Vector3();

/** Does the illusion's step from `from` to `to` touch a player body, a hittable or an arena collider? Allocation-free. */
function illusionTouchesSolid(from, to, owner) {
  const players = game.players;
  if (players) {
    for (let i = 0; i < players.length; i++) {
      const p = players[i];
      if (!p || p === owner || !p.position) continue;
      if (p.health && p.health.isAlive === false) continue;
      const r = (p.radius || 0.35) + BALL_RADIUS;
      const y0 = p.position.y + r, y1 = p.position.y + Math.max(r, (p.height || 1.8) - r);
      if (segmentVsVerticalCapsule(from, to, p.position, y0, y1, r, _illPoint, _illNormal) >= 0) return true;
    }
  }
  const hittables = game.hittables;
  if (hittables && typeof hittables.forEach === 'function') {
    let hit = false;
    for (const h of hittables) {
      if (!h || typeof h.intersect !== 'function') continue;
      try { if (h.intersect(from, to, BALL_RADIUS)) { hit = true; break; } } catch (e) { /* a broken hittable never stops the illusion */ }
    }
    if (hit) return true;
  }
  const cols = game.arena && game.arena.colliders;
  if (cols && cols.length) {
    for (let i = 0; i < cols.length; i++) {
      const c = cols[i];
      const b = c && (c.box || c.aabb || c);
      const mn = b && b.min, mx = b && b.max;
      if (!mn || !mx || !Number.isFinite(mn.x)) continue;
      if (to.x > mn.x - BALL_RADIUS && to.x < mx.x + BALL_RADIUS && to.y > mn.y - BALL_RADIUS && to.y < mx.y + BALL_RADIUS &&
          to.z > mn.z - BALL_RADIUS && to.z < mx.z + BALL_RADIUS) return true;
    }
  }
  return false;
}

function updateIllusionBalls(dt) {
  if (dt <= 0) return;
  const floorY = game.court ? game.court.floorY : 0;
  for (let i = _illusionLive.length - 1; i >= 0; i--) {
    const it = _illusionLive[i];
    it.remaining -= dt;
    it.age += dt;
    _illFrom.copy(it.mesh.position);
    it.velocity.y -= it.gravity * dt;
    it.mesh.position.addScaledVector(it.velocity, dt);
    it.mesh.rotateOnWorldAxis(it.axis, it.spin * dt);
    if (it.remaining <= 0 || it.mesh.position.y <= floorY + BALL_RADIUS) { vanishIllusion(i, true); continue; }
    if (it.age > ILLUSION_CONTACT_GRACE && illusionTouchesSolid(_illFrom, it.mesh.position, it.owner)) vanishIllusion(i, true);
  }
}

/** Removes every illusion ball at once (round reset). */
export function clearIllusionBalls() {
  for (let i = _illusionLive.length - 1; i >= 0; i--) vanishIllusion(i, false);
}

// ===============================================================================================================
// Shared system (lazily registered the first time an illusion is spawned)
// ===============================================================================================================

class ShadowIllusionSystem {
  constructor() {
    // Round/match boundaries: transient decoys and illusion balls never carry over. Animated clones belong to their
    // abilities (which dissolve them on round reset); the match boundaries sweep anything left behind.
    this._unsubs = [
      game.events.on(EV.RoundEnded, () => { dissolveAllClones(false, CLONE_KIND.AFTERIMAGE); clearIllusionBalls(); }),
      game.events.on(EV.RoundStarted, () => { dissolveAllClones(false, CLONE_KIND.AFTERIMAGE); clearIllusionBalls(); }),
      game.events.on(EV.MatchStarted, () => { dissolveAllClones(false); clearIllusionBalls(); }),
      game.events.on(EV.MatchEnded, () => { dissolveAllClones(false); clearIllusionBalls(); }),
    ];
  }

  /** Scaled dt: hitstop and pause freeze illusions like everything else in play. */
  update(dt) {
    for (let i = activeClones.length - 1; i >= 0; i--) {
      const c = activeClones[i];
      if (c) c.update(dt);
    }
    updateIllusionBalls(dt);
  }

  dispose() {
    for (const u of this._unsubs) u();
    this._unsubs = [];
    dissolveAllClones(false);
    clearIllusionBalls();
  }
}

let _system = null;

/** Registers the shared illusion system once (right after Match updated the players this frame). */
export function ensureShadowSystem() {
  if (_system || typeof game.addSystem !== 'function') return _system;
  _system = new ShadowIllusionSystem();
  game.addSystem(_system, ORDER.MATCH + 1);
  return _system;
}
