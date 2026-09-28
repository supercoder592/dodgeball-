// ---------------------------------------------------------------------------------------------------------------
// Renderer (owner: render) - WebGL2 rendering stack for the broadcast-sports look. See WEB_ARCHITECTURE.md §3.4.
//
//   WebGLRenderer (sRGB output, ACES filmic, soft PCF shadows, quality-capped pixel ratio)
//   EffectComposer (HalfFloat, linear HDR):
//     RenderPass -> GTAO (medium+, half-res) -> UnrealBloom (subtle, high threshold, medium+)
//       -> 'grade' ShaderPass (vignette, CA, saturation, exposure/flash pulses, danger / freeze / rewind looks)
//       -> OutputPass (tone mapping + sRGB) -> SMAA (high) / FXAA (low, medium) on the display-referred image
//   Environment: PMREM of RoomEnvironment as scene.environment (dimmed for an indoor arena), dark background, haze.
//
// Juice drives screen feedback through pulse(type, intensity, duration) (decays in REAL time, so it keeps
// animating through hitstop) and setSustained(type, amount) for persistent looks (Danger Sense, frozen, rewinding).
// Notes on three r186: PCFSoftShadowMap was removed; PCFShadowMap now performs vogel-disk soft filtering controlled
// by light.shadow.radius, which is what the arena lights use.
// ---------------------------------------------------------------------------------------------------------------
import * as THREE from 'three';
import { EffectComposer } from 'three/addons/postprocessing/EffectComposer.js';
import { RenderPass } from 'three/addons/postprocessing/RenderPass.js';
import { GTAOPass } from 'three/addons/postprocessing/GTAOPass.js';
import { UnrealBloomPass } from 'three/addons/postprocessing/UnrealBloomPass.js';
import { ShaderPass } from 'three/addons/postprocessing/ShaderPass.js';
import { SMAAPass } from 'three/addons/postprocessing/SMAAPass.js';
import { FXAAPass } from 'three/addons/postprocessing/FXAAPass.js';
import { OutputPass } from 'three/addons/postprocessing/OutputPass.js';
import { RoomEnvironment } from 'three/addons/environments/RoomEnvironment.js';
import { GradeShader, GRADE_BASE } from './gradeShader.js';
import { ScreenPulses } from './pulses.js';
import { qualityId, qualityPreset } from './quality.js';
import { game } from '../game.js';

/** Global look tuning (exposed so designers can tweak from the console: game.renderer.look). */
export const RENDER_LOOK = {
  exposure: 1.0,              // ACES tone mapping exposure
  environmentIntensity: 0.32, // RoomEnvironment is a bright white studio - an arena bowl is much darker
  background: 0x07080a,       // above the lit bowl (only visible through gaps)
  fogColor: 0x121418,         // faint haze in the air under the floodlights
  fogNear: 26,
  fogFar: 115,
  fov: 60,
  near: 0.05,
  far: 260,
  bloom: { strength: 0.22, radius: 0.4, threshold: 2.4 }, // only lamp lenses / VFX cores bloom
  ao: {
    radius: 0.5, distanceExponent: 1.4, thickness: 1.2, scale: 1.0, distanceFallOff: 1.0, blendIntensity: 0.85,
    denoise: { lumaPhi: 10, depthPhi: 2, normalPhi: 3, radius: 6, rings: 2 },
  },
};

/**
 * GTAO at a reduced resolution that also skips geometry which would pollute the normal/depth G-buffer: points,
 * lines, sprites, transparent meshes (VFX, cloaks, shields) and anything flagged `userData.noAO` (crowd impostor
 * cards, whose alpha-tested silhouettes would otherwise become solid rectangles in the AO depth).
 */
class ArenaGTAOPass extends GTAOPass {
  constructor(scene, camera, width, height, resolutionScale) {
    const s = resolutionScale || 1;
    super(scene, camera, Math.max(1, Math.round(width * s)), Math.max(1, Math.round(height * s)));
    this.resolutionScale = s;
    const cache = this._visibilityCache;
    // Traversal callback allocated once (the pass runs every frame).
    this._hideVisitor = (o) => {
      if (!o.visible) return;
      let hide = o.isPoints || o.isLine || o.isLine2 || o.isSprite || (o.userData && o.userData.noAO === true);
      if (!hide && o.isMesh && o.material) {
        const m = o.material;
        if (Array.isArray(m)) { for (let i = 0; i < m.length; i++) if (m[i] && m[i].transparent) { hide = true; break; } } else hide = m.transparent === true;
      }
      if (hide) { o.visible = false; cache.push(o); }
    };
  }
  setSize(width, height) {
    const s = this.resolutionScale || 1;
    super.setSize(Math.max(1, Math.round(width * s)), Math.max(1, Math.round(height * s)));
  }
  _overrideVisibility() { this.scene.traverse(this._hideVisitor); }
}

export class Renderer {
  /**
   * @param {HTMLElement} container element that receives the canvas (sized to it)
   * @param {'low'|'medium'|'high'} quality
   */
  constructor(container, quality = 'high') {
    this.container = container || document.body;
    this.quality = qualityId(quality);
    this.settings = qualityPreset(this.quality);
    this.look = RENDER_LOOK;

    // ------------------------------------------------------------------ WebGL renderer
    const three = new THREE.WebGLRenderer({
      antialias: false,                 // post AA (SMAA/FXAA) - MSAA is lost through the composer anyway
      powerPreference: 'high-performance',
      stencil: false,
      alpha: false,
    });
    this.three = three;
    three.outputColorSpace = THREE.SRGBColorSpace;
    three.toneMapping = THREE.ACESFilmicToneMapping;
    three.toneMappingExposure = RENDER_LOOK.exposure;
    three.shadowMap.enabled = true;
    three.shadowMap.type = THREE.PCFShadowMap; // r186: soft vogel-disk PCF (PCFSoftShadowMap was removed)
    three.info.autoReset = false;              // we reset once per frame so stats cover every pass
    three.setClearColor(RENDER_LOOK.background, 1);
    const canvas = three.domElement;
    canvas.style.display = 'block';
    canvas.style.touchAction = 'none';
    canvas.setAttribute('tabindex', '0');
    this.container.appendChild(canvas);

    // ------------------------------------------------------------------ scene + camera
    this.scene = new THREE.Scene();
    this.scene.name = 'DodgeballScene';
    this.scene.background = new THREE.Color(RENDER_LOOK.background);
    this.scene.fog = new THREE.Fog(RENDER_LOOK.fogColor, RENDER_LOOK.fogNear, RENDER_LOOK.fogFar);
    const { w, h } = this._containerSize();
    this.camera = new THREE.PerspectiveCamera(RENDER_LOOK.fov, w / h, RENDER_LOOK.near, RENDER_LOOK.far);
    this.camera.position.set(0, 6.5, -19);
    this.camera.lookAt(0, 0.8, 0);
    this.scene.add(this.camera); // lets systems parent view-space objects to the camera

    // ------------------------------------------------------------------ image based lighting
    this.pmrem = new THREE.PMREMGenerator(three);
    const room = new RoomEnvironment();
    this.envTexture = this.pmrem.fromScene(room, 0.04).texture;
    if (typeof room.dispose === 'function') room.dispose();
    this.scene.environment = this.envTexture;
    this.scene.environmentIntensity = RENDER_LOOK.environmentIntensity;

    // ------------------------------------------------------------------ post
    this.pulses = new ScreenPulses();
    this.composer = null;
    this.passes = { render: null, ao: null, bloom: null, grade: null, output: null, aa: null };
    this._width = w; this._height = h;
    this._pixelRatio = 1;
    this._time = 0;
    this._contextLost = false;
    this._applyPixelRatio();
    three.setSize(w, h, true);
    this._buildComposer();

    // ------------------------------------------------------------------ resize / context loss
    this._onResize = () => this.resize();
    window.addEventListener('resize', this._onResize);
    this._resizeObserver = null;
    if (typeof ResizeObserver === 'function') {
      this._resizeObserver = new ResizeObserver(() => this.resize());
      this._resizeObserver.observe(this.container);
    }
    this._onContextLost = (e) => { e.preventDefault(); this._contextLost = true; console.warn('[renderer] WebGL context lost'); };
    this._onContextRestored = () => { this._contextLost = false; this._buildComposer(); this.resize(true); };
    canvas.addEventListener('webglcontextlost', this._onContextLost, false);
    canvas.addEventListener('webglcontextrestored', this._onContextRestored, false);
  }

  // ================================================================== public API

  /** Renders one frame. `realDt` = unscaled seconds (screen pulses decay in real time). */
  render(realDt = 1 / 60) {
    if (this._contextLost) return;
    const dt = Math.max(0, Math.min(0.1, realDt || 0));
    this._time += dt;
    this.pulses.update(dt);
    this._applyGrade();
    this.three.info.reset();
    if (this.composer) this.composer.render(dt);
    else this.three.render(this.scene, this.camera);
  }

  /** Re-reads the container size and resizes renderer, camera and every pass. */
  resize(force = false) {
    const { w, h } = this._containerSize();
    const prChanged = this._applyPixelRatio();
    if (!force && !prChanged && w === this._width && h === this._height) return;
    this._width = w; this._height = h;
    this.three.setSize(w, h, true);
    this.camera.aspect = w / h;
    this.camera.updateProjectionMatrix();
    if (this.composer) {
      this.composer.setPixelRatio(this._pixelRatio);
      this.composer.setSize(w, h);
    }
    if (this.passes.grade) this.passes.grade.uniforms.uResolution.value.set(w * this._pixelRatio, h * this._pixelRatio);
  }

  /**
   * One-shot screen pulse. Types: 'hit' 'heavyHit' 'perfectCatch' 'ultimate' 'freeze' 'rewind' 'danger'.
   * @param {string} type
   * @param {number} intensity 0..~2
   * @param {number} duration real seconds
   */
  pulse(type, intensity = 1, duration = 0.3) { return this.pulses.pulse(type, intensity, duration); }

  /** Persistent screen look (0 = off .. 1 = full), smoothed. Same types as pulse(). */
  setSustained(type, amount) { return this.pulses.setSustained(type, amount); }

  /** Switches quality at runtime (pixel ratio, post stack, arena shadows). */
  setQuality(quality) {
    const q = qualityId(quality);
    if (q === this.quality) return;
    this.quality = q;
    this.settings = qualityPreset(q);
    if (game.config) game.config.quality = q;
    this._applyPixelRatio();
    this._buildComposer();
    game.arena?.applyQuality?.(q);
    this.resize(true);
  }

  /** Tone mapping exposure (1 = neutral). */
  setExposure(e) { this.three.toneMappingExposure = RENDER_LOOK.exposure = Math.max(0.05, e); }

  /** Render statistics of the last frame (all passes). */
  stats() {
    const r = this.three.info.render, m = this.three.info.memory;
    return {
      calls: r.calls, triangles: r.triangles, geometries: m.geometries, textures: m.textures,
      quality: this.quality, pixelRatio: this._pixelRatio, width: this._width, height: this._height,
    };
  }

  get width() { return this._width; }
  get height() { return this._height; }
  get pixelRatio() { return this._pixelRatio; }
  get domElement() { return this.three.domElement; }

  dispose() {
    window.removeEventListener('resize', this._onResize);
    if (this._resizeObserver) this._resizeObserver.disconnect();
    const canvas = this.three.domElement;
    canvas.removeEventListener('webglcontextlost', this._onContextLost);
    canvas.removeEventListener('webglcontextrestored', this._onContextRestored);
    this._disposeComposer();
    this.envTexture?.dispose();
    this.pmrem.dispose();
    this.three.dispose();
    canvas.remove();
  }

  // ================================================================== internals

  _containerSize() {
    const c = this.container;
    const w = Math.max(1, Math.floor((c && c.clientWidth) || window.innerWidth || 1280));
    const h = Math.max(1, Math.floor((c && c.clientHeight) || window.innerHeight || 720));
    return { w, h };
  }

  /** Applies the quality-capped device pixel ratio. Returns true if it changed. */
  _applyPixelRatio() {
    const dpr = window.devicePixelRatio || 1;
    const pr = Math.max(0.5, Math.min(dpr, this.settings.maxPixelRatio));
    if (pr === this._pixelRatio) return false;
    this._pixelRatio = pr;
    this.three.setPixelRatio(pr);
    return true;
  }

  _disposeComposer() {
    if (!this.composer) return;
    for (const p of this.composer.passes) { try { p.dispose && p.dispose(); } catch (e) { /* pass without GPU resources */ } }
    this.composer.dispose();
    this.composer = null;
    for (const k of Object.keys(this.passes)) this.passes[k] = null;
  }

  /** (Re)builds the post-processing chain for the current quality. Falls back to direct rendering on failure. */
  _buildComposer() {
    this._disposeComposer();
    const S = this.settings, w = this._width, h = this._height, pr = this._pixelRatio;
    try {
      const composer = new EffectComposer(this.three);
      composer.setPixelRatio(pr);
      composer.setSize(w, h);

      const render = new RenderPass(this.scene, this.camera);
      composer.addPass(render);

      let ao = null;
      if (S.ao) {
        const A = RENDER_LOOK.ao;
        ao = new ArenaGTAOPass(this.scene, this.camera, w * pr, h * pr, S.aoScale);
        ao.updateGtaoMaterial({
          radius: A.radius, distanceExponent: A.distanceExponent, thickness: A.thickness, scale: A.scale,
          distanceFallOff: A.distanceFallOff, samples: S.aoSamples, screenSpaceRadius: false,
        });
        ao.updatePdMaterial({ ...A.denoise, samples: Math.min(16, S.aoSamples) });
        ao.blendIntensity = A.blendIntensity;
        composer.addPass(ao);
      }

      let bloom = null;
      if (S.bloom) {
        const B = RENDER_LOOK.bloom;
        bloom = new UnrealBloomPass(new THREE.Vector2(w * pr, h * pr), B.strength, B.radius, B.threshold);
        composer.addPass(bloom);
      }

      const grade = new ShaderPass(GradeShader);
      grade.uniforms.uResolution.value.set(w * pr, h * pr);
      grade.uniforms.uFlashColor.value.setRGB(GRADE_BASE.flashColor[0], GRADE_BASE.flashColor[1], GRADE_BASE.flashColor[2]);
      composer.addPass(grade);

      const output = new OutputPass();
      composer.addPass(output);

      // Anti-aliasing on the display-referred (tone mapped, sRGB) image, as both SMAA and FXAA expect.
      let aa = null;
      if (S.aa === 'smaa') aa = new SMAAPass();
      else if (S.aa === 'fxaa') aa = new FXAAPass();
      if (aa) composer.addPass(aa);

      composer.setSize(w, h); // propagate the final size to every pass (incl. half-res AO)
      this.composer = composer;
      Object.assign(this.passes, { render, ao, bloom, grade, output, aa });
    } catch (e) {
      console.warn('[renderer] post-processing unavailable, rendering directly', e);
      this._disposeComposer();
      this.composer = null;
    }
  }

  /** Pushes resting grade + pulse channel values into the grade pass uniforms. */
  _applyGrade() {
    const g = this.passes.grade;
    if (!g) return;
    const u = g.uniforms, v = this.pulses.values;
    u.uTime.value = this._time;
    u.uExposure.value = v.exposure;
    u.uFlash.value = v.flash;
    u.uCA.value = GRADE_BASE.ca + v.ca;
    u.uSaturation.value = Math.max(0, GRADE_BASE.saturation + v.saturation);
    u.uVignette.value = GRADE_BASE.vignette + v.vignette;
    u.uDanger.value = v.danger;
    u.uFreeze.value = v.freeze;
    u.uRewind.value = v.rewind;
    u.uZoom.value = v.zoom;
    u.uGrain.value = GRADE_BASE.grain;
  }
}
