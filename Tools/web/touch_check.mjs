// Touch controls check: headless Chromium emulating landscape phones (hasTouch + isMobile) driven by REAL touch events
// (CDP Input.dispatchTouchEvent -> pointer events with capture, several fingers at once).
//   node Tools/web/touch_check.mjs [--viewports 844x390,915x412,1000x460,1280x720,800x360,667x375,812x375,926x428,390x844]
//        [--tag touch] [--no-gestures] [--root dir] (serve another directory; env DU_ROOT works too)
// Every viewport: ?autoplay=1&touch=1 (bots frozen once the round plays), the five buttons + pause must stay on screen
// inside the safe margin and never overlap each other (circles) or the HUD (scoreboard, player card, minimap, a full
// 5-line kill feed with revive lines, shot-clock countdown, last-throw readout, toasts, stick, hints); 閃避 stays out
// of the stick zone, the two hints never collide, the kill feed only ever shows whole, full-height lines. Measured
// twice: without safe-area insets and with notched-phone insets forced onto the --safe-* variables (headless env()
// insets are always 0; landscape 47/47/21 like an iPhone 12-15, portrait 47 top / 34 bottom; landscape viewports
// under 700 px wide - iPhone SE class, no notch - skip it). Portrait: the scoreboard alone is wider than the screen
// (the portrait HUD itself is out of scope), so it is left out of the overlap list there. Screenshots with and
// without the ball: Tools/web/screenshots/<tag>-<w>x<h>-{ball,empty}.png, plus <tag>-<w>x<h>-safe.png (insets
// forced, ball held) on phones.
// Gestures (first viewport, a second page with ?norender=1 so frames - and input dispatch - run at phone speed
// instead of SwiftShader's ~1 fps): primary 接球 -> 撿球 (ball in manual reach, auto pickup off) -> tap picks up -> 投球;
// press locked at pointerdown (a 接球 press that flips to 投球 never charges / throws); hold-to-charge + drag-to-aim
// + release throws; an incoming enemy ball keeps 接球; 傳球 shown only with the ball and passes; the automatic touch pickup; 閃避 moving = slide, still =
// jump, quick swipe up while moving = jump; a short tap on the look side cycles the target, a drag only looks;
// stick / aim hints fade after the first seconds of the playing phase. Review follow-ups: 閃避 while winding up a
// throw on the move = jump throw; a near-miss tap just outside the round primary still presses it; a tap on the
// hidden 傳球's spot never re-targets; a cancelled 投球 touch keeps the ball; a keyboard player with the touch overlay
// up gets no touch pickup assist and sees the [E] prompt; the pressed 大絕 lights up. Prints a JSON report; exit code
// 1 when any check failed.
import { chromium } from 'playwright-core';
import fs from 'fs';
import path from 'path';
import { fileURLToPath } from 'url';
import { startServer } from './serve.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const args = process.argv.slice(2);
const opt = (name, def) => { const i = args.indexOf('--' + name); return i >= 0 ? args[i + 1] : def; };
const flag = (name) => args.includes('--' + name);
const viewports = opt('viewports', '844x390,915x412,1000x460,1280x720,800x360,667x375,812x375,926x428,390x844').split(',').map((v) => v.split('x').map(Number));
const tag = opt('tag', 'touch');
const exe = process.env.PW_CHROMIUM || '/opt/pw-browsers/chromium-1194/chrome-linux/chrome';
const QUERY = 'autoplay=1&hero=Rayne&seed=7&quality=low&touch=1&possession=0&roundTime=600';
const MARGIN = 4; // px every control keeps from the viewport edge
/** Border colour of the primary button per mode (css .tc-m-*): the colour must follow the label. */
const MODE_RGB = { throw: '255, 106, 43', catch: '154, 217, 255', pickup: '61, 220, 132' };

const server = await startServer(0, opt('root') ? { root: opt('root') } : {});
const url = `http://127.0.0.1:${server.address().port}/index.html?${QUERY}`;
const browser = await chromium.launch({
  executablePath: exe,
  args: ['--use-angle=swiftshader', '--enable-unsafe-swiftshader', '--ignore-gpu-blocklist', '--enable-webgl', '--autoplay-policy=no-user-gesture-required'],
});
const shotDir = path.join(here, 'screenshots');
fs.mkdirSync(shotDir, { recursive: true });
const report = { url, checks: [], pageErrors: [], consoleErrors: [], screenshots: [] };
const check = (name, ok, info = undefined) => { report.checks.push({ name, ok: !!ok, ...(info === undefined ? {} : { info }) }); return !!ok; };
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// ------------------------------------------------------------------ in-page helpers (serialised into the page)
const PAGE_HELPERS = (MARGIN_) => {
  const g = window.__DU.game;
  const local = () => g.localPlayer;
  window.__tc = {
    g,
    local,
    freezeBots() { for (const p of g.players) if (!p.isLocal) p.inputLocked = true; },
    /** Loose ball at rest `dist` m in front of the local player (own zone), or far away in the enemy half. */
    placeBall(dist) {
      const p = local(), ball = g.balls.ball;
      const pos = p.position.clone();
      if (dist == null) pos.set(0, 0, -g.court.sideSign(p.team) * (g.court.halfL - 2));
      else { pos.x += Math.sin(p.yaw) * dist; pos.z += Math.cos(p.yaw) * dist; }
      pos.y = 0;
      ball.resetTo(pos);
      ball.clearReservation();
      return { x: pos.x, z: pos.z };
    },
    giveBall() { const p = local(); return p.combat.giveBall(g.balls.ball); },
    /** Local player back near its own baseline, facing the centre line, camera snapped behind. */
    toBaseline() {
      const p = local(), s = g.court.sideSign(p.team);
      const pos = p.position.clone().set(0, 0, s * (g.court.halfL - 1.2));
      const yaw = s < 0 ? 0 : Math.PI;
      p.teleport(pos, yaw);
      g.cameraRig.setTarget(p, true);
    },
    state() {
      const p = local(), c = p.combat, t = g.input.touch, ball = g.balls.ball;
      const prim = t.buttons.primary;
      return {
        mode: prim.dataset.mode, label: prim.textContent.trim(), passShown: !t.buttons.pass.classList.contains('tc-off'),
        hasBall: c.hasBall, charging: c.isCharging, charge: c.charge, fsm: p.fsm.current, grounded: p.motor.isGrounded,
        sliding: p.motor.isSliding, speed: p.motor.planarSpeed, ball: ball.state, isPass: !!ball.isPass,
        thrownByLocal: ball.lastThrower === p, camYaw: g.cameraRig.yaw, hintsOff: t.root.classList.contains('tc-hints-off'),
        manualLock: c._manualLockUntil > g.time.now, target: c.currentTarget ? c.currentTarget.id : null, now: g.time.now,
        stick: +t.moveMagnitude.toFixed(2), canAct: p.canAct,
      };
    },
    primaryColor() { return getComputedStyle(g.input.touch.buttons.primary).borderTopColor; },
    center(sel) { const r = document.querySelector(sel).getBoundingClientRect(); return { x: r.left + r.width / 2, y: r.top + r.height / 2 }; },
    spy() {
      window.__tcCalls = [];
      if (g.input.__spied) return;
      g.input.__spied = true;
      const orig = g.input.setTouchAction.bind(g.input);
      g.input.setTouchAction = (a, on) => { if (on) window.__tcCalls.push(a); orig(a, on); };
    },
    calls() { return window.__tcCalls.slice(); },
    /** Fills the kill feed (5 lines, revive ↺ lines included), shows the shot clock, last throw and a toast. */
    stressHud() {
      const h = g.hud;
      for (let i = 0; i < 5; i++) {
        if (i % 2) h._feed('<span class="kf-a">GOUKI</span><i class="kf-ico"></i><span class="kf-v">HOUDINI</span><small>HEADSHOT · 爆頭</small>', '#fff', i === 0);
        else h._feed('<i class="kf-rev">↺</i><span class="kf-v">HOUDINI</span><small>PERFECT CATCH · 完美接球</small>', '#fff', i === 4);
      }
      h.el.sc.classList.add('is-on');
      h.el.sc.querySelector('b').textContent = '3';
      h.el.lastThrow.classList.add('is-on');
      h.toast('ULTIMATE READY · 終極技就緒', '#ffc940', 30);
    },
    /** Forces safe-area insets onto the --safe-* variables (null: back to env()). */
    insets(v) {
      let st = document.getElementById('tc-safe');
      if (!v) { if (st) st.remove(); return; }
      if (!st) { st = document.createElement('style'); st.id = 'tc-safe'; document.head.appendChild(st); }
      st.textContent = `:root{--safe-t:${v.t}px!important;--safe-r:${v.r}px!important;--safe-b:${v.b}px!important;--safe-l:${v.l}px!important}`;
    },
    layout() {
      const vw = innerWidth, vh = innerHeight;
      const vis = (el) => { if (!el) return false; const cs = getComputedStyle(el); const r = el.getBoundingClientRect(); return cs.display !== 'none' && cs.visibility !== 'hidden' && +cs.opacity > 0.05 && r.width > 0 && r.height > 0; };
      const box = (el, name) => { const r = el.getBoundingClientRect(); return { name, x0: r.left, y0: r.top, x1: r.right, y1: r.bottom }; };
      const buttons = [];
      for (const el of document.querySelectorAll('#touch .tc-btn, #touch .tc-pause')) {
        // Measure hidden 傳球 too: it must have room whenever it appears.
        const r = el.getBoundingClientRect();
        buttons.push({ ...box(el, el.dataset.act || 'pause'), cx: r.left + r.width / 2, cy: r.top + r.height / 2, rad: r.width / 2 });
      }
      const hud = [];
      const add = (sel, name) => { for (const el of document.querySelectorAll(sel)) if (vis(el)) hud.push(box(el, name)); };
      if (vw >= vh) add('.sb', 'scoreboard');
      add('.pc', 'playerCard');
      add('.mm', 'minimap');
      add('.kf', 'killFeed');
      add('.sc', 'shotClock');
      add('.lt', 'lastThrow');
      add('.hud-toast', 'toast');
      add('.pr', 'pickupPrompt');
      add('#touch .tc-stick', 'stick');
      add('#touch .tc-hint', 'hint');
      const inter = (a, b) => Math.max(0, Math.min(a.x1, b.x1) - Math.max(a.x0, b.x0)) * Math.max(0, Math.min(a.y1, b.y1) - Math.max(a.y0, b.y0));
      const problems = [];
      // 閃避 must not sit in the stick zone (a left thumb landing there would dodge instead of moving).
      const mz = document.querySelector('#touch .tc-zone-move').getBoundingClientRect();
      const dodge = buttons.find((b) => b.name === 'dodge');
      if (dodge && dodge.x0 < mz.right) problems.push(`dodge inside the stick zone (${Math.round(dodge.x0)} < ${Math.round(mz.right)})`);
      const hints = [...document.querySelectorAll('#touch .tc-hint')].filter(vis).map((el) => box(el, el.className));
      if (hints.length === 2 && inter(hints[0], hints[1]) > 0) problems.push('the two hints overlap');
      for (const b of buttons) {
        if (b.x0 < MARGIN_ || b.y0 < MARGIN_ || b.x1 > vw - MARGIN_ || b.y1 > vh - MARGIN_) problems.push(`${b.name} off screen ${JSON.stringify([b.x0, b.y0, b.x1, b.y1].map(Math.round))}`);
        for (const h of hud) if (inter(b, h) > 0) problems.push(`${b.name} overlaps ${h.name}`);
      }
      // Kill feed: whole lines only (the clip never cuts through a line).
      const kf = document.querySelector('.kf');
      let kfLines = 0;
      if (kf && vis(kf)) {
        const kb = kf.getBoundingClientRect().bottom;
        for (const it of kf.querySelectorAll('.kf-item')) {
          const r = it.getBoundingClientRect();
          if (r.top < kb - 0.5 && r.bottom > kb + 0.5) problems.push(`kill feed line cut (${Math.round(r.top)}..${Math.round(r.bottom)} vs ${Math.round(kb)})`);
          if (r.height < 18) problems.push(`kill feed line squashed to ${r.height.toFixed(1)} px`);
          if (r.bottom <= kb + 0.5) kfLines++;
        }
      }
      let minGap = Infinity;
      for (let i = 0; i < buttons.length; i++) {
        for (let j = i + 1; j < buttons.length; j++) {
          const a = buttons[i], b = buttons[j];
          const gap = Math.hypot(a.cx - b.cx, a.cy - b.cy) - a.rad - b.rad;
          minGap = Math.min(minGap, gap);
          if (gap < 4) problems.push(`${a.name} too close to ${b.name} (${gap.toFixed(1)} px)`);
        }
      }
      return { problems, minGap: Math.round(minGap * 10) / 10, kfLines, buttons: buttons.map((b) => ({ name: b.name, x: Math.round(b.x0), y: Math.round(b.y0), w: Math.round(b.x1 - b.x0) })), hud: hud.map((h) => h.name) };
    },
  };
};

async function openMatch(w, h, extraQuery = '') {
  const ctx = await browser.newContext({ viewport: { width: w, height: h }, hasTouch: true, isMobile: true, deviceScaleFactor: 1 });
  const page = await ctx.newPage();
  page.on('pageerror', (e) => report.pageErrors.push(`${w}x${h}: ${e && e.stack || e}`));
  page.on('console', (m) => { if (m.type() === 'error') report.consoleErrors.push(`${w}x${h}: ${m.text()}`); });
  await page.goto(url + extraQuery, { waitUntil: 'load', timeout: 60000 });
  await page.waitForFunction(() => window.__DU && window.__DU.ready === true, null, { timeout: 120000 });
  await page.waitForFunction(() => {
    const g = window.__DU.game;
    return g.match && g.match.phase === 'playing' && g.input.touch && g.input.touch.visible && g.localPlayer && g.localPlayer.canAct;
  }, null, { timeout: 60000 });
  await page.evaluate(`(${PAGE_HELPERS.toString()})(${MARGIN})`);
  await page.evaluate(() => window.__tc.freezeBots());
  const cdp = await ctx.newCDPSession(page);
  return { ctx, page, cdp };
}

/**
 * Fingers: id -> {x, y}. touchStart / touchMove list every finger still down (Chrome diffs them against the last
 * event); touchEnd lists only the finger(s) lifting - the others stay down.
 */
function touchPad(cdp) {
  const fingers = new Map();
  const pt = (id, p) => ({ x: p.x, y: p.y, id, radiusX: 4, radiusY: 4, force: 1 });
  const send = (type) => cdp.send('Input.dispatchTouchEvent', { type, touchPoints: [...fingers].map(([id, p]) => pt(id, p)) });
  return {
    async down(id, x, y) { fingers.set(id, { x, y }); await send('touchStart'); },
    async move(id, x, y) { fingers.set(id, { x, y }); await send('touchMove'); },
    async up(id) {
      const p = fingers.get(id);
      if (!p) return;
      fingers.delete(id);
      await cdp.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [pt(id, p)] });
    },
    async tap(id, x, y, holdMs = 60) { await this.down(id, x, y); await sleep(holdMs); await this.up(id); },
  };
}

const st = (page) => page.evaluate(() => window.__tc.state());
async function waitState(page, pred, timeout = 1500) {
  const t0 = Date.now();
  let s = await st(page);
  while (!pred(s) && Date.now() - t0 < timeout) { await sleep(30); s = await st(page); }
  return s;
}

/** Waits for the primary button's colour transition to land on `mode`'s colour (screenshots at ~1 fps). */
async function waitColor(page, mode, timeout = 4000) {
  const t0 = Date.now();
  let c = await page.evaluate(() => window.__tc.primaryColor());
  while (!c.includes(MODE_RGB[mode]) && Date.now() - t0 < timeout) { await sleep(100); c = await page.evaluate(() => window.__tc.primaryColor()); }
  return c.includes(MODE_RGB[mode]);
}

async function gestures(page, cdp, w, h) {
  const pad = touchPad(cdp);
  const P = await page.evaluate(() => window.__tc.center('#touch .tc-primary'));
  const D = await page.evaluate(() => window.__tc.center('#touch .tc-dodge'));
  const S = await page.evaluate(() => window.__tc.center('#touch .tc-pass'));
  const STICK = { x: Math.round(w * 0.14), y: Math.round(h * 0.7) };
  const LOOK = { x: Math.round(w * 0.64), y: Math.round(h * 0.4) };
  const lookEl = await page.evaluate(({ x, y }) => { const e = document.elementFromPoint(x, y); return e && e.className; }, LOOK);
  check('look tap point hits the look zone', String(lookEl).includes('tc-zone-look'), lookEl);
  const hint0 = await page.evaluate(() => ({ off: window.__tc.g.input.touch.root.classList.contains('tc-hints-off'), t: window.__tc.g.input.touch._hintT }));
  check('hints still shown when the round starts playing (pre-round / countdown use no hint time)', !hint0.off && hint0.t < 3, hint0);

  // --- primary: catch -> pickup (manual reach, auto pickup off) -> tap -> throw
  await page.evaluate(() => { window.__tc.g.input.touchAutoPickup = false; window.__tc.placeBall(null); });
  let s = await waitState(page, (x) => x.mode === 'catch');
  check('primary shows 接球 without a ball nearby', s.mode === 'catch' && s.label === '接球' && !s.passShown, s);
  await page.evaluate(() => window.__tc.placeBall(1.25));
  s = await waitState(page, (x) => x.mode === 'pickup');
  check('primary shows 撿球 with a loose ball in manual reach', s.mode === 'pickup' && s.label === '撿球' && !s.hasBall && s.ball === 'free', s);
  // An enemy ball on its way (a stand-in live ball, present for one synchronous lateUpdate only) keeps 接球 and
  // holds the automatic touch pickup, so the hands stay free for the catch.
  const threat = await page.evaluate(async () => {
    const tc = window.__tc, g = tc.g, p = tc.local();
    const mod = await import('./js/input/input.js');
    const enemy = g.players.find((q) => g.areEnemies(q, p));
    const fake = { state: 'live', isPass: false, lastThrower: enemy, position: { x: p.position.x + 1, y: 1.2, z: p.position.z + 8 }, velocity: { x: -1, y: 0, z: -10 } };
    const list = g.balls.active;
    list.push(fake);
    const out = {};
    try {
      g.input.lateUpdate();
      out.mode = g.input.touch.primaryMode; out.incoming = mod.enemyBallIncoming(p); out.assist = mod.touchPickupReady(p);
      fake.velocity.z = 10; // flying away: no threat
      out.awayIncoming = mod.enemyBallIncoming(p);
    } finally { list.splice(list.indexOf(fake), 1); }
    g.input.lateUpdate();
    out.modeAfter = g.input.touch.primaryMode; out.assistAfter = mod.touchPickupReady(p);
    return out;
  });
  check('an incoming enemy ball keeps 接球 and holds the touch auto pickup', threat.mode === 'catch' && threat.incoming && !threat.assist && !threat.awayIncoming && threat.modeAfter === 'pickup' && threat.assistAfter, threat);
  await pad.tap(1, P.x, P.y);
  s = await waitState(page, (x) => x.hasBall && x.mode === 'throw');
  check('tap 撿球 picks the ball up, primary shows 投球, 傳球 appears', s.hasBall && s.mode === 'throw' && s.label === '投球' && s.passShown, s);

  // --- the action is locked at pointerdown: a 接球 press that flips to 投球 never charges or throws
  await page.evaluate(() => window.__tc.placeBall(null));
  s = await waitState(page, (x) => x.mode === 'catch' && !x.hasBall);
  await pad.down(1, P.x, P.y);
  await sleep(80);
  await page.evaluate(() => window.__tc.giveBall());
  s = await waitState(page, (x) => x.mode === 'throw');
  await sleep(400);
  const midPress = await st(page);
  await pad.up(1);
  await sleep(250);
  s = await st(page);
  check('label flip mid-press keeps the pressed action (no charge, no throw)', midPress.mode === 'throw' && !midPress.charging && s.hasBall && s.ball === 'held', { midPress, after: s });

  // --- hold to charge, drag to fine-aim, release throws
  const yaw0 = (await st(page)).camYaw;
  await pad.down(1, P.x, P.y);
  s = await waitState(page, (x) => x.charging, 800);
  const charging = s.charging;
  for (let i = 1; i <= 6; i++) { await pad.move(1, P.x - i * 8, P.y - i * 2); await sleep(30); }
  await sleep(450);
  const held = await st(page);
  await pad.up(1);
  s = await waitState(page, (x) => !x.hasBall && x.ball === 'live', 800);
  check('hold 投球 charges, drag aims, release throws', charging && held.charge > 0.3 && Math.abs(held.camYaw - yaw0) > 0.01 && !s.hasBall && s.ball === 'live' && s.thrownByLocal && !s.isPass,
    { charge: +held.charge.toFixed(2), yawDelta: +(held.camYaw - yaw0).toFixed(3), after: { ball: s.ball, hasBall: s.hasBall } });
  s = await waitState(page, (x) => x.mode !== 'throw');
  check('after the throw the primary leaves 投球 and 傳球 hides', s.mode !== 'throw' && !s.passShown, s);

  // --- a cancelled 投球 touch (system edge swipe) drops the wind-up and keeps the ball
  await page.evaluate(() => { window.__tc.placeBall(null); window.__tc.giveBall(); });
  await waitState(page, (x) => x.mode === 'throw' && x.hasBall);
  await cdp.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [{ x: P.x, y: P.y, id: 9 }] });
  const cancelPre = await waitState(page, (x) => x.charging, 800);
  await sleep(250);
  await cdp.send('Input.dispatchTouchEvent', { type: 'touchCancel', touchPoints: [] });
  await sleep(300);
  s = await st(page);
  check('a cancelled 投球 touch keeps the ball (no throw)', cancelPre.charging && s.hasBall && s.ball === 'held' && !s.charging, { cancelPre: cancelPre.charging, after: s });

  // --- 傳球: only with the ball, tap passes
  await page.evaluate(() => { window.__tc.placeBall(null); window.__tc.giveBall(); });
  s = await waitState(page, (x) => x.passShown);
  const passBtn = s.passShown;
  await pad.tap(1, S.x, S.y);
  s = await waitState(page, (x) => !x.hasBall, 800);
  check('傳球 visible with the ball and tapping it passes', passBtn && !s.hasBall && s.isPass, { passBtn, after: s });

  // --- automatic touch pickup within manual reach (walking onto the ball)
  await page.evaluate(() => { window.__tc.g.input.touchAutoPickup = true; window.__tc.placeBall(1.3); });
  s = await waitState(page, (x) => x.hasBall, 1500);
  check('touch auto pickup grabs a ball 1.3 m away (beyond the 0.9 m auto radius)', s.hasBall && s.mode === 'throw', s);
  await page.evaluate(() => { window.__tc.placeBall(null); });
  await waitState(page, (x) => !x.hasBall && x.mode === 'catch');

  // --- 閃避: standing still = jump
  await page.evaluate(() => window.__tc.toBaseline());
  s = await waitState(page, (x) => x.grounded && x.speed < 0.3 && x.fsm === 'grounded', 2000);
  await pad.tap(2, D.x, D.y);
  s = await waitState(page, (x) => x.fsm === 'airborne' || !x.grounded, 600);
  check('閃避 tap while standing still jumps', s.fsm === 'airborne' || !s.grounded, s);
  await waitState(page, (x) => x.grounded && x.fsm === 'grounded', 2500);

  // --- 閃避: moving = slide (stick finger + dodge finger)
  await page.evaluate(() => window.__tc.toBaseline());
  await waitState(page, (x) => x.grounded && x.fsm === 'grounded', 1500);
  await pad.down(1, STICK.x, STICK.y);
  for (let i = 1; i <= 4; i++) { await pad.move(1, STICK.x, STICK.y - i * 16); await sleep(20); }
  const run = await waitState(page, (x) => x.speed > 4, 1500);
  await pad.down(2, D.x, D.y);
  await sleep(50);
  await pad.up(2);
  s = await waitState(page, (x) => x.sliding || x.fsm === 'sliding', 600);
  check('閃避 tap while moving slides', s.sliding || s.fsm === 'sliding', { runSpeed: +run.speed.toFixed(2), after: s });
  await pad.up(1);
  await waitState(page, (x) => x.grounded && x.fsm === 'grounded' && x.speed < 0.3, 2500);

  // --- 閃避: quick upward swipe while moving = jump
  await page.evaluate(() => window.__tc.toBaseline());
  await waitState(page, (x) => x.grounded && x.fsm === 'grounded', 1500);
  await pad.down(1, STICK.x, STICK.y);
  for (let i = 1; i <= 4; i++) { await pad.move(1, STICK.x, STICK.y - i * 16); await sleep(20); }
  await waitState(page, (x) => x.speed > 4, 1500);
  await pad.down(2, D.x, D.y);
  await pad.move(2, D.x, D.y - 14);
  await pad.move(2, D.x + 2, D.y - 32);
  await pad.up(2);
  s = await waitState(page, (x) => x.fsm === 'airborne' || !x.grounded || x.sliding, 600);
  check('閃避 quick upward swipe while moving jumps (not a slide)', (s.fsm === 'airborne' || !s.grounded) && !s.sliding, s);
  await pad.up(1);
  await waitState(page, (x) => x.grounded && x.fsm === 'grounded', 2500);

  // --- 閃避 while winding up a throw on the move = jump (throw), not a dead slide request
  await page.evaluate(() => { window.__tc.g.input.touchAutoPickup = false; window.__tc.toBaseline(); window.__tc.placeBall(null); window.__tc.giveBall(); });
  await waitState(page, (x) => x.mode === 'throw' && x.grounded && x.fsm === 'grounded', 2000);
  await pad.down(1, STICK.x, STICK.y);
  for (let i = 1; i <= 4; i++) { await pad.move(1, STICK.x + i * 16, STICK.y); await sleep(20); }
  await waitState(page, (x) => x.speed > 2, 1500);
  await pad.down(3, P.x, P.y);
  const windUp = await waitState(page, (x) => x.charging && x.fsm === 'chargingThrow', 800);
  await page.evaluate(() => window.__tc.spy());
  await pad.down(2, D.x, D.y); await sleep(70); await pad.up(2);
  let airborne = false;
  for (let i = 0; i < 20 && !airborne; i++) { const q = await st(page); if (!q.grounded || q.fsm === 'airborne') airborne = true; await sleep(30); }
  const dodgeCalls = await page.evaluate(() => window.__tc.calls());
  await pad.up(3); await sleep(200); await pad.up(1);
  check('閃避 while winding up a throw on the move jumps (jump throw)', windUp.charging && dodgeCalls.includes('jump') && !dodgeCalls.includes('slide') && airborne,
    { windUp: { fsm: windUp.fsm, stick: windUp.stick }, calls: dodgeCalls, airborne });
  await waitState(page, (x) => x.grounded && x.fsm === 'grounded' && x.speed < 0.3, 2500);
  await page.evaluate(() => { window.__tc.placeBall(null); });
  await waitState(page, (x) => !x.hasBall && x.mode === 'catch');

  // --- near-miss on the round primary (bounding-box corner, outside the circle) still presses it; a tap on the
  // hidden 傳球's spot never re-targets
  const geo = await page.evaluate(() => { const r = window.__tc.g.input.touch.buttons.primary.getBoundingClientRect(); return { cx: r.left + r.width / 2, cy: r.top + r.height / 2, r: r.width / 2 }; });
  const corner = { x: Math.round(geo.cx - (geo.r + 5) * Math.SQRT1_2), y: Math.round(geo.cy - (geo.r + 5) * Math.SQRT1_2) };
  const cornerEl = await page.evaluate(({ x, y }) => { const e = document.elementFromPoint(x, y); return e && e.className; }, corner);
  await page.evaluate(() => window.__tc.spy());
  await pad.tap(4, corner.x, corner.y, 70);
  await sleep(150);
  let calls0 = await page.evaluate(() => window.__tc.calls());
  check('a near-miss tap just outside the round 接球 presses it (not a target cycle)', String(cornerEl).includes('tc-zone-look') && calls0.includes('catch') && !calls0.includes('cycleTarget'), { corner, cornerEl, calls: calls0 });
  await waitState(page, (x) => x.fsm === 'grounded', 1500);
  await sleep(400); // catch stance / whiff recovery
  const passEl = await page.evaluate(({ x, y }) => { const e = document.elementFromPoint(x, y); return e && e.className; }, { x: Math.round(S.x), y: Math.round(S.y) });
  const tgt0 = (await st(page)).target;
  await page.evaluate(() => window.__tc.spy());
  await pad.tap(4, Math.round(S.x), Math.round(S.y), 70);
  await sleep(150);
  calls0 = await page.evaluate(() => window.__tc.calls());
  s = await st(page);
  check('a tap on the hidden 傳球 spot does nothing (no target cycle)', !s.passShown && calls0.length === 0 && s.target === tgt0, { passEl, calls: calls0, targetBefore: tgt0, targetAfter: s.target });

  // --- look zone: short tap = cycle target, drag = look only
  await page.evaluate(() => window.__tc.spy());
  const before = await st(page);
  await pad.tap(3, LOOK.x, LOOK.y, 70);
  await sleep(150);
  let calls = await page.evaluate(() => window.__tc.calls());
  s = await st(page);
  check('short tap on the look side cycles the target', calls.includes('cycleTarget') && s.manualLock && s.target !== null && s.target !== before.target,
    { calls, targetBefore: before.target, targetAfter: s.target, manualLock: s.manualLock });
  await page.evaluate(() => window.__tc.spy());
  const y0 = (await st(page)).camYaw;
  await pad.down(3, LOOK.x, LOOK.y);
  for (let i = 1; i <= 8; i++) { await pad.move(3, LOOK.x + i * 12, LOOK.y); await sleep(35); }
  await pad.up(3);
  await sleep(150);
  calls = await page.evaluate(() => window.__tc.calls());
  s = await st(page);
  check('dragging on the look side looks without cycling the target', !calls.includes('cycleTarget') && Math.abs(s.camYaw - y0) > 0.05, { calls, yawDelta: +(s.camYaw - y0).toFixed(3) });

  // --- a keyboard player with the touch overlay on screen: no touch pickup assist, the [E] prompt is back
  await page.evaluate(() => { window.__tc.g.input.touchAutoPickup = true; window.__tc.toBaseline(); window.__tc.placeBall(null); });
  await waitState(page, (x) => x.mode === 'catch' && x.fsm === 'grounded', 2000);
  await page.keyboard.down('KeyZ'); await page.keyboard.up('KeyZ');
  await page.evaluate(() => window.__tc.placeBall(1.3));
  await sleep(600);
  const kbm = await page.evaluate(() => {
    const g = window.__tc.g, pr = document.querySelector('.pr');
    return { lastDevice: g.input.lastDevice, hasBall: g.localPlayer.combat.hasBall, prompt: !!pr && pr.classList.contains('is-on'), promptText: pr && pr.textContent };
  });
  check('keyboard player (touch overlay up): no touch auto pickup, [E] prompt shown', kbm.lastDevice === 'kbm' && !kbm.hasBall && kbm.prompt, kbm);
  // A finger again: the assist is back.
  await pad.tap(3, LOOK.x, LOOK.y + 40, 300);
  s = await waitState(page, (x) => x.hasBall, 1500);
  check('back on touch: the touch auto pickup works again', s.hasBall, s);
  await page.evaluate(() => { window.__tc.placeBall(null); });
  await waitState(page, (x) => !x.hasBall);

  // --- the pressed 大絕 lights up like the other buttons
  const ultFlash = await page.evaluate(() => {
    const u = window.__tc.g.input.touch.buttons.ultimate;
    const a = getComputedStyle(u).backgroundImage;
    u.classList.add('tc-down');
    const b = getComputedStyle(u).backgroundImage;
    u.classList.remove('tc-down');
    return { up: a, down: b };
  });
  check('pressed 大絕 changes its background (press flash)', ultFlash.up !== ultFlash.down && ultFlash.down.includes('255, 255, 255'), ultFlash);

  // --- hints fade after the first seconds of play
  s = await waitState(page, (x) => x.hintsOff, 8000);
  check('stick / aim hints fade out after ~6 s', s.hintsOff);
}

if (!flag('no-gestures')) {
  const [w, h] = viewports[0];
  let session = null;
  try {
    session = await openMatch(w, h, '&norender=1');
    await gestures(session.page, session.cdp, w, h);
  } catch (e) {
    check(`gestures ${w}x${h} ran`, false, String(e && e.stack || e));
  }
  if (session) await session.ctx.close();
}
for (let i = 0; i < viewports.length; i++) {
  const [w, h] = viewports[i];
  let session = null;
  try {
    session = await openMatch(w, h);
    const { page } = session;
    // Layout with the ball (投球 + 傳球 shown) and the HUD at its largest.
    await page.evaluate(() => { window.__tc.placeBall(null); window.__tc.giveBall(); window.__tc.stressHud(); });
    await waitState(page, (x) => x.mode === 'throw' && x.passShown);
    const throwColor = await waitColor(page, 'throw');
    await sleep(300); // 傳球 fade-in
    const lay = await page.evaluate(() => window.__tc.layout());
    check(`layout ${w}x${h}: controls on screen, no overlaps (min gap ${lay.minGap} px, ${lay.kfLines} feed lines)`, lay.problems.length === 0, lay.problems.length ? lay : { minGap: lay.minGap, kfLines: lay.kfLines, hud: lay.hud });
    let file = path.join(shotDir, `${tag}-${w}x${h}-ball.png`);
    await page.screenshot({ path: file });
    report.screenshots.push(path.relative(path.join(here, '..', '..'), file));
    // Notched phone: safe-area insets forced onto the --safe-* variables (portrait: top / bottom, landscape: sides).
    const safe = h > w ? { t: 47, r: 0, b: 34, l: 0 } : w >= 700 ? { t: 0, r: 47, b: 21, l: 47 } : null;
    if (safe) {
      await page.evaluate((v) => { window.__tc.insets(v); window.__tc.stressHud(); }, safe);
      await sleep(350);
      const layS = await page.evaluate(() => window.__tc.layout());
      check(`layout ${w}x${h} + safe-area insets ${JSON.stringify(safe)}: no overlaps (min gap ${layS.minGap} px, ${layS.kfLines} feed lines)`, layS.problems.length === 0, layS.problems.length ? layS : { minGap: layS.minGap, kfLines: layS.kfLines });
      if (Math.min(w, h) <= 430) {
        // Let the fresh kill-feed lines finish sliding in (SwiftShader renders ~1 fps) before the picture.
        await page.waitForFunction(() => [...document.querySelectorAll('.kf-item')].every((e) => +getComputedStyle(e).opacity > 0.95), null, { timeout: 5000 }).catch(() => {});
        await sleep(600);
        file = path.join(shotDir, `${tag}-${w}x${h}-safe.png`);
        await page.screenshot({ path: file });
        report.screenshots.push(path.relative(path.join(here, '..', '..'), file));
      }
      await page.evaluate(() => window.__tc.insets(null));
    }
    await page.evaluate(() => window.__tc.placeBall(null));
    await waitState(page, (x) => x.mode === 'catch' && !x.passShown);
    const catchColor = await waitColor(page, 'catch');
    check(`primary colour follows the mode at ${w}x${h} (投球 orange, 接球 blue)`, throwColor && catchColor);
    file = path.join(shotDir, `${tag}-${w}x${h}-empty.png`);
    await page.screenshot({ path: file });
    report.screenshots.push(path.relative(path.join(here, '..', '..'), file));
  } catch (e) {
    check(`viewport ${w}x${h} ran`, false, String(e && e.stack || e));
  }
  if (session) await session.ctx.close();
}
await browser.close();
server.close();
const failed = report.checks.filter((c) => !c.ok).length;
report.summary = `${report.checks.length - failed}/${report.checks.length} checks passed`;
console.log(JSON.stringify(report, null, 2));
process.exit(failed || report.pageErrors.length || report.consoleErrors.length ? 1 : 0);
