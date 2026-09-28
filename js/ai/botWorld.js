// ---------------------------------------------------------------------------------------------------------------
// Read-only game-world queries shared by the AI (bot brain, perception, ability context).
// Single-ball play: matchBall / possessionLeft / predictRestPoint / predictLanding / ballSituation summarise who owns
// the one ball (holder, dead-ball zone owner, where a live throw or a roll will end) the way a player reads the court;
// zoneAt / outfield* wrap the court's U-outfield API (world/court.js) with local fallbacks of the same math.
// Everything goes through the `game` context and is defensive about optional systems / members, because the AI must
// keep working while other modules evolve. Hot-path helpers never allocate (they write into `out` vectors and use
// cached confinement bounds); only decision-rate helpers (line of sight) may call APIs that allocate.
// ---------------------------------------------------------------------------------------------------------------
import { game } from '../game.js';
import { TEAM, ZONE, KMH_TO_MS, STANDARD_HIT_DAMAGE, GRAVITY, BALL_RADIUS, opponent } from '../core/constants.js';
import { RallyMath } from '../core/rules.js';
import { Court } from '../world/court.js';
import { heroMovement, heroCombat, BASE_MOVEMENT, BASE_COMBAT } from '../abilities/roster.js';
import { clamp, clamp01, lerp, planarAngleDeg, planarDistance } from './aiMath.js';

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
let _fallbackCourt = null;
/**
 * Cached movement confinement of (team, zone) - Court.confinement() allocates, so the four combinations are built
 * once per court instance. Before the arena exists a regulation Court stands in.
 */
export function confinementOf(team, zone) {
  const court = game.court || (_fallbackCourt || (_fallbackCourt = new Court()));
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

// ------------------------------------------------------------------ single ball & possession

/** The one match ball (BallManager.ball, else matchBalls[0]), or null. */
export function matchBall() {
  const bm = game.balls;
  if (!bm) return null;
  const b = bm.ball;
  if (b !== undefined) return b || null;
  return (bm.matchBalls && bm.matchBalls[0]) || null;
}

/** Seconds left on `team`'s running possession clock (Match.possession), Infinity when it is not their clock. */
export function possessionLeft(team) {
  const m = game.match;
  const pos = m && m.possession;
  if (!pos || pos.team !== team || !pos.running) return Infinity;
  const r = pos.remaining;
  return Number.isFinite(r) ? r : Infinity;
}

/** Possession limit of the rules (s), Infinity when disabled. */
export function possessionLimit() {
  const m = game.match;
  const lim = m && m.rules ? m.rules.possessionLimit : 0;
  return lim > 0 ? lim : Infinity;
}

/** Rolling resistance and loose-ball drag (combat/ball.js BALL_PHYS rollingDecel / freeDrag). */
export const ROLL = Object.freeze({ decel: 0.45, drag: 0.006, bounceKeep: 0.85, landingStep: 1 / 30, landingMax: 2 });

/** Planar travel (m) of a ball rolling at `speed` until it stops: integral of v dv / (decel + drag v^2). */
export function rollDistance(speed) {
  if (!(speed > 0)) return 0;
  return Math.log(1 + (ROLL.drag * speed * speed) / ROLL.decel) / (2 * ROLL.drag);
}

function _clampToHall(out) {
  const court = game.court;
  const hx = court ? court.halfW + (court.runOff || 4) - 0.2 : 8.3;
  const hz = court ? court.halfL + (court.outfieldDepth || 3) + (court.runOff || 4) - 0.2 : 15.8;
  out.x = clamp(out.x, -hx, hx); out.z = clamp(out.z, -hz, hz);
  return out;
}

/**
 * First floor contact of a ballistic ball (position, velocity, gravity scale of `ball`), stepping 1/30 s up to 2 s,
 * into `out` (clamped to the hall). Returns the time (s) or -1 when it stays airborne longer.
 */
export function predictLanding(ball, out) {
  const p = ball.position, v = ball.velocity;
  const floorY = (game.court ? game.court.floorY : 0) + (ball.radius || BALL_RADIUS);
  const g = GRAVITY * (ball.state === BALL_STATE.LIVE ? (Number.isFinite(ball.gravityScale) ? ball.gravityScale : 1) : 1);
  out.x = p.x; out.y = p.y; out.z = p.z;
  if (!v) return -1;
  for (let t = ROLL.landingStep; t <= ROLL.landingMax + 1e-9; t += ROLL.landingStep) {
    const y = p.y + v.y * t - 0.5 * g * t * t;
    out.x = p.x + v.x * t; out.z = p.z + v.z * t; out.y = y;
    if (y <= floorY && v.y - g * t < 0) { out.y = floorY; _clampToHall(out); return t; }
  }
  _clampToHall(out);
  return -1;
}

/**
 * Where the ball will come to rest (planar, into `out`): held -> the holder; loose and rolling -> v|v|-based roll
 * distance; airborne (loose or live) -> landing point plus the roll of the kept planar speed. Clamped to the hall.
 */
export function predictRestPoint(ball, out) {
  const p = ball.position;
  out.x = p.x; out.y = p.y; out.z = p.z;
  const st = ball.state;
  if (st === BALL_STATE.HELD && ball.holder && ball.holder.position) {
    const hp = ball.holder.position;
    out.x = hp.x; out.y = hp.y; out.z = hp.z;
    return out;
  }
  if (st !== BALL_STATE.FREE && st !== BALL_STATE.LIVE) return out;
  const v = ball.velocity;
  if (!v) return out;
  const floorY = (game.court ? game.court.floorY : 0) + (ball.radius || BALL_RADIUS);
  let vx = v.x, vz = v.z;
  if (st === BALL_STATE.LIVE || p.y > floorY + 0.08 || Math.abs(v.y) > 0.6) {
    predictLanding(ball, out);
    vx *= ROLL.bounceKeep; vz *= ROLL.bounceKeep;
  }
  const sp = Math.hypot(vx, vz);
  if (sp > 0.05) { const d = rollDistance(sp); out.x += (vx / sp) * d; out.z += (vz / sp) * d; }
  return _clampToHall(out);
}

// ------------------------------------------------------------------ court zones & the U outfield path

/**
 * Zone { team, zone } of a planar point on the painted lines (court.zoneAt when available, else the same rule):
 * inside the court -> that half's INFIELD; inside the outer boundary -> the OUTFIELD of the team whose U it is (the
 * U surrounds the opponent's half: z >= 0 belongs to Home). Returns `out`, or null for unreachable spots.
 */
export function zoneAt(p, out, margin = 0) {
  const court = game.court;
  if (court && typeof court.zoneAt === 'function') return court.zoneAt(p, out, margin);
  const hw = court ? court.halfW : 4.5, hl = court ? court.halfL : 9;
  const sw = court && court.sideOutfieldWidth > 0 ? court.sideOutfieldWidth : 2.5;
  const d = court && court.outfieldDepth > 0 ? court.outfieldDepth : 3;
  const ax = Math.abs(p.x), az = Math.abs(p.z);
  if (ax <= hw && az <= hl) { out.team = p.z < 0 ? TEAM.HOME : TEAM.AWAY; out.zone = ZONE.INFIELD; return out; }
  if (ax <= hw + sw + margin && az <= hl + d + margin) { out.team = p.z >= 0 ? TEAM.HOME : TEAM.AWAY; out.zone = ZONE.OUTFIELD; return out; }
  return null;
}

// Local fallback of the U centre line (world/court.js outfieldPoint): A(-xs, s z0) -> B(-xs, s zb) -> C(xs, s zb) ->
// D(xs, s z0), arc-length parametrised; s = +1 for Home's U (on the Away side).
const OUTFIELD_FRONT_INSET = 0.8;
function _uDims() {
  const court = game.court;
  const hw = court ? court.halfW : 4.5, hl = court ? court.halfL : 9;
  const sw = court && court.sideOutfieldWidth > 0 ? court.sideOutfieldWidth : 2.5;
  const d = court && court.outfieldDepth > 0 ? court.outfieldDepth : 3;
  _ud.xs = hw + sw * 0.5; _ud.zb = hl + d * 0.5; _ud.z0 = OUTFIELD_FRONT_INSET;
  _ud.arm = _ud.zb - _ud.z0; _ud.back = 2 * _ud.xs; _ud.L = 2 * _ud.arm + _ud.back;
  return _ud;
}
const _ud = { xs: 0, zb: 0, z0: 0, arm: 0, back: 0, L: 1 };
const _uSide = (team) => (team === TEAM.HOME ? 1 : -1);

/** Arc length (m) of a team's U centre line (30.9 m on the regulation court). */
export function outfieldPathLength() {
  const court = game.court;
  if (court && Number.isFinite(court.outfieldPathLength)) return court.outfieldPathLength;
  return _uDims().L;
}

/** Point at u in [0, 1] along `team`'s U centre line into `out` (u = 0 is always the -X end). */
export function outfieldPoint(team, u, out) {
  const court = game.court;
  if (court && typeof court.outfieldPoint === 'function') { court.outfieldPoint(team, u, out); return out; }
  const g = _uDims(), s = _uSide(team);
  let d = clamp01(u) * g.L;
  if (d <= g.arm) { out.x = -g.xs; out.z = s * (g.z0 + d); } else if ((d -= g.arm) <= g.back) { out.x = -g.xs + d; out.z = s * g.zb; } else { d -= g.back; out.x = g.xs; out.z = s * (g.zb - d); }
  out.y = court ? court.floorY : 0;
  return out;
}

/** u of the nearest point of `team`'s U centre line to planar `p`. */
export function outfieldParam(team, p) {
  const court = game.court;
  if (court && typeof court.outfieldParam === 'function') return court.outfieldParam(team, p);
  const g = _uDims(), s = _uSide(team);
  const z = p.z * s; // mirrored into the +Z frame
  // Left arm, back, right arm: nearest point on each segment, keep the best.
  const zl = clamp(z, g.z0, g.zb), dl = Math.hypot(p.x + g.xs, z - zl);
  const xb = clamp(p.x, -g.xs, g.xs), db = Math.hypot(p.x - xb, z - g.zb);
  const dr = Math.hypot(p.x - g.xs, z - zl);
  let d;
  if (dl <= db && dl <= dr) d = zl - g.z0;
  else if (db <= dr) d = g.arm + (xb + g.xs);
  else d = g.arm + g.back + (g.zb - zl);
  return clamp01(d / g.L);
}

/**
 * Steering target that walks `team`'s U from `p` toward `targetU` around the hole corners (never straight across the
 * opponent's half): the path point at most `lookAhead` metres of arc from the current u.
 */
export function outfieldWaypoint(team, p, targetU, lookAhead, out) {
  const court = game.court;
  if (court && typeof court.outfieldWaypoint === 'function') { court.outfieldWaypoint(team, p, targetU, lookAhead, out); return out; }
  const L = outfieldPathLength();
  const u0 = outfieldParam(team, p);
  const du = clamp(targetU - u0, -lookAhead / L, lookAhead / L);
  return outfieldPoint(team, u0 + du, out);
}

// ------------------------------------------------------------------ ball situation

const _zs = { team: TEAM.NONE, zone: ZONE.INFIELD };
/** Empty ball-situation record for ballSituation(). */
export function createBallSituation(Vec) {
  return {
    ball: null, state: null, holder: null, ownerTeam: TEAM.NONE, ownerZone: null, ours: false, theirs: false,
    restPoint: new Vec(), landing: new Vec(), hasLanding: false, left: Infinity, isPass: false, thrower: null,
  };
}

/**
 * Who has the one ball, from `self`'s point of view, into `out`:
 *   HELD      owner = the holder's team (ours when a teammate holds it)
 *   FREE      owner = court.zoneAt(rest point) (dead-ball rule: the zone it stops in retrieves it); null = ball boy
 *   LIVE      ours when our thrower's ball will land in one of our zones (restPoint / landing predicted)
 *   STASIS / DESPAWNED: owner NONE (Chrono's field, award or ball boy pending)
 * `left` = seconds on our running possession clock (Infinity when it is not ours).
 */
export function ballSituation(self, out) {
  const ball = matchBall();
  out.ball = ball; out.state = ball ? ball.state : null; out.holder = null; out.thrower = null; out.isPass = false;
  out.ownerTeam = TEAM.NONE; out.ownerZone = null; out.ours = false; out.theirs = false; out.hasLanding = false;
  out.left = self ? possessionLeft(self.team) : Infinity;
  if (!ball || !self) return out;
  const st = ball.state;
  if (st === BALL_STATE.HELD && ball.holder) {
    out.holder = ball.holder;
    out.ownerTeam = ball.holder.team; out.ownerZone = ball.holder.zone;
    out.restPoint.copy(ball.holder.position);
  } else if (st === BALL_STATE.FREE) {
    predictRestPoint(ball, out.restPoint);
    const z = zoneAt(out.restPoint, _zs);
    if (z) { out.ownerTeam = z.team; out.ownerZone = z.zone; }
  } else if (st === BALL_STATE.LIVE) {
    out.thrower = ball.lastThrower || null;
    out.isPass = !!ball.isPass;
    out.hasLanding = predictLanding(ball, out.landing) >= 0;
    predictRestPoint(ball, out.restPoint);
    const z = zoneAt(out.hasLanding ? out.landing : out.restPoint, _zs);
    if (z) { out.ownerTeam = z.team; out.ownerZone = z.zone; }
    if (out.thrower && out.thrower.team === self.team) {
      // Our throw: ours while it is a pass or will come down in our own zones; otherwise it is contested.
      out.ours = out.isPass || out.ownerTeam === self.team;
      out.theirs = false;
      return out;
    }
    if (out.thrower) { out.theirs = out.isPass || out.ownerTeam !== self.team; return out; }
  } else {
    out.restPoint.copy(ball.position);
    const cust = ball.custodyTeam;
    if (Number.isFinite(cust) && cust !== TEAM.NONE) out.ownerTeam = cust;
    else if (ball.reservedTeam !== undefined && ball.reservedTeam !== TEAM.NONE && Number.isFinite(ball.reservedTeam)) out.ownerTeam = ball.reservedTeam;
  }
  out.ours = out.ownerTeam === self.team;
  out.theirs = out.ownerTeam === opponent(self.team);
  return out;
}
