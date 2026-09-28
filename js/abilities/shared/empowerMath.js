// ---------------------------------------------------------------------------------------------------------------
// Empowered throws (pure, unit tested). Single-ball rule: no ability may conjure a second dodgeball, so the former
// "ability ball" skills (Rayne Supersonic Meteor / Hyperbeam Transpierce, Elsa Glacier Freeze, Screws Glue Trap Ball)
// now EMPOWER the match ball the caster is holding. applyEmpower() turns the ThrowParams Combat.buildThrowParams built
// for that held ball into the special throw (style, speed/radius/gravity, unblockable, pierce, payload) exactly the way
// the old abilityUtil.throwAbilityBall did - except that the ball keeps its rally count (the 220 km/h cap still binds).
// No three.js import: numbers and plain objects only.
// ---------------------------------------------------------------------------------------------------------------

/**
 * @typedef {object} EmpowerOptions
 * @property {string} [style='standard']   ball look while live ('meteor' | 'beam' | 'glue' | 'freeze' | ...)
 * @property {number} [speedMul=1]         multiplies params.speedMul (ignored unless > 0)
 * @property {number} [radiusMul=1]        multiplies params.radiusMul (ignored unless > 0)
 * @property {number} [gravityScale=0.5]   replaces params.gravityScale (0 = laser-flat)
 * @property {boolean} [unblockable=false] OR-ed into params.unblockable
 * @property {boolean} [pierce=false]      OR-ed into params.pierce
 * @property {object|null} [payload=null]  ball payload (hit/surface/catch hooks)
 * @property {boolean} [keepRally=true]    false resets the rally count to 0
 */

/**
 * Applies an empowered-throw recipe to `params` in place (returns it). Always marks the throw as an ability throw
 * (isAbility) and never as a pass.
 * @param {object} params ThrowParams from Combat.buildThrowParams
 * @param {EmpowerOptions} [opts]
 * @returns {object} params
 */
export function applyEmpower(params, opts = {}) {
  if (!params) return params;
  const o = opts || {};
  params.style = o.style || 'standard';
  params.speedMul = (params.speedMul ?? 1) * (o.speedMul > 0 ? o.speedMul : 1);
  params.radiusMul = (params.radiusMul ?? 1) * (o.radiusMul > 0 ? o.radiusMul : 1);
  params.gravityScale = Number.isFinite(o.gravityScale) ? o.gravityScale : 0.5;
  params.unblockable = !!params.unblockable || !!o.unblockable;
  params.pierce = !!params.pierce || !!o.pierce;
  params.payload = o.payload || null;
  params.isAbility = true;
  params.isPass = false;
  if (o.keepRally === false) params.rallyCount = 0;
  return params;
}

/**
 * Charge (seconds) an empowered throw is built with: at least a full charge, more if the caster had been charging
 * longer (a held-back throw is never weakened by pressing the skill).
 * @param {number} chargeSeconds current wind-up of the caster (0 when not charging)
 * @param {number} fullChargeTime profile full-charge time
 */
export function empowerChargeSeconds(chargeSeconds, fullChargeTime) {
  const c = Number.isFinite(chargeSeconds) ? chargeSeconds : 0;
  const f = Number.isFinite(fullChargeTime) ? fullChargeTime : 0.75;
  return Math.max(c, f);
}
