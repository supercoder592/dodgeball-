// ---------------------------------------------------------------------------------------------------------------
// Bear - Aegis Barrier wall (神盾屏障). A 3.2 m energy wall spanning the court width just inside Bear's half of the
// centre line, projected from a floor emitter rail between two sideline pylons.
//   Gameplay: registered in game.hittables. Enemy live balls -> 'block' (a ripple spreads from the hit point), Bear's
//   team's balls -> 'pass'. Unblockable balls (Hyperbeam) pierce it (ripple only). Lobs above 3.2 m clear it.
//   Look: layered hexagonal energy lattice (ShaderMaterial, additive) with a fresnel rim, rising scan sweep, fine
//   scan-lines, per-cell flicker, bright emitter glow at the base and travelling hit ripples that warp the lattice.
// Lifetime: deploy() rises in 0.35 s (scaled - it only blocks once risen past 35%), retract() stops blocking at once
// and fades the energy out on real time, then the object disposes itself.
// ---------------------------------------------------------------------------------------------------------------
import * as THREE from 'three';
import { game } from '../../game.js';
import { TEAM } from '../../core/constants.js';
import { sweptPointVsAabb } from './screwsGadgetMath.js';
import {
  throwerTeam, teamColor, fx, sfx, sceneRoot, addWorldObject, removeWorldObject, disposeObject3D, gadgetMaterial,
  sharedGeometry,
} from './screwsGadgetKit.js';

export const AEGIS_DEFAULTS = Object.freeze({
  height: 3.2,            // m (spec)
  thickness: 0.14,        // m: collision slab
  offset: 0.9,            // m inside Bear's half from the centre line (enemies stay >= 0.35 m on their side)
  sideOverhang: 0.25,     // m past each sideline
  riseTime: 0.35,         // s (scaled)
  solidFrom: 0.35,        // fraction of the rise at which the wall starts blocking
  fadeTime: 0.45,         // real s
  hexScale: 2.4,          // hex cells per metre
  maxRipples: 6,          // must match the shader array size
  rippleLife: 1.2,        // real s (must match RIPPLE_LIFE in the shader)
  pylonHeight: 0.95,      // m
  intensity: 1,
});

const MAX_RIPPLES = 6;

const WALL_VERT = /* glsl */`
varying vec2 vUv;
varying vec3 vWorldPos;
varying vec3 vNormalW;
void main() {
  vUv = uv;
  vec4 wp = modelMatrix * vec4(position, 1.0);
  vWorldPos = wp.xyz;
  vNormalW = normalize(mat3(modelMatrix) * normal);
  gl_Position = projectionMatrix * viewMatrix * wp;
}`;

const WALL_FRAG = /* glsl */`
uniform float uTime;
uniform float uOpacity;
uniform float uRise;
uniform float uHexScale;
uniform float uIntensity;
uniform float uLayer;
uniform vec2 uSize;
uniform vec3 uColor;
uniform vec3 uCoreColor;
uniform vec4 uRipples[${MAX_RIPPLES}];
varying vec2 vUv;
varying vec3 vWorldPos;
varying vec3 vNormalW;

const float RIPPLE_LIFE = 1.2;
const float RIPPLE_SPEED = 4.5;

float hash21(vec2 p) {
  p = fract(p * vec2(123.34, 456.21));
  p += dot(p, p + 45.32);
  return fract(p.x * p.y);
}
float hexDist(vec2 p) {
  p = abs(p);
  return max(dot(p, normalize(vec2(1.0, 1.7320508))), p.x);
}
// xy: offset from the cell centre, zw: cell id
vec4 hexCoords(vec2 uv) {
  const vec2 r = vec2(1.0, 1.7320508);
  vec2 h = r * 0.5;
  vec2 a = mod(uv, r) - h;
  vec2 b = mod(uv - h, r) - h;
  vec2 gv = dot(a, a) < dot(b, b) ? a : b;
  return vec4(gv, uv - gv);
}

void main() {
  vec2 p = vUv * uSize;                         // metres across / up the wall
  float riseH = uRise * uSize.y;
  if (p.y > riseH + 0.02) discard;

  // Hit ripples: an expanding ring + a hot core at the impact, warping the lattice.
  float rip = 0.0;
  vec2 warp = vec2(0.0);
  for (int i = 0; i < ${MAX_RIPPLES}; i++) {
    vec4 r = uRipples[i];
    float age = uTime - r.z;
    if (r.w <= 0.0 || age < 0.0 || age > RIPPLE_LIFE) continue;
    vec2 d = p - r.xy;
    float dist = length(d);
    float fade = 1.0 - age / RIPPLE_LIFE;
    fade *= fade;
    float ring = exp(-pow((dist - age * RIPPLE_SPEED) * 3.2, 2.0)) * fade;
    float core = exp(-dist * dist * 5.0) * exp(-age * 7.0);
    rip += (ring * 0.9 + core * 1.6) * r.w;
    warp += (d / max(dist, 1e-3)) * ring * 0.10 * r.w;
  }

  vec4 hc = hexCoords((p + warp) * uHexScale + vec2(uLayer * 0.37, uLayer * 0.61));
  float e = 0.5 - hexDist(hc.xy);                // 0 on the cell border
  float edge = 1.0 - smoothstep(0.015, 0.06, e);
  float cell = hash21(hc.zw + uLayer);
  float cellPulse = pow(0.5 + 0.5 * sin(uTime * (1.3 + cell * 2.2) + cell * 40.0), 10.0);

  float scan = 0.5 + 0.5 * sin(p.y * 60.0 - uTime * 8.0);
  scan = scan * scan * 0.07;
  float sweepPos = fract(uTime * 0.32 + uLayer * 0.5) * (uSize.y + 1.0) - 0.5;
  float sweep = exp(-pow((p.y - sweepPos) * 4.0, 2.0)) * 0.35;
  float base = exp(-p.y * 2.4) * 0.85;
  float topFade = 1.0 - smoothstep(uSize.y - 0.45, uSize.y, p.y);
  float sideFade = smoothstep(0.0, 0.3, p.x) * smoothstep(0.0, 0.3, uSize.x - p.x);
  float front = exp(-pow((riseH - p.y) * 7.0, 2.0)) * step(uRise, 0.999) * 1.4; // leading edge while rising

  vec3 N = normalize(vNormalW) * (gl_FrontFacing ? 1.0 : -1.0);
  vec3 V = normalize(cameraPosition - vWorldPos);
  float fres = pow(1.0 - abs(dot(N, V)), 3.0);

  float a = 0.045 + edge * (0.42 + 0.5 * cellPulse) + cellPulse * 0.12 + scan + sweep + base + fres * 0.55 + rip + front;
  a *= topFade * sideFade * uOpacity * uIntensity;
  vec3 col = mix(uColor, uCoreColor, clamp(rip * 0.6 + front * 0.5 + base * 0.35 + edge * cellPulse * 0.4, 0.0, 1.0));
  gl_FragColor = vec4(col * 1.8, clamp(a, 0.0, 1.0));
  #include <tonemapping_fragment>
  #include <colorspace_fragment>
}`;

const _min = new THREE.Vector3();
const _max = new THREE.Vector3();
const _n = { x: 0, y: 0, z: 0 };

export class BearAegisWall {
  /** @param {import('../../gameplay/player.js').Player} owner @param {object} params */
  constructor(owner, params = {}) {
    this.owner = owner;
    /** Hittable contract: team whose balls pass. */
    this.team = owner.team;
    this.p = { ...AEGIS_DEFAULTS, ...params };
    this.state = 'idle'; // idle | rising | active | fading | disposed
    this.blocks = 0;
    this.root = null;
    this._rise = 0;
    this._fade = 1;
    this._hw = 0;          // hardware scale-in 0..1
    this._ripIndex = 0;
    this._layers = [];
    this._result = { t: 0, point: new THREE.Vector3(), normal: new THREE.Vector3() };
    this._colorHex = teamColor(this.team, 0xffc85a).getHex();
  }

  /** True while enemy balls are stopped. */
  get solid() {
    return (this.state === 'rising' && this._rise >= this.p.solidFrom) || this.state === 'active';
  }

  deploy() {
    if (this.state !== 'idle') return;
    const court = game.court;
    const halfW = court ? court.halfW : 4.5;
    const side = court ? court.sideSign(this.team) : (this.team === TEAM.HOME ? -1 : 1);
    this.floorY = court ? court.floorY : 0;
    this.width = 2 * (halfW + this.p.sideOverhang);
    this.z = side * this.p.offset;
    this._build();
    game.hittables?.add?.(this);
    addWorldObject(this);
    this.state = 'rising';
    this._rise = 0;
    _min.set(0, this.floorY + 0.05, this.z);
    fx('shockwave', _min, { scale: 0.8, color: this._colorHex });
    sfx('aegisDeploy', _min, 1, 1);
  }

  /** Stops blocking immediately and fades out (then disposes itself). */
  retract() {
    if (this.state === 'idle' || this.state === 'disposed' || this.state === 'fading') return;
    game.hittables?.delete?.(this);
    this.state = 'fading';
    _min.set(0, this.floorY + 1, this.z);
    sfx('aegisDown', _min, 0.8, 1);
  }

  dispose() {
    if (this.state === 'disposed') return;
    game.hittables?.delete?.(this);
    removeWorldObject(this);
    disposeObject3D(this.root);
    this.root = null;
    this._layers = [];
    this.state = 'disposed';
  }

  // ------------------------------------------------------------------ hittable contract

  /** Swept sphere vs the wall slab. @returns {{t, point, normal}|null} */
  intersect(from, to, radius) {
    if (!this.solid) return null;
    const hz = this.p.thickness * 0.5 + radius;
    const z0 = this.z - hz, z1 = this.z + hz;
    if ((from.z < z0 && to.z < z0) || (from.z > z1 && to.z > z1)) return null; // quick reject
    const hw = this.width * 0.5;
    _min.set(-hw - radius, this.floorY - radius, z0);
    _max.set(hw + radius, this.floorY + this.p.height * this._rise + radius, z1);
    const t = sweptPointVsAabb(from, to, _min, _max, _n);
    if (t < 0) return null;
    const r = this._result;
    r.t = t;
    r.point.lerpVectors(from, to, t);
    r.normal.set(_n.x, _n.y, _n.z);
    // Ripple on the wall face, not at the ball centre.
    r.point.z = this.z + Math.sign(r.normal.z || (from.z - this.z) || 1) * this.p.thickness * 0.5;
    return r;
  }

  /** @returns {'pass'|'block'} */
  onBallHit(ball, hit) {
    const team = throwerTeam(ball);
    const point = (hit && hit.point) || ball.position;
    if (team === this.team || team === TEAM.NONE) return 'pass';
    const speed = ball.velocity ? ball.velocity.length() : 20;
    if (ball.unblockable) {
      this.addRipple(point, 0.9);
      sfx('shieldImpact', point, 0.6, 0.7);
      return 'pass';
    }
    this.addRipple(point, Math.min(1.6, Math.max(0.6, speed / 22)));
    fx('shieldImpact', point, { direction: hit && hit.normal, color: this._colorHex, scale: 1 });
    sfx('shieldImpact', point, 1, 1);
    this.blocks++;
    return 'block';
  }

  /** Adds a travelling ripple at a world point on the wall. */
  addRipple(worldPoint, strength = 1) {
    if (!this._layers.length) return;
    const x = worldPoint.x + this.width * 0.5;
    const y = worldPoint.y - this.floorY;
    const i = this._ripIndex = (this._ripIndex + 1) % MAX_RIPPLES;
    const now = game.time.realNow;
    for (const m of this._layers) m.material.uniforms.uRipples.value[i].set(x, y, now, strength);
  }

  // ------------------------------------------------------------------ system: visuals + rise (scaled) / fade (real)

  update(dt, realDt) {
    if (this.state === 'idle' || this.state === 'disposed') return;
    if (this.state === 'rising') {
      this._rise = Math.min(1, this._rise + dt / this.p.riseTime);
      if (this._rise >= 1) this.state = 'active';
    } else if (this.state === 'fading') {
      this._fade -= realDt / this.p.fadeTime;
      if (this._fade <= 0) { this.dispose(); return; }
    }
    this._hw = Math.min(1, this._hw + realDt / 0.2);
    const ease = 1 - Math.pow(1 - this._hw, 3);
    if (this._hardware) this._hardware.scale.set(1, Math.max(0.001, ease), 1);
    const now = game.time.realNow;
    const flicker = this.state === 'fading' ? 0.8 + 0.2 * Math.sin(now * 60) : 1;
    for (const m of this._layers) {
      const u = m.material.uniforms;
      u.uTime.value = now;
      u.uRise.value = this._rise;
      u.uOpacity.value = Math.max(0, this._fade) * flicker;
    }
  }

  // ------------------------------------------------------------------ build

  _build() {
    const scene = sceneRoot();
    const root = this.root = new THREE.Group();
    root.name = 'BearAegisWall';
    const p = this.p, W = this.width, H = p.height;
    const color = teamColor(this.team, 0xffc85a).clone().lerp(new THREE.Color(0xffffff), 0.2);
    const core = color.clone().lerp(new THREE.Color(0xffffff), 0.75);

    // Two energy layers a few cm apart (different lattice phase) give the field depth.
    for (let layer = 0; layer < 2; layer++) {
      const uniforms = {
        uTime: { value: 0 }, uOpacity: { value: 1 }, uRise: { value: 0 }, uHexScale: { value: p.hexScale * (layer ? 1.6 : 1) },
        uIntensity: { value: p.intensity * (layer ? 0.55 : 1) }, uLayer: { value: layer }, uSize: { value: new THREE.Vector2(W, H) },
        uColor: { value: color.clone() }, uCoreColor: { value: core.clone() },
        uRipples: { value: Array.from({ length: MAX_RIPPLES }, () => new THREE.Vector4(0, 0, -100, 0)) },
      };
      const mat = new THREE.ShaderMaterial({
        uniforms, vertexShader: WALL_VERT, fragmentShader: WALL_FRAG,
        transparent: true, depthWrite: false, blending: THREE.AdditiveBlending, side: THREE.DoubleSide,
      });
      const plane = new THREE.Mesh(new THREE.PlaneGeometry(W, H, 1, 1), mat);
      plane.position.set(0, this.floorY + H * 0.5, this.z + (layer ? 0.045 : -0.045));
      plane.renderOrder = 6 + layer;
      plane.frustumCulled = false;
      root.add(plane);
      this._layers.push(plane);
    }

    // Emitter hardware (realistic machined metal + emissive strips), scaled in from the floor.
    const hw = this._hardware = new THREE.Group();
    hw.position.set(0, this.floorY, this.z);
    const gun = gadgetMaterial('gunmetal'), steel = gadgetMaterial('steel');
    const glow = gadgetMaterial('emissive:' + teamColor(this.team, 0xffc85a).getHex());
    const rail = new THREE.Mesh(new THREE.BoxGeometry(W, 0.055, 0.16), gun);
    rail.position.y = 0.0275;
    rail.receiveShadow = true;
    hw.add(rail);
    const strip = new THREE.Mesh(new THREE.BoxGeometry(W - 0.12, 0.012, 0.032), glow);
    strip.position.y = 0.061;
    hw.add(strip);
    const pylonGeo = sharedGeometry('aegisPylon', () => new THREE.CylinderGeometry(0.075, 0.1, 1, 20));
    const bandGeo = sharedGeometry('aegisBand', () => new THREE.CylinderGeometry(0.079, 0.079, 0.028, 20));
    const capGeo = sharedGeometry('aegisCap', () => new THREE.CylinderGeometry(0.045, 0.06, 0.07, 16));
    for (const sx of [-1, 1]) {
      const x = sx * (W * 0.5);
      const pylon = new THREE.Mesh(pylonGeo, gun);
      pylon.scale.y = p.pylonHeight;
      pylon.position.set(x, p.pylonHeight * 0.5, 0);
      pylon.castShadow = true; pylon.receiveShadow = true;
      hw.add(pylon);
      for (const h of [0.25, 0.5, 0.75]) {
        const band = new THREE.Mesh(bandGeo, glow);
        band.position.set(x, p.pylonHeight * h, 0);
        hw.add(band);
      }
      const cap = new THREE.Mesh(capGeo, steel);
      cap.position.set(x, p.pylonHeight + 0.035, 0);
      cap.castShadow = true;
      hw.add(cap);
    }
    root.add(hw);
    if (scene) scene.add(root);
  }
}
