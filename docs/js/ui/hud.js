// ---------------------------------------------------------------------------------------------------------------
// HUD (system, ORDER.HUD, lateUpdate after the camera): broadcast-style DOM overlay in #ui.
//   top      : scoreboard (round wins, player pips incl. the starting outfielder, clock, round, possession shot
//              clock + ball-possession dot), kill feed, mini-map, LIVE tag (spectating)
//   centre   : crosshair + hit marker, charge bar (Rayne's overcharge segment), catch timing feedback, lock marker
//              projected over combat.currentTarget, pass-receiver marker, off-screen ball pointer, local 3-2-1
//              possession countdown, round countdown numbers, banners (serve, time violation), toasts, Danger Sense
//   bottom   : player card (portrait, HP with 'PENDING' delayed elimination, ultimate meter, status chips, zone),
//              skill / ultimate radial cooldowns with key hints, last throw km/h + rally, pickup prompt,
//              spectator overlay, click-to-play hint, match results panel
// Event driven (EV.*) + polled state, UNSCALED time (realDt). DOM writes only happen when a value changes
// (per-element caches), no per-frame allocations in the steady state.
// ---------------------------------------------------------------------------------------------------------------
import * as THREE from 'three';
import { game } from '../game.js';
import { EV } from '../core/events.js';
import { TEAM, TEAM_COLORS, ZONE } from '../core/constants.js';
import {
  formatClock, formatCooldown, cssColor, chargeModel, STATUS_LABELS, STATUS_TYPES, failLabel, causeLabel, TEAM_LABELS, clamp01,
} from './format.js';
import { Settings } from '../input/settings.js';
import { Minimap, hiddenOnMinimap } from './minimap.js';
import { portraitUrl } from './heroSelect.js';
import { restartMatch, backToHeroSelect } from './flow.js';

/** HUD tuning (seconds are real time). */
export const HUD_TUNING = Object.freeze({
  killFeedMax: 5,
  killFeedLife: 6.5,
  bannerTime: 1.7,
  bannerMinWhenQueued: 0.65,
  bannerQueueMax: 4,
  catchTime: 1.1,
  hitMarkerTime: 0.22,
  damageFlashTime: 0.3,
  dangerHold: 0.5,          // s an active DangerSense event keeps the edges lit without a refresh
  dangerRise: 18,           // 1/s
  dangerFall: 4,            // 1/s
  dangerTimeScale: 1.2,     // s of time-to-impact mapped to full intensity
  minimapInterval: 1 / 20,
  scanInterval: 1 / 8,
  statusInterval: 1 / 10,
  hpGhostDelay: 0.4,
  hpGhostRate: 0.8,         // fraction / s
  lastThrowShow: 5,
  resultsDelay: 2.4,
  toastTime: 2.2,
  countdownTime: 0.95,
  lowHp: 0.35,
  lowTime: 10,              // s left on the clock that turns it red
  failToastCooldown: 0.6,
  shotClockWarn: 3,         // s left on the possession clock that turns the bar red / shows the local 3-2-1
  ballPointerMargin: 30,    // px from the screen edge for the off-screen ball arrow
  passMarkerLift: 2.05,     // m above the receiver's feet for the pass marker
});

const GOLD = '#ffc940';
const RED = '#ff4a3d';
const GREEN = '#3ddc84';
const WHITE = '#ffffff';
const HOME_CSS = cssColor(TEAM_COLORS[TEAM.HOME]);
const AWAY_CSS = cssColor(TEAM_COLORS[TEAM.AWAY]);
const teamCss = (t) => (t === TEAM.AWAY ? AWAY_CSS : t === TEAM.HOME ? HOME_CSS : '#c8ced6');

const ICONS = {
  skill: '<svg viewBox="0 0 24 24" aria-hidden="true"><path fill="currentColor" d="M13.2 2 4.5 13.6h6.1L9.8 22l8.7-11.6h-6.1z"/></svg>',
  ultimate: '<svg viewBox="0 0 24 24" aria-hidden="true"><path fill="currentColor" d="m12 1.8 2.6 6.2 6.7.6-5.1 4.4 1.6 6.6L12 16.1l-5.8 3.5 1.6-6.6-5.1-4.4 6.7-.6z"/></svg>',
  passive: '<svg viewBox="0 0 24 24" aria-hidden="true"><path fill="currentColor" d="M12 2.2 19.8 5.6v5.9c0 4.9-3.3 8.5-7.8 10.3-4.5-1.8-7.8-5.4-7.8-10.3V5.6z"/></svg>',
};

const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const nameOf = (p) => (p && p.hero && p.hero.id ? String(p.hero.id).toUpperCase() : (p && p.name) || '—');

// ------------------------------------------------------------------ change-only DOM writes
const cacheOf = (el) => el._duc || (el._duc = Object.create(null));
function setText(el, v) { if (!el) return; const c = cacheOf(el); if (c.text !== v) { c.text = v; el.textContent = v; } }
function setClass(el, cls, on) { if (!el) return; const c = cacheOf(el); on = !!on; if (c[cls] !== on) { c[cls] = on; el.classList.toggle(cls, on); } }
function setShown(el, on) { setClass(el, 'is-on', on); }
/** Numeric CSS custom property, quantised to 1/q so tiny changes do not touch the DOM. */
function setVarNum(el, name, v, q = 400) {
  if (!el) return;
  const r = Math.round((Number.isFinite(v) ? v : 0) * q) / q;
  const c = cacheOf(el);
  if (c[name] !== r) { c[name] = r; el.style.setProperty(name, String(r)); }
}
function setVarStr(el, name, v) { if (!el) return; const c = cacheOf(el); if (c[name] !== v) { c[name] = v; el.style.setProperty(name, v); } }
/** Restarts a CSS animation class. */
function replay(el, cls) { if (!el) return; el.classList.remove(cls); void el.offsetWidth; el.classList.add(cls); }

// Pickup prompt filter: a loose ball the local player may legally take (zone / reservation rules), no closure per scan.
let _pickupFor = null;
const PICKUP_BALL = (b) => !!b && (b.state === 'free' || b.state === 'stasis')
  && (!_pickupFor || typeof b.canBePickedUpBy !== 'function' || b.canBePickedUpBy(_pickupFor));
const _proj = new THREE.Vector3();
const _cam = new THREE.Vector3();
const _zoneOut = { team: TEAM.NONE, zone: ZONE.INFIELD };

export class Hud {
  constructor() {
    this.root = null;
    this._enabled = true;
    this._visible = false;
    this._unsubs = [];
    this.el = {};
    this._banners = [];
    this._bannerActive = false;
    this._bannerT = 0;
    this._bannerAge = 0;
    this._toastT = 0;
    this._catchT = 0;
    this._hitT = 0;
    this._dmgT = 0;
    this._countT = 0;
    this._lastCount = -1;
    this._throwT = 0;
    this._failT = 0;
    this._danger = { active: false, level: 0, shown: 0, t: -1e9, ball: null };
    this._kills = [];
    this._mmT = 0; this._scanT = 0; this._statusT = 0;
    this._cardPlayer = null; this._cardHero = null;
    this._ghost = 1; this._ghostHold = 0; this._lastHpFrac = 1; this._hpInt = -1; this._hpMax = -1; this._ultPct = -1;
    this._overTime = 0; this._overBonus = 0;
    this._chargeKey = -1;
    this._charge = { base: 0, over: 0, bonus: 0 };
    this._cdKey = { skill: -1, ultimate: -1 };
    this._hintDevice = '';
    this._lockX = -1e4; this._lockY = -1e4; this._lockTarget = null; this._lockDist = -1;
    this._results = { open: false, t: -1, focus: 0 };
    this._pipState = [[], []];
    this._clockKey = -1; this._round = -1; this._scoreKey = -1; this._winPips = -1;
    this._specTarget = undefined;
    this._vw = typeof innerWidth === 'number' ? innerWidth : 1280;
    this._vh = typeof innerHeight === 'number' ? innerHeight : 720;
    this._onResize = () => { this._vw = innerWidth; this._vh = innerHeight; };
    this.minimap = null;
    this._shotKey = -2; this._shotTeam = -2; this._scKey = -1; this._hasBallTeam = -2;
    this._ptTarget = null; this._ptX = -1e4; this._ptY = -1e4;
    this._bpX = -1e4; this._bpY = -1e4; this._bpA = 1e4;
  }

  async init() {
    this._build();
    window.addEventListener('resize', this._onResize);
    const on = (name, fn) => this._unsubs.push(game.events.on(name, fn));
    on(EV.MatchStarted, () => this._onMatchStarted());
    on(EV.MatchPhase, (e) => this._onPhase(e));
    on(EV.RoundCountdown, (e) => this._onCountdown(e));
    on(EV.RoundStarted, () => { this._hideCountdown(); this.banner('FIGHT!\n開戰！', WHITE, 1.1); });
    on(EV.ServeReady, (e) => this._onServeReady(e));
    on(EV.PossessionWarning, (e) => this._onPossessionWarning(e));
    on(EV.PossessionViolation, (e) => this._onPossessionViolation(e));
    on(EV.BallAwarded, (e) => this._onBallAwarded(e));
    on(EV.RoundEnded, (e) => this._onRoundEnded(e));
    on(EV.MatchEnded, (e) => this._onMatchEnded(e));
    on(EV.PlayerEliminated, (e) => this._onEliminated(e));
    on(EV.PlayerRevived, (e) => this._onRevived(e));
    on(EV.PlayerDamaged, (e) => { if (e && e.player && e.player === game.localPlayer) { this._dmgT = HUD_TUNING.damageFlashTime; replay(this.el.card, 'hurt'); } });
    on(EV.PlayerZone, (e) => this._onZone(e));
    on(EV.BallThrown, (e) => this._onThrown(e));
    on(EV.BallCaught, (e) => this._onCaught(e));
    on(EV.CatchWhiff, (e) => { if (e && e.player === game.localPlayer) this._catchFeedback('miss', 'MISS', '落空 · 太早或太晚'); });
    on(EV.BallHitPlayer, (e) => this._onHit(e));
    on(EV.DangerSense, (e) => this._onDanger(e));
    on(EV.AbilityCast, (e) => this._onAbilityCast(e));
    on(EV.AbilityFailed, (e) => this._onAbilityFailed(e));
    on(EV.UltCharge, (e) => {
      if (e && e.becameReady && e.player === game.localPlayer) {
        const key = game.input ? game.input.hint('ultimate') : 'R';
        this.toast(`ULTIMATE READY · 終極技就緒${key ? `  [${key}]` : ''}`, GOLD, 2.2);
      }
    });
  }

  dispose() {
    for (const u of this._unsubs) u();
    this._unsubs = [];
    window.removeEventListener('resize', this._onResize);
    if (this.minimap) this.minimap.dispose();
    this._closeResults();
    if (this.root) this.root.remove();
    this.root = null;
  }

  // ------------------------------------------------------------------ public API
  /** Shows / hides the whole HUD (hero select hides it). */
  show(b) {
    this._enabled = !!b;
    if (!b) this._closeResults();
  }

  /**
   * Big centre banner. `text` may contain '\n': first line = headline, second = subtitle (e.g. 中文).
   * Queued; a queued banner shortens the current one. color: 0xRRGGBB or CSS colour.
   */
  banner(text, color = WHITE, duration = HUD_TUNING.bannerTime) {
    this._banners.push({ text: String(text ?? ''), color: cssColor(color, WHITE), duration: Math.max(0.3, Number(duration) || HUD_TUNING.bannerTime) });
    if (this._banners.length > HUD_TUNING.bannerQueueMax) this._banners.splice(0, this._banners.length - HUD_TUNING.bannerQueueMax);
    if (!this._bannerActive) this._nextBanner();
  }

  /** Small notification under the scoreboard. */
  toast(text, color = WHITE, duration = HUD_TUNING.toastTime) {
    if (!this.root) return;
    const el = this.el.toast;
    el.textContent = String(text ?? '');
    el.style.setProperty('--tc', cssColor(color, WHITE));
    this._toastT = Math.max(0.3, Number(duration) || HUD_TUNING.toastTime);
    setShown(el, true);
    replay(el, 'pop');
  }

  // ------------------------------------------------------------------ frame
  lateUpdate(dt, realDt) {
    if (!this.root) return;
    const match = game.match;
    const phase = match && match.phase;
    const inMatch = !!phase && phase !== 'idle' && phase !== 'select';
    const visible = this._enabled && inMatch;
    if (visible !== this._visible) {
      this._visible = visible;
      setClass(this.root, 'hud-visible', visible);
      if (!visible) this._resetTransient();
    }
    this._tickTimers(realDt);
    if (!visible) return;

    const local = game.localPlayer;
    setClass(this.root, 'spectating', !local);
    this._updateScoreboard(match, local);
    this._updateCard(local, realDt);
    this._updateAbilities(local);
    this._updateCenter(local);
    this._updateLock(local);
    this._updatePassMarker(local);
    this._updateBallPointer(local);
    this._updateShotClock(match, local);
    this._updateDanger(realDt);
    this._updateSpectate(local);
    this._updateHints(local, phase);
    this._updatePeriodic(local, realDt);
    this._updateKillFeed(realDt);
    this._updateResults(realDt);
  }

  // ------------------------------------------------------------------ DOM construction
  _build() {
    if (this.root) return;
    const ui = document.getElementById('ui') || document.body;
    const root = document.createElement('div');
    root.id = 'hud';
    root.className = 'hud';
    const chips = STATUS_TYPES.map((t) => `<span class="chip tone-${STATUS_LABELS[t].tone}" data-status="${t}">${STATUS_LABELS[t].en}<small>${STATUS_LABELS[t].zh}</small><i></i></span>`).join('');
    const slot = (id, cls) => `
      <div class="ab-slot ${cls}" data-slot="${id}">
        <div class="ab-disc">${ICONS[id] || ''}<span class="ab-ring"></span><span class="ab-sweep"></span><span class="ab-cd"></span></div>
        ${id === 'passive' ? '' : '<kbd class="ab-key"></kbd>'}
        <div class="ab-label"><b></b><small></small></div>
      </div>`;
    root.innerHTML = `
      <div class="hud-danger" aria-hidden="true"><i class="dg-l"></i><i class="dg-r"></i><i class="dg-t"></i><i class="dg-b"></i></div>
      <div class="sb">
        <div class="sb-team sb-home"><span class="sb-you">YOU 你</span><span class="sb-pips"></span><span class="sb-wins"></span><span class="sb-name">${TEAM_LABELS[0].en}<small>${TEAM_LABELS[0].zh}</small></span><span class="sb-score">0</span></div>
        <div class="sb-center"><div class="sb-clock">0:00</div><div class="sb-round"></div><div class="sb-shot"><i></i><b></b></div></div>
        <div class="sb-team sb-away"><span class="sb-score">0</span><span class="sb-name">${TEAM_LABELS[1].en}<small>${TEAM_LABELS[1].zh}</small></span><span class="sb-wins"></span><span class="sb-pips"></span><span class="sb-you">YOU 你</span></div>
      </div>
      <div class="hud-live"><i></i>LIVE <small>直播</small></div>
      <div class="hud-toast"></div>
      <div class="kf"></div>
      <div class="mm"><canvas></canvas><span class="mm-label">MAP 小地圖</span></div>
      <div class="xh"><i class="xh-t"></i><i class="xh-b"></i><i class="xh-l"></i><i class="xh-r"></i><b></b><span class="xh-hit"></span></div>
      <div class="ch"><div class="ch-track"><div class="ch-base"><span></span></div><div class="ch-over"><span></span></div></div><div class="ch-label"></div></div>
      <div class="cf"><b></b><small></small></div>
      <div class="lk"><span class="lk-box"></span><span class="lk-name"></span></div>
      <div class="pt"><span class="pt-tag">PASS ▸ <b></b> <small>傳球</small></span><i></i></div>
      <div class="bp"><i></i></div>
      <div class="sc"><b></b><small>THROW / PASS · 快投或傳</small></div>
      <div class="cd"></div>
      <div class="bn"><div class="bn-main"></div><div class="bn-sub"></div></div>
      <div class="pc">
        <div class="pc-portrait"><img alt="" decoding="async"></div>
        <div class="pc-body">
          <div class="pc-head"><span class="pc-name"></span><span class="pc-title"></span><span class="pc-zone">OUTFIELD 外場</span></div>
          <div class="pc-hp"><div class="hp-ghost"></div><div class="hp-fill"></div><span class="hp-pending">PENDING 延遲淘汰</span></div>
          <div class="pc-hprow"><span class="hp-num">100</span><span class="hp-max">/ 100</span><span class="pc-ult-label">ULT 終極技 <b class="ult-num">0%</b></span></div>
          <div class="pc-ult"><div class="ult-fill"></div></div>
          <div class="pc-status">${chips}<span class="chip tone-good" data-status="counter">COUNTER +20%<small>反擊</small><i></i></span></div>
        </div>
      </div>
      <div class="ab">${slot('passive', 'ab-passive')}${slot('skill', 'ab-skill')}${slot('ultimate', 'ab-ult')}</div>
      <div class="lt"><span class="lt-label">LAST THROW <small>球速</small></span><b class="lt-speed">0</b><span class="lt-unit">km/h</span><span class="lt-rally"></span></div>
      <div class="pr"></div>
      <div class="sp">
        <div class="sp-row"><span class="sp-tag">SPECTATING <small>觀戰中</small></span><span class="sp-name"></span></div>
        <div class="sp-row sp-btns">
          <button type="button" class="sp-btn" data-cmd="camera">⟳ CAMERA <small>切換鏡頭</small></button>
          <button type="button" class="sp-btn" data-cmd="menu">☰ MENU <small>選單</small></button>
        </div>
        <div class="sp-hint"></div>
      </div>
      <div class="lh">CLICK TO PLAY <small>點擊畫面開始操控</small></div>
      <div class="rs du-menu" role="dialog" aria-label="Match result">
        <div class="rs-title"></div>
        <div class="rs-score"><span class="rs-home"></span><i>–</i><span class="rs-away"></span></div>
        <div class="rs-sub"></div>
        <div class="rs-btns">
          <button type="button" tabindex="-1" class="du-nav-item rs-btn" data-cmd="rematch">REMATCH<small>再戰</small></button>
          <button type="button" tabindex="-1" class="du-nav-item rs-btn" data-cmd="heroes">HERO SELECT<small>選擇英雄</small></button>
        </div>
      </div>`;
    ui.appendChild(root);
    this.root = root;
    const q = (s) => root.querySelector(s);
    const e = this.el;
    e.danger = q('.hud-danger');
    e.sb = q('.sb');
    e.center = q('.sb-center');
    e.clock = q('.sb-clock');
    e.shot = q('.sb-shot');
    e.shotNum = q('.sb-shot b');
    e.round = q('.sb-round');
    e.team = [q('.sb-home'), q('.sb-away')];
    e.pips = [q('.sb-home .sb-pips'), q('.sb-away .sb-pips')];
    e.wins = [q('.sb-home .sb-wins'), q('.sb-away .sb-wins')];
    e.score = [q('.sb-home .sb-score'), q('.sb-away .sb-score')];
    e.live = q('.hud-live');
    e.toast = q('.hud-toast');
    e.kf = q('.kf');
    e.mm = q('.mm');
    e.crosshair = q('.xh');
    e.charge = q('.ch');
    e.chargeLabel = q('.ch-label');
    e.catch = q('.cf');
    e.catchMain = q('.cf b');
    e.catchSub = q('.cf small');
    e.lock = q('.lk');
    e.lockName = q('.lk-name');
    e.pass = q('.pt');
    e.passName = q('.pt b');
    e.pointer = q('.bp');
    e.sc = q('.sc');
    e.scNum = q('.sc b');
    e.zone = q('.pc-zone');
    e.count = q('.cd');
    e.banner = q('.bn');
    e.bannerMain = q('.bn-main');
    e.bannerSub = q('.bn-sub');
    e.card = q('.pc');
    e.portrait = q('.pc-portrait img');
    e.name = q('.pc-name');
    e.title = q('.pc-title');
    e.hp = q('.pc-hp');
    e.hpNum = q('.hp-num');
    e.hpMax = q('.hp-max');
    e.ult = q('.pc-ult');
    e.ultNum = q('.ult-num');
    e.chips = {};
    for (const c of root.querySelectorAll('.pc-status .chip')) e.chips[c.dataset.status] = { root: c, time: c.querySelector('i') };
    e.abilities = q('.ab');
    e.slots = {};
    for (const s of root.querySelectorAll('.ab-slot')) {
      e.slots[s.dataset.slot] = { root: s, cd: s.querySelector('.ab-cd'), key: s.querySelector('.ab-key'), name: s.querySelector('.ab-label b'), zh: s.querySelector('.ab-label small') };
    }
    e.lastThrow = q('.lt');
    e.throwSpeed = q('.lt-speed');
    e.throwRally = q('.lt-rally');
    e.prompt = q('.pr');
    e.spectate = q('.sp');
    e.specName = q('.sp-name');
    e.specHint = q('.sp-hint');
    e.lockHint = q('.lh');
    e.results = q('.rs');
    e.portrait.addEventListener('error', () => { e.portrait.style.visibility = 'hidden'; });
    e.portrait.addEventListener('load', () => { e.portrait.style.visibility = ''; });

    this.minimap = new Minimap(q('.mm canvas'));

    root.querySelector('.sp-btns').addEventListener('click', (ev) => {
      const b = ev.target.closest('[data-cmd]');
      if (!b) return;
      if (b.dataset.cmd === 'camera') this._cycleSpectate();
      else if (game.input) game.input.requestPause('touch');
    });
    e.results.addEventListener('mousedown', (ev) => { if (ev.target.closest('button')) ev.preventDefault(); });
    e.results.addEventListener('click', (ev) => {
      const b = ev.target.closest('[data-cmd]');
      if (b) this._resultsCommand(b.dataset.cmd);
    });
  }

  // ------------------------------------------------------------------ timers
  _tickTimers(realDt) {
    if (this._bannerActive) {
      this._bannerT -= realDt;
      this._bannerAge += realDt;
      const cut = this._banners.length > 0 && this._bannerAge >= HUD_TUNING.bannerMinWhenQueued;
      if (this._bannerT <= 0 || cut) this._nextBanner();
    }
    if (this._toastT > 0) { this._toastT -= realDt; if (this._toastT <= 0) setShown(this.el.toast, false); }
    if (this._catchT > 0) { this._catchT -= realDt; if (this._catchT <= 0) setShown(this.el.catch, false); }
    if (this._countT > 0) { this._countT -= realDt; if (this._countT <= 0) setShown(this.el.count, false); }
    if (this._hitT > 0) this._hitT -= realDt;
    if (this._dmgT > 0) this._dmgT -= realDt;
    if (this._failT > 0) this._failT -= realDt;
    if (this._throwT > 0) { this._throwT -= realDt; if (this._throwT <= 0) setClass(this.el.lastThrow, 'fresh', false); }
  }

  _nextBanner() {
    const b = this._banners.shift();
    const el = this.el.banner;
    if (!b || !el) { this._bannerActive = false; if (el) setShown(el, false); return; }
    const nl = b.text.indexOf('\n');
    this.el.bannerMain.textContent = nl >= 0 ? b.text.slice(0, nl) : b.text;
    this.el.bannerSub.textContent = nl >= 0 ? b.text.slice(nl + 1) : '';
    el.style.setProperty('--bc', b.color);
    setShown(el, true);
    replay(el, 'play');
    this._bannerActive = true;
    this._bannerT = b.duration;
    this._bannerAge = 0;
  }

  _resetTransient() {
    this._banners.length = 0;
    this._bannerActive = false;
    setShown(this.el.banner, false);
    setShown(this.el.count, false);
    setShown(this.el.catch, false);
    setShown(this.el.toast, false);
    setShown(this.el.lock, false);
    setShown(this.el.pass, false);
    setShown(this.el.pointer, false);
    setShown(this.el.sc, false);
    setShown(this.el.shot, false);
    this._ptTarget = null;
    this._danger.shown = 0; this._danger.active = false; this._danger.ball = null;
    setVarNum(this.el.danger, '--dz', 0);
    this._lockTarget = null;
  }

  // ------------------------------------------------------------------ scoreboard
  _updateScoreboard(match, local) {
    const e = this.el;
    const tl = Math.max(0, Number.isFinite(match.timeLeft) ? match.timeLeft : 0);
    const key = tl < 10 ? Math.ceil(tl * 10) : 100000 + Math.ceil(tl);
    if (key !== this._clockKey) { this._clockKey = key; setText(e.clock, formatClock(tl)); }
    setClass(e.center, 'low', match.phase === 'playing' && tl <= HUD_TUNING.lowTime && tl > 0);
    const r = match.round || 1;
    if (r !== this._round) { this._round = r; setText(e.round, `ROUND ${r} · 第${r}回合`); }

    const rules = match.rules || {};
    const toWin = rules.roundsToWin || 2;
    const scores = match.scores || [0, 0];
    const sk = (scores[0] | 0) * 1000 + (scores[1] | 0) + toWin * 1e6;
    if (sk !== this._scoreKey) {
      this._scoreKey = sk;
      for (let t = 0; t < 2; t++) {
        setText(e.score[t], String(scores[t] | 0));
        const w = e.wins[t];
        if (w.childElementCount !== toWin) w.innerHTML = '<i></i>'.repeat(toWin);
        for (let i = 0; i < w.children.length; i++) setClass(w.children[i], 'on', (scores[t] | 0) > i);
      }
    }
    setClass(e.team[0], 'mine', !!local && local.team === TEAM.HOME);
    setClass(e.team[1], 'mine', !!local && local.team === TEAM.AWAY);

    // Player pips: one per player slot. 1 infield, 2 outfield (eliminated), 3 down (ragdoll), 4 pending elimination,
    // 5 starting outfielder (元外野, ring).
    const per = rules.playersPerTeam || 4;
    for (let t = 0; t < 2; t++) {
      const box = e.pips[t];
      if (box.childElementCount !== per) { box.innerHTML = '<i></i>'.repeat(per); this._pipState[t].length = 0; }
    }
    let hi = 0, ai = 0;
    const players = game.players;
    for (let i = 0; i < players.length; i++) {
      const p = players[i];
      if (p.team !== TEAM.HOME && p.team !== TEAM.AWAY) continue;
      const t = p.team === TEAM.HOME ? 0 : 1;
      const idx = t === 0 ? hi++ : ai++;
      if (idx >= per) continue;
      let st = 1;
      if (p.health && p.health.pending) st = 4;
      else if (p.fsm && p.fsm.incapReason === 'eliminated') st = 3;
      else if (p.zone === ZONE.OUTFIELD) st = p.isStartingOutfielder ? 5 : 2;
      if (p === local) st += 10;
      this._setPip(t, idx, st);
    }
    for (let i = hi; i < per; i++) this._setPip(0, i, 0);
    for (let i = ai; i < per; i++) this._setPip(1, i, 0);
  }

  _setPip(t, i, st) {
    const arr = this._pipState[t];
    if (arr[i] === st) return;
    arr[i] = st;
    const el = this.el.pips[t].children[i];
    if (!el) return;
    const base = st % 10;
    el.className = `${base === 1 ? 'in' : base === 2 ? 'out' : base === 3 ? 'down' : base === 4 ? 'pending' : base === 5 ? 'ob' : ''}${st >= 10 ? ' me' : ''}`;
  }

  // ------------------------------------------------------------------ player card
  _updateCard(local, realDt) {
    const e = this.el;
    setShown(e.card, !!local);
    if (!local) { this._cardPlayer = null; return; }
    const hero = local.hero || {};
    if (this._cardPlayer !== local || this._cardHero !== hero.id) this._rebuildCard(local, hero);

    const h = local.health;
    const max = h && h.maxHp > 0 ? h.maxHp : (hero.maxHp || 100);
    const hp = h ? Math.max(0, Math.min(max, h.hp)) : max;
    const frac = clamp01(hp / max);
    if (frac < this._lastHpFrac - 1e-4) this._ghostHold = HUD_TUNING.hpGhostDelay;
    if (frac >= this._ghost) this._ghost = frac;
    else if (this._ghostHold > 0) this._ghostHold -= realDt;
    else this._ghost = Math.max(frac, this._ghost - HUD_TUNING.hpGhostRate * realDt);
    this._lastHpFrac = frac;
    setVarNum(e.hp, '--hp', frac);
    setVarNum(e.hp, '--ghost', this._ghost);
    const hpi = Math.ceil(hp);
    if (hpi !== this._hpInt) { this._hpInt = hpi; setText(e.hpNum, String(hpi)); }
    if (max !== this._hpMax) { this._hpMax = max; setText(e.hpMax, `/ ${Math.round(max)}`); }
    setClass(e.card, 'low', frac > 0 && frac <= HUD_TUNING.lowHp);
    setClass(e.card, 'pending', !!(h && h.pending));
    setClass(e.card, 'outfield', local.zone === ZONE.OUTFIELD);
    setText(e.zone, local.isStartingOutfielder ? 'OUTFIELD 外場 · 元外野' : 'OUTFIELD 外場');
    setClass(e.card, 'down', !!(local.fsm && local.fsm.incapReason === 'eliminated'));

    const ab = local.abilities;
    const ult = ab ? clamp01(ab.ultimateCharge || 0) : 0;
    const ready = this._ultReady(local);
    setVarNum(e.ult, '--ult', ult);
    const pct = ready ? 101 : Math.floor(ult * 100);
    if (pct !== this._ultPct) { this._ultPct = pct; setText(e.ultNum, ready ? 'READY 就緒' : `${pct}%`); }
    setClass(e.card, 'ult-ready', ready);
  }

  _rebuildCard(local, hero) {
    const e = this.el;
    this._cardPlayer = local;
    this._cardHero = hero.id;
    this._ghost = 1; this._ghostHold = 0; this._lastHpFrac = 1; this._hpInt = -1; this._hpMax = -1; this._ultPct = -1;
    const url = hero.id ? portraitUrl(hero.id) : '';
    if (url && e.portrait.getAttribute('src') !== url) e.portrait.setAttribute('src', url);
    setText(e.name, String(hero.id || local.name || 'YOU').toUpperCase());
    setText(e.title, `${hero.titleZh || ''}`);
    setVarStr(e.card, '--tc', teamCss(local.team));
    setVarStr(e.card, '--hc', cssColor(hero.color, WHITE));
    setVarStr(e.abilities, '--hc', cssColor(hero.color, WHITE));
    const defs = (hero.abilities) || {};
    for (const slot of ['passive', 'skill', 'ultimate']) {
      const s = e.slots[slot];
      const inst = local.abilities && local.abilities[slot];
      const def = (inst && inst.def) || defs[slot];
      setText(s.name, def ? def.name : '—');
      setText(s.zh, def ? (def.nameZh || '') : '');
      setClass(s.root, 'empty', !def);
      s.root.title = def ? `${def.name} ${def.nameZh || ''} — ${def.desc || ''}` : '';
    }
    // Overcharge-style passive (Rayne): keeps charging to params.fullTime for a speed bonus.
    const passive = local.abilities && local.abilities.passive;
    const params = (passive && passive.params) || (defs.passive && defs.passive.params) || {};
    const pid = (passive && passive.id) || (defs.passive && defs.passive.id) || '';
    this._overTime = pid === 'rayne.overcharge' || (params.fullTime > 0 && params.maxSpeedBonus > 0) ? (params.fullTime || 2) : 0;
    this._overBonus = params.maxSpeedBonus || 0.5;
    this._chargeKey = -1;
    this._cdKey.skill = -1; this._cdKey.ultimate = -1;
    this._hintDevice = '';
  }

  _ultReady(local) {
    const ab = local && local.abilities;
    if (!ab) return false;
    const u = ab.ultimate;
    const cost = u && u.def ? (u.def.ultCost ?? 1) : 1;
    return typeof ab.isUltimateReady === 'function' ? ab.isUltimateReady(cost) : (ab.ultimateCharge || 0) >= cost;
  }

  // ------------------------------------------------------------------ abilities
  _updateAbilities(local) {
    const e = this.el;
    const input = game.input;
    const touchUi = !!(input && input.touch && input.touch.visible);
    setShown(e.abilities, !!local && !touchUi);
    if (!local) return;
    const device = input ? input.lastDevice : 'kbm';
    if (device !== this._hintDevice) {
      this._hintDevice = device;
      setText(e.slots.skill.key, input ? input.hint('skill') : 'F');
      setText(e.slots.ultimate.key, input ? input.hint('ultimate') : 'R');
      setClass(e.slots.skill.key, 'is-on', !!(input ? input.hint('skill') : 'F'));
      setClass(e.slots.ultimate.key, 'is-on', !!(input ? input.hint('ultimate') : 'R'));
    }
    const ab = local.abilities;
    const ultCharge = ab ? clamp01(ab.ultimateCharge || 0) : 0;
    const hasBall = !!(local.combat && local.combat.hasBall);
    for (let k = 0; k < 2; k++) {
      const slot = k === 0 ? 'skill' : 'ultimate';
      const s = e.slots[slot];
      const a = ab && ab[slot];
      if (!a) {
        if (touchUi) input.touch.setAbilityState(slot, 0, '', false, false, 0);
        continue;
      }
      const busy = !!(a.isActive || a.isCasting);
      const cdFrac = a.phase === 'cooldown' ? clamp01(a.cooldownNormalized || 0) : 0;
      const cdSec = a.phase === 'cooldown' ? (a.cooldownRemaining || 0) : 0;
      const meterOk = slot !== 'ultimate' || this._ultReady(local);
      const ready = !!a.isReady && meterOk;
      const needBall = !!(a.def && a.def.requiresBall) && !hasBall;
      setVarNum(s.root, '--cd', cdFrac);
      setVarNum(s.root, '--charge', slot === 'ultimate' ? ultCharge : 1);
      setClass(s.root, 'ready', ready && !needBall);
      setClass(s.root, 'active', busy);
      setClass(s.root, 'cooling', cdFrac > 0);
      setClass(s.root, 'need-ball', needBall);
      // Label: cooldown seconds, or remaining active time.
      const shownSec = cdSec > 0 ? cdSec : busy ? (a.activeRemaining || 0) : 0;
      const ck = shownSec > 0 ? Math.ceil(shownSec * (shownSec < 3 ? 10 : 1)) + (shownSec < 3 ? 0 : 10000) : -2;
      if (ck !== this._cdKey[slot]) { this._cdKey[slot] = ck; setText(s.cd, formatCooldown(shownSec)); }
      if (touchUi) input.touch.setAbilityState(slot, cdFrac, s.cd.textContent, ready && !needBall, busy, slot === 'ultimate' ? ultCharge : 1);
    }
  }

  // ------------------------------------------------------------------ crosshair / charge / catch
  _updateCenter(local) {
    const e = this.el;
    const combat = local && local.combat;
    const aiming = !!local && !(local.fsm && local.fsm.incapReason === 'eliminated');
    setShown(e.crosshair, aiming);
    if (!aiming) { setShown(e.charge, false); return; }
    setClass(e.crosshair, 'has-ball', !!(combat && combat.hasBall));
    setClass(e.crosshair, 'hit', this._hitT > 0);
    setClass(e.crosshair, 'catching', !!(combat && combat.catchArmed));
    const charging = !!(combat && combat.isCharging);
    setShown(e.charge, charging);
    if (!charging) { this._chargeKey = -1; return; }
    const full = (combat.profile && combat.profile.fullChargeTime) || 0.75;
    const m = chargeModel(combat.charge, combat.chargeSeconds, full, this._overTime, this._charge);
    setVarNum(e.charge, '--c', m.base);
    setVarNum(e.charge, '--o', m.over);
    setClass(e.charge, 'has-over', this._overTime > 0);
    setClass(e.charge, 'full', m.base >= 0.999);
    setClass(e.charge, 'overcharging', m.over > 0);
    const overPct = Math.round(this._overBonus * m.bonus * 100);
    const key = this._overTime > 0 && m.base >= 0.999 ? 1000 + overPct : Math.round(m.base * 100);
    if (key !== this._chargeKey) {
      this._chargeKey = key;
      setText(e.chargeLabel, key >= 1000 ? `OVERCHARGE +${overPct}% · 過載` : `CHARGE ${key}% · 蓄力`);
    }
  }

  _catchFeedback(kind, main, sub) {
    const e = this.el;
    e.catch.dataset.kind = kind;
    e.catchMain.textContent = main;
    e.catchSub.textContent = sub || '';
    setShown(e.catch, true);
    replay(e.catch, 'pop');
    this._catchT = HUD_TUNING.catchTime;
  }

  // ------------------------------------------------------------------ lock marker
  _updateLock(local) {
    const e = this.el;
    const target = local && local.combat ? local.combat.currentTarget : null;
    const cam = game.camera;
    if (!target || !cam || target.isTargetable === false || !(target.chestPosition || target.position)) {
      setShown(e.lock, false);
      this._lockTarget = null;
      return;
    }
    if (target !== this._lockTarget) {
      this._lockTarget = target;
      this._lockDist = -1;
      setText(e.lockName, nameOf(target));
      setVarStr(e.lock, '--lc', teamCss(target.team));
      replay(e.lock, 'acquire');
    }
    cam.updateMatrixWorld();
    _proj.copy(target.chestPosition || target.position).project(cam);
    const behind = _proj.z > 1;
    let x = (_proj.x * 0.5 + 0.5) * this._vw;
    let y = (0.5 - _proj.y * 0.5) * this._vh;
    if (behind) { x = this._vw - x; y = this._vh - 40; }
    const m = 36;
    const off = behind || x < m || x > this._vw - m || y < m || y > this._vh - m;
    x = Math.min(this._vw - m, Math.max(m, x));
    y = Math.min(this._vh - m, Math.max(m, y));
    setShown(e.lock, true);
    setClass(e.lock, 'offscreen', off);
    if (Math.abs(x - this._lockX) >= 0.5 || Math.abs(y - this._lockY) >= 0.5) {
      this._lockX = x; this._lockY = y;
      e.lock.style.transform = `translate3d(${x.toFixed(1)}px,${y.toFixed(1)}px,0)`;
    }
  }

  // ------------------------------------------------------------------ pass marker / ball pointer
  /** 8 Hz: which teammate a pass would reach now (Combat.previewPassReceiver - aim cone, else nearest). */
  _scanPassReceiver(local) {
    const c = local && local.combat;
    let r = null;
    if (c && c.hasBall && typeof c.previewPassReceiver === 'function' && !(local.fsm && local.fsm.incapReason)) {
      try { r = c.previewPassReceiver(); } catch { r = null; }
    }
    if (r === this._ptTarget) return;
    this._ptTarget = r;
    if (r) {
      setText(this.el.passName, nameOf(r));
      setVarStr(this.el.pass, '--pc', teamCss(r.team));
      replay(this.el.pass, 'acquire');
    }
  }

  /** Chevron over the pass receiver (clamped to the screen edge when off-screen). */
  _updatePassMarker(local) {
    const e = this.el;
    const r = this._ptTarget;
    const cam = game.camera;
    if (!r || !cam || !r.position || !local || !local.combat || !local.combat.hasBall) { setShown(e.pass, false); return; }
    cam.updateMatrixWorld();
    _proj.copy(r.position);
    _proj.y += HUD_TUNING.passMarkerLift;
    _proj.project(cam);
    const behind = _proj.z > 1;
    let x = (_proj.x * 0.5 + 0.5) * this._vw;
    let y = (0.5 - _proj.y * 0.5) * this._vh;
    if (behind) { x = this._vw - x; y = this._vh - 60; }
    const m = 40;
    const off = behind || x < m || x > this._vw - m || y < m || y > this._vh - m;
    x = Math.min(this._vw - m, Math.max(m, x));
    y = Math.min(this._vh - m, Math.max(m, y));
    setShown(e.pass, true);
    setClass(e.pass, 'offscreen', off);
    if (Math.abs(x - this._ptX) >= 0.5 || Math.abs(y - this._ptY) >= 0.5) {
      this._ptX = x; this._ptY = y;
      e.pass.style.transform = `translate3d(${x.toFixed(1)}px,${y.toFixed(1)}px,0)`;
    }
  }

  /**
   * Edge arrow toward the single ball when it is off-screen (phones: the action is often out of view). Coloured by
   * the holder's team (white when loose); pulses while an enemy holder winds up. A cloaked enemy holder is not given
   * away.
   */
  _updateBallPointer(local) {
    const e = this.el;
    const cam = game.camera;
    const ball = game.balls && game.balls.ball;
    const m = game.match;
    let show = !!(local && cam && ball && ball.position && m && m.isPlaying && ball.state !== 'despawned');
    const holder = show && ball.state === 'held' ? ball.holder : null;
    if (holder && (holder === local || (game.areEnemies(local, holder) && holder.status
      && typeof holder.status.has === 'function' && holder.status.has('cloaked') && !holder.status.has('revealed')))) show = false;
    if (!show) { setShown(e.pointer, false); return; }
    cam.updateMatrixWorld();
    _proj.copy(holder ? (holder.chestPosition || holder.position) : ball.position).project(cam);
    const behind = _proj.z > 1;
    const hw = this._vw / 2, hh = this._vh / 2, mg = HUD_TUNING.ballPointerMargin;
    let dx = _proj.x * hw, dy = -_proj.y * hh;
    if (!behind && Math.abs(dx) < hw - mg * 0.5 && Math.abs(dy) < hh - mg * 0.5) { setShown(e.pointer, false); return; }
    if (behind) { dx = -dx; dy = -dy; if (Math.abs(dx) + Math.abs(dy) < 1) dy = hh; }
    const t = Math.min((hw - mg) / Math.max(1e-3, Math.abs(dx)), (hh - mg) / Math.max(1e-3, Math.abs(dy)));
    const x = hw + dx * t, y = hh + dy * t, a = Math.atan2(dy, dx);
    setShown(e.pointer, true);
    setVarStr(e.pointer, '--bc', holder ? teamCss(holder.team) : WHITE);
    setClass(e.pointer, 'charging', !!(holder && game.areEnemies(local, holder) && holder.combat && holder.combat.isCharging));
    if (Math.abs(x - this._bpX) >= 0.5 || Math.abs(y - this._bpY) >= 0.5 || Math.abs(a - this._bpA) >= 0.02) {
      this._bpX = x; this._bpY = y; this._bpA = a;
      e.pointer.style.transform = `translate3d(${x.toFixed(1)}px,${y.toFixed(1)}px,0) rotate(${a.toFixed(3)}rad)`;
    }
  }

  // ------------------------------------------------------------------ possession shot clock
  /**
   * Scoreboard shot-clock bar (possessing team colour, remaining / limit, tenths in the last 3 s), the possession dot
   * on the team name, and the big local 3-2-1 when the local player holds the ball or owns the dead ball.
   */
  _updateShotClock(match, local) {
    const e = this.el;
    const pos = match.possession;
    const on = !!pos && match.phase === 'playing' && pos.limit > 0 && (pos.team === TEAM.HOME || pos.team === TEAM.AWAY);
    const team = on ? pos.team : TEAM.NONE;
    if (team !== this._hasBallTeam) {
      this._hasBallTeam = team;
      setClass(e.team[0], 'has-ball', team === TEAM.HOME);
      setClass(e.team[1], 'has-ball', team === TEAM.AWAY);
    }
    setShown(e.shot, on);
    if (!on) { this._shotKey = -2; this._scKey = -1; setShown(e.sc, false); return; } // -2: rewrite the text when shown again
    if (team !== this._shotTeam) { this._shotTeam = team; setVarStr(e.shot, '--st', teamCss(team)); }
    const rem = Math.max(0, Number.isFinite(pos.remaining) ? pos.remaining : pos.limit);
    const warn = rem <= HUD_TUNING.shotClockWarn;
    setVarNum(e.shot, '--p', clamp01(rem / pos.limit), 100);
    const key = warn ? Math.ceil(rem * 10) : -1;
    if (key !== this._shotKey) { this._shotKey = key; setText(e.shotNum, warn ? `HOLD ${rem.toFixed(1)} 持球` : ''); }
    setClass(e.shot, 'warn', warn);
    setClass(e.shot, 'paused', !pos.running);
    // Local countdown: my hands, or a dead ball resting in my own zone.
    let mine = false;
    if (local && local.team === team && warn && rem > 0) {
      if (pos.holder === local) mine = true;
      else if (pos.deadBall && !pos.holder && game.court && game.balls && game.balls.ball) {
        const z = game.court.zoneAt(game.balls.ball.position, _zoneOut);
        mine = !!z && z.team === local.team && z.zone === local.zone;
      }
    }
    setShown(e.sc, mine);
    const n = mine ? Math.max(1, Math.ceil(rem)) : -1;
    if (n !== this._scKey) { this._scKey = n; if (n > 0) setText(e.scNum, String(n)); }
  }

  _onPossessionWarning(ev) {
    const local = game.localPlayer;
    if (!ev || !local || ev.team !== local.team) return;
    replay(this.el.sc, 'pop');
  }

  _onPossessionViolation(ev) {
    if (!ev) return;
    const local = game.localPlayer;
    const to = ev.awardedTo;
    const L = TEAM_LABELS[to] || { en: '', zh: '' };
    const color = local ? (ev.team === local.team ? RED : GOLD) : teamCss(to);
    this.banner(`TIME VIOLATION\n持球超時 · 球權交給 ${L.en} ${L.zh}`, color, 1.7);
  }

  _onServeReady(ev) {
    if (!ev) return;
    const local = game.localPlayer;
    const t = ev.team;
    const L = TEAM_LABELS[t];
    if (!L) return;
    const lim = (game.match && game.match.rules && game.match.rules.possessionLimit) || 0;
    if (local && ev.player === local) {
      this.banner(`YOUR SERVE\n你發球${lim > 0 ? ` · ${lim} 秒內投出或傳球` : ''}`, teamCss(t), 1.9);
    } else {
      this.banner(`${L.en} SERVES\n${L.en} 發球 · ${L.zh}持球開球`, teamCss(t), 1.6);
    }
  }

  _onBallAwarded(ev) {
    if (!ev) return;
    if (this.minimap) this.minimap.setAward(ev);
    const L = TEAM_LABELS[ev.team];
    if (ev.cause === 'outOfArena') this.toast(`OUT OF PLAY · 出界 — ${L ? `${L.en} BALL ${L.zh}球權` : ''}`, teamCss(ev.team), 2);
    else if (ev.cause === 'vanish') this.toast('VANISHED · 球被變走了', '#c9a7ff', 1.8);
  }

  // ------------------------------------------------------------------ Danger Sense
  _onDanger(ev) {
    if (!ev || ev.player !== game.localPlayer) return;
    const d = this._danger;
    d.active = ev.active !== false;
    const tti = Number.isFinite(ev.timeToImpact) ? ev.timeToImpact : 0.6;
    d.level = Math.max(0.45, Math.min(1, 1 - tti / HUD_TUNING.dangerTimeScale));
    d.ball = ev.ball || null;
    d.t = game.time.realNow;
  }

  _updateDanger(realDt) {
    const d = this._danger;
    const target = d.active && game.time.realNow - d.t < HUD_TUNING.dangerHold ? d.level : 0;
    const rate = target > d.shown ? HUD_TUNING.dangerRise : HUD_TUNING.dangerFall;
    d.shown += (target - d.shown) * Math.min(1, realDt * rate);
    if (d.shown < 0.003) d.shown = 0;
    const el = this.el.danger;
    setVarNum(el, '--dz', d.shown, 200);
    if (d.shown <= 0) return;
    // Directional emphasis: the edge facing the incoming ball glows hardest (camera space, works behind the view).
    let l = 0.5, r = 0.5, t = 0.5, b = 0.5;
    const cam = game.camera;
    if (d.ball && d.ball.position && cam) {
      cam.updateMatrixWorld();
      _cam.copy(d.ball.position).applyMatrix4(cam.matrixWorldInverse);
      const behind = _cam.z > 0;
      const len = Math.hypot(_cam.x, _cam.y) || 1;
      const hx = _cam.x / len, hy = _cam.y / len;
      l = 0.35 + Math.max(0, -hx) * 0.65; r = 0.35 + Math.max(0, hx) * 0.65;
      t = 0.35 + Math.max(0, hy) * 0.65; b = 0.35 + Math.max(0, -hy) * 0.65;
      if (behind) { b = 1; l = Math.max(l, 0.7); r = Math.max(r, 0.7); }
    }
    setVarNum(el, '--dl', l, 50); setVarNum(el, '--dr', r, 50); setVarNum(el, '--dt', t, 50); setVarNum(el, '--db', b, 50);
  }

  // ------------------------------------------------------------------ spectator / hints
  _updateSpectate(local) {
    const e = this.el;
    const spectating = !local;
    setShown(e.spectate, spectating);
    setShown(e.live, spectating);
    if (!spectating) return;
    const input = game.input;
    if (input && !input.blocked && (input.actions.cycleTarget.pressed || input.actions.throw.pressed)) this._cycleSpectate();
    const rig = game.cameraRig;
    const tgt = rig ? rig.target : null;
    if (tgt !== this._specTarget) {
      this._specTarget = tgt;
      if (tgt && tgt.hero) {
        setText(e.specName, `${nameOf(tgt)} · ${tgt.hero.titleZh || ''}`);
        setVarStr(e.spectate, '--tc', teamCss(tgt.team));
      } else {
        setText(e.specName, 'OVERVIEW · 全景');
        setVarStr(e.spectate, '--tc', WHITE);
      }
    }
    const dev = input ? input.lastDevice : 'kbm';
    setText(e.specHint, dev === 'gamepad' ? 'R3 camera 切換鏡頭 · START menu 選單'
      : dev === 'touch' ? '' : 'Tab / click: camera 切換鏡頭 · Esc: menu 選單');
  }

  _cycleSpectate() {
    const rig = game.cameraRig;
    if (!rig || typeof rig.setTarget !== 'function') return;
    const list = game.players.filter((p) => p && p.position && !(p.fsm && p.fsm.incapReason === 'eliminated'));
    const cur = rig.target || null;
    const i = cur ? list.indexOf(cur) : -1;
    const next = i + 1 < list.length ? list[i + 1] : null; // ... -> last player -> overview -> first player
    try { rig.setTarget(next, false); } catch (err) { console.warn('[hud] spectate camera switch failed', err); }
  }

  _updateHints(local, phase) {
    const input = game.input;
    const show = !!local && !!input && !input.touchMode && !input.pointerLocked && !input.lockUnavailable && !input.blocked
      && input.lastDevice === 'kbm' && (phase === 'playing' || phase === 'countdown' || phase === 'preRound');
    setShown(this.el.lockHint, show);
  }

  // ------------------------------------------------------------------ periodic (minimap, pickup prompt, chips)
  _updatePeriodic(local, realDt) {
    const e = this.el;
    const mmOn = !!Settings.values.showMinimap;
    setShown(e.mm, mmOn);
    this._mmT -= realDt;
    if (mmOn && this._mmT <= 0) {
      this._mmT = HUD_TUNING.minimapInterval;
      try { this.minimap.draw(local); } catch (err) { if (!this._mmErr) { this._mmErr = true; console.warn('[hud] minimap draw failed', err); } }
    }
    this._scanT -= realDt;
    if (this._scanT <= 0) {
      this._scanT = HUD_TUNING.scanInterval;
      this._scanPickup(local);
      this._scanPassReceiver(local);
      if (this._lockTarget && local) {
        const d = Math.round(this._lockTarget.position.distanceTo(local.position) * 2) / 2;
        if (d !== this._lockDist) { this._lockDist = d; setText(e.lockName, `${nameOf(this._lockTarget)}  ${d.toFixed(1)} m`); }
      }
    }
    this._statusT -= realDt;
    if (this._statusT <= 0 && local) {
      this._statusT = HUD_TUNING.statusInterval;
      const st = local.status;
      for (let i = 0; i < STATUS_TYPES.length; i++) {
        const type = STATUS_TYPES[i];
        const chip = e.chips[type];
        const on = !!(st && typeof st.has === 'function' && st.has(type));
        setShown(chip.root, on);
        if (on) {
          const rem = typeof st.remaining === 'function' ? st.remaining(type) : 0;
          setText(chip.time, rem > 0 && rem < 60 ? formatCooldown(rem) : '');
        }
      }
      const combat = local.combat;
      const boost = combat && combat.counterBoostUntil > game.time.now ? combat.counterBoostUntil - game.time.now : 0;
      setShown(e.chips.counter.root, boost > 0);
      if (boost > 0) setText(e.chips.counter.time, formatCooldown(boost));
    }
  }

  _scanPickup(local) {
    const e = this.el;
    const combat = local && local.combat;
    let show = false;
    if (combat && !combat.hasBall && game.balls && !(local.fsm && local.fsm.incapReason) && game.match && game.match.isPlaying) {
      const r = (combat.profile && combat.profile.manualPickupRadius) || 1.6;
      let ball = null;
      try {
        _pickupFor = local;
        if (typeof game.balls.findNearest === 'function') ball = game.balls.findNearest(local.position, PICKUP_BALL, r);
      } catch { ball = null; }
      _pickupFor = null;
      show = !!ball;
    }
    if (show) {
      const key = game.input ? game.input.hint('pickup') : 'E';
      setText(e.prompt, key ? `[${key}] PICK UP · 撿球` : 'GRAB · 撿球');
    }
    setShown(e.prompt, show);
  }

  // ------------------------------------------------------------------ kill feed
  _feed(html, color, isLocal) {
    const kf = this.el.kf;
    if (!kf) return;
    const item = document.createElement('div');
    item.className = `kf-item${isLocal ? ' me' : ''}`;
    item.style.setProperty('--c1', color || WHITE);
    item.innerHTML = html;
    kf.prepend(item);
    this._kills.unshift({ el: item, t: HUD_TUNING.killFeedLife });
    while (this._kills.length > HUD_TUNING.killFeedMax) { const k = this._kills.pop(); k.el.remove(); }
  }

  _updateKillFeed(realDt) {
    for (let i = this._kills.length - 1; i >= 0; i--) {
      const k = this._kills[i];
      k.t -= realDt;
      if (k.t <= 0.35 && !k.fading) { k.fading = true; k.el.classList.add('fade'); }
      if (k.t <= 0) { k.el.remove(); this._kills.splice(i, 1); }
    }
  }

  // ------------------------------------------------------------------ match flow events
  _onMatchStarted() {
    this._closeResults();
    for (const k of this._kills) k.el.remove();
    this._kills.length = 0;
    this._scoreKey = -1; this._round = -1; this._clockKey = -1;
    this._pipState[0].length = 0; this._pipState[1].length = 0;
    this._shotKey = -2; this._shotTeam = -2; this._scKey = -1; this._hasBallTeam = -2;
    this._ptTarget = null;
    if (this.minimap) this.minimap.setAward(null);
    this._cardPlayer = null;
    this._specTarget = undefined;
    setClass(this.el.lastThrow, 'is-on', false);
  }

  _onPhase(ev) {
    const cur = ev && ev.current;
    if (cur !== 'matchEnd') this._closeResults();
    if (cur === 'preRound') {
      const m = game.match;
      const r = (m && m.round) || 1;
      const toWin = (m && m.rules && m.rules.roundsToWin) || 2;
      const s = (m && m.scores) || [0, 0];
      const decider = s[0] === toWin - 1 && s[1] === toWin - 1;
      this.banner(decider ? `FINAL ROUND\n決勝局 · ROUND ${r}` : `ROUND ${r}\n第 ${r} 回合`, WHITE, 1.6);
    }
  }

  _onCountdown(ev) {
    const n = Math.ceil((ev && Number.isFinite(ev.secondsLeft) ? ev.secondsLeft : 0) - 1e-3);
    if (n <= 0) return;
    if (n === this._lastCount && this._countT > 0) return;
    this._lastCount = n;
    const el = this.el.count;
    el.textContent = String(n);
    setShown(el, true);
    replay(el, 'pop');
    this._countT = HUD_TUNING.countdownTime;
  }

  _hideCountdown() { this._countT = 0; this._lastCount = -1; setShown(this.el.count, false); }

  _onRoundEnded(ev) {
    const local = game.localPlayer;
    const w = ev ? ev.winner : -1;
    const why = ev && ev.reason === 'time' ? 'TIME UP · 時間到' : '';
    if (w !== TEAM.HOME && w !== TEAM.AWAY) { this.banner(`DRAW\n平手${why ? ` · ${why}` : ''}`, '#c8ced6', 2); return; }
    if (local) {
      const won = w === local.team;
      this.banner(won ? `ROUND WON\n贏得本局${why ? ` · ${why}` : ''}` : `ROUND LOST\n輸掉本局${why ? ` · ${why}` : ''}`, won ? GOLD : RED, 2.2);
    } else {
      const L = TEAM_LABELS[w];
      this.banner(`${L.en} TAKES ROUND ${ev.round || ''}\n${L.zh}拿下本局${why ? ` · ${why}` : ''}`, teamCss(w), 2.2);
    }
  }

  _onMatchEnded(ev) {
    const local = game.localPlayer;
    const w = ev ? ev.winner : -1;
    let title, color;
    if (w !== TEAM.HOME && w !== TEAM.AWAY) { title = 'DRAW\n平手'; color = '#c8ced6'; }
    else if (local) { const won = w === local.team; title = won ? 'VICTORY\n勝利' : 'DEFEAT\n落敗'; color = won ? GOLD : RED; }
    else { title = `${TEAM_LABELS[w].en} WINS\n${TEAM_LABELS[w].zh}獲勝`; color = teamCss(w); }
    this._banners.length = 0;
    this.banner(title, color, HUD_TUNING.resultsDelay + 0.2);
    const r = this._results;
    // AI-only (attract) matches loop by themselves: no result menu there. Otherwise Match opens hero select after its
    // matchEndDelay; until then the panel offers an instant rematch.
    const m = game.match;
    r.t = local && !(m && m.setup && m.setup.spectate) ? HUD_TUNING.resultsDelay : -1;
    r.title = title; r.color = color;
    r.scores = (ev && ev.scores) || (game.match && game.match.scores) || [0, 0];
  }

  _updateResults(realDt) {
    const r = this._results;
    if (r.t > 0) { r.t -= realDt; if (r.t <= 0) this._openResults(); }
  }

  _openResults() {
    const r = this._results;
    const e = this.el;
    if (r.open || !this._enabled) return;
    r.open = true;
    const [main, sub] = String(r.title || '').split('\n');
    e.results.querySelector('.rs-title').textContent = main || '';
    e.results.querySelector('.rs-sub').textContent = sub || '';
    e.results.style.setProperty('--rc', r.color || WHITE);
    e.results.querySelector('.rs-home').textContent = `HOME ${r.scores[0] | 0}`;
    e.results.querySelector('.rs-away').textContent = `${r.scores[1] | 0} AWAY`;
    setShown(e.results, true);
    r.focus = 0;
    this._applyResultsFocus();
    if (game.input) { game.input.releaseLock(); game.input.pushMenu(this); }
  }

  _closeResults() {
    const r = this._results;
    r.t = -1;
    if (!r.open) return;
    r.open = false;
    setShown(this.el.results, false);
    if (game.input) game.input.popMenu(this);
  }

  _applyResultsFocus() {
    const btns = this.el.results.querySelectorAll('.rs-btn');
    for (let i = 0; i < btns.length; i++) btns[i].classList.toggle('focus', i === this._results.focus);
  }

  _resultsCommand(cmd) {
    this._closeResults();
    if (cmd === 'rematch') restartMatch();
    else backToHeroSelect();
  }

  /** Menu actions while the results panel is open (Input menu stack). */
  onMenuAction(action) {
    if (!this._results.open) return false;
    const r = this._results;
    switch (action) {
      case 'left': case 'up': r.focus = 0; this._applyResultsFocus(); return true;
      case 'right': case 'down': r.focus = 1; this._applyResultsFocus(); return true;
      case 'confirm': this._resultsCommand(r.focus === 0 ? 'rematch' : 'heroes'); return true;
      case 'start': this._resultsCommand('rematch'); return true;
      case 'back': this._resultsCommand('heroes'); return true;
      default: return false;
    }
  }

  // ------------------------------------------------------------------ gameplay events
  _onEliminated(ev) {
    if (!ev || !ev.player) return;
    const local = game.localPlayer;
    const v = ev.player, a = ev.attacker;
    const tag = causeLabel(ev.cause);
    const html = a && a !== v
      ? `<span class="kf-a" style="color:${teamCss(a.team)}">${esc(nameOf(a))}</span><i class="kf-ico"></i><span class="kf-v" style="color:${teamCss(v.team)}">${esc(nameOf(v))}</span>${tag ? `<small>${esc(tag)}</small>` : ''}`
      : `<i class="kf-ico"></i><span class="kf-v" style="color:${teamCss(v.team)}">${esc(nameOf(v))}</span><small>${esc(tag || 'OUT · 出局')}</small>`;
    this._feed(html, teamCss(a ? a.team : v.team), v === local || a === local);
    if (v === local) this.banner('ELIMINATED\n出局 — 到外場繼續投球 · keep throwing from the outfield', RED, 2.2);
    else if (a && a === local) { this._hitT = HUD_TUNING.hitMarkerTime * 1.6; this.toast(`ELIMINATED ${nameOf(v)} · 擊倒`, GOLD, 1.6); }
  }

  _onRevived(ev) {
    if (!ev || !ev.player) return;
    const p = ev.player;
    const why = ev.cause === 'perfectCatch' ? 'PERFECT CATCH · 完美接球' : ev.cause === 'outfieldHit' || ev.cause === 'outfield' ? 'OUTFIELD HIT · 外場擊中' : 'BACK IN · 回場';
    this._feed(`<i class="kf-rev">↺</i><span class="kf-v" style="color:${teamCss(p.team)}">${esc(nameOf(p))}</span><small>${esc(why)}</small>`, teamCss(p.team), p === game.localPlayer);
    if (p === game.localPlayer) this.banner('BACK IN!\n回到內場', GREEN, 1.6);
  }

  _onZone(ev) {
    if (!ev || ev.player !== game.localPlayer) return;
    if (ev.zone === ZONE.OUTFIELD) this.toast('OUTFIELD · 外場 — hit an enemy to return 擊中敵人即可回場', '#9ad9ff', 3.2);
  }

  _onThrown(ev) {
    if (!ev || ev.isPass) return;
    const local = game.localPlayer;
    if (local ? ev.thrower !== local : false) return;
    const kmh = Math.round(ev.speedKmh || (ev.velocity ? ev.velocity.length() * 3.6 : 0));
    const e = this.el;
    setText(e.throwSpeed, String(kmh));
    const rally = ev.rallyCount | 0;
    setText(e.throwRally, rally > 0 ? `RALLY ×${rally} 連續回擊` : ev.isCounter ? 'COUNTER 反擊' : '');
    setClass(e.throwRally, 'is-on', rally > 0 || !!ev.isCounter);
    setClass(e.lastThrow, 'is-on', true);
    setClass(e.lastThrow, 'fresh', true);
    setClass(e.lastThrow, 'hot', kmh >= 150);
    replay(e.throwSpeed, 'pop');
    this._throwT = HUD_TUNING.lastThrowShow;
  }

  _onCaught(ev) {
    if (!ev) return;
    const local = game.localPlayer;
    const q = ev.quality;
    const secs = Number.isFinite(ev.secondsBeforeImpact) ? ev.secondsBeforeImpact : 0;
    if (ev.intercepted) {
      // Enemy pass caught: possession only (no revive / ult / counter boost).
      const c = ev.catcher;
      if (c && c === local) this._catchFeedback('normal', 'INTERCEPT', '攔截 · 搶到球權');
      else if (local && ev.thrower && ev.thrower.team === local.team) this.toast('PASS INTERCEPTED · 傳球被攔截', RED, 1.6);
      if (c) this._feed(`<i class="kf-int">✋</i><span class="kf-v" style="color:${teamCss(c.team)}">${esc(nameOf(c))}</span><small>INTERCEPTED · 攔截</small>`, teamCss(c.team), c === local);
      return;
    }
    if (ev.catcher && ev.catcher === local) {
      if (q === 'perfect') {
        this._catchFeedback('perfect', 'PERFECT CATCH!', `${secs.toFixed(2)} s · 完美接球`);
        this.banner('完美接球!\nPERFECT CATCH · +15% ULT · COUNTER +20%', GOLD, 1.5);
      } else if (q === 'normal') this._catchFeedback('normal', 'CATCH', `${secs.toFixed(2)} s · 接球`);
      else this._catchFeedback('miss', 'MISS', '落空');
    } else if (ev.thrower && ev.thrower === local && q !== 'miss') {
      this.toast(`${nameOf(ev.catcher)} CAUGHT IT · 被接住了`, RED, 1.4);
    }
    if (q === 'perfect' && ev.catcher) {
      this._feed(`<i class="kf-star">★</i><span class="kf-v" style="color:${teamCss(ev.catcher.team)}">${esc(nameOf(ev.catcher))}</span><small>PERFECT CATCH · 完美接球</small>`, GOLD, ev.catcher === local);
    }
  }

  _onHit(ev) {
    if (!ev) return;
    const local = game.localPlayer;
    const o = ev.outcome;
    const landed = o === 'damaged' || o === 'eliminated' || o === 'delayed';
    if (ev.attacker && ev.attacker === local && landed) {
      this._hitT = HUD_TUNING.hitMarkerTime;
      replay(this.el.crosshair, 'hitpulse');
    }
    if (ev.victim && ev.victim === local && o === 'delayed') {
      this.toast('DELAYED IMPACT · 延遲衝擊 — a teammate catch saves you 隊友接球即可取消', '#ffd36b', 2.4);
    }
  }

  _onAbilityCast(ev) {
    if (!ev || !ev.player) return;
    const local = game.localPlayer;
    const def = ev.ability && ev.ability.def;
    if (ev.player === local) {
      const s = this.el.slots[ev.slot];
      if (s) replay(s.root, 'cast');
    }
    if (ev.slot === 'ultimate' && def) {
      const p = ev.player;
      this._feed(`<span class="kf-a" style="color:${teamCss(p.team)}">${esc(nameOf(p))}</span><i class="kf-ult">✦</i><span class="kf-ab">${esc(String(def.name).toUpperCase())}</span><small>${esc(def.nameZh || '')}</small>`, teamCss(p.team), p === local);
    }
  }

  _onAbilityFailed(ev) {
    if (!ev || ev.player !== game.localPlayer) return;
    const s = this.el.slots[ev.slot];
    if (s) replay(s.root, 'fail');
    if (this._failT > 0) return;
    this._failT = HUD_TUNING.failToastCooldown;
    this.toast(failLabel(ev.reason), '#ff9a8a', 1.2);
  }
}
