// ---------------------------------------------------------------------------------------------------------------
// GPU side of the particle system (owner: fx): one InstancedMesh of camera-facing quads per blend mode.
// Per-instance data lives in InstancedBufferAttributes (position+size, HDR colour+alpha, velocity+stretch,
// roll+atlas frame) written by ParticleSim; a small ShaderMaterial billboards/streaks the quads, samples the packed
// atlas (textures.js), softens the intersection with the court floor and applies fog / tone mapping / colour space.
// Only [0, liveCount) of each attribute is uploaded per frame (addUpdateRange).
// ---------------------------------------------------------------------------------------------------------------
import * as THREE from 'three';
import { ParticleSim } from './particleSim.js';
import { ATLAS_COLS, ATLAS_ROWS } from './textures.js';

const VERT = /* glsl */`
attribute vec4 iPosSize;   // world xyz, diameter (m)
attribute vec4 iColor;     // linear HDR rgb, alpha
attribute vec4 iVel;       // world velocity (m/s), stretch (s) - stretch > 0 turns the sprite into a velocity streak
attribute vec2 iRotFrame;  // roll (rad), atlas frame
uniform float uFloorY;
uniform float uSoftFloor;
varying vec2 vUv;
varying vec4 vColor;
varying float vFrame;
varying float vFloorFade;
#include <fog_pars_vertex>
void main() {
  vUv = uv;
  vColor = iColor;
  vFrame = iRotFrame.y;
  float size = iPosSize.w;
  vec4 mvPosition = modelViewMatrix * vec4(iPosSize.xyz, 1.0);
  vec2 corner = position.xy;
  vec2 offset;
  if (iVel.w > 0.0) {
    // Motion streak: align the quad's x axis with the screen-space direction of travel.
    vec3 vv = (modelViewMatrix * vec4(iVel.xyz, 0.0)).xyz;
    float l = length(vv.xy);
    vec2 d = l > 1e-4 ? vv.xy / l : vec2(1.0, 0.0);
    float len = size + l * iVel.w;
    offset = d * (corner.x * len) + vec2(-d.y, d.x) * (corner.y * size);
  } else {
    float c = cos(iRotFrame.x), s = sin(iRotFrame.x);
    offset = vec2(c * corner.x - s * corner.y, s * corner.x + c * corner.y) * size;
  }
  mvPosition.xy += offset;
  gl_Position = projectionMatrix * mvPosition;
  // World height of this corner: camera right.y = viewMatrix[1][0], up.y = viewMatrix[1][1].
  float worldY = iPosSize.y + viewMatrix[1][0] * offset.x + viewMatrix[1][1] * offset.y;
  vFloorFade = clamp((worldY - uFloorY) / max(uSoftFloor * size, 0.015), 0.0, 1.0);
  #include <fog_vertex>
}`;

const FRAG = /* glsl */`
uniform sampler2D uMap;
uniform vec2 uAtlas;
uniform float uGlint;
uniform float uLight;
varying vec2 vUv;
varying vec4 vColor;
varying float vFrame;
varying float vFloorFade;
#include <fog_pars_fragment>
void main() {
  float f = floor(vFrame + 0.5);
  float col = mod(f, uAtlas.x);
  float row = floor(f / uAtlas.x);
  vec2 uv = vec2((col + vUv.x) / uAtlas.x, 1.0 - (row + 1.0 - vUv.y) / uAtlas.y);
  vec3 t = texture2D(uMap, uv).rgb;          // r = shade, g = alpha, b = glint (see textures.js)
  float a = t.g * vColor.a * vFloorFade;
  if (a < 0.003) discard;
  vec3 rgb = vColor.rgb * t.r * uLight + t.b * uGlint * (0.5 * vColor.rgb + 0.5);
  gl_FragColor = vec4(rgb, a);
  #ifdef USE_FOG
    #ifdef FOG_EXP2
      float fogFactor = 1.0 - exp(-fogDensity * fogDensity * vFogDepth * vFogDepth);
    #else
      float fogFactor = smoothstep(fogNear, fogFar, vFogDepth);
    #endif
    #ifdef VFX_ADDITIVE
      gl_FragColor.a *= 1.0 - fogFactor;
    #else
      gl_FragColor.rgb = mix(gl_FragColor.rgb, fogColor, fogFactor);
    #endif
  #endif
  #include <tonemapping_fragment>
  #include <colorspace_fragment>
}`;

/**
 * Hides an object from override-material passes (GTAO / SAO normal+depth prepasses, depth pre-passes): a camera-facing
 * quad rendered with MeshNormalMaterial would write depth and stamp dark AO halos around every puff of smoke.
 * @param {THREE.Mesh} mesh
 */
export function excludeFromOverridePasses(mesh) {
  let saved = -1;
  const isInstanced = !!mesh.isInstancedMesh;
  mesh.onBeforeRender = (renderer, scene, camera, geometry) => {
    if (!scene || !scene.overrideMaterial) return;
    if (isInstanced) { saved = mesh.count; mesh.count = 0; } else { saved = geometry.drawRange.count; geometry.drawRange.count = 0; }
  };
  mesh.onAfterRender = (renderer, scene, camera, geometry) => {
    if (saved < 0) return;
    if (isInstanced) mesh.count = saved; else geometry.drawRange.count = saved;
    saved = -1;
  };
}

/** One blend-mode layer of GPU particles. */
export class ParticleLayer {
  /**
   * @param {{name:string, capacity:number, additive:boolean, atlas:THREE.Texture, renderOrder?:number,
   *          softFloor?:number, glint?:number, light?:number}} o
   */
  constructor(o) {
    this.name = o.name;
    this.additive = !!o.additive;
    this.sim = new ParticleSim(o.capacity);
    const cap = this.sim.capacity;
    const mk = (size) => {
      const a = new THREE.InstancedBufferAttribute(new Float32Array(cap * size), size);
      a.setUsage(THREE.DynamicDrawUsage);
      return a;
    };
    this.aPosSize = mk(4); this.aColor = mk(4); this.aVel = mk(4); this.aRotFrame = mk(2);
    this._attrs = [this.aPosSize, this.aColor, this.aVel, this.aRotFrame];
    this._out = { posSize: this.aPosSize.array, color: this.aColor.array, vel: this.aVel.array, rotFrame: this.aRotFrame.array };

    const geo = new THREE.PlaneGeometry(1, 1);
    geo.deleteAttribute('normal');
    geo.setAttribute('iPosSize', this.aPosSize);
    geo.setAttribute('iColor', this.aColor);
    geo.setAttribute('iVel', this.aVel);
    geo.setAttribute('iRotFrame', this.aRotFrame);

    const uniforms = THREE.UniformsUtils.merge([THREE.UniformsLib.fog, {
      uMap: { value: null }, uAtlas: { value: new THREE.Vector2(ATLAS_COLS, ATLAS_ROWS) },
      uFloorY: { value: 0 }, uSoftFloor: { value: o.softFloor ?? 0.35 },
      uGlint: { value: o.glint ?? (this.additive ? 1.2 : 0.55) }, uLight: { value: o.light ?? 1 },
    }]);
    uniforms.uMap.value = o.atlas; // assigned after merge: merge() would clone (and re-upload) the texture
    this.material = new THREE.ShaderMaterial({
      name: `vfx-particles-${o.name}`,
      uniforms, vertexShader: VERT, fragmentShader: FRAG,
      transparent: true, depthWrite: false, depthTest: true, fog: true,
      blending: this.additive ? THREE.AdditiveBlending : THREE.NormalBlending,
      defines: this.additive ? { VFX_ADDITIVE: '' } : {},
    });
    this.mesh = new THREE.InstancedMesh(geo, this.material, cap);
    this.mesh.name = `vfx-${o.name}`;
    this.mesh.count = 0;
    this.mesh.visible = false;
    this.mesh.frustumCulled = false; // instances span the arena; the quad's own bounds are meaningless
    this.mesh.castShadow = false; this.mesh.receiveShadow = false;
    this.mesh.matrixAutoUpdate = false;
    this.mesh.renderOrder = o.renderOrder ?? 10;
    excludeFromOverridePasses(this.mesh);
  }

  get count() { return this.sim.count; }
  get capacity() { return this.sim.capacity; }

  /** @param {import('./particleSim.js').ParticleSpec} spec */
  spawn(spec) { return this.sim.spawn(spec); }

  clear() { this.sim.clear(); this.mesh.count = 0; this.mesh.visible = false; }

  /** Simulate + upload the live range. */
  update(dt, time, floorY) {
    this.material.uniforms.uFloorY.value = floorY;
    if (!this.sim.update(dt, time, floorY, this._out)) return;
    const n = this.sim.count;
    for (const a of this._attrs) {
      a.clearUpdateRanges();
      if (n > 0) { a.addUpdateRange(0, n * a.itemSize); a.needsUpdate = true; }
    }
    this.mesh.count = n;
    this.mesh.visible = n > 0;
  }

  dispose() {
    this.mesh.removeFromParent();
    this.mesh.geometry.dispose();
    this.material.dispose();
  }
}
