// ---------------------------------------------------------------------------------------------------------------
// Asset loading with caching (kernel): manifest (docs/assets/manifest.json), GLTF models/clips, textures.
//   * gltf(path) / clip(g, key) / texture(path, srgb) are cached: every caller of the same asset shares one promise
//     (or one THREE.Texture), so main.js can preload exactly what Avatar.load() will ask for later.
//   * GLBs may be meshopt-compressed (EXT_meshopt_compression, Tools/web/gltf_post.mjs): the decoder module is
//     imported lazily on the first compressed buffer view, so uncompressed GLBs never fetch it.
//   * Textures decode off the main thread (fetch + createImageBitmap) where that is reliable; Safari / old Firefox
//     and any failure fall back to THREE.TextureLoader (HTMLImageElement, decoded during upload).
//   * prefetch(path) only warms the HTTP cache (low fetch priority, nothing decoded or kept in memory).
//   * Failures are never cached: a rejected GLTF, a texture that did not decode and a failed prefetch are dropped from
//     their cache, so the next caller (match launch, Avatar.load) requests the file again - a network drop during the
//     menu's background prefetch must not disable a hero for the whole session. forget(path) drops a GLTF on purpose
//     (main.js: a stalled request is abandoned before a retry).
// ---------------------------------------------------------------------------------------------------------------
import * as THREE from 'three';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';

/** Module specifier of three's meshopt decoder (resolved through the import map, loaded on demand). */
const MESHOPT_DECODER = 'three/addons/libs/meshopt_decoder.module.js';

/**
 * Stand-in MeshoptDecoder for GLTFLoader.setMeshoptDecoder(): claims support, and imports the real decoder the
 * first time a compressed buffer view is decoded. GLTFLoader only calls decodeGltfBufferAsync().
 */
function lazyMeshoptDecoder() {
  let real = null;
  const load = () => {
    if (!real) {
      real = import(MESHOPT_DECODER).then(async (m) => {
        const d = m.MeshoptDecoder;
        if (!d || !d.supported) throw new Error('[assets] MeshoptDecoder unsupported (needs WebAssembly)');
        await d.ready;
        return d;
      });
    }
    return real;
  };
  return {
    supported: true,
    get ready() { return load().then(() => undefined); },
    /** @returns {Promise<Uint8Array>} */
    decodeGltfBufferAsync(count, stride, source, mode, filter) {
      return load().then((d) => {
        if (d.decodeGltfBufferAsync) return d.decodeGltfBufferAsync(count, stride, source, mode, filter);
        const out = new Uint8Array(count * stride);
        d.decodeGltfBuffer(out, count, stride, source, mode, filter);
        return out;
      });
    },
  };
}

/** createImageBitmap decodes off-thread and honours the options we need everywhere except Safari / Firefox < 98. */
function imageBitmapUsable() {
  if (typeof createImageBitmap === 'undefined' || typeof fetch === 'undefined' || typeof navigator === 'undefined') return false;
  const ua = navigator.userAgent || '';
  if (/^((?!chrome|android).)*safari/i.test(ua)) return false;
  const ff = ua.match(/Firefox\/(\d+)/);
  return !(ff && Number(ff[1]) < 98);
}

const now = () => (typeof performance !== 'undefined' ? performance.now() : Date.now());

const BITMAP_OPTIONS = Object.freeze({ premultiplyAlpha: 'none', colorSpaceConversion: 'none', imageOrientation: 'from-image' });

export class Assets {
  /** @param {THREE.WebGLRenderer} [renderer] used for the (lazy) max anisotropy query */
  constructor(renderer) {
    this.gltfLoader = new GLTFLoader();
    this.gltfLoader.setMeshoptDecoder(lazyMeshoptDecoder());
    this.textureLoader = new THREE.TextureLoader();
    this._renderer = renderer || null;
    this._maxAniso = 0;
    this._bitmaps = imageBitmapUsable();
    this._gltf = new Map();
    this._tex = new Map();
    this._texReady = new Map();
    this._prefetched = new Map();
    this.manifest = null;
    this.base = 'assets/';
    this.onProgress = null; // (loaded, total, label)
    this._loaded = 0; this._total = 0;
    /** performance.now() of the last download activity (GLB bytes received, any load settled): stall detection. */
    this.activityAt = 0;
    this._touch = () => { this.activityAt = now(); };
  }

  /**
   * Renderer max anisotropy, queried on first use: the query is a synchronous GPU round trip, which at construction
   * time would stall on the renderer's pending PMREM work.
   */
  get maxAnisotropy() {
    if (!this._maxAniso) {
      const caps = this._renderer && this._renderer.capabilities;
      this._maxAniso = (caps && caps.getMaxAnisotropy()) || 4;
    }
    return this._maxAniso;
  }
  set maxAnisotropy(v) { this._maxAniso = v; }

  async loadManifest() {
    const res = await fetch(this.base + 'manifest.json');
    if (!res.ok) throw new Error('assets/manifest.json missing - run Tools/web/build_assets.py');
    this.manifest = await res.json();
    return this.manifest;
  }

  _track(promise, label) {
    this._total++;
    this._report(label);
    return promise.finally(() => { this._loaded++; this._touch(); this._report(label); });
  }
  _report(label) { this.onProgress && this.onProgress(this._loaded, this._total, label); }

  /** Loads (once) and returns the parsed GLTF for a path relative to docs/assets/. */
  gltf(path) {
    let p = this._gltf.get(path);
    if (!p) {
      p = this._track(this.gltfLoader.loadAsync(this.base + path, this._touch), path);
      this._gltf.set(path, p);
      // Not cached when it fails: the next caller retries (the rejection itself still reaches every awaiting caller).
      p.catch(() => { if (this._gltf.get(path) === p) this._gltf.delete(path); });
    }
    return p;
  }

  /**
   * Drops a cached / in-flight GLTF or texture (both colour spaces) so the next request for it starts over (a stalled
   * request is abandoned; it may still settle later, unobserved).
   */
  forget(path) {
    this._gltf.delete(path);
    for (const key of [path, path + '#srgb']) { this._tex.delete(key); this._texReady.delete(key); }
  }

  /** True once gltf(path) has been requested (loaded or in flight). */
  hasGltf(path) { return this._gltf.has(path); }

  /** Cached texture (glTF UV convention: flipY=false). srgb=true for colour maps. */
  texture(path, srgb = false) {
    const key = path + (srgb ? '#srgb' : '');
    if (!this._tex.has(key)) {
      const t = new THREE.Texture();
      t.colorSpace = srgb ? THREE.SRGBColorSpace : THREE.NoColorSpace;
      t.flipY = false;
      t.anisotropy = Math.min(8, this.maxAnisotropy);
      const p = this._decodeInto(t, path);
      this._track(p, path);
      this._tex.set(key, t);
      this._texReady.set(key, p);
      // A map that did not decode stays blank on its current users but is not cached: the next caller retries.
      p.then(() => {
        if (!t.image && this._tex.get(key) === t) { this._tex.delete(key); this._texReady.delete(key); }
      });
    }
    return this._tex.get(key);
  }

  /** Same as texture() but resolves once the image is decoded (never rejects: a failed map stays blank). */
  textureAsync(path, srgb = false) {
    this.texture(path, srgb);
    return this._texReady.get(path + (srgb ? '#srgb' : ''));
  }

  /** @returns {Promise<THREE.Texture>} */
  async _decodeInto(t, path) {
    const url = this.base + path;
    if (this._bitmaps) {
      try {
        const res = await fetch(url);
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        const blob = await res.blob();
        t.image = await createImageBitmap(blob, BITMAP_OPTIONS);
        t.needsUpdate = true;
        return t;
      } catch (e) {
        // Fall through to the HTMLImageElement path (it reports its own failure).
      }
    }
    return new Promise((resolve) => {
      this.textureLoader.load(url, (loaded) => { t.image = loaded.image; t.needsUpdate = true; resolve(t); }, undefined, () => {
        console.warn('[assets] texture failed', path);
        resolve(t);
      });
    });
  }

  /**
   * Warms the HTTP cache for a file (relative to docs/assets/) without decoding or keeping it: low fetch priority,
   * one request per path (a failed one may be retried), never rejects. Used for background prefetch of hero textures.
   * @returns {Promise<void>}
   */
  prefetch(path) {
    if (!this._prefetched.has(path)) {
      const p = typeof fetch === 'undefined' ? Promise.resolve()
        : fetch(this.base + path, { priority: 'low' }).then((r) => {
          if (!r.ok) throw new Error(`HTTP ${r.status}`);
          return r.arrayBuffer();
        }).then(() => undefined, () => { this._prefetched.delete(path); });
      this._prefetched.set(path, p);
    }
    return this._prefetched.get(path);
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
