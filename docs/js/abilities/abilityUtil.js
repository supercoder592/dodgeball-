// Shared helpers for hero abilities (kernel).
import * as THREE from 'three';
import { game } from '../game.js';
import { Court } from '../world/court.js';

const _down = new THREE.Vector3(0, -1, 0);

/**
 * Conjures a temporary ability projectile at the thrower's hand and launches it through the normal throw pipeline
 * (throw modifiers, rally cap, BallThrown event). Ability balls are recycled automatically when they stop being live.
 * opts: { style, speedMul=1, radiusMul=1, gravityScale=0.5, unblockable=false, pierce=false, payload=null, target=null }
 * @returns {import('../combat/ball.js').Ball|null}
 */
export function throwAbilityBall(thrower, opts = {}) {
  if (!thrower || !thrower.combat || !game.balls) return null;
  const combat = thrower.combat;
  const ball = game.balls.spawnAbilityBall(combat.getThrowOrigin(), opts.style || 'standard');
  if (!ball) return null;
  const p = combat.buildThrowParams(combat.profile.fullChargeTime, opts.target || combat.currentTarget, true);
  p.style = opts.style || 'standard';
  p.speedMul *= opts.speedMul > 0 ? opts.speedMul : 1;
  p.radiusMul *= opts.radiusMul > 0 ? opts.radiusMul : 1;
  p.gravityScale = opts.gravityScale ?? 0.5;
  p.unblockable = p.unblockable || !!opts.unblockable;
  p.pierce = p.pierce || !!opts.pierce;
  p.payload = opts.payload || null;
  p.rallyCount = 0;
  p.isAbility = true;
  combat.launchBall(ball, p);
  return ball;
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
