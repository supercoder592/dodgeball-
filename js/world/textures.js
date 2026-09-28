// ---------------------------------------------------------------------------------------------------------------
// Procedural PBR canvas textures for the arena (owner: render). Everything is generated at boot from a seeded RNG,
// so the site ships no texture files for the venue and the look is identical on every run.
//
//   createHardwoodTextures   maple strip flooring tile: colour (planks, grain, mineral streaks, varnish mottling),
//                            normal (seams, butt joints, cupping, grain - derived from a height canvas via Sobel),
//                            roughness (varnish wear, shoe scuffs). Seamless in both directions.
//   createCourtPaintTexture  painted lines / stains / wordmarks for the whole hardwood region (RGBA overlay that the
//                            floor shader blends over the wood with its own roughness)
//   createVinylTextures      pebbled sports vinyl for the run-off
//   createPaddingTextures    wall padding panels with seams, pillowing and printed branding
//   createBlockWallTextures  painted concrete block (CMU) for the upper walls
//   createConcreteTextures   sealed concrete for bleacher treads
//   createBannerAtlas        hanging hero banners, team banners, LED ribbon and end-wall graphic in one atlas
//   drawWordmark             the DODGEBALL ULTRA logotype (also used by the scoreboard)
// Colour canvases are sRGB; normal/roughness data canvases are linear (NoColorSpace).
// ---------------------------------------------------------------------------------------------------------------
import * as THREE from 'three';
import { Rng } from '../core/rng.js';
import { TEAM, TEAM_COLORS } from '../core/constants.js';

/** Display font stack (heavy condensed sans for sports graphics). */
export const SPORT_FONT = "'Arial Black', 'Helvetica Neue', 'Segoe UI', 'DejaVu Sans', Arial, sans-serif";

/** Hardwood look tuning (sRGB HSL of varnished maple under 5000 K LEDs). */
export const HARDWOOD = Object.freeze({
  plankWidth: 0.057,            // 2 1/4" maple strip
  plankLength: [0.7, 2.6],      // random board lengths (m)
  hue: 34, sat: 44, light: 62,  // base HSL
  toneSigma: 4.0,               // per-board lightness variation (%)
  darkBoardChance: 0.07,        // occasional heartwood-tinted board
  roughness: 0.34,              // varnished base roughness (G channel 0..1)
  normalStrength: 2.2,
});

// ------------------------------------------------------------------------------------------------- helpers

/** New canvas (DOM or Offscreen). */
export function makeCanvas(w, h) {
  if (typeof document !== 'undefined') {
    const c = document.createElement('canvas');
    c.width = w; c.height = h;
    return c;
  }
  return new OffscreenCanvas(w, h);
}

function ctx2d(canvas, readback = false) {
  return canvas.getContext('2d', readback ? { willReadFrequently: true } : undefined);
}

/** Wraps a canvas as a mip-mapped texture. */
export function canvasTexture(canvas, { srgb = true, repeat = true, anisotropy = 8, name = '' } = {}) {
  const t = new THREE.CanvasTexture(canvas);
  t.name = name;
  t.colorSpace = srgb ? THREE.SRGBColorSpace : THREE.NoColorSpace;
  t.wrapS = t.wrapT = repeat ? THREE.RepeatWrapping : THREE.ClampToEdgeWrapping;
  t.anisotropy = anisotropy;
  t.generateMipmaps = true;
  t.minFilter = THREE.LinearMipmapLinearFilter;
  t.magFilter = THREE.LinearFilter;
  t.needsUpdate = true;
  return t;
}

const hex = (c) => '#' + (c >>> 0).toString(16).padStart(6, '0');
const rgba = (c, a) => `rgba(${(c >> 16) & 255},${(c >> 8) & 255},${c & 255},${a})`;
const hsl = (h, s, l, a = 1) => `hsla(${h.toFixed(1)},${s.toFixed(1)}%,${l.toFixed(1)}%,${a})`;
const clamp = (v, a, b) => (v < a ? a : v > b ? b : v);

/**
 * Converts a height canvas (R channel, 0..255) into a tangent-space normal map canvas (OpenGL convention, +Y up for
 * a flipY texture). Sobel filter with wrap-around sampling so tiling textures stay seamless.
 */
export function normalFromHeight(heightCanvas, strength = 2, wrap = true) {
  const w = heightCanvas.width, h = heightCanvas.height;
  const src = ctx2d(heightCanvas, true).getImageData(0, 0, w, h).data;
  const H = new Float32Array(w * h);
  for (let i = 0, j = 0; i < H.length; i++, j += 4) H[i] = src[j] / 255;
  const out = makeCanvas(w, h);
  const octx = ctx2d(out);
  const img = octx.createImageData(w, h);
  const o = img.data;
  const ix = (x) => (wrap ? (x + w) % w : clamp(x, 0, w - 1));
  const iy = (y) => (wrap ? (y + h) % h : clamp(y, 0, h - 1));
  for (let y = 0; y < h; y++) {
    const yu = iy(y - 1) * w, yc = y * w, yd = iy(y + 1) * w;
    for (let x = 0; x < w; x++) {
      const xl = ix(x - 1), xr = ix(x + 1);
      const tl = H[yu + xl], t = H[yu + x], tr = H[yu + xr];
      const l = H[yc + xl], r = H[yc + xr];
      const bl = H[yd + xl], b = H[yd + x], br = H[yd + xr];
      const dx = (tr + 2 * r + br) - (tl + 2 * l + bl);
      const dy = (bl + 2 * b + br) - (tl + 2 * t + tr);
      // canvas y grows downward = -v, so the green channel takes +dh/dy
      let nx = -dx * strength, ny = dy * strength, nz = 1;
      const inv = 1 / Math.hypot(nx, ny, nz);
      nx *= inv; ny *= inv; nz *= inv;
      const k = (yc + x) * 4;
      o[k] = (nx * 0.5 + 0.5) * 255;
      o[k + 1] = (ny * 0.5 + 0.5) * 255;
      o[k + 2] = (nz * 0.5 + 0.5) * 255;
      o[k + 3] = 255;
    }
  }
  octx.putImageData(img, 0, 0);
  return out;
}

/** Draws `fn(dx, dy)` at every wrapped copy needed for an item of radius r at (x, y) in a w x h tile. */
function wrapped(w, h, x, y, r, fn) {
  const xs = [0], ys = [0];
  if (x - r < 0) xs.push(w); if (x + r > w) xs.push(-w);
  if (y - r < 0) ys.push(h); if (y + r > h) ys.push(-h);
  for (const dx of xs) for (const dy of ys) fn(dx, dy);
}

/** Speckle noise layer: n dots of random size/alpha in `color` over a w x h tile (seamless). */
function speckle(ctx, rng, w, h, n, color, alpha, size) {
  for (let i = 0; i < n; i++) {
    const x = rng.next() * w, y = rng.next() * h, s = size * (0.5 + rng.next());
    ctx.fillStyle = rgba(color, alpha * (0.3 + rng.next() * 0.7));
    ctx.fillRect(x, y, s, s);
  }
}

/** Large soft blotches (seamless) for low-frequency variation. */
function mottle(ctx, rng, w, h, n, colors, alpha, rMin, rMax) {
  for (let i = 0; i < n; i++) {
    const x = rng.next() * w, y = rng.next() * h, r = rng.range(rMin, rMax);
    const c = colors[i % colors.length], a = alpha * (0.4 + rng.next() * 0.6);
    wrapped(w, h, x, y, r, (dx, dy) => {
      const g = ctx.createRadialGradient(x + dx, y + dy, 0, x + dx, y + dy, r);
      g.addColorStop(0, rgba(c, a));
      g.addColorStop(1, rgba(c, 0));
      ctx.fillStyle = g;
      ctx.fillRect(x + dx - r, y + dy - r, r * 2, r * 2);
    });
  }
}

/** Sets `ctx.font` to `${style} ${size}px SPORT_FONT`, shrinking the size until `text` fits `maxWidth`. */
export function fitFont(ctx, text, maxWidth, size, style = 'italic 900') {
  ctx.font = `${style} ${size}px ${SPORT_FONT}`;
  const w = ctx.measureText(text).width;
  if (w > maxWidth) { size *= maxWidth / w; ctx.font = `${style} ${size}px ${SPORT_FONT}`; }
  return size;
}

// ------------------------------------------------------------------------------------------------- wordmark

/**
 * DODGEBALL ULTRA logotype centred at (cx, cy), fitted to `width` px. Heavy italic caps, an orange/blue speed
 * swoosh under "ULTRA".
 * @param {CanvasRenderingContext2D} ctx
 * @param {{ color?:string, accentA?:string, accentB?:string, stacked?:boolean, alpha?:number, shadow?:boolean }} opts
 */
export function drawWordmark(ctx, cx, cy, width, opts = {}) {
  const color = opts.color || '#f4f4f0';
  const accentA = opts.accentA || hex(TEAM_COLORS[TEAM.HOME]);
  const accentB = opts.accentB || hex(TEAM_COLORS[TEAM.AWAY]);
  ctx.save();
  ctx.globalAlpha = opts.alpha ?? 1;
  ctx.translate(cx, cy);
  ctx.textAlign = 'center';
  ctx.textBaseline = 'middle';
  if (opts.stacked) {
    let size = width * 0.24;
    ctx.font = `italic 900 ${size}px ${SPORT_FONT}`;
    const m = ctx.measureText('DODGEBALL').width;
    size *= Math.min(1, width / Math.max(1, m));
    ctx.font = `italic 900 ${size}px ${SPORT_FONT}`;
    if (opts.shadow) { ctx.shadowColor = 'rgba(0,0,0,0.45)'; ctx.shadowBlur = size * 0.12; }
    ctx.fillStyle = color;
    ctx.fillText('DODGEBALL', 0, -size * 0.45);
    ctx.fillStyle = accentA;
    ctx.fillText('ULTRA', 0, size * 0.62);
    ctx.shadowBlur = 0;
    // swoosh
    ctx.fillStyle = accentB;
    ctx.beginPath();
    ctx.moveTo(-width * 0.42, size * 1.18);
    ctx.quadraticCurveTo(0, size * 1.02, width * 0.46, size * 1.1);
    ctx.lineTo(width * 0.44, size * 1.2);
    ctx.quadraticCurveTo(0, size * 1.14, -width * 0.42, size * 1.18);
    ctx.fill();
  } else {
    let size = width * 0.12;
    ctx.font = `italic 900 ${size}px ${SPORT_FONT}`;
    const m = ctx.measureText('DODGEBALL ULTRA').width;
    size *= Math.min(1.4, (width * 0.92) / Math.max(1, m));
    ctx.font = `italic 900 ${size}px ${SPORT_FONT}`;
    const wD = ctx.measureText('DODGEBALL ').width, wU = ctx.measureText('ULTRA').width;
    const x0 = -(wD + wU) / 2;
    if (opts.shadow) { ctx.shadowColor = 'rgba(0,0,0,0.45)'; ctx.shadowBlur = size * 0.12; }
    ctx.textAlign = 'left';
    ctx.fillStyle = color;
    ctx.fillText('DODGEBALL ', x0, 0);
    ctx.fillStyle = accentA;
    ctx.fillText('ULTRA', x0 + wD, 0);
    ctx.shadowBlur = 0;
    ctx.fillStyle = accentB;
    ctx.fillRect(x0 + wD + size * 0.05, size * 0.5, wU * 0.95, size * 0.1);
  }
  ctx.restore();
}

// ------------------------------------------------------------------------------------------------- hardwood

/**
 * Seamless maple strip flooring tile. Boards run along the texture's V axis (the court's long axis).
 * @param {{ size?:number, tileMeters?:number, seed?:number, anisotropy?:number }} o
 * @returns {{ map:THREE.Texture, normalMap:THREE.Texture, roughnessMap:THREE.Texture, tileMeters:number }}
 */
export function createHardwoodTextures({ size = 2048, tileMeters = 4, seed = 20260928, anisotropy = 8 } = {}) {
  const H = HARDWOOD;
  const rng = new Rng(seed);
  const cols = Math.max(8, Math.round(tileMeters / H.plankWidth));
  const colW = size / cols;
  const ppm = size / tileMeters;

  // Board layout: per column a closed loop of boards (wraps vertically so the tile is seamless).
  const layout = [];
  for (let c = 0; c < cols; c++) {
    const boards = [];
    let y = rng.next() * size, left = size;
    while (left > 1) {
      let len = rng.range(H.plankLength[0], H.plankLength[1]) * ppm;
      if (len > left - H.plankLength[0] * ppm * 0.6) len = left;
      boards.push({
        y: y % size, len,
        tone: clamp(rng.gaussian(0, 1), -2.5, 2.5), hue: rng.gaussian(0, 1),
        dark: rng.chance(H.darkBoardChance), level: rng.gaussian(0, 1), rough: rng.gaussian(0, 1), seed: rng.next(),
      });
      y += len; left -= len;
    }
    layout.push(boards);
  }
  const forEachPiece = (fn) => {
    for (let c = 0; c < cols; c++) {
      const x = c * colW;
      for (const b of layout[c]) {
        fn(x, b.y, b, c);
        if (b.y + b.len > size) fn(x, b.y - size, b, c); // wrapped remainder
      }
    }
  };

  // ---- colour
  const color = makeCanvas(size, size);
  const cx = ctx2d(color);
  cx.fillStyle = hsl(H.hue, H.sat, H.light);
  cx.fillRect(0, 0, size, size);
  const s = size / 2048; // stroke scale
  forEachPiece((x, y, b) => {
    const l = H.light + b.tone * H.toneSigma - (b.dark ? 9 : 0);
    const sat = H.sat + b.tone * 2 + (b.dark ? 4 : 0);
    const hue = H.hue + b.hue * 1.8 - (b.dark ? 3 : 0);
    // board base with a faint lengthwise gradient (figure)
    const g = cx.createLinearGradient(0, y, 0, y + b.len);
    g.addColorStop(0, hsl(hue, sat, l + 1.2));
    g.addColorStop(0.5, hsl(hue + 0.6, sat + 1, l - 0.8));
    g.addColorStop(1, hsl(hue, sat, l + 0.6));
    cx.fillStyle = g;
    cx.fillRect(x, y, colW + 0.5, b.len);
    // grain lines, clipped to the board
    cx.save();
    cx.beginPath(); cx.rect(x, y, colW, b.len); cx.clip();
    const r = new Rng(Math.floor(b.seed * 1e9) + 7);
    const n = 5 + r.int(0, 7);
    for (let i = 0; i < n; i++) {
      const gx = x + r.next() * colW, dark = r.chance(0.72);
      cx.strokeStyle = dark ? `rgba(105,62,28,${r.range(0.05, 0.16)})` : `rgba(255,228,180,${r.range(0.04, 0.09)})`;
      cx.lineWidth = r.range(0.5, 1.5) * s * 1.6;
      cx.beginPath();
      const amp = r.range(0.2, 1.3) * s * 2, freq = r.range(0.004, 0.012) / s, ph = r.next() * 6.28;
      cx.moveTo(gx, y);
      for (let k = 0; k <= b.len; k += 24 * s) cx.lineTo(gx + Math.sin(k * freq + ph) * amp, y + k);
      cx.stroke();
    }
    // mineral streak / pin knot
    if (r.chance(0.22)) {
      cx.fillStyle = `rgba(78,56,34,${r.range(0.08, 0.2)})`;
      cx.beginPath();
      cx.ellipse(x + colW * r.range(0.3, 0.7), y + b.len * r.next(), colW * r.range(0.12, 0.3), r.range(6, 40) * s * 3, 0, 0, Math.PI * 2);
      cx.fill();
    }
    for (let i = 0; i < 3; i++) {
      if (!r.chance(0.5)) continue;
      cx.fillStyle = `rgba(70,45,20,${r.range(0.15, 0.35)})`;
      cx.fillRect(x + r.next() * colW, y + r.next() * b.len, 1.4 * s * 1.5, 2.5 * s * 1.5);
    }
    cx.restore();
    // butt joint
    cx.fillStyle = 'rgba(62,36,14,0.55)';
    cx.fillRect(x, y, colW, Math.max(1, 1.2 * s));
  });
  // seams between strips
  cx.fillStyle = 'rgba(66,38,15,0.42)';
  for (let c = 0; c < cols; c++) cx.fillRect(c * colW - 0.5, 0, Math.max(1, 1.1 * s), size);
  // varnish mottling (amber build-up and lighter traffic lanes), shoe scuffs
  mottle(cx, rng, size, size, 36, [0xb87a32, 0xf2cf98, 0x9a6a36], 0.06, size * 0.05, size * 0.2);
  for (let i = 0; i < 70; i++) {
    const x = rng.next() * size, y = rng.next() * size, r = rng.range(8, 45) * s * 2;
    cx.strokeStyle = `rgba(40,30,24,${rng.range(0.03, 0.08)})`;
    cx.lineWidth = rng.range(1, 3) * s * 2;
    cx.beginPath();
    cx.arc(x, y, r, rng.next() * 6.28, rng.next() * 6.28 + rng.range(0.4, 1.6));
    cx.stroke();
  }

  // ---- height -> normal (half resolution is plenty for seams and cupping)
  const hs = Math.max(256, size >> 1), hk = hs / size;
  const height = makeCanvas(hs, hs);
  const hx = ctx2d(height, true);
  hx.fillStyle = 'rgb(128,128,128)';
  hx.fillRect(0, 0, hs, hs);
  forEachPiece((x, y, b) => {
    const X = x * hk, Y = y * hk, W = colW * hk, L = b.len * hk;
    const base = 128 + b.level * 5;
    const g = hx.createLinearGradient(X, 0, X + W, 0); // cupping: board edges sit slightly lower
    g.addColorStop(0, `rgb(${base - 7},${base - 7},${base - 7})`);
    g.addColorStop(0.5, `rgb(${base + 2},${base + 2},${base + 2})`);
    g.addColorStop(1, `rgb(${base - 7},${base - 7},${base - 7})`);
    hx.fillStyle = g;
    hx.fillRect(X, Y, W + 0.5, L);
    hx.fillStyle = 'rgb(40,40,40)';
    hx.fillRect(X, Y, W, 1); // butt joint groove
  });
  hx.fillStyle = 'rgb(46,46,46)';
  for (let c = 0; c < cols; c++) hx.fillRect(Math.round(c * colW * hk), 0, 1, hs);
  // fine grain relief
  for (let i = 0; i < cols * 10; i++) {
    const x = rng.next() * hs, y = rng.next() * hs;
    hx.strokeStyle = rng.chance(0.5) ? 'rgba(150,150,150,0.25)' : 'rgba(110,110,110,0.25)';
    hx.lineWidth = 0.8;
    hx.beginPath(); hx.moveTo(x, y); hx.lineTo(x + rng.range(-0.6, 0.6), y + rng.range(20, 90) * hk * 2); hx.stroke();
  }
  const normal = normalFromHeight(height, H.normalStrength, true);

  // ---- roughness (G channel read by three.js; material.roughness stays 1)
  const rs = Math.max(256, size >> 2), rk = rs / size;
  const rough = makeCanvas(rs, rs);
  const rx = ctx2d(rough);
  const r0 = Math.round(H.roughness * 255);
  rx.fillStyle = `rgb(${r0},${r0},${r0})`;
  rx.fillRect(0, 0, rs, rs);
  forEachPiece((x, y, b) => {
    const v = clamp(Math.round(r0 + b.rough * 6), 0, 255);
    rx.fillStyle = `rgb(${v},${v},${v})`;
    rx.fillRect(x * rk, y * rk, colW * rk + 0.5, b.len * rk);
  });
  mottle(rx, rng, rs, rs, 26, [0xa8a8a8], 0.18, rs * 0.04, rs * 0.14);  // worn varnish lanes (rougher)
  mottle(rx, rng, rs, rs, 14, [0x404040], 0.18, rs * 0.05, rs * 0.12);  // freshly buffed (glossier)
  for (let i = 0; i < 120; i++) {
    const x = rng.next() * rs, y = rng.next() * rs, r = rng.range(3, 14);
    rx.strokeStyle = `rgba(190,190,190,${rng.range(0.1, 0.3)})`;
    rx.lineWidth = rng.range(0.6, 1.8);
    rx.beginPath(); rx.arc(x, y, r, rng.next() * 6.28, rng.next() * 6.28 + 1.2); rx.stroke();
  }

  return {
    map: canvasTexture(color, { srgb: true, anisotropy, name: 'hardwood.color' }),
    normalMap: canvasTexture(normal, { srgb: false, anisotropy, name: 'hardwood.normal' }),
    roughnessMap: canvasTexture(rough, { srgb: false, anisotropy, name: 'hardwood.roughness' }),
    tileMeters,
  };
}

// ------------------------------------------------------------------------------------------------- court paint

/** Painted court markings (colours sRGB). */
export const COURT_PAINT = Object.freeze({
  lineWidth: 0.05,           // 5 cm lines
  lineColor: 'rgba(246,246,240,0.96)',
  homeZoneAlpha: 0.7,        // outfield strips painted in the (muted) colour of the team that stands there
  awayZoneAlpha: 0.9,        // blue over maple mixes toward grey, so it is painted more opaquely
  homeZone: 0xc4521f,        // painted zone colours (muted versions of TEAM_COLORS, read well on maple)
  awayZone: 0x2356a3,
  circleRadius: 1.8,
  circleStain: 'rgba(16,22,36,0.72)',
  sidelineWordmarkAlpha: 0.55,
  edgeBand: 0.035,           // dark threshold strip where the hardwood meets the vinyl
});

/**
 * RGBA overlay covering the hardwood rectangle [-halfX, halfX] x [-halfZ, halfZ]. UV mapping (applied by the floor
 * shader from world position): u = (halfX - x) / (2 halfX), v = (z + halfZ) / (2 halfZ) with flipY, i.e. the canvas
 * is the top view with +Z up and -X to the right, so text reads correctly for a viewer standing at -Z.
 * @param {{ halfX:number, halfZ:number, ppm?:number, court:import('./court.js').Court, anisotropy?:number }} o
 */
export function createCourtPaintTexture({ halfX, halfZ, ppm = 80, court, anisotropy = 8 }) {
  const W = Math.round(2 * halfX * ppm), Hh = Math.round(2 * halfZ * ppm);
  const canvas = makeCanvas(W, Hh);
  const ctx = ctx2d(canvas);
  ctx.clearRect(0, 0, W, Hh);
  const X = (x) => (halfX - x) * ppm;
  const Z = (z) => (halfZ - z) * ppm;
  /** Axis-aligned world rectangle. */
  const rect = (x0, z0, x1, z1, style) => {
    ctx.fillStyle = style;
    const a = X(Math.max(x0, x1)), b = Z(Math.max(z0, z1));
    ctx.fillRect(a, b, Math.abs(x1 - x0) * ppm, Math.abs(z1 - z0) * ppm);
  };
  const P = COURT_PAINT, lw = P.lineWidth, hw = lw / 2;
  const hW = court.halfW, hL = court.halfL, D = court.outfieldDepth;
  const home = TEAM_COLORS[TEAM.HOME], away = TEAM_COLORS[TEAM.AWAY];

  // Outfield strips (Home's outfield is behind the Away baseline, +Z).
  const hb = court.outfieldBounds(TEAM.HOME), ab = court.outfieldBounds(TEAM.AWAY);
  rect(hb.minX, hb.minZ, hb.maxX, hb.maxZ, rgba(P.homeZone, P.homeZoneAlpha));
  rect(ab.minX, ab.minZ, ab.maxX, ab.maxZ, rgba(P.awayZone, P.awayZoneAlpha));

  // Centre circle stain + team halves accent ring.
  const cxp = X(0), czp = Z(0), R = P.circleRadius * ppm;
  ctx.fillStyle = P.circleStain;
  ctx.beginPath(); ctx.arc(cxp, czp, R, 0, Math.PI * 2); ctx.fill();
  ctx.lineWidth = 0.12 * ppm;
  ctx.strokeStyle = rgba(away, 0.85); // +Z half (canvas top) = Away
  ctx.beginPath(); ctx.arc(cxp, czp, R - 0.2 * ppm, Math.PI, Math.PI * 2); ctx.stroke();
  ctx.strokeStyle = rgba(home, 0.85);
  ctx.beginPath(); ctx.arc(cxp, czp, R - 0.2 * ppm, 0, Math.PI); ctx.stroke();
  drawWordmark(ctx, cxp, czp, R * 1.5, { stacked: true, alpha: 0.95 });

  // Lines: sidelines, baselines (extended across the outfield width), centre line, outfield borders, circle.
  const L = P.lineColor;
  rect(-hW - hw, -hL, -hW + hw, hL, L);
  rect(hW - hw, -hL, hW + hw, hL, L);
  rect(hb.minX, -hL - hw, hb.maxX, -hL + hw, L);
  rect(hb.minX, hL - hw, hb.maxX, hL + hw, L);
  rect(-hW, -hw, hW, hw, L);
  for (const b of [hb, ab]) {
    const zFar = b.minZ < 0 ? b.minZ : b.maxZ;
    rect(b.minX, zFar - hw, b.maxX, zFar + hw, L);
    rect(b.minX - hw, b.minZ, b.minX + hw, b.maxZ, L);
    rect(b.maxX - hw, b.minZ, b.maxX + hw, b.maxZ, L);
  }
  ctx.strokeStyle = L;
  ctx.lineWidth = lw * ppm;
  ctx.beginPath(); ctx.arc(cxp, czp, R, 0, Math.PI * 2); ctx.stroke();
  // Sideline hash marks every 3 m (outside the court).
  for (let z = -hL + 3; z < hL - 0.5; z += 3) {
    if (Math.abs(z) < 0.5) continue;
    rect(-hW - 0.35, z - hw, -hW, z + hw, L);
    rect(hW, z - hw, hW + 0.35, z + hw, L);
  }

  // Team words in the outfield strips, each readable from its own team's half.
  const teamWord = (word, zc, flip, col) => {
    ctx.save();
    ctx.translate(X(0), Z(zc));
    if (flip) ctx.rotate(Math.PI);
    ctx.font = `italic 900 ${1.05 * ppm}px ${SPORT_FONT}`;
    ctx.textAlign = 'center'; ctx.textBaseline = 'middle';
    ctx.fillStyle = rgba(col, 0.55);
    ctx.fillText(word, 0, 0);
    ctx.restore();
  };
  teamWord('HOME', (hb.minZ + hb.maxZ) / 2, false, 0xf4f4f0);
  teamWord('AWAY', (ab.minZ + ab.maxZ) / 2, true, 0xf4f4f0);

  // Sideline wordmarks between the court and the hardwood edge (read from each sideline).
  const sideX = (hW + halfX) / 2 + 0.25;
  for (const sgn of [-1, 1]) {
    ctx.save();
    ctx.translate(X(sgn * sideX), Z(0));
    ctx.rotate(sgn > 0 ? -Math.PI / 2 : Math.PI / 2);
    drawWordmark(ctx, 0, 0, 9.5 * ppm, { alpha: P.sidelineWordmarkAlpha });
    ctx.restore();
  }

  // Dark threshold band at the hardwood edge.
  ctx.strokeStyle = 'rgba(28,24,20,0.92)';
  ctx.lineWidth = P.edgeBand * ppm * 2;
  ctx.strokeRect(0, 0, W, Hh);

  return canvasTexture(canvas, { srgb: true, repeat: false, anisotropy, name: 'court.paint' });
}

// ------------------------------------------------------------------------------------------------- vinyl

/** Pebbled sports vinyl (run-off). */
export function createVinylTextures({ size = 512, seed = 31, base = 0x2a3039, anisotropy = 8 } = {}) {
  const rng = new Rng(seed);
  const c = makeCanvas(size, size), x = ctx2d(c);
  x.fillStyle = hex(base); x.fillRect(0, 0, size, size);
  mottle(x, rng, size, size, 24, [0x1d2229, 0x39414c], 0.35, size * 0.05, size * 0.2);
  speckle(x, rng, size, size, size * 14, 0x56606c, 0.35, 1.1);
  speckle(x, rng, size, size, size * 10, 0x14181d, 0.4, 1.1);
  const h = makeCanvas(size, size), hx = ctx2d(h, true);
  hx.fillStyle = 'rgb(128,128,128)'; hx.fillRect(0, 0, size, size);
  for (let i = 0; i < size * 9; i++) {
    const px = rng.next() * size, py = rng.next() * size, r = rng.range(0.8, 2.2);
    const v = rng.chance(0.5) ? 170 : 95;
    hx.fillStyle = `rgba(${v},${v},${v},0.6)`;
    hx.beginPath(); hx.arc(px, py, r, 0, Math.PI * 2); hx.fill();
  }
  return {
    map: canvasTexture(c, { srgb: true, anisotropy, name: 'vinyl.color' }),
    normalMap: canvasTexture(normalFromHeight(h, 1.2, true), { srgb: false, anisotropy, name: 'vinyl.normal' }),
  };
}

// ------------------------------------------------------------------------------------------------- padding

/**
 * Wall padding tile: `panels` vinyl pads of `panelMeters` width, `heightMeters` tall, with seams, pillowing and
 * printed branding (DODGEBALL ULTRA on the first panel, team accent stripe on top).
 */
export function createPaddingTextures({ pxPerMeter = 256, panelMeters = 2, panels = 3, heightMeters = 1.2, seed = 5, anisotropy = 8 } = {}) {
  const rng = new Rng(seed);
  const pw = Math.round(panelMeters * pxPerMeter), W = pw * panels, Hh = Math.round(heightMeters * pxPerMeter);
  const c = makeCanvas(W, Hh), x = ctx2d(c);
  const home = TEAM_COLORS[TEAM.HOME], away = TEAM_COLORS[TEAM.AWAY];
  for (let p = 0; p < panels; p++) {
    const g = x.createLinearGradient(0, 0, 0, Hh);
    g.addColorStop(0, '#26314a'); g.addColorStop(0.55, '#1b2438'); g.addColorStop(1, '#141b2b');
    x.fillStyle = g; x.fillRect(p * pw, 0, pw, Hh);
  }
  mottle(x, rng, W, Hh, 30, [0x0e1320, 0x33405c], 0.25, Hh * 0.2, Hh * 0.6);
  // accent stripes
  x.fillStyle = hex(home); x.fillRect(0, Hh * 0.06, W, Hh * 0.035);
  x.fillStyle = hex(away); x.fillRect(0, Hh * 0.105, W, Hh * 0.012);
  // branding
  drawWordmark(x, pw * 0.5, Hh * 0.55, pw * 0.8, { alpha: 0.95 });
  x.save();
  x.textAlign = 'center'; x.textBaseline = 'middle';
  fitFont(x, '3v3 SUPER LEAGUE', pw * 0.8, Hh * 0.2);
  x.fillStyle = 'rgba(240,240,236,0.85)';
  x.fillText('3v3 SUPER LEAGUE', pw * 1.5, Hh * 0.56);
  fitFont(x, '#DODGEBALLULTRA', pw * 0.8, Hh * 0.2);
  x.fillStyle = rgba(home, 0.95);
  x.fillText('#DODGEBALLULTRA', pw * 2.5, Hh * 0.56);
  x.restore();
  // seams (dark vertical gaps)
  x.fillStyle = 'rgba(5,7,12,0.9)';
  for (let p = 0; p <= panels; p++) x.fillRect(p * pw - 2, 0, 4, Hh);
  x.fillRect(0, Hh - 3, W, 3);

  // height: pillowed panels, seam grooves, subtle vinyl wrinkles
  const h = makeCanvas(W >> 1, Hh >> 1), hx = ctx2d(h, true);
  const hw = h.width, hh = h.height, ppw = pw / 2;
  hx.fillStyle = 'rgb(60,60,60)'; hx.fillRect(0, 0, hw, hh);
  for (let p = 0; p < panels; p++) {
    const gx = hx.createLinearGradient(p * ppw, 0, (p + 1) * ppw, 0);
    gx.addColorStop(0, 'rgb(70,70,70)'); gx.addColorStop(0.04, 'rgb(170,170,170)');
    gx.addColorStop(0.96, 'rgb(170,170,170)'); gx.addColorStop(1, 'rgb(70,70,70)');
    hx.fillStyle = gx; hx.fillRect(p * ppw + 2, 2, ppw - 4, hh - 4);
    const gy = hx.createLinearGradient(0, 0, 0, hh);
    gy.addColorStop(0, 'rgba(60,60,60,0.8)'); gy.addColorStop(0.08, 'rgba(60,60,60,0)');
    gy.addColorStop(0.92, 'rgba(60,60,60,0)'); gy.addColorStop(1, 'rgba(60,60,60,0.8)');
    hx.fillStyle = gy; hx.fillRect(p * ppw, 0, ppw, hh);
  }
  for (let i = 0; i < 60; i++) {
    hx.strokeStyle = `rgba(${rng.chance(0.5) ? 200 : 120},128,128,0.12)`;
    hx.lineWidth = rng.range(2, 6);
    const px = rng.next() * hw, py = rng.next() * hh;
    hx.beginPath(); hx.moveTo(px, py); hx.lineTo(px + rng.range(-30, 30), py + rng.range(-20, 20)); hx.stroke();
  }
  return {
    map: canvasTexture(c, { srgb: true, anisotropy, name: 'pad.color' }),
    normalMap: canvasTexture(normalFromHeight(h, 1.6, true), { srgb: false, anisotropy, name: 'pad.normal' }),
    tileMeters: panelMeters * panels,
  };
}

// ------------------------------------------------------------------------------------------------- walls

/** Painted concrete block (CMU) wall, 4 m tile of 0.4 x 0.2 m blocks in running bond. */
export function createBlockWallTextures({ size = 512, tileMeters = 4, seed = 9, base = 0x3b4049, anisotropy = 4 } = {}) {
  const rng = new Rng(seed);
  const c = makeCanvas(size, size), x = ctx2d(c);
  const h = makeCanvas(size, size), hx = ctx2d(h, true);
  const ppm = size / tileMeters, bw = 0.4 * ppm, bh = 0.2 * ppm, mortar = Math.max(1.5, 0.012 * ppm);
  x.fillStyle = '#2a2e35'; x.fillRect(0, 0, size, size);
  hx.fillStyle = 'rgb(70,70,70)'; hx.fillRect(0, 0, size, size);
  const rows = Math.round(size / bh), colsN = Math.round(size / bw);
  const r0 = (base >> 16) & 255, g0 = (base >> 8) & 255, b0 = base & 255;
  for (let r = 0; r < rows; r++) {
    const off = (r % 2) * bw / 2;
    for (let k = -1; k <= colsN; k++) {
      const bx = k * bw + off, by = r * bh, t = rng.range(-6, 6);
      x.fillStyle = `rgb(${clamp(r0 + t, 0, 255)},${clamp(g0 + t, 0, 255)},${clamp(b0 + t + 1, 0, 255)})`;
      x.fillRect(bx + mortar / 2, by + mortar / 2, bw - mortar, bh - mortar);
      hx.fillStyle = 'rgb(180,180,180)';
      hx.fillRect(bx + mortar / 2, by + mortar / 2, bw - mortar, bh - mortar);
    }
  }
  speckle(x, rng, size, size, size * 18, 0x5a606a, 0.25, 1.2);
  speckle(x, rng, size, size, size * 12, 0x1a1d22, 0.3, 1.2);
  mottle(x, rng, size, size, 18, [0x1f2228, 0x50555e], 0.18, size * 0.05, size * 0.25);
  speckle(hx, rng, size, size, size * 20, 0x9a9a9a, 0.3, 1.5);
  return {
    map: canvasTexture(c, { srgb: true, anisotropy, name: 'block.color' }),
    normalMap: canvasTexture(normalFromHeight(h, 1.4, true), { srgb: false, anisotropy, name: 'block.normal' }),
    tileMeters,
  };
}

/** Sealed grey concrete for bleacher treads and risers (2 m tile). */
export function createConcreteTextures({ size = 512, tileMeters = 2, seed = 13, base = 0x5a5e66, anisotropy = 4 } = {}) {
  const rng = new Rng(seed);
  const c = makeCanvas(size, size), x = ctx2d(c);
  x.fillStyle = hex(base); x.fillRect(0, 0, size, size);
  mottle(x, rng, size, size, 40, [0x474b52, 0x6d717a, 0x55504a], 0.3, size * 0.03, size * 0.18);
  speckle(x, rng, size, size, size * 20, 0x7a7e86, 0.3, 1.1);
  speckle(x, rng, size, size, size * 20, 0x2f3238, 0.35, 1.1);
  const h = makeCanvas(size >> 1, size >> 1), hx = ctx2d(h, true);
  hx.fillStyle = 'rgb(128,128,128)'; hx.fillRect(0, 0, h.width, h.height);
  speckle(hx, rng, h.width, h.height, h.width * 30, 0xb4b4b4, 0.4, 1.3);
  speckle(hx, rng, h.width, h.height, h.width * 30, 0x505050, 0.4, 1.3);
  return {
    map: canvasTexture(c, { srgb: true, anisotropy, name: 'concrete.color' }),
    normalMap: canvasTexture(normalFromHeight(h, 0.9, true), { srgb: false, anisotropy, name: 'concrete.normal' }),
    tileMeters,
  };
}

// ------------------------------------------------------------------------------------------------- banners

/**
 * Banner atlas (2048 x 1024):
 *   top half    : one vertical banner per entry in `heroes` (retired-jersey style, hero name + number) followed by
 *                 HOME and AWAY team banners
 *   y 512..640  : LED ribbon strip (repeats horizontally)
 *   y 640..1024 : end-wall graphic (wordmark on dark)
 * @param {{ heroes:string[] }} o
 * @returns {{ texture:THREE.Texture, rects:{ heroes:{u0,v0,u1,v1}[], home:object, away:object, ribbon:object, wall:object } }}
 */
export function createBannerAtlas({ heroes, anisotropy = 8 }) {
  const W = 2048, Hh = 1024;
  const c = makeCanvas(W, Hh), x = ctx2d(c);
  x.fillStyle = '#0b0d12'; x.fillRect(0, 0, W, Hh);
  const home = TEAM_COLORS[TEAM.HOME], away = TEAM_COLORS[TEAM.AWAY];
  const count = heroes.length + 2, bw = Math.floor(W / count), bh = 512;
  const uv = (px, py, pw, ph) => ({ u0: px / W, u1: (px + pw) / W, v0: 1 - (py + ph) / Hh, v1: 1 - py / Hh });
  const rects = { heroes: [], home: null, away: null, ribbon: null, wall: null };
  const palette = [0x1c2a4a, 0x5a1d1d, 0x1f3b2c, 0x2a2238, 0x3a2c14];

  const banner = (i, bg, accent, title, sub, num) => {
    const px = i * bw + 4, pw = bw - 8;
    const g = x.createLinearGradient(0, 0, 0, bh);
    g.addColorStop(0, hex(bg)); g.addColorStop(1, rgba(bg, 0.85));
    x.fillStyle = g; x.fillRect(px, 0, pw, bh);
    x.fillStyle = hex(accent); x.fillRect(px, 18, pw, 10); x.fillRect(px, bh - 60, pw, 8);
    // swallow-tail notch at the bottom
    x.fillStyle = '#0b0d12';
    x.beginPath(); x.moveTo(px, bh); x.lineTo(px + pw / 2, bh - 34); x.lineTo(px + pw, bh); x.fill();
    x.save();
    x.textAlign = 'center'; x.textBaseline = 'middle';
    x.fillStyle = '#f4f2ec';
    if (num !== null) {
      x.font = `900 ${pw * 0.62}px ${SPORT_FONT}`;
      x.fillText(String(num), px + pw / 2, 190);
    }
    let fs = pw * 0.2;
    x.font = `italic 900 ${fs}px ${SPORT_FONT}`;
    const m = x.measureText(title).width;
    if (m > pw * 0.88) { fs *= (pw * 0.88) / m; x.font = `italic 900 ${fs}px ${SPORT_FONT}`; }
    x.fillText(title, px + pw / 2, num !== null ? 330 : 220);
    x.font = `700 ${pw * 0.085}px ${SPORT_FONT}`;
    x.fillStyle = hex(accent);
    x.fillText(sub, px + pw / 2, num !== null ? 385 : 300);
    x.restore();
    return uv(px, 0, pw, bh);
  };
  heroes.forEach((name, i) => {
    rects.heroes.push(banner(i, palette[i % palette.length], i % 2 ? away : home, name.toUpperCase(), 'HALL OF FAME', (i * 7 + 3) % 99));
  });
  rects.home = banner(heroes.length, 0x2a1408, home, 'HOME', 'DEFEND -Z', null);
  rects.away = banner(heroes.length + 1, 0x08182e, away, 'AWAY', 'DEFEND +Z', null);

  // LED ribbon: dark with repeating wordmark + league text, fine LED dot grid.
  const ry = 512, rh = 128;
  x.fillStyle = '#05070b'; x.fillRect(0, ry, W, rh);
  for (let k = 0; k < 4; k++) {
    const cx0 = k * (W / 4);
    if (k % 2 === 0) drawWordmark(x, cx0 + W / 8, ry + rh / 2, W / 4 * 0.8);
    else {
      x.save();
      x.font = `italic 900 ${rh * 0.46}px ${SPORT_FONT}`;
      x.textAlign = 'center'; x.textBaseline = 'middle';
      x.fillStyle = hex(k === 1 ? away : home);
      x.fillText(k === 1 ? '3v3 SUPER LEAGUE' : 'RALLY BOOST 220 KM/H', cx0 + W / 8, ry + rh / 2);
      x.restore();
    }
  }
  x.fillStyle = 'rgba(0,0,0,0.35)';
  for (let px = 0; px < W; px += 4) x.fillRect(px, ry, 1, rh);
  for (let py = ry; py < ry + rh; py += 4) x.fillRect(0, py, W, 1);
  rects.ribbon = uv(0, ry, W, rh);

  // End-wall graphic.
  const wy = 640, wh = 384;
  const g = x.createLinearGradient(0, wy, 0, wy + wh);
  g.addColorStop(0, '#10141d'); g.addColorStop(1, '#070910');
  x.fillStyle = g; x.fillRect(0, wy, W, wh);
  x.fillStyle = hex(home); x.fillRect(0, wy + 26, W / 2, 10);
  x.fillStyle = hex(away); x.fillRect(W / 2, wy + 26, W / 2, 10);
  drawWordmark(x, W / 2, wy + wh * 0.5, W * 0.82, { shadow: true });
  x.save();
  x.font = `700 ${wh * 0.09}px ${SPORT_FONT}`;
  x.textAlign = 'center'; x.fillStyle = 'rgba(230,230,225,0.75)';
  x.fillText('WORLD 3v3 SUPERPOWERED DODGEBALL CHAMPIONSHIP', W / 2, wy + wh * 0.84);
  x.restore();
  rects.wall = uv(0, wy, W, wh);

  const texture = canvasTexture(c, { srgb: true, repeat: false, anisotropy, name: 'banners' });
  return { texture, rects };
}
