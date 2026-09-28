// ---------------------------------------------------------------------------------------------------------------
// Procedural textures of a regulation rubber dodgeball (generated once on a canvas, shared by every ball).
//
// Pebble grain: 3D cellular (Worley F1) noise evaluated ON THE SPHERE for every texel of the equirectangular UV
// layout of THREE.SphereGeometry - no seam at u = 0/1 and no pinching at the poles, unlike a tiled 2D pattern.
// Each cell point is a ~4 mm raised pebble (real playground balls: 2-5 mm grain). A moulding seam runs around the
// equator and a small inflation valve sits on one side. Outputs (all equirectangular, flipY = true like any canvas):
//   map           greyscale albedo detail (multiplied by the material colour, so one texture serves every style)
//   normalMap     tangent-space normals from the height field (OpenGL convention, +Y = +v)
//   roughnessMap  G channel: worn pebble tops slightly glossier than the valleys
// ---------------------------------------------------------------------------------------------------------------
import * as THREE from 'three';

/** Texture generation tuning. */
export const BALL_TEXTURE = Object.freeze({
  width: 768,
  height: 384,
  /** Cells per unit radius: 0.105 m ball / 26 = ~4 mm pebble spacing. */
  pebbleCells: 26,
  /** Pebble bump radius in cell units (touching pebbles ~0.6). */
  pebbleRadius: 0.62,
  /** Normal-map strength (height units -> slope). */
  normalStrength: 2.6,
  /** Equator seam half-width on the unit sphere (~1.2 mm on the real ball). */
  seamWidth: 0.011,
  /** Valve direction (unit sphere) and angular radius. */
  valveDir: [0.94, 0.28, 0.2],
  valveRadius: 0.035,
  anisotropy: 8,
});

let _cache = null;

/** Integer hash -> [0, 1). */
function hash3(ix, iy, iz, k) {
  let h = Math.imul(ix, 374761393) ^ Math.imul(iy, 668265263) ^ Math.imul(iz, 1440662683) ^ Math.imul(k, 1274126177);
  h = Math.imul(h ^ (h >>> 13), 1274126177);
  h ^= h >>> 16;
  return (h >>> 0) / 4294967296;
}

/** Cheap smooth low-frequency field on the unit sphere (-0.5..0.5) for moulding / wear mottling. */
function mottle(x, y, z) {
  return 0.5 * (Math.sin(x * 5.3 + 1.7 + Math.sin(z * 3.1)) * Math.sin(y * 4.1 + 0.3) * 0.6 +
    Math.sin(z * 6.7 + 2.1 + Math.sin(y * 2.3) * 1.5) * Math.sin(x * 3.7 - 0.8) * 0.4);
}

/**
 * Worley F1 distance (cell units) from p to the nearest jittered cell point. Only the 2x2x2 neighbourhood toward
 * the point's nearest corner is searched (jitter is kept within 0.15..0.85 so the true nearest is almost always
 * there) - 3.4x cheaper than the full 27 cells with no visible difference.
 */
function worleyF1(x, y, z) {
  const ix = Math.floor(x), iy = Math.floor(y), iz = Math.floor(z);
  const bx = x - ix < 0.5 ? -1 : 0, by = y - iy < 0.5 ? -1 : 0, bz = z - iz < 0.5 ? -1 : 0;
  let best = 1e9;
  for (let c = 0; c < 8; c++) {
    const cx = ix + bx + (c & 1), cy = iy + by + ((c >> 1) & 1), cz = iz + bz + ((c >> 2) & 1);
    const px = cx + 0.15 + 0.7 * hash3(cx, cy, cz, 1);
    const py = cy + 0.15 + 0.7 * hash3(cx, cy, cz, 2);
    const pz = cz + 0.15 + 0.7 * hash3(cx, cy, cz, 3);
    const dx = px - x, dy = py - y, dz = pz - z;
    const d = dx * dx + dy * dy + dz * dz;
    if (d < best) best = d;
  }
  return Math.sqrt(best);
}

function makeCanvas(w, h) {
  if (typeof document !== 'undefined' && document.createElement) {
    const c = document.createElement('canvas');
    c.width = w; c.height = h;
    return c;
  }
  if (typeof OffscreenCanvas !== 'undefined') return new OffscreenCanvas(w, h);
  return null;
}

/**
 * Shared dodgeball textures (generated on first call). Returns nulls where no canvas is available.
 * @returns {{map:THREE.Texture|null, normalMap:THREE.Texture|null, roughnessMap:THREE.Texture|null}}
 */
export function getBallTextures() {
  if (_cache) return _cache;
  const T = BALL_TEXTURE;
  const W = T.width, H = T.height;
  const cAlb = makeCanvas(W, H), cNrm = makeCanvas(W, H), cRgh = makeCanvas(W, H);
  if (!cAlb || !cNrm || !cRgh) { _cache = { map: null, normalMap: null, roughnessMap: null }; return _cache; }

  const height = new Float32Array(W * H);
  const albedo = new Float32Array(W * H);
  const rough = new Float32Array(W * H);
  const vl = Math.hypot(T.valveDir[0], T.valveDir[1], T.valveDir[2]);
  const vx = T.valveDir[0] / vl, vy = T.valveDir[1] / vl, vz = T.valveDir[2] / vl;
  const cosValve = Math.cos(T.valveRadius), cosValveRing = Math.cos(T.valveRadius * 1.6);

  for (let j = 0; j < H; j++) {
    // Canvas row 0 is uv.y = 1 (flipY) = the north pole of THREE.SphereGeometry (phi = 0 .. PI from the top).
    const theta = ((j + 0.5) / H) * Math.PI;
    const st = Math.sin(theta), ct = Math.cos(theta);
    for (let i = 0; i < W; i++) {
      const phi = ((i + 0.5) / W) * Math.PI * 2;
      // SphereGeometry: x = -cos(phi) sin(theta), y = cos(theta), z = sin(phi) sin(theta).
      const sx = -Math.cos(phi) * st, sy = ct, sz = Math.sin(phi) * st;
      const k = j * W + i;

      // Pebbles: rounded bumps where the cellular distance is small.
      const f1 = worleyF1(sx * T.pebbleCells, sy * T.pebbleCells, sz * T.pebbleCells);
      let bump = 1 - f1 / T.pebbleRadius;
      bump = bump > 0 ? bump * bump * (3 - 2 * bump) : 0;
      let h = bump * 0.6;
      let alb = 0.9 + 0.06 * bump;
      let rg = 0.66 - 0.14 * bump;

      // Moulding seam around the equator: a shallow groove with a darker line.
      const seam = Math.exp(-(sy * sy) / (T.seamWidth * T.seamWidth));
      h = h * (1 - seam) - 0.35 * seam;
      alb *= 1 - 0.38 * seam;
      rg = rg + (0.78 - rg) * seam;

      // Inflation valve: a smooth recessed disc with a raised rim.
      const dv = sx * vx + sy * vy + sz * vz;
      if (dv > cosValveRing) {
        if (dv > cosValve) { h = -0.25; alb *= 0.45; rg = 0.5; }
        else { h = 0.35; alb *= 0.8; rg = 0.55; }
      }

      // Low-frequency mottling (moulding / wear) for a non-CG look.
      const m = mottle(sx, sy, sz);
      alb *= 1 + 0.08 * m;
      rg += 0.05 * m;

      height[k] = h; albedo[k] = alb; rough[k] = rg;
    }
  }

  const ctxA = cAlb.getContext('2d'), ctxN = cNrm.getContext('2d'), ctxR = cRgh.getContext('2d');
  const imgA = ctxA.createImageData(W, H), imgN = ctxN.createImageData(W, H), imgR = ctxR.createImageData(W, H);
  const s = T.normalStrength;
  for (let j = 0; j < H; j++) {
    const jUp = j > 0 ? j - 1 : 0, jDn = j < H - 1 ? j + 1 : H - 1;
    for (let i = 0; i < W; i++) {
      const k = j * W + i, o = k * 4;
      const iL = (i + W - 1) % W, iR = (i + 1) % W;
      // Slopes along +u (right) and +v (up = previous canvas row).
      const du = (height[j * W + iR] - height[j * W + iL]) * 0.5;
      const dv = (height[jUp * W + i] - height[jDn * W + i]) * 0.5;
      let nx = -du * s, ny = -dv * s, nz = 1;
      const inv = 1 / Math.hypot(nx, ny, nz);
      nx *= inv; ny *= inv; nz *= inv;
      imgN.data[o] = (nx * 0.5 + 0.5) * 255;
      imgN.data[o + 1] = (ny * 0.5 + 0.5) * 255;
      imgN.data[o + 2] = (nz * 0.5 + 0.5) * 255;
      imgN.data[o + 3] = 255;
      const a = Math.min(255, Math.max(0, albedo[k] * 255));
      imgA.data[o] = a; imgA.data[o + 1] = a; imgA.data[o + 2] = a; imgA.data[o + 3] = 255;
      const r = Math.min(255, Math.max(0, rough[k] * 255));
      imgR.data[o] = 255; imgR.data[o + 1] = r; imgR.data[o + 2] = 0; imgR.data[o + 3] = 255;
    }
  }
  ctxA.putImageData(imgA, 0, 0);
  ctxN.putImageData(imgN, 0, 0);
  ctxR.putImageData(imgR, 0, 0);

  const make = (canvas, srgb) => {
    const t = new THREE.CanvasTexture(canvas);
    t.colorSpace = srgb ? THREE.SRGBColorSpace : THREE.NoColorSpace;
    t.wrapS = THREE.RepeatWrapping;
    t.wrapT = THREE.ClampToEdgeWrapping;
    t.anisotropy = T.anisotropy;
    t.needsUpdate = true;
    return t;
  };
  _cache = { map: make(cAlb, true), normalMap: make(cNrm, false), roughnessMap: make(cRgh, false) };
  return _cache;
}
