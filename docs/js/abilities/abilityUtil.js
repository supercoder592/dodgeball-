// Shared helpers for hero abilities (kernel).
import * as THREE from 'three';
import { game } from '../game.js';
import { Court } from '../world/court.js';
import { applyEmpower, empowerChargeSeconds } from './shared/empowerMath.js';

const _down = new THREE.Vector3(0, -1, 0);

/**
 * Empowered throw of the match ball `thrower` is HOLDING (single-ball rule: abilities never conjure a second
 * dodgeball). Builds the throw through the normal pipeline (throw modifiers, 220 km/h rally cap, BallThrown with
 * isAbility:true - which also releases the possession clock) and applies the ability style / payload. The ball keeps
 * its rally count. launchBall plays the throw animation, callers must not play a second one.
 * opts: { style, speedMul=1, radiusMul=1, gravityScale=0.5, unblockable=false, pierce=false, payload=null, target=null,
 *         keepRally=true }
 * @returns {import('../combat/ball.js').Ball|null} the launched ball, or null (no ball in hand / cannot act)
 */
export function throwEmpoweredBall(thrower, opts = {}) {
  const combat = thrower && thrower.combat;
  const ball = combat && combat.heldBall;
  if (!ball || thrower.canAct === false) return null;
  const secs = empowerChargeSeconds(combat.isCharging ? combat.chargeSeconds : 0, combat.profile && combat.profile.fullChargeTime);
  if (combat.isCharging && combat.cancelCharge) combat.cancelCharge();
  const p = combat.buildThrowParams(secs, opts.target || combat.currentTarget, true);
  applyEmpower(p, opts);
  return combat.launchBall(ball, p) || null;
}

/**
 * @deprecated Single-ball rule: alias of {@link throwEmpoweredBall}. It can never spawn a ball any more - without a
 * held ball it returns null.
 */
export function throwAbilityBall(thrower, opts = {}) {
  return throwEmpoweredBall(thrower, opts);
}

/** Floor point below `pos` (court floor). */
export function groundPoint(pos) {
  return new THREE.Vector3(pos.x, game.court ? game.court.floorY : 0, pos.z);
}

/** Clamp a position into the confinement of `player`'s team and zone (court rules must always hold). */
export function clampToPlayerZone(player, pos) {
  if (!game.court || !player) return pos;
  return Court.clamp(game.court.confinement(player.team, player.zone), pos.clone());
}

export { _down as DOWN };
