// ---------------------------------------------------------------------------------------------------------------
// BallTrail - a camera-facing ribbon behind a ball, one draw call, allocation-free per frame.
//
// The ball's rendered position is sampled every frame into a short history (newest first). Each sample ages on the
// SCALED clock (so a hitstop freezes the streak together with the ball); samples older than the style's lifetime
// drop off the tail. Every sample becomes two vertices offset along cross(tangent, toCamera): the ribbon always
// faces the viewer. Width tapers toward the tail, alpha fades with age^1.6, and both scale with ball speed, so a
// lazy lob leaves nothing while a 200 km/h rally ball draws a crisp streak. Colour/blend per ball style:
//   standard  faint warm-white air streak (only fast balls)     meteor  fire (additive, HDR -> bloom)
//   beam      cyan-white energy (additive, long)                glue    translucent green
//   freeze    icy blue (additive)                               turret  amber tracer
// ---------------------------------------------------------------------------------------------------------------
import * as THREE from 'three';

/** Trail look per ball style. color is linear RGB and may exceed 1 (HDR, picked up by bloom). */
export const TRAIL_STYLES = Object.freeze({
  standard: { color: [1.0, 0.96, 0.9], alpha: 0.22, width: 0.07, life: 0.14, minSpeed: 16, fullSpeed: 40, additive: false },
  meteor: { color: [2.6, 1.05, 0.25], alpha: 0.95, width: 0.22, life: 0.3, minSpeed: 0, fullSpeed: 18, additive: true },
  beam: { color: [1.3, 2.4, 2.8], alpha: 1.0, width: 0.17, life: 0.42, minSpeed: 0, fullSpeed: 18, additive: true },
  glue: { color: [0.42, 0.85, 0.18], alpha: 0.5, width: 0.1, life: 0.2, minSpeed: 3, fullSpeed: 20, additive: false },
  freeze: { color: [0.9, 1.6, 2.4], alpha: 0.75, width: 0.15, life: 0.34, minSpeed: 0, fullSpeed: 18, additive: true },
  turret: { color: [2.0, 1.4, 0.45], alpha: 0.55, width: 0.08, life: 0.16, minSpeed: 6, fullSpeed: 30, additive: true },
});

const MAX_POINTS = 40;
/** Minimum distance (m) between stored samples; closer frames just move the head sample. */
const MIN_SPACING = 0.035;

const _cam = new THREE.Vector3();
const _tan = new THREE.Vector3();
const _view = new THREE.Vector3();
const _side = new THREE.Vector3();

export class BallTrail {
  constructor() {
    this.count = 0;
    this.px = new Float32Array(MAX_POINTS);
    this.py = new Float32Array(MAX_POINTS);
    this.pz = new Float32Array(MAX_POINTS);
    this.age = new Float32Array(MAX_POINTS);
    this.style = TRAIL_STYLES.standard;
    this.intensity = 0;

    const geo = new THREE.BufferGeometry();
    this.positions = new Float32Array(MAX_POINTS * 2 * 3);
    this.colors = new Float32Array(MAX_POINTS * 2 * 4);
    const pos = new THREE.BufferAttribute(this.positions, 3).setUsage(THREE.DynamicDrawUsage);
    const col = new THREE.BufferAttribute(this.colors, 4).setUsage(THREE.DynamicDrawUsage);
    geo.setAttribute('position', pos);
    geo.setAttribute('color', col);
    const index = [];
    for (let i = 0; i < MAX_POINTS - 1; i++) {
      const a = i * 2, b = a + 1, c = a + 2, d = a + 3;
      index.push(a, b, c, b, d, c);
    }
    geo.setIndex(index);
    geo.setDrawRange(0, 0);
    this.geometry = geo;
    this.material = new THREE.MeshBasicMaterial({
      vertexColors: true, transparent: true, depthWrite: false, side: THREE.DoubleSide, fog: true,
    });
    this.mesh = new THREE.Mesh(geo, this.material);
    this.mesh.name = 'BallTrail';
    this.mesh.frustumCulled = false;   // vertices live in world space and change every frame
    this.mesh.renderOrder = 2;
    this.mesh.castShadow = false;
    this.mesh.receiveShadow = false;
    this.mesh.visible = false;
    this.setStyle('standard');
  }

  setStyle(style) {
    const s = TRAIL_STYLES[style] || TRAIL_STYLES.standard;
    if (s === this.style && this.material.userData.styled) return;
    this.style = s;
    this.material.blending = s.additive ? THREE.AdditiveBlending : THREE.NormalBlending;
    this.material.userData.styled = true;
  }

  /** Forget the history (teleports, catches, respawns) so no streak is drawn across the jump. */
  reset() {
    this.count = 0;
    this.geometry.setDrawRange(0, 0);
    this.mesh.visible = false;
  }

  /**
   * @param {number} dt scaled seconds (ages the samples)
   * @param {THREE.Vector3} head rendered ball position
   * @param {number} speed ball speed (m/s)
   * @param {boolean} emitting whether the ball should currently leave a streak
   * @param {THREE.Camera|null} camera
   * @param {number} [widthScale] ball size factor
   */
  update(dt, head, speed, emitting, camera, widthScale = 1) {
    const s = this.style;
    // Age and drop expired samples.
    for (let i = 0; i < this.count; i++) this.age[i] += dt;
    while (this.count > 0 && this.age[this.count - 1] > s.life) this.count--;

    // Speed-driven intensity (smoothed so the streak does not pop).
    const target = emitting ? Math.min(1, Math.max(0, (speed - s.minSpeed) / Math.max(1e-3, s.fullSpeed - s.minSpeed))) : 0;
    const k = 1 - Math.exp(-dt * 18);
    this.intensity += (target - this.intensity) * (dt > 0 ? k : 0);

    if (emitting && this.intensity > 0.01) {
      const moved = this.count === 0 ? Infinity :
        Math.hypot(head.x - this.px[0], head.y - this.py[0], head.z - this.pz[0]);
      if (moved >= MIN_SPACING || this.count < 2) {
        // Push a new head sample (shift history by one).
        const n = Math.min(this.count, MAX_POINTS - 1);
        this.px.copyWithin(1, 0, n); this.py.copyWithin(1, 0, n); this.pz.copyWithin(1, 0, n); this.age.copyWithin(1, 0, n);
        this.count = n + 1;
      }
      this.px[0] = head.x; this.py[0] = head.y; this.pz[0] = head.z; this.age[0] = 0;
    }

    if (this.count < 2 || this.intensity <= 0.01) {
      this.mesh.visible = false;
      this.geometry.setDrawRange(0, 0);
      if (!emitting && this.intensity <= 0.01) this.count = 0;
      return;
    }

    if (camera) camera.getWorldPosition(_cam); else _cam.set(0, 10, 20);
    const P = this.positions, C = this.colors;
    const halfW = 0.5 * s.width * widthScale * (0.55 + 0.45 * this.intensity);
    const [cr, cg, cb] = s.color;
    for (let i = 0; i < this.count; i++) {
      const i0 = i > 0 ? i - 1 : i, i1 = i < this.count - 1 ? i + 1 : i;
      _tan.set(this.px[i0] - this.px[i1], this.py[i0] - this.py[i1], this.pz[i0] - this.pz[i1]);
      _view.set(_cam.x - this.px[i], _cam.y - this.py[i], _cam.z - this.pz[i]);
      _side.crossVectors(_tan, _view);
      const sl = _side.length();
      if (sl > 1e-8) _side.multiplyScalar(1 / sl); else _side.set(0, 1, 0);
      const a = Math.min(1, this.age[i] / s.life);
      const w = halfW * (1 - 0.85 * a);
      const alpha = s.alpha * this.intensity * Math.pow(1 - a, 1.6);
      const o = i * 6;
      P[o] = this.px[i] + _side.x * w; P[o + 1] = this.py[i] + _side.y * w; P[o + 2] = this.pz[i] + _side.z * w;
      P[o + 3] = this.px[i] - _side.x * w; P[o + 4] = this.py[i] - _side.y * w; P[o + 5] = this.pz[i] - _side.z * w;
      // Additive: premultiply so the tail fades to black; normal blending uses the alpha channel.
      const m = s.additive ? alpha : 1;
      const c = i * 8;
      C[c] = cr * m; C[c + 1] = cg * m; C[c + 2] = cb * m; C[c + 3] = alpha;
      C[c + 4] = cr * m; C[c + 5] = cg * m; C[c + 6] = cb * m; C[c + 7] = alpha;
    }
    this.geometry.attributes.position.needsUpdate = true;
    this.geometry.attributes.color.needsUpdate = true;
    this.geometry.setDrawRange(0, (this.count - 1) * 6);
    this.mesh.visible = true;
  }

  dispose() {
    this.mesh.removeFromParent();
    this.geometry.dispose();
    this.material.dispose();
  }
}
