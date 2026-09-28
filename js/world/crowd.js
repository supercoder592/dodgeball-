// ---------------------------------------------------------------------------------------------------------------
// Arena crowd (owner: render). Distant spectators are IMPOSTORS of the real Rocketbox motion-capture humans - never
// procedural bodies: the hero models are posed with their mocap clips (idle / breathe / cheer / look around / wave)
// and rendered by an orthographic camera into an sRGB, mip-mapped atlas. That bake runs OFFLINE
// (Tools/web/bake_crowd.mjs -> docs/assets/crowd/atlas.json + atlas-<cell>.webp, one image per quality cell width),
// so the game only downloads one small WebP. build() never waits for it: it returns a hidden InstancedMesh at once
// and the cards appear when the atlas is decoded. Without a matching pre-baked atlas (missing file, or CROWD changed
// since the bake - see crowdBakeKey) the same bake runs at runtime as a fallback, after an idle callback.
// The stands are then filled with one InstancedMesh of alpha-tested cards (one draw call) that
//   - pick an atlas cell (model x pose), optionally mirrored, with per-instance colour variation / team tint,
//   - turn partially toward the camera (cylindrical billboard, clamped) so cards never read as paper-thin,
//   - breathe, sway and - when the match gets exciting (eliminations, perfect catches, round ends) - jump.
// Motion is evaluated in the vertex shader from real time (the crowd keeps moving through hitstop).
// If assets or the renderer are unavailable the crowd is simply skipped.
// ---------------------------------------------------------------------------------------------------------------
import * as THREE from 'three';
import * as SkeletonUtils from 'three/addons/utils/SkeletonUtils.js';
import { game } from '../game.js';
import { EV } from '../core/events.js';
import { Rng } from '../core/rng.js';
import { TEAM, TEAM_COLORS } from '../core/constants.js';
import { SLOT } from '../abilities/abilityBase.js';
import { heroById } from '../abilities/roster.js';

export const CROWD = Object.freeze({
  /** Casual-looking avatars used as spectators (helmets etc. hidden via roster.hiddenParts). */
  heroes: ['Rayne', 'Specter', 'Houdini', 'Gale', 'Elsa', 'Shadow', 'Screws', 'Chrono'],
  /** Mocap clip + normalised sample time for each baked pose. */
  poses: [
    { clip: 'idle', at: 0.2 }, { clip: 'breathe', at: 0.55 }, { clip: 'cheer', at: 0.45 },
    { clip: 'lookaround', at: 0.35 }, { clip: 'wave', at: 0.4 },
  ],
  atlasColumns: 8,
  frameWidth: 1.2,          // metres captured by a cell (height = 2x)
  frameBottom: -0.08,       // metres below the feet included in the cell
  yawJitter: 0.5,           // random baked body turn (rad)
  brightness: [0.45, 0.76], // instance colour multiplier (stands are lit less than the court)
  teamTintChance: 0.4,      // fans wearing their team's colour
  teamTint: 0.5,
  faceCamera: 1.0,          // 0 = cards face the court, 1 = full cylindrical billboard
  maxTurn: 1.65,            // clamp of the camera turn (rad): cameras anywhere on the court side get a full front view
  bobAmp: 0.012,            // idle breathing bob (m)
  swayAmp: 0.025,           // idle weight shift at head height (m)
  jumpAmp: 0.18,            // jump height at full excitement (m)
  idleExcite: 0.06,         // a few fans always bounce
  nearHide: 9.0,            // cards closer than this (3D) to the camera collapse (no flat cards filling broadcast rail shots)
  exciteDecay: 0.45,        // excitement lost per real second
  loadTimeout: 20000,       // ms before giving up on the runtime (fallback) bake
  atlasFile: 'crowd/atlas.json', // pre-baked atlas description, relative to game.assets.base
});

/**
 * Identifies everything in CROWD that changes the baked atlas. Tools/web/bake_crowd.mjs stores it in atlas.json and
 * build() ignores a pre-baked atlas whose key differs (it bakes at runtime instead). Lighting, materials and
 * roster.hiddenParts are not part of the key: re-run the bake tool after changing them.
 * @returns {string}
 */
export function crowdBakeKey() {
  return JSON.stringify({ h: CROWD.heroes, p: CROWD.poses, c: CROWD.atlasColumns, w: CROWD.frameWidth, b: CROWD.frameBottom, y: CROWD.yawJitter });
}

/**
 * @typedef {Object} CrowdCell   one baked impostor (atlas cell)
 * @property {number} u @property {number} v  lower-left corner in texture space (GL convention, v up)
 * @property {'m'|'f'} gender @property {string} pose  clip key of the baked pose
 */

/** Team-tint colours (linear) for the two ends of the arena. */
const _home = new THREE.Color(TEAM_COLORS[TEAM.HOME]);
const _away = new THREE.Color(TEAM_COLORS[TEAM.AWAY]);
const _tmpColor = new THREE.Color();
const _white = new THREE.Color(1, 1, 1);
const _m4 = new THREE.Matrix4();
const _pos = new THREE.Vector3();
const _quat = new THREE.Quaternion();
const _scale = new THREE.Vector3();
const _v1 = new THREE.Vector3();
const _v2 = new THREE.Vector3();

/**
 * @typedef {Object} CrowdSlot   a standing spot in the stands
 * @property {number} x @property {number} y @property {number} z  feet position (world)
 * @property {number} yaw  facing (yaw 0 = +Z) toward the court
 * @property {number} front 0..1, 1 = front rows (slightly more energetic)
 */

export class Crowd {
  constructor() {
    this.mesh = null;
    this.atlas = null;          // impostor atlas owner (pre-baked THREE.Texture, or the fallback WebGLRenderTarget)
    this.count = 0;             // cards in the stands (0 until the atlas is ready)
    /** Settles when the cards are visible (true) or the crowd was skipped (false). Never rejects. */
    this.ready = Promise.resolve(false);
    this._token = null;
    this.excitement = 0;
    this._lastT = null;
    this._unsubs = [];
    this.uniforms = {
      uTime: { value: 0 },
      uExcite: { value: 0 },
      uCellSize: { value: new THREE.Vector2(1, 1) },
      uFaceCam: { value: CROWD.faceCamera },
      uMaxTurn: { value: CROWD.maxTurn },
      uBob: { value: CROWD.bobAmp },
      uSway: { value: CROWD.swayAmp },
      uJump: { value: CROWD.jumpAmp },
      uNearHide: { value: CROWD.nearHide },
    };
  }

  /**
   * Creates the instanced crowd. Resolves immediately with the (still hidden) mesh; the atlas loads - or, as a
   * fallback, is baked - in the background and the cards appear once it is ready (see `ready`).
   * @param {{ slots:CrowdSlot[], renderer:THREE.WebGLRenderer, envTexture?:THREE.Texture, cellWidth?:number, seed?:number }} o
   * @returns {Promise<THREE.InstancedMesh|null>}
   */
  async build({ slots, renderer, envTexture = null, cellWidth = 128, seed = 77 }) {
    if (!slots || !slots.length || !renderer) return null;
    this._createMesh(slots);
    this._subscribe();
    const token = { cancelled: false };
    this._token = token;
    this.ready = this._populate(slots, renderer, envTexture, cellWidth, seed, token).catch((e) => {
      console.warn('[crowd] skipped', e);
      return false;
    });
    return this.mesh;
  }

  /**
   * Bakes the impostor atlas now (used by the runtime fallback and by Tools/web/bake_crowd.mjs).
   * @param {THREE.WebGLRenderer} renderer
   * @param {THREE.Texture|null} envTexture
   * @param {number} cellWidth  cell width in px (height = 2x)
   * @returns {Promise<{ rt:THREE.WebGLRenderTarget, cells:CrowdCell[], cellU:number, cellV:number, width:number, height:number }|null>}
   */
  bakeAtlas(renderer, envTexture, cellWidth) { return this._bake(renderer, envTexture, cellWidth); }

  /** Raises the crowd's excitement (0..1); it decays in real time. */
  excite(amount) { this.excitement = Math.min(1, Math.max(this.excitement, amount)); }

  dispose() {
    if (this._token) this._token.cancelled = true;
    this._token = null;
    for (const off of this._unsubs) off();
    this._unsubs.length = 0;
    if (this.mesh) {
      this.mesh.removeFromParent();
      this.mesh.geometry.dispose();
      this.mesh.material.dispose();
      this.mesh.dispose?.();
      this.mesh = null;
    }
    this.atlas?.dispose();
    this.atlas = null;
    this.count = 0;
  }

  // ================================================================== atlas: pre-baked or runtime fallback

  /** Loads the pre-baked atlas, else bakes one (after an idle callback), then shows the cards. */
  async _populate(slots, renderer, envTexture, cellWidth, seed, token) {
    let atlas = await this._loadAtlas(cellWidth);
    if (!atlas && !token.cancelled) {
      await idle();
      if (token.cancelled) return false;
      let timer = null;
      const timeout = new Promise((resolve) => {
        timer = setTimeout(() => { token.cancelled = true; console.warn('[crowd] bake timed out - stands stay empty'); resolve(null); }, CROWD.loadTimeout);
      });
      const baked = await Promise.race([this._bake(renderer, envTexture, cellWidth, token), timeout]);
      clearTimeout(timer);
      if (baked) atlas = { owner: baked.rt, texture: baked.rt.texture, cells: baked.cells, cellU: baked.cellU, cellV: baked.cellV };
    }
    if (!atlas) return false;
    if (token.cancelled || !this.mesh) {
      // Only the render target is ours alone; a pre-baked texture stays in the shared asset cache.
      if (atlas.owner !== atlas.texture) atlas.owner.dispose();
      return false;
    }
    this.atlas = atlas.owner;
    this._fillMesh(slots, atlas, seed);
    return true;
  }

  /**
   * Pre-baked atlas for the tier closest to `cellWidth` (the smallest tier >= cellWidth, else the largest).
   * @returns {Promise<{ owner:THREE.Texture, texture:THREE.Texture, cells:CrowdCell[], cellU:number, cellV:number }|null>}
   */
  async _loadAtlas(cellWidth) {
    const assets = game.assets;
    if (!assets || typeof fetch === 'undefined') return null;
    let desc = null;
    try {
      const res = await fetch(assets.base + CROWD.atlasFile);
      if (res.ok) desc = await res.json();
    } catch (e) { /* offline or missing: runtime bake */ }
    if (!desc || !desc.tiers) { console.info('[crowd] no pre-baked atlas - baking at runtime (run Tools/web/bake_crowd.mjs)'); return null; }
    if (desc.key !== crowdBakeKey()) { console.info('[crowd] pre-baked atlas is stale (CROWD changed) - baking at runtime'); return null; }
    const widths = Object.keys(desc.tiers).map(Number).filter((w) => w > 0).sort((a, b) => a - b);
    if (!widths.length) return null;
    const w = widths.find((x) => x >= cellWidth) || widths[widths.length - 1];
    const tier = desc.tiers[w];
    if (!tier || !tier.file || !Array.isArray(desc.cells) || !desc.cells.length || !(desc.cellU > 0) || !(desc.cellV > 0)) return null;
    const dir = CROWD.atlasFile.slice(0, CROWD.atlasFile.lastIndexOf('/') + 1);
    const texture = await assets.textureAsync(dir + tier.file, true);
    if (!texture || !texture.image) return null;
    // Same sampling as the render-target atlas: trilinear, no anisotropy (the image is stored bottom-up, GL rows).
    texture.anisotropy = 1;
    texture.name = 'crowd.atlas';
    return { owner: texture, texture, cells: desc.cells, cellU: desc.cellU, cellV: desc.cellV };
  }

  // ================================================================== baking

  async _bake(renderer, envTexture, cellW, token = { cancelled: false }) {
    const assets = game.assets;
    const manifest = assets && assets.manifest;
    if (!manifest || !manifest.heroes) return null;
    const heroes = CROWD.heroes.filter((id) => manifest.heroes[id]);
    if (!heroes.length) return null;

    // ---- load models, colour maps and clips in parallel (all cached in game.assets and shared with the avatars)
    const models = (await Promise.all(heroes.map(async (id) => {
      const def = manifest.heroes[id];
      try {
        const gltf = await assets.gltf(def.folder + def.model);
        const maps = {};
        await Promise.all(Object.entries(def.materials || {}).map(async ([name, spec]) => {
          if (!spec.map) return;
          const t = await assets.textureAsync(def.folder + spec.map, true);
          if (t && t.image) maps[name] = t; // a missing map only darkens that part
        }));
        return { id, def, gltf, maps, g: def.gender === 'female' ? 'f' : 'm' };
      } catch (e) {
        console.warn('[crowd] model unavailable', id, e && e.message);
        return null;
      }
    }))).filter(Boolean);
    if (!models.length) return null;

    const clips = { m: {}, f: {} };
    await Promise.all(['m', 'f'].flatMap((g) => CROWD.poses.map(async (p) => {
      if (clips[g][p.clip] !== undefined) return;
      clips[g][p.clip] = null;
      try {
        const c = await assets.clip(g, p.clip);
        if (c) { stripRootMotion(c); clips[g][p.clip] = c; }
      } catch (e) { /* pose skipped */ }
    })));

    // ---- bake scene: arena-like top light + soft fill, orthographic front camera
    const scene = new THREE.Scene();
    scene.environment = envTexture;
    scene.environmentIntensity = 0.45;
    const key = new THREE.DirectionalLight(0xfff1e0, 2.3);
    key.position.set(0.6, 4, 2.4);
    const rim = new THREE.DirectionalLight(0xd4e2ff, 0.9);
    rim.position.set(-1.5, 2.5, -2.5);
    scene.add(key, rim, new THREE.HemisphereLight(0xe2e8ff, 0x3a2e24, 0.8));
    const fw = CROWD.frameWidth, fh = fw * 2, fb = CROWD.frameBottom;
    const cam = new THREE.OrthographicCamera(-fw / 2, fw / 2, fh / 2, -fh / 2, 0.1, 20);
    cam.position.set(0, fb + fh / 2, 6);
    cam.lookAt(0, fb + fh / 2, 0);

    const cells = [];
    for (let m = 0; m < models.length; m++) {
      for (const p of CROWD.poses) if (clips[models[m].g][p.clip]) cells.push({ m, pose: p });
    }
    if (!cells.length || token.cancelled) return null;
    const cols = Math.min(CROWD.atlasColumns, cells.length), rows = Math.ceil(cells.length / cols);
    const cellH = cellW * 2, W = cols * cellW, H = rows * cellH;
    const rt = new THREE.WebGLRenderTarget(W, H, {
      colorSpace: THREE.SRGBColorSpace, generateMipmaps: true,
      minFilter: THREE.LinearMipmapLinearFilter, magFilter: THREE.LinearFilter, depthBuffer: true,
    });
    rt.texture.name = 'crowd.atlas';

    // ---- renderer state save
    const prevTarget = renderer.getRenderTarget();
    const prevClear = renderer.getClearColor(new THREE.Color());
    const prevAlpha = renderer.getClearAlpha();
    const prevAutoClear = renderer.autoClear;
    const prevShadow = renderer.shadowMap.enabled;
    renderer.setClearColor(0x000000, 0);
    renderer.autoClear = true;
    renderer.shadowMap.enabled = false;

    const rng = new Rng(4242);
    const cellInfo = [];
    const created = [];
    try {
      let idx = 0;
      for (let m = 0; m < models.length; m++) {
        const info = models[m];
        const model = SkeletonUtils.clone(info.gltf.scene);
        created.push(...applyBakeMaterials(model, info));
        scene.add(model);
        const mixer = new THREE.AnimationMixer(model);
        const lThigh = model.getObjectByName('Bip01_L_Thigh'), rThigh = model.getObjectByName('Bip01_R_Thigh');
        const pelvis = model.getObjectByName('Bip01_Pelvis') || model.getObjectByName('Bip01');
        for (const p of CROWD.poses) {
          const clip = clips[info.g][p.clip];
          if (!clip) continue;
          mixer.stopAllAction();
          const action = mixer.clipAction(clip);
          action.reset().play();
          mixer.setTime(clip.duration * p.at);
          // Face the camera (+Z): the thighs' left-right axis must map to +X (character's left = +X at yaw 0).
          model.position.set(0, 0, 0);
          model.rotation.set(0, 0, 0);
          model.updateMatrixWorld(true);
          let yaw = 0;
          if (lThigh && rThigh) {
            lThigh.getWorldPosition(_v1); rThigh.getWorldPosition(_v2);
            yaw = Math.atan2(_v1.z - _v2.z, _v1.x - _v2.x);
          }
          model.rotation.y = yaw + rng.range(-CROWD.yawJitter, CROWD.yawJitter);
          model.updateMatrixWorld(true);
          if (pelvis) { pelvis.getWorldPosition(_v1); model.position.x -= _v1.x; model.position.z -= _v1.z; }
          model.updateMatrixWorld(true);

          const cx = idx % cols, cy = Math.floor(idx / cols);
          rt.viewport.set(cx * cellW, cy * cellH, cellW, cellH);
          rt.scissor.set(cx * cellW, cy * cellH, cellW, cellH);
          rt.scissorTest = true;
          renderer.setRenderTarget(rt);
          renderer.render(scene, cam);
          cellInfo.push({ u: (cx * cellW) / W, v: (cy * cellH) / H, gender: info.g, pose: p.clip });
          idx++;
        }
        mixer.stopAllAction();
        mixer.uncacheRoot(model);
        scene.remove(model);
      }
    } finally {
      rt.scissorTest = false;
      rt.viewport.set(0, 0, W, H);
      rt.scissor.set(0, 0, W, H);
      renderer.setRenderTarget(prevTarget);
      renderer.setClearColor(prevClear, prevAlpha);
      renderer.autoClear = prevAutoClear;
      renderer.shadowMap.enabled = prevShadow;
      for (const mat of created) mat.dispose(); // the colour maps stay cached for the avatars
    }
    if (!cellInfo.length) { rt.dispose(); return null; }
    return { rt, cells: cellInfo, cellU: cellW / W, cellV: cellH / H, width: W, height: H };
  }

  // ================================================================== instanced cards

  /** Hidden InstancedMesh with one card per slot; _fillMesh() assigns the atlas cells and shows it. */
  _createMesh(slots) {
    const fw = CROWD.frameWidth, fh = fw * 2;
    const geo = new THREE.PlaneGeometry(fw, fh);
    geo.translate(0, fh / 2 + CROWD.frameBottom, 0);
    const n = slots.length;
    geo.setAttribute('aCell', new THREE.InstancedBufferAttribute(new Float32Array(n * 4), 4));
    geo.setAttribute('aMotion', new THREE.InstancedBufferAttribute(new Float32Array(n * 2), 2));

    const mat = new THREE.MeshBasicMaterial({ alphaTest: 0.5, side: THREE.DoubleSide });
    mat.name = 'crowd';
    const U = this.uniforms;
    mat.onBeforeCompile = (shader) => {
      Object.assign(shader.uniforms, U);
      shader.vertexShader = shader.vertexShader
        .replace('#include <common>', `#include <common>
          attribute vec4 aCell;   // atlas u0, v0, mirror flag, phase
          attribute vec2 aMotion; // base yaw (faces the court), energy
          uniform float uTime; uniform float uExcite; uniform vec2 uCellSize; uniform float uFaceCam;
          uniform float uMaxTurn; uniform float uBob; uniform float uSway; uniform float uJump; uniform float uNearHide;`)
        .replace('#include <uv_vertex>', `#include <uv_vertex>
          vMapUv = aCell.xy + vec2(mix(uv.x, 1.0 - uv.x, aCell.z), uv.y) * uCellSize;`)
        .replace('#include <begin_vertex>', `
          vec3 transformed = vec3(position);
          float ph = aCell.w * 6.2831853;
          float h01 = clamp(position.y / 1.8, 0.0, 1.2);
          transformed.y += (sin(uTime * 1.6 + ph) * 0.5 + 0.5) * uBob;              // breathing
          transformed.x += sin(uTime * 0.55 + ph * 1.7) * uSway * h01;               // weight shift
          float hop = max(0.0, sin(uTime * 7.5 + ph * 3.0));
          transformed.y += hop * hop * uJump * clamp(uExcite * aMotion.y, 0.0, 1.0); // excited jumping
          // partial cylindrical billboard: turn from the court-facing yaw toward the camera (clamped)
          vec3 ctr = (modelMatrix * instanceMatrix * vec4(0.0, 0.0, 0.0, 1.0)).xyz;
          vec2 toCam = cameraPosition.xz - ctr.xz;
          float d = atan(toCam.x, toCam.y) - aMotion.x;
          d = atan(sin(d), cos(d));
          float yaw = aMotion.x + clamp(d, -uMaxTurn, uMaxTurn) * uFaceCam;
          float cy = cos(yaw), sy = sin(yaw);
          transformed = vec3(transformed.x * cy + transformed.z * sy, transformed.y, -transformed.x * sy + transformed.z * cy);
          // a camera inside the stands would see the cards edge-on: collapse the nearest ones
          vec3 dc = cameraPosition - ctr;
          if (dot(dc, dc) < uNearHide * uNearHide) transformed = vec3(0.0);
        `);
    };
    mat.customProgramCacheKey = () => 'du-crowd-impostor';

    const mesh = new THREE.InstancedMesh(geo, mat, n);
    mesh.name = 'Crowd';
    mesh.userData.noAO = true;       // alpha-tested cards must not enter the AO G-buffer
    mesh.castShadow = false;
    mesh.receiveShadow = false;
    mesh.visible = false;            // until an atlas is ready

    // Real-time animation clock + excitement decay, evaluated once per rendered frame.
    mesh.onBeforeRender = () => {
      const now = game.time ? game.time.realNow : performance.now() / 1000;
      const dt = this._lastT === null ? 0 : Math.max(0, Math.min(0.1, now - this._lastT));
      if (dt === 0 && this._lastT !== null) return; // several passes in one frame
      this._lastT = now;
      this.excitement = Math.max(0, this.excitement - CROWD.exciteDecay * dt);
      U.uTime.value = now;
      U.uExcite.value = Math.max(CROWD.idleExcite, this.excitement);
    };
    this.mesh = mesh;
  }

  /**
   * Picks an atlas cell, height, mirror, phase, energy and colour per card, binds the atlas and shows the mesh.
   * @param {CrowdSlot[]} slots
   * @param {{ texture:THREE.Texture, cells:CrowdCell[], cellU:number, cellV:number }} atlas
   * @param {number} seed
   */
  _fillMesh(slots, atlas, seed) {
    const mesh = this.mesh;
    const geo = mesh.geometry;
    const n = slots.length;
    const aCellAttr = geo.getAttribute('aCell'), aMotionAttr = geo.getAttribute('aMotion');
    const aCell = aCellAttr.array, aMotion = aMotionAttr.array;
    this.uniforms.uCellSize.value.set(atlas.cellU, atlas.cellV);
    mesh.material.map = atlas.texture;
    mesh.material.needsUpdate = true;
    const rng = new Rng(seed);
    const cells = atlas.cells;
    for (let i = 0; i < n; i++) {
      const s = slots[i];
      const cell = cells[rng.int(0, cells.length)];
      const height = rng.range(0.93, 1.06) * (cell.gender === 'f' ? 0.97 : 1);
      _pos.set(s.x, s.y, s.z);
      _quat.identity();                 // rotation is applied in the shader (billboarding)
      _scale.setScalar(height);
      mesh.setMatrixAt(i, _m4.compose(_pos, _quat, _scale));
      aCell[i * 4] = cell.u; aCell[i * 4 + 1] = cell.v;
      aCell[i * 4 + 2] = rng.chance(0.5) ? 1 : 0;
      aCell[i * 4 + 3] = rng.next();
      aMotion[i * 2] = s.yaw;
      aMotion[i * 2 + 1] = (0.35 + rng.next() * 0.65) * (0.8 + 0.2 * (s.front || 0)) * (cell.pose === 'cheer' ? 1.2 : 1);
      // colour: brightness variation + optional team colour (by end of the arena)
      const b = rng.range(CROWD.brightness[0], CROWD.brightness[1]);
      _tmpColor.copy(_white);
      if (rng.chance(CROWD.teamTintChance)) _tmpColor.lerp(s.z < 0 ? _home : _away, CROWD.teamTint * rng.range(0.5, 1));
      _tmpColor.offsetHSL(rng.range(-0.02, 0.02), 0, 0).multiplyScalar(b);
      mesh.setColorAt(i, _tmpColor);
    }
    aCellAttr.needsUpdate = true;
    aMotionAttr.needsUpdate = true;
    mesh.instanceMatrix.needsUpdate = true;
    if (mesh.instanceColor) mesh.instanceColor.needsUpdate = true;
    mesh.computeBoundingSphere();
    mesh.computeBoundingBox?.();
    mesh.visible = true;
    this.count = n;
  }

  _subscribe() {
    const ev = game.events;
    if (!ev) return;
    const on = (name, fn) => this._unsubs.push(ev.on(name, fn));
    on(EV.PlayerEliminated, () => this.excite(1));
    on(EV.BallCaught, (e) => this.excite(e && e.quality === 'perfect' ? 0.85 : 0.35));
    on(EV.BallHitPlayer, () => this.excite(0.3));
    on(EV.RoundEnded, () => this.excite(1));
    on(EV.MatchEnded, () => this.excite(1));
    on(EV.AbilityCast, (e) => { if (e && e.slot === SLOT.ULTIMATE) this.excite(0.6); });
  }
}

/** Resolves after an idle callback (or a short timeout), so a fallback bake does not compete with the boot. */
function idle() {
  return new Promise((resolve) => {
    if (typeof requestIdleCallback === 'function') requestIdleCallback(() => resolve(), { timeout: 2000 });
    else setTimeout(resolve, 200);
  });
}

/** Removes horizontal root translation from a clip (keeps the vertical bob), like the avatar module does. */
function stripRootMotion(clip) {
  for (const t of clip.tracks) {
    if (!t.name.endsWith('.position')) continue;
    const v = t.values;
    for (let i = 0; i < v.length; i += 3) { v[i] = 0; v[i + 2] = 0; }
  }
}

/**
 * Gives a cloned avatar simple lit materials for the bake (colour map only - normal detail is sub-pixel at crowd
 * distance) and hides roster.hiddenParts. Returns the created materials for disposal.
 */
function applyBakeMaterials(model, info) {
  const created = [];
  const hidden = ((heroById(info.id) && heroById(info.id).hiddenParts) || []).map((s) => s.toLowerCase());
  model.traverse((o) => {
    if (!o.isMesh) return;
    o.frustumCulled = false;
    o.castShadow = false;
    const mats = Array.isArray(o.material) ? o.material : [o.material];
    const out = mats.map((m) => {
      const name = (m && m.name) || '';
      const spec = (info.def.materials || {})[name] || {};
      const mat = new THREE.MeshStandardMaterial({ name, roughness: spec.kind === 'skin' ? 0.6 : 0.8, metalness: 0 });
      if (info.maps[name]) mat.map = info.maps[name];
      if (spec.kind === 'hair' || spec.alphaTest) { mat.alphaTest = spec.alphaTest || 0.35; mat.side = THREE.DoubleSide; }
      const lower = name.toLowerCase();
      if (hidden.some((h) => lower.endsWith('_' + h) || lower.includes(h))) mat.visible = false;
      created.push(mat);
      return mat;
    });
    o.material = Array.isArray(o.material) ? out : out[0];
  });
  return created;
}
