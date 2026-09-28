// Single-ball rules audit: plays a headless AI-vs-AI match without rendering (?norender=1) and checks the 4v4
// one-ball rules. Reports:
//   * dodgeballs in play (match balls + any live ability ball; must never exceed 1), spawnAbilityBall calls;
//   * players per team, distinct heroes, starting outfielders (never leave the outfield, never revived / returned);
//   * serves per round vs the rule (round 1 coin, then the loser, after a draw the team that did not serve);
//   * possession-limit warnings / violations, longest held stint, longest loose stint and longest UNOWNED stint;
//   * passes by direction (in->out, out->in, in->in, out->out), interceptions;
//   * throws / hits split by thrower zone, catches (perfect / normal), eliminations, revives by cause;
//   * round reasons and durations; players outside their legal zone (sampled every 0.5 s vs court.confinement);
//   * page / console errors and HTTP 4xx/5xx responses.
// Prints a JSON report plus a PASS/FAIL list; exit code 1 on any FAIL.
//   node Tools/web/rules_audit.mjs [seed=11] [seconds=150] [extra query, e.g. "difficulty=hard"]
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
const httpErrs = [];
page.on('pageerror', (e) => errs.push('PAGE ' + e.message));
page.on('console', (m) => { if (m.type() === 'error') errs.push('error ' + m.text().slice(0, 240)); });
page.on('response', (res) => { if (res.status() >= 400) httpErrs.push(`${res.status()} ${res.url().replace(/^https?:\/\/[^/]+/, '')}`); });
await page.goto(`http://127.0.0.1:${server.address().port}/index.html?autoplay=1&spectate=1&seed=${seed}&quality=low&norender=1${extra}`);
await page.waitForFunction(() => window.__DU && window.__DU.ready, null, { timeout: 120000 });

await page.evaluate(() => {
  const g = window.__DU.game;
  const Court = g.court.constructor;
  const A = (window.__rules = {
    ev: {}, spawnAbilityBall: 0, samples: 0, badBallSamples: 0, maxBalls: 0, badPlayers: 0,
    starterZoneViolations: 0, starterRevives: 0, starterZoneEvents: 0, heroes: new Set(), starterCount: [0, 0],
    looseStints: [], unownedStints: [], heldStints: [],
    throwsByZone: { infield: 0, outfield: 0 }, abilityThrows: 0, passesTotal: 0,
    passes: { 'in->out': 0, 'out->in': 0, 'in->in': 0, 'out->out': 0 }, lobs: 0,
    hitsByZone: { infield: 0, outfield: 0 }, landedByZone: { infield: 0, outfield: 0 },
    catches: { perfect: 0, normal: 0 }, intercepts: 0, eliminations: 0, eliminationsByCause: {}, revives: {},
    awards: {}, serves: [], expectedServes: [], rounds: [], violations: 0, warnings: 0,
    illegalSamples: 0, illegalWho: {}, zoneSamples: 0, notes: [], roundStart: -1,
    passOutcome: { completed: 0, intercepted: 0, missed: 0, teleported: 0 },
    throwOutcome: { infield: { hit: 0, caught: 0, blocked: 0, missed: 0 }, outfield: { hit: 0, caught: 0, blocked: 0, missed: 0 } },
  });
  const zoneOf = (p) => (p && p.zone) || 'infield';
  let lastServe = g.match && g.match.round >= 1 ? g.match.serveTeam : -1, lastWinner = -2;
  if (lastServe >= 0) A.serves.push({ round: g.match.round, team: lastServe });
  if (g.match && g.match.isPlaying) A.roundStart = g.time.now;
  const emit = g.events.emit.bind(g.events);
  let pendingPass = null; // { ball, to } of the last lob / flat pass in flight
  const settlePass = (kind) => { if (pendingPass) { A.passOutcome[kind]++; pendingPass = null; } };
  let pendingThrow = null; // { ball, zone } of the last real throw in flight: first contact decides its outcome
  const settleThrow = (kind) => { if (pendingThrow) { A.throwOutcome[pendingThrow.zone][kind]++; pendingThrow = null; } };
  g.events.emit = (name, p) => {
    A.ev[name] = (A.ev[name] || 0) + 1;
    try {
      if (pendingPass && p && p.ball === pendingPass.ball) {
        if (name === 'ball:pickedUp') settlePass(p.pass && p.player === pendingPass.to ? 'completed' : 'missed');
        else if (name === 'ball:caught') settlePass(p.intercepted ? 'intercepted' : 'missed');
        else if (name === 'ball:bounced' || name === 'ball:blocked' || name === 'ball:hitPlayer' || name === 'ball:awarded') settlePass('missed');
      }
      if (pendingThrow && p && p.ball === pendingThrow.ball) {
        if (name === 'ball:hitPlayer') settleThrow(p.outcome === 'damaged' || p.outcome === 'eliminated' ? 'hit' : 'blocked');
        else if (name === 'ball:caught') settleThrow('caught');
        else if (name === 'ball:blocked') settleThrow('blocked');
        else if (name === 'ball:bounced' || name === 'ball:pickedUp' || name === 'ball:awarded' || name === 'ball:thrown') settleThrow('missed');
      }
      switch (name) {
        case 'ball:thrown':
          settleThrow('missed');
          if (p.isPass) A.passesTotal++; else { A.throwsByZone[zoneOf(p.thrower)]++; pendingThrow = { ball: p.ball, zone: zoneOf(p.thrower) }; }
          if (p.isAbility) A.abilityThrows++;
          break;
        case 'ball:passed': {
          if (!p.from || !p.to) break;
          const key = `${zoneOf(p.from) === 'infield' ? 'in' : 'out'}->${zoneOf(p.to) === 'infield' ? 'in' : 'out'}`;
          A.passes[key]++;
          if (p.lob) A.lobs++;
          if (p.teleported) A.passOutcome.teleported++; else pendingPass = { ball: p.ball, to: p.to };
          break;
        }
        case 'ball:hitPlayer': {
          const z = zoneOf(p.attacker);
          A.hitsByZone[z]++;
          if (p.outcome === 'damaged' || p.outcome === 'eliminated') A.landedByZone[z]++;
          break;
        }
        case 'ball:caught':
          if (p.intercepted) A.intercepts++; else A.catches[p.quality === 'perfect' ? 'perfect' : 'normal']++;
          break;
        case 'player:eliminated':
          A.eliminations++;
          A.eliminationsByCause[p.cause || '?'] = (A.eliminationsByCause[p.cause || '?'] || 0) + 1;
          break;
        case 'player:revived':
          A.revives[p.cause || '?'] = (A.revives[p.cause || '?'] || 0) + 1;
          if (p.player && p.player.isStartingOutfielder && p.cause !== 'roundReset' && g.match.isPlaying) A.starterRevives++;
          break;
        case 'player:zone':
          if (p.player && p.player.isStartingOutfielder && p.zone !== 'outfield') A.starterZoneEvents++;
          break;
        case 'ball:awarded':
          A.awards[p.cause] = (A.awards[p.cause] || 0) + 1;
          break;
        case 'round:serve':
          A.serves.push({ round: p.round, team: p.team });
          if (p.round > 1 && lastServe >= 0 && lastWinner > -2) A.expectedServes.push({ round: p.round, team: lastWinner >= 0 ? 1 - lastWinner : 1 - lastServe });
          lastServe = p.team;
          break;
        case 'round:started':
          A.roundStart = g.time.now;
          break;
        case 'round:ended':
          A.rounds.push({ round: p.round, winner: p.winner, reason: p.reason, duration: A.roundStart >= 0 ? +(g.time.now - A.roundStart).toFixed(1) : null });
          A.roundStart = -1;
          lastWinner = p.winner;
          break;
        case 'match:started':
          lastServe = -1; lastWinner = -2;
          break;
        case 'possession:violation': A.violations++; break;
        case 'possession:warning': A.warnings++; break;
        default: break;
      }
    } catch (e) { A.notes.push(String(e)); }
    return emit(name, p);
  };
  const orig = g.balls.spawnAbilityBall.bind(g.balls);
  g.balls.spawnAbilityBall = (...a) => { A.spawnAbilityBall++; return orig(...a); };

  const legal = (p) => {
    // Ability-widened motor confinement (Gouki's Tackle Intercept past the centre line) is legal.
    const conf = g.court.confinement(p.team, p.zone);
    const mb = p.motor && p.motor._hasBounds ? p.motor._bounds : null;
    if (mb && (Math.abs(mb.minZ - conf.minZ) > 0.05 || Math.abs(mb.maxZ - conf.maxZ) > 0.05 || Math.abs(mb.minX - conf.minX) > 0.05)) return true;
    return Court.distanceTo(conf, p.position) <= 0.25;
  };
  let looseStart = -1, unownedStart = -1, heldBy = null, heldStart = -1, tick = 0;
  const close = (arr, t0, now) => { if (t0 >= 0) arr.push(now - t0); return -1; };
  const sample = () => {
    const m = g.match, b = g.balls;
    if (!m || !b) return;
    const now = g.time.now;
    tick++;
    A.samples++;
    const extraBalls = b.active.filter((x) => !x.recycled && !b.matchBalls.includes(x)).length;
    const n = b.matchBalls.length + extraBalls;
    A.maxBalls = Math.max(A.maxBalls, n);
    if (m.players.length && n !== 1) A.badBallSamples++;
    for (const p of m.players) if (p.hero) A.heroes.add(p.hero.id);
    const playing = m.phase === 'playing' || m.phase === 'preRound' || m.phase === 'countdown';
    if (m.players.length) {
      const per = [0, 0], starters = [0, 0];
      for (const p of m.players) {
        per[p.team]++;
        if (p.isStartingOutfielder) {
          starters[p.team]++;
          if (playing && p.zone !== 'outfield') A.starterZoneViolations++;
        }
      }
      if (per[0] !== 4 || per[1] !== 4) A.badPlayers++;
      A.starterCount = starters;
    }
    // Legal-zone check every 0.5 s (standing, living, not incapacitated players).
    if (m.isPlaying && tick % 5 === 0) {
      for (const p of m.players) {
        if (!p.health || !p.health.isAlive || (p.fsm && p.fsm.incapReason) || p.isRagdolling) continue;
        A.zoneSamples++;
        if (!legal(p)) {
          A.illegalSamples++;
          const k = `${p.hero && p.hero.id}:${p.zone}`;
          A.illegalWho[k] = (A.illegalWho[k] || 0) + 1;
        }
      }
    }
    const ball = b.ball;
    if (ball && m.isPlaying) {
      const awarding = ball.state === 'despawned' && !(ball.custodyTeam >= 0);
      const loose = ball.state === 'free' || awarding;
      if (loose && looseStart < 0) looseStart = now;
      if (!loose) looseStart = close(A.looseStints, looseStart, now);
      const poss = m.possession;
      const unowned = ball.state !== 'live' && ball.state !== 'held' && !(poss && poss.team >= 0);
      if (unowned && unownedStart < 0) unownedStart = now;
      if (!unowned) unownedStart = close(A.unownedStints, unownedStart, now);
      const h = ball.state === 'held' ? ball.holder : null;
      if (h !== heldBy) {
        if (heldBy) close(A.heldStints, heldStart, now);
        heldBy = h; heldStart = h ? now : -1;
      }
    } else {
      looseStart = close(A.looseStints, looseStart, now);
      unownedStart = close(A.unownedStints, unownedStart, now);
      if (heldBy) close(A.heldStints, heldStart, now);
      heldBy = null; heldStart = -1;
    }
  };
  window.__rulesTimer = setInterval(sample, 100);
});

await page.waitForTimeout(seconds * 1000);
const r = await page.evaluate(() => {
  clearInterval(window.__rulesTimer);
  const A = window.__rules;
  const g = window.__DU.game;
  return { ...A, heroes: [...A.heroes], now: g.time.now, stats: window.__DU.stats(), limit: g.match.rules.possessionLimit };
});

const sum = (a) => a.reduce((x, y) => x + y, 0);
const max = (a) => (a.length ? Math.max(...a) : 0);
const mean = (a) => (a.length ? sum(a) / a.length : 0);
const k = Math.max(0.25, r.now / 240); // throughput thresholds are per 240 game-seconds
const throws = r.throwsByZone.infield + r.throwsByZone.outfield;
const servesOk = r.expectedServes.every((e) => r.serves.some((s) => s.round === e.round && s.team === e.team));
const decided = r.rounds.filter((e) => e.reason === 'eliminated' || e.reason === 'time');
const report = {
  seed, extra: extra || null, gameSeconds: Math.round(r.now), fps: Math.round(r.stats.fps), phase: r.stats.phase, round: r.stats.round, scores: r.stats.scores,
  heroes: r.heroes.length, starters: r.starterCount, maxDodgeballs: r.maxBalls, badBallSamples: r.badBallSamples, spawnAbilityBall: r.spawnAbilityBall,
  throws, throwsByZone: r.throwsByZone, abilityThrows: r.abilityThrows, passThrows: r.passesTotal, passes: sum(Object.values(r.passes)), passDirections: r.passes, lobs: r.lobs, passOutcome: r.passOutcome, throwOutcome: r.throwOutcome,
  intercepts: r.intercepts, hitsByZone: r.hitsByZone, landedByZone: r.landedByZone, catches: r.catches,
  eliminations: r.eliminations, eliminationsByCause: r.eliminationsByCause, revives: r.revives,
  starter: { zoneViolations: r.starterZoneViolations, zoneEvents: r.starterZoneEvents, revives: r.starterRevives },
  loose: { stints: r.looseStints.length, max: +max(r.looseStints).toFixed(2), mean: +mean(r.looseStints).toFixed(2) },
  unowned: { stints: r.unownedStints.length, max: +max(r.unownedStints).toFixed(2) },
  held: { stints: r.heldStints.length, max: +max(r.heldStints).toFixed(2), mean: +mean(r.heldStints).toFixed(2) },
  possession: { limit: r.limit, violations: r.violations, warnings: r.warnings },
  awards: r.awards, serves: r.serves, expectedServes: r.expectedServes, rounds: r.rounds,
  illegalZone: { samples: r.illegalSamples, of: r.zoneSamples, who: r.illegalWho },
  matchesEnded: r.ev['match:ended'] || 0, casts: r.ev['ability:cast'] || 0, notes: r.notes.slice(0, 5),
};
console.log(JSON.stringify(report, null, 1));

const checks = [];
const check = (name, ok, detail = '') => checks.push({ name, ok: !!ok, detail });
const pageErrors = [...new Set(errs)];
check('no page / console errors', pageErrors.length === 0, pageErrors.slice(0, 5).join(' | '));
check('no HTTP errors', httpErrs.length === 0, [...new Set(httpErrs)].slice(0, 5).join(', '));
check('never more than one dodgeball (every sample)', r.badBallSamples === 0 && r.maxBalls <= 1, `bad samples ${r.badBallSamples}, max ${r.maxBalls}`);
check('no ability balls conjured', r.spawnAbilityBall === 0, `spawnAbilityBall calls ${r.spawnAbilityBall}`);
check('4 players per team', r.badPlayers === 0, `bad samples ${r.badPlayers}`);
check('1 starting outfielder per team', r.starterCount[0] === 1 && r.starterCount[1] === 1, JSON.stringify(r.starterCount));
check('starting outfielder never leaves / is never revived', r.starterZoneViolations === 0 && r.starterRevives === 0 && r.starterZoneEvents === 0,
  JSON.stringify(report.starter));
check('8 distinct heroes', r.heroes.length >= 8, `${r.heroes.length}`);
check('no illegal positions', r.illegalSamples === 0, JSON.stringify(r.illegalWho));
check('throws >= 30 / 240 s', throws >= Math.floor(30 * k), `${throws}`);
check('passes >= 4 / 240 s with in->out and out->in', sum(Object.values(r.passes)) >= Math.floor(4 * k) && r.passes['in->out'] >= 1 && r.passes['out->in'] >= 1, JSON.stringify(r.passes));
check('outfield throws >= 4 / 240 s and >= 1 outfield hit', r.throwsByZone.outfield >= Math.floor(4 * k) && r.landedByZone.outfield >= 1,
  `throws ${r.throwsByZone.outfield}, landed ${r.landedByZone.outfield}`);
check('catches happen', r.catches.perfect + r.catches.normal >= 1, JSON.stringify(r.catches));
check('max loose stint <= 12 s', max(r.looseStints) <= 12, max(r.looseStints).toFixed(2));
check('mean loose stint <= 4 s', mean(r.looseStints) <= 4, mean(r.looseStints).toFixed(2));
check('max unowned stint <= 6 s', max(r.unownedStints) <= 6, max(r.unownedStints).toFixed(2));
check('max held stint <= possessionLimit + 0.25', !(r.limit > 0) || max(r.heldStints) <= r.limit + 0.25, `${max(r.heldStints).toFixed(2)} vs ${r.limit}`);
check('possession violations <= 1', r.violations <= 1, `${r.violations}`);
check('>= 1 round decided (eliminated / time)', decided.length >= 1, JSON.stringify(r.rounds));
check('round durations 10-150 s', r.rounds.every((x) => x.duration == null || (x.duration >= 10 && x.duration <= 151)), JSON.stringify(r.rounds.map((x) => x.duration)));
check('serve order follows the rule', servesOk, JSON.stringify({ serves: r.serves, expected: r.expectedServes }));
for (const c of checks) console.log(`${c.ok ? 'PASS' : 'FAIL'}  ${c.name}${c.detail ? `  (${c.detail})` : ''}`);
const failed = checks.filter((c) => !c.ok).length;
console.log(failed ? `${failed} check(s) FAILED` : 'all checks passed');
await browser.close();
server.close();
process.exitCode = failed ? 1 : 0;
