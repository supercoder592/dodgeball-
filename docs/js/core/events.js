// Decoupled publish/subscribe bus (Subject-Observer). Combat publishes facts; Juice/HUD/Audio/VFX/AI/abilities observe.
// Handlers are isolated: an exception in one is logged and the others still run. Subscribing during emit is safe.

/** Event names and payload shapes. ALWAYS use these constants. */
export const EV = Object.freeze({
  // match flow
  MatchStarted: 'match:started',            // { roundsToWin, playersPerTeam }
  RoundCountdown: 'round:countdown',        // { round, secondsLeft }
  RoundStarted: 'round:started',            // { round, duration }
  RoundEnded: 'round:ended',                // { round, winner (TEAM or -1), reason:'eliminated'|'time'|'draw', scores:[home,away] }
  MatchEnded: 'match:ended',                // { winner, scores }
  MatchPhase: 'match:phase',                // { previous, current }
  // players
  PlayerSpawned: 'player:spawned',          // { player }
  PlayerState: 'player:state',              // { player, previous, current }
  PlayerDamaged: 'player:damaged',          // { player, attacker, damage, remainingHp, point }
  PlayerEliminated: 'player:eliminated',    // { player, attacker, cause, impulse:Vector3, point:Vector3 }
  PlayerRevived: 'player:revived',          // { player, reviver, cause }
  PlayerZone: 'player:zone',                // { player, zone }
  Status: 'player:status',                  // { player, type, applied:boolean, duration, magnitude }
  // balls & combat
  BallPickedUp: 'ball:pickedUp',            // { ball, player }
  ChargeStarted: 'throw:chargeStarted',     // { player, ball }
  BallThrown: 'ball:thrown',                // { ball, thrower, target, origin, velocity, speedKmh, rallyCount, charge, isAbility, isPass, isCounter }
  BallHitPlayer: 'ball:hitPlayer',          // { ball, attacker, victim, point, normal, velocity, speedKmh, damage, outcome, local }
  CatchAttempt: 'catch:attempt',            // { player }
  CatchWhiff: 'catch:whiff',                // { player }
  BallCaught: 'ball:caught',                // { ball, catcher, thrower, quality, secondsBeforeImpact, point, speedKmh, rallyCount, local }
  BallPassed: 'ball:passed',                // { ball, from, to, teleported }
  BallBounced: 'ball:bounced',              // { ball, point, normal, impactSpeed, floor, rallyReset, endedLive }
  BallBlocked: 'ball:blocked',              // { ball, blocker, point, normal }
  BallOutOfBounds: 'ball:outOfBounds',      // { ball, position }
  DangerSense: 'danger:sense',              // { player, ball, timeToImpact, speedKmh, active }
  // abilities
  AbilityCast: 'ability:cast',              // { player, ability, slot }
  AbilityEnded: 'ability:ended',            // { player, ability, slot, interrupted }
  AbilityFailed: 'ability:failed',          // { player, ability, slot, reason }
  UltCharge: 'ability:ultCharge',           // { player, value, becameReady }
  // juice
  Hitstop: 'juice:hitstop',                 // { duration, scale }
});

export class EventBus {
  constructor() { this.handlers = new Map(); }
  on(name, fn) {
    if (!this.handlers.has(name)) this.handlers.set(name, []);
    const list = this.handlers.get(name);
    if (!list.includes(fn)) this.handlers.set(name, [...list, fn]);
    return () => this.off(name, fn);
  }
  off(name, fn) {
    const list = this.handlers.get(name);
    if (list) this.handlers.set(name, list.filter((h) => h !== fn));
  }
  emit(name, payload) {
    const list = this.handlers.get(name);
    if (!list) return;
    for (const fn of list) {
      try { fn(payload); } catch (e) { console.error(`[events] handler for ${name} threw`, e); }
    }
  }
  clear() { this.handlers.clear(); }
}
