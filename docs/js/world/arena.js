// ---------------------------------------------------------------------------------------------------------------
// Arena (owner: render) - photo-plausible indoor dodgeball venue around the kernel Court. See WEB_ARCHITECTURE.md §3.4.
//
//   floor      : varnished maple hardwood (procedural PBR tile + clear-coat) with a painted-lines overlay blended in
//                the floor shader (lines, centre circle + wordmark, tinted outfield strips), sports-vinyl run-off
//   walls      : padded run-off walls with seams & branding, painted concrete-block upper walls, dark ceiling
//   stands     : stepped concrete bleachers on both long sides, instanced seats, handrails, LED ribbon boards,
//                suites band, and a crowd of baked Rocketbox impostors (world/crowd.js) that reacts to the match
//   rig        : box truss with floodlight heads; 4 shadow-casting key SpotLights (count/size per quality), fill
//                spots for the stands / end walls, hemisphere ambient; environment lighting comes from the Renderer
//   dressing   : end-wall video boards (live score, world/scoreboard.js), hall-of-fame hero banners, team banners
//   physics    : `colliders` (THREE.Box3[]) for walls, bleacher steps, end walls and the ceiling
// Static geometry is merged per material (~25 draw calls for the whole venue incl. the crowd).
// ---------------------------------------------------------------------------------------------------------------
import * as THREE from 'three';
import { mergeGeometries } from 'three/addons/utils/BufferGeometryUtils.js';
import { game } from '../game.js';
import { COURT, HERO_IDS } from '../core/constants.js';
import { Rng } from '../core/rng.js';
import { Court } from './court.js';
import {
  createHardwoodTextures, createCourtPaintTexture, createVinylTextures, createPaddingTextures,
  createBlockWallTextures, createConcreteTextures, createBannerAtlas,
} from './textures.js';
import { Scoreboard } from './scoreboard.js';
import { Crowd } from './crowd.js';
import { qualityId, qualityPreset } from '../render/quality.js';

/** Venue layout (metres). The run-off walls follow the kernel COURT constants. */
export const ARENA = Object.freeze({
  wallX: COURT.width / 2 + COURT.runOff,                          // 8.5  long-side padded walls
  wallZ: COURT.length / 2 + COURT.outfieldDepth + COURT.runOff,   // 16   end padded walls
  hardwoodHalfX: 7.0,           // hardwood slab (court + outfield strips + margin)
  hardwoodHalfZ: 13.6,
  hardwoodTile: 4,              // metres per hardwood texture tile
  padHeight: 1.2,
  padThickness: 0.14,
  rows: 12,                     // bleacher rows per side
  rowDepth: 0.85,
  rowRise: 0.42,
  seatPitch: 0.52,
  aisleZ: [-9.6, -3.2, 3.2, 9.6],
  aisleWidth: 1.1,
  wallThickness: 0.5,
  ceilingY: 18,
  trussY: 13.2,                 // truss centre height
  trussHalfX: 5.6,
  trussHalfZ: 13,
  trussSize: 0.45,              // box truss cross-section
  trussBay: 0.55,
  lampSpacing: 3,
  scoreboard: { width: 7.4, height: 3.7, y: 9.2 },
  ribbon: { y0: 6.45, y1: 7.15 },
  suites: { y0: 8.4, y1: 10.2 },
  crowdOccupancy: 0.64,
});

/** Lighting rig (three.js physical units: candela for spots). Key lights are listed in shadow priority order. */
export const LIGHTING = Object.freeze({
  keyColor: 0xfff2e2,           // ~5000 K sports LED
  keyIntensity: 300,
  keyAngle: 0.74,
  keyPenumbra: 0.7,
  keyDistance: 48,
  keyDecay: 2,
  keys: [                       // diagonal pairs first so 2 shadow casters still light both halves
    { pos: [-5.6, 12.75, -6], target: [-1.2, 0, -5.4] },
    { pos: [5.6, 12.75, 6], target: [1.2, 0, 5.4] },
    { pos: [5.6, 12.75, -6], target: [1.2, 0, -5.4] },
    { pos: [-5.6, 12.75, 6], target: [-1.2, 0, 5.4] },
  ],
  shadow: { near: 4, far: 34, bias: -0.00025, normalBias: 0.03 },
  fillColor: 0xffe9d2,
  fills: [                      // non-shadow spots: stands and end walls (quality.fillLights)
    { pos: [6, 13.5, 0], target: [14, 3.5, 0], intensity: 150, angle: 1.05 },
    { pos: [-6, 13.5, 0], target: [-14, 3.5, 0], intensity: 150, angle: 1.05 },
    { pos: [0, 13, 9], target: [0, 5.5, 16], intensity: 110, angle: 0.95 },
    { pos: [0, 13, -9], target: [0, 5.5, -16], intensity: 110, angle: 0.95 },
  ],
  hemi: { sky: 0xdfe6f5, ground: 0x6a4b30, intensity: 0.55 },
  lowQualityHemiBoost: 1.5,     // no fill spots on low: lift the ambient instead
  lensGlow: [24, 23, 21],       // HDR radiance of the lamp lenses (drives bloom)
});

/** Material tuning. */
export const ARENA_LOOK = Object.freeze({
  hardwoodNormalScale: 0.45,
  hardwoodRoughness: 1.3,        // multiplier on the roughness map (varnish ~0.34 -> ~0.44 base lobe)
  hardwoodSpecular: 0.6,         // base-layer specular intensity under the clear coat
  hardwoodClearcoat: 0.3,
  hardwoodClearcoatRoughness: 0.08,
  paintRoughness: 0.45,
  paintPxPerMeter: { low: 48, medium: 64, high: 80 },
  seatColor: 0x1c2740,
  screenGain: 1.6,               // video board brightness (HDR, slight bloom)
  ribbonGain: 1.35,
  suiteGlow: [2.6, 2.2, 1.7],     // suite downlights (small, bright, ~3500 K)
  suiteInterior: [0.07, 0.05, 0.035], // dim warm interior seen through the tinted glass
  exitGlow: [0.1, 1.8, 0.45],
});

// ------------------------------------------------------------------------------------------------- helpers
const _v = new THREE.Vector3();
const _v2 = new THREE.Vector3();
const _q = new THREE.Quaternion();
const _e = new THREE.Euler();
const _m = new THREE.Matrix4();
const _one = new THREE.Vector3(1, 1, 1);
const _up = new THREE.Vector3(0, 1, 0);
const _z = new THREE.Vector3(0, 0, 1);

/** Planar world-space UVs per face (box mapping) so tiling textures keep a constant texel density. */
function worldUV(geo, tile) {
  const p = geo.attributes.position, n = geo.attributes.normal, uv = geo.attributes.uv;
  if (!uv || !n) return;
  for (let i = 0; i < p.count; i++) {
    const ax = Math.abs(n.getX(i)), ay = Math.abs(n.getY(i)), az = Math.abs(n.getZ(i));
    const x = p.getX(i), y = p.getY(i), z = p.getZ(i);
    if (ay >= ax && ay >= az) uv.setXY(i, x / tile, z / tile);
    else if (ax >= az) uv.setXY(i, z / tile, y / tile);
    else uv.setXY(i, x / tile, y / tile);
  }
  uv.needsUpdate = true;
}

/** Remaps a geometry's 0..1 UVs into an atlas rect {u0,v0,u1,v1}. */
function remapUV(geo, r) {
  const uv = geo.attributes.uv;
  for (let i = 0; i < uv.count; i++) uv.setXY(i, r.u0 + uv.getX(i) * (r.u1 - r.u0), r.v0 + uv.getY(i) * (r.v1 - r.v0));
  uv.needsUpdate = true;
}

/** Geometry batch merged into one mesh per material. */
class Batch {
  /**
   * @param {string} name
   * @param {THREE.Material} material
   * @param {{ uvTile?:number, cast?:boolean, receive?:boolean, colors?:boolean }} o
   */
  constructor(name, material, { uvTile = 0, cast = false, receive = true, colors = false } = {}) {
    this.name = name; this.material = material; this.uvTile = uvTile;
    this.cast = cast; this.receive = receive; this.colors = colors;
    this.geos = [];
  }
  /** Adds a geometry (already in world space); `color` = [r,g,b] linear (colour batches only). */
  add(geo, color = null) {
    if (this.uvTile) worldUV(geo, this.uvTile);
    if (this.colors) {
      const c = color || [1, 1, 1], n = geo.attributes.position.count, a = new Float32Array(n * 3);
      for (let i = 0; i < n; i++) { a[i * 3] = c[0]; a[i * 3 + 1] = c[1]; a[i * 3 + 2] = c[2]; }
      geo.setAttribute('color', new THREE.BufferAttribute(a, 3));
    }
    this.geos.push(geo);
    return geo;
  }
  /** Axis-aligned (optionally yawed) box by centre and size. */
  box(w, h, d, x, y, z, ry = 0, color = null) {
    const g = new THREE.BoxGeometry(w, h, d);
    _e.set(0, ry, 0); _q.setFromEuler(_e);
    g.applyMatrix4(_m.compose(_v.set(x, y, z), _q, _one));
    return this.add(g, color);
  }
  /** Box spanning two corners (axis-aligned). */
  span(x0, y0, z0, x1, y1, z1, color = null) {
    return this.box(Math.abs(x1 - x0), Math.abs(y1 - y0), Math.abs(z1 - z0), (x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2, 0, color);
  }
  /** Square beam of `t` thickness from a to b (Vector3-like arrays or vectors). */
  beam(a, b, t, color = null) {
    const ax = a.x ?? a[0], ay = a.y ?? a[1], az = a.z ?? a[2];
    const bx = b.x ?? b[0], by = b.y ?? b[1], bz = b.z ?? b[2];
    _v.set(bx - ax, by - ay, bz - az);
    const len = _v.length();
    if (len < 1e-4) return null;
    const g = new THREE.BoxGeometry(t, t, len);
    _q.setFromUnitVectors(_z, _v.normalize());
    g.applyMatrix4(_m.compose(_v2.set((ax + bx) / 2, (ay + by) / 2, (az + bz) / 2), _q, _one));
    return this.add(g, color);
  }
  /** Cylinder of radius r from a to b. */
  rod(a, b, r, color = null, segments = 8) {
    _v.set(b[0] - a[0], b[1] - a[1], b[2] - a[2]);
    const len = _v.length();
    if (len < 1e-4) return null;
    const g = new THREE.CylinderGeometry(r, r, len, segments, 1, true);
    _q.setFromUnitVectors(_up, _v.normalize());
    g.applyMatrix4(_m.compose(_v2.set((a[0] + b[0]) / 2, (a[1] + b[1]) / 2, (a[2] + b[2]) / 2), _q, _one));
    return this.add(g, color);
  }
  /** Vertical rectangle (w x h) centred at (x,y,z) whose front faces yaw `ry` (0 = +Z). */
  quad(w, h, x, y, z, ry, uvRect = null, uScale = 1, color = null) {
    const g = new THREE.PlaneGeometry(w, h);
    if (uScale !== 1) { const uv = g.attributes.uv; for (let i = 0; i < uv.count; i++) uv.setX(i, uv.getX(i) * uScale); }
    if (uvRect) remapUV(g, uvRect);
    _e.set(0, ry, 0); _q.setFromEuler(_e);
    g.applyMatrix4(_m.compose(_v.set(x, y, z), _q, _one));
    return this.add(g, color);
  }
  /** Horizontal rectangle at height y spanning [x0,x1] x [z0,z1], facing up (or down). */
  floor(x0, z0, x1, z1, y, down = false) {
    const g = new THREE.PlaneGeometry(Math.abs(x1 - x0), Math.abs(z1 - z0));
    g.rotateX(down ? Math.PI / 2 : -Math.PI / 2);
    g.translate((x0 + x1) / 2, y, (z0 + z1) / 2);
    return this.add(g);
  }
  /** Merges into a static mesh under `parent` (null if empty). */
  build(parent) {
    if (!this.geos.length) return null;
    const merged = mergeGeometries(this.geos, false);
    for (const g of this.geos) g.dispose();
    this.geos.length = 0;
    if (!merged) { console.warn('[arena] merge failed for', this.name); return null; }
    merged.computeBoundingSphere();
    const mesh = new THREE.Mesh(merged, this.material);
    mesh.name = this.name;
    mesh.castShadow = this.cast;
    mesh.receiveShadow = this.receive;
    mesh.matrixAutoUpdate = false;
    mesh.updateMatrix();
    parent.add(mesh);
    return mesh;
  }
}

// ------------------------------------------------------------------------------------------------- Arena

export class Arena {
  constructor() {
    /** Kernel court geometry (main.js sets game.court = arena.court). */
    this.court = new Court();
    /** @type {THREE.Box3[]} static collision volumes (walls, bleacher steps, end walls, ceiling). */
    this.colliders = [];
    /** @type {THREE.Light[]} every light of the rig. */
    this.lights = [];
    this.keyLights = [];
    this.fillLights = [];
    this.hemi = null;
    this.group = null;
    this.crowd = new Crowd();
    this.scoreboard = null;
    this.quality = 'high';
    /** Interior of the padded walls + ceiling height (camera rigs / out-of-bounds helpers). */
    this.bounds = { minX: -ARENA.wallX, maxX: ARENA.wallX, minZ: -ARENA.wallZ, maxZ: ARENA.wallZ, minY: 0, maxY: ARENA.ceilingY };
    this.floorY = 0;
    this._materials = [];
    this._textures = [];
    this._built = false;
  }

  /** Builds the venue into `scene`. Safe to await once. */
  async build(scene) {
    if (this._built) return this;
    this._built = true;
    this.quality = qualityId((game.renderer && game.renderer.quality) || (game.config && game.config.quality));
    const S = qualityPreset(this.quality);
    const group = new THREE.Group();
    group.name = 'Arena';
    this.group = group;
    scene.add(group);

    const mats = this._createMaterials(S);
    const B = {
      pad: new Batch('ArenaPadding', mats.pad),
      block: new Batch('ArenaWalls', mats.block, { uvTile: 4 }),
      concrete: new Batch('ArenaBleachers', mats.concrete, { uvTile: 2 }),
      vinyl: new Batch('ArenaRunOff', mats.vinyl, { uvTile: 2 }),
      dark: new Batch('ArenaDarkTrim', mats.dark),
      metal: new Batch('ArenaSteel', mats.metal, { receive: false }),
      glow: new Batch('ArenaGlow', mats.glow, { receive: false, colors: true }),
      glass: new Batch('ArenaGlass', mats.glass, { receive: false }),
      banner: new Batch('ArenaBanners', mats.banner, { receive: false }),
      ribbon: new Batch('ArenaRibbons', mats.ribbon, { receive: false }),
      screen: new Batch('ArenaScreens', mats.screen, { receive: false }),
    };

    this._buildFloor(group, mats, B);
    this._buildWalls(B);
    const slots = this._buildStands(group, mats, B);
    this._buildRig(group, B);
    this._buildDressing(B);
    this._buildColliders();
    for (const b of Object.values(B)) {
      const mesh = b.build(group);
      if (mesh && b === B.screen) mesh.onBeforeRender = () => this.scoreboard && this.scoreboard.tick(game.time ? game.time.realNow : performance.now() / 1000);
    }
    this.applyQuality(this.quality);

    // Crowd impostors (async bake from the real avatar models; skipped gracefully if unavailable).
    const renderer = game.renderer && game.renderer.three;
    if (renderer && S.crowdCell > 0) {
      try {
        const mesh = await this.crowd.build({ slots, renderer, envTexture: game.renderer.envTexture, cellWidth: S.crowdCell });
        if (mesh) group.add(mesh);
      } catch (e) {
        console.warn('[arena] crowd skipped', e);
      }
    }
    group.updateMatrixWorld(true);
    return this;
  }

  /** Applies shadow / fill-light settings for a quality level (also called by Renderer.setQuality). */
  applyQuality(quality) {
    this.quality = qualityId(quality);
    const S = qualityPreset(this.quality);
    this.keyLights.forEach((l, i) => {
      const cast = i < S.shadowLights;
      if (l.castShadow !== cast || l.shadow.mapSize.x !== S.shadowMapSize) {
        l.castShadow = cast;
        l.shadow.mapSize.set(S.shadowMapSize, S.shadowMapSize);
        if (l.shadow.map) { l.shadow.map.dispose(); l.shadow.map = null; }
      }
      l.shadow.radius = S.shadowRadius;
    });
    for (const l of this.fillLights) l.visible = S.fillLights;
    if (this.hemi) this.hemi.intensity = LIGHTING.hemi.intensity * (S.fillLights ? 1 : LIGHTING.lowQualityHemiBoost);
  }

  /** Scene statistics for dev pages. */
  stats() {
    let meshes = 0, tris = 0;
    this.group && this.group.traverse((o) => {
      if (!o.isMesh) return;
      meshes++;
      const g = o.geometry, c = g.index ? g.index.count : g.attributes.position.count;
      tris += (c / 3) * (o.isInstancedMesh ? o.count : 1);
    });
    return { meshes, triangles: Math.round(tris), crowd: this.crowd.count, colliders: this.colliders.length, lights: this.lights.length, quality: this.quality };
  }

  dispose() {
    this.crowd.dispose();
    this.scoreboard?.dispose();
    if (this.group) {
      this.group.traverse((o) => { if (o.isMesh && o.geometry) o.geometry.dispose(); });
      for (const l of this.lights) l.shadow?.map?.dispose();
      this.group.removeFromParent();
    }
    for (const m of this._materials) m.dispose();
    for (const t of this._textures) t.dispose();
    this._materials.length = 0; this._textures.length = 0;
    this.colliders.length = 0; this.lights.length = 0; this.keyLights.length = 0; this.fillLights.length = 0;
    this.group = null; this.floor = null; this.seats = null; this.hemi = null; this.scoreboard = null;
    this.crowd = new Crowd();
    this._built = false;
  }

  // ================================================================== materials

  _createMaterials(S) {
    const T = (t) => { this._textures.push(t); return t; };
    const M = (m) => { this._materials.push(m); return m; };
    const aniso = Math.min(S.anisotropy, (game.renderer && game.renderer.three.capabilities.getMaxAnisotropy()) || 4);
    const tex = (n) => Math.max(256, Math.round(n * S.texScale));

    // Hardwood + painted overlay
    const wood = createHardwoodTextures({ size: tex(2048), tileMeters: ARENA.hardwoodTile, anisotropy: aniso });
    const repX = (2 * ARENA.hardwoodHalfX) / wood.tileMeters, repZ = (2 * ARENA.hardwoodHalfZ) / wood.tileMeters;
    for (const t of [wood.map, wood.normalMap, wood.roughnessMap]) { t.repeat.set(repX, repZ); T(t); }
    const paint = T(createCourtPaintTexture({
      halfX: ARENA.hardwoodHalfX, halfZ: ARENA.hardwoodHalfZ, court: this.court, anisotropy: aniso,
      ppm: ARENA_LOOK.paintPxPerMeter[this.quality] || 64,
    }));
    const woodParams = {
      name: 'hardwood', map: wood.map, normalMap: wood.normalMap, roughnessMap: wood.roughnessMap,
      roughness: ARENA_LOOK.hardwoodRoughness, metalness: 0,
      normalScale: new THREE.Vector2(ARENA_LOOK.hardwoodNormalScale, ARENA_LOOK.hardwoodNormalScale),
    };
    const hardwood = M(S.clearcoat
      ? new THREE.MeshPhysicalMaterial({ ...woodParams, specularIntensity: ARENA_LOOK.hardwoodSpecular, clearcoat: ARENA_LOOK.hardwoodClearcoat, clearcoatRoughness: ARENA_LOOK.hardwoodClearcoatRoughness })
      : new THREE.MeshStandardMaterial(woodParams));
    const paintUniforms = {
      uPaint: { value: paint },
      uPaintHalf: { value: new THREE.Vector2(ARENA.hardwoodHalfX, ARENA.hardwoodHalfZ) },
      uPaintRough: { value: ARENA_LOOK.paintRoughness },
    };
    hardwood.onBeforeCompile = (shader) => {
      Object.assign(shader.uniforms, paintUniforms);
      shader.vertexShader = shader.vertexShader
        .replace('#include <common>', '#include <common>\nuniform vec2 uPaintHalf;\nvarying vec2 vPaintUv;')
        .replace('#include <begin_vertex>', `#include <begin_vertex>
          { vec4 duW = modelMatrix * vec4(transformed, 1.0);
            vPaintUv = vec2((uPaintHalf.x - duW.x) / (2.0 * uPaintHalf.x), (duW.z + uPaintHalf.y) / (2.0 * uPaintHalf.y)); }`);
      shader.fragmentShader = shader.fragmentShader
        .replace('#include <common>', '#include <common>\nuniform sampler2D uPaint;\nuniform float uPaintRough;\nvarying vec2 vPaintUv;')
        .replace('#include <map_fragment>', `#include <map_fragment>
          vec4 duPaint = texture2D(uPaint, vPaintUv);
          diffuseColor.rgb = mix(diffuseColor.rgb, duPaint.rgb, duPaint.a);`)
        .replace('#include <roughnessmap_fragment>', `#include <roughnessmap_fragment>
          roughnessFactor = mix(roughnessFactor, uPaintRough, duPaint.a * 0.85);`);
    };
    hardwood.customProgramCacheKey = () => 'du-hardwood-paint';

    const vinylT = createVinylTextures({ size: tex(512), anisotropy: aniso });
    const vinyl = M(new THREE.MeshStandardMaterial({ name: 'vinyl', map: T(vinylT.map), normalMap: T(vinylT.normalMap), roughness: 0.62, metalness: 0, normalScale: new THREE.Vector2(0.6, 0.6) }));

    const padT = createPaddingTextures({ pxPerMeter: Math.round(256 * Math.max(0.5, S.texScale)), heightMeters: ARENA.padHeight, anisotropy: aniso });
    const pad = M(new THREE.MeshStandardMaterial({ name: 'padding', map: T(padT.map), normalMap: T(padT.normalMap), roughness: 0.5, metalness: 0, normalScale: new THREE.Vector2(0.8, 0.8) }));
    this._padTile = padT.tileMeters;

    const blockT = createBlockWallTextures({ size: tex(512), anisotropy: Math.min(4, aniso) });
    const block = M(new THREE.MeshStandardMaterial({ name: 'blockWall', map: T(blockT.map), normalMap: T(blockT.normalMap), roughness: 0.9, metalness: 0 }));

    const concT = createConcreteTextures({ size: tex(512), anisotropy: Math.min(4, aniso) });
    const concrete = M(new THREE.MeshStandardMaterial({ name: 'bleacherConcrete', map: T(concT.map), normalMap: T(concT.normalMap), roughness: 0.85, metalness: 0 }));

    const dark = M(new THREE.MeshStandardMaterial({ name: 'darkTrim', color: 0x15171b, roughness: 0.8, metalness: 0.1 }));
    const metal = M(new THREE.MeshStandardMaterial({ name: 'steel', color: 0x6f757e, roughness: 0.42, metalness: 0.85 }));
    const glow = M(new THREE.MeshBasicMaterial({ name: 'glow', vertexColors: true }));
    const glass = M(new THREE.MeshStandardMaterial({ name: 'suiteGlass', color: 0x0c0f14, roughness: 0.08, metalness: 0.7, envMapIntensity: 1.6 }));
    const seat = M(new THREE.MeshStandardMaterial({ name: 'seat', color: 0xffffff, roughness: 0.45, metalness: 0 }));

    const atlas = createBannerAtlas({ heroes: HERO_IDS.slice(), anisotropy: aniso });
    this._bannerRects = atlas.rects;
    T(atlas.texture);
    const banner = M(new THREE.MeshStandardMaterial({ name: 'banner', map: atlas.texture, roughness: 0.82, metalness: 0, side: THREE.DoubleSide }));
    const ribbon = M(new THREE.MeshBasicMaterial({ name: 'ledRibbon', map: atlas.texture }));
    ribbon.color.setScalar(ARENA_LOOK.ribbonGain);

    this.scoreboard = new Scoreboard();
    const screen = M(new THREE.MeshBasicMaterial({ name: 'videoBoard', map: this.scoreboard.texture }));
    screen.color.setScalar(ARENA_LOOK.screenGain);

    return { hardwood, vinyl, pad, block, concrete, dark, metal, glow, glass, seat, banner, ribbon, screen };
  }

  // ================================================================== floor

  _buildFloor(group, mats, B) {
    const hx = ARENA.hardwoodHalfX, hz = ARENA.hardwoodHalfZ, wx = ARENA.wallX, wz = ARENA.wallZ;
    const geo = new THREE.PlaneGeometry(2 * hx, 2 * hz);
    geo.rotateX(-Math.PI / 2);
    const floor = new THREE.Mesh(geo, mats.hardwood);
    floor.name = 'Hardwood';
    floor.receiveShadow = true;
    floor.matrixAutoUpdate = false;
    floor.updateMatrix();
    group.add(floor);
    this.floor = floor;
    // Vinyl run-off frame around the hardwood (no overlap -> no z-fighting).
    B.vinyl.floor(-wx, hz, wx, wz, 0);
    B.vinyl.floor(-wx, -wz, wx, -hz, 0);
    B.vinyl.floor(hx, -hz, wx, hz, 0);
    B.vinyl.floor(-wx, -hz, -hx, hz, 0);
  }

  // ================================================================== walls, ceiling

  _buildWalls(B) {
    const A = ARENA, wx = A.wallX, wz = A.wallZ, ph = A.padHeight, pt = A.padThickness, t = A.wallThickness;
    const backX = this._backX(), top = A.ceilingY, tile = this._padTile || 6;
    // Padded walls (front faces), text readable from the court.
    B.pad.quad(2 * wz, ph, wx, ph / 2, 0, -Math.PI / 2, null, (2 * wz) / tile);
    B.pad.quad(2 * wz, ph, -wx, ph / 2, 0, Math.PI / 2, null, (2 * wz) / tile);
    B.pad.quad(2 * wx, ph, 0, ph / 2, wz, Math.PI, null, (2 * wx) / tile);
    B.pad.quad(2 * wx, ph, 0, ph / 2, -wz, 0, null, (2 * wx) / tile);
    // Pad top caps.
    B.dark.span(wx, ph, -wz - pt, wx + pt, ph + 0.03, wz + pt);
    B.dark.span(-wx - pt, ph, -wz - pt, -wx, ph + 0.03, wz + pt);
    B.dark.span(-wx, ph, wz, wx, ph + 0.03, wz + pt);
    B.dark.span(-wx, ph, -wz - pt, wx, ph + 0.03, -wz);
    // End walls (full height, behind the end pads) and long upper walls behind the top row.
    const ez = wz + pt;
    B.block.span(-backX - t, 0, ez, backX + t, top, ez + t);
    B.block.span(-backX - t, 0, -ez - t, backX + t, top, -ez);
    B.block.span(backX, 0, -ez, backX + t, top, ez);
    B.block.span(-backX - t, 0, -ez, -backX, top, ez);
    // Ceiling + roof beams.
    B.dark.floor(-backX, -ez, backX, ez, top, true);
    for (let z = -ez + 2; z < ez; z += 4) B.dark.span(-backX, top - 1.1, z - 0.12, backX, top - 0.05, z + 0.12); // black-painted roof steel
    // Baseboards along the end walls between pads and walls (dark kick strip).
    B.dark.span(-wx, 0, wz, wx, 0.12, wz + 0.02);
    B.dark.span(-wx, 0, -wz - 0.02, wx, 0.12, -wz);
  }

  _backX() { return ARENA.wallX + ARENA.padThickness + ARENA.rows * ARENA.rowDepth; }
  _rowX(i) { return ARENA.wallX + ARENA.padThickness + i * ARENA.rowDepth; }
  _rowY(i) { return ARENA.padHeight + i * ARENA.rowRise; }
  _inAisle(z) { for (const a of ARENA.aisleZ) if (Math.abs(z - a) < ARENA.aisleWidth / 2) return true; return false; }

  // ================================================================== stands

  /** Bleachers, seats, rails, ribbon boards, suites. Returns crowd slots. */
  _buildStands(group, mats, B) {
    const A = ARENA, backX = this._backX(), ez = A.wallZ + A.padThickness;
    const rng = new Rng(1337);
    const seatSpots = [];
    const slots = [];
    for (const s of [-1, 1]) {
      const yawToCourt = s > 0 ? -Math.PI / 2 : Math.PI / 2;
      for (let i = 0; i < A.rows; i++) {
        const x0 = this._rowX(i), y = this._rowY(i);
        const yb = i === 0 ? 0 : y - A.rowRise - 0.3;
        // Stepped slab: front face = riser, top = tread (hidden parts are overlapped by the row in front).
        B.concrete.span(s * x0, yb, -ez, s * backX, y, ez);
        // Aisle half-steps in the back half of the previous tread.
        if (i > 0) for (const az of A.aisleZ) {
          B.concrete.span(s * (x0 - A.rowDepth / 2), this._rowY(i - 1), az - A.aisleWidth / 2, s * x0, this._rowY(i - 1) + A.rowRise / 2, az + A.aisleWidth / 2);
        }
        // Seats + crowd slots.
        for (let z = -ez + 0.55; z <= ez - 0.55; z += A.seatPitch) {
          if (this._inAisle(z)) continue;
          seatSpots.push({ x: s * (x0 + 0.5), y: y + 0.42, z, s });
          const centre = 1 - Math.min(1, Math.abs(z) / ez);
          const occ = A.crowdOccupancy * (0.7 + 0.45 * centre) * (i < 2 ? 1.1 : 1);
          if (rng.chance(Math.min(0.95, occ))) {
            slots.push({ x: s * (x0 + 0.2 + rng.range(-0.04, 0.06)), y, z: z + rng.range(-0.06, 0.06), yaw: yawToCourt, front: 1 - i / (A.rows - 1) });
          }
        }
      }
      // Front rail above the padded wall, top rail at the back, aisle handrails.
      const railX = s * (A.wallX + 0.07), railY = A.padHeight + 0.95;
      B.metal.rod([railX, railY, -ez], [railX, railY, ez], 0.03, null, 8);
      for (let z = -ez + 1; z < ez; z += 2) B.metal.rod([railX, A.padHeight, z], [railX, railY, z], 0.022, null, 6);
      const topY = this._rowY(A.rows - 1), bx = s * (backX - 0.15);
      B.metal.rod([bx, topY + 1.0, -ez], [bx, topY + 1.0, ez], 0.03, null, 8);
      for (const az of A.aisleZ) {
        const xa = s * (this._rowX(1) + 0.1), xb = s * (this._rowX(A.rows - 1) + 0.1);
        B.metal.rod([xa, this._rowY(1) + 0.9, az], [xb, this._rowY(A.rows - 1) + 0.9, az], 0.025, null, 8);
        for (let i = 1; i < A.rows; i += 3) {
          const px = s * (this._rowX(i) + 0.1);
          B.metal.rod([px, this._rowY(i), az], [px, this._rowY(i) + 0.9, az], 0.02, null, 6);
        }
      }
      // LED ribbon board above the top row (segments sized to keep the text aspect).
      const rr = this._bannerRects.ribbon, rh = A.ribbon.y1 - A.ribbon.y0;
      const segLen = rh * ((rr.u1 - rr.u0) * 2048) / ((rr.v1 - rr.v0) * 1024); // keep the atlas aspect
      const total = 2 * ez, nSeg = Math.max(1, Math.round(total / segLen)), L = total / nSeg;
      for (let k = 0; k < nSeg; k++) {
        const zc = -ez + L * (k + 0.5);
        B.ribbon.quad(L, rh, s * (backX - 0.03), (A.ribbon.y0 + A.ribbon.y1) / 2, zc, yawToCourt, rr);
      }
      B.dark.span(s * (backX - 0.08), A.ribbon.y0 - 0.08, -ez, s * backX, A.ribbon.y1 + 0.08, ez);
      // Suites band: tinted glass (reflects the environment) with warm interior downlights and dark mullions.
      const sy = (A.suites.y0 + A.suites.y1) / 2, sh = A.suites.y1 - A.suites.y0;
      B.glass.quad(2 * ez - 2, sh, s * (backX - 0.02), sy, 0, yawToCourt);
      for (let z = -ez + 1; z <= ez - 1 + 1e-6; z += 3) {
        B.dark.span(s * (backX - 0.12), A.suites.y0 - 0.1, z - 0.08, s * (backX - 0.01), A.suites.y1 + 0.1, z + 0.08);
        if (z + 1.5 < ez - 1) {
          for (const dz of [0.9, 2.1]) B.glow.quad(0.5, 0.08, s * (backX - 0.035), A.suites.y1 - 0.18, z + dz, yawToCourt, null, 1, ARENA_LOOK.suiteGlow);
          B.glow.quad(2.6, sh * 0.35, s * (backX - 0.03), A.suites.y0 + sh * 0.18, z + 1.5, yawToCourt, null, 1, ARENA_LOOK.suiteInterior);
        }
      }
      B.dark.span(s * (backX - 0.4), A.suites.y0 - 0.25, -ez, s * backX, A.suites.y0 - 0.1, ez);
    }
    this._buildSeats(group, mats.seat, seatSpots, rng);
    return slots;
  }

  /** One InstancedMesh for every moulded seat. */
  _buildSeats(group, material, spots, rng) {
    const parts = [
      new THREE.BoxGeometry(0.42, 0.05, 0.45).translate(0, 0, 0),                         // pan
      new THREE.BoxGeometry(0.05, 0.4, 0.45).rotateZ(-0.14).translate(0.22, 0.22, 0),      // backrest
      new THREE.BoxGeometry(0.06, 0.4, 0.06).translate(0.05, -0.22, 0),                   // stem
    ];
    const geo = mergeGeometries(parts, false);
    for (const p of parts) p.dispose();
    const mesh = new THREE.InstancedMesh(geo, material, spots.length);
    mesh.name = 'ArenaSeats';
    mesh.receiveShadow = true;
    mesh.castShadow = false;
    const base = new THREE.Color(ARENA_LOOK.seatColor), c = new THREE.Color();
    spots.forEach((sp, i) => {
      _e.set(0, sp.s > 0 ? 0 : Math.PI, 0); _q.setFromEuler(_e);
      mesh.setMatrixAt(i, _m.compose(_v.set(sp.x, sp.y, sp.z), _q, _one));
      c.copy(base).multiplyScalar(rng.range(0.9, 1.1));
      mesh.setColorAt(i, c);
    });
    mesh.instanceMatrix.needsUpdate = true;
    if (mesh.instanceColor) mesh.instanceColor.needsUpdate = true;
    mesh.computeBoundingSphere();
    mesh.matrixAutoUpdate = false;
    mesh.updateMatrix();
    group.add(mesh);
    this.seats = mesh;
  }

  // ================================================================== truss + lights

  _buildRig(group, B) {
    const A = ARENA, L = LIGHTING, y = A.trussY, h = A.trussSize / 2, tx = A.trussHalfX, tz = A.trussHalfZ;
    // Box trusses: two long runs over the sidelines + three cross runs.
    this._truss(B.metal, [-tx, y, -tz], [-tx, y, tz]);
    this._truss(B.metal, [tx, y, -tz], [tx, y, tz]);
    for (const z of [-tz, 0, tz]) this._truss(B.metal, [-tx, y, z], [tx, y, z]);
    // Suspension cables.
    for (const x of [-tx, tx]) for (const z of [-tz, 0, tz]) B.metal.rod([x, y + h, z], [x, A.ceilingY - 1.1, z], 0.012, null, 4);

    // Floodlight heads under the long trusses, aimed into the court; the four key spots sit in four of them.
    const lensC = L.lensGlow;
    const head = new THREE.Object3D();
    const addHead = (x, z, tgt) => {
      head.position.set(x, y - h - 0.32, z);
      head.lookAt(tgt[0], tgt[1], tgt[2]);
      head.updateMatrix();
      const hous = new THREE.BoxGeometry(0.5, 0.42, 0.34);
      hous.applyMatrix4(head.matrix);
      B.dark.add(hous);
      const lens = new THREE.PlaneGeometry(0.44, 0.36);
      lens.translate(0, 0, 0.175);
      lens.applyMatrix4(head.matrix);
      B.glow.add(lens, lensC);
      B.metal.rod([x, y - h, z], [x, y - h - 0.15, z], 0.03, null, 6);
    };
    for (const sx of [-1, 1]) {
      for (let z = -tz + 1; z <= tz - 1 + 1e-6; z += A.lampSpacing) {
        addHead(sx * tx, z, [sx * 1.2, 0, z * 0.8]);
      }
    }

    // Key spots (shadow casters by quality), fills, hemisphere ambient.
    for (const k of L.keys) {
      const sp = new THREE.SpotLight(L.keyColor, L.keyIntensity, L.keyDistance, L.keyAngle, L.keyPenumbra, L.keyDecay);
      sp.name = `KeyLight${this.keyLights.length}`;
      sp.position.set(k.pos[0], k.pos[1], k.pos[2]);
      sp.target.position.set(k.target[0], k.target[1], k.target[2]);
      sp.shadow.camera.near = L.shadow.near;
      sp.shadow.camera.far = L.shadow.far;
      sp.shadow.bias = L.shadow.bias;
      sp.shadow.normalBias = L.shadow.normalBias;
      group.add(sp, sp.target);
      this.keyLights.push(sp);
      this.lights.push(sp);
    }
    for (const f of L.fills) {
      const sp = new THREE.SpotLight(L.fillColor, f.intensity, 60, f.angle, 1, 2);
      sp.name = `FillLight${this.fillLights.length}`;
      sp.position.set(f.pos[0], f.pos[1], f.pos[2]);
      sp.target.position.set(f.target[0], f.target[1], f.target[2]);
      sp.castShadow = false;
      group.add(sp, sp.target);
      this.fillLights.push(sp);
      this.lights.push(sp);
    }
    this.hemi = new THREE.HemisphereLight(L.hemi.sky, L.hemi.ground, L.hemi.intensity);
    this.hemi.name = 'ArenaAmbient';
    group.add(this.hemi);
    this.lights.push(this.hemi);
  }

  /** Square box truss (4 chords + zig-zag lacing on every face) from a to b (horizontal runs). */
  _truss(batch, a, b) {
    const A = ARENA, h = A.trussSize / 2, chord = 0.05, lace = 0.025;
    // Plain numbers here: batch.beam() reuses the module temporaries.
    const len = Math.hypot(b[0] - a[0], b[2] - a[2]);
    const dx = (b[0] - a[0]) / len, dz = (b[2] - a[2]) / len;
    const sx = -dz, sz = dx; // horizontal perpendicular
    const corner = (t, sy, ss) => [a[0] + dx * t + sx * ss * h, a[1] + sy * h, a[2] + dz * t + sz * ss * h];
    const cs = [[1, 1], [1, -1], [-1, -1], [-1, 1]];
    for (const [sy, ss] of cs) batch.beam(corner(0, sy, ss), corner(len, sy, ss), chord);
    const bays = Math.max(1, Math.round(len / A.trussBay)), step = len / bays;
    for (let k = 0; k < bays; k++) {
      const t0 = k * step, t1 = t0 + step, flip = k % 2 === 0;
      for (let f = 0; f < 4; f++) {
        const [ya, sa] = cs[f], [yb, sb] = cs[(f + 1) % 4];
        batch.beam(corner(flip ? t0 : t1, ya, sa), corner(flip ? t1 : t0, yb, sb), lace);
      }
    }
  }

  // ================================================================== dressing

  _buildDressing(B) {
    const A = ARENA, R = this._bannerRects, backX = this._backX(), ez = A.wallZ + A.padThickness;
    const sb = A.scoreboard;
    for (const s of [-1, 1]) {
      const face = s > 0 ? Math.PI : 0;       // inward-facing yaw on the +Z / -Z end walls
      const zf = s * ez;
      // Video board: housing, screen, mounting frame.
      B.dark.box(sb.width + 0.5, sb.height + 0.5, 0.6, 0, sb.y, zf - s * 0.3, 0);
      B.screen.quad(sb.width, sb.height, 0, sb.y, zf - s * 0.605, face);
      B.metal.span(-sb.width / 2, sb.y + sb.height / 2 + 0.25, zf - s * 0.1, sb.width / 2, sb.y + sb.height / 2 + 0.4, zf - s * 0.02);
      // LED ribbon across the end wall (between the wall graphic and the video board).
      {
        const rr = R.ribbon, rh = 0.6, segLen = rh * ((rr.u1 - rr.u0) * 2048) / ((rr.v1 - rr.v0) * 1024);
        const total = 2 * backX, nSeg = Math.max(1, Math.round(total / segLen)), L = total / nSeg;
        for (let k = 0; k < nSeg; k++) B.ribbon.quad(L, rh, s * (-backX + L * (k + 0.5)), 6.1, zf - s * 0.03, face, rr);
        B.dark.span(-backX, 6.1 - rh / 2 - 0.06, zf - s * 0.06, backX, 6.1 + rh / 2 + 0.06, zf);
      }
      // End-wall graphic below the board and the team banners flanking it (Home hangs at the -Z end it defends).
      const gw = 10, gh = gw * ((R.wall.v1 - R.wall.v0) * 1024) / ((R.wall.u1 - R.wall.u0) * 2048);
      B.ribbon.quad(gw, gh, 0, 4.4, zf - s * 0.02, face, R.wall);
      const team = s < 0 ? R.home : R.away;
      const bw = 1.7, bh = bw * ((team.v1 - team.v0) * 1024) / ((team.u1 - team.u0) * 2048);
      for (const x of [-6.4, 6.4]) B.banner.quad(bw, bh, x, sb.y + 0.3, zf - s * 0.06, face, team);
      // EXIT signs near the corners.
      for (const x of [-7.6, 7.6]) {
        B.dark.box(0.46, 0.22, 0.06, x, 3.0, zf - s * 0.03, 0);
        B.glow.quad(0.4, 0.16, x, 3.0, zf - s * 0.065, face, null, 1, ARENA_LOOK.exitGlow);
      }
    }
    // Hall-of-fame hero banners hanging along the long upper walls.
    const heroes = R.heroes, perSide = Math.ceil(heroes.length / 2);
    heroes.forEach((r, i) => {
      const s = i < perSide ? 1 : -1, k = i % perSide;
      const z = (k - (perSide - 1) / 2) * 5.6;
      const bw = 1.5, bh = bw * ((r.v1 - r.v0) * 1024) / ((r.u1 - r.u0) * 2048);
      const x = s * (backX - 0.3);
      B.banner.quad(bw, bh, x, 13.4, z, s > 0 ? -Math.PI / 2 : Math.PI / 2, r);
      B.metal.rod([x, 13.4 + bh / 2 + 0.05, z - bw / 2 - 0.05], [x, 13.4 + bh / 2 + 0.05, z + bw / 2 + 0.05], 0.02, null, 6);
    });
  }

  // ================================================================== colliders

  _buildColliders() {
    const A = ARENA, C = this.colliders, backX = this._backX(), t = A.wallThickness;
    const ez = A.wallZ + A.padThickness, top = A.ceilingY;
    const add = (x0, y0, z0, x1, y1, z1) => C.push(new THREE.Box3(
      new THREE.Vector3(Math.min(x0, x1), Math.min(y0, y1), Math.min(z0, z1)),
      new THREE.Vector3(Math.max(x0, x1), Math.max(y0, y1), Math.max(z0, z1))));
    for (const s of [-1, 1]) {
      // Padded wall + first row (one volume from the pad face), then each higher step.
      add(s * A.wallX, 0, -ez, s * (backX + t), A.padHeight, ez);
      for (let i = 1; i < A.rows; i++) add(s * this._rowX(i), 0, -ez, s * (backX + t), this._rowY(i), ez);
      // Upper long wall.
      add(s * backX, 0, -ez - t, s * (backX + t), top, ez + t);
      // End wall (pad face at wallZ).
      add(-backX - t, 0, s * A.wallZ, backX + t, top, s * (ez + t));
    }
    // Ceiling.
    add(-backX - t, top, -ez - t, backX + t, top + 0.5, ez + t);
  }
}
