// ---------------------------------------------------------------------------------------------------------------
// Dodgeball Ultra (web) - boot sequence (kernel). See Tools/web/WEB_ARCHITECTURE.md.
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
import { HeroSelect } from './ui/heroSelect.js';
import { Loading } from './ui/loading.js';
import { HEROES, heroById } from './abilities/roster.js';
import './abilities/heroes/index.js';

const params = new URLSearchParams(location.search);
const flag = (k) => params.get(k) === '1' || params.get(k) === 'true';

function detectQuality() {
  const q = params.get('quality');
  if (q === 'low' || q === 'medium' || q === 'high') return q;
  const mobile = /Android|iPhone|iPad|iPod/i.test(navigator.userAgent) || (navigator.maxTouchPoints > 1 && innerWidth < 1100);
  return mobile ? 'low' : 'high';
}

/** Distinct random heroes for the bot slots (deterministic with ?seed). */
export function buildSetup({ localHero, localTeam = TEAM.HOME, difficulty = 'normal', spectate = false, rng = game.rng }) {
  const pool = rng.shuffle(HERO_IDS.filter((h) => h !== localHero));
  const home = [], away = [];
  if (!spectate) (localTeam === TEAM.HOME ? home : away).push(localHero);
  for (const h of pool) {
    if (home.length < 3) home.push(h); else if (away.length < 3) away.push(h);
    if (home.length === 3 && away.length === 3) break;
  }
  return { localHero: spectate ? null : localHero, localTeam, homeHeroes: home, awayHeroes: away, difficulty, spectate };
}

async function boot() {
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
    const container = document.getElementById('app');
    game.renderer = new Renderer(container, game.config.quality);
    game.scene = game.renderer.scene;
    game.camera = game.renderer.camera;

    game.assets = new Assets(game.renderer.three);
    game.assets.onProgress = (loaded, total, label) => Loading.progress(loaded, total, label);
    await game.assets.loadManifest();
    game.roster = HEROES;

    game.physics = new Physics();
    game.addSystem(game.physics, ORDER.PHYSICS);

    game.arena = new Arena();
    await game.arena.build(game.scene);
    game.court = game.arena.court;

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

    // Preload every hero model + both animation sets so matches start instantly.
    const loads = [];
    for (const h of HEROES) {
      const def = game.assets.manifest.heroes[h.id];
      if (!def) { console.warn(`[boot] no realistic avatar for ${h.id} in assets/manifest.json`); continue; }
      loads.push(game.assets.gltf(def.folder + def.model));
    }
    for (const g of ['m', 'f']) for (const key of Object.keys(game.assets.manifest.clips[g] || {})) loads.push(game.assets.clip(g, key));
    await Promise.all(loads);

    game.start();
    Loading.hide();

    const difficulty = params.get('difficulty') || 'normal';
    if (game.config.autoplay) {
      const hero = heroById(params.get('hero')) ? params.get('hero') : game.rng.pick(HERO_IDS);
      const team = params.get('team') === 'away' ? TEAM.AWAY : TEAM.HOME;
      await game.match.startMatch(buildSetup({ localHero: hero, localTeam: team, difficulty, spectate: game.config.spectate }));
    } else {
      showHeroSelect();
    }
    window.__DU.ready = true;
  } catch (e) {
    console.error('[boot] failed', e);
    Loading.error(String(e && e.message || e));
  }
}

/** Opens hero select; also used by the pause menu / match end ("back to hero select"). */
export function showHeroSelect() {
  HeroSelect.show(HEROES, async (choice) => {
    HeroSelect.hide();
    await game.match.startMatch(buildSetup({ localHero: choice.hero, localTeam: choice.team, difficulty: choice.difficulty }));
  });
}
game.showHeroSelect = showHeroSelect;

window.__DU = {
  ready: false,
  game,
  stats: () => ({
    fps: Math.round(game.stats.fps), frameMs: +game.stats.frameMs.toFixed(2), quality: game.config.quality,
    phase: game.match && game.match.phase, round: game.match && game.match.round, scores: game.match && game.match.scores,
    players: game.players.map((p) => ({ id: p.id, hero: p.hero && p.hero.id, team: p.team, zone: p.zone, hp: p.health && p.health.hp, state: p.fsm && p.fsm.current })),
    balls: game.balls && game.balls.active ? game.balls.active.map((b) => ({ state: b.state, kmh: Math.round(b.speedKmh || 0) })) : [],
    draw: game.renderer && game.renderer.three ? game.renderer.three.info.render : null,
  }),
};

boot();
