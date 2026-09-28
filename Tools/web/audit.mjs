// Gameplay audit: plays a headless AI-vs-AI match without rendering (?norender=1, so the simulation runs at the
// browser's full frame rate even with software GL) and reports what actually happened - throws, hits, catch attempts,
// perfect catches, ability casts, matches finished - plus any page errors/warnings.
//   node Tools/web/audit.mjs [seed=11] [seconds=150] [extra query, e.g. "difficulty=hard"]
import { chromium } from 'playwright-core';
import { startServer } from './serve.mjs';

const seed = process.argv[2] || 11;
const seconds = Number(process.argv[3] || 150);
const extra = process.argv[4] ? `&${process.argv[4]}` : '';
const exe = process.env.PW_CHROMIUM || '/opt/pw-browsers/chromium-1194/chrome-linux/chrome';

const server = await startServer(0);
const browser = await chromium.launch({ executablePath: exe, args: ['--use-angle=swiftshader', '--enable-unsafe-swiftshader', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 800, height: 450 } });
const errs = [];
page.on('pageerror', (e) => errs.push('PAGE ' + e.message));
page.on('console', (m) => { if (m.type() === 'error' || m.type() === 'warning') errs.push(m.type() + ' ' + m.text().slice(0, 240)); });
await page.goto(`http://127.0.0.1:${server.address().port}/index.html?autoplay=1&spectate=1&seed=${seed}&quality=low&norender=1${extra}`);
await page.waitForFunction(() => window.__DU && window.__DU.ready, null, { timeout: 90000 });
await page.evaluate(() => {
  const g = window.__DU.game;
  const A = (window.__audit = { ev: {}, casts: {}, catches: {}, outcomes: {} });
  const emit = g.events.emit.bind(g.events);
  g.events.emit = (name, p) => {
    A.ev[name] = (A.ev[name] || 0) + 1;
    if (name === 'ability:cast') A.casts[p.ability.def.id] = (A.casts[p.ability.def.id] || 0) + 1;
    if (name === 'ball:caught') A.catches[p.quality] = (A.catches[p.quality] || 0) + 1;
    if (name === 'ball:hitPlayer') A.outcomes[p.outcome] = (A.outcomes[p.outcome] || 0) + 1;
    return emit(name, p);
  };
});
await page.waitForTimeout(seconds * 1000);
const r = await page.evaluate(() => ({ a: window.__audit, now: window.__DU.game.time.now, stats: window.__DU.stats() }));
const ev = r.a.ev;
console.log(JSON.stringify({
  gameSeconds: Math.round(r.now), fps: Math.round(r.stats.fps), phase: r.stats.phase, round: r.stats.round, scores: r.stats.scores,
  throws: ev['ball:thrown'] || 0, hits: ev['ball:hitPlayer'] || 0, catchAttempts: ev['catch:attempt'] || 0,
  matchesEnded: ev['match:ended'] || 0, revives: ev['player:revived'] || 0,
  catches: r.a.catches, outcomes: r.a.outcomes, casts: r.a.casts,
}, null, 1));
const unique = [...new Set(errs)];
console.log(unique.length ? `ERRORS/WARNINGS (${unique.length}):\n` + unique.slice(0, 20).join('\n') : 'no errors or warnings');
await browser.close();
server.close();
process.exitCode = errs.some((e) => e.startsWith('PAGE') || e.startsWith('error')) ? 1 : 0;
