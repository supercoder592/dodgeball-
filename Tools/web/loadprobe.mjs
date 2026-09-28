// ---------------------------------------------------------------------------------------------------------------
// Load-time probe for the web build (headless Chromium + SwiftShader WebGL). Measures what a player waits for and
// prints ONE JSON object, so before/after runs of an optimisation can be diffed:
//
//   menu    navigation -> window.__DU.ready (hero select shown), module graph evaluated, first rendered frame behind
//           the menu, portraits loaded; bytes + requests before the menu by category (Resource Timing, wire bytes)
//   phases  boot phase durations from the 'du:boot:<phase>' User Timing marks in js/main.js (each mark ENDS a
//           phase; any mark added later shows up automatically), with the bytes that finished during each phase
//   play    (--play) PLAY on hero select -> players spawned (preRound) -> first rendered match frame -> countdown ->
//           playing, frame hitches in between, bytes/requests loaded for the match
//   cpu     (--cpu) sampled V8 CPU profile of the boot (and of the play window): self time bucketed by kind of work
//           (shader compile, texture upload, procedural textures, glTF parse, GC, idle ...) and by owning subsystem
//           (crowd bake, PMREM, first frames ...), plus the top functions
//   spans   (--spans) wraps a few named methods (crowd bake, arena textures, ...) with marks by rewriting those module
//           responses on the fly (CDP Fetch) - no product change; see SPANS below
//   trace   (--trace file.json) Chrome trace of the boot (open in DevTools > Performance) + main-thread / GPU-process
//           busy time by event (the GPU process is where SwiftShader compiles shaders and rasterises)
//
// Profiles: desktop (1280x720, no throttling, auto quality -> 'high') and --mobile (Pixel 7 landscape: phone UA,
// touch, DPR 2.625 -> auto quality 'low'; CDP network 20 Mbps down / 5 Mbps up / 60 ms latency, CPU throttling 4x).
// The site is served GitHub-Pages style (serve.mjs { pages: true }: gzip for text, max-age=600, ETag) so byte counts
// are wire bytes. Each run launches a fresh browser (cold HTTP cache AND cold GPU program cache).
// Software GL inflates absolute times (shader compiles, draws) - compare runs made with identical flags only.
//
//   node Tools/web/loadprobe.mjs [--mobile] [--play] [--cpu] [--spans] [--runs 3] [--query "quality=low"]
//        [--hero Rayne] [--repeat] [--trace boot-trace.json] [--out result.json] [--timeout 300] [--dev-server]
//
//   --query      extra URL query (default "seed=7"; add quality=low|medium|high to pin the quality)
//   --runs N     repeat the whole measurement N times; `median` summarises the key numbers
//   --repeat     after the cold load, reload in the same context (warm HTTP cache) and report that load too
//   --dev-server serve with serve.mjs dev headers (no-cache, no gzip) instead of GitHub Pages headers
// Exit code 1 when a run failed (timeout, page errors, failed requests).
// ---------------------------------------------------------------------------------------------------------------
import { chromium, devices } from 'playwright-core';
import fs from 'fs';
import path from 'path';
import { startServer } from './serve.mjs';

const args = process.argv.slice(2);
const opt = (name, def) => { const i = args.indexOf('--' + name); return i >= 0 && i + 1 < args.length ? args[i + 1] : def; };
const flag = (name) => args.includes('--' + name);

const OPTS = {
  mobile: flag('mobile'),
  play: flag('play'),
  cpu: flag('cpu'),
  spans: flag('spans'),
  repeat: flag('repeat'),
  runs: Math.max(1, Number(opt('runs', '1')) || 1),
  query: opt('query', 'seed=7'),
  hero: opt('hero', null),
  trace: opt('trace', null),
  out: opt('out', null),
  timeoutMs: Math.max(10, Number(opt('timeout', '300')) || 300) * 1000,
  devServer: flag('dev-server'),
  exe: process.env.PW_CHROMIUM || '/opt/pw-browsers/chromium-1194/chrome-linux/chrome',
};

/** Measurement profiles. Network numbers are CDP Network.emulateNetworkConditions units (bytes/s, ms). */
const PROFILES = {
  desktop: {
    context: { viewport: { width: 1280, height: 720 }, deviceScaleFactor: 1 },
    network: null,
    cpuSlowdown: 1,
  },
  mobile: {
    context: { ...devices['Pixel 7 landscape'] },
    network: { offline: false, latency: 60, downloadThroughput: (20e6 / 8), uploadThroughput: (5e6 / 8), connectionType: 'cellular4g' },
    cpuSlowdown: 4,
  },
};

/**
 * Methods wrapped with 'du:span:<name>:start|end' marks when --spans is given. The wrapper is appended to the module
 * source (the class binding is in module scope), so it survives refactors as long as the method exists; a missing
 * class/method is skipped silently. Several calls of one method aggregate (count, first start, last end, summed ms).
 */
const SPANS = [
  { file: 'js/render/renderer.js', target: 'Renderer.prototype', method: '_buildComposer', name: 'renderer.composer' },
  { file: 'js/world/arena.js', target: 'Arena.prototype', method: '_createMaterials', name: 'arena.textures' },
  { file: 'js/world/arena.js', target: 'Arena.prototype', method: 'build', name: 'arena.build' },
  { file: 'js/world/crowd.js', target: 'Crowd.prototype', method: 'build', name: 'crowd.build' },
  { file: 'js/world/crowd.js', target: 'Crowd.prototype', method: '_bake', name: 'crowd.bake' },
  { file: 'js/ui/heroSelect.js', target: 'HeroSelect', method: 'show', name: 'heroSelect.show' },
];

/** Resource category of a same-origin path (relative to the site root). First match wins. */
const CATEGORIES = [
  ['html', (p) => p === '' || p.endsWith('.html')],
  ['css', (p) => p.endsWith('.css')],
  ['js', (p) => p.startsWith('js/')],
  ['vendor-three', (p) => p.startsWith('vendor/three/build/')],
  ['vendor-addons', (p) => p.startsWith('vendor/three/examples/')],
  ['vendor-other', (p) => p.startsWith('vendor/')],
  ['manifest', (p) => p.endsWith('manifest.json')],
  ['portraits', (p) => /\/portrait\.[a-z]+$/.test(p)],
  ['hero-glb', (p) => p.startsWith('assets/heroes/') && p.endsWith('.glb')],
  ['hero-textures', (p) => p.startsWith('assets/heroes/')],
  ['anim-glb', (p) => p.startsWith('assets/anims/')],
  ['assets-other', (p) => p.startsWith('assets/')],
  ['other', () => true],
];
const categoryOf = (p) => CATEGORIES.find(([, test]) => test(p))[0];

// ------------------------------------------------------------------------------------------ CPU profile summary

/** WebGL-side work as seen from the main thread (three.js wrapper names; the native GL call is attributed to them). */
const SHADER_FNS = new Set(['onFirstUse', 'getProgramInfoLog', 'getShaderInfoLog', 'compileShader', 'linkProgram', 'getProgramParameter', 'getShaderParameter', 'WebGLShader', 'WebGLProgram']);
const UPLOAD_FNS = new Set(['texImage2D', 'texSubImage2D', 'texImage3D', 'texSubImage3D', 'texStorage2D', 'texStorage3D', 'compressedTexImage2D', 'compressedTexSubImage2D', 'generateMipmap', 'uploadTexture', 'bufferData', 'bufferSubData', 'createBuffer']);

/** Kind of work a sample's leaf frame does. */
function bucketOf(cf, url) {
  const fn = cf.functionName;
  if (fn === '(idle)') return 'idle';
  if (fn === '(garbage collector)') return 'gc';
  if (fn === '(program)') return 'browser-native';
  if (fn === '(root)') return 'other';
  if (SHADER_FNS.has(fn)) return 'shader-compile';
  if (UPLOAD_FNS.has(fn) && url.startsWith('vendor/three/')) return 'gpu-upload';
  if (!url) return 'browser-api';
  if (/^js\/(world|combat|vfx)\/[A-Za-z]*[tT]extures\.js$/.test(url)) return 'procedural-textures';
  if (url.includes('/loaders/')) return 'asset-parse';
  if (url.startsWith('vendor/three/build/three.module.js')) return 'three-webgl';
  if (url.startsWith('vendor/three/build/')) return 'three-core';
  if (url.startsWith('vendor/cannon')) return 'physics';
  if (url.startsWith('vendor/')) return 'three-addons';
  if (url.startsWith('js/')) return 'game-js';
  return 'other';
}

/** Owning subsystem: the INNERMOST stack frame matching one of these wins. */
const OWNERS = [
  ['crowd bake', (cf, url) => url === 'js/world/crowd.js'],
  ['PMREM environment', (cf) => cf.functionName === 'fromScene' || cf.functionName === 'fromEquirectangular'],
  ['procedural textures', (cf, url) => /^js\/(world|combat|vfx)\/[A-Za-z]*[tT]extures\.js$/.test(url)],
  ['asset parse (glTF/images)', (cf, url) => url.includes('/loaders/')],
  ['arena build', (cf, url) => url === 'js/world/arena.js' || url === 'js/world/scoreboard.js'],
  ['avatars', (cf, url) => url.startsWith('js/characters/')],
  ['render frames', (cf, url) => (cf.functionName === 'tick' && url === 'js/game.js') || url === 'js/render/renderer.js'],
  ['hero select UI', (cf, url) => url.startsWith('js/ui/')],
  ['audio', (cf, url) => url.startsWith('js/audio/')],
  ['vfx', (cf, url) => url.startsWith('js/vfx/')],
  ['match', (cf, url) => url.startsWith('js/match/') || url.startsWith('js/combat/')],
  ['boot (main.js)', (cf, url) => url === 'js/main.js'],
];

/**
 * @param {object} profile CDP Profiler.Profile
 * @param {string} origin page origin (stripped from urls)
 */
function summarizeProfile(profile, origin) {
  const byId = new Map();
  const parent = new Map();
  for (const n of profile.nodes) {
    byId.set(n.id, n);
    for (const c of n.children || []) parent.set(c, n.id);
  }
  const rel = (u) => (u && u.startsWith(origin) ? u.slice(origin.length + 1).split('?')[0] : u || '');
  const fkey = (cf) => `${cf.functionName || '(anonymous)'} ${rel(cf.url)}:${cf.lineNumber + 1}`;
  const buckets = {}, owners = {}, self = new Map(), incl = new Map();
  let total = 0;
  const add = (o, k, v) => { o[k] = (o[k] || 0) + v; };
  for (let i = 0; i < profile.samples.length; i++) {
    const ms = (profile.timeDeltas[i + 1] || 0) / 1000; // DevTools convention: sample i lasts until sample i+1
    if (!ms) continue;
    total += ms;
    const leaf = byId.get(profile.samples[i]);
    const b = bucketOf(leaf.callFrame, rel(leaf.callFrame.url));
    add(buckets, b, ms);
    const lk = fkey(leaf.callFrame);
    self.set(lk, (self.get(lk) || 0) + ms);
    let owner = null;
    const seen = new Set();
    for (let id = leaf.id; id !== undefined; id = parent.get(id)) {
      const cf = byId.get(id).callFrame;
      const url = rel(cf.url);
      if (!owner) { const o = OWNERS.find(([, test]) => test(cf, url)); if (o) owner = o[0]; }
      if (url.startsWith('js/')) {
        const k = fkey(cf);
        if (!seen.has(k)) { seen.add(k); incl.set(k, (incl.get(k) || 0) + ms); }
      }
    }
    if (!owner) owner = b === 'idle' || b === 'gc' || b === 'browser-native' ? b : 'other (module eval, loaders glue)';
    owners[owner] = owners[owner] || { ms: 0, kinds: {} };
    owners[owner].ms += ms;
    add(owners[owner].kinds, b, ms);
  }
  const round = (o) => Object.fromEntries(Object.entries(o).sort((a, b) => b[1] - a[1]).map(([k, v]) => [k, Math.round(v)]));
  const top = (m, n) => [...m].sort((a, b) => b[1] - a[1]).slice(0, n).map(([k, v]) => `${Math.round(v)} ms  ${k}`);
  const ownerOut = {};
  for (const [k, v] of Object.entries(owners).sort((a, b) => b[1].ms - a[1].ms)) {
    ownerOut[k] = { ms: Math.round(v.ms), kinds: Object.fromEntries(Object.entries(round(v.kinds)).filter(([, ms]) => ms >= 5)) };
  }
  return {
    sampledMs: Math.round(total),
    busyMs: Math.round(total - (buckets.idle || 0)),
    byKind: round(buckets),
    byOwner: ownerOut,
    topSelf: top(self, 15),
    topInclusiveGameJs: top(incl, 20),
  };
}

// ------------------------------------------------------------------------------------------ trace summary

/** Self time per event name on the renderer main thread and the GPU main thread of a Chrome trace. */
function summarizeTrace(file) {
  const events = JSON.parse(fs.readFileSync(file, 'utf8')).traceEvents || [];
  const threads = new Map(); // `${pid}:${tid}` -> name
  for (const e of events) if (e.ph === 'M' && e.name === 'thread_name') threads.set(`${e.pid}:${e.tid}`, e.args.name);
  const perThread = new Map();
  for (const e of events) {
    if (e.ph !== 'X' || typeof e.dur !== 'number') continue;
    const k = `${e.pid}:${e.tid}`;
    if (!perThread.has(k)) perThread.set(k, []);
    perThread.get(k).push(e);
  }
  // Flame-chart self time: events nest by time on one thread; self = duration - time covered by direct children.
  const summarize = (list) => {
    list.sort((a, b) => a.ts - b.ts || b.dur - a.dur);
    const nodes = [], stack = [];
    let busy = 0;
    for (const e of list) {
      while (stack.length && stack[stack.length - 1].end <= e.ts) stack.pop();
      const top = stack[stack.length - 1];
      if (top) top.child += Math.min(e.dur, top.end - e.ts); else busy += e.dur;
      const node = { name: e.name, dur: e.dur, end: e.ts + e.dur, child: 0 };
      stack.push(node);
      nodes.push(node);
    }
    const selfBy = {};
    for (const n of nodes) selfBy[n.name] = (selfBy[n.name] || 0) + Math.max(0, n.dur - n.child);
    const topNames = Object.entries(selfBy).sort((a, b) => b[1] - a[1]).slice(0, 12).map(([k, v]) => `${Math.round(v / 1000)} ms  ${k}`);
    return { busyMs: Math.round(busy / 1000), topSelf: topNames };
  };
  const pick = (name) => {
    let best = null;
    for (const [k, n] of threads) if (n === name && perThread.has(k) && (!best || perThread.get(k).length > perThread.get(best).length)) best = k;
    return best ? summarize(perThread.get(best)) : null;
  };
  return { rendererMain: pick('CrRendererMain'), gpuMain: pick('CrGpuMain'), file };
}

// ------------------------------------------------------------------------------------------ page-side helpers

/** Installed before any page script: bigger Resource Timing buffer, long-task log, error capture. */
function initScript() {
  try { performance.setResourceTimingBufferSize(10000); } catch (e) { /* older engines */ }
  const P = (window.__probe = { longTasks: [] });
  try {
    new PerformanceObserver((list) => { for (const e of list.getEntries()) P.longTasks.push([e.startTime, e.duration]); })
      .observe({ type: 'longtask', buffered: true });
  } catch (e) { /* no long task API */ }
}

/** Page-side snapshot: marks, paints, resources, long tasks, renderer counters. */
function snapshot() {
  const r3 = window.__DU && window.__DU.game && window.__DU.game.renderer && window.__DU.game.renderer.three;
  const nav = performance.getEntriesByType('navigation')[0];
  return {
    now: performance.now(),
    marks: performance.getEntriesByType('mark').filter((m) => m.name.startsWith('du:')).map((m) => [m.name, m.startTime]),
    paints: Object.fromEntries(performance.getEntriesByType('paint').map((p) => [p.name, p.startTime])),
    nav: nav ? { url: nav.name, end: nav.responseEnd, transfer: nav.transferSize, decoded: nav.decodedBodySize, dcl: nav.domContentLoadedEventEnd, load: nav.loadEventEnd } : null,
    resources: performance.getEntriesByType('resource').map((e) => ({
      url: e.name, start: e.startTime, end: e.responseEnd, transfer: e.transferSize, decoded: e.decodedBodySize, type: e.initiatorType,
    })),
    longTasks: (window.__probe && window.__probe.longTasks) || [],
    heapMB: performance.memory ? +(performance.memory.usedJSHeapSize / 1048576).toFixed(1) : null,
    gl: r3 ? {
      programs: r3.info.programs ? r3.info.programs.length : null,
      textures: r3.info.memory.textures, geometries: r3.info.memory.geometries,
      canvas: [r3.domElement.width, r3.domElement.height],
    } : null,
    quality: window.__DU && window.__DU.game && window.__DU.game.config ? window.__DU.game.config.quality : null,
  };
}

// ------------------------------------------------------------------------------------------ measurement

const r0 = (v) => (v == null ? null : Math.round(v));
const mb = (b) => +(b / 1048576).toFixed(2);
const kb = (b) => Math.round(b / 1024);

/** Groups resource entries (page-relative paths) into categories. */
function groupBytes(list, origin) {
  const by = {};
  let transfer = 0, decoded = 0;
  for (const r of list) {
    const p = r.url.startsWith(origin) ? r.url.slice(origin.length + 1).split('?')[0] : r.url;
    const c = categoryOf(p);
    by[c] = by[c] || { requests: 0, kb: 0, decodedKB: 0 };
    by[c].requests++;
    by[c].kb += r.transfer / 1024;
    by[c].decodedKB += r.decoded / 1024;
    transfer += r.transfer; decoded += r.decoded;
  }
  for (const v of Object.values(by)) { v.kb = Math.round(v.kb); v.decodedKB = Math.round(v.decodedKB); }
  const sorted = Object.fromEntries(Object.entries(by).sort((a, b) => b[1].kb - a[1].kb));
  return { requests: list.length, MB: mb(transfer), decodedMB: mb(decoded), byCategory: sorted };
}

/** Boot phases from the ordered 'du:boot:*' marks: each mark ends a phase that started at the previous one (or 0). */
function phasesFrom(marks, resources) {
  const boot = marks.filter(([n]) => n.startsWith('du:boot:')).sort((a, b) => a[1] - b[1]);
  const out = {};
  let prev = 0;
  for (const [name, t] of boot) {
    const id = name.slice('du:boot:'.length);
    const done = resources.filter((r) => r.end > prev && r.end <= t);
    out[id] = { ms: r0(t - prev), endMs: r0(t), requests: done.length, kb: kb(done.reduce((a, r) => a + r.transfer, 0)) };
    prev = t;
  }
  return out;
}

/** Spans from 'du:span:<name>:start|end' marks (--spans). */
function spansFrom(marks) {
  const open = new Map(), out = {};
  for (const [n, t] of [...marks].sort((a, b) => a[1] - b[1])) {
    const m = /^du:span:(.+):(start|end)$/.exec(n);
    if (!m) continue;
    const [, id, edge] = m;
    if (edge === 'start') { if (!open.has(id)) open.set(id, []); open.get(id).push(t); continue; }
    const starts = open.get(id);
    const s = starts && starts.length ? starts.shift() : null;
    if (s == null) continue;
    const o = (out[id] = out[id] || { count: 0, startMs: r0(s), endMs: 0, sumMs: 0 });
    o.count++; o.endMs = r0(t); o.sumMs += t - s;
  }
  for (const o of Object.values(out)) { o.sumMs = r0(o.sumMs); o.wallMs = o.endMs - o.startMs; }
  return out;
}

/** Rewrites the SPANS module responses on the fly (CDP Fetch, only those URLs are intercepted). */
async function installSpans(cdp) {
  const byFile = new Map();
  for (const s of SPANS) { if (!byFile.has(s.file)) byFile.set(s.file, []); byFile.get(s.file).push(s); }
  await cdp.send('Fetch.enable', { patterns: [...byFile.keys()].map((f) => ({ urlPattern: `*/${f}*`, requestStage: 'Response' })) });
  cdp.on('Fetch.requestPaused', async (ev) => {
    try {
      const p = new URL(ev.request.url).pathname.slice(1);
      const specs = byFile.get(p);
      const { body, base64Encoded } = await cdp.send('Fetch.getResponseBody', { requestId: ev.requestId });
      if (!specs || (ev.responseStatusCode && ev.responseStatusCode !== 200)) { await cdp.send('Fetch.continueRequest', { requestId: ev.requestId }); return; }
      const src = base64Encoded ? Buffer.from(body, 'base64').toString('utf8') : body;
      const wrap = specs.map((s) => `;(() => { try { const o = ${s.target}; const f = o && o[${JSON.stringify(s.method)}];
  if (typeof f !== 'function') return;
  o[${JSON.stringify(s.method)}] = function (...a) {
    const id = ${JSON.stringify('du:span:' + s.name)};
    performance.mark(id + ':start');
    let r;
    try { r = f.apply(this, a); } catch (e) { performance.mark(id + ':end'); throw e; }
    if (r && typeof r.then === 'function') return r.finally(() => performance.mark(id + ':end'));
    performance.mark(id + ':end');
    return r;
  }; } catch (e) { /* class not in this module version */ } })();`).join('\n');
      const headers = (ev.responseHeaders || []).filter((h) => !/^(content-encoding|content-length)$/i.test(h.name));
      await cdp.send('Fetch.fulfillRequest', { requestId: ev.requestId, responseCode: 200, responseHeaders: headers, body: Buffer.from(src + '\n' + wrap + '\n').toString('base64') });
    } catch (e) {
      try { await cdp.send('Fetch.continueRequest', { requestId: ev.requestId }); } catch { /* request gone */ }
    }
  });
}

/** Waits for window.__DU.ready, then for the first rendered frame (mark, or two rAFs on builds without marks). */
async function waitForMenu(page, timeout) {
  await page.waitForFunction(() => window.__DU && window.__DU.ready === true, null, { timeout, polling: 50 });
  const hasMark = await page.waitForFunction(() => performance.getEntriesByName('du:boot:firstFrame').length > 0, null, { timeout: Math.min(timeout, 120000), polling: 50 })
    .then(() => true).catch(() => false);
  let fallbackFrame = null;
  if (!hasMark) fallbackFrame = await page.evaluate(() => new Promise((res) => requestAnimationFrame(() => requestAnimationFrame(() => res(performance.now())))));
  // Hero select portraits (menu fully drawn): wait until every card image settled (loaded or failed).
  await page.waitForFunction(() => {
    const imgs = document.querySelectorAll('.hs-card img');
    return imgs.length === 0 || [...imgs].every((i) => i.complete);
  }, null, { timeout: 60000, polling: 50 }).catch(() => {});
  return fallbackFrame;
}

/** Summarises one load (cold or repeat) from a page snapshot. */
function summarizeLoad(snap, origin, fallbackFrame) {
  const mark = (id) => { const m = snap.marks.find(([n]) => n === 'du:boot:' + id); return m ? m[1] : null; };
  const ready = mark('ready');
  const readyAt = ready != null ? ready : snap.now;
  const all = snap.resources.slice();
  if (snap.nav) all.unshift({ url: snap.nav.url, start: 0, end: snap.nav.end, transfer: snap.nav.transfer, decoded: snap.nav.decoded, type: 'navigation' });
  const beforeMenu = all.filter((r) => r.end <= readyAt);
  const portraits = all.filter((r) => /\/portrait\.[a-z]+(\?|$)/.test(r.url));
  const longBefore = snap.longTasks.filter(([s]) => s <= (mark('firstFrame') || readyAt));
  const largest = beforeMenu.slice().sort((a, b) => b.transfer - a.transfer).slice(0, 10)
    .map((r) => `${kb(r.transfer)} KB  ${r.url.slice(origin.length + 1)}`);
  return {
    quality: snap.quality,
    menuMs: r0(ready),                                         // window.__DU.ready (hero select DOM shown)
    menuVisibleMs: r0(mark('firstFrame') ?? fallbackFrame),    // first rendered frame (menu composited over it)
    portraitsMs: portraits.length ? r0(Math.max(...portraits.map((r) => r.end))) : null,
    modulesMs: r0(mark('modules')),                            // module graph fetched + evaluated, boot() starts
    firstPaintMs: r0(snap.paints['first-paint']),
    firstContentfulPaintMs: r0(snap.paints['first-contentful-paint']),
    domContentLoadedMs: snap.nav ? r0(snap.nav.dcl) : null,
    beforeMenu: groupBytes(beforeMenu, origin),
    afterMenuUntilNow: groupBytes(all.filter((r) => r.end > readyAt), origin),
    longTasks: { count: longBefore.length, totalMs: r0(longBefore.reduce((a, [, d]) => a + d, 0)), maxMs: r0(longBefore.reduce((a, [, d]) => Math.max(a, d), 0)) },
    heapMB: snap.heapMB,
    gl: snap.gl,
    largestBeforeMenu: largest,
    phases: phasesFrom(snap.marks, all),
    spans: OPTS.spans ? spansFrom(snap.marks) : undefined,
  };
}

/** PLAY on hero select -> first match frame / countdown / playing. */
async function measurePlay(page, origin, timeout) {
  const heroIndex = await page.evaluate((hero) => {
    const g = window.__DU.game;
    const P = window.__probe;
    P.play = { phases: [], frames: [], t0: 0 };
    g.events.on('match:phase', (e) => P.play.phases.push([e.current, performance.now()]));
    const tick = g.tick;
    g.tick = function (ts) {
      const s = performance.now();
      const r = tick.call(this, ts);
      if (P.play.frames.length < 5000) P.play.frames.push([s, performance.now() - s, g.match ? g.match.phase : null]);
      return r;
    };
    const idx = hero ? (g.roster || []).findIndex((h) => h.id.toLowerCase() === String(hero).toLowerCase()) : -1;
    if (idx >= 0) { const card = document.querySelector(`.hs-card[data-i="${idx}"]`); if (card) card.click(); }
    return idx;
  }, OPTS.hero);
  const started = await page.evaluate(() => {
    const btn = document.querySelector('.hs-play');
    if (!btn) return false;
    window.__probe.play.t0 = performance.now();
    btn.click();
    return true;
  });
  if (!started) return { error: 'no .hs-play button (autoplay query or changed hero select)' };
  const reached = await page.waitForFunction(() => window.__probe.play.phases.some(([p]) => p === 'playing'), null, { timeout, polling: 100 })
    .then(() => true).catch(() => false);
  await page.waitForTimeout(300);
  const d = await page.evaluate(() => {
    const P = window.__probe.play;
    const g = window.__DU.game;
    return {
      P, now: performance.now(), setup: g.match && g.match.setup ? { home: g.match.setup.homeHeroes, away: g.match.setup.awayHeroes, local: g.match.setup.localHero } : null,
      resources: performance.getEntriesByType('resource').filter((e) => e.startTime >= P.t0)
        .map((e) => ({ url: e.name, start: e.startTime, end: e.responseEnd, transfer: e.transferSize, decoded: e.decodedBodySize })),
      programs: g.renderer && g.renderer.three.info.programs ? g.renderer.three.info.programs.length : null,
      heapMB: performance.memory ? +(performance.memory.usedJSHeapSize / 1048576).toFixed(1) : null,
    };
  });
  const { P } = d;
  const at = (phase) => { const e = P.phases.find(([p]) => p === phase); return e ? e[1] : null; };
  const pre = at('preRound'), cd = at('countdown'), playing = at('playing');
  const firstFrame = pre != null ? P.frames.find(([s]) => s >= pre) : null;
  const inWindow = P.frames.filter(([s]) => s >= P.t0 && (playing == null || s <= playing));
  const rel = (t) => (t == null ? null : r0(t - P.t0));
  return {
    heroIndex,
    setup: d.setup,
    reachedPlaying: reached,
    clickToPreRoundMs: rel(pre),                                           // players created + avatars loaded
    clickToFirstFrameMs: firstFrame ? r0(firstFrame[0] + firstFrame[1] - P.t0) : null, // first rendered match frame
    firstFrameMs: firstFrame ? r0(firstFrame[1]) : null,                   // its duration (shader compiles, uploads)
    clickToCountdownMs: rel(cd),
    clickToPlayingMs: rel(playing),
    frames: {
      count: inWindow.length,
      maxMs: r0(inWindow.reduce((a, f) => Math.max(a, f[1]), 0)),
      over100ms: inWindow.filter((f) => f[1] > 100).length,
    },
    loaded: groupBytes(d.resources.filter((r) => playing == null || r.end <= playing), origin),
    programsAfter: d.programs,
    heapMB: d.heapMB,
  };
}

async function runOnce(runIndex, server) {
  const origin = `http://127.0.0.1:${server.address().port}`;
  const profile = OPTS.mobile ? PROFILES.mobile : PROFILES.desktop;
  const browser = await chromium.launch({
    executablePath: OPTS.exe,
    args: ['--use-angle=swiftshader', '--enable-unsafe-swiftshader', '--ignore-gpu-blocklist', '--enable-webgl', '--autoplay-policy=no-user-gesture-required'],
  });
  const result = { run: runIndex + 1, errors: [] };
  try {
    const context = await browser.newContext(profile.context);
    await context.addInitScript(initScript);
    const page = await context.newPage();
    page.on('pageerror', (e) => result.errors.push('pageerror: ' + String(e && e.message || e).slice(0, 300)));
    page.on('console', (m) => { if (m.type() === 'error') result.errors.push('console: ' + m.text().slice(0, 300)); });
    page.on('requestfailed', (r) => result.errors.push(`requestfailed: ${r.url()} ${r.failure() && r.failure().errorText}`));
    page.on('response', (r) => { if (r.status() >= 400) result.errors.push(`http ${r.status()}: ${r.url()}`); });
    const cdp = await context.newCDPSession(page);
    await cdp.send('Network.enable');
    if (profile.network) await cdp.send('Network.emulateNetworkConditions', profile.network);
    if (profile.cpuSlowdown > 1) await cdp.send('Emulation.setCPUThrottlingRate', { rate: profile.cpuSlowdown });
    if (OPTS.spans) await installSpans(cdp);
    if (OPTS.cpu) {
      await cdp.send('Profiler.enable');
      await cdp.send('Profiler.setSamplingInterval', { interval: 1000 });
      await cdp.send('Profiler.start');
    }
    const tracePath = OPTS.trace && runIndex === 0 ? path.resolve(OPTS.trace) : null;
    if (tracePath) {
      await browser.startTracing(page, {
        path: tracePath, screenshots: false,
        categories: ['devtools.timeline', 'disabled-by-default-devtools.timeline', 'toplevel', 'v8', 'v8.execute', 'blink', 'loading', 'gpu', 'disabled-by-default-gpu.service'],
      });
    }
    const url = `${origin}/index.html${OPTS.query ? '?' + OPTS.query.replace(/^\?/, '') : ''}`;
    result.url = url;
    const wall0 = Date.now();
    await page.goto(url, { waitUntil: 'commit', timeout: OPTS.timeoutMs });
    const fallback = await waitForMenu(page, OPTS.timeoutMs);
    result.wallMs = Date.now() - wall0;
    if (tracePath) { await browser.stopTracing(); result.trace = summarizeTrace(tracePath); }
    if (OPTS.cpu) result.cpuBoot = summarizeProfile((await cdp.send('Profiler.stop')).profile, origin);
    Object.assign(result, summarizeLoad(await page.evaluate(snapshot), origin, fallback));

    if (OPTS.play) {
      if (OPTS.cpu) await cdp.send('Profiler.start');
      result.play = await measurePlay(page, origin, OPTS.timeoutMs);
      if (OPTS.cpu) result.cpuPlay = summarizeProfile((await cdp.send('Profiler.stop')).profile, origin);
    }
    if (OPTS.repeat) {
      await page.goto('about:blank');
      await page.goto(url, { waitUntil: 'commit', timeout: OPTS.timeoutMs });
      const fb = await waitForMenu(page, OPTS.timeoutMs);
      const warm = summarizeLoad(await page.evaluate(snapshot), origin, fb);
      result.repeat = {
        menuMs: warm.menuMs, menuVisibleMs: warm.menuVisibleMs, modulesMs: warm.modulesMs,
        beforeMenu: warm.beforeMenu, phases: warm.phases, note: 'same context: warm HTTP cache, GPU program cache of this browser',
      };
    }
  } catch (e) {
    result.errors.push('harness: ' + String(e && e.message || e).split('\n')[0]);
  } finally {
    await browser.close().catch(() => {});
  }
  result.errors = [...new Set(result.errors)].slice(0, 30);
  return result;
}

const median = (xs) => {
  const v = xs.filter((x) => typeof x === 'number').sort((a, b) => a - b);
  if (!v.length) return null;
  const m = v.length >> 1;
  return v.length % 2 ? v[m] : Math.round((v[m - 1] + v[m]) / 2 * 100) / 100;
};

/** Medians of the headline numbers across runs. */
function medianOf(runs) {
  const ok = runs.filter((r) => r.menuMs != null);
  if (!ok.length) return null;
  const pick = (f) => median(ok.map((r) => { try { return f(r); } catch { return null; } }));
  const phaseIds = [...new Set(ok.flatMap((r) => Object.keys(r.phases || {})))];
  const out = {
    menuMs: pick((r) => r.menuMs), menuVisibleMs: pick((r) => r.menuVisibleMs), portraitsMs: pick((r) => r.portraitsMs),
    modulesMs: pick((r) => r.modulesMs), firstContentfulPaintMs: pick((r) => r.firstContentfulPaintMs),
    beforeMenuMB: pick((r) => r.beforeMenu.MB), beforeMenuRequests: pick((r) => r.beforeMenu.requests),
    longTaskMs: pick((r) => r.longTasks.totalMs), maxLongTaskMs: pick((r) => r.longTasks.maxMs),
    phasesMs: Object.fromEntries(phaseIds.map((id) => [id, pick((r) => r.phases[id].ms)])),
  };
  if (ok.some((r) => r.play)) {
    out.play = {
      clickToPreRoundMs: pick((r) => r.play.clickToPreRoundMs), clickToFirstFrameMs: pick((r) => r.play.clickToFirstFrameMs),
      firstFrameMs: pick((r) => r.play.firstFrameMs), clickToPlayingMs: pick((r) => r.play.clickToPlayingMs),
      loadedMB: pick((r) => r.play.loaded.MB), maxFrameMs: pick((r) => r.play.frames.maxMs),
    };
  }
  if (ok.some((r) => r.repeat)) out.repeatMenuMs = pick((r) => r.repeat.menuMs);
  return out;
}

// ------------------------------------------------------------------------------------------ main

const server = await startServer(0, { pages: !OPTS.devServer });
const runs = [];
for (let i = 0; i < OPTS.runs; i++) {
  process.stderr.write(`[loadprobe] run ${i + 1}/${OPTS.runs} (${OPTS.mobile ? 'mobile' : 'desktop'}) ...\n`);
  const r = await runOnce(i, server);
  process.stderr.write(`[loadprobe]   menu ${r.menuMs} ms, visible ${r.menuVisibleMs} ms, ${r.beforeMenu ? r.beforeMenu.MB : '?'} MB` +
    `${r.play ? `, play->first frame ${r.play.clickToFirstFrameMs} ms` : ''}${r.errors.length ? `, ${r.errors.length} error(s)` : ''}\n`);
  runs.push(r);
}
server.close();

const report = {
  tool: 'Tools/web/loadprobe.mjs',
  date: new Date().toISOString(),
  profile: OPTS.mobile ? 'mobile (Pixel 7 landscape, 20 Mbps / 60 ms, CPU x4)' : 'desktop (1280x720, unthrottled)',
  server: OPTS.devServer ? 'serve.mjs dev (no-cache, no gzip)' : 'serve.mjs GitHub Pages emulation (gzip text, max-age=600, ETag)',
  gl: 'SwiftShader (software) - absolute GPU-side times are inflated',
  query: OPTS.query,
  median: medianOf(runs),
  runs,
};
const json = JSON.stringify(report, null, 1);
if (OPTS.out) fs.writeFileSync(path.resolve(OPTS.out), json);
console.log(json);
process.exitCode = runs.some((r) => r.menuMs == null || r.errors.length) ? 1 : 0;
