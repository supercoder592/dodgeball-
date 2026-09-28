// ---------------------------------------------------------------------------------------------------------------
// Dodgeball Ultra (web) - boot sequence and lazy asset flow (kernel). See Tools/web/WEB_ARCHITECTURE.md.
//
//   boot: loading screen (painted before any WebGL work) ─► renderer ─► manifest ─► arena (the crowd atlas streams
//         in afterwards) ─► systems ─► 10 portraits ─► hero select   (window.__DU.ready)
//   menu idle: background prefetch, one hero per idle callback - the hero highlighted in the menu first, then roster
//         order. GLBs + clips are parsed, textures only warm the HTTP cache.
//   match start (PLAY / watch AI / ?autoplay / back to hero select): loading screen with the line-up while exactly
//         the line-up's heroes (model, maps, clip set of their gender) load ─► Match.startMatch ─► shader warm-up
//         (compileAsync + one hidden frame, preRound frozen under the overlay) ─► countdown.
// Avatar.load() asks game.assets for the same cached promises, so a player can never spawn before its model: the
// match preload only moves the wait in front of the loading bar and warms the GPU. A file that fails is retried once
// (game.assets never caches failures); a load that stops progressing shows an error with Back / Reload instead of
// holding the loading screen forever. WATCH AI keeps prefetching the rest of the roster while the (non-interactive)
// match runs, because the attract loop restarts with a random line-up without going through launchMatch.
// ---------------------------------------------------------------------------------------------------------------
import { game, ORDER } from './game.js';
import { Rng } from './core/rng.js';
import { TEAM, HERO_IDS } from './core/constants.js';
import { Assets } from './engine/assets.js';
import { Renderer } from './render/renderer.js';
import { Arena } from './world/arena.js';
import { Physics } from './characters/ragdoll.js';
import { CameraRig } from './camera/cameraRig.js';
import { Input } from './input/input.js';
import { Juice } from './juice/juice.js';
import { Vfx } from './vfx/vfx.js';
import { Audio } from './audio/audio.js';
import { BallManager } from './combat/balls.js';
import { Match } from './match/match.js';
import { Hud } from './ui/hud.js';
import { HeroSelect, portraitUrl } from './ui/heroSelect.js';
import { Loading } from './ui/loading.js';
import { Pause } from './ui/pause.js';
import { HEROES, heroById } from './abilities/roster.js';
import './abilities/heroes/index.js';

const params = new URLSearchParams(location.search);
const flag = (k) => params.get(k) === '1' || params.get(k) === 'true';

/**
 * Boot phase marker: a 'du:boot:<phase>' User Timing mark at the END of each boot phase (read by
 * Tools/web/loadprobe.mjs and the DevTools performance panel). A handful per page load - no measurable cost.
 * @param {string} phase
 */
const mark = (phase) => { if (typeof performance !== 'undefined' && performance.mark) performance.mark('du:boot:' + phase); };

/** Boot / match-load tuning. */
const LOAD = Object.freeze({
  portraitTimeoutMs: 6000,   // the menu opens even if a portrait is slow (the card shows its fallback)
  warmUpTimeoutMs: 10000,    // compileAsync cap (x2 for the warm-up frames): a stuck driver never holds the match
  idleTimeoutMs: 1500,       // requestIdleCallback timeout between background prefetch items
  stallTimeoutMs: 40000,     // match load: no byte received and no load settled for this long = stalled (error + back)
  stallCheckMs: 1000,        // stall watchdog period
  retryDelayMs: 800,         // match load: a failed file is requested once more after this delay
  firstPrefetchDelayMs: 600, // let the menu settle (first frames, input) before prefetching
  /** Material texture slots Avatar._makeMaterial() loads, with their colour space (srgb). */
  texSlots: Object.freeze([['map', true], ['normalMap', false], ['ormMap', false]]),
});

const wait = (ms) => new Promise((r) => setTimeout(r, ms));
/**
 * Resolves after `n` animation frames have been painted (the task after the last rAF callback, which runs after the
 * frame's paint), or after `capMs` (a hidden tab has no frames and must not stall).
 */
const frames = (n = 1, capMs = 250) => new Promise((resolve) => {
  let left = n, done = false;
  const finish = () => { if (!done) { done = true; resolve(); } };
  const step = () => { if (--left <= 0) setTimeout(finish, 0); else requestAnimationFrame(step); };
  requestAnimationFrame(step);
  setTimeout(finish, capMs);
});
const idle = () => new Promise((r) => {
  if (typeof requestIdleCallback === 'function') requestIdleCallback(() => r(), { timeout: LOAD.idleTimeoutMs });
  else setTimeout(r, 120);
});

function detectQuality() {
  const q = params.get('quality');
  if (q === 'low' || q === 'medium' || q === 'high') return q;
  const mobile = /Android|iPhone|iPad|iPod/i.test(navigator.userAgent) || (navigator.maxTouchPoints > 1 && innerWidth < 1100);
  return mobile ? 'low' : 'high';
}

/**
 * Distinct random heroes for the bot slots (deterministic with ?seed). Fills Match.defaults.playersPerTeam per team so
 * launchMatch() preloads the exact line-up (Match only tops up teams that are short).
 */
export function buildSetup({ localHero, localTeam = TEAM.HOME, difficulty = 'normal', spectate = false, rng = game.rng }) {
  const n = Math.max(1, Math.round((Match.defaults && Match.defaults.playersPerTeam) || 3));
  const pool = rng.shuffle(HERO_IDS.filter((h) => h !== localHero));
  const home = [], away = [];
  if (!spectate) (localTeam === TEAM.HOME ? home : away).push(localHero);
  for (const h of pool) {
    if (home.length < n) home.push(h); else if (away.length < n) away.push(h);
    if (home.length === n && away.length === n) break;
  }
  return { localHero: spectate ? null : localHero, localTeam, homeHeroes: home, awayHeroes: away, difficulty, spectate };
}

async function boot() {
  mark('modules'); // module graph fetched + evaluated
  const seed = Number(params.get('seed')) || (Date.now() % 100000);
  game.rng = new Rng(seed);
  game.config = {
    quality: detectQuality(), seed, params,
    spectate: flag('spectate'), autoplay: flag('autoplay') || flag('spectate'), debug: flag('debug'),
  };
  // Test hook: ?maxdt=0.5 lets slow software-rendered headless runs simulate in (near) real time.
  const maxdt = Number(params.get('maxdt'));
  if (maxdt > 0) game.maxFrameDt = Math.min(1, maxdt);
  // Test hook: ?norender=1 simulates at full frame rate without drawing (gameplay audits in software-GL browsers).
  if (params.get('norender') === '1') game.skipRender = true;
  Loading.show();
  try {
    // Let the loading screen paint before the synchronous WebGL context / PMREM / composer work.
    await frames(1);
    const container = document.getElementById('app');
    game.renderer = new Renderer(container, game.config.quality);
    game.scene = game.renderer.scene;
    game.camera = game.renderer.camera;
    mark('renderer');

    game.assets = new Assets(game.renderer.three);
    game.assets.onProgress = (loaded, total, label) => Loading.progress(loaded, total, label);
    await game.assets.loadManifest();
    game.roster = HEROES;
    mark('manifest');
    const portraits = preloadPortraits();

    game.physics = new Physics();
    game.addSystem(game.physics, ORDER.PHYSICS);

    game.arena = new Arena();
    await game.arena.build(game.scene); // the crowd atlas streams in afterwards (world/crowd.js)
    game.court = game.arena.court;
    mark('arena');

    game.input = game.addSystem(new Input(), ORDER.INPUT);
    game.cameraRig = game.addSystem(new CameraRig(), ORDER.CAMERA);
    game.juice = game.addSystem(new Juice(), ORDER.JUICE);
    game.vfx = game.addSystem(new Vfx(), ORDER.VFX);
    game.audio = game.addSystem(new Audio(), ORDER.AUDIO);
    game.balls = game.addSystem(new BallManager(), ORDER.BALLS);
    game.match = game.addSystem(new Match(), ORDER.MATCH);
    game.hud = game.addSystem(new Hud(), ORDER.HUD);
    for (const sys of [game.input, game.cameraRig, game.juice, game.vfx, game.audio, game.balls, game.match, game.hud]) {
      if (sys.init) await sys.init();
    }
    mark('systems');

    // Hero select only needs the portraits; models / maps / clips load per match (launchMatch) or in the background.
    await portraits;
    mark('preload');
    game.assets.onProgress = null; // background loads must not drive the (hidden) loading bar

    game.start();
    // Queued after the kernel loop's first callback: marks the end of the first rendered frame (shader compiles).
    requestAnimationFrame(() => mark('firstFrame'));

    const difficulty = params.get('difficulty') || 'normal';
    if (game.config.autoplay) {
      const hero = heroById(params.get('hero')) ? params.get('hero') : game.rng.pick(HERO_IDS);
      const team = params.get('team') === 'away' ? TEAM.AWAY : TEAM.HOME;
      await launchMatch(buildSetup({ localHero: hero, localTeam: team, difficulty, spectate: game.config.spectate }));
    } else {
      Loading.hide();
      showHeroSelect();
    }
    mark('ready');
    window.__DU.ready = true;
  } catch (e) {
    console.error('[boot] failed', e);
    Loading.error(String(e && e.message || e));
  }
}

// =================================================================================================== portraits
/** Fetches + decodes the 10 hero select portraits (never rejects; capped by LOAD.portraitTimeoutMs). */
function preloadPortraits() {
  const all = HEROES.map((h) => new Promise((resolve) => {
    const img = new Image();
    img.decoding = 'async';
    img.onload = () => { (img.decode ? img.decode() : Promise.resolve()).then(resolve, resolve); };
    img.onerror = () => resolve();
    img.src = portraitUrl(h.id);
  }));
  return Promise.race([Promise.all(all), wait(LOAD.portraitTimeoutMs)]);
}

// =================================================================================================== asset sets
const heroDef = (id) => (game.assets && game.assets.manifest && game.assets.manifest.heroes[id]) || null;
const genderOf = (def) => (def && def.gender === 'female' ? 'f' : 'm');

/** Texture files [path, srgb] Avatar._buildMaterials() will request for a hero (hidden parts skipped). */
function heroTextures(id) {
  const def = heroDef(id);
  if (!def) return [];
  const hero = heroById(id);
  const hidden = (hero && hero.hiddenParts) || [];
  const out = [];
  for (const [name, spec] of Object.entries(def.materials || {})) {
    // Same match as Avatar._buildMaterials (material name vs roster hiddenParts).
    if (hidden.some((part) => name === part || name.endsWith('_' + part) || name.includes(part))) continue;
    for (const [slot, srgb] of LOAD.texSlots) if (spec[slot]) out.push([def.folder + spec[slot], srgb]);
  }
  return out;
}

/** Clip keys of a gender set (Avatar loads every clip of its gender). */
const clipKeys = (g) => Object.keys((game.assets.manifest.clips && game.assets.manifest.clips[g]) || {});

/**
 * Loads everything the heroes of a match setup need (models, decoded maps, clip sets of their genders) with the
 * loading bar. A file that fails is requested once more; one that still fails is left to Avatar.load() (which
 * requests it again and reports it exactly as before). Resolves early when nothing progressed for
 * LOAD.stallTimeoutMs (a request that never answers).
 * @param {string[]} heroIds
 * @returns {Promise<string[]>} paths still pending when the load stalled ([] = complete)
 */
async function loadHeroes(heroIds) {
  const a = game.assets;
  const jobs = [];
  const genders = new Set();
  const blank = (t) => { if (!t || !t.image) throw new Error('texture failed'); };
  for (const id of heroIds) {
    const def = heroDef(id);
    if (!def) continue;
    genders.add(genderOf(def));
    const model = def.folder + def.model;
    jobs.push([model, () => a.gltf(model)]);
    for (const [path, srgb] of heroTextures(id)) jobs.push([path, () => a.textureAsync(path, srgb).then(blank)]);
  }
  for (const g of genders) {
    for (const key of clipKeys(g)) {
      const file = game.assets.manifest.clips[g][key].file;
      jobs.push([file, () => a.gltf(file)]); // Assets.clip() clones from this cached GLTF
    }
  }
  let done = 0;
  const total = jobs.length;
  const pending = new Set();
  a.activityAt = performance.now();
  Loading.progress(0, total, jobs.length ? jobs[0][0] : '');
  const all = Promise.allSettled(jobs.map(([label, run]) => {
    pending.add(label);
    return Promise.resolve().then(run)
      .catch(() => wait(LOAD.retryDelayMs).then(run)) // failures are not cached: this is a new request
      .finally(() => {
        pending.delete(label);
        done++;
        Loading.progress(done, total, label);
      });
  }));
  let finished = false;
  all.then(() => { finished = true; });
  while (!finished) {
    await Promise.race([all, wait(LOAD.stallCheckMs)]);
    if (!finished && performance.now() - a.activityAt > LOAD.stallTimeoutMs) return [...pending];
  }
  return [];
}

// =================================================================================================== match launch
let _launchToken = 0;
let _launching = false;

/**
 * Starts a match behind the loading screen: loads the line-up's heroes, spawns them (Match.startMatch), then warms up
 * shaders + texture uploads before the countdown. A newer launch (or match end) supersedes an older one.
 * @param {object} setup buildSetup() result
 */
async function launchMatch(setup) {
  const token = ++_launchToken;
  _launching = true;
  const heroes = [...(setup.homeHeroes || []), ...(setup.awayHeroes || [])].filter((h, i, all) => h && all.indexOf(h) === i);
  // Roster entries have no display name: the id is the name (hero select shows it upper-cased as well).
  const names = (list) => (list || []).map((id) => String((heroById(id) && heroById(id).id) || id).toUpperCase());
  Loading.show({ label: 'Loading heroes · 載入角色', matchup: { home: names(setup.homeHeroes), away: names(setup.awayHeroes) } });
  let failed = false;
  try {
    await frames(1); // paint the overlay before any parsing
    const stalled = await loadHeroes(heroes);
    if (token !== _launchToken) return;
    if (stalled.length) {
      // A request that never answers: abandon it (the next launch requests it again) and let the player leave.
      failed = true;
      _launching = false;
      for (const path of stalled) game.assets.forget(path);
      console.warn('[boot] match load stalled', stalled);
      if (game.input) game.input.releaseLock(); // PLAY locked the mouse: the buttons must be clickable
      Loading.error(`${stalled.length} file(s) did not finish downloading · ${stalled.length} 個檔案未能下載完成`, {
        title: 'Download stalled · 下載停滯',
        hint: 'Check your connection, then go back and try again. 請檢查網路連線，返回後再試一次。',
        back: () => { Loading.hide(); showHeroSelect(); },
      });
      return;
    }
    if (typeof performance !== 'undefined' && performance.mark) performance.mark('du:match:assets');
    Loading.status('Entering the court · 進場');
    await game.match.startMatch(setup);
    if (token !== _launchToken) return;
    await warmUpMatch(token);
    if (typeof performance !== 'undefined' && performance.mark) performance.mark('du:match:warm');
  } finally {
    if (token === _launchToken && !failed) {
      _launching = false;
      Loading.hide();
    }
  }
  // WATCH AI: the attract loop restarts with a random line-up (Match._afterMatchEnd), so keep prefetching the rest.
  if (setup.spectate && token === _launchToken) runPrefetch(LOAD.firstPrefetchDelayMs);
}

/**
 * Compiles every program of the populated scene (compileAsync with KHR_parallel_shader_compile, otherwise a
 * synchronous compile()) and draws one hidden frame for texture uploads and shadow/depth
 * variants, with the match clock frozen under the overlay. Skipped with ?norender.
 */
async function warmUpMatch(token) {
  const r = game.renderer, three = r && r.three;
  if (!three || game.skipRender || !game.match.players || !game.match.players.length) return;
  Loading.status('Warming up · 準備畫面');
  const wasPaused = game.time.paused;
  game.time.paused = true;
  game.skipRender = true;
  try {
    // Without KHR_parallel_shader_compile (asked via has(): get() would log a warning) compile() links
    // synchronously, which is fine here - the overlay is up and the clock is frozen.
    const parallel = !!(three.extensions && three.extensions.has && three.extensions.has('KHR_parallel_shader_compile'));
    // Compile against the composer's scene target: output colour space / tone mapping are part of the program key.
    const prev = three.getRenderTarget();
    three.setRenderTarget(r.composer ? r.composer.readBuffer : null);
    let pending = null;
    try {
      if (parallel && typeof three.compileAsync === 'function') pending = three.compileAsync(game.scene, game.camera);
      else three.compile(game.scene, game.camera);
    } finally {
      three.setRenderTarget(prev);
    }
    if (pending) await Promise.race([pending, wait(LOAD.warmUpTimeoutMs)]);
  } catch (e) {
    console.warn('[boot] shader warm-up skipped', e);
  }
  game.skipRender = false;
  // Wait for real frames: the GPU may still be linking (no frames are produced meanwhile). The cap only guards
  // against a tab that went hidden (no frames at all).
  if (token === _launchToken && !document.hidden) await frames(2, LOAD.warmUpTimeoutMs * 2);
  // Unfreeze unless the player opened the pause menu meanwhile.
  if (!Pause.isOpen) game.time.paused = wasPaused;
}

// =================================================================================================== prefetch
let _prefetching = false;
const _prefetched = new Set();

/**
 * Background work is allowed on the menu and while spectating (WATCH AI: no player input to hitch), never during a
 * played match or while one loads.
 */
const spectating = () => !!(game.match && game.match.setup && game.match.setup.spectate && game.match.phase !== 'idle');
const canPrefetch = () => !_launching && (HeroSelect.visible || spectating());

/** Next background item: the hero highlighted in the menu first, then roster order. */
function nextPrefetch() {
  const cur = HeroSelect.roster && HeroSelect.roster[HeroSelect.index];
  if (cur && !_prefetched.has(cur.id) && heroDef(cur.id)) return cur.id;
  for (const h of HEROES) if (!_prefetched.has(h.id) && heroDef(h.id)) return h.id;
  return null;
}

/** Waits for a background load, but never longer than LOAD.stallTimeoutMs (a stalled request must not end the queue). */
const settledOrStalled = (p) => Promise.race([p.catch(() => null), wait(LOAD.stallTimeoutMs)]);

/** Prefetches one hero: parses its model and its gender's clips, warms the HTTP cache for its maps. */
async function prefetchHero(id) {
  const a = game.assets, def = heroDef(id);
  if (!def) return;
  await settledOrStalled(a.gltf(def.folder + def.model));
  const g = genderOf(def);
  for (const key of clipKeys(g)) {
    if (!canPrefetch()) break;
    await settledOrStalled(a.gltf(a.manifest.clips[g][key].file));
  }
  for (const [path] of heroTextures(id)) {
    if (!canPrefetch()) break;
    await settledOrStalled(a.prefetch(path));
  }
}

/** Runs the background queue one item per idle callback while the menu is up; restarted by showHeroSelect(). */
async function runPrefetch(delayMs = 0) {
  if (_prefetching || !game.assets) return;
  _prefetching = true;
  try {
    if (delayMs) await wait(delayMs);
    for (;;) {
      await idle();
      if (!canPrefetch()) break;
      const item = nextPrefetch();
      if (!item) break;
      _prefetched.add(item); // marked first: a failing hero is not retried in a loop
      await prefetchHero(item);
    }
  } catch (e) {
    console.warn('[boot] background prefetch stopped', e);
  } finally {
    _prefetching = false;
  }
}

/** Opens hero select; also used by the pause menu / match end ("back to hero select"). */
export function showHeroSelect() {
  _launchToken++; // a match still loading behind the overlay is abandoned
  if (_launching) { _launching = false; Loading.hide(); }
  HeroSelect.show(HEROES, (choice) => {
    HeroSelect.hide();
    return launchMatch(buildSetup({ localHero: choice.hero, localTeam: choice.team, difficulty: choice.difficulty, spectate: !!choice.spectate }));
  });
  runPrefetch(LOAD.firstPrefetchDelayMs);
}
game.showHeroSelect = showHeroSelect;

window.__DU = {
  ready: false,
  game,
  /** Lazy-loading state for probes / DevTools: background prefetch queue and match launch. */
  loader: () => ({ prefetching: _prefetching, launching: _launching, prefetched: [..._prefetched] }),
  stats: () => ({
    fps: Math.round(game.stats.fps), frameMs: +game.stats.frameMs.toFixed(2), quality: game.config.quality,
    phase: game.match && game.match.phase, round: game.match && game.match.round, scores: game.match && game.match.scores,
    players: game.players.map((p) => ({ id: p.id, hero: p.hero && p.hero.id, team: p.team, zone: p.zone, hp: p.health && p.health.hp, state: p.fsm && p.fsm.current })),
    balls: game.balls && game.balls.active ? game.balls.active.map((b) => ({ state: b.state, kmh: Math.round(b.speedKmh || 0) })) : [],
    draw: game.renderer && game.renderer.three ? game.renderer.three.info.render : null,
  }),
};

boot();
