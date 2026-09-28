// Production bundle of the web build (deploy only - docs/ itself stays unbundled native ES modules for development).
//   node Tools/web/bundle.mjs <outDir> [--src docs] [--no-minify] [--json]
// Builds docs/js/main.js + everything it imports (game modules, three + addons via the import map in
// docs/index.html, cannon-es) into ONE minified ES module:
//   <outDir>/bundle/<hash>/main.js (+ .map)   and a flat file per module worker (e.g. audio.synthWorker.js).
// The hash covers every output file, so the directory name changes whenever the code does: the files are immutable
// and the service worker (docs/sw.js) may serve them cache-first forever.
// Source patterns handled here (checked on every build, the script fails loudly on anything it cannot bundle):
//   * bare specifiers ('three', 'three/addons/...', 'cannon-es') -> resolved through the page's import map;
//   * import(CONST) where `const CONST = '<literal>'` is declared in the same file -> rewritten to import('<literal>')
//     so esbuild inlines the target (assets.js: the lazily loaded meshopt decoder);
//   * new URL('./x.js', import.meta.url) used for module workers -> the worker is built as its own entry next to
//     main.js and the literal is rewritten to its output name (audio.js: synthWorker.js).
// Prints { dir, files: [{ path, bytes }], ... } as JSON with --json (deploy_pages.sh reads 'dir').
import * as esbuild from 'esbuild';
import crypto from 'crypto';
import fs from 'fs';
import path from 'path';
import { fileURLToPath } from 'url';

const here = path.dirname(fileURLToPath(import.meta.url));
const args = process.argv.slice(2);
const opt = (name, def) => { const i = args.indexOf('--' + name); return i >= 0 ? args[i + 1] : def; };
const flag = (name) => args.includes('--' + name);
const outDir = path.resolve(args.find((a, i) => !a.startsWith('--') && !(i > 0 && args[i - 1] === '--src')) || '');
if (!args.length || !outDir) {
  console.error('usage: node Tools/web/bundle.mjs <outDir> [--src docs] [--no-minify] [--json]');
  process.exit(2);
}
const src = path.resolve(opt('src', path.join(here, '..', '..', 'docs')));
const jsRoot = path.join(src, 'js');

/** Import map of the dev page: the single source of truth for bare specifiers. */
function readImportMap() {
  const html = fs.readFileSync(path.join(src, 'index.html'), 'utf8');
  const m = html.match(/<script type="importmap">([\s\S]*?)<\/script>/);
  if (!m) throw new Error('docs/index.html has no import map');
  return JSON.parse(m[1]).imports || {};
}

/** @param {string} dir @returns {string[]} */
function walkJs(dir) {
  const out = [];
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    const f = path.join(dir, e.name);
    if (e.isDirectory()) out.push(...walkJs(f));
    else if (e.name.endsWith('.js') && !e.name.endsWith('.test.js')) out.push(f);
  }
  return out;
}

const WORKER_URL = /new URL\(\s*(['"])(\.{1,2}\/[^'"]+\.m?js)\1\s*,\s*import\.meta\.url\s*\)/g;
const CONST_IMPORT = /\bimport\(\s*([A-Za-z_$][\w$]*)\s*\)/g;

/** Worker entries found in the sources: absolute file -> output name (no extension). */
function findWorkers() {
  const workers = new Map();
  for (const file of walkJs(jsRoot)) {
    const text = fs.readFileSync(file, 'utf8');
    for (const m of text.matchAll(WORKER_URL)) {
      const target = path.resolve(path.dirname(file), m[2]);
      if (!fs.existsSync(target)) throw new Error(`${path.relative(src, file)}: new URL('${m[2]}') points at a missing file`);
      workers.set(target, path.relative(jsRoot, target).replace(/\.m?js$/, '').split(path.sep).join('.'));
    }
  }
  return workers;
}

/**
 * esbuild plugin: import-map resolution + the two source rewrites described in the header.
 * @param {Record<string,string>} imports @param {Map<string,string>} workers
 */
function duPlugin(imports, workers) {
  const keys = Object.keys(imports).sort((a, b) => b.length - a.length);
  const resolveBare = (spec) => {
    for (const k of keys) {
      if (k.endsWith('/') ? spec.startsWith(k) : spec === k) {
        const target = k.endsWith('/') ? imports[k] + spec.slice(k.length) : imports[k];
        return path.resolve(src, target);
      }
    }
    return null;
  };
  return {
    name: 'du',
    setup(build) {
      build.onResolve({ filter: /^[^./]/ }, (a) => {
        const file = resolveBare(a.path);
        if (!file) return { errors: [{ text: `bare specifier '${a.path}' is not in the import map of docs/index.html` }] };
        if (!fs.existsSync(file)) return { errors: [{ text: `import map target missing: ${path.relative(src, file)}` }] };
        return { path: file };
      });
      build.onLoad({ filter: /\.m?js$/ }, (a) => {
        if (!a.path.startsWith(jsRoot + path.sep)) return undefined; // vendor code: untouched
        let text = fs.readFileSync(a.path, 'utf8');
        text = text.replace(CONST_IMPORT, (all, name) => {
          const decl = text.match(new RegExp(`\\bconst\\s+${name.replace(/\$/g, '\\$')}\\s*=\\s*(['"])([^'"]+)\\1`));
          if (!decl) throw new Error(`${path.relative(src, a.path)}: import(${name}) - no string constant to resolve it`);
          return `import(${JSON.stringify(decl[2])})`;
        });
        text = text.replace(WORKER_URL, (all, q, rel) => {
          const name = workers.get(path.resolve(path.dirname(a.path), rel));
          return `new URL(${JSON.stringify('./' + name + '.js')}, import.meta.url)`;
        });
        return { contents: text, loader: 'js' };
      });
    },
  };
}

const imports = readImportMap();
const workers = findWorkers();
const entryPoints = { main: path.join(jsRoot, 'main.js') };
for (const [file, name] of workers) entryPoints[name] = file;
const minify = !flag('no-minify');
const result = await esbuild.build({
  entryPoints,
  bundle: true,
  format: 'esm',
  target: 'esnext', // keep the source syntax: the unbundled build already requires it
  platform: 'browser',
  minify,
  keepNames: true, // error messages use constructor.name
  legalComments: 'eof',
  sourcemap: 'linked',
  sourcesContent: false,
  charset: 'utf8',
  write: false,
  metafile: true,
  outdir: path.join(outDir, 'bundle', '_'),
  logLevel: 'warning',
  // `Module.Named || Module.default` fallbacks (player.js, match.js) are intentional: undefined in both builds.
  logOverride: { 'import-is-undefined': 'silent' },
  plugins: [duPlugin(imports, workers)],
});

// Anything the page would still have to resolve at run time means the bundle is incomplete.
const leftovers = [];
for (const f of result.outputFiles) {
  if (!f.path.endsWith('.js')) continue;
  for (const m of f.text.matchAll(/\bimport\(\s*(?!["'`])([^)]{0,40})/g)) leftovers.push(`${path.basename(f.path)}: import(${m[1]}`);
  for (const m of f.text.matchAll(/(?:^|[;\n])\s*import\s*(?:[\w{},*\s$]+from\s*)?["']([^"']+)["']/g)) leftovers.push(`${path.basename(f.path)}: import '${m[1]}'`);
}
if (leftovers.length) {
  console.error('[bundle] unresolved imports left in the output:\n  ' + leftovers.join('\n  '));
  process.exit(1);
}

const hash = crypto.createHash('sha256');
for (const f of [...result.outputFiles].sort((a, b) => a.path.localeCompare(b.path))) hash.update(path.basename(f.path)).update(f.contents);
const id = hash.digest('hex').slice(0, 10);
const dir = path.join(outDir, 'bundle', id);
fs.mkdirSync(dir, { recursive: true });
const files = [];
for (const f of result.outputFiles) {
  const dst = path.join(dir, path.basename(f.path));
  fs.writeFileSync(dst, f.contents);
  files.push({ path: path.relative(outDir, dst).split(path.sep).join('/'), bytes: f.contents.length });
}
const inputs = Object.keys(result.metafile.inputs).length;
if (flag('analyze')) {
  for (const [name, o] of Object.entries(result.metafile.outputs)) {
    if (!name.endsWith('.js')) continue;
    const top = Object.entries(o.inputs).sort((a, b) => b[1].bytesInOutput - a[1].bytesInOutput).slice(0, 25);
    console.error(`[bundle] ${path.basename(name)}: largest inputs\n` + top.map(([f, v]) => `  ${(v.bytesInOutput / 1024).toFixed(1).padStart(7)} KB  ${path.relative(src, path.resolve(f))}`).join('\n'));
  }
}
const summary = { dir: path.relative(outDir, dir).split(path.sep).join('/'), main: `bundle/${id}/main.js`, modules: inputs, workers: [...workers.values()], files };
if (flag('json')) console.log(JSON.stringify(summary));
else console.log(`[bundle] ${inputs} modules -> ${summary.main} (${files.filter((f) => f.path.endsWith('.js')).map((f) => `${path.basename(f.path)} ${(f.bytes / 1024).toFixed(0)} KB`).join(', ')})`);
