// Turns a copy of docs/ into the production site (run by deploy_pages.sh on the gh-pages tree; docs/ itself is never
// touched and stays the unbundled development build).
//   node Tools/web/build_site.mjs <siteDir> [--version <commit>] [--no-minify]
// 1. bundle.mjs: js/main.js + three + cannon-es + workers -> <siteDir>/bundle/<hash>/ (one minified ES module).
// 2. index.html: the import map and js/main.js are replaced by the bundle; preload hints are added for the bundle
//    (modulepreload) and assets/manifest.json, the hero-select portraits are prefetched.
// 3. sw.js: VERSION / HASHES / SHELL placeholders are filled in (see docs/sw.js). HASHES covers every file a page
//    may request except the network-first ones (html, sw.js, version.json), so the service worker serves them
//    cache-first and a new deploy re-downloads only files whose content changed.
// js/ and vendor/ stay in the site: the dev pages (dev/*.html) keep using them through their own import maps.
import crypto from 'crypto';
import fs from 'fs';
import path from 'path';
import { execFileSync } from 'child_process';
import { fileURLToPath } from 'url';

const here = path.dirname(fileURLToPath(import.meta.url));
const args = process.argv.slice(2);
const opt = (name, def) => { const i = args.indexOf('--' + name); return i >= 0 ? args[i + 1] : def; };
const site = args[0] && !args[0].startsWith('--') ? path.resolve(args[0]) : null;
if (!site || !fs.existsSync(path.join(site, 'index.html'))) {
  console.error('usage: node Tools/web/build_site.mjs <siteDir (a copy of docs/)> [--version <commit>] [--no-minify]');
  process.exit(2);
}
const rel = (f) => path.relative(site, f).split(path.sep).join('/');

// 1. Bundle ------------------------------------------------------------------------------------------------------
const bundleArgs = [path.join(here, 'bundle.mjs'), site, '--src', site, '--json'];
if (args.includes('--no-minify')) bundleArgs.push('--no-minify');
const bundle = JSON.parse(execFileSync(process.execPath, bundleArgs, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'inherit'] }).trim().split('\n').pop());

// 2. index.html ----------------------------------------------------------------------------------------------------
const manifest = JSON.parse(fs.readFileSync(path.join(site, 'assets', 'manifest.json'), 'utf8'));
const portraits = Object.values(manifest.heroes || {}).filter((h) => h.folder && h.portrait).map((h) => `assets/${h.folder}${h.portrait}`);
const indexFile = path.join(site, 'index.html');
let html = fs.readFileSync(indexFile, 'utf8');
const before = html;
html = html.replace(/<script type="importmap">[\s\S]*?<\/script>\n?/, '');
html = html.replace(/<script type="module" src="js\/main\.js"><\/script>/, `<script type="module" src="${bundle.main}"></script>`);
if (html === before || html.includes('js/main.js') || html.includes('importmap')) throw new Error('index.html: import map / js/main.js script tag not found - update build_site.mjs');
const hints = [
  `<link rel="modulepreload" href="${bundle.main}" />`,
  '<link rel="preload" href="assets/manifest.json" as="fetch" crossorigin="anonymous" />',
  ...portraits.map((p) => `<link rel="prefetch" href="${p}" />`),
].join('\n');
html = html.replace(/(<link rel="stylesheet"[^>]*>)/, `${hints}\n$1`);
fs.writeFileSync(indexFile, html);

// 3. sw.js ---------------------------------------------------------------------------------------------------------
const NETWORK_FIRST = /(^|\/)[^/]*\.html$|^sw\.js$|^version\.json$/;
const SKIP = /^\.|\/\.|\.md$|\.map$|\.test\.js$/;
const hashes = {};
(function walk(dir) {
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    const f = path.join(dir, e.name);
    const r = rel(f);
    if (e.isDirectory()) { if (!e.name.startsWith('.')) walk(f); continue; }
    if (SKIP.test(r) || NETWORK_FIRST.test(r)) continue;
    hashes[r] = crypto.createHash('sha256').update(fs.readFileSync(f)).digest('base64url').slice(0, 12);
  }
})(site);
const sorted = Object.fromEntries(Object.entries(hashes).sort(([a], [b]) => (a < b ? -1 : 1)));
const shell = [bundle.main, ...bundle.files.map((f) => f.path).filter((p) => p.endsWith('.js') && p !== bundle.main), 'css/game.css', 'assets/manifest.json', ...portraits]
  .filter((p) => sorted[p]);
const content = crypto.createHash('sha256').update(JSON.stringify(sorted)).digest('hex').slice(0, 8);
const version = `${opt('version', 'local')}-${content}`;
const swFile = path.join(site, 'sw.js');
let sw = fs.readFileSync(swFile, 'utf8');
const swBefore = sw;
sw = sw.replace("'__DU_VERSION__'", JSON.stringify(version))
  .replace('/*__DU_HASHES__*/ {}', JSON.stringify(sorted))
  .replace('/*__DU_SHELL__*/ []', JSON.stringify(shell));
if (sw === swBefore || sw.includes('__DU_')) throw new Error('sw.js placeholders not found - update build_site.mjs');
fs.writeFileSync(swFile, sw);

const kb = (n) => `${(n / 1024).toFixed(0)} KB`;
console.log(`[site] ${bundle.modules} modules -> ${bundle.main} (${bundle.files.filter((f) => f.path.endsWith('.js')).map((f) => `${path.basename(f.path)} ${kb(f.bytes)}`).join(', ')}); ` +
  `sw ${version}: ${Object.keys(sorted).length} cache-first files, ${shell.length} precached`);
