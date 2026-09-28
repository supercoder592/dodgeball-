// ---------------------------------------------------------------------------------------------------------------
// throwHeldBall - the one entry point hero abilities use for an "empowered" throw of the match ball they hold
// (single-ball rule: abilities never conjure a second dodgeball). Thin wrapper over the kernel helper
// abilityUtil.throwEmpoweredBall: buildThrowParams(max(chargeSeconds, fullChargeTime), target, isAbility=true) ->
// applyEmpower -> launchBall.
//   * Returns the launched ball, or null when the caster holds no ball / cannot act (callers refund and report).
//   * launchBall plays the throw animation and emits EV.BallThrown { isAbility: true }, which also releases the
//     possession clock - callers must not play a second throw animation.
// ---------------------------------------------------------------------------------------------------------------
import { throwEmpoweredBall } from '../abilityUtil.js';

/**
 * Throws the ball `thrower` is holding as an empowered ability throw.
 * @param {import('../../gameplay/player.js').Player} thrower
 * @param {import('./empowerMath.js').EmpowerOptions & {target?: object}} [opts]
 * @returns {import('../../combat/ball.js').Ball|null}
 */
export function throwHeldBall(thrower, opts = {}) {
  return throwEmpoweredBall(thrower, opts);
}

/** Does `player` hold the match ball right now (the precondition of every empowered throw)? */
export function holdsBall(player) {
  return !!(player && player.combat && player.combat.heldBall);
}
