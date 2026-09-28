// ---------------------------------------------------------------------------------------------------------------
// Elsa's world objects (three.js side):
//   * getFrostTextures()  procedural, allocation-once PBR frost textures (albedo / alpha / normal / glint), built from
//                         typed arrays as DataTextures (no canvas, no files), cached for the whole session.
//   * IceTrailField       Frost Trail floor patches: ONE InstancedMesh (single draw call) of thin frosted-ice sheets
//                         laid under a thrown ball's ground track. Patches flash-form, hold and melt (shrink + wet tint);
//                         the field answers "is this foot on the ice?" for the haste buff.
//   * FrostSheen          Absolute Zero overlay: a large transparent crystalline-frost plane 6 mm above the enemy half,
//                         creeping from the centre line to the baseline with world-locked UVs, plus cold-mist emitters.
// Look: realistic ice - low roughness, clearcoat, IOR 1.31, normal-mapped fractures, and a view-dependent glitter term
// injected into the emissive chunk so individual crystals flash as the camera moves (no cartoon shading).
// Gameplay rules (haste, slippery, statuses) live in heroes/elsa.js; this file is presentation + geometry queries.
// ---------------------------------------------------------------------------------------------------------------
import * as THREE from 'three';
import { game } from '../../game.js';
import { patchEnvelope, pointInPatch, fbm, hash2 } from './elsaFrostMath.js';

/** Tuning shared by the frost visuals. */
export const FROST_LOOK = Object.freeze({
  iceColor: 0xdcefff,          // albedo tint of the trail ice (bluish white)
  sheenColor: 0xeaf6ff,        // albedo tint of the Absolute Zero glaze
  glintColor: 0xcfeaff,        // colour of crystal glints
  wetTint: [0.7, 0.79, 0.88],  // instance colour of melting ice (darker, wet)
  patchOpacity: 0.92,
  roughness: 0.14,
  clearcoatRoughness: 0.05,
  ior: 1.31,                   // water ice
  patchGlint: 2.6,             // glitter strength (emissive multiplier)
  sheenGlint: 2.0,
  patchHeight: 0.004,          // metres above the floor (avoids z-fighting with the painted lines)
  sheenHeight: 0.006,
  sheenTile: 2.25,             // world size (m) of one frost texture tile on the sheen
  growTime: 0.12,              // flash-freeze formation of a patch (s)
  meltTime: 0.7,               // melt (shrink) at the end of a patch's life (s)
  solidEnvelope: 0.3,          // a forming / melting patch counts as "ice underfoot" above this size fraction
});

// ------------------------------------------------------------------ procedural textures

const smoothstep = (a, b, x) => { const t = Math.min(1, Math.max(0, (x - a) / (b - a))); return t * t * (3 - 2 * t); };
const clamp01 = (x) => (x < 0 ? 0 : x > 1 ? 1 : x);

/** Small deterministic PRNG (mulberry32) for texture synthesis - never touches the gameplay RNG. */
function mulberry(seed) {
  let s = seed >>> 0;
  return () => {
    s = (s + 0x6d2b79f5) >>> 0;
    let t = Math.imul(s ^ (s >>> 15), s | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

function makeDataTexture(data, n, srgb, repeat) {
  const t = new THREE.DataTexture(data, n, n, THREE.RGBAFormat, THREE.UnsignedByteType);
  t.wrapS = t.wrapT = repeat ? THREE.RepeatWrapping : THREE.ClampToEdgeWrapping;
  t.magFilter = THREE.LinearFilter;
  t.minFilter = THREE.LinearMipmapLinearFilter;
  t.generateMipmaps = true;
  t.anisotropy = 4;
  t.colorSpace = srgb ? THREE.SRGBColorSpace : THREE.NoColorSpace;
  t.needsUpdate = true;
  return t;
}

/** Tangent-space normal map (RGBA8) from a height field by central differences. */
function normalsFromHeight(h, n, strength, tile) {
  const out = new Uint8Array(n * n * 4);
  const at = (x, y) => {
    if (tile) { x = (x + n) % n; y = (y + n) % n; } else { x = Math.min(n - 1, Math.max(0, x)); y = Math.min(n - 1, Math.max(0, y)); }
    return h[y * n + x];
  };
  for (let y = 0; y < n; y++) {
    for (let x = 0; x < n; x++) {
      let nx = -(at(x + 1, y) - at(x - 1, y)) * strength;
      let ny = -(at(x, y + 1) - at(x, y - 1)) * strength;
      let nz = 1;
      const l = Math.hypot(nx, ny, nz);
      nx /= l; ny /= l; nz /= l;
      const i = (y * n + x) * 4;
      out[i] = (nx * 0.5 + 0.5) * 255; out[i + 1] = (ny * 0.5 + 0.5) * 255; out[i + 2] = (nz * 0.5 + 0.5) * 255; out[i + 3] = 255;
    }
  }
  return out;
}

/** Packs albedo (grey level * tint), alpha (grey, alphaMap reads G) and glint (R mask, G phase) into textures. */
function packTextures(n, albedo, alpha, height, glintMask, glintPhase, tint, normalStrength, tile) {
  const a = new Uint8Array(n * n * 4), m = new Uint8Array(n * n * 4), g = new Uint8Array(n * n * 4);
  for (let i = 0; i < n * n; i++) {
    const j = i * 4, v = clamp01(albedo[i]);
    a[j] = v * tint[0] * 255; a[j + 1] = v * tint[1] * 255; a[j + 2] = v * tint[2] * 255; a[j + 3] = 255;
    const al = clamp01(alpha[i]) * 255;
    m[j] = al; m[j + 1] = al; m[j + 2] = al; m[j + 3] = 255;
    g[j] = glintMask[i] * 255; g[j + 1] = glintPhase[i] * 255; g[j + 2] = 0; g[j + 3] = 255;
  }
  return {
    map: makeDataTexture(a, n, true, tile),
    alphaMap: makeDataTexture(m, n, false, tile),
    normalMap: makeDataTexture(normalsFromHeight(height, n, normalStrength, tile), n, false, tile),
    glintMap: makeDataTexture(g, n, false, tile),
  };
}

/** One organic sheet of frosted ice (non-tiling): soft irregular outline, fractures, fine frost grain, sparse crystals. */
function buildPatchTextures(n, seed) {
  const albedo = new Float32Array(n * n), alpha = new Float32Array(n * n), height = new Float32Array(n * n);
  const gm = new Float32Array(n * n), gp = new Float32Array(n * n);
  for (let y = 0; y < n; y++) {
    for (let x = 0; x < n; x++) {
      const i = y * n + x;
      const u = (x + 0.5) / n, v = (y + 0.5) / n;
      // Rounded-rectangle metric (superellipse, p = 2.6): consecutive patches along the ball's track merge into one
      // continuous sheet instead of reading as separate blobs.
      const du = Math.abs(u * 2 - 1), dv = Math.abs(v * 2 - 1);
      const r = Math.pow(Math.pow(du, 2.6) + Math.pow(dv, 2.6), 1 / 2.6);
      const edge = 0.8 + 0.45 * (fbm(u, v, 4, 3, seed) - 0.5);    // organic outline
      const body = fbm(u, v, 8, 4, seed + 3);                       // ice thickness variation
      const fine = fbm(u, v, 32, 2, seed + 11);                     // frost grain
      const crack = 1 - smoothstep(0, 0.028, Math.abs(fbm(u, v, 3, 3, seed + 7) - 0.5)); // fracture network
      const cover = (1 - smoothstep(edge - 0.16, edge + 0.04, r)) * (0.74 + 0.26 * body);
      alpha[i] = cover * (0.86 + 0.14 * fine) + crack * cover * 0.12;
      height[i] = 0.55 * body + 0.3 * fine - 0.4 * crack;
      albedo[i] = 0.8 + 0.12 * fine + 0.1 * crack - 0.07 * body;  // fractures scatter white, thick ice reads bluer
      if (cover > 0.3 && hash2(x, y, seed + 13) > 0.986) { gm[i] = 0.55 + 0.45 * hash2(x, y, seed + 19); gp[i] = hash2(x, y, seed + 17); }
    }
  }
  return packTextures(n, albedo, alpha, height, gm, gp, [0.9, 0.96, 1.0], 3.2, false);
}

/** Random-walk frost dendrites (fern-like crystal growth) stamped into a tiling buffer. */
function drawDendrites(buf, n, count, seed) {
  const rnd = mulberry(seed);
  const stamp = (x, y, v) => {
    const ix = ((Math.round(x) % n) + n) % n, iy = ((Math.round(y) % n) + n) % n;
    const i = iy * n + ix;
    if (buf[i] < v) buf[i] = v;
    const w = v * 0.4;
    const l = iy * n + ((ix + n - 1) % n), r = iy * n + ((ix + 1) % n), u = ((iy + n - 1) % n) * n + ix, d = ((iy + 1) % n) * n + ix;
    if (buf[l] < w) buf[l] = w; if (buf[r] < w) buf[r] = w; if (buf[u] < w) buf[u] = w; if (buf[d] < w) buf[d] = w;
  };
  const walk = (x, y, ang, len, depth) => {
    for (let s = 0; s < len; s++) {
      stamp(x, y, 1 - 0.55 * (s / len));
      ang += (rnd() - 0.5) * 0.22;
      x += Math.cos(ang); y += Math.sin(ang);
      if (depth < 2 && rnd() < 0.085) walk(x, y, ang + (rnd() < 0.5 ? 1 : -1) * (0.85 + rnd() * 0.35), len * 0.42, depth + 1);
    }
  };
  for (let k = 0; k < count; k++) walk(rnd() * n, rnd() * n, rnd() * Math.PI * 2, 30 + rnd() * 50, 0);
}

/** Tiling crystalline frost glaze for the Absolute Zero sheen. */
function buildSheenTextures(n, seed) {
  const albedo = new Float32Array(n * n), alpha = new Float32Array(n * n), height = new Float32Array(n * n);
  const gm = new Float32Array(n * n), gp = new Float32Array(n * n), dend = new Float32Array(n * n);
  drawDendrites(dend, n, 64, seed);
  for (let y = 0; y < n; y++) {
    for (let x = 0; x < n; x++) {
      const i = y * n + x;
      const u = x / n, v = y / n; // periodic noise needs [0,1) sampling for a seamless tile
      const cover = fbm(u, v, 4, 5, seed);
      const fine = fbm(u, v, 32, 2, seed + 5);
      const d = dend[i];
      const base = 0.42 + 0.58 * smoothstep(0.28, 0.7, cover);
      alpha[i] = Math.max(base * (0.78 + 0.22 * fine), d * 0.95);
      height[i] = 0.45 * cover + 0.25 * fine + 0.65 * d;
      albedo[i] = 0.78 + 0.12 * fine + 0.12 * d;
      if (hash2(x, y, seed + 29) > 0.988) { gm[i] = (0.5 + 0.5 * hash2(x, y, seed + 31)) * Math.min(1, base + d); gp[i] = hash2(x, y, seed + 37); }
    }
  }
  return packTextures(n, albedo, alpha, height, gm, gp, [0.93, 0.97, 1.0], 2.4, true);
}

let _textures = null;
/**
 * Session-wide frost textures (built once, ~10 ms). Shared by every Elsa effect and intentionally never disposed
 * (a few hundred KB of GPU memory; rebuilding every match would only cost time).
 * @returns {{patch:{map,alphaMap,normalMap,glintMap}, sheen:{map,alphaMap,normalMap,glintMap}}}
 */
export function getFrostTextures() {
  if (!_textures) _textures = { patch: buildPatchTextures(128, 11), sheen: buildSheenTextures(256, 23) };
  return _textures;
}

/**
 * Injects a view-dependent glitter term into a MeshPhysicalMaterial's emissive: each crystal of the glint map
 * (R = mask, G = random phase) flashes only when the view direction sweeps across its phase, like real frost under
 * floodlights. `strength` scales the flash.
 */
function addGlitter(material, strength, key) {
  const uGlint = { value: strength };
  material.userData.uGlint = uGlint;
  material.onBeforeCompile = (shader) => {
    shader.uniforms.uGlint = uGlint;
    shader.fragmentShader = 'uniform float uGlint;\n' + shader.fragmentShader.replace(
      '#include <emissivemap_fragment>',
      `#include <emissivemap_fragment>
#ifdef USE_EMISSIVEMAP
  {
    vec3 glintView = normalize( vViewPosition );
    float glintPhase = dot( glintView, vec3( 12.9898, 78.233, 37.719 ) ) * 2.5 + emissiveColor.g * 43.0;
    float glint = pow( 0.5 + 0.5 * sin( glintPhase ), 16.0 );
    totalEmissiveRadiance = emissive * emissiveColor.r * ( 0.1 + glint * uGlint );
  }
#endif`);
  };
  material.customProgramCacheKey = () => key;
}

// ------------------------------------------------------------------ Frost Trail patches

const _m4 = new THREE.Matrix4();
const _pos = new THREE.Vector3();
const _quat = new THREE.Quaternion();
const _scl = new THREE.Vector3();
const _col = new THREE.Color();
const _up = new THREE.Vector3(0, 1, 0);
const _zeroMatrix = new THREE.Matrix4().makeScale(0, 0, 0);

/**
 * Pool of Frost Trail ice patches rendered as one InstancedMesh. Ages on SCALED time (hitstop/pause freeze the melt).
 */
export class IceTrailField {
  /**
   * @param {{capacity?:number, width?:number, length?:number}} opts width across travel, length along travel (m)
   */
  constructor({ capacity = 120, width = 0.9, length = 1.08 } = {}) {
    this.capacity = capacity;
    this.width = width;
    this.length = length;
    this.x = new Float32Array(capacity);
    this.z = new Float32Array(capacity);
    this.yaw = new Float32Array(capacity);
    this.age = new Float32Array(capacity);
    this.life = new Float32Array(capacity);
    this.jitter = new Float32Array(capacity); // per-patch size variation
    this.env = new Float32Array(capacity);    // current size envelope 0..1
    this.active = new Uint8Array(capacity);
    this.activeCount = 0;
    this._highWater = 0;
    this._serial = 0;
    this.mesh = null;
    this.material = null;
    this.geometry = null;
  }

  _ensureMesh() {
    if (this.mesh) return this.mesh;
    const tex = getFrostTextures().patch;
    this.geometry = new THREE.PlaneGeometry(this.width, this.length);
    this.geometry.rotateX(-Math.PI / 2); // lie flat, normal +Y; local +Z = travel direction
    this.material = new THREE.MeshPhysicalMaterial({
      color: FROST_LOOK.iceColor, map: tex.map, alphaMap: tex.alphaMap, normalMap: tex.normalMap,
      normalScale: new THREE.Vector2(0.55, 0.55), roughness: FROST_LOOK.roughness, metalness: 0,
      clearcoat: 1, clearcoatRoughness: FROST_LOOK.clearcoatRoughness, ior: FROST_LOOK.ior, specularIntensity: 1,
      emissive: FROST_LOOK.glintColor, emissiveMap: tex.glintMap, emissiveIntensity: 1,
      transparent: true, opacity: FROST_LOOK.patchOpacity, depthWrite: false,
      polygonOffset: true, polygonOffsetFactor: -2, polygonOffsetUnits: -2,
    });
    addGlitter(this.material, FROST_LOOK.patchGlint, 'elsa-ice-patch');
    const mesh = new THREE.InstancedMesh(this.geometry, this.material, this.capacity);
    mesh.name = 'ElsaIceTrail';
    mesh.instanceMatrix.setUsage(THREE.DynamicDrawUsage);
    mesh.frustumCulled = false; // instances move every frame; bounds would need recomputing
    mesh.receiveShadow = true;
    mesh.castShadow = false;
    mesh.renderOrder = 2;       // over the Absolute Zero sheen
    _col.setRGB(1, 1, 1);
    for (let i = 0; i < this.capacity; i++) { mesh.setMatrixAt(i, _zeroMatrix); mesh.setColorAt(i, _col); }
    mesh.count = 0;
    mesh.visible = false;
    this.mesh = mesh;
    return mesh;
  }

  /** Lays a patch centred at (x, z) facing `yaw`; recycles the most melted patch when the pool is full. */
  spawn(x, z, yaw, life) {
    const mesh = this._ensureMesh();
    if (!mesh.parent && game.scene) game.scene.add(mesh);
    let slot = -1;
    for (let i = 0; i < this.capacity; i++) if (!this.active[i]) { slot = i; break; }
    if (slot < 0) {
      let best = -1;
      for (let i = 0; i < this.capacity; i++) {
        const f = this.age[i] / Math.max(1e-3, this.life[i]);
        if (f > best) { best = f; slot = i; }
      }
    } else {
      this.activeCount++;
    }
    const k = this._serial++;
    this.x[slot] = x; this.z[slot] = z;
    this.yaw[slot] = yaw + (hash2(k, 3, 5) - 0.5) * 0.28;       // +/- 8 deg so the trail is not a ruler line
    this.jitter[slot] = 0.9 + 0.22 * hash2(k, 7, 5);
    this.age[slot] = 0; this.life[slot] = life; this.env[slot] = 0;
    this.active[slot] = 1;
    if (slot + 1 > this._highWater) this._highWater = slot + 1;
    mesh.count = this._highWater;
    mesh.visible = true;
    return slot;
  }

  /** Ages patches on scaled time and rewrites their instance transforms. */
  update(dt) {
    const mesh = this.mesh;
    if (!mesh || this.activeCount === 0) return;
    const floorY = game.court ? game.court.floorY : 0;
    const wet = FROST_LOOK.wetTint;
    let high = 0;
    for (let i = 0; i < this._highWater; i++) {
      if (!this.active[i]) continue;
      this.age[i] += dt;
      const env = patchEnvelope(this.age[i], this.life[i], FROST_LOOK.growTime, FROST_LOOK.meltTime);
      this.env[i] = env;
      if (env <= 0) {
        this.active[i] = 0; this.activeCount--;
        mesh.setMatrixAt(i, _zeroMatrix);
        continue;
      }
      high = i + 1;
      const s = this.jitter[i] * env;
      _pos.set(this.x[i], floorY + FROST_LOOK.patchHeight + (i % 4) * 0.0004, this.z[i]);
      _quat.setFromAxisAngle(_up, this.yaw[i]);
      _scl.set(s, 1, s);
      mesh.setMatrixAt(i, _m4.compose(_pos, _quat, _scl));
      // Melting ice darkens toward wet floor.
      const meltStart = this.life[i] - FROST_LOOK.meltTime;
      const w = this.age[i] > meltStart ? Math.min(1, (this.age[i] - meltStart) / FROST_LOOK.meltTime) : 0;
      _col.setRGB(1 + (wet[0] - 1) * w, 1 + (wet[1] - 1) * w, 1 + (wet[2] - 1) * w);
      mesh.setColorAt(i, _col);
    }
    this._highWater = high;
    mesh.count = high;
    mesh.visible = high > 0;
    mesh.instanceMatrix.needsUpdate = true;
    if (mesh.instanceColor) mesh.instanceColor.needsUpdate = true;
  }

  /** True when planar point (px, pz) stands on a (sufficiently formed) patch, grown by `margin` metres. */
  containsPoint(px, pz, margin = 0) {
    if (this.activeCount === 0) return false;
    const hw = this.width * 0.5, hl = this.length * 0.5;
    for (let i = 0; i < this._highWater; i++) {
      if (!this.active[i] || this.env[i] < FROST_LOOK.solidEnvelope) continue;
      const s = this.jitter[i] * this.env[i];
      if (pointInPatch(px, pz, this.x[i], this.z[i], this.yaw[i], hw * s, hl * s, margin)) return true;
    }
    return false;
  }

  /** Removes every patch (round reset). */
  clear() {
    this.active.fill(0);
    this.activeCount = 0;
    this._highWater = 0;
    if (this.mesh) {
      for (let i = 0; i < this.capacity; i++) this.mesh.setMatrixAt(i, _zeroMatrix);
      this.mesh.count = 0;
      this.mesh.visible = false;
      this.mesh.instanceMatrix.needsUpdate = true;
    }
  }

  dispose() {
    this.clear();
    if (this.mesh) {
      this.mesh.removeFromParent();
      if (this.mesh.dispose) this.mesh.dispose();
    }
    if (this.geometry) this.geometry.dispose();
    if (this.material) this.material.dispose();
    this.mesh = null; this.geometry = null; this.material = null;
  }
}

// ------------------------------------------------------------------ Absolute Zero sheen

/**
 * Crystalline frost glaze over one team's infield half. Placement is court-relative: `place(sideSign, ...)` puts the
 * sheen's origin on the centre line and extends it toward that side's baseline. `setEnvelope(spread, opacity)` creeps
 * it out (world-locked UVs, so crystals never stretch) and fades it. Cold-mist VFX emitters ride on anchors over it.
 */
export class FrostSheen {
  constructor({ mistCount = 6 } = {}) {
    this.mistCount = mistCount;
    this.root = null;
    this.mesh = null;
    this.material = null;
    this.geometry = null;
    this.textures = null; // per-sheen clones (own repeat/offset), sharing the cached image sources
    this._texList = null;
    this.anchors = [];
    this._mistHandles = [];
    this._mistFallback = false;
    this._mistTimer = 0;
    this._mistColor = 0xdcefff;
    this._mistScale = 1.6;
    this.width = 9;
    this.depth = 9;
  }

  _ensure() {
    if (this.root) return;
    const src = getFrostTextures().sheen;
    this.textures = { map: src.map.clone(), alphaMap: src.alphaMap.clone(), normalMap: src.normalMap.clone(), glintMap: src.glintMap.clone() };
    this._texList = Object.values(this.textures);
    for (const t of this._texList) t.needsUpdate = true;
    this.geometry = new THREE.PlaneGeometry(1, 1);
    this.geometry.rotateX(-Math.PI / 2);
    this.geometry.translate(0, 0, 0.5); // spans local z in [0, 1]: z = 0 on the centre line (v = 1), z = 1 at the baseline
    this.material = new THREE.MeshPhysicalMaterial({
      color: FROST_LOOK.sheenColor, map: this.textures.map, alphaMap: this.textures.alphaMap, normalMap: this.textures.normalMap,
      normalScale: new THREE.Vector2(0.4, 0.4), roughness: FROST_LOOK.roughness + 0.04, metalness: 0,
      clearcoat: 1, clearcoatRoughness: FROST_LOOK.clearcoatRoughness, ior: FROST_LOOK.ior, specularIntensity: 1,
      emissive: FROST_LOOK.glintColor, emissiveMap: this.textures.glintMap, emissiveIntensity: 1,
      transparent: true, opacity: 0, depthWrite: false,
      polygonOffset: true, polygonOffsetFactor: -1, polygonOffsetUnits: -1,
    });
    addGlitter(this.material, FROST_LOOK.sheenGlint, 'elsa-frost-sheen');
    this.mesh = new THREE.Mesh(this.geometry, this.material);
    this.mesh.name = 'ElsaAbsoluteZeroSheen';
    this.mesh.receiveShadow = true;
    this.mesh.renderOrder = 1;
    this.root = new THREE.Group();
    this.root.name = 'ElsaAbsoluteZero';
    this.root.add(this.mesh);
    for (let i = 0; i < this.mistCount; i++) { const a = new THREE.Object3D(); this.anchors.push(a); this.root.add(a); }
    this.root.visible = false;
  }

  /**
   * @param {number} sideSign -1 = Home half (z < 0), +1 = Away half (z > 0)
   * @param {number} width court width (m)
   * @param {number} depth half-court length (m)
   * @param {number} floorY
   */
  place(sideSign, width, depth, floorY) {
    this._ensure();
    if (!this.root.parent && game.scene) game.scene.add(this.root);
    this.width = width; this.depth = depth;
    this.root.position.set(0, floorY + FROST_LOOK.sheenHeight, 0);
    this.root.rotation.set(0, sideSign < 0 ? Math.PI : 0, 0); // local +Z points into the frozen half
    // Mist anchors: 2 lanes x N/2 rows over the half, just above the ice.
    const rows = Math.max(1, Math.ceil(this.anchors.length / 2));
    this.anchors.forEach((a, i) => {
      const lane = i % 2 === 0 ? -0.25 : 0.25, row = Math.floor(i / 2);
      a.position.set(lane * width, 0.05, depth * (row + 0.5) / rows);
    });
    this.root.updateMatrixWorld(true);
    this.setEnvelope(0, 0);
  }

  /** @param {number} spread 0..1 creep from the centre line  @param {number} opacity 0..1 */
  setEnvelope(spread, opacity) {
    if (!this.root) return;
    const d = Math.max(0.001, this.depth * spread);
    this.mesh.scale.set(this.width, 1, d);
    // World-locked tiling: texcoord depends only on the distance from the centre line (see geometry note above).
    const rx = this.width / FROST_LOOK.sheenTile, ry = d / FROST_LOOK.sheenTile;
    for (let i = 0; i < this._texList.length; i++) { const t = this._texList[i]; t.repeat.set(rx, ry); t.offset.set(0, -ry); }
    this.material.opacity = opacity;
    this.root.visible = opacity > 0.002;
  }

  /** Starts looping cold mist on the anchors for `duration` seconds (VFX attach; periodic one-shots as a fallback). */
  startMist(duration, color, scale) {
    this.stopMist();
    this._mistColor = color; this._mistScale = scale;
    const vfx = game.vfx;
    this._mistFallback = false;
    if (!vfx) return;
    for (const a of this.anchors) {
      let h = null;
      try { h = vfx.attach ? vfx.attach('frozenMist', a, { duration, color, scale }) : null; } catch (e) { h = null; }
      if (h) this._mistHandles.push(h); else this._mistFallback = true;
    }
    this._mistTimer = 0;
  }

  /** Fallback mist pulses when VFX attach is unavailable (scaled dt). */
  tickMist(dt) {
    if (!this._mistFallback || !game.vfx || !game.vfx.play) return;
    this._mistTimer -= dt;
    if (this._mistTimer > 0) return;
    this._mistTimer = 0.9;
    for (const a of this.anchors) {
      a.getWorldPosition(_pos);
      try { game.vfx.play('frozenMist', _pos, { scale: this._mistScale, color: this._mistColor }); } catch (e) { this._mistFallback = false; }
    }
  }

  stopMist() {
    const vfx = game.vfx;
    for (const h of this._mistHandles) { try { if (vfx && vfx.stop) vfx.stop(h); } catch (e) { /* optional system */ } }
    this._mistHandles.length = 0;
    this._mistFallback = false;
  }

  hide() {
    this.stopMist();
    if (this.root) { this.material.opacity = 0; this.root.visible = false; }
  }

  dispose() {
    this.hide();
    if (this.root) this.root.removeFromParent();
    if (this.geometry) this.geometry.dispose();
    if (this.material) this.material.dispose();
    if (this._texList) for (const t of this._texList) t.dispose();
    this.root = null; this.mesh = null; this.geometry = null; this.material = null; this.textures = null; this._texList = null;
    this.anchors.length = 0;
  }
}
