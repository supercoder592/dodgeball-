// ---------------------------------------------------------------------------------------------------------------
// Hero select: full-screen broadcast-style draft screen.
//   * 10 hero cards (realistic Rocketbox portrait, name, 中文 title, role) in a 5 x 2 grid
//   * details: bio (EN + 中文), HP, passive / skill / ultimate with bilingual names, descriptions, cooldowns
//   * team (HOME / AWAY), difficulty (Easy / Normal / Hard / Pro), PLAY / 開始 and Watch AI match / 觀戰
//   * keyboard (arrows/WASD, Enter, T, 1-4, Q/E, V), gamepad (d-pad/stick, A, X, LB/RB, Y), mouse and touch
// HeroSelect.show(roster, onConfirm) calls onConfirm({ hero, team, difficulty, spectate }) (hero = null when spectating).
// ---------------------------------------------------------------------------------------------------------------
import { game } from '../game.js';
import { TEAM, TEAM_COLORS } from '../core/constants.js';
import { DIFFICULTIES, TEAM_LABELS, cssColor } from './format.js';
import { loadJson, saveJson } from '../input/settings.js';
import { Pause } from './pause.js';
import { rememberDifficulty, unlockAudio } from './flow.js';

const STORAGE_KEY = 'dodgeballUltra.heroSelect.v1';
const GRID_COLUMNS = 5;
const SLOT_LABELS = {
  passive: { en: 'PASSIVE', zh: '被動' },
  skill: { en: 'SKILL', zh: '技能' },
  ultimate: { en: 'ULTIMATE', zh: '終極技' },
};

const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

/** Portrait URL from the asset manifest (falls back to the conventional folder). */
export function portraitUrl(heroId) {
  const m = game.assets && game.assets.manifest;
  const def = m && m.heroes && m.heroes[heroId];
  const base = (game.assets && game.assets.base) || 'assets/';
  if (def && def.folder) return `${base}${def.folder}${def.portrait || 'portrait.webp'}`;
  return `${base}heroes/${String(heroId).toLowerCase()}/portrait.webp`;
}

class HeroSelectScreen {
  constructor() {
    this.root = null;
    this.visible = false;
    this.roster = [];
    this.onConfirm = null;
    this.index = 0;
    const saved = loadJson(STORAGE_KEY, { hero: 'Rayne', team: TEAM.HOME, difficulty: 'normal' });
    this.team = saved.team === TEAM.AWAY ? TEAM.AWAY : TEAM.HOME;
    this.difficulty = DIFFICULTIES.some((d) => d.id === saved.difficulty) ? saved.difficulty : 'normal';
    this._savedHero = saved.hero;
    this._confirmed = false;
    this._device = '';
    this._shownOnce = false;
    this._cardsSig = '';
    this.cards = [];
  }

  /** @param {object[]} roster HEROES @param {(choice:{hero:string,team:number,difficulty:string,spectate:boolean})=>void} onConfirm */
  show(roster, onConfirm) {
    this.roster = Array.isArray(roster) && roster.length ? roster : (game.roster || []);
    this.onConfirm = onConfirm;
    this._build();
    // First visit: restore the saved pick; afterwards keep the last selection.
    const wanted = this._shownOnce && this.roster[this.index] ? this.roster[this.index].id : this._savedHero;
    const savedIdx = this.roster.findIndex((h) => h.id === wanted);
    this.index = savedIdx >= 0 ? savedIdx : 0;
    this._shownOnce = true;
    this._confirmed = false;
    if (Pause.isOpen) Pause.close(false);
    game.time.paused = false;
    if (game.hud && typeof game.hud.show === 'function') game.hud.show(false);
    this.visible = true;
    this.root.classList.add('open');
    this.root.setAttribute('aria-hidden', 'false');
    this.root.classList.remove('hs-leaving');
    this._render();
    if (game.input) { game.input.releaseLock(); game.input.pushMenu(this); }
  }

  hide() {
    if (!this.root || !this.visible) return;
    this.visible = false;
    this.root.classList.remove('open');
    this.root.setAttribute('aria-hidden', 'true');
    if (game.input) game.input.popMenu(this);
    if (game.hud && typeof game.hud.show === 'function') game.hud.show(true);
  }

  // ------------------------------------------------------------------ input
  onMenuAction(action, source) {
    if (!this.visible) return false;
    this._setDevice(source === 'gamepad' ? 'gamepad' : 'kbm');
    const n = this.roster.length;
    switch (action) {
      case 'left': this._select((this.index - 1 + n) % n); return true;
      case 'right': this._select((this.index + 1) % n); return true;
      case 'up': this._select((this.index - GRID_COLUMNS + n) % n); return true;
      case 'down': this._select((this.index + GRID_COLUMNS) % n); return true;
      case 'confirm': case 'start': this._confirm(false); return true;
      case 'watch': this._confirm(true); return true;
      case 'team': case 'select': this._setTeam(this.team === TEAM.HOME ? TEAM.AWAY : TEAM.HOME); return true;
      case 'prev': this._stepDifficulty(-1); return true;
      case 'next': this._stepDifficulty(1); return true;
      case 'diff1': case 'diff2': case 'diff3': case 'diff4':
        this._setDifficulty(DIFFICULTIES[Number(action.slice(4)) - 1].id); return true;
      case 'back': return true;
      default: return false;
    }
  }

  // ------------------------------------------------------------------ state
  _select(i) {
    if (i === this.index || i < 0 || i >= this.roster.length) return;
    this.index = i;
    this._render();
  }
  _setTeam(t) { this.team = t; this._render(); this._save(); }
  _setDifficulty(d) { this.difficulty = d; this._render(); this._save(); }
  _stepDifficulty(dir) {
    const i = DIFFICULTIES.findIndex((d) => d.id === this.difficulty);
    this._setDifficulty(DIFFICULTIES[Math.min(DIFFICULTIES.length - 1, Math.max(0, i + dir))].id);
  }
  _save() {
    const h = this.roster[this.index];
    saveJson(STORAGE_KEY, { hero: h ? h.id : 'Rayne', team: this.team, difficulty: this.difficulty });
  }

  _confirm(spectate) {
    if (this._confirmed || !this.visible) return;
    const hero = this.roster[this.index];
    if (!hero) return;
    this._confirmed = true;
    this._save();
    rememberDifficulty(this.difficulty);
    unlockAudio();
    // Called inside the user gesture: grab the mouse right away for desktop players.
    const input = game.input;
    if (!spectate && input && !input.touchMode && input.lastDevice === 'kbm') input.requestLock();
    this.root.classList.add('hs-leaving');
    // Spectating: no local hero. Match derives spectate mode from a missing localHero, so this also works through a
    // boot callback that forwards only { hero, team, difficulty }.
    const choice = { hero: spectate ? null : hero.id, team: this.team, difficulty: this.difficulty, spectate: !!spectate };
    try {
      if (typeof this.onConfirm === 'function') {
        const r = this.onConfirm(choice);
        if (r && typeof r.catch === 'function') r.catch((e) => { console.error('[heroSelect] starting the match failed', e); });
      }
    } catch (e) {
      console.error('[heroSelect] onConfirm threw', e);
      this._confirmed = false;
    }
  }

  _setDevice(d) {
    if (d === this._device || !this.root) return;
    this._device = d;
    this.root.dataset.device = d;
  }

  // ------------------------------------------------------------------ DOM
  _build() {
    if (!this.root) {
      const ui = document.getElementById('ui') || document.body;
      const root = document.createElement('div');
      root.id = 'hero-select';
      root.className = 'du-menu hs';
      root.setAttribute('aria-hidden', 'true');
      root.innerHTML = `
        <div class="hs-bg" aria-hidden="true"></div>
        <header class="hs-top">
          <div class="hs-brand"><b>DODGEBALL</b><span>ULTRA</span></div>
          <div class="hs-heading">SELECT YOUR HERO <small>選擇英雄</small></div>
          <div class="hs-mode">3 v 3 · BEST OF 3 <small>三戰兩勝</small></div>
        </header>
        <main class="hs-main">
          <div class="hs-grid" role="listbox" aria-label="Heroes"></div>
          <aside class="hs-detail" aria-live="polite">
            <div class="hs-hero-head">
              <div class="hs-portrait-lg"><img alt="" decoding="async"></div>
              <div class="hs-hero-id">
                <div class="hs-role"></div>
                <h1 class="hs-name"></h1>
                <div class="hs-title"></div>
                <div class="hs-stats"></div>
              </div>
            </div>
            <p class="hs-bio"></p>
            <ul class="hs-abilities"></ul>
          </aside>
        </main>
        <footer class="hs-bottom">
          <div class="hs-group">
            <div class="hs-label">TEAM <small>隊伍</small></div>
            <div class="hs-seg hs-team">
              <button type="button" tabindex="-1" data-team="${TEAM.HOME}" class="hs-home">${TEAM_LABELS[0].en}<small>${TEAM_LABELS[0].zh}</small></button>
              <button type="button" tabindex="-1" data-team="${TEAM.AWAY}" class="hs-away">${TEAM_LABELS[1].en}<small>${TEAM_LABELS[1].zh}</small></button>
            </div>
          </div>
          <div class="hs-group">
            <div class="hs-label">DIFFICULTY <small>難度</small></div>
            <div class="hs-seg hs-diff">
              ${DIFFICULTIES.map((d) => `<button type="button" tabindex="-1" data-diff="${d.id}">${d.en}<small>${d.zh}</small></button>`).join('')}
            </div>
          </div>
          <div class="hs-actions">
            <button type="button" tabindex="-1" class="hs-watch">WATCH AI MATCH<small>觀戰</small><kbd class="hk-kbm">V</kbd><kbd class="hk-pad">Y</kbd></button>
            <button type="button" tabindex="-1" class="hs-play">PLAY<small>開始</small><kbd class="hk-kbm">Enter</kbd><kbd class="hk-pad">A</kbd></button>
          </div>
        </footer>
        <div class="hs-hints">
          <span class="hk-kbm">←↑↓→ Hero 英雄 · T Team 隊伍 · 1–4 Difficulty 難度 · V Watch 觀戰 · Enter Play 開始</span>
          <span class="hk-pad">D-pad Hero 英雄 · X Team 隊伍 · LB/RB Difficulty 難度 · Y Watch 觀戰 · A Play 開始</span>
        </div>`;
      ui.appendChild(root);
      this.root = root;
      this.grid = root.querySelector('.hs-grid');
      // Missing portrait: hide the broken image (error events do not bubble - capture them).
      this.grid.addEventListener('error', (e) => { if (e.target && e.target.tagName === 'IMG') e.target.style.visibility = 'hidden'; }, true);
      this.detail = {
        img: root.querySelector('.hs-portrait-lg img'),
        role: root.querySelector('.hs-role'),
        name: root.querySelector('.hs-name'),
        title: root.querySelector('.hs-title'),
        stats: root.querySelector('.hs-stats'),
        bio: root.querySelector('.hs-bio'),
        abilities: root.querySelector('.hs-abilities'),
      };
      this.detail.img.addEventListener('error', () => { this.detail.img.style.visibility = 'hidden'; });
      this.detail.img.addEventListener('load', () => { this.detail.img.style.visibility = ''; });

      root.addEventListener('mousedown', (e) => { if (e.target.closest('button')) e.preventDefault(); });
      root.addEventListener('pointerdown', (e) => this._setDevice(e.pointerType === 'touch' ? 'touch' : 'kbm'));
      root.addEventListener('click', (e) => {
        const card = e.target.closest('.hs-card');
        if (card) { this._select(Number(card.dataset.i)); return; }
        const t = e.target.closest('[data-team]');
        if (t) { this._setTeam(Number(t.dataset.team)); return; }
        const d = e.target.closest('[data-diff]');
        if (d) { this._setDifficulty(d.dataset.diff); return; }
        if (e.target.closest('.hs-play')) { this._confirm(false); return; }
        if (e.target.closest('.hs-watch')) this._confirm(true);
      });
      root.addEventListener('dblclick', (e) => { if (e.target.closest('.hs-card')) this._confirm(false); });
      this._setDevice(game.input && game.input.lastDevice === 'gamepad' ? 'gamepad' : game.input && game.input.touchMode ? 'touch' : 'kbm');
    }
    // (Re)build cards when the roster changes.
    const sig = this.roster.map((h) => h.id).join(',');
    if (this._cardsSig !== sig) {
      this._cardsSig = sig;
      this.grid.innerHTML = this.roster.map((h, i) => `
        <div class="hs-card" role="option" data-i="${i}" style="--hc:${cssColor(h.color)}">
          <img src="${esc(portraitUrl(h.id))}" alt="" loading="eager" decoding="async">
          <div class="hs-card-shade"></div>
          <div class="hs-card-info"><b>${esc(String(h.id).toUpperCase())}</b><span>${esc(h.titleZh || '')}</span><em>${esc(h.role || '')}</em></div>
          ${h.maxHp > 100 ? `<i class="hs-card-hp">${h.maxHp} HP</i>` : ''}
        </div>`).join('');
      this.cards = Array.from(this.grid.querySelectorAll('.hs-card'));
    }
  }

  _render() {
    if (!this.root) return;
    const hero = this.roster[this.index];
    this.cards.forEach((c, i) => { c.classList.toggle('sel', i === this.index); c.setAttribute('aria-selected', i === this.index ? 'true' : 'false'); });
    this.root.style.setProperty('--team', cssColor(TEAM_COLORS[this.team]));
    this.root.dataset.team = this.team === TEAM.AWAY ? 'away' : 'home';
    for (const b of this.root.querySelectorAll('[data-team]')) b.classList.toggle('on', Number(b.dataset.team) === this.team);
    for (const b of this.root.querySelectorAll('[data-diff]')) b.classList.toggle('on', b.dataset.diff === this.difficulty);
    if (!hero) return;
    const d = this.detail;
    this.root.style.setProperty('--hc', cssColor(hero.color));
    const url = portraitUrl(hero.id);
    if (d.img.getAttribute('src') !== url) d.img.setAttribute('src', url);
    d.role.textContent = `${String(hero.role || '').toUpperCase()} · ${hero.roleZh || ''}`;
    d.name.textContent = String(hero.id).toUpperCase();
    d.title.innerHTML = `${esc(hero.title || '')} <span>${esc(hero.titleZh || '')}</span>`;
    const hpNote = hero.maxHp > 100 ? ' <em>THICK HIDE 厚皮</em>' : '';
    d.stats.innerHTML = `<span><b>${hero.maxHp || 100}</b> HP${hpNote}</span><span><b>${hero.height ? hero.height.toFixed(2) : '1.80'}</b> m</span>`;
    d.bio.innerHTML = `${esc(hero.bio || '')}<small>${esc(hero.bioZh || '')}</small>`;
    const ab = hero.abilities || {};
    d.abilities.innerHTML = ['passive', 'skill', 'ultimate'].map((slot) => {
      const a = ab[slot];
      if (!a) return '';
      const L = SLOT_LABELS[slot];
      const key = slot === 'skill' ? '<kbd class="hk-kbm">F</kbd><kbd class="hk-pad">LB</kbd>' : slot === 'ultimate' ? '<kbd class="hk-kbm">R</kbd><kbd class="hk-pad">RB</kbd>' : '';
      const meta = [];
      if (a.cooldown) meta.push(`CD ${a.cooldown} s`);
      if (a.duration) meta.push(`${a.duration} s`);
      if (slot === 'ultimate') meta.push('ULT 100%');
      return `<li class="hs-ab hs-ab-${slot}">
          <div class="hs-ab-slot">${L.en}<small>${L.zh}</small>${key}</div>
          <div class="hs-ab-body">
            <div class="hs-ab-name">${esc(a.name)} <span>${esc(a.nameZh || '')}</span>${meta.length ? `<i>${meta.join(' · ')}</i>` : ''}</div>
            <p>${esc(a.desc || '')}<small>${esc(a.descZh || '')}</small></p>
          </div>
        </li>`;
    }).join('');
    const sel = this.cards[this.index];
    if (sel && sel.scrollIntoView && this.grid.scrollHeight > this.grid.clientHeight + 4) sel.scrollIntoView({ block: 'nearest' });
  }
}

/** Singleton hero select screen (main.js: HeroSelect.show(HEROES, onConfirm) / HeroSelect.hide()). */
export const HeroSelect = new HeroSelectScreen();
