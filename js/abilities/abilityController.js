// Owns a player's passive / skill / ultimate instances and the ultimate meter (kernel).
import { game } from '../game.js';
import { Meter } from '../core/timers.js';
import { EV } from '../core/events.js';
import { ZONE } from '../core/constants.js';
import { abilityRegistry, SLOT } from './abilityBase.js';

/** Smallest passive ultimate-charge change (0..1) worth an EV.UltCharge event. */
const ULT_PASSIVE_EVENT_STEP = 0.005;

export class AbilityController {
  /** @param {import('../gameplay/player.js').Player} owner */
  constructor(owner) {
    this.owner = owner;
    this.meter = new Meter(1, 0);
    /** Ultimate charge per second while infield in a live round (0..1 scale). */
    this.passiveUltPerSecond = 0.008;
    /**
     * Share of that trickle a STARTING outfielder (元外野, out there all round and never targetable) still earns, so the
     * permanent outfielder can reach an ultimate too. Eliminated outfielders earn none (they get back in by a hit).
     */
    this.starterUltShare = 0.5;
    /** Charge value carried by the last EV.UltCharge event (passive gains are throttled against it). */
    this._announced = 0;
    this.passive = null; this.skill = null; this.ultimate = null;
    this.all = [];
  }

  /** @param {object} hero roster hero definition with abilities {passive, skill, ultimate} */
  setup(hero) {
    this.teardown();
    this.passive = this._create(hero.abilities.passive);
    this.skill = this._create(hero.abilities.skill);
    this.ultimate = this._create(hero.abilities.ultimate);
  }

  teardown() { for (const a of this.all) a.unequip(); this.all = []; this.passive = this.skill = this.ultimate = null; }

  get(slot) { return slot === SLOT.PASSIVE ? this.passive : slot === SLOT.SKILL ? this.skill : slot === SLOT.ULTIMATE ? this.ultimate : null; }
  /** First ability that is an instance of `cls`. */
  find(cls) { return this.all.find((a) => a instanceof cls) || null; }

  get ultimateCharge() { return this.meter.normalized; }
  isUltimateReady(cost = 1) { return this.meter.value + 1e-4 >= Math.min(1, cost); }

  tryUseSkill() { return !!this.skill && this.skill.tryActivate(); }
  tryUseUltimate() { return !!this.ultimate && this.ultimate.tryActivate(); }

  consumeUltimate(cost = 1) { const before = this.meter.value; this.meter.add(-Math.min(1, cost)); this._publish(before, false); }

  /** Spec: a Perfect Catch adds 0.15. */
  addUltimateCharge(amount, reason = 'misc') {
    if (!amount) return;
    const before = this.meter.value;
    const { becameFull } = this.meter.add(amount);
    this._publish(before, becameFull, reason);
  }
  setUltimateCharge(v) { const before = this.meter.value; this.meter.set(v); this._publish(before, this.meter.full && before < 1); }

  interruptAll(reason) { for (const a of this.all) a.interrupt(reason); }

  resetForRound(clearUltimate = false) {
    for (const a of this.all) a.resetForRound();
    if (clearUltimate) this.setUltimateCharge(0);
  }

  update(dt) {
    const live = !game.match || game.match.isPlaying;
    if (live && !this.meter.full && this.passiveUltPerSecond > 0) {
      const o = this.owner;
      const share = o.zone === ZONE.INFIELD ? 1 : o.isStartingOutfielder ? this.starterUltShare : 0;
      if (share > 0) this.addUltimateCharge(this.passiveUltPerSecond * share * dt, 'passive');
    }
    for (const a of this.all) a.tick(dt);
  }

  _create(def) {
    if (!def) return null;
    const cls = abilityRegistry.get(def.id);
    if (!cls) { console.warn(`[abilities] no class registered for ${def.id}`); return null; }
    const a = new cls(this.owner, def);
    this.all.push(a);
    a.initialize();
    return a;
  }

  _publish(before, becameReady, reason) {
    if (Math.abs(before - this.meter.value) < 1e-6) return;
    const value = this.meter.normalized;
    // Passive trickle charges every frame: only announce visible steps (readers poll ultimateCharge for the bar).
    if (reason === 'passive' && !becameReady && Math.abs(value - this._announced) < ULT_PASSIVE_EVENT_STEP) return;
    this._announced = value;
    game.events.emit(EV.UltCharge, { player: this.owner, value, becameReady: !!becameReady, reason });
  }
}
