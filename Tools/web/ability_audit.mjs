// Ability audit (single-ball rules): plays headless AI-vs-AI matches without rendering (?norender=1) with FORCED
// line-ups so every hero is exercised - also as the starting outfielder (slot 3) - and reports, per ability id: casts,
// failures by reason, casts from the outfield, empowered throws (BallThrown isAbility), plus any ability ball spawned
// (must be 0), ball objects > 1 (must be 0) and page / console errors (must be 0).
//   node Tools/web/ability_audit.mjs [seed=11] [secondsPerLineup=90] [extra query, e.g. "difficulty=hard"]
// Exit code 1 on any FAIL.
import { chromium } from 'playwright-core';
import { startServer } from './serve.mjs';

const seed = process.argv[2] || 11;
const seconds = Number(process.argv[3] || 90);
const extra = process.argv[4] ? `&${process.argv[4]}` : '';
const exe = process.env.PW_CHROMIUM || '/opt/pw-browsers/chromium-1194/chrome-linux/chrome';

// Every hero appears in two line-ups, once as a starting outfielder (last slot) where possible.
const LINEUPS = [
  [['Rayne', 'Shadow', 'Gale', 'Elsa'], ['Bear', 'Gouki', 'Screws', 'Houdini']],
  [['Specter', 'Chrono', 'Houdini', 'Rayne'], ['Elsa', 'Gale', 'Shadow', 'Screws']],
  [['Screws', 'Bear', 'Rayne', 'Gouki'], ['Houdini', 'Specter', 'Elsa', 'Chrono']],
  [['Gouki', 'Elsa', 'Specter', 'Bear'], ['Chrono', 'Rayne', 'Houdini', 'Gale']],
];

const server = await startServer(0);
const browser = await chromium.launch({ executablePath: exe, args: ['--use-angle=swiftshader', '--enable-unsafe-swiftshader', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 800, height: 450 } });
const errs = [];
page.on('pageerror', (e) => errs.push('PAGE ' + e.message));
page.on('console', (m) => { if (m.type() === 'error') errs.push('error ' + m.text().slice(0, 240)); });
await page.goto(`http://127.0.0.1:${server.address().port}/index.html?autoplay=1&spectate=1&seed=${seed}&quality=low&norender=1${extra}`);
await page.waitForFunction(() => window.__DU && window.__DU.ready, null, { timeout: 90000 });

await page.evaluate(() => {
  const g = window.__DU.game;
  const A = (window.__ab = { casts: {}, outfieldCasts: {}, fails: {}, empowered: {}, abilityBallSpawns: 0, maxBalls: 0, starterCasts: 0 });
  const emit = g.events.emit.bind(g.events);
  g.events.emit = (name, p) => {
    try {
      if (name === 'ability:cast' && p && p.ability) {
        const id = p.ability.def.id;
        A.casts[id] = (A.casts[id] || 0) + 1;
        if (p.player && p.player.zone === 'outfield') A.outfieldCasts[id] = (A.outfieldCasts[id] || 0) + 1;
        if (p.player && p.player.isStartingOutfielder) A.starterCasts++;
      } else if (name === 'ability:failed' && p && p.ability) {
        const k = p.ability.def.id + ':' + p.reason;
        A.fails[k] = (A.fails[k] || 0) + 1;
      } else if (name === 'ball:thrown' && p && p.isAbility) {
        const st = (p.ball && p.ball.style) || '?';
        A.empowered[st] = (A.empowered[st] || 0) + 1;
      }
    } catch (e) { /* instrumentation never breaks the game */ }
    return emit(name, p);
  };
  const bm = g.balls;
  const spawn = bm.spawnAbilityBall.bind(bm);
  bm.spawnAbilityBall = (...a) => { A.abilityBallSpawns++; return spawn(...a); };
  setInterval(() => {
    const n = bm.active.filter((b) => !b.recycled).length;
    if (n > A.maxBalls) A.maxBalls = n;
  }, 100);
});

const lineupResults = [];
for (const [home, away] of LINEUPS) {
  const t0 = await page.evaluate(async ([h, a]) => {
    const g = window.__DU.game;
    await g.match.startMatch({ localHero: null, localTeam: 0, homeHeroes: h, awayHeroes: a, difficulty: g.match.setup?.difficulty || 'normal', spectate: true });
    return g.time.now;
  }, [home, away]);
  await page.waitForTimeout(seconds * 1000);
  const info = await page.evaluate(() => {
    const g = window.__DU.game;
    return { now: g.time.now, round: g.match.round, starters: g.players.filter((p) => p.isStartingOutfielder).map((p) => p.hero && (p.hero.id || p.heroId) || '?') };
  });
  lineupResults.push({ home, away, gameSeconds: Math.round(info.now - t0), round: info.round, starters: info.starters });
}

const r = await page.evaluate(() => window.__ab);
const checks = [
  ['no page/console errors', errs.length === 0],
  ['no ability ball spawned', r.abilityBallSpawns === 0],
  ['never more than 1 ball object', r.maxBalls <= 1],
];
console.log(JSON.stringify({ lineups: lineupResults, ...r, errors: errs.slice(0, 20) }, null, 1));
let fail = false;
for (const [name, ok] of checks) { console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}`); if (!ok) fail = true; }
await browser.close();
server.close();
process.exit(fail ? 1 : 0);
