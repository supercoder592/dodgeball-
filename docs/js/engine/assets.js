// Asset loading with caching: manifest (docs/assets/manifest.json), GLTF models/clips, textures. (kernel)
import * as THREE from 'three';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';

export class Assets {
  constructor(renderer) {
    this.gltfLoader = new GLTFLoader();
    this.textureLoader = new THREE.TextureLoader();
    this.maxAnisotropy = renderer && renderer.capabilities ? renderer.capabilities.getMaxAnisotropy() : 4;
    this._gltf = new Map();
    this._tex = new Map();
    this.manifest = null;
    this.base = 'assets/';
    this.onProgress = null; // (loaded, total, label)
    this._loaded = 0; this._total = 0;
  }

  async loadManifest() {
    const res = await fetch(this.base + 'manifest.json');
    if (!res.ok) throw new Error('assets/manifest.json missing - run Tools/web/build_assets.py');
    this.manifest = await res.json();
    return this.manifest;
  }

  _track(promise, label) {
    this._total++;
    this._report(label);
    return promise.finally(() => { this._loaded++; this._report(label); });
  }
  _report(label) { this.onProgress && this.onProgress(this._loaded, this._total, label); }

  /** Loads (once) and returns the parsed GLTF for a path relative to docs/assets/. */
  gltf(path) {
    if (!this._gltf.has(path)) this._gltf.set(path, this._track(this.gltfLoader.loadAsync(this.base + path), path));
    return this._gltf.get(path);
  }

  /** Cached texture (glTF UV convention: flipY=false). srgb=true for colour maps. */
  texture(path, srgb = false) {
    const key = path + (srgb ? '#srgb' : '');
    if (!this._tex.has(key)) {
      let t;
      const p = new Promise((resolve) => {
        t = this.textureLoader.load(this.base + path, () => resolve(t), undefined, () => { console.warn('[assets] texture failed', path); resolve(t); });
      });
      t.colorSpace = srgb ? THREE.SRGBColorSpace : THREE.NoColorSpace;
      t.flipY = false;
      t.anisotropy = Math.min(8, this.maxAnisotropy);
      this._track(p, path);
      this._tex.set(key, t);
    }
    return this._tex.get(key);
  }

  /** Motion-capture clip for 'm' | 'f' and a semantic key (idle, walk, run, sprint, crouch, stunned, cheer, wave, defeat, breathe, lookaround). */
  async clip(gender, key) {
    const def = this.manifest && this.manifest.clips[gender] && this.manifest.clips[gender][key];
    if (!def) return null;
    const g = await this.gltf(def.file);
    const clip = g.animations[0].clone();
    clip.name = key;
    return clip;
  }
}
