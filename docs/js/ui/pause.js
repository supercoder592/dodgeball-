// ---------------------------------------------------------------------------------------------------------------
// Pause menu: Resume / Controls / Settings / Restart match / Hero select. Opening sets game.time.paused (gameplay
// freezes on the scaled clock) and releases pointer lock; closing resumes and re-locks the mouse. Keyboard, gamepad,
// mouse and touch navigation (menu actions arrive from the Input system via onMenuAction).
// ---------------------------------------------------------------------------------------------------------------
import { game } from '../game.js';
import { Settings, SETTINGS_SPEC } from '../input/settings.js';
import { restartMatch, backToHeroSelect, applyAudioSettings } from './flow.js';
import { formatClock } from './format.js';

/** Controls reference: [action EN, 中文, keyboard & mouse, gamepad, touch]. */
export const CONTROLS_TABLE = Object.freeze([
  ['Move', '移動', 'W A S D', 'Left stick', 'Left stick'],
  ['Look / aim', '視角 / 瞄準', 'Mouse', 'Right stick', 'Drag right side'],
  ['Sprint', '衝刺', 'Shift (hold)', 'L3 (toggle)', 'Push stick fully'],
  ['Jump', '跳躍', 'Space', 'A', 'JUMP'],
  ['Slide', '滑鏟', 'C / Ctrl', 'B', 'SLIDE'],
  ['Throw — hold to charge', '投球（按住蓄力）', 'Left mouse', 'RT', 'THROW'],
  ['Catch', '接球', 'Right mouse', 'LT', 'CATCH'],
  ['Pass', '傳球', 'Q', 'Y', 'PASS'],
  ['Pick up', '撿球', 'E', 'X', 'GRAB'],
  ['Skill', '技能', 'F', 'LB', 'SKILL'],
  ['Ultimate', '終極技', 'R', 'RB', 'ULT'],
  ['Cycle target', '切換目標', 'Tab / Middle mouse', 'R3 / D-pad →', 'TARGET'],
  ['Pause', '暫停', 'Esc / P', 'Start', '❚❚'],
]);

export const TIPS = Object.freeze([
  ['Perfect catch: press catch 0–0.15 s before impact — revives a teammate, +15% ultimate, counter throw +20% speed.',
    '完美接球：球命中前 0–0.15 秒按下接球——復活一名外場隊友、終極技 +15%、反擊球速 +20%。'],
  ['Rally boost: every catch-and-rethrow adds +10% speed (max 220 km/h) until the ball touches the floor.',
    '連續回擊：每次接球再投出球速 +10%（上限 220 km/h），球一落地即重置。'],
  ['Eliminated players throw from the outfield behind the enemy baseline — a hit brings them back in.',
    '出局球員移至敵方底線後的外場仍可投球，擊中敵人即可回到內場。'],
]);

/** Settings page rows. kind: range | toggle | cycle. */
const SETTING_ROWS = [
  { key: 'mouseSensitivity', kind: 'range', en: 'Mouse sensitivity', zh: '滑鼠靈敏度', fmt: (v) => `${v.toFixed(3)}°/px` },
  { key: 'invertY', kind: 'toggle', en: 'Invert vertical look', zh: '反轉垂直視角' },
  { key: 'gamepadLookSpeed', kind: 'range', en: 'Controller look speed', zh: '手把視角速度', fmt: (v) => `${Math.round(v)}°/s` },
  { key: 'touchSensitivity', kind: 'range', en: 'Touch look sensitivity', zh: '觸控視角靈敏度', fmt: (v) => `${v.toFixed(2)}°/px` },
  { key: 'touchControls', kind: 'cycle', en: 'Touch controls', zh: '觸控按鈕', labels: { auto: 'AUTO 自動', on: 'ON 開啟', off: 'OFF 關閉' } },
  { key: 'volume', kind: 'range', en: 'Master volume', zh: '主音量', fmt: (v) => `${Math.round(v * 100)}%` },
  { key: 'muted', kind: 'toggle', en: 'Mute', zh: '靜音' },
  { key: 'showMinimap', kind: 'toggle', en: 'Mini-map', zh: '小地圖' },
];
const QUALITIES = ['low', 'medium', 'high'];
const QUALITY_LABELS = { low: 'LOW 低', medium: 'MEDIUM 中', high: 'HIGH 高' };

class PauseMenu {
  constructor() {
    this.root = null;
    this.isOpen = false;
    this.page = 'main';
    this._focus = 0;
    this._openedAt = 0;
    this._pages = {};
  }

  /** Opens the menu (only while a match is loaded). */
  open(source = 'keyboard') {
    if (this.isOpen) return;
    const ph = game.match && game.match.phase;
    if (!ph || ph === 'idle' || ph === 'select') return;
    this._build();
    this.isOpen = true;
    this._openedAt = performance.now();
    this.source = source;
    game.time.paused = true;
    this._refreshMeta();
    this._refreshSettings();
    this._showPage('main');
    this.root.classList.add('open');
    this.root.setAttribute('aria-hidden', 'false');
    if (game.input) { game.input.releaseLock(); game.input.pushMenu(this); }
  }

  /** Closes and resumes. relock=false when leaving to another screen. */
  close(relock = true) {
    if (!this.isOpen) return;
    this.isOpen = false;
    this.root.classList.remove('open');
    this.root.setAttribute('aria-hidden', 'true');
    game.time.paused = false;
    const input = game.input;
    if (input) {
      input.popMenu(this);
      if (relock && !input.touchMode && input.lastDevice === 'kbm' && game.localPlayer) input.requestLock();
    }
  }

  toggle(source) { if (this.isOpen) this.close(); else this.open(source); }

  // ------------------------------------------------------------------ menu actions (from Input)
  onMenuAction(action) {
    switch (action) {
      case 'up': this._moveFocus(-1); return true;
      case 'down': this._moveFocus(1); return true;
      case 'left': this._adjust(-1); return true;
      case 'right': this._adjust(1); return true;
      case 'confirm': this._activate(this._items()[this._focus]); return true;
      case 'back':
        if (performance.now() - this._openedAt < 320) return true; // the Esc that unlocked the mouse
        if (this.page !== 'main') this._showPage('main'); else this.close();
        return true;
      case 'start': this.close(); return true;
      default: return false;
    }
  }

  // ------------------------------------------------------------------ DOM
  _build() {
    if (this.root) return;
    const ui = document.getElementById('ui') || document.body;
    const root = document.createElement('div');
    root.id = 'pause';
    root.className = 'du-menu pz';
    root.setAttribute('role', 'dialog');
    root.setAttribute('aria-modal', 'true');
    root.setAttribute('aria-hidden', 'true');
    root.setAttribute('aria-label', 'Paused');
    const btn = (cmd, en, zh, cls = '') => `<button type="button" tabindex="-1" class="du-nav-item pz-btn ${cls}" data-cmd="${cmd}"><span>${en}</span><small>${zh}</small></button>`;
    const controlsRows = CONTROLS_TABLE.map((r) => `<tr><th>${r[0]}<small>${r[1]}</small></th><td><kbd>${r[2]}</kbd></td><td><kbd>${r[3]}</kbd></td><td><kbd>${r[4]}</kbd></td></tr>`).join('');
    const tips = TIPS.map((t) => `<li>${t[0]}<small>${t[1]}</small></li>`).join('');
    const settingRows = SETTING_ROWS.map((r) => {
      const spec = SETTINGS_SPEC[r.key];
      let control = '';
      if (r.kind === 'range') {
        const step = ((spec.max - spec.min) / 40).toPrecision(3);
        control = `<input type="range" class="du-nav-item pz-range" data-setting="${r.key}" min="${spec.min}" max="${spec.max}" step="${step}"><output data-out="${r.key}"></output>`;
      } else {
        control = `<button type="button" tabindex="-1" class="du-nav-item pz-toggle" data-setting="${r.key}" data-kind="${r.kind}"></button>`;
      }
      return `<div class="pz-row"><label>${r.en}<small>${r.zh}</small></label><div class="pz-ctl">${control}</div></div>`;
    }).join('');
    root.innerHTML = `
      <div class="pz-backdrop"></div>
      <div class="pz-panel">
        <header class="pz-head">
          <div class="pz-title">PAUSED <span>暫停</span></div>
          <div class="pz-meta"></div>
        </header>
        <section class="pz-page" data-page="main">
          ${btn('resume', 'Resume', '繼續比賽', 'pz-primary')}
          ${btn('controls', 'Controls', '操作說明')}
          ${btn('settings', 'Settings', '設定')}
          ${btn('restart', 'Restart match', '重新開始')}
          ${btn('heroes', 'Hero select', '返回選角')}
        </section>
        <section class="pz-page" data-page="controls">
          <table class="pz-controls">
            <thead><tr><th></th><th>Keyboard &amp; mouse<small>鍵盤滑鼠</small></th><th>Controller<small>手把</small></th><th>Touch<small>觸控</small></th></tr></thead>
            <tbody>${controlsRows}</tbody>
          </table>
          <ul class="pz-tips">${tips}</ul>
          ${btn('back', 'Back', '返回', 'pz-back')}
        </section>
        <section class="pz-page" data-page="settings">
          ${settingRows}
          <div class="pz-row"><label>Graphics quality<small>畫質（將重新載入）</small></label><div class="pz-ctl"><button type="button" tabindex="-1" class="du-nav-item pz-toggle" data-quality="1"></button></div></div>
          ${btn('back', 'Back', '返回', 'pz-back')}
        </section>
      </div>`;
    ui.appendChild(root);
    this.root = root;
    this.meta = root.querySelector('.pz-meta');
    for (const sec of root.querySelectorAll('.pz-page')) this._pages[sec.dataset.page] = sec;

    // Buttons: no native focus on click (keyboard activation is driven by onMenuAction only).
    root.addEventListener('mousedown', (e) => { const b = e.target.closest('button'); if (b) e.preventDefault(); });
    root.addEventListener('click', (e) => {
      const item = e.target.closest('.du-nav-item');
      if (!item || item.tagName === 'INPUT') return;
      this._syncFocus(item);
      this._activate(item);
    });
    root.addEventListener('pointerover', (e) => {
      if (e.pointerType !== 'mouse') return;
      const item = e.target.closest('.du-nav-item');
      if (item) this._syncFocus(item);
    });
    root.addEventListener('input', (e) => {
      const el = e.target;
      if (el && el.dataset && el.dataset.setting) { Settings.set(el.dataset.setting, Number(el.value)); this._afterSetting(el.dataset.setting); }
    });
    root.addEventListener('pointerdown', (e) => { const item = e.target.closest('.du-nav-item'); if (item) this._syncFocus(item); });
  }

  _showPage(page) {
    this.page = page;
    for (const k in this._pages) this._pages[k].classList.toggle('active', k === page);
    this._focus = 0;
    this._applyFocus();
  }

  _items() { const p = this._pages[this.page]; return p ? p.querySelectorAll('.du-nav-item') : []; }

  _moveFocus(d) {
    const items = this._items();
    if (!items.length) return;
    this._focus = (this._focus + d + items.length) % items.length;
    this._applyFocus();
  }

  _syncFocus(el) {
    const items = this._items();
    for (let i = 0; i < items.length; i++) if (items[i] === el) { this._focus = i; break; }
    this._applyFocus();
  }

  _applyFocus() {
    const items = this._items();
    for (let i = 0; i < items.length; i++) items[i].classList.toggle('focus', i === this._focus);
    const el = items[this._focus];
    if (el && el.scrollIntoView && this.page !== 'main') el.scrollIntoView({ block: 'nearest' });
  }

  _adjust(dir) {
    const el = this._items()[this._focus];
    if (!el) return;
    if (el.tagName === 'INPUT') {
      const key = el.dataset.setting, spec = SETTINGS_SPEC[key];
      const step = (spec.max - spec.min) / 20;
      Settings.set(key, Settings.get(key) + dir * step);
      this._afterSetting(key);
    } else if (el.dataset.setting || el.dataset.quality) {
      this._activate(el, dir);
    }
  }

  _activate(el, dir = 1) {
    if (!el) return;
    const cmd = el.dataset.cmd;
    if (cmd) { this._command(cmd); return; }
    if (el.dataset.quality) { this._cycleQuality(dir); return; }
    const key = el.dataset.setting;
    if (!key) return;
    const spec = SETTINGS_SPEC[key];
    if (el.dataset.kind === 'toggle') Settings.set(key, !Settings.get(key));
    else if (el.dataset.kind === 'cycle' && spec.values) {
      const i = spec.values.indexOf(Settings.get(key));
      Settings.set(key, spec.values[(i + dir + spec.values.length) % spec.values.length]);
    }
    this._afterSetting(key);
  }

  _command(cmd) {
    switch (cmd) {
      case 'resume': this.close(true); break;
      case 'controls': this._showPage('controls'); break;
      case 'settings': this._refreshSettings(); this._showPage('settings'); break;
      case 'back': this._showPage('main'); break;
      case 'restart': this.close(false); restartMatch(); break;
      case 'heroes': this.close(false); backToHeroSelect(); break;
      default: break;
    }
  }

  _afterSetting(key) {
    if (key === 'volume' || key === 'muted') applyAudioSettings();
    this._refreshSettings();
  }

  _cycleQuality(dir) {
    const cur = (game.config && game.config.quality) || 'high';
    const next = QUALITIES[(QUALITIES.indexOf(cur) + dir + QUALITIES.length) % QUALITIES.length];
    try {
      const url = new URL(location.href);
      url.searchParams.set('quality', next);
      location.assign(url.toString());
    } catch (e) { console.warn('[pause] quality change failed', e); }
  }

  _refreshSettings() {
    if (!this.root) return;
    for (const row of SETTING_ROWS) {
      const v = Settings.get(row.key);
      if (row.kind === 'range') {
        const input = this.root.querySelector(`input[data-setting="${row.key}"]`);
        const out = this.root.querySelector(`output[data-out="${row.key}"]`);
        if (input && document.activeElement !== input) input.value = String(v);
        if (out) out.textContent = row.fmt(v);
      } else {
        const b = this.root.querySelector(`button[data-setting="${row.key}"]`);
        if (!b) continue;
        b.textContent = row.kind === 'toggle' ? (v ? 'ON 開' : 'OFF 關') : (row.labels[v] || String(v).toUpperCase());
        b.classList.toggle('on', row.kind === 'toggle' ? !!v : v !== 'off');
      }
    }
    const q = this.root.querySelector('button[data-quality]');
    if (q) q.textContent = QUALITY_LABELS[(game.config && game.config.quality) || 'high'] || 'HIGH 高';
  }

  _refreshMeta() {
    const m = game.match;
    if (!m || !this.meta) return;
    const s = m.scores || [0, 0];
    const local = game.localPlayer;
    const who = local && local.hero ? ` · ${String(local.hero.id).toUpperCase()}` : ' · SPECTATING 觀戰';
    this.meta.textContent = `ROUND ${m.round || 1} 第${m.round || 1}回合 · HOME ${s[0]} – ${s[1]} AWAY · ${formatClock(m.timeLeft || 0)}${who}`;
  }
}

/** Singleton pause menu. */
export const Pause = new PauseMenu();
