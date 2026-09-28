// ---------------------------------------------------------------------------------------------------------------
// Procedural particle sprite atlas (owner: fx). Drawn once at init on a canvas, no image files.
//
// Channel packing (important): the canvas is kept FULLY OPAQUE and the sprite data is packed into the colour channels
//   R = shade   (0..1 lighting/detail multiplier, e.g. smoke lit from above, darker rims on droplets)
//   G = alpha   (coverage)
//   B = glint   (hot core / specular highlight, added on top of the tinted colour)
// Browsers store canvases premultiplied; with real alpha every fully transparent texel would lose its RGB and linear
// filtering / mip-mapping would bleed black fringes into soft sprites. Packing alpha into G sidesteps that entirely.
//
// Layout: 4 x 2 cells of 128 px, row 0 at the top of the canvas (see FRAME).
// ---------------------------------------------------------------------------------------------------------------
import * as THREE from 'three';

/** Atlas frame indices (column + row * ATLAS_COLS). */
export const FRAME = Object.freeze({ SOFT: 0, SMOKE: 1, SPARK: 2, SHARD: 3, RING: 4, CHUNK: 5, BLOB: 6, WISP: 7 });
export const ATLAS_COLS = 4;
export const ATLAS_ROWS = 2;
/** Cell resolution in pixels. 128 is plenty for soft sprites and keeps generation < 20 ms. */
export const ATLAS_CELL = 128;
/** Transparent border inside each cell so mip-mapping never bleeds neighbours. */
const CELL_MARGIN = 0.06;

/** Seeded mulberry32 so the sprites are identical on every load (no Math.random, no game.rng perturbation). */
function mulberry32(seed) {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) | 0;
    let t = Math.imul(a ^ (a >>> 15), 1 | a);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

/** Tileable 2D value noise with smoothstep interpolation + fBm, built on a seeded lattice. */
function makeNoise(seed) {
  const rnd = mulberry32(seed);
  const N = 64;
  const lattice = new Float32Array(N * N);
  for (let i = 0; i < lattice.length; i++) lattice[i] = rnd();
  const at = (x, y) => lattice[((y % N + N) % N) * N + ((x % N + N) % N)];
  const value = (x, y) => {
    const xi = Math.floor(x), yi = Math.floor(y);
    const xf = x - xi, yf = y - yi;
    const u = xf * xf * (3 - 2 * xf), v = yf * yf * (3 - 2 * yf);
    const a = at(xi, yi), b = at(xi + 1, yi), c = at(xi, yi + 1), d = at(xi + 1, yi + 1);
    return a + (b - a) * u + (c - a) * v + (a - b - c + d) * u * v;
  };
  const fbm = (x, y, octaves = 4) => {
    let sum = 0, amp = 0.5, norm = 0, f = 1;
    for (let o = 0; o < octaves; o++) { sum += value(x * f, y * f) * amp; norm += amp; amp *= 0.5; f *= 2.03; }
    return sum / norm;
  };
  return { value, fbm };
}

const clamp01 = (x) => (x < 0 ? 0 : x > 1 ? 1 : x);
const smoothstep = (e0, e1, x) => { const t = clamp01((x - e0) / (e1 - e0)); return t * t * (3 - 2 * t); };

/**
 * Signed-distance-ish coverage for a convex polygon given as [x0,y0,x1,y1,...] (counter-clockwise).
 * Returns the minimum inward distance to any edge (positive inside).
 */
function polyInside(poly, x, y) {
  let minD = Infinity;
  const n = poly.length / 2;
  for (let i = 0; i < n; i++) {
    const x0 = poly[i * 2], y0 = poly[i * 2 + 1];
    const x1 = poly[((i + 1) % n) * 2], y1 = poly[((i + 1) % n) * 2 + 1];
    const ex = x1 - x0, ey = y1 - y0;
    const len = Math.hypot(ex, ey) || 1;
    // inward normal of a CCW polygon is (-ey, ex)
    const d = ((x - x0) * -ey + (y - y0) * ex) / len;
    if (d < minD) minD = d;
  }
  return minD;
}

/** Random convex polygon (sorted angles, jittered radius) for shards and debris chunks. */
function randomConvex(rnd, verts, rx, ry, jitter) {
  const angles = [];
  for (let i = 0; i < verts; i++) angles.push((i / verts) * Math.PI * 2 + (rnd() - 0.5) * (Math.PI * 2 / verts) * 0.6);
  angles.sort((a, b) => a - b);
  const out = [];
  for (const a of angles) {
    const r = 1 - rnd() * jitter;
    out.push(Math.cos(a) * rx * r, Math.sin(a) * ry * r);
  }
  return out;
}

/**
 * Painters: (x, y) in [-1, 1] with +y = up (canvas top), return via `o` = [shade, alpha, glint].
 * Each painter gets its own noise so sprites are not correlated.
 */
function buildPainters() {
  const nSmoke = makeNoise(11), nSmoke2 = makeNoise(29), nWisp = makeNoise(47), nChunk = makeNoise(83), nBlob = makeNoise(97);
  const rnd = mulberry32(1234);
  const shard = randomConvex(rnd, 5, 0.42, 0.9, 0.35);
  const chunk = randomConvex(rnd, 7, 0.8, 0.62, 0.3);

  return [
    // SOFT: gaussian glow with a hot centre (glows, flashes, motes, dust specks).
    (x, y, o) => {
      const r2 = x * x + y * y;
      o[0] = 1; o[1] = Math.exp(-r2 * 4.2) * (1 - smoothstep(0.8, 1.0, Math.sqrt(r2))); o[2] = Math.exp(-r2 * 22);
    },
    // SMOKE: noise-warped puff, lit from above, soft billowy edge (dust, smoke, mist, fire tongues).
    (x, y, o) => {
      const n = nSmoke.fbm(x * 1.9 + 7.3, y * 1.9 + 3.1, 5);
      const n2 = nSmoke2.fbm(x * 3.7 + 1.7, y * 3.7 + 9.2, 4);
      const r = Math.sqrt(x * x + y * y) + (n - 0.5) * 0.55;
      const body = 1 - smoothstep(0.3, 0.95, r);
      o[1] = clamp01(body * (0.45 + 0.75 * n2));
      o[0] = clamp01(0.62 + 0.3 * y * 0.5 + 0.35 * (n2 - 0.5) + 0.12);
      o[2] = clamp01(Math.pow(body, 3) * 0.9 * n); // dense core: brighter centre for fire
    },
    // SPARK: thin streak with a hot core, tapered ends (sparks, embers, droplet streaks).
    (x, y, o) => {
      const ax = Math.abs(x);
      const taper = 1 - smoothstep(0.35, 0.98, ax);
      o[0] = 1; o[1] = Math.exp(-y * y * 70) * taper; o[2] = Math.exp(-y * y * 260) * Math.exp(-x * x * 3);
    },
    // SHARD: faceted ice / glass fragment with bright edges (ice crystals, clone shards).
    (x, y, o) => {
      const d = polyInside(shard, x, y);
      o[1] = smoothstep(-0.02, 0.03, d);
      const facet = x * 0.8 + y * 0.35 > 0.02 ? 1.0 : 0.72;
      o[0] = facet * (0.85 + 0.15 * y);
      o[2] = clamp01(1 - smoothstep(0.0, 0.08, d)) * o[1] * 0.9 + Math.exp(-((x + 0.1) ** 2 + (y - 0.35) ** 2) * 60) * 0.6;
    },
    // RING: thin soft ring (bubbles, small billboard shock rings).
    (x, y, o) => {
      const r = Math.sqrt(x * x + y * y);
      const k = Math.exp(-((r - 0.78) ** 2) * 420);
      o[0] = 1; o[1] = k; o[2] = k * 0.5;
    },
    // CHUNK: irregular debris fragment, darker underside (earthquake debris, floor specks).
    (x, y, o) => {
      const d = polyInside(chunk, x, y);
      o[1] = smoothstep(-0.02, 0.03, d);
      o[0] = clamp01(0.5 + 0.35 * y + 0.3 * (nChunk.fbm(x * 4 + 2, y * 4 + 5, 3) - 0.5));
      o[2] = 0;
    },
    // BLOB: liquid droplet with a darker rim and a specular highlight (glue, sweat).
    (x, y, o) => {
      const n = nBlob.fbm(x * 2.5 + 3, y * 2.5 + 1, 3);
      const r = Math.sqrt(x * x + y * y) + (n - 0.5) * 0.15;
      o[1] = 1 - smoothstep(0.7, 0.8, r);
      o[0] = clamp01(0.55 + 0.45 * (1 - r) + 0.1 * y);
      o[2] = Math.exp(-((x + 0.28) ** 2 + (y - 0.32) ** 2) * 38) * 0.95;
    },
    // WISP: stretched, streaky smoke (swirls, heat haze, cold mist sheets, magnetic field lines).
    (x, y, o) => {
      const n = nWisp.fbm(x * 1.3 + 4, y * 5.5 + 2, 5);
      const fall = (1 - smoothstep(0.45, 0.97, Math.abs(x))) * Math.exp(-y * y * 5);
      o[0] = clamp01(0.8 + 0.2 * n);
      o[1] = clamp01(fall * (0.25 + 1.1 * (n - 0.3)));
      o[2] = clamp01(fall * fall * n * 0.5);
    },
  ];
}

/**
 * Builds the particle atlas texture. Colour space is data (NoColorSpace): channels are not colours.
 * @returns {THREE.CanvasTexture}
 */
export function createParticleAtlas() {
  const W = ATLAS_COLS * ATLAS_CELL, H = ATLAS_ROWS * ATLAS_CELL;
  const canvas = document.createElement('canvas');
  canvas.width = W; canvas.height = H;
  const ctx = canvas.getContext('2d');
  const img = ctx.createImageData(W, H);
  const data = img.data;
  const painters = buildPainters();
  const o = [0, 0, 0];
  const inner = 1 - CELL_MARGIN;
  for (let f = 0; f < painters.length; f++) {
    const col = f % ATLAS_COLS, row = Math.floor(f / ATLAS_COLS);
    const paint = painters[f];
    for (let py = 0; py < ATLAS_CELL; py++) {
      // +y up inside the sprite (canvas rows grow downward)
      const y = (1 - ((py + 0.5) / ATLAS_CELL) * 2) / inner;
      for (let px = 0; px < ATLAS_CELL; px++) {
        const x = (((px + 0.5) / ATLAS_CELL) * 2 - 1) / inner;
        let s = 0, a = 0, g = 0;
        if (x >= -1 && x <= 1 && y >= -1 && y <= 1) { paint(x, y, o); s = o[0]; a = o[1]; g = o[2]; }
        const i = ((row * ATLAS_CELL + py) * W + col * ATLAS_CELL + px) * 4;
        data[i] = Math.round(clamp01(s) * 255);
        data[i + 1] = Math.round(clamp01(a) * 255);
        data[i + 2] = Math.round(clamp01(g) * 255);
        data[i + 3] = 255; // opaque on purpose (see header)
      }
    }
  }
  ctx.putImageData(img, 0, 0);
  const tex = new THREE.CanvasTexture(canvas);
  tex.colorSpace = THREE.NoColorSpace;
  tex.generateMipmaps = true;
  tex.minFilter = THREE.LinearMipmapLinearFilter;
  tex.magFilter = THREE.LinearFilter;
  tex.wrapS = tex.wrapT = THREE.ClampToEdgeWrapping;
  tex.name = 'vfx-atlas';
  tex.needsUpdate = true;
  return tex;
}
