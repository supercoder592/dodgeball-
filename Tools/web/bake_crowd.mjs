// Offline bake of the arena crowd's impostor atlas (world/crowd.js) - the game then downloads one small WebP instead
// of 8 hero models + 25 colour maps + 10 mocap clips and 40 skinned renders at boot.
//   node Tools/web/bake_crowd.mjs [--cells 64,96,128] [--quality 90] [--lossless]
// Runs the game's own Crowd.bakeAtlas() (same models, clips, lights, camera, Renderer environment) in headless
// Chromium (SwiftShader), reads each render target back and writes:
//   docs/assets/crowd/atlas-<cell>.webp   one atlas per quality cell width (render/quality.js crowdCell: 64/96/128);
//                                         sRGB RGBA, stored bottom-up (GL row order: row 0 = v 0) so the game loads it
//                                         with flipY=false like every other asset
//   docs/assets/crowd/atlas.json          { key, tiers: { <cell>: { file, width, height } }, cellU, cellV,
//                                           cells: [{ u, v, gender, pose }] }  (u/v: texture-space fractions, the
//                                           same for every tier)
// `key` is crowdBakeKey(): the game ignores the files (and bakes at runtime) once CROWD in crowd.js changes. Re-run
// after changing the crowd's heroes / poses / framing, its bake lighting or materials, roster.hiddenParts, or the
// hero assets. Requires python3 + Pillow (WebP encoding) and `npm install` in Tools/web.
import { chromium } from 'playwright-core';
import { execFileSync } from 'child_process';
import fs from 'fs';
import os from 'os';
import path from 'path';
import { fileURLToPath } from 'url';
import { startServer } from './serve.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const outDir = path.join(here, '..', '..', 'docs', 'assets', 'crowd');
const args = process.argv.slice(2);
const opt = (name, def) => { const i = args.indexOf('--' + name); return i >= 0 ? args[i + 1] : def; };
const cellWidths = opt('cells', '64,96,128').split(',').map(Number).filter((n) => n > 0);
const quality = Number(opt('quality', '90'));
const lossless = args.includes('--lossless');
const exe = process.env.PW_CHROMIUM || '/opt/pw-browsers/chromium-1194/chrome-linux/chrome';

// Virtual page (served only to the headless browser): the game's modules with the import map of index.html.
const BAKE_PAGE = `<!doctype html><html><head><meta charset="utf-8">
<script type="importmap">{ "imports": { "three": "../vendor/three/build/three.module.js", "three/addons/": "../vendor/three/examples/jsm/" } }</script>
</head><body><div id="app"></div><script type="module">
import { game } from '../js/game.js';
import { Assets } from '../js/engine/assets.js';
import { Renderer } from '../js/render/renderer.js';
import { Crowd, crowdBakeKey } from '../js/world/crowd.js';
game.config = { ...game.config, quality: 'high', params: new URLSearchParams() };
const renderer = new Renderer(document.getElementById('app'), 'high');
game.renderer = renderer; game.scene = renderer.scene; game.camera = renderer.camera;
game.assets = new Assets(renderer.three);
game.assets.base = '../assets/';
await game.assets.loadManifest();
window.__bake = async (cellWidth) => {
  const baked = await new Crowd().bakeAtlas(renderer.three, renderer.envTexture, cellWidth);
  if (!baked) return null;
  const { rt, width, height } = baked;
  const px = new Uint8Array(width * height * 4);
  renderer.three.readRenderTargetPixels(rt, 0, 0, width, height, px);
  rt.dispose();
  let bin = '';
  for (let i = 0; i < px.length; i += 0x8000) bin += String.fromCharCode.apply(null, px.subarray(i, i + 0x8000));
  return { width, height, cellU: baked.cellU, cellV: baked.cellV, cells: baked.cells, rgba: btoa(bin) };
};
window.__key = crowdBakeKey();
window.__ready = true;
</script></body></html>`;

const server = await startServer(0);
const base = `http://127.0.0.1:${server.address().port}/`;
const browser = await chromium.launch({
  executablePath: exe,
  args: ['--use-angle=swiftshader', '--enable-unsafe-swiftshader', '--ignore-gpu-blocklist', '--enable-webgl'],
});
const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'du-crowd-'));
try {
  const page = await browser.newPage({ viewport: { width: 640, height: 360 } });
  const problems = [];
  page.on('pageerror', (e) => problems.push(String(e && e.stack || e)));
  page.on('console', (m) => { if (m.type() === 'error' || m.type() === 'warning') problems.push(`${m.type()}: ${m.text()}`); });
  await page.route('**/dev/__bake_crowd.html', (r) => r.fulfill({ contentType: 'text/html', body: BAKE_PAGE }));
  await page.goto(base + 'dev/__bake_crowd.html');
  await page.waitForFunction(() => window.__ready === true, null, { timeout: 120000 });
  const key = await page.evaluate(() => window.__key);
  const desc = { key, generator: 'Tools/web/bake_crowd.mjs', tiers: {}, cellU: 0, cellV: 0, cells: null };
  fs.mkdirSync(outDir, { recursive: true });
  for (const cell of cellWidths) {
    const t0 = Date.now();
    const r = await page.evaluate((c) => window.__bake(c), cell);
    if (!r) throw new Error(`bake failed for cell ${cell}: ${problems.join(' | ') || 'no models/clips'}`);
    const raw = path.join(tmp, `atlas-${cell}.rgba`);
    fs.writeFileSync(raw, Buffer.from(r.rgba, 'base64'));
    const file = `atlas-${cell}.webp`;
    // Fully transparent texels keep RGB 0 (exact), exactly like the render target the mipmaps were built from.
    execFileSync('python3', ['-c', [
      'import sys; from PIL import Image',
      'w, h = int(sys.argv[2]), int(sys.argv[3])',
      'im = Image.frombytes("RGBA", (w, h), open(sys.argv[1], "rb").read())',
      'im.save(sys.argv[4], "WEBP", quality=int(sys.argv[5]), method=6, exact=True, alpha_quality=100, lossless=sys.argv[6] == "1")',
    ].join('\n'), raw, String(r.width), String(r.height), path.join(outDir, file), String(quality), lossless ? '1' : '0']);
    // Cells are the same fractions of every tier (same grid): stored once.
    const layout = { cellU: r.cellU, cellV: r.cellV, cells: r.cells };
    if (!desc.cells) Object.assign(desc, layout);
    else if (JSON.stringify(layout) !== JSON.stringify({ cellU: desc.cellU, cellV: desc.cellV, cells: desc.cells })) {
      throw new Error(`cell ${cell}: atlas layout differs from the first tier (a model or clip failed to load?)`);
    }
    desc.tiers[cell] = { file, width: r.width, height: r.height };
    const size = fs.statSync(path.join(outDir, file)).size;
    console.log(`cell ${cell}: ${r.width}x${r.height}, ${r.cells.length} cells, ${(size / 1024).toFixed(1)} KB (${Date.now() - t0} ms)`);
  }
  // One cell per line: small, and diffs stay readable.
  const json = JSON.stringify({ ...desc, cells: [] }, null, 1).replace('"cells": []',
    '"cells": [\n' + desc.cells.map((c) => '  ' + JSON.stringify(c)).join(',\n') + '\n ]');
  fs.writeFileSync(path.join(outDir, 'atlas.json'), json + '\n');
  if (problems.length) console.warn('page messages:\n  ' + problems.join('\n  '));
  console.log('wrote', path.relative(process.cwd(), outDir));
} finally {
  fs.rmSync(tmp, { recursive: true, force: true });
  await browser.close();
  server.close();
}
