// ---------------------------------------------------------------------------------------------------------------
// Arena video board (owner: render). A canvas texture shown on the end-wall jumbotrons: live score, round, clock,
// infield pips, last throw speed and short event call-outs ("PERFECT CATCH", "OUT!"). Reads game.match defensively
// and listens to game events; redraws only when something visible changed (clock ticks at most every 0.25 s).
// ---------------------------------------------------------------------------------------------------------------
import * as THREE from 'three';
import { game } from '../game.js';
import { EV } from '../core/events.js';
import { TEAM, TEAM_COLORS } from '../core/constants.js';
import { makeCanvas, canvasTexture, drawWordmark, SPORT_FONT } from './textures.js';

export const SCOREBOARD = Object.freeze({
  width: 1024,
  height: 512,
  checkInterval: 0.25,   // real seconds between state polls
  calloutTime: 2.6,      // seconds a call-out stays up
  ledPitch: 4,           // LED grid overlay pitch in px
});

const hex = (c) => '#' + (c >>> 0).toString(16).padStart(6, '0');

export class Scoreboard {
  constructor() {
    this.canvas = makeCanvas(SCOREBOARD.width, SCOREBOARD.height);
    this.ctx = this.canvas.getContext('2d');
    this.texture = canvasTexture(this.canvas, { srgb: true, repeat: false, anisotropy: 4, name: 'scoreboard' });
    this.texture.generateMipmaps = false;         // redrawn at runtime: skip mip regeneration
    this.texture.minFilter = THREE.LinearFilter;
    this._grid = this._makeGrid();
    this._sig = '';
    this._nextCheck = 0;
    this._lastKmh = 0;
    this._callout = null;       // { text, color, until }
    this._unsubs = [];
    this._subscribe();
    this.redraw(true);
  }

  /** Called from a mesh onBeforeRender hook with real time; cheap when nothing changed. */
  tick(realNow) {
    if (realNow < this._nextCheck) return;
    this._nextCheck = realNow + SCOREBOARD.checkInterval;
    if (this._callout && realNow > this._callout.until) this._callout = null;
    this.redraw(false, realNow);
  }

  /** Current match snapshot (all fields optional-safe). */
  _state() {
    const m = game.match;
    const scores = (m && m.scores) || [0, 0];
    const count = (team) => {
      try { return m && typeof m.countInfield === 'function' ? m.countInfield(team) : null; } catch (e) { return null; }
    };
    return {
      phase: (m && m.phase) || 'idle',
      round: (m && m.round) || 0,
      home: scores[0] | 0, away: scores[1] | 0,
      time: m && Number.isFinite(m.timeLeft) ? Math.max(0, Math.ceil(m.timeLeft)) : null,
      homeIn: count(TEAM.HOME), awayIn: count(TEAM.AWAY),
      perTeam: (m && m.rules && m.rules.playersPerTeam) || 3,
    };
  }

  /** Redraws when the visible signature changed (or `force`). */
  redraw(force = false, realNow = 0) {
    const s = this._state();
    const c = this._callout;
    const sig = `${s.phase}|${s.round}|${s.home}|${s.away}|${s.time}|${s.homeIn}|${s.awayIn}|${this._lastKmh}|${c ? c.text : ''}`;
    if (!force && sig === this._sig) return;
    this._sig = sig;
    this._draw(s, c);
    this.texture.needsUpdate = true;
  }

  _draw(s, callout) {
    const x = this.ctx, W = this.canvas.width, H = this.canvas.height;
    const home = TEAM_COLORS[TEAM.HOME], away = TEAM_COLORS[TEAM.AWAY];
    const g = x.createLinearGradient(0, 0, 0, H);
    g.addColorStop(0, '#0a1020'); g.addColorStop(1, '#04060c');
    x.fillStyle = g; x.fillRect(0, 0, W, H);
    x.textAlign = 'center'; x.textBaseline = 'middle';

    const live = s.phase !== 'idle' && s.phase !== 'select';
    if (!live) {
      drawWordmark(x, W / 2, H * 0.42, W * 0.8, { stacked: true, shadow: true });
      x.font = `700 ${H * 0.07}px ${SPORT_FONT}`;
      x.fillStyle = 'rgba(220,225,235,0.8)';
      x.fillText('3v3 SUPER LEAGUE  ·  BEST OF 3', W / 2, H * 0.86);
    } else {
      // team panels
      x.fillStyle = hex(home); x.fillRect(24, 24, W * 0.36, 84);
      x.fillStyle = hex(away); x.fillRect(W - 24 - W * 0.36, 24, W * 0.36, 84);
      x.font = `italic 900 60px ${SPORT_FONT}`;
      x.fillStyle = '#ffffff';
      x.fillText('HOME', 24 + W * 0.18, 68);
      x.fillText('AWAY', W - 24 - W * 0.18, 68);
      // scores
      x.font = `900 ${H * 0.42}px ${SPORT_FONT}`;
      x.fillStyle = '#ffffff';
      x.fillText(String(s.home), 24 + W * 0.18, H * 0.52);
      x.fillText(String(s.away), W - 24 - W * 0.18, H * 0.52);
      // centre: round + clock
      x.font = `700 ${H * 0.075}px ${SPORT_FONT}`;
      x.fillStyle = 'rgba(210,220,235,0.85)';
      x.fillText(s.round > 0 ? `ROUND ${s.round}` : 'GET READY', W / 2, 60);
      if (s.time !== null) {
        const mm = Math.floor(s.time / 60), ss = String(s.time % 60).padStart(2, '0');
        x.font = `900 ${H * 0.2}px ${SPORT_FONT}`;
        x.fillStyle = s.time <= 10 && s.phase === 'playing' ? '#ff4a3a' : '#ffd24a';
        x.fillText(`${mm}:${ss}`, W / 2, H * 0.36);
      }
      // infield pips
      const pips = (cx, n, col) => {
        if (n === null) return;
        for (let i = 0; i < s.perTeam; i++) {
          x.beginPath();
          x.arc(cx + (i - (s.perTeam - 1) / 2) * 44, H * 0.83, 15, 0, Math.PI * 2);
          x.fillStyle = i < n ? hex(col) : 'rgba(255,255,255,0.12)';
          x.fill();
        }
      };
      pips(24 + W * 0.18, s.homeIn, home);
      pips(W - 24 - W * 0.18, s.awayIn, away);
      // throw speed
      x.font = `700 ${H * 0.06}px ${SPORT_FONT}`;
      x.fillStyle = 'rgba(200,210,225,0.8)';
      x.fillText('THROW SPEED', W / 2, H * 0.6);
      x.font = `900 ${H * 0.13}px ${SPORT_FONT}`;
      x.fillStyle = '#ffffff';
      x.fillText(this._lastKmh > 0 ? `${this._lastKmh} km/h` : '--', W / 2, H * 0.73);
    }
    if (callout) {
      x.fillStyle = 'rgba(0,0,0,0.72)';
      x.fillRect(0, H * 0.3, W, H * 0.4);
      x.font = `italic 900 ${H * 0.2}px ${SPORT_FONT}`;
      x.fillStyle = callout.color;
      x.fillText(callout.text, W / 2, H * 0.5);
    }
    // LED pixel grid
    x.drawImage(this._grid, 0, 0);
  }

  _makeGrid() {
    const W = this.canvas.width, H = this.canvas.height, p = SCOREBOARD.ledPitch;
    const c = makeCanvas(W, H), x = c.getContext('2d');
    x.fillStyle = 'rgba(0,0,0,0.38)';
    for (let i = 0; i < W; i += p) x.fillRect(i, 0, 1, H);
    for (let j = 0; j < H; j += p) x.fillRect(0, j, W, 1);
    return c;
  }

  _say(text, color) {
    this._callout = { text, color, until: (game.time ? game.time.realNow : 0) + SCOREBOARD.calloutTime };
    this._nextCheck = 0;
  }

  _subscribe() {
    const ev = game.events;
    if (!ev) return;
    const on = (name, fn) => this._unsubs.push(ev.on(name, fn));
    on(EV.BallThrown, (e) => {
      if (!e || e.isPass) return;
      const k = Math.round(e.speedKmh || 0);
      if (k > 0) { this._lastKmh = k; this._nextCheck = 0; }
    });
    on(EV.BallCaught, (e) => { if (e && e.quality === 'perfect') this._say('PERFECT CATCH!', '#ffd24a'); });
    on(EV.PlayerEliminated, (e) => {
      const p = e && e.player;
      const name = p && ((p.hero && p.hero.id) || p.name);
      this._say(name ? `${String(name).toUpperCase()} OUT!` : 'OUT!', '#ff5a3a');
    });
    on(EV.RoundEnded, (e) => {
      const w = e && e.winner;
      this._say(w === TEAM.HOME ? 'HOME WINS ROUND' : w === TEAM.AWAY ? 'AWAY WINS ROUND' : 'DRAW', w === TEAM.AWAY ? hex(TEAM_COLORS[TEAM.AWAY]) : hex(TEAM_COLORS[TEAM.HOME]));
    });
    on(EV.MatchEnded, (e) => {
      const w = e && e.winner;
      this._say(w === TEAM.HOME ? 'HOME WINS!' : w === TEAM.AWAY ? 'AWAY WINS!' : 'MATCH DRAWN', '#ffffff');
    });
    on(EV.MatchStarted, () => { this._lastKmh = 0; this._nextCheck = 0; });
  }

  dispose() {
    for (const off of this._unsubs) off();
    this._unsubs.length = 0;
    this.texture.dispose();
  }
}
