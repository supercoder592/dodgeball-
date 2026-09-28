// ---------------------------------------------------------------------------------------------------------------
// Status - timed / permanent status effects of one player (web port of the Unity StatusEffectController).
//
// Effects are keyed by (type, source): the same source re-applying refreshes its entry (longest remaining time,
// strongest magnitude); different sources coexist and a type is active while any entry of it is. Queries return the
// strongest magnitude / longest remaining time. Durations tick on scaled time (hitstop and pause hold them).
//
// Side effects (applied when a type turns on/off):
//   slow / haste  -> moveSpeedMul = (1 - strongest slow) * (1 + strongest haste)   (read by Motor every step)
//   frozen        -> fsm.incapacitate('frozen') / release, combat.catchingBlocked, avatar.setFrozen, frost mist VFX,
//                    ice burst on thaw, local 'freeze' screen tint
//   cloaked       -> avatar.setCloaked (blocked while 'revealed'; 'revealed' strips an active cloak)
//   slippery      -> motor traction modifier (1 - magnitude): ice sliding, heavy inertia
//   stunned       -> applying it from gameplay code forwards to fsm.stun(duration); the fsm mirrors its stun back
//                    here (source = fsm) so has('stunned') works for everyone
// Every change of a type's active state (or a new source / stronger magnitude) emits EV.Status
// { player, type, applied, duration, magnitude } (duration 0 = permanent).
// ---------------------------------------------------------------------------------------------------------------
import { game } from '../game.js';
import { EV } from '../core/events.js';
import { statusSpeedMultiplier } from './player_math.js';

export const STATUS = Object.freeze({
  SLOW: 'slow', HASTE: 'haste', FROZEN: 'frozen', STUNNED: 'stunned', INVULNERABLE: 'invulnerable', CLOAKED: 'cloaked',
  REVEALED: 'revealed', SILENCED: 'silenced', ROOTED: 'rooted', SLIPPERY: 'slippery', DODGE_DISABLED: 'dodgeDisabled',
  SILENT_FOOTSTEPS: 'silentFootsteps', OBSCURED: 'obscured', MAGNETIZED: 'magnetized',
});

/** Hard-control effects are always cleared by clearForReset(), even when applied as permanent. */
const HARD_CONTROL = new Set([STATUS.FROZEN, STATUS.STUNNED, STATUS.ROOTED]);

export class Status {
  static defaults = {
    maxSlow: 0.9,            // strongest slow accepted (a 100% slow would be a root: use 'rooted')
    maxHaste: 1,             // strongest haste accepted (+100%)
    frozenMistHeight: 0.9,   // m above the feet (informational for VFX)
    thawBurstScale: 0.6,
    localFrozenScreenTint: 0.6,
    permanentVfxDuration: 30, // VFX attach duration used for open-ended effects (stopped explicitly on removal)
  };

  /** @param {import('./player.js').Player} player */
  constructor(player) {
    this.player = player;
    this.tuning = { ...Status.defaults };
    /** @type {{type:string, source:any, duration:number, remaining:number, magnitude:number, permanent:boolean}[]} */
    this.entries = [];
    this._pool = [];
    this._counts = Object.create(null);
    this._sideActive = Object.create(null);
    this._changed = [];
    this._blockedCatching = false;
    this._frozenMist = null;
    /** Product of slow/haste: read by the Motor every step. */
    this.moveSpeedMul = 1;
  }

  // ------------------------------------------------------------------ contract
  /**
   * Applies (or refreshes) an effect.
   * @param {string} type STATUS value
   * @param {number} duration seconds; <= 0 means permanent (until removed)
   * @param {number} [magnitude] e.g. 0.6 for a 60% slow, 0.2 for +20% haste, 0.85 slippery = 15% traction
   * @param {any} [source] owner of the entry (ability instance, puddle...); same source refreshes its entry
   * @returns {boolean} applied
   */
  apply(type, duration, magnitude = 1, source = null) {
    if (!type || !Number.isFinite(duration) || !Number.isFinite(magnitude)) return false;
    const p = this.player;
    // Eliminated players (ragdolling / outfield transfer) take no new effects.
    if (p && p.health && p.health.isEliminated) return false;
    // Revealed blocks stealth.
    if (type === STATUS.CLOAKED && this.has(STATUS.REVEALED)) return false;
    // Stuns are owned by the state machine; it mirrors them back here with itself as the source.
    const fsm = p && p.fsm;
    if (type === STATUS.STUNNED && fsm && !fsm.isMirroringStun && source !== fsm) {
      return duration > 0 ? !!fsm.stun(duration) : false;
    }
    const permanent = duration <= 0;
    let e = this._find(type, source);
    let notify = false;
    if (e) {
      e.permanent = e.permanent || permanent;
      e.remaining = e.permanent ? Infinity : Math.max(e.remaining, duration);
      e.duration = e.permanent ? 0 : Math.max(e.duration, duration);
      if (magnitude > e.magnitude + 1e-6) { e.magnitude = magnitude; notify = true; }
    } else {
      e = this._pool.pop() || {};
      e.type = type; e.source = source; e.duration = permanent ? 0 : duration;
      e.remaining = permanent ? Infinity : duration; e.magnitude = magnitude; e.permanent = permanent;
      this.entries.push(e);
      this._counts[type] = (this._counts[type] || 0) + 1;
      notify = true;
    }
    // Revealed strips any active cloak.
    if (type === STATUS.REVEALED && this.has(STATUS.CLOAKED)) this.remove(STATUS.CLOAKED);
    this._processType(type, notify);
    return true;
  }

  /** Removes `type` entries from `source` (every source when omitted). */
  remove(type, source) {
    if (!this._counts[type]) return;
    const fsm = this.player && this.player.fsm;
    let removed = false;
    for (let i = this.entries.length - 1; i >= 0; i--) {
      const e = this.entries[i];
      if (e.type !== type) continue;
      if (source !== undefined && source !== null && e.source !== source) continue;
      this._release(i);
      removed = true;
    }
    if (!removed) return;
    // Somebody cleansed the stun from outside: end the fsm stun as well.
    if (type === STATUS.STUNNED && fsm && !fsm.isMirroringStun && !this.has(STATUS.STUNNED)) fsm.stunRemaining = 0;
    this._processType(type, true);
  }

  /** Removes every effect (permanent passive markers included). */
  removeAll() {
    this._changed.length = 0;
    for (let i = this.entries.length - 1; i >= 0; i--) {
      const t = this.entries[i].type;
      if (!this._changed.includes(t)) this._changed.push(t);
      this._release(i);
    }
    for (const t in this._sideActive) if (this._sideActive[t] && !this._changed.includes(t)) this._changed.push(t);
    this._flushChanged();
  }

  /**
   * Round reset / elimination: removes every timed effect and every hard-control effect; permanent passive markers
   * (e.g. Gale's silentFootsteps) stay.
   */
  clearForReset() {
    this._changed.length = 0;
    for (let i = this.entries.length - 1; i >= 0; i--) {
      const e = this.entries[i];
      if (e.permanent && !HARD_CONTROL.has(e.type)) continue;
      if (!this._changed.includes(e.type)) this._changed.push(e.type);
      this._release(i);
    }
    this._flushChanged();
  }

  has(type) { return (this._counts[type] || 0) > 0; }
  /** True if `source` has an entry of `type`. */
  hasFrom(type, source) { return !!this._find(type, source); }
  /** Strongest magnitude of `type` (0 when inactive). */
  magnitude(type) {
    if (!this.has(type)) return 0;
    let best = 0;
    for (const e of this.entries) if (e.type === type && e.magnitude > best) best = e.magnitude;
    return best;
  }
  /** Longest remaining seconds of `type` (Infinity for permanent, 0 when inactive). */
  remaining(type) {
    if (!this.has(type)) return 0;
    let best = 0;
    for (const e of this.entries) if (e.type === type && e.remaining > best) best = e.remaining;
    return best;
  }

  /** Ticks durations (scaled dt) and expires effects. */
  update(dt) {
    if (!(dt > 0) || this.entries.length === 0) return;
    this._changed.length = 0;
    for (let i = this.entries.length - 1; i >= 0; i--) {
      const e = this.entries[i];
      if (e.permanent) continue;
      e.remaining -= dt;
      if (e.remaining <= 0) {
        if (!this._changed.includes(e.type)) this._changed.push(e.type);
        this._release(i);
      }
    }
    this._flushChanged();
  }

  /** Stops attached VFX / screen tints (Player.dispose). */
  dispose() {
    this.removeAll();
    if (this._frozenMist) { game.vfx?.stop?.(this._frozenMist); this._frozenMist = null; }
  }

  // ------------------------------------------------------------------ internals
  _find(type, source) {
    for (const e of this.entries) if (e.type === type && e.source === source) return e;
    return null;
  }

  _release(i) {
    const e = this.entries[i];
    const last = this.entries.length - 1;
    if (i !== last) this.entries[i] = this.entries[last];
    this.entries.pop();
    this._counts[e.type] = Math.max(0, (this._counts[e.type] || 0) - 1);
    e.source = null;
    this._pool.push(e);
  }

  _flushChanged() {
    if (this._changed.length === 0) return;
    // Snapshot (only when something changed): side effects may re-enter apply/remove/clear on this component.
    const list = this._changed.splice(0);
    for (let i = 0; i < list.length; i++) this._processType(list[i], true);
  }

  /** Applies side effects of `type` turning on/off and publishes EV.Status. */
  _processType(type, notify) {
    const active = this.has(type);
    const wasActive = !!this._sideActive[type];
    this._sideActive[type] = active;
    const p = this.player;
    const mag = active ? this.magnitude(type) : 0;
    switch (type) {
      case STATUS.SLOW:
      case STATUS.HASTE:
        this.moveSpeedMul = statusSpeedMultiplier(this.magnitude(STATUS.SLOW), this.magnitude(STATUS.HASTE), this.tuning.maxSlow, this.tuning.maxHaste);
        break;
      case STATUS.SLIPPERY:
        if (p && p.motor) {
          if (active) p.motor.setTractionModifier(this, 1 - Math.min(1, Math.max(0, mag)));
          else p.motor.removeTractionModifier(this);
        }
        break;
      case STATUS.FROZEN:
        if (active !== wasActive) this._applyFrozen(active);
        break;
      case STATUS.CLOAKED:
        if (active !== wasActive) this._safeAvatar('setCloaked', active);
        break;
      default:
        break;
    }
    if (!active && !wasActive) return; // nothing was there and nothing is there
    if (active === wasActive && !notify) return;
    const rem = active ? this.remaining(type) : 0;
    game.events.emit(EV.Status, {
      player: p, type, applied: active, duration: Number.isFinite(rem) ? rem : 0, magnitude: mag,
    });
  }

  _applyFrozen(frozen) {
    const p = this.player;
    if (!p) return;
    const fsm = p.fsm;
    if (fsm) {
      if (frozen) fsm.incapacitate('frozen');
      else fsm.release('frozen');
    }
    const c = p.combat;
    if (c) {
      if (frozen) {
        if (!c.catchingBlocked) { c.catchingBlocked = true; this._blockedCatching = true; }
      } else if (this._blockedCatching) {
        c.catchingBlocked = false;
        this._blockedCatching = false;
      }
    }
    this._safeAvatar('setFrozen', frozen);
    const vfx = game.vfx;
    if (frozen) {
      if (!this._frozenMist && vfx && vfx.attach && p.root) {
        const rem = this.remaining(STATUS.FROZEN);
        this._frozenMist = vfx.attach('frozenMist', p.root, { duration: Number.isFinite(rem) ? rem : this.tuning.permanentVfxDuration }) || null;
      }
      if (vfx && vfx.play && p.root) vfx.play('iceBurst', p.chestPosition.clone(), { scale: this.tuning.thawBurstScale * 0.8 });
    } else {
      if (this._frozenMist && vfx && vfx.stop) vfx.stop(this._frozenMist);
      this._frozenMist = null;
      // Thaw: the ice cracks off.
      if (vfx && vfx.play && p.root) vfx.play('iceBurst', p.chestPosition.clone(), { scale: this.tuning.thawBurstScale });
    }
    if (p.isLocal && game.renderer && game.renderer.setSustained) {
      game.renderer.setSustained('freeze', frozen ? this.tuning.localFrozenScreenTint : 0);
    }
  }

  _safeAvatar(method, arg) {
    const a = this.player && this.player.avatar;
    if (!a || typeof a[method] !== 'function') return;
    try { a[method](arg); } catch (e) { console.error(`[status] avatar.${method} failed`, e); }
  }
}
