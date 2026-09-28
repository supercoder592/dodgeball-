// ---------------------------------------------------------------------------------------------------------------
// HUD mini-map (2D canvas): the 18 x 9 m court with both teams' U outfields (each U = two side arms + the back strip
// around the OPPONENT's half), the players and the single match ball, oriented so the viewer's team attacks "up".
// Markers: filled dot = infield, hollow ring = eliminated outfielder, hollow diamond = starting outfielder (元外野),
// gold ring = ball holder, pulsing ring = where an awarded / ball-boy ball will reappear. Enemies that are cloaked or
// have Silent Footsteps (Gale's passive) are hidden unless revealed (and so is the ball in their hands).
// The static court is cached in an offscreen canvas; the dynamic layer is redrawn by the HUD at ~20 Hz.
// ---------------------------------------------------------------------------------------------------------------
import { game } from '../game.js';
import { TEAM, TEAM_COLORS, COURT, ZONE } from '../core/constants.js';
import { cssColor } from './format.js';

export const MINIMAP = Object.freeze({
  padding: 7,            // px around the drawing
  playerRadius: 4.3,     // px
  ballRadius: 3.2,       // px (the only ball: emphasised, with a white halo)
  holderRing: '#ffc940', // ring around the ball holder
  awardPulse: 1.6,       // Hz of the award-spot ring
  liveTail: 0.07,        // s of velocity drawn as a tail on live balls
});

const HOME = cssColor(TEAM_COLORS[TEAM.HOME]);
const AWAY = cssColor(TEAM_COLORS[TEAM.AWAY]);

/** True when `p` must not appear on an enemy's mini-map (cloak / Silent Footsteps, unless revealed). */
export function hiddenOnMinimap(p) {
  const st = p && p.status;
  if (st && typeof st.has === 'function') {
    if (st.has('revealed')) return false;
    if (st.has('cloaked') || st.has('silentFootsteps')) return true;
  }
  const passive = p && p.hero && p.hero.abilities && p.hero.abilities.passive;
  return !!passive && passive.id === 'gale.silent_footsteps';
}

export class Minimap {
  /** @param {HTMLCanvasElement} canvas */
  constructor(canvas) {
    this.canvas = canvas;
    this.ctx = canvas.getContext('2d');
    this._w = 0; this._h = 0; this._dpr = 1;
    this._flip = 0;
    this._static = null;
    this._staticValid = false;
    /** Latest award spot {x, z, team, ball} (setAward), shown while the ball is hidden for a hand-over. */
    this._award = null;
    this._ro = null;
    const measure = () => {
      const r = canvas.getBoundingClientRect();
      this._setSize(r.width, r.height);
    };
    if (typeof ResizeObserver === 'function') {
      this._ro = new ResizeObserver((entries) => {
        const e = entries[entries.length - 1];
        const box = e && e.contentRect;
        if (box) this._setSize(box.width, box.height);
      });
      this._ro.observe(canvas);
    } else {
      this._onResize = measure;
      window.addEventListener('resize', measure);
    }
    measure();
  }

  dispose() {
    if (this._ro) this._ro.disconnect();
    if (this._onResize) window.removeEventListener('resize', this._onResize);
  }

  _setSize(w, h) {
    const dpr = Math.min(2, window.devicePixelRatio || 1);
    if (w === this._w && h === this._h && dpr === this._dpr) return;
    this._w = w; this._h = h; this._dpr = dpr;
    this.canvas.width = Math.max(1, Math.round(w * dpr));
    this.canvas.height = Math.max(1, Math.round(h * dpr));
    this._staticValid = false;
  }

  _geometry() {
    const c = game.court;
    const halfL = c ? c.halfL : COURT.length / 2;
    const halfW = c ? c.halfW : COURT.width / 2;
    const depth = c ? c.outfieldDepth : COURT.outfieldDepth;
    const side = c && Number.isFinite(c.sideOutfieldWidth) ? c.sideOutfieldWidth : (COURT.sideOutfieldWidth || 2.5);
    const pad = MINIMAP.padding;
    const totalW = 2 * (halfW + side), totalL = 2 * (halfL + depth);
    const s = Math.max(0.1, Math.min((this._w - 2 * pad) / totalW, (this._h - 2 * pad) / totalL));
    return { halfL, halfW, depth, side, s, cx: this._w / 2, cy: this._h / 2 };
  }

  /** Remembers the latest ball hand-over (EV.BallAwarded payload) for the pulsing award-spot ring. */
  setAward(e) {
    if (!e || !e.position) { this._award = null; return; }
    const a = this._award || (this._award = { x: 0, z: 0, team: TEAM.NONE, ball: null });
    a.x = e.position.x; a.z = e.position.z; a.team = e.team; a.ball = e.ball || null;
  }

  /** Builds the cached court drawing for the current size / orientation. */
  _buildStatic(g, flip) {
    if (!this._static) this._static = document.createElement('canvas');
    const sc = this._static;
    sc.width = this.canvas.width; sc.height = this.canvas.height;
    const ctx = sc.getContext('2d');
    ctx.setTransform(this._dpr, 0, 0, this._dpr, 0, 0);
    ctx.clearRect(0, 0, this._w, this._h);
    const rect = (x0, z0, x1, z1, fill, stroke, lw = 1) => {
      const ax = g.cx - x0 * g.s * flip, ay = g.cy - z0 * g.s * flip;
      const bx = g.cx - x1 * g.s * flip, by = g.cy - z1 * g.s * flip;
      const x = Math.min(ax, bx), y = Math.min(ay, by), w = Math.abs(ax - bx), h = Math.abs(ay - by);
      if (fill) { ctx.fillStyle = fill; ctx.fillRect(x, y, w, h); }
      if (stroke) { ctx.strokeStyle = stroke; ctx.lineWidth = lw; ctx.strokeRect(x + 0.5, y + 0.5, w - 1, h - 1); }
    };
    const oW = g.halfW + g.side, oL = g.halfL + g.depth;
    // Halves tinted by the defending team.
    rect(-g.halfW, -g.halfL, g.halfW, 0, 'rgba(255,106,43,0.13)');
    rect(-g.halfW, 0, g.halfW, g.halfL, 'rgba(43,140,255,0.13)');
    // U outfields in the owning team's tint: Home's U surrounds the Away half (+Z), Away's the Home half (-Z).
    const court = game.court;
    const drawU = (team, fill) => {
      const rects = court && typeof court.outfieldRects === 'function' ? court.outfieldRects(team) : null;
      if (rects) { for (const r of rects) rect(r.minX, r.minZ, r.maxX, r.maxZ, fill, 'rgba(255,255,255,0.18)'); return; }
      const sz = team === TEAM.HOME ? 1 : -1; // fallback geometry (same as Court.outfieldRects)
      rect(-oW, 0, -g.halfW, sz * g.halfL, fill, 'rgba(255,255,255,0.18)');
      rect(g.halfW, 0, oW, sz * g.halfL, fill, 'rgba(255,255,255,0.18)');
      rect(-oW, sz * g.halfL, oW, sz * oL, fill, 'rgba(255,255,255,0.18)');
    };
    drawU(TEAM.HOME, 'rgba(255,106,43,0.22)');
    drawU(TEAM.AWAY, 'rgba(43,140,255,0.22)');
    rect(-oW, -oL, oW, oL, null, 'rgba(255,255,255,0.4)', 1);
    rect(-g.halfW, -g.halfL, g.halfW, g.halfL, null, 'rgba(255,255,255,0.75)', 1.2);
    // Centre line across the full width (it also splits the two teams' arms).
    ctx.strokeStyle = 'rgba(255,255,255,0.85)';
    ctx.lineWidth = 1.2;
    ctx.beginPath();
    ctx.moveTo(g.cx - oW * g.s, g.cy); ctx.lineTo(g.cx + oW * g.s, g.cy);
    ctx.stroke();
    this._staticValid = true;
    this._flip = flip;
  }

  /** Redraws the map for `viewer` (the local player, or null when spectating). */
  draw(viewer) {
    if (this._w < 4 || this._h < 4) return;
    const ctx = this.ctx;
    const flip = viewer && viewer.team === TEAM.AWAY ? -1 : 1;
    const g = this._geometry();
    if (!this._staticValid || flip !== this._flip) this._buildStatic(g, flip);
    ctx.setTransform(1, 0, 0, 1, 0, 0);
    ctx.clearRect(0, 0, this.canvas.width, this.canvas.height);
    ctx.drawImage(this._static, 0, 0);
    ctx.setTransform(this._dpr, 0, 0, this._dpr, 0, 0);
    const k = g.s * flip;

    // The single match ball (a held ball is shown as the holder's gold ring). Pooled extras, if any, are skipped.
    const mb = game.balls ? (game.balls.ball || (game.balls.matchBalls && game.balls.matchBalls[0]) || null) : null;
    const aw = this._award;
    if (mb && mb.state === 'despawned' && aw && (!aw.ball || aw.ball === mb)) {
      // Hand-over in progress: pulsing ring at the award spot, in the receiving team's colour.
      const ph = 0.5 + 0.5 * Math.sin(game.time.realNow * Math.PI * 2 * MINIMAP.awardPulse);
      const x = g.cx - aw.x * k, y = g.cy - aw.z * k;
      ctx.strokeStyle = aw.team === TEAM.AWAY ? AWAY : aw.team === TEAM.HOME ? HOME : '#ffffff';
      ctx.globalAlpha = 0.45 + 0.55 * ph;
      ctx.lineWidth = 1.6;
      ctx.beginPath(); ctx.arc(x, y, MINIMAP.ballRadius + 2 + 2 * ph, 0, Math.PI * 2); ctx.stroke();
      ctx.globalAlpha = 1;
    } else if (mb && mb.position && mb.state !== 'held' && mb.state !== 'despawned') {
      const x = g.cx - mb.position.x * k, y = g.cy - mb.position.z * k;
      if (mb.state === 'live' && mb.velocity) {
        ctx.strokeStyle = 'rgba(255,90,70,0.8)';
        ctx.lineWidth = 1.8;
        ctx.beginPath();
        ctx.moveTo(x, y);
        ctx.lineTo(x + mb.velocity.x * k * MINIMAP.liveTail, y + mb.velocity.z * k * MINIMAP.liveTail);
        ctx.stroke();
        ctx.fillStyle = '#ff4b3a';
      } else if (mb.state === 'stasis') {
        ctx.strokeStyle = '#9ad9ff';
        ctx.lineWidth = 1.2;
        ctx.beginPath(); ctx.arc(x, y, MINIMAP.ballRadius + 2.5, 0, Math.PI * 2); ctx.stroke();
        ctx.fillStyle = '#d64a3a';
      } else ctx.fillStyle = '#ff6a55';
      ctx.beginPath(); ctx.arc(x, y, MINIMAP.ballRadius, 0, Math.PI * 2); ctx.fill();
      ctx.strokeStyle = '#ffffff'; ctx.lineWidth = 1;
      ctx.beginPath(); ctx.arc(x, y, MINIMAP.ballRadius + 0.8, 0, Math.PI * 2); ctx.stroke();
    }

    // Players.
    const players = game.players;
    for (let i = 0; i < players.length; i++) {
      const p = players[i];
      if (!p || !p.position) continue;
      if (viewer && p !== viewer && game.areEnemies(viewer, p) && hiddenOnMinimap(p)) continue;
      const x = g.cx - p.position.x * k, y = g.cy - p.position.z * k;
      const color = p.team === TEAM.AWAY ? AWAY : HOME;
      const down = p.fsm && p.fsm.incapReason === 'eliminated';
      if (p === viewer) {
        // Local player: arrow along the facing direction.
        const f = p.forward;
        const dx = f ? -f.x * flip : 0, dy = f ? -f.z * flip : -1;
        const len = Math.hypot(dx, dy) || 1;
        const ux = dx / len, uy = dy / len;
        const r = MINIMAP.playerRadius + 2.2;
        ctx.fillStyle = '#ffffff';
        ctx.strokeStyle = color;
        ctx.lineWidth = 1.6;
        ctx.beginPath();
        ctx.moveTo(x + ux * r * 1.35, y + uy * r * 1.35);
        ctx.lineTo(x - ux * r * 0.8 - uy * r * 0.85, y - uy * r * 0.8 + ux * r * 0.85);
        ctx.lineTo(x - ux * r * 0.35, y - uy * r * 0.35);
        ctx.lineTo(x - ux * r * 0.8 + uy * r * 0.85, y - uy * r * 0.8 - ux * r * 0.85);
        ctx.closePath();
        ctx.fill(); ctx.stroke();
        if (p.combat && p.combat.hasBall) {
          ctx.strokeStyle = MINIMAP.holderRing; ctx.lineWidth = 2;
          ctx.beginPath(); ctx.arc(x, y, r + 1.5, 0, Math.PI * 2); ctx.stroke();
        }
        continue;
      }
      if (down) {
        ctx.strokeStyle = 'rgba(255,255,255,0.45)';
        ctx.lineWidth = 1.4;
        const r = MINIMAP.playerRadius * 0.8;
        ctx.beginPath();
        ctx.moveTo(x - r, y - r); ctx.lineTo(x + r, y + r);
        ctx.moveTo(x + r, y - r); ctx.lineTo(x - r, y + r);
        ctx.stroke();
        continue;
      }
      const r = MINIMAP.playerRadius;
      if (p.zone === ZONE.OUTFIELD && p.isStartingOutfielder) {
        // Starting outfielder (元外野): hollow diamond.
        ctx.beginPath();
        ctx.moveTo(x, y - r); ctx.lineTo(x + r, y); ctx.lineTo(x, y + r); ctx.lineTo(x - r, y); ctx.closePath();
        ctx.strokeStyle = color; ctx.lineWidth = 1.8; ctx.stroke();
      } else if (p.zone === ZONE.OUTFIELD) {
        ctx.beginPath(); ctx.arc(x, y, r, 0, Math.PI * 2);
        ctx.strokeStyle = color; ctx.lineWidth = 1.8; ctx.stroke();
      } else {
        ctx.beginPath(); ctx.arc(x, y, r, 0, Math.PI * 2);
        ctx.fillStyle = color; ctx.fill();
        ctx.strokeStyle = 'rgba(0,0,0,0.55)'; ctx.lineWidth = 1; ctx.stroke();
      }
      if (p.combat && p.combat.hasBall) {
        ctx.strokeStyle = MINIMAP.holderRing; ctx.lineWidth = 2;
        ctx.beginPath(); ctx.arc(x, y, r + 2.2, 0, Math.PI * 2); ctx.stroke();
      }
    }
  }
}
