// ---------------------------------------------------------------------------------------------------------------
// Pooled mesh effects (owner: fx): things particles are bad at.
//   ShapePool 'ring'   - flat quad with an analytic shader: expanding shock rings, floor decals (frost, glue, scorch),
//                        square zone outlines, travelling ripples (magnetic field), radial cracks (earthquake).
//                        Orientation: floor / camera billboard / arbitrary normal (shield impact ripples).
//   ShapePool 'sphere' - fresnel shimmer bubble (stasis, magnetic field dome, clone spawn pop).
//   ShapePool 'column' - open cylinder / square prism with a vertical fade and scrolling streaks (revive beam,
//                        teleport flash, temporal zone walls).
//   RibbonPool         - camera-facing light trails built on the CPU from a timed point history (beam, fire, rewind).
//   LightPool          - a few shadowless PointLights for flashes and moving glows (real illumination of players and
//                        floor; always in the scene with intensity 0 so the light count - and every shader - is stable).
// All materials: transparent, depthWrite off, fog aware, tone mapped, excluded from AO override passes.
// Every pool is fixed size and allocation free after construction.
// ---------------------------------------------------------------------------------------------------------------
import * as THREE from 'three';
import { excludeFromOverridePasses } from './particles.js';

/** Ring shader modes. */
export const RING_MODE = Object.freeze({ RING: 0, DISC: 1, SQUARE: 2, RIPPLE: 3, CRACKS: 4 });
/** Shape orientation. */
export const ORIENT = Object.freeze({ FLOOR: 0, BILLBOARD: 1, NORMAL: 2 });

const NOISE_GLSL = /* glsl */`
float vfxHash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
float vfxNoise(vec2 p) {
  vec2 i = floor(p), f = fract(p);
  vec2 u = f * f * (3.0 - 2.0 * f);
  return mix(mix(vfxHash(i), vfxHash(i + vec2(1.0, 0.0)), u.x), mix(vfxHash(i + vec2(0.0, 1.0)), vfxHash(i + vec2(1.0, 1.0)), u.x), u.y);
}
float vfxFbm(vec2 p) {
  float s = 0.0, a = 0.5;
  for (int i = 0; i < 4; i++) { s += a * vfxNoise(p); p *= 2.03; a *= 0.5; }
  return s / 0.9375;
}`;

const FOG_FRAG = /* glsl */`
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
  #include <colorspace_fragment>`;

const BASIC_VERT = /* glsl */`
varying vec2 vUv;
varying vec3 vNormalV;
varying vec3 vViewDir;
varying vec3 vObj;
#include <fog_pars_vertex>
void main() {
  vUv = uv;
  vObj = position;
  vec4 mvPosition = modelViewMatrix * vec4(position, 1.0);
  vNormalV = normalize(normalMatrix * normal);
  vViewDir = normalize(-mvPosition.xyz);
  gl_Position = projectionMatrix * mvPosition;
  #include <fog_vertex>
}`;

const RING_FRAG = /* glsl */`
uniform vec3 uColor;
uniform float uOpacity, uRadius, uWidth, uMode, uTime, uSeed, uNoise, uInner;
varying vec2 vUv;
#include <fog_pars_fragment>
${NOISE_GLSL}
void main() {
  vec2 p = vUv * 2.0 - 1.0;
  float r = length(p);
  float ang = atan(p.y, p.x);
  vec2 cp = vec2(cos(ang), sin(ang)); // seamless angular noise domain
  vec3 col = uColor;
  float a = 0.0;
  if (uMode < 0.5) {
    // expanding shock ring + optional faint wash inside it
    float n = vfxFbm(cp * 2.5 + uSeed + uTime * 0.4);
    float rr = r + (n - 0.5) * uNoise;
    a = 1.0 - smoothstep(0.0, uWidth, abs(rr - uRadius));
    a = a * a + uInner * (1.0 - smoothstep(0.0, uRadius, rr)) * step(rr, uRadius);
  } else if (uMode < 1.5) {
    // floor decal disc with an organic edge and interior detail (frost, glue, scorch)
    float n = vfxFbm(p * 3.0 + uSeed);
    float edge = uRadius * (1.0 + (n - 0.5) * uNoise * 2.0);
    a = 1.0 - smoothstep(edge - uWidth, edge, r);
    float detail = vfxFbm(p * 9.0 + uSeed * 1.7);
    col *= 0.6 + 0.8 * detail;
    a *= 0.7 + 0.3 * detail;
  } else if (uMode < 2.5) {
    // square zone outline + inward scan lines (time zone)
    vec2 q = abs(p);
    float m = max(q.x, q.y);
    a = 1.0 - smoothstep(0.0, uWidth, abs(m - uRadius));
    float scan = pow(0.5 + 0.5 * sin(m * 26.0 + uTime * 6.0), 8.0);
    a += uInner * step(m, uRadius) * (0.25 + scan);
  } else if (uMode < 3.5) {
    // concentric ripples travelling inward (uInner > 0) or outward (< 0), bright rim
    float waves = pow(0.5 + 0.5 * sin(r * 30.0 + uTime * 8.0 * uInner), 6.0);
    a = waves * (1.0 - smoothstep(uRadius * 0.8, uRadius, r)) * smoothstep(0.04, 0.3, r) * 0.7;
    a += (1.0 - smoothstep(0.0, uWidth, abs(r - uRadius))) * 0.9;
  } else {
    // radial fractures + branches (ground cracks)
    float n = vfxFbm(vec2(ang * 3.0, r * 2.5) + uSeed);
    float lines = abs(sin(ang * 7.0 + n * 5.0 + uSeed));
    float crack = 1.0 - smoothstep(0.0, 0.05 + 0.06 * r, lines);
    float br = abs(sin(ang * 19.0 + vfxFbm(p * 5.0 + uSeed) * 8.0));
    float branch = (1.0 - smoothstep(0.0, 0.04, br)) * step(0.3, r) * 0.7;
    a = max(crack, branch) * (1.0 - smoothstep(uRadius * 0.55, uRadius, r)) * smoothstep(0.03, 0.14, r);
  }
  a = clamp(a, 0.0, 1.0) * uOpacity * (1.0 - smoothstep(0.96, 1.0, r * step(uMode, 1.5) + max(abs(p.x), abs(p.y)) * step(1.5, uMode)));
  if (a < 0.003) discard;
  gl_FragColor = vec4(col, a);
  ${FOG_FRAG}
}`;

const SPHERE_FRAG = /* glsl */`
uniform vec3 uColor;
uniform float uOpacity, uTime, uPower, uFill, uBands;
varying vec3 vNormalV;
varying vec3 vViewDir;
varying vec3 vObj;
#include <fog_pars_fragment>
void main() {
  float facing = abs(dot(normalize(vNormalV), normalize(vViewDir)));
  float rim = pow(1.0 - facing, uPower);
  float bands = 0.5 + 0.5 * sin(vObj.y * uBands + uTime * 3.0 + sin(vObj.x * 7.0 + uTime * 1.7) * 1.5);
  float shimmer = 0.78 + 0.22 * sin(uTime * 11.0 + vObj.x * 13.0 + vObj.z * 9.0);
  float a = (rim * (0.65 + 0.35 * bands) + uFill * (0.4 + 0.6 * bands)) * shimmer * uOpacity;
  if (a < 0.003) discard;
  gl_FragColor = vec4(uColor, clamp(a, 0.0, 1.0));
  ${FOG_FRAG}
}`;

const COLUMN_FRAG = /* glsl */`
uniform vec3 uColor;
uniform float uOpacity, uTime, uScroll, uFadePow, uEdge;
varying vec2 vUv;
varying vec3 vNormalV;
varying vec3 vViewDir;
#include <fog_pars_fragment>
${NOISE_GLSL}
void main() {
  float h = vUv.y;
  float vfade = pow(1.0 - h, uFadePow) * smoothstep(0.0, 0.05, h);
  float n = 0.5 + 0.5 * vfxNoise(vec2(vUv.x * 28.0, h * 5.0 - uTime * uScroll));
  float facing = abs(dot(normalize(vNormalV), normalize(vViewDir)));
  float edge = mix(1.0, pow(1.0 - facing, 1.4) * 1.5 + 0.2, uEdge);
  float a = vfade * n * edge * uOpacity;
  if (a < 0.003) discard;
  gl_FragColor = vec4(uColor * (0.8 + 0.4 * n), clamp(a, 0.0, 1.0));
  ${FOG_FRAG}
}`;

const RIBBON_VERT = /* glsl */`
varying vec2 vUv;
#include <fog_pars_vertex>
void main() {
  vUv = uv;
  vec4 mvPosition = modelViewMatrix * vec4(position, 1.0);
  gl_Position = projectionMatrix * mvPosition;
  #include <fog_vertex>
}`;

const RIBBON_FRAG = /* glsl */`
uniform vec3 uColor;
uniform float uOpacity, uCore;
varying vec2 vUv;
#include <fog_pars_fragment>
void main() {
  float across = 1.0 - abs(vUv.y * 2.0 - 1.0);
  float along = 1.0 - vUv.x;                       // 1 at the head, 0 at the tail
  float a = pow(across, 1.4) * pow(along, 1.5) * smoothstep(0.0, 0.04, vUv.x + 0.02) * uOpacity;
  if (a < 0.003) discard;
  vec3 col = uColor + vec3(uCore) * pow(across, 8.0) * along;
  gl_FragColor = vec4(col, clamp(a, 0.0, 1.0));
  ${FOG_FRAG}
}`;

function makeMaterial(name, vert, frag, uniforms, additive, side = THREE.DoubleSide) {
  const u = THREE.UniformsUtils.merge([THREE.UniformsLib.fog, uniforms]);
  return new THREE.ShaderMaterial({
    name, uniforms: u, vertexShader: vert, fragmentShader: frag,
    transparent: true, depthWrite: false, depthTest: true, fog: true, side,
    blending: additive ? THREE.AdditiveBlending : THREE.NormalBlending,
    defines: additive ? { VFX_ADDITIVE: '' } : {},
  });
}

const _up = new THREE.Vector3(0, 1, 0);
const _zAxis = new THREE.Vector3(0, 0, 1);
const _qFloor = new THREE.Quaternion().setFromAxisAngle(new THREE.Vector3(1, 0, 0), -Math.PI / 2);
const _q = new THREE.Quaternion();
const _qSpin = new THREE.Quaternion();

/**
 * Reusable shape spawn description (fill the shared SHAPE instance, then pool.spawn(SHAPE)).
 * Radii/widths are world metres; colours linear HDR.
 */
export class ShapeSpec {
  constructor() { this.pos = new THREE.Vector3(); this.normal = new THREE.Vector3(0, 1, 0); this.offset = new THREE.Vector3(); this.color = new THREE.Color(); this.reset(); }
  reset() {
    this.pos.set(0, 0, 0); this.normal.set(0, 1, 0); this.offset.set(0, 0, 0); this.color.setRGB(1, 1, 1);
    this.life = 0.5; this.delay = 0;
    this.r0 = 0.1; this.r1 = 1; this.easePow = 2;      // radius over life (ease-out)
    this.w0 = 0.1; this.w1 = 0.1;                        // ring width / column height scale
    this.h0 = 2; this.h1 = 2;                            // column height (m)
    this.opacity = 1; this.fadeIn = 0.05; this.fadeStart = 0; this.fadePow = 1.5;
    this.pulseAmp = 0; this.pulseFreq = 0;
    this.hold = false;                                   // stays at fadeStart until released (sustained shapes)
    this.orient = ORIENT.FLOOR; this.lift = 0.012;       // floor decals float 1.2 cm over the floor
    this.follow = null;                                  // emitter (has .pos) to follow
    this.mode = RING_MODE.RING; this.noise = 0.05; this.inner = 0; this.spin = 0;
    this.power = 2.5; this.fill = 0; this.bands = 10;    // sphere
    this.scroll = 1; this.fadePowV = 1.2; this.edge = 0.6; this.square = false; // column
    return this;
  }
}
export const SHAPE = new ShapeSpec();

class Shape {
  constructor(mesh) {
    this.mesh = mesh;
    this.u = mesh.material.uniforms;
    this.active = false;
    this.pos = new THREE.Vector3(); this.normal = new THREE.Vector3(0, 1, 0); this.offset = new THREE.Vector3();
    this.color = new THREE.Color();
    this.seed = Math.random() * 100;
    this.angle = 0;
    this.t = 0;
    this.hold = false;
    this.hidden = false; // set by the owning emitter while it is paused (target invisible / cloaked)
    this.follow = null;
  }
}

/** Pool of one kind of shape mesh. */
export class ShapePool {
  /**
   * @param {'ring'|'sphere'|'column'} kind
   * @param {number} count
   * @param {boolean} additive
   * @param {number} renderOrder
   */
  constructor(kind, count, additive, renderOrder) {
    this.kind = kind;
    this.additive = additive;
    this.group = new THREE.Group();
    this.group.name = `vfx-${kind}${additive ? '-add' : ''}`;
    this.items = [];
    let geo, geoSquare = null, mat;
    if (kind === 'ring') {
      geo = new THREE.PlaneGeometry(1, 1);
    } else if (kind === 'sphere') {
      geo = new THREE.IcosahedronGeometry(1, 3);
    } else {
      geo = new THREE.CylinderGeometry(1, 1, 1, 40, 1, true); geo.translate(0, 0.5, 0);
      geoSquare = new THREE.CylinderGeometry(1, 1, 1, 4, 1, true); geoSquare.translate(0, 0.5, 0);
    }
    this.geo = geo; this.geoSquare = geoSquare;
    for (let i = 0; i < count; i++) {
      if (kind === 'ring') {
        mat = makeMaterial('vfx-ring', BASIC_VERT, RING_FRAG, {
          uColor: { value: new THREE.Color() }, uOpacity: { value: 1 }, uRadius: { value: 0.5 }, uWidth: { value: 0.05 },
          uMode: { value: 0 }, uTime: { value: 0 }, uSeed: { value: 0 }, uNoise: { value: 0 }, uInner: { value: 0 },
        }, additive);
        mat.polygonOffset = true; mat.polygonOffsetFactor = -2; mat.polygonOffsetUnits = -2;
      } else if (kind === 'sphere') {
        mat = makeMaterial('vfx-sphere', BASIC_VERT, SPHERE_FRAG, {
          uColor: { value: new THREE.Color() }, uOpacity: { value: 1 }, uTime: { value: 0 }, uPower: { value: 2.5 },
          uFill: { value: 0 }, uBands: { value: 10 },
        }, additive, THREE.FrontSide);
      } else {
        mat = makeMaterial('vfx-column', BASIC_VERT, COLUMN_FRAG, {
          uColor: { value: new THREE.Color() }, uOpacity: { value: 1 }, uTime: { value: 0 }, uScroll: { value: 1 },
          uFadePow: { value: 1.2 }, uEdge: { value: 0.6 },
        }, additive);
      }
      const mesh = new THREE.Mesh(geo, mat);
      mesh.visible = false;
      mesh.frustumCulled = false;
      mesh.castShadow = false; mesh.receiveShadow = false;
      mesh.renderOrder = renderOrder;
      excludeFromOverridePasses(mesh);
      this.group.add(mesh);
      this.items.push(new Shape(mesh));
    }
  }

  /**
   * Starts a shape from SHAPE-like spec. Steals the most progressed non-held shape when saturated.
   * @param {ShapeSpec} s
   * @returns {Shape|null}
   */
  spawn(s) {
    let sh = null, best = -1;
    for (const it of this.items) {
      if (!it.active) { sh = it; break; }
      if (!it.hold && it.t > best) { best = it.t; sh = it; }
    }
    if (!sh) return null;
    sh.active = true; sh.t = 0; sh.life = Math.max(0.01, s.life); sh.delay = s.delay;
    sh.r0 = s.r0; sh.r1 = s.r1; sh.easePow = s.easePow; sh.w0 = s.w0; sh.w1 = s.w1; sh.h0 = s.h0; sh.h1 = s.h1;
    sh.opacity = s.opacity; sh.fadeIn = s.fadeIn; sh.fadeStart = Math.min(0.999, Math.max(0, s.fadeStart)); sh.fadePow = s.fadePow;
    sh.pulseAmp = s.pulseAmp; sh.pulseFreq = s.pulseFreq; sh.hold = s.hold; sh.orient = s.orient; sh.lift = s.lift;
    sh.follow = s.follow; sh.pos.copy(s.pos); sh.normal.copy(s.normal).normalize(); sh.offset.copy(s.offset);
    sh.color.copy(s.color); sh.mode = s.mode; sh.noise = s.noise; sh.inner = s.inner; sh.spin = s.spin; sh.angle = 0;
    sh.power = s.power; sh.fill = s.fill; sh.bands = s.bands; sh.scroll = s.scroll; sh.fadePowV = s.fadePowV; sh.edge = s.edge;
    sh.seed = Math.random() * 100;
    sh.hidden = false;
    if (this.kind === 'column') sh.mesh.geometry = s.square ? this.geoSquare : this.geo;
    sh.square = s.square;
    sh.mesh.visible = false;
    return sh;
  }

  /**
   * Lets a held shape finish: it stops following, stays where it is and fades out over `fade` seconds.
   * @param {Shape|null} sh @param {number} fade seconds
   */
  static release(sh, fade = 0.35) {
    if (!sh || !sh.active) return;
    sh.follow = null;
    sh.hidden = false;
    if (!sh.hold) return;
    sh.hold = false;
    sh.fadeStart = Math.min(0.999, sh.t);
    sh.life = Math.max(0.01, fade) / Math.max(1e-3, 1 - sh.t);
  }

  clear() { for (const it of this.items) { it.active = false; it.mesh.visible = false; } }

  /**
   * @param {number} dt scaled seconds
   * @param {number} time effect clock
   * @param {THREE.Quaternion} camQuat camera world orientation (billboards)
   * @param {number} floorY
   */
  update(dt, time, camQuat, floorY) {
    for (const sh of this.items) {
      if (!sh.active) continue;
      if (sh.delay > 0) { sh.delay -= dt; sh.mesh.visible = false; continue; }
      if (sh.follow) {
        if (sh.follow.active) sh.pos.copy(sh.follow.pos);
        else ShapePool.release(sh);
      }
      sh.t += dt / sh.life;
      if (sh.hold && sh.t > sh.fadeStart) sh.t = sh.fadeStart;
      if (sh.t >= 1) { sh.active = false; sh.mesh.visible = false; continue; }
      const t = sh.t;
      const e = 1 - Math.pow(1 - t, sh.easePow);
      let op = sh.opacity;
      if (sh.fadeIn > 0 && t < sh.fadeIn) op *= t / sh.fadeIn;
      if (t > sh.fadeStart) op *= Math.pow(1 - (t - sh.fadeStart) / (1 - sh.fadeStart), sh.fadePow);
      if (sh.pulseAmp > 0) op *= 1 - sh.pulseAmp * 0.5 * (1 + Math.sin(time * sh.pulseFreq + sh.seed));
      const m = sh.mesh;
      const R = Math.max(0.001, sh.r0 + (sh.r1 - sh.r0) * e);
      m.position.copy(sh.pos).add(sh.offset);
      sh.angle += sh.spin * dt;

      // orientation
      if (sh.orient === ORIENT.FLOOR) {
        if (this.kind === 'ring') { m.position.y = floorY + sh.lift; m.quaternion.copy(_qFloor); }
        else m.quaternion.identity();
        if (sh.angle !== 0) { _qSpin.setFromAxisAngle(_up, sh.angle); m.quaternion.premultiply(_qSpin); }
      } else if (sh.orient === ORIENT.BILLBOARD) {
        m.quaternion.copy(camQuat);
        if (sh.angle !== 0) { _qSpin.setFromAxisAngle(_zAxis, sh.angle); m.quaternion.multiply(_qSpin); }
      } else {
        _q.setFromUnitVectors(this.kind === 'ring' ? _zAxis : _up, sh.normal);
        m.quaternion.copy(_q);
      }

      const u = sh.u;
      u.uColor.value.copy(sh.color);
      u.uOpacity.value = op;
      u.uTime.value = time + sh.seed;
      if (this.kind === 'ring') {
        // quad spans +-Rmax so the ring never clips; shader radius/width are normalised to Rmax
        const rMax = Math.max(sh.r0, sh.r1) * (1.12 + (sh.mode === RING_MODE.DISC ? sh.noise : 0)) + Math.max(sh.w0, sh.w1);
        m.scale.set(rMax * 2, rMax * 2, 1);
        u.uRadius.value = R / rMax;
        u.uWidth.value = Math.max(0.002, (sh.w0 + (sh.w1 - sh.w0) * e) / rMax);
        u.uMode.value = sh.mode; u.uSeed.value = sh.seed; u.uNoise.value = sh.noise; u.uInner.value = sh.inner;
      } else if (this.kind === 'sphere') {
        m.scale.setScalar(R);
        u.uPower.value = sh.power; u.uFill.value = sh.fill; u.uBands.value = sh.bands;
      } else {
        const H = sh.h0 + (sh.h1 - sh.h0) * e;
        // square prism: 4 radial segments rotated 45 deg, radius = half diagonal so the side equals 2R
        const rr = sh.square ? R * Math.SQRT2 : R;
        m.scale.set(rr, Math.max(0.01, H), rr);
        if (sh.square) { _qSpin.setFromAxisAngle(_up, Math.PI / 4); m.quaternion.multiply(_qSpin); }
        u.uScroll.value = sh.scroll; u.uFadePow.value = sh.fadePowV; u.uEdge.value = sh.edge;
      }
      m.visible = !sh.hidden && op > 0.002;
    }
  }

  dispose() {
    this.group.removeFromParent();
    for (const it of this.items) it.mesh.material.dispose();
    this.geo.dispose();
    if (this.geoSquare) this.geoSquare.dispose();
  }
}

// ------------------------------------------------------------------------------------------------ ribbons
const RIBBON_POINTS = 40;

class Ribbon {
  constructor(mesh) {
    this.mesh = mesh;
    this.u = mesh.material.uniforms;
    this.geo = mesh.geometry;
    this.posAttr = this.geo.getAttribute('position');
    this.uvAttr = this.geo.getAttribute('uv');
    this.px = new Float32Array(RIBBON_POINTS); this.py = new Float32Array(RIBBON_POINTS);
    this.pz = new Float32Array(RIBBON_POINTS); this.pt = new Float32Array(RIBBON_POINTS);
    this.n = 0;
    this.active = false;
    this.follow = null;
    this.offset = new THREE.Vector3();
    this.color = new THREE.Color();
    this.width = 0.2; this.life = 0.3; this.opacity = 1; this.core = 0; this.minSeg = 0.08;
  }
}

/** Camera-facing trails from a timed point history (newest first). */
export class RibbonPool {
  constructor(count, renderOrder = 12) {
    this.group = new THREE.Group();
    this.group.name = 'vfx-ribbons';
    this.items = [];
    // shared index buffer layout: quad strip of RIBBON_POINTS pairs
    const idx = [];
    for (let i = 0; i < RIBBON_POINTS - 1; i++) {
      const a = i * 2, b = a + 1, c = a + 2, d = a + 3;
      idx.push(a, b, c, b, d, c);
    }
    for (let i = 0; i < count; i++) {
      const geo = new THREE.BufferGeometry();
      const pos = new THREE.BufferAttribute(new Float32Array(RIBBON_POINTS * 2 * 3), 3); pos.setUsage(THREE.DynamicDrawUsage);
      const uv = new THREE.BufferAttribute(new Float32Array(RIBBON_POINTS * 2 * 2), 2); uv.setUsage(THREE.DynamicDrawUsage);
      geo.setAttribute('position', pos); geo.setAttribute('uv', uv); geo.setIndex(idx);
      geo.setDrawRange(0, 0);
      const mat = makeMaterial('vfx-ribbon', RIBBON_VERT, RIBBON_FRAG, {
        uColor: { value: new THREE.Color() }, uOpacity: { value: 1 }, uCore: { value: 0 },
      }, true);
      const mesh = new THREE.Mesh(geo, mat);
      mesh.frustumCulled = false; mesh.visible = false; mesh.renderOrder = renderOrder;
      mesh.castShadow = false; mesh.receiveShadow = false;
      excludeFromOverridePasses(mesh);
      this.group.add(mesh);
      this.items.push(new Ribbon(mesh));
    }
  }

  /**
   * @param {{pos:THREE.Vector3, active:boolean}} follow emitter
   * @param {THREE.Color} color linear HDR
   * @param {number} width metres
   * @param {number} life seconds a point survives
   * @param {number} opacity
   * @param {number} core white-hot core intensity
   * @returns {Ribbon|null}
   */
  spawn(follow, color, width, life, opacity = 1, core = 0) {
    let r = null, oldest = Infinity;
    for (const it of this.items) {
      if (!it.active) { r = it; break; }
      if (!it.follow && it.pt[0] < oldest) { oldest = it.pt[0]; r = it; }
    }
    if (!r) return null;
    r.active = true; r.follow = follow; r.n = 0;
    r.color.copy(color); r.width = width; r.life = Math.max(0.05, life); r.opacity = opacity; r.core = core;
    r.minSeg = Math.max(0.03, width * 0.35);
    r.geo.setDrawRange(0, 0);
    return r;
  }

  static release(r) { if (r) r.follow = null; }

  clear() { for (const it of this.items) { it.active = false; it.follow = null; it.n = 0; it.mesh.visible = false; } }

  /** @param {THREE.Vector3} camPos camera world position (ribbons face it) */
  update(dt, time, camPos) {
    for (const r of this.items) {
      if (!r.active) continue;
      // push the head point
      if (r.follow && r.follow.active && !r.follow.paused) {
        const p = r.follow.pos;
        // index 0 is the live head; index 1 the newest committed point. Commit when the head is minSeg away.
        const moved = r.n < 2 ? Infinity : Math.hypot(p.x - r.px[1], p.y - r.py[1], p.z - r.pz[1]);
        if (moved >= r.minSeg) {
          // shift history (newest at index 0)
          const n = Math.min(r.n + 1, RIBBON_POINTS);
          r.px.copyWithin(1, 0, n - 1); r.py.copyWithin(1, 0, n - 1); r.pz.copyWithin(1, 0, n - 1); r.pt.copyWithin(1, 0, n - 1);
          r.n = n;
        }
        r.px[0] = p.x; r.py[0] = p.y; r.pz[0] = p.z; r.pt[0] = time;
      } else if (r.follow && !r.follow.active) r.follow = null;
      // age out the tail
      while (r.n > 0 && time - r.pt[r.n - 1] > r.life) r.n--;
      if (r.n < 2) {
        r.mesh.visible = false;
        if (!r.follow) r.active = false;
        continue;
      }
      this._build(r, time, camPos);
    }
  }

  _build(r, time, cam) {
    const pos = r.posAttr.array, uv = r.uvAttr.array;
    let sx = 0, sy = 1, sz = 0; // last valid side vector
    for (let i = 0; i < r.n; i++) {
      const a = Math.max(0, i - 1), b = Math.min(r.n - 1, i + 1);
      const tx = r.px[a] - r.px[b], ty = r.py[a] - r.py[b], tz = r.pz[a] - r.pz[b];
      const vx = cam.x - r.px[i], vy = cam.y - r.py[i], vz = cam.z - r.pz[i];
      // side = tangent x view
      let cx = ty * vz - tz * vy, cy = tz * vx - tx * vz, cz = tx * vy - ty * vx;
      const len = Math.hypot(cx, cy, cz);
      if (len > 1e-6) { cx /= len; cy /= len; cz /= len; sx = cx; sy = cy; sz = cz; } else { cx = sx; cy = sy; cz = sz; }
      const age = Math.min(1, (time - r.pt[i]) / r.life);
      const w = r.width * 0.5 * Math.sqrt(1 - age * 0.85);
      const o = i * 6;
      pos[o] = r.px[i] + cx * w; pos[o + 1] = r.py[i] + cy * w; pos[o + 2] = r.pz[i] + cz * w;
      pos[o + 3] = r.px[i] - cx * w; pos[o + 4] = r.py[i] - cy * w; pos[o + 5] = r.pz[i] - cz * w;
      const u = i * 4;
      uv[u] = age; uv[u + 1] = 0; uv[u + 2] = age; uv[u + 3] = 1;
    }
    r.posAttr.clearUpdateRanges(); r.posAttr.addUpdateRange(0, r.n * 6); r.posAttr.needsUpdate = true;
    r.uvAttr.clearUpdateRanges(); r.uvAttr.addUpdateRange(0, r.n * 4); r.uvAttr.needsUpdate = true;
    r.geo.setDrawRange(0, (r.n - 1) * 6);
    r.u.uColor.value.copy(r.color); r.u.uOpacity.value = r.opacity; r.u.uCore.value = r.core;
    r.mesh.visible = true;
  }

  dispose() {
    this.group.removeFromParent();
    for (const it of this.items) { it.geo.dispose(); it.mesh.material.dispose(); }
  }
}

// ------------------------------------------------------------------------------------------------ lights
class LightSlot {
  constructor(light) {
    this.light = light;
    this.active = false; this.t = 0; this.duration = 0.2; this.peak = 0; this.follow = null; this.flicker = 0;
    this.sustain = false; this.fadeOut = 0.15; this.releaseT = -1; this.level = 0;
  }
}

/** A handful of shadowless point lights reused for flashes and moving glows. */
export class LightPool {
  constructor(count) {
    this.group = new THREE.Group();
    this.group.name = 'vfx-lights';
    this.items = [];
    for (let i = 0; i < count; i++) {
      const l = new THREE.PointLight(0xffffff, 0, 8, 2);
      l.castShadow = false;
      this.group.add(l);
      this.items.push(new LightSlot(l));
    }
  }

  _acquire() {
    let best = null, lowest = Infinity;
    for (const s of this.items) {
      if (!s.active) return s;
      const lvl = s.sustain && s.follow ? Infinity : s.level;
      if (lvl < lowest) { lowest = lvl; best = s; }
    }
    return best;
  }

  /**
   * One-shot flash with a quadratic decay.
   * @param {THREE.Vector3} pos @param {THREE.Color} color @param {number} intensity candela @param {number} duration s
   * @param {number} distance cutoff (m)
   */
  flash(pos, color, intensity, duration, distance = 8) {
    const s = this._acquire();
    if (!s) return null;
    s.active = true; s.t = 0; s.duration = Math.max(0.02, duration); s.peak = intensity; s.follow = null; s.flicker = 0;
    s.sustain = false; s.level = intensity; s.releaseT = -1;
    s.light.position.copy(pos); s.light.color.copy(color); s.light.distance = distance; s.light.intensity = intensity;
    return s;
  }

  /** Sustained glow that follows an emitter until released. */
  attach(follow, color, intensity, distance = 6, flicker = 0) {
    const s = this._acquire();
    if (!s) return null;
    s.active = true; s.t = 0; s.duration = Infinity; s.peak = intensity; s.follow = follow; s.flicker = flicker;
    s.sustain = true; s.fadeOut = 0.15; s.level = 0; s.releaseT = -1;
    s.light.color.copy(color); s.light.distance = distance; s.light.intensity = 0;
    if (follow) s.light.position.copy(follow.pos);
    return s;
  }

  static release(s) {
    if (!s || !s.sustain) return;
    s.sustain = false; s.follow = null; s.duration = s.t + s.fadeOut; s.peak = s.level;
    s.releaseT = s.t;
  }

  clear() { for (const s of this.items) { s.active = false; s.follow = null; s.light.intensity = 0; } }

  update(dt, time) {
    for (const s of this.items) {
      if (!s.active) continue;
      s.t += dt;
      if (s.sustain) {
        if (s.follow && !s.follow.active) { LightPool.release(s); continue; }
        if (s.follow) s.light.position.copy(s.follow.pos);
        if (s.follow && s.follow.paused) { s.level = 0; s.light.intensity = 0; continue; }
        // quick fade-in, optional fire flicker (two incommensurate sines + a faster jitter)
        const fin = Math.min(1, s.t / 0.06);
        const fl = s.flicker > 0 ? 1 - s.flicker * (0.5 + 0.25 * Math.sin(time * 23.0 + s.t) + 0.25 * Math.sin(time * 37.7)) : 1;
        s.level = s.peak * fin * fl;
      } else if (s.releaseT >= 0) {
        const k = 1 - Math.min(1, (s.t - s.releaseT) / Math.max(0.01, s.duration - s.releaseT));
        s.level = s.peak * k;
        if (k <= 0) { s.active = false; s.releaseT = -1; }
      } else {
        const k = 1 - Math.min(1, s.t / s.duration);
        s.level = s.peak * k * k;
        if (k <= 0) s.active = false;
      }
      s.light.intensity = s.active ? s.level : 0;
    }
  }

  dispose() { this.group.removeFromParent(); for (const s of this.items) s.light.dispose(); }
}
