// ---------------------------------------------------------------------------------------------------------------
// Service worker (deployed site only): makes repeat visits near-instant and never pins players to an old build.
// Registered by index.html (never on localhost unless ?sw=1; ?sw=0 unregisters it and deletes its caches).
//   * Tools/web/deploy_pages.sh replaces the three placeholders below: VERSION (commit + content hash), HASHES
//     (published path -> content hash of every cacheable file) and SHELL (paths precached on install).
//   * Cache-first for every path in HASHES (bundle/<hash>/, assets/, css/, js/, vendor/): one cache per deploy,
//     'du-<VERSION>'. On install, entries whose content hash did not change are copied over from the previous
//     deploy's cache (a new deploy only downloads what changed), then SHELL is fetched; activate deletes every other
//     'du-*' cache and claims open pages. skipWaiting + clients.claim: index.html reloads an already-controlled page
//     once when the new worker takes over, but only while hero select is open (never during a match).
//   * Network-first (cache as offline fallback) for navigations, index.html, version.json and anything else.
//   * Mixed builds are impossible: build_site.mjs stamps VERSION into index.html (<html data-du-build>). A navigation
//     whose fresh page names another build (a new deploy while this older worker still controls the scope) marks that
//     page as foreign: its requests bypass this worker's cache (revalidated network, nothing stored) until the new
//     worker takes over. The page asks the worker that claims it for its VERSION ('du:version' message) and reloads
//     only when that differs from its own build.
//   * Unreplaced placeholders (docs/ served by the dev server with ?sw=1) = dev mode: network-first for everything.
// ---------------------------------------------------------------------------------------------------------------
/* eslint-env serviceworker */
const VERSION = '__DU_VERSION__';
/** @type {Record<string,string>} published path (relative to the scope) -> content hash */
const HASHES = /*__DU_HASHES__*/ {};
/** @type {string[]} paths fetched on install */
const SHELL = /*__DU_SHELL__*/ [];

const DEV = VERSION.startsWith('__');
const PREFIX = 'du-';
const CACHE = PREFIX + (DEV ? 'dev' : VERSION);
/** Pseudo entry in each cache holding that deploy's HASHES (lets the next deploy reuse unchanged files). */
const META = '__du_hashes.json';
const SCOPE = new URL(self.registration.scope);

/** @param {string} rel @returns {string} absolute cache key (no query string) */
const keyOf = (rel) => new URL(rel, SCOPE).href;

/** @param {URL} url @returns {string|null} path relative to the scope ('' -> 'index.html'), null outside it */
function relOf(url) {
  if (url.origin !== SCOPE.origin || !url.pathname.startsWith(SCOPE.pathname)) return null;
  const rel = decodeURIComponent(url.pathname.slice(SCOPE.pathname.length));
  return rel === '' || rel.endsWith('/') ? rel + 'index.html' : rel;
}

/** Fetch that revalidates with the server (skips a possibly stale HTTP cache entry; a 304 is cheap). */
const fresh = (rel) => fetch(keyOf(rel), { cache: 'no-cache', credentials: 'same-origin' });

/**
 * Client ids of pages running another deploy's build (see header). Mirrored into this deploy's cache (FOREIGN entry)
 * because the browser may stop and restart an idle worker while such a page is still open.
 */
const foreign = new Set();
const FOREIGN = '__du_foreign.json';
const FOREIGN_MAX = 16;
let foreignLoaded = null;
/** @returns {Promise<void>} resolves once the persisted foreign ids are in `foreign` (read once per worker start) */
function loadForeign() {
  if (!foreignLoaded) {
    foreignLoaded = caches.open(CACHE).then((c) => c.match(keyOf(FOREIGN)))
      .then((r) => (r ? r.json() : []))
      .then((ids) => { for (const id of ids) foreign.add(id); }, () => undefined);
  }
  return foreignLoaded;
}
/** @param {string} id @param {boolean} on */
async function markForeign(id, on) {
  await loadForeign();
  if (on === foreign.has(id)) return;
  if (on) foreign.add(id); else foreign.delete(id);
  const ids = [...foreign].slice(-FOREIGN_MAX);
  const c = await caches.open(CACHE);
  await c.put(keyOf(FOREIGN), new Response(JSON.stringify(ids), { headers: { 'content-type': 'application/json' } }));
}
/** @param {string} html @returns {boolean} true when the page is stamped with a build other than this worker's */
const otherBuild = (html) => {
  const m = /data-du-build="([^"]*)"/.exec(html);
  return !!m && m[1] !== VERSION;
};

self.addEventListener('message', (event) => {
  if (event.data === 'du:version' && event.ports && event.ports[0]) event.ports[0].postMessage(DEV ? 'dev' : VERSION);
});

self.addEventListener('install', (event) => {
  event.waitUntil((async () => {
    const cache = await caches.open(CACHE);
    if (!DEV) {
      await cache.put(keyOf(META), new Response(JSON.stringify(HASHES), { headers: { 'content-type': 'application/json' } }));
      await carryOver(cache).catch(() => undefined);
      // Precache the shell (bundle, css, manifest, portraits). Failures are ignored: a missing entry is fetched on use.
      await Promise.all(SHELL.map(async (rel) => {
        if (await cache.match(keyOf(rel))) return;
        try {
          const res = await fresh(rel);
          if (res.ok) await cache.put(keyOf(rel), res);
        } catch (e) { /* offline or 404: fetched on first use instead */ }
      }));
    }
    await self.skipWaiting();
  })());
});

/** Copies unchanged files (same content hash) from older du-* caches into the new one. @param {Cache} cache */
async function carryOver(cache) {
  for (const name of await caches.keys()) {
    if (!name.startsWith(PREFIX) || name === CACHE) continue;
    const old = await caches.open(name);
    const metaRes = await old.match(keyOf(META));
    if (!metaRes) continue;
    const oldHashes = await metaRes.json();
    for (const req of await old.keys()) {
      const rel = relOf(new URL(req.url));
      if (!rel || rel === META || !HASHES[rel] || oldHashes[rel] !== HASHES[rel]) continue;
      if (await cache.match(req)) continue;
      const res = await old.match(req);
      if (res) await cache.put(req, res);
    }
  }
}

self.addEventListener('activate', (event) => {
  event.waitUntil((async () => {
    for (const name of await caches.keys()) {
      if (name.startsWith(PREFIX) && name !== CACHE) await caches.delete(name);
    }
    await self.clients.claim();
  })());
});

self.addEventListener('fetch', (event) => {
  const req = event.request;
  if (req.method !== 'GET' || req.headers.has('range')) return;
  const url = new URL(req.url);
  const rel = relOf(url);
  if (rel === null) return; // other origins: browser default
  if (!DEV && req.mode === 'navigate') {
    event.respondWith(navigate(req, rel, event.resultingClientId));
    return;
  }
  if (rel === 'sw.js') return;
  event.respondWith(route(req, rel, event.clientId));
});

/**
 * Subresources: another build's page goes to the network (revalidated, nothing stored), hashed files are cache-first,
 * everything else network-first.
 * @param {Request} req @param {string} rel @param {string} [clientId] @returns {Promise<Response>}
 */
async function route(req, rel, clientId) {
  if (!DEV && clientId) {
    await loadForeign();
    if (foreign.has(clientId)) return fetch(req, { cache: 'no-cache' });
  }
  if (!DEV && Object.prototype.hasOwnProperty.call(HASHES, rel)) return cacheFirst(rel);
  return networkFirst(req, rel);
}

/** @param {string} rel @returns {Promise<Response>} */
async function cacheFirst(rel) {
  const cache = await caches.open(CACHE);
  const hit = await cache.match(keyOf(rel));
  if (hit) return hit;
  const res = await fresh(rel);
  if (res.ok && res.status === 200) cache.put(keyOf(rel), res.clone()).catch(() => undefined);
  return res;
}

/**
 * Navigation: network-first, and a page stamped with another build is marked foreign (see header).
 * @param {Request} req @param {string} rel @param {string} [clientId] resultingClientId of the navigation
 * @returns {Promise<Response>}
 */
async function navigate(req, rel, clientId) {
  let res;
  try {
    res = await fetch(req.url, { cache: 'no-cache', credentials: 'same-origin' });
  } catch (e) {
    const hit = await (await caches.open(CACHE)).match(keyOf(rel));
    if (hit) return hit;
    throw e;
  }
  if (res.redirected) return Response.redirect(res.url, 302);
  if (!(res.ok && res.status === 200 && res.type === 'basic')) return res;
  let other = false;
  if ((res.headers.get('content-type') || '').includes('text/html')) {
    try { other = otherBuild(await res.clone().text()); } catch (e) { /* unreadable body: treat as this build */ }
  }
  if (clientId) await markForeign(clientId, other).catch(() => undefined);
  // Another build's page is never stored here: the offline fallback must name files this cache holds.
  if (!other) (await caches.open(CACHE)).put(keyOf(rel), res.clone()).catch(() => undefined);
  return res;
}

/** @param {Request} req @param {string} rel @returns {Promise<Response>} */
async function networkFirst(req, rel) {
  const cache = await caches.open(CACHE);
  try {
    // Always revalidate: a page from the HTTP cache (GitHub Pages: max-age=600) could name a bundle that is gone.
    const res = req.mode === 'navigate' ? await fetch(req.url, { cache: 'no-cache', credentials: 'same-origin' }) : await fetch(req, { cache: 'no-cache' });
    if (res.redirected && req.mode === 'navigate') return Response.redirect(res.url, 302);
    if (res.ok && res.status === 200 && res.type === 'basic') cache.put(keyOf(rel), res.clone()).catch(() => undefined);
    return res;
  } catch (e) {
    const hit = await cache.match(keyOf(rel));
    if (hit) return hit;
    throw e;
  }
}
