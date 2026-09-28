// Minimal static file server for docs/ (the GitHub Pages site).  node Tools/web/serve.mjs [port] [--pages] [--root dir]
// --root / startServer(port, { root }) / env DU_ROOT serve another directory instead of docs/, e.g. the production
// site built by `Tools/web/deploy_pages.sh --dry-run <dir>` (smoke.mjs, audit.mjs and loadprobe.mjs honour DU_ROOT).
// Default: every response is a full 200 with 'cache-control: no-cache' (dev: edits show up on reload).
// startServer(port, { pages: true }) / --pages mimics GitHub Pages instead, so load measurements see the same wire
// bytes and cache behaviour as players: gzip for text types, 'cache-control: max-age=600', ETag / Last-Modified with
// 304 revalidation.
import http from 'http';
import fs from 'fs';
import path from 'path';
import zlib from 'zlib';
import { fileURLToPath } from 'url';

const docsRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..', 'docs');
const types = {
  '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.mjs': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8', '.json': 'application/json', '.glb': 'model/gltf-binary', '.webp': 'image/webp',
  '.png': 'image/png', '.jpg': 'image/jpeg', '.svg': 'image/svg+xml', '.txt': 'text/plain; charset=utf-8',
  '.md': 'text/markdown; charset=utf-8', '.wasm': 'application/wasm', '.hdr': 'application/octet-stream',
};
/** Types GitHub Pages (Fastly) serves gzip-compressed. Binary formats (glb, webp, png, wasm) go out as stored. */
const compressible = new Set(['.html', '.js', '.mjs', '.css', '.json', '.svg', '.txt', '.md']);

/**
 * @param {number} [port] 0 = any free port
 * @param {{ pages?: boolean, root?: string }} [options] pages: GitHub Pages headers (gzip, max-age=600, ETag + 304);
 *   root: directory to serve (default: env DU_ROOT, else docs/)
 * @returns {Promise<http.Server>}
 */
export function startServer(port = 0, { pages = false, root = process.env.DU_ROOT || docsRoot } = {}) {
  root = path.resolve(root);
  const gzCache = new Map(); // file -> { mtimeMs, body } (pages mode: compressed once, up front, like a CDN edge)
  if (pages) {
    const walk = (dir) => {
      for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
        const f = path.join(dir, e.name);
        if (e.isDirectory()) walk(f);
        else if (compressible.has(path.extname(f))) gzCache.set(f, { mtimeMs: fs.statSync(f).mtimeMs, body: zlib.gzipSync(fs.readFileSync(f), { level: 6 }) });
      }
    };
    walk(root);
  }
  const server = http.createServer((req, res) => {
    let p = decodeURIComponent(new URL(req.url, 'http://x').pathname);
    if (p.endsWith('/')) p += 'index.html';
    const file = path.join(root, p);
    if (!file.startsWith(root) || !fs.existsSync(file) || fs.statSync(file).isDirectory()) {
      res.writeHead(404); res.end('not found'); return;
    }
    const ext = path.extname(file);
    const type = types[ext] || 'application/octet-stream';
    if (!pages) {
      res.writeHead(200, { 'content-type': type, 'cache-control': 'no-cache' });
      fs.createReadStream(file).pipe(res);
      return;
    }
    const st = fs.statSync(file);
    const etag = `"${st.size.toString(16)}-${Math.floor(st.mtimeMs).toString(16)}"`;
    const headers = { 'content-type': type, 'cache-control': 'max-age=600', etag, 'last-modified': st.mtime.toUTCString(), vary: 'Accept-Encoding' };
    if (req.headers['if-none-match'] === etag) { res.writeHead(304, headers); res.end(); return; }
    if (compressible.has(ext) && /\bgzip\b/.test(req.headers['accept-encoding'] || '')) {
      let c = gzCache.get(file);
      if (!c || c.mtimeMs !== st.mtimeMs) {
        c = { mtimeMs: st.mtimeMs, body: zlib.gzipSync(fs.readFileSync(file), { level: 6 }) };
        gzCache.set(file, c);
      }
      res.writeHead(200, { ...headers, 'content-encoding': 'gzip', 'content-length': c.body.length });
      res.end(c.body);
      return;
    }
    res.writeHead(200, { ...headers, 'content-length': st.size });
    fs.createReadStream(file).pipe(res);
  });
  return new Promise((resolve) => server.listen(port, '127.0.0.1', () => resolve(server)));
}

if (process.argv[1] && fileURLToPath(import.meta.url) === path.resolve(process.argv[1])) {
  const args = process.argv.slice(2);
  const port = Number(args.find((a) => /^\d+$/.test(a)) || 8080);
  const ri = args.indexOf('--root');
  const s = await startServer(port, { pages: args.includes('--pages'), ...(ri >= 0 ? { root: args[ri + 1] } : {}) });
  console.log(`Dodgeball Ultra web build: http://127.0.0.1:${s.address().port}/${args.includes('--pages') ? ' (GitHub Pages headers)' : ''}${ri >= 0 ? ` serving ${path.resolve(args[ri + 1])}` : ''}`);
}
