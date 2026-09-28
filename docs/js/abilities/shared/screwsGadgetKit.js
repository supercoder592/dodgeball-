// ---------------------------------------------------------------------------------------------------------------
// Gadget kit shared by the tank heroes (Bear, Gouki, Screws): guarded access to optional systems (VFX, audio, juice),
// ball iteration / steering helpers, player geometry helpers, world-object registration, procedural PBR textures and
// shared realistic materials. Everything here degrades gracefully when a system is missing.
// ---------------------------------------------------------------------------------------------------------------
import * as THREE from 'three';
import { game, ORDER } from '../../game.js';
import { TEAM, TEAM_COLORS } from '../../core/constants.js';
import { periodicValueNoise2 } from './screwsGadgetMath.js';

/** Ball states (mirrors combat/ball.js). */
export const BALL = Object.freeze({ FREE: 'free', HELD: 'held', LIVE: 'live', STASIS: 'stasis', DESPAWNED: 'despawned' });
/** Fixed simulation step (s); used to decide whether a ball field already ran this frame. */
const FIELD_FRESH_WINDOW = 2.5 / 60;
const EMPTY = Object.freeze([]);
const UP = new THREE.Vector3(0, 1, 0);

const _warned = new Set();
function warnOnce(key, e) {
  if (_warned.has(key)) return;
  _warned.add(key);
  console.warn(`[tank abilities] ${key} failed`, e);
}

// ------------------------------------------------------------------ optional systems

/** One-shot VFX (`game.vfx.play`). */
export function fx(id, position, opts) {
  const v = game.vfx;
  if (!v || !v.play || !position) return;
  try { v.play(id, position, opts); } catch (e) { warnOnce('vfx.play ' + id, e); }
}
/** Attached VFX; returns a handle for fxStop (or null). */
export function fxAttach(id, object3D, opts) {
  const v = game.vfx;
  if (!v || !v.attach || !object3D) return null;
  try { return v.attach(id, object3D, opts) || null; } catch (e) { warnOnce('vfx.attach ' + id, e); return null; }
}
export function fxStop(handle) {
  const v = game.vfx;
  if (!v || !v.stop || handle == null) return;
  try { v.stop(handle); } catch (e) { warnOnce('vfx.stop', e); }
}
/** Positional SFX (`game.audio.play(id, position, volume, pitch)`). */
export function sfx(id, position, volume = 1, pitch = 1) {
  const a = game.audio;
  if (!a || !a.play) return;
  try { a.play(id, position, volume, pitch); } catch (e) { warnOnce('audio.play ' + id, e); }
}
/** Camera trauma (0..1, trauma^2 shake model in Juice). */
export function trauma(t) { try { game.juice?.addTrauma?.(t); } catch (e) { warnOnce('juice.addTrauma', e); } }
/** Timed shake (trauma units, Hz, real seconds, source position for distance falloff). */
export function shake(amplitude, frequency, duration, sourcePos = null) {
  try { game.juice?.shake?.(amplitude, frequency, duration, sourcePos); } catch (e) { warnOnce('juice.shake', e); }
}
/** Hitstop through the juice pipeline (clamped there to the 0.03-0.1 s spec), else straight on the clock. */
export function hitstop(duration) {
  try {
    if (game.juice && game.juice.hitstop) game.juice.hitstop(duration);
    else if (game.time && game.time.hitstop) game.time.hitstop(Math.min(0.1, Math.max(0.03, duration)));
  } catch (e) { warnOnce('hitstop', e); }
}
/** Full-screen grade pulse (`renderer.pulse`). */
export function pulse(type, intensity, duration) {
  try { game.renderer?.pulse?.(type, intensity, duration); } catch (e) { warnOnce('renderer.pulse', e); }
}
/** Emit an event safely. */
export function emit(name, payload) { try { game.events.emit(name, payload); } catch (e) { warnOnce('emit ' + name, e); } }

// ------------------------------------------------------------------ world objects

/**
 * Registers a self-updating world object (barrier, puddle, turret...) as a game system so it keeps animating (realDt)
 * and running its lifetime (scaled dt) independently of the ability phase that spawned it.
 */
export function addWorldObject(obj) { if (obj) game.addSystem(obj, ORDER.ABILITIES); return obj; }
export function removeWorldObject(obj) { if (obj) game.removeSystem(obj); }

/** Disposes geometries/materials under `root` (skips anything flagged userData.shared) and detaches it. */
export function disposeObject3D(root) {
  if (!root) return;
  root.traverse((o) => {
    if (o.geometry && !o.geometry.userData?.shared) o.geometry.dispose();
    const m = o.material;
    if (m) for (const mat of Array.isArray(m) ? m : [m]) if (!mat.userData?.shared) mat.dispose();
  });
  if (root.parent) root.parent.remove(root);
}

// ------------------------------------------------------------------ balls

/** Every active ball (match + ability balls); falls back to the match balls. Never allocates. */
export function allBalls() {
  const b = game.balls;
  if (!b) return EMPTY;
  return b.active || b.matchBalls || EMPTY;
}
export function matchBalls() {
  const b = game.balls;
  return (b && (b.matchBalls || b.active)) || EMPTY;
}
/** Team of the player who last threw `ball` (TEAM.NONE when unknown). */
export function throwerTeam(ball) {
  const t = ball && ball.lastThrower;
  return t && typeof t.team === 'number' ? t.team : TEAM.NONE;
}
/** Sets a ball's velocity through its API when present (wakes sleeping rigid state), else writes the vector. */
export function setBallVelocity(ball, v) {
  if (!ball) return;
  if (ball.setVelocity) ball.setVelocity(v);
  else if (ball.velocity) ball.velocity.copy(v);
}
export function registerBallField(field) {
  const b = game.balls;
  if (!b || !b.registerField) return false;
  try { b.registerField(field); return true; } catch (e) { warnOnce('registerField', e); return false; }
}
export function unregisterBallField(field) {
  const b = game.balls;
  if (!b || !b.unregisterField) return;
  try { b.unregisterField(field); } catch (e) { warnOnce('unregisterField', e); }
}

/**
 * Remembers when a ball field last processed a ball (scaled clock). The owning ability runs the same logic manually in
 * its per-frame tick for balls the BallManager did not route through the field (e.g. free balls, or no BallManager).
 */
export class FieldStamp {
  constructor() { this._map = new WeakMap(); }
  touch(ball) { this._map.set(ball, game.time.now); }
  /** True when the field already handled `ball` during the last couple of fixed steps. */
  fresh(ball) {
    const t = this._map.get(ball);
    return t !== undefined && game.time.now - t <= FIELD_FRESH_WINDOW;
  }
}

// ------------------------------------------------------------------ players

/** Planar unit forward of a player (yaw 0 faces +Z). */
export function planarForward(player, out) {
  const f = player && player.forward;
  if (f) out.set(f.x, 0, f.z); else out.set(Math.sin(player?.yaw || 0), 0, Math.cos(player?.yaw || 0));
  if (out.lengthSq() < 1e-8) out.set(0, 0, 1);
  return out.normalize();
}
/** Planar unit right of a planar forward (character's right hand side). */
export function planarRight(forward, out) { return out.set(-forward.z, 0, forward.x); }

/** Chest point of a player (copied, never the getter's own vector). */
export function chestOf(player, out) {
  const c = player && player.chestPosition;
  if (c) return out.copy(c);
  out.copy(player.position);
  out.y += (player.height || 1.8) * 0.72;
  return out;
}

const _hf = new THREE.Vector3(), _hr = new THREE.Vector3();
/** World position of the player's right palm socket (falls back to a point in front of the chest). */
export function rightHandOf(player, out) {
  const s = player && player.avatar && player.avatar.rightHandSocket;
  if (s && s.getWorldPosition) {
    s.getWorldPosition(out);
    if (Number.isFinite(out.x) && out.distanceToSquared(player.position) < 9) return out;
  }
  planarForward(player, _hf);
  planarRight(_hf, _hr);
  chestOf(player, out);
  out.y -= 0.12;
  return out.addScaledVector(_hf, 0.42).addScaledVector(_hr, 0.2);
}

export function isAlive(p) { return !!p && (!p.health || p.health.isAlive !== false) && !(p.health && p.health.isEliminated); }
export function isInfield(p) { return !!p && p.zone === 'infield'; }
export function isGrounded(p) {
  if (!p) return false;
  if (typeof p.isGrounded === 'boolean') return p.isGrounded;
  return !p.motor || p.motor.isGrounded !== false;
}
export function hasStatus(p, type) { return !!(p && p.status && p.status.has && p.status.has(type)); }

/** Multiplies a hit's knockback whether it is a scalar (m/s) or a vector. */
export function scaleKnockback(hit, k) {
  if (!hit) return;
  const kb = hit.knockback;
  if (typeof kb === 'number') hit.knockback = kb * k;
  else if (kb && kb.multiplyScalar) kb.multiplyScalar(k);
}

/** Team colour as THREE.Color (cached per team). */
const _teamColors = new Map();
export function teamColor(team, fallback = 0xffffff) {
  if (!_teamColors.has(team)) {
    const hex = TEAM_COLORS[team] ?? fallback;
    _teamColors.set(team, new THREE.Color(hex));
  }
  return _teamColors.get(team);
}

/** Orient a unit-Y object (cylinder) between two points. */
const _seg = new THREE.Vector3();
export function orientBetween(obj, a, b, thickness) {
  _seg.subVectors(b, a);
  const len = _seg.length();
  obj.position.copy(a);
  if (len < 1e-5) { obj.scale.set(thickness, 1e-4, thickness); return; }
  obj.quaternion.setFromUnitVectors(UP, _seg.multiplyScalar(1 / len));
  obj.scale.set(thickness, len, thickness);
}

// ------------------------------------------------------------------ procedural textures & materials

const _texCache = new Map();
/**
 * Tileable tangent-space normal map from fBm value noise (DataTexture: works without a DOM canvas).
 * @param {string} key cache key
 * @param {{size?:number, period?:number, octaves?:number, strength?:number, seed?:number, ridged?:boolean}} o
 */
export function noiseNormalMap(key, o = {}) {
  if (_texCache.has(key)) return _texCache.get(key);
  const size = o.size || 128, period = o.period || 8, octaves = o.octaves || 3, strength = o.strength || 2, seed = o.seed || 1;
  const h = new Float32Array(size * size);
  for (let y = 0; y < size; y++) {
    for (let x = 0; x < size; x++) {
      let amp = 1, freq = 1, sum = 0, norm = 0;
      for (let k = 0; k < octaves; k++) {
        const p = period * freq;
        let n = periodicValueNoise2((x / size) * p, (y / size) * p, p, seed + k * 13);
        if (o.ridged) n = 1 - Math.abs(n * 2 - 1);
        sum += n * amp; norm += amp; amp *= 0.5; freq *= 2;
      }
      h[y * size + x] = sum / norm;
    }
  }
  const data = new Uint8Array(size * size * 4);
  for (let y = 0; y < size; y++) {
    for (let x = 0; x < size; x++) {
      const xl = h[y * size + ((x - 1 + size) % size)], xr = h[y * size + ((x + 1) % size)];
      const yd = h[((y - 1 + size) % size) * size + x], yu = h[((y + 1) % size) * size + x];
      let nx = (xl - xr) * strength, ny = (yd - yu) * strength, nz = 1;
      const inv = 1 / Math.hypot(nx, ny, nz);
      nx *= inv; ny *= inv; nz *= inv;
      const i = (y * size + x) * 4;
      data[i] = (nx * 0.5 + 0.5) * 255; data[i + 1] = (ny * 0.5 + 0.5) * 255; data[i + 2] = (nz * 0.5 + 0.5) * 255; data[i + 3] = 255;
    }
  }
  const tex = new THREE.DataTexture(data, size, size, THREE.RGBAFormat);
  tex.wrapS = tex.wrapT = THREE.RepeatWrapping;
  tex.magFilter = THREE.LinearFilter;
  tex.minFilter = THREE.LinearMipmapLinearFilter;
  tex.generateMipmaps = true;
  tex.colorSpace = THREE.NoColorSpace;
  tex.needsUpdate = true;
  tex.userData.shared = true;
  _texCache.set(key, tex);
  return tex;
}

const _matCache = new Map();
/**
 * Shared realistic PBR materials for gadgets (never disposed by disposeObject3D).
 * kinds: 'gunmetal' (powder-coated dark steel), 'steel' (machined), 'rubber', 'paint:<team>' (team enamel),
 * 'emissive:<hex>', 'glass' (dark smoked polycarbonate), 'ball' (red foam-rubber dodgeball).
 */
export function gadgetMaterial(kind) {
  if (_matCache.has(kind)) return _matCache.get(kind);
  let m;
  const cast = noiseNormalMap('castMetal', { size: 128, period: 16, octaves: 3, strength: 0.9, seed: 21 });
  if (kind === 'gunmetal') {
    m = new THREE.MeshStandardMaterial({ color: 0x2f3338, metalness: 0.8, roughness: 0.42, normalMap: cast, normalScale: new THREE.Vector2(0.35, 0.35) });
  } else if (kind === 'steel') {
    m = new THREE.MeshStandardMaterial({ color: 0xa9aeb4, metalness: 1, roughness: 0.26 });
  } else if (kind === 'rubber') {
    m = new THREE.MeshStandardMaterial({ color: 0x151515, metalness: 0, roughness: 0.88 });
  } else if (kind === 'glass') {
    m = new THREE.MeshPhysicalMaterial({ color: 0x0e1418, metalness: 0, roughness: 0.08, clearcoat: 1, clearcoatRoughness: 0.05, transparent: true, opacity: 0.55 });
  } else if (kind === 'ball') {
    const pebble = noiseNormalMap('ballPebble', { size: 128, period: 24, octaves: 2, strength: 2.2, seed: 5 });
    m = new THREE.MeshStandardMaterial({ color: 0xa3141b, metalness: 0, roughness: 0.62, normalMap: pebble, normalScale: new THREE.Vector2(0.6, 0.6) });
  } else if (kind.startsWith('paint:')) {
    const team = Number(kind.slice(6));
    m = new THREE.MeshPhysicalMaterial({ color: teamColor(team, 0x55c22b).clone().multiplyScalar(0.8), metalness: 0.25, roughness: 0.38, clearcoat: 0.6, clearcoatRoughness: 0.25 });
  } else if (kind.startsWith('emissive:')) {
    const c = new THREE.Color(Number(kind.slice(9)));
    m = new THREE.MeshStandardMaterial({ color: 0x111111, emissive: c, emissiveIntensity: 3, roughness: 0.4, metalness: 0 });
  } else {
    m = new THREE.MeshStandardMaterial({ color: 0x808080, roughness: 0.5 });
  }
  m.userData.shared = true;
  _matCache.set(kind, m);
  return m;
}

const _geoCache = new Map();
/** Shared geometry by key (built once by `make`). */
export function sharedGeometry(key, make) {
  if (!_geoCache.has(key)) {
    const g = make();
    g.userData.shared = true;
    _geoCache.set(key, g);
  }
  return _geoCache.get(key);
}

/** Shared scene parent (null-safe). */
export function sceneRoot() { return game.scene || null; }
