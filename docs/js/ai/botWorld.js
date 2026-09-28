// ---------------------------------------------------------------------------------------------------------------
// Read-only game-world queries shared by the AI (bot brain, perception, ability context).
// Everything goes through the `game` context and is defensive about optional systems / members, because the AI must
// keep working while other modules evolve. Hot-path helpers never allocate (they write into `out` vectors and use
// cached confinement bounds); only decision-rate helpers (line of sight) may call APIs that allocate.
// ---------------------------------------------------------------------------------------------------------------
import { game } from '../game.js';
import { TEAM, ZONE, KMH_TO_MS, STANDARD_HIT_DAMAGE } from '../core/constants.js';
import { RallyMath } from '../core/rules.js';
import { heroMovement, heroCombat, BASE_MOVEMENT, BASE_COMBAT } from '../abilities/roster.js';
import { clamp01, lerp, planarAngleDeg, planarDistance } from './aiMath.js';

/** Player state ids (mirror of gameplay/states.js STATE; kept local so the AI never breaks the import graph). */
export const STATE = Object.freeze({
  GROUNDED: 'grounded', AIRBORNE: 'airborne', SPRINTING: 'sprinting', SLIDING: 'sliding', CHARGING: 'chargingThrow',
  CATCHING: 'catching', STUNNED: 'stunned', INCAPACITATED: 'incapacitated',
});

/** Ball states (combat/ball.js). */
export const BALL_STATE = Object.freeze({ FREE: 'free', HELD: 'held', LIVE: 'live', STASIS: 'stasis', DESPAWNED: 'despawned' });

/** Chest height as a fraction of body height (matches the avatar's sternum on Rocketbox rigs). */
export const CHEST_HEIGHT_FRAC = 0.72;
/** Fallback capsule when a player does not expose radius/height. */
export const DEFAULT_BODY_RADIUS = 0.32;
export const DEFAULT_BODY_HEIGHT = 1.8;

// ------------------------------------------------------------------ player readouts

/** Current PlayerStateMachine id is `id` (works with `is(id)`, a string `current` or a state object). */
export function stateIs(p, id) {
  const f = p && p.fsm;
  if (!f) return false;
  if (typeof f.is === 'function') return f.is(id);
  const c = f.current;
  return (typeof c === 'string' ? c : c && c.id) === id;
}

export function hasStatus(p, type) {
  const s = p && p.status;
  return !!(s && typeof s.has === 'function' && s.has(type));
}

export function holdsBall(p) { return !!(p && p.combat && p.combat.hasBall); }
export function isCharging(p) { return !!(p && p.combat && p.combat.isCharging); }
export function bodyRadius(p) { return (p && p.radius > 0) ? p.radius : DEFAULT_BODY_RADIUS; }
export function bodyHeight(p) { return (p && p.height > 0) ? p.height : DEFAULT_BODY_HEIGHT; }

/** Chest point of `p` into `out` (computed from feet + height: never touches allocating getters). */
export function chestOf(p, out) {
  const pos = p.position;
  out.x = pos.x; out.y = pos.y + bodyHeight(p) * CHEST_HEIGHT_FRAC; out.z = pos.z;
  return out;
}

/** Planar body forward from yaw (yaw 0 faces +Z). */
export function forwardX(p) { return Math.sin((p && p.yaw) || 0); }
export function forwardZ(p) { return Math.cos((p && p.yaw) || 0); }

export function planarSpeedOf(p) {
  const m = p && p.motor;
  if (m && Number.isFinite(m.planarSpeed)) return m.planarSpeed;
  const v = p && p.velocity;
  return v ? Math.hypot(v.x, v.z) : 0;
}

/** -1 for Home (defends -Z), +1 for Away. */
export function sideSign(team) { return team === TEAM.HOME ? -1 : 1; }

const _moveProfiles = new WeakMap();
const _combatProfiles = new WeakMap();
/** Movement tuning of `p` (motor profile when exposed, else the roster merge; cached per hero). */
export function movementProfile(p) {
  const mp = p && p.motor && p.motor.profile;
  if (mp && typeof mp === 'object') return mp;
  const hero = p && p.hero;
  if (!hero) return BASE_MOVEMENT;
  let prof = _moveProfiles.get(hero);
  if (!prof) { prof = heroMovement(hero); _moveProfiles.set(hero, prof); }
  return prof;
}
/** Combat tuning of `p` (combat profile when exposed, else the roster merge; cached per hero). */
export function combatProfile(p) {
  const cp = p && p.combat && p.combat.profile;
  if (cp && typeof cp === 'object') return cp;
  const hero = p && p.hero;
  if (!hero) return BASE_COMBAT;
  let prof = _combatProfiles.get(hero);
  if (!prof) { prof = heroCombat(hero); _combatProfiles.set(hero, prof); }
  return prof;
}

/** Effective Perfect Catch window of `p` (0.15 s, 0.225 s with Iron Mitts). */
export function perfectWindowOf(p) {
  const c = p && p.combat;
  if (c && c.perfectWindow > 0) return c.perfectWindow;
  const prof = combatProfile(p);
  return (prof.perfectCatchWindow || 0.15) * (prof.perfectWindowMul || 1);
}

/**
 * Can `observer` see `target`? Cloaked enemies (Gale's Optical Camouflage) are only noticed inside
 * `cloakRadius` unless revealed.
 */
export function isPerceivable(observer, target, cloakRadius) {
  if (!target) return false;
  if (hasStatus(target, 'cloaked') && !hasStatus(target, 'revealed')) {
    return !!observer && planarDistance(observer.position, target.position) <= cloakRadius;
  }
  return true;
}

/** Target is locked into something it cannot abort (airborne, charging, stunned, frozen, rooted...). */
export function isCommitted(p) {
  if (!p) return false;
  return stateIs(p, STATE.AIRBORNE) || stateIs(p, STATE.CHARGING) || stateIs(p, STATE.STUNNED) ||
    stateIs(p, STATE.INCAPACITATED) || hasStatus(p, 'frozen') || hasStatus(p, 'rooted');
}

/** Targetable players of `team` (infield, alive, not mid-elimination). */
export function countTargetable(team) {
  let n = 0;
  const players = game.players;
  for (let i = 0; i < players.length; i++) { const p = players[i]; if (p.team === team && p.isTargetable) n++; }
  return n;
}

export function roundTimeLeft() {
  const m = game.match;
  return m && Number.isFinite(m.timeLeft) ? m.timeLeft : Infinity;
}

/** A teammate of `self` is in Chrono's Delayed Impact window (a catch now cancels the elimination). */
export function anyTeammatePending(self) {
  const players = game.players;
  for (let i = 0; i < players.length; i++) {
    const p = players[i];
    if (p !== self && p.team === self.team && p.health && p.health.pending) return true;
  }
  return false;
}

// ------------------------------------------------------------------ court

const _conf = { court: null, bounds: [null, null, null, null] };
/**
 * Cached movement confinement of (team, zone) - Court.confinement() allocates, so the four combinations are built
 * once per court instance. Returns null before the court exists.
 */
export function confinementOf(team, zone) {
  const court = game.court;
  if (!court) return null;
  if (_conf.court !== court) {
    _conf.court = court;
    _conf.bounds[0] = court.confinement(TEAM.HOME, ZONE.INFIELD);
    _conf.bounds[1] = court.confinement(TEAM.HOME, ZONE.OUTFIELD);
    _conf.bounds[2] = court.confinement(TEAM.AWAY, ZONE.INFIELD);
    _conf.bounds[3] = court.confinement(TEAM.AWAY, ZONE.OUTFIELD);
  }
  return _conf.bounds[(team === TEAM.AWAY ? 2 : 0) + (zone === ZONE.OUTFIELD ? 1 : 0)];
}

/** Z of the centre line (court centred at the origin). */
export const CENTER_Z = 0;

// ------------------------------------------------------------------ targeting

/**
 * Attractiveness (0.01..~1.5) of throwing at `enemy` from `origin`; 0 = do not throw (invulnerable / out of range).
 * Prefers close, slow, committed (charging / airborne / stunned / frozen) targets and targets facing away (a ball
 * outside the frontal catch cone cannot be caught); avoids armed catchers, Thick Hide and already-doomed players.
 */
export function scoreTarget(origin, enemy, maxRange) {
  if (!enemy || hasStatus(enemy, 'invulnerable')) return 0;
  const distance = planarDistance(origin, enemy.position);
  if (distance > maxRange) return 0;

  const proximity = 1 - clamp01((distance - 4) / Math.max(1, maxRange - 4));
  const sprint = Math.max(1, movementProfile(enemy).sprintSpeed || 7.4);
  const slowness = 1 - clamp01(planarSpeedOf(enemy) / sprint);

  const cone = combatProfile(enemy).catchConeAngle || 75;
  const facingAngle = planarAngleDeg(forwardX(enemy), forwardZ(enemy), origin.x - enemy.position.x, origin.z - enemy.position.z);
  const facingAway = clamp01((facingAngle - cone * 0.6) / Math.max(1, cone * 0.8));

  let busy = 0;
  if (stateIs(enemy, STATE.CHARGING)) busy += 0.35;          // committed to a throw, slowed
  else if (stateIs(enemy, STATE.AIRBORNE)) busy += 0.3;      // cannot change direction
  else if (stateIs(enemy, STATE.SLIDING)) busy += 0.1;       // committed to a line
  else if (stateIs(enemy, STATE.STUNNED)) busy += 0.5;
  else if (stateIs(enemy, STATE.CATCHING)) busy -= 0.4;      // armed catch stance
  else if (stateIs(enemy, STATE.INCAPACITATED)) busy += 0.45; // frozen / channeling / grabbed
  if (hasStatus(enemy, 'frozen')) busy += 0.4;                // Elsa: a second hit eliminates
  if (holdsBall(enemy)) busy += 0.15;                         // hands full -> cannot catch
  if (enemy.combat && enemy.combat.catchArmed) busy -= 0.3;
  if (hasStatus(enemy, 'slippery') || hasStatus(enemy, 'dodgeDisabled')) busy += 0.15;
  if (hasStatus(enemy, 'rooted')) busy += 0.2;
  if (hasStatus(enemy, 'obscured')) busy -= 0.08;             // uncertain which body is real
  const h = enemy.health;
  if (h) {
    if (h.hp > STANDARD_HIT_DAMAGE + 0.5) busy -= 0.12;         // Thick Hide: needs two hits
    if (h.pending) busy -= 0.4;                                 // already going down (Delayed Impact)
  }
  return Math.max(0.01, 0.2 + 0.35 * proximity + 0.2 * slowness + 0.25 * facingAway + busy);
}

/**
 * Is the throw line from `origin` to `point` free of opposing hittables (Aegis Barrier, enemy clones, turrets)?
 * Own-team hittables let our balls pass. Decision-rate only (hittable.intersect may allocate).
 */
export function hasLineOfSight(self, origin, point, ballRadius) {
  const set = game.hittables;
  if (!set || set.size === 0) return true;
  for (const h of set) {
    if (!h || h.team === self.team || typeof h.intersect !== 'function') continue;
    try {
      if (h.intersect(origin, point, ballRadius)) return false;
    } catch (e) { /* a broken hittable never blocks the AI */ }
  }
  return true;
}

/** Planar world position of a hittable when it exposes one (clones: position / root / object), else null. */
export function hittablePosition(h) {
  if (!h) return null;
  const p = h.position || (h.root && h.root.position) || (h.object && h.object.position) || null;
  return p && Number.isFinite(p.x) && Number.isFinite(p.z) ? p : null;
}

/**
 * Release speed (m/s) a throw of `chargeSeconds` would have: base speed x charge curve x Rayne's Overcharge x Gale's
 * stealth bonus x perfect-catch counter boost, then Rally Boost with the 220 km/h cap. Used for lead only.
 */
export function estimateThrowSpeed(thrower, chargeSeconds, now) {
  const prof = combatProfile(thrower);
  const base = (prof.throwSpeedKmh || 88) * KMH_TO_MS;
  const full = Math.max(0.05, prof.fullChargeTime || 0.75);
  let mult = lerp(prof.minChargeMul ?? 0.72, 1, clamp01(chargeSeconds / full));
  const hero = thrower && thrower.hero;
  const passive = hero && hero.abilities && hero.abilities.passive;
  if (passive && passive.id === 'rayne.overcharge') {
    const pr = passive.params || {};
    mult *= 1 + (pr.maxSpeedBonus ?? 0.5) * clamp01(chargeSeconds / (pr.fullTime || 2));
  }
  if (hasStatus(thrower, 'cloaked') && hero && hero.id === 'Gale') {
    const sk = hero.abilities && hero.abilities.skill;
    mult *= 1 + ((sk && sk.params && sk.params.throwBonus) ?? 0.3);
  }
  const combat = thrower && thrower.combat;
  if (combat && combat.counterBoostUntil > now) mult *= 1.2;
  const rally = combat && combat.heldBall ? (combat.heldBall.rallyCount | 0) : 0;
  return RallyMath.computeSpeed(base * mult, rally);
}
