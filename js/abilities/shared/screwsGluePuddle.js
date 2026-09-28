// ---------------------------------------------------------------------------------------------------------------
// Screws - Glue Trap puddle (黏膠陷阱). A 2 m, 4 s pool of industrial glue on the court floor: glossy dark-green,
// viscous (clear-coated physical material with a slowly churning procedural normal map, breathing blobs), spreading in
// with a viscous overshoot when it lands and shrinking/drying out when it expires.
// Gameplay: grounded ENEMIES of Screws inside it are slowed by 60% (Status 'slow', refreshed while they stand in it,
// lingering 0.3 s after they step out - sticky soles). Players jumping over it are not caught.
// Lifetime on the scaled clock (hitstop/pause freeze it); animation on real time.
// ---------------------------------------------------------------------------------------------------------------
import * as THREE from 'three';
import { game } from '../../game.js';
import { blobRadius } from './screwsGadgetMath.js';
import {
  fx, sfx, sceneRoot, addWorldObject, removeWorldObject, disposeObject3D, noiseNormalMap, sharedGeometry, isAlive,
  isGrounded,
} from './screwsGadgetKit.js';

export const GLUE_DEFAULTS = Object.freeze({
  radius: 2,             // m (spec)
  duration: 4,           // s (spec, scaled clock)
  slow: 0.6,             // 60% slow (spec) - Status 'slow' magnitude
  linger: 0.3,           // s the slow persists after stepping out
  reapply: 0.15,         // s between Status refreshes per player
  spreadTime: 0.3,       // real s
  fadeTime: 0.45,        // s at the end of the lifetime (shrinks + fades)
  wobble: 0.12,          // outline irregularity (fraction of the radius)
  blobs: 6,
  color: 0x22410f,       // dark industrial green
});

const _pos = new THREE.Vector3();

/** One glue pool. Spawned by the Glue Trap Ball payload; tracked and cleared by the ability. */
export class ScrewsGluePuddle {
  /**
   * @param {import('../../gameplay/player.js').Player} owner Screws
   * @param {THREE.Vector3} center floor point
   * @param {object} params
   */
  constructor(owner, center, params = {}) {
    this.owner = owner;
    this.team = owner.team;
    this.p = { ...GLUE_DEFAULTS, ...params };
    this.center = new THREE.Vector3(center.x, game.court ? game.court.floorY : 0, center.z);
    this.life = this.p.duration;
    this.age = 0;           // real seconds since spawn (animation)
    this.alive = true;
    this.onExpired = null;  // callback(puddle)
    this._stuck = new Map(); // player -> scaled time of the next Status refresh
    this._seed = Math.floor(Math.random() * 1000);
    this._build();
    addWorldObject(this);
    fx('glueSplat', this.center, { scale: this.p.radius / 2, color: this.p.color });
    sfx('glueSplat', this.center, 1, 1);
  }

  /** Current effective radius (grows while spreading, shrinks while drying). */
  get effectiveRadius() { return this.p.radius * this._scale; }

  contains(pos) {
    const dx = pos.x - this.center.x, dz = pos.z - this.center.z;
    const r = this.effectiveRadius;
    return dx * dx + dz * dz <= r * r;
  }

  update(dt, realDt) {
    if (!this.alive) return;
    this.age += realDt;
    this.life -= dt;
    const p = this.p;

    // Spread: viscous ease-out with a small overshoot; dry out: shrink to 85% and fade.
    const s = Math.min(1, this.age / p.spreadTime);
    const spread = s < 1 ? 1 - Math.pow(1 - s, 3) * Math.cos(s * 2.2) : 1;
    const dry = this.life < p.fadeTime ? Math.max(0, this.life / p.fadeTime) : 1;
    this._scale = Math.max(0.05, spread * (0.85 + 0.15 * dry));
    this.root.scale.set(this._scale, 1, this._scale);
    this.mat.opacity = 0.95 * dry;

    // Churning surface: the normal map slowly rotates and drifts, its strength breathes.
    const t = game.time.realNow;
    this.normalTex.rotation = t * 0.05 + this._seed;
    this.normalTex.offset.set(Math.sin(t * 0.21 + this._seed) * 0.15, Math.cos(t * 0.17) * 0.15);
    const ns = 0.45 + 0.12 * Math.sin(t * 1.3 + this._seed);
    this.mat.normalScale.set(ns, ns);
    for (let i = 0; i < this.blobs.length; i++) {
      const b = this.blobs[i];
      const k = 1 + 0.18 * Math.sin(t * (0.9 + i * 0.37) + i * 2.1);
      b.scale.set(b.userData.r * k, b.userData.h * (2 - k), b.userData.r * k);
    }

    if (this.life <= 0) { this.expire(); return; }
    this._applySlow();
  }

  _applySlow() {
    const now = game.time.now;
    const players = game.players;
    for (let i = 0; i < players.length; i++) {
      const pl = players[i];
      if (!pl || pl.team === this.team || pl.team < 0 || !isAlive(pl) || !pl.status || !pl.status.apply) continue;
      if (!isGrounded(pl) || !this.contains(pl.position)) continue;
      const next = this._stuck.get(pl);
      if (next === undefined) {
        // First contact: a wet splat under the feet.
        _pos.set(pl.position.x, this.center.y + 0.02, pl.position.z);
        fx('glueSplat', _pos, { scale: 0.35, color: this.p.color });
        sfx('glueStick', _pos, 0.8, 0.9 + Math.random() * 0.2);
      }
      if (next === undefined || now >= next) {
        pl.status.apply('slow', this.p.linger + this.p.reapply, this.p.slow, this);
        this._stuck.set(pl, now + this.p.reapply);
      }
    }
  }

  /** Ends the puddle now (lifetime over or cleared by the round). */
  expire() {
    if (!this.alive) return;
    this.alive = false;
    removeWorldObject(this);
    disposeObject3D(this.root);
    this.normalTex?.dispose(); // releases this clone's reference to the shared GPU image
    this._stuck.clear();
    const cb = this.onExpired;
    this.onExpired = null;
    if (cb) cb(this);
  }

  _build() {
    const p = this.p;
    const shape = new THREE.Shape();
    const N = 56;
    for (let i = 0; i <= N; i++) {
      const th = (i / N) * Math.PI * 2;
      const r = blobRadius(th, p.radius, p.wobble, this._seed);
      const x = Math.cos(th) * r, y = Math.sin(th) * r;
      if (i === 0) shape.moveTo(x, y); else shape.lineTo(x, y);
    }
    const geo = new THREE.ShapeGeometry(shape, 1);
    geo.rotateX(-Math.PI / 2); // lie flat; ShapeGeometry UVs are the shape coordinates in metres

    // Per-puddle clone so rotation/offset animate independently (the image source is shared on the GPU).
    this.normalTex = noiseNormalMap('glueSurface', { size: 128, period: 6, octaves: 4, strength: 3.5, seed: 17 }).clone();
    this.normalTex.repeat.set(0.55, 0.55);
    this.normalTex.center.set(0.5, 0.5);
    this.mat = new THREE.MeshPhysicalMaterial({
      color: p.color, roughness: 0.12, metalness: 0, clearcoat: 1, clearcoatRoughness: 0.04, specularIntensity: 1,
      ior: 1.47, normalMap: this.normalTex, normalScale: new THREE.Vector2(0.5, 0.5), sheen: 0.25,
      sheenColor: new THREE.Color(0x5f8f3a), sheenRoughness: 0.4, transparent: true, opacity: 0.95, depthWrite: false,
      polygonOffset: true, polygonOffsetFactor: -2, polygonOffsetUnits: -2,
    });

    const root = this.root = new THREE.Group();
    root.name = 'ScrewsGluePuddle';
    root.position.set(this.center.x, this.center.y + 0.006, this.center.z);
    const disc = new THREE.Mesh(geo, this.mat);
    disc.receiveShadow = true;
    disc.renderOrder = 2;
    root.add(disc);

    // Viscous blobs standing proud of the surface (flattened hemispheres sharing the glue material).
    const blobGeo = sharedGeometry('glueBlob', () => new THREE.SphereGeometry(1, 16, 8, 0, Math.PI * 2, 0, Math.PI / 2));
    this.blobs = [];
    for (let i = 0; i < p.blobs; i++) {
      const a = (i / p.blobs) * Math.PI * 2 + this._seed;
      const d = p.radius * (0.2 + 0.5 * ((i * 0.618 + this._seed * 0.01) % 1));
      const b = new THREE.Mesh(blobGeo, this.mat);
      b.userData.r = 0.07 + 0.08 * ((i * 0.37) % 1);
      b.userData.h = 0.025 + 0.02 * ((i * 0.53) % 1);
      b.position.set(Math.cos(a) * d, 0, Math.sin(a) * d);
      b.scale.set(b.userData.r, b.userData.h, b.userData.r);
      b.renderOrder = 2;
      root.add(b);
      this.blobs.push(b);
    }
    this._scale = 0.05;
    root.scale.set(this._scale, 1, this._scale);
    sceneRoot()?.add(root);
  }
}
