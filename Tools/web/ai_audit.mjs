// AI gameplay audit for single-ball 4v4 (3 infield + 1 starting outfielder per team): plays a headless AI-vs-AI
// match without rendering (?norender=1) and measures how the bots play the one ball - throws, passes by direction
// (in->out, out->in...), outfield throws / hits, interceptions, loose and held stints, possession violations, round
// lengths and outcomes, plus page errors. Prints JSON and a PASS/FAIL list; exit code 1 on any FAIL.
//   node Tools/web/ai_audit.mjs [seed=11] [seconds=240] [extra query, e.g. "difficulty=hard"]
// Thresholds are per 240 game-seconds (scaled to the run length). The rules workstream owns rules_audit.mjs; this
// script only looks at bot behaviour.
import { chromium } from 'playwright-core';
import { startServer } from './serve.mjs';

const seed = process.argv[2] || 11;
const seconds = Number(process.argv[3] || 240);
const extra = process.argv[4] ? `&${process.argv[4]}` : '';
const exe = process.env.PW_CHROMIUM || '/opt/pw-browsers/chromium-1194/chrome-linux/chrome';

const server = await startServer(0);
const browser = await chromium.launch({ executablePath: exe, args: ['--use-angle=swiftshader', '--enable-unsafe-swiftshader', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 800, height: 450 } });
const errs = [];
page.on('pageerror', (e) => errs.push('PAGE ' + e.message));
const httpErrs = [];
page.on('response', (res) => { if (res.status() >= 400) httpErrs.push(`${res.status()} ${res.url().replace(/^https?:\/\/[^/]+/, '')}`); });
page.on('console', (m) => { if (m.type() === 'error' || m.type() === 'warning') errs.push(m.type() + ' ' + m.text().slice(0, 240)); });
await page.goto(`http://127.0.0.1:${server.address().port}/index.html?autoplay=1&spectate=1&seed=${seed}&quality=low&norender=1${extra}`);
await page.waitForFunction(() => window.__DU && window.__DU.ready, null, { timeout: 90000 });
await page.evaluate(() => {
  const g = window.__DU.game;
  const A = (window.__ai = {
    ev: {}, passes: { 'in->out': 0, 'out->in': 0, 'in->in': 0, 'out->out': 0 }, throwsByZone: { infield: 0, outfield: 0 },
    hitsByZone: { infield: 0, outfield: 0 }, abilityThrows: 0, intercepts: 0, catches: 0, awards: {}, rounds: [],
    looseStints: [], heldStints: [], maxBalls: 0, badBallSamples: 0, samples: 0, roundStart: 0,
    violations: 0, illegalZoneSamples: 0, illegalWho: {}, centreCrowdSamples: 0, idleHolderSamples: 0,
  });
  const zoneOf = (p) => (p && p.zone) || 'infield';
  const emit = g.events.emit.bind(g.events);
  g.events.emit = (name, p) => {
    A.ev[name] = (A.ev[name] || 0) + 1;
    try {
      if (name === 'ball:thrown') {
        if (p.isAbility) A.abilityThrows++;
        if (!p.isPass) A.throwsByZone[zoneOf(p.thrower)]++;
      } else if (name === 'ball:passed') {
        const a = zoneOf(p.from) === 'outfield' ? 'out' : 'in', b = zoneOf(p.to) === 'outfield' ? 'out' : 'in';
        A.passes[`${a}->${b}`]++;
      } else if (name === 'ball:hitPlayer') {
        A.hitsByZone[zoneOf(p.attacker)]++;
      } else if (name === 'ball:caught') {
        if (p.intercepted) A.intercepts++; else A.catches++;
      } else if (name === 'ball:awarded') {
        A.awards[p.cause] = (A.awards[p.cause] || 0) + 1;
      } else if (name === 'possession:violation') {
        A.violations++;
      } else if (name === 'round:started') {
        A.roundStart = g.time.now;
      } else if (name === 'round:ended') {
        A.rounds.push({ reason: p.reason, winner: p.winner, duration: +(g.time.now - A.roundStart).toFixed(1) });
      }
    } catch (e) { /* instrumentation must never break the game */ }
    return emit(name, p);
  };
  // 10 Hz sampler: ball count, loose / held stints, illegal positions, holder idling.
  let looseSince = -1, heldBy = null, heldSince = 0;
  const inZone = (p) => {
    const c = g.court, b = c.confinement(p.team, p.zone), x = p.position.x, z = p.position.z, m = 0.25;
    // An ability that widens the motor confinement (Gouki's Tackle Intercept past the centre line) is legal.
    const mb = p.motor && p.motor._hasBounds ? p.motor._bounds : null;
    if (mb && (Math.abs(mb.minZ - b.minZ) > 0.05 || Math.abs(mb.maxZ - b.maxZ) > 0.05 || Math.abs(mb.minX - b.minX) > 0.05)) return true;
    if (x < b.minX - m || x > b.maxX + m || z < b.minZ - m || z > b.maxZ + m) return false;
    const h = b.hole;
    return !(h && x > h.minX + m && x < h.maxX - m && z > h.minZ + m && z < h.maxZ - m);
  };
  setInterval(() => {
    const m = g.match, bm = g.balls;
    if (!m || !bm) return;
    const now = g.time.now;
    const n = (bm.active ? bm.active.length : 0);
    A.maxBalls = Math.max(A.maxBalls, n, bm.matchBalls.length);
    if (!m.isPlaying) { looseSince = -1; heldBy = null; return; }
    A.samples++;
    if (bm.matchBalls.length !== 1) A.badBallSamples++;
    const ball = bm.ball || bm.matchBalls[0];
    if (!ball) return;
    const loose = ball.state === 'free' || ball.state === 'despawned';
    if (loose) { if (looseSince < 0) looseSince = now; } else if (looseSince >= 0) { A.looseStints.push(now - looseSince); looseSince = -1; }
    const h = ball.state === 'held' ? ball.holder : null;
    if (h !== heldBy) {
      if (heldBy) A.heldStints.push(now - heldSince);
      heldBy = h; heldSince = now;
    }
    for (const p of g.players) {
      if (!p.health || !p.health.isAlive || (p.fsm && p.fsm.incapReason)) continue;
      if (!inZone(p)) { A.illegalZoneSamples++; const k = `${p.hero && p.hero.id}:${p.zone}`; A.illegalWho[k] = (A.illegalWho[k] || 0) + 1; }
      if (p.zone === 'infield' && Math.abs(p.position.z) < 0.9) A.centreCrowdSamples++;
    }
    if (h && h.intentSource && h.canAct && now - heldSince > 4 && h.velocity && Math.hypot(h.velocity.x, h.velocity.z) < 0.2 && !(h.combat && h.combat.isCharging)) A.idleHolderSamples++;
  }, 100);
});
await page.waitForTimeout(seconds * 1000);
const r = await page.evaluate(() => {
  const g = window.__DU.game;
  const heroes = new Set(g.players.map((p) => p.hero && p.hero.id));
  return {
    a: window.__ai, now: g.time.now, stats: window.__DU.stats(), players: g.players.length, heroes: heroes.size,
    starters: g.players.filter((p) => p.isStartingOutfielder).length,
    possessionLimit: g.match && g.match.rules ? g.match.rules.possessionLimit : null,
  };
});
const A = r.a, ev = A.ev;
const sum = (xs) => xs.reduce((s, x) => s + x, 0);
const max = (xs) => (xs.length ? Math.max(...xs) : 0);
const mean = (xs) => (xs.length ? sum(xs) / xs.length : 0);
const passes = sum(Object.values(A.passes));
const k = Math.max(0.25, r.now / 240); // thresholds are per 240 game-seconds
const report = {
  seed, gameSeconds: Math.round(r.now), fps: Math.round(r.stats.fps), phase: r.stats.phase, round: r.stats.round, scores: r.stats.scores,
  players: r.players, heroes: r.heroes, startingOutfielders: r.starters, maxBallObjects: A.maxBalls, badBallSamples: A.badBallSamples,
  throws: A.throwsByZone, abilityThrows: A.abilityThrows, passes: A.passes, hits: A.hitsByZone, catches: A.catches,
  intercepts: A.intercepts, catchAttempts: ev['catch:attempt'] || 0, awards: A.awards, violations: A.violations,
  loose: { count: A.looseStints.length, max: +max(A.looseStints).toFixed(1), mean: +mean(A.looseStints).toFixed(2) },
  held: { count: A.heldStints.length, max: +max(A.heldStints).toFixed(2), mean: +mean(A.heldStints).toFixed(2) },
  rounds: A.rounds, matchesEnded: ev['match:ended'] || 0, casts: ev['ability:cast'] || 0,
  illegalZoneSamples: A.illegalZoneSamples, illegalWho: A.illegalWho, centreCrowdSamples: A.centreCrowdSamples,
  idleHolderSamples: A.idleHolderSamples, samples: A.samples,
};
console.log(JSON.stringify(report, null, 1));
const lim = Number.isFinite(r.possessionLimit) && r.possessionLimit > 0 ? r.possessionLimit : 10;
const checks = [
  ['no page/console errors', !errs.some((e) => e.startsWith('PAGE') || e.startsWith('error'))],
  ['exactly one ball', A.badBallSamples === 0 && A.maxBalls <= 1],
  ['throws (non-pass) >= 30 / 240 s', A.throwsByZone.infield + A.throwsByZone.outfield >= 30 * k],
  ['passes >= 4 / 240 s', passes >= 4 * k],
  ['>= 1 in->out pass', A.passes['in->out'] >= 1],
  ['>= 1 out->in pass', A.passes['out->in'] >= 1],
  ['outfield throws >= 4 / 240 s', A.throwsByZone.outfield >= 4 * k],
  ['outfield hits >= 1', A.hitsByZone.outfield >= 1],
  ['max loose stint <= 12 s', max(A.looseStints) <= 12],
  ['mean loose stint <= 4 s', mean(A.looseStints) <= 4],
  [`max held stint <= ${lim + 0.25} s`, max(A.heldStints) <= lim + 0.25],
  ['possession violations <= 1', A.violations <= 1],
  ['>= 1 round decided (eliminated/time)', A.rounds.some((x) => x.reason === 'eliminated' || x.reason === 'time')],
  ['bots stay in legal zones', A.illegalZoneSamples <= Math.max(3, A.samples * 0.002)],
];
let fail = 0;
for (const [name, ok] of checks) { if (!ok) fail++; console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}`); }
const unique = [...new Set(errs)];
if (httpErrs.length) console.log(`HTTP errors: ${[...new Set(httpErrs)].join(', ')}`);
console.log(unique.length ? `ERRORS/WARNINGS (${unique.length}):\n` + unique.slice(0, 20).join('\n') : 'no errors or warnings');
await browser.close();
server.close();
process.exitCode = fail ? 1 : 0;
