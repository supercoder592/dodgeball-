// ---------------------------------------------------------------------------------------------------------------
// Player - one dodgeball athlete: identity, transform, components and the per-frame pipeline (web port of the Unity
// DodgeballPlayer). Players are NOT game systems: Match calls update()/fixedUpdate() for each of them.
//
//   update(dt, realDt):  sample intent -> status -> fsm -> (skill / ultimate / pass / pickup) -> combat -> abilities
//                        -> health -> avatar
//   fixedUpdate(dt):     fsm -> motor -> history.record
//
// The intent source (HumanController / Bot) is always sampled so camera look and AI perception keep running, but its
// commands are replaced by a neutral intent while the player cannot act (stunned, incapacitated, inputLocked);
// only look/aim/target survive.
// Transform: `root` (THREE.Group in game.scene) holds feet position (`position` aliases root.position) and yaw
// (root.rotation.y; yaw 0 faces +Z). The avatar hangs under root.
// ---------------------------------------------------------------------------------------------------------------
import * as THREE from 'three';
import { game } from '../game.js';
import { EV } from '../core/events.js';
import { ZONE, TEAM, DEFAULT_MAX_HP } from '../core/constants.js';
import { heroMovement, heroCombat } from '../abilities/roster.js';
import { AbilityController } from '../abilities/abilityController.js';
import * as CombatModule from '../combat/combat.js';
import * as AvatarModule from '../characters/avatar.js';
import { RewindHistory } from './history.js';
import { Motor } from './motor.js';
import { PlayerStateMachine, STATE, INCAP } from './states.js';
import { Health } from './health.js';
import { Status } from './status.js';
import { wrapAngle } from './player_math.js';

/** Player-level tuning. */
export const PLAYER_TUNING = Object.freeze({
  radius: 0.32,              // capsule radius (m)
  defaultHeight: 1.8,        // when the hero has no height
  chestHeightFrac: 0.72,     // chest = feet + height * frac
  headHeightFrac: 0.93,
  aimPointDistance: 10,      // neutral aim point distance (m)
  historySeconds: 6,         // rewind buffer (Chrono needs 3 s, Specter 3 s; margin for latency)
  historyRate: 30,           // snapshots per second
  landFeedbackMinSpeed: 3.2, // m/s vertical impact before dust + thud
  landFeedbackMaxSpeed: 8,
});

const _up = new THREE.Vector3(0, 1, 0);

/**
 * Fills `out` (or a new object) with a neutral intent for `player`: no movement, no buttons, aiming straight ahead.
 * @param {Player|null} player
 * @param {object} [out]
 */
export function neutralIntent(player, out = null) {
  const it = out || {
    move: new THREE.Vector3(), aimDir: new THREE.Vector3(0, 0, 1), aimPoint: new THREE.Vector3(), target: null,
    sprint: false, jump: false, slide: false, throwPressed: false, throwHeld: false, throwReleased: false,
    catchPressed: false, pass: false, pickup: false, skill: false, ultimate: false, cycleTarget: false,
  };
  it.move.set(0, 0, 0);
  const yaw = player && Number.isFinite(player.yaw) ? player.yaw : 0;
  it.aimDir.set(Math.sin(yaw), 0, Math.cos(yaw));
  if (player && player.position) it.aimPoint.copy(player.position).addScaledVector(it.aimDir, PLAYER_TUNING.aimPointDistance);
  else it.aimPoint.copy(it.aimDir).multiplyScalar(PLAYER_TUNING.aimPointDistance);
  it.aimPoint.y += 1.3;
  it.target = null;
  it.sprint = it.jump = it.slide = false;
  it.throwPressed = it.throwHeld = it.throwReleased = false;
  it.catchPressed = it.pass = it.pickup = it.skill = it.ultimate = it.cycleTarget = false;
  return it;
}

const finiteVec = (v) => !!v && Number.isFinite(v.x) && Number.isFinite(v.y) && Number.isFinite(v.z);

export class Player {
  /**
   * @param {{id:number, name?:string, team:number, hero:object, isHuman?:boolean, isLocal?:boolean,
   *          intentSource?:{sample:(player:Player, dt:number)=>object}, slot?:number}} opts
   */
  constructor({ id, name, team, hero, isHuman = false, isLocal = false, intentSource = null, slot = 0 } = {}) {
    this.id = id;
    this.hero = hero || null;
    this.name = name || (hero && hero.id) || `Player ${id}`;
    this.team = Number.isFinite(team) ? team : TEAM.NONE;
    this.slot = slot | 0;
    this.isHuman = !!isHuman;
    this.isLocal = !!isLocal;
    this.intentSource = intentSource;
    this.zone = ZONE.INFIELD;
    /** Set by Match during countdowns / breaks and while ragdolling: commands are ignored. */
    this.inputLocked = false;

    /** Transform root (feet at origin, yaw only). Added to the scene in init(). */
    this.root = new THREE.Group();
    this.root.name = `Player_${id}_${this.name}`;
    this.root.rotation.order = 'YXZ';
    this.root.userData.player = this;
    /** World velocity (m/s) - shared with and owned by the Motor (see the `velocity` accessor). */
    this._velocity = new THREE.Vector3();

    this.radius = PLAYER_TUNING.radius;
    /** Standing capsule height (m); `height` includes the slide crouch. */
    this.baseHeight = (hero && hero.height) || PLAYER_TUNING.defaultHeight;

    /** @type {Motor} */ this.motor = null;
    /** @type {PlayerStateMachine} */ this.fsm = null;
    /** @type {any} */ this.combat = null;
    /** @type {Health} */ this.health = null;
    /** @type {Status} */ this.status = null;
    /** @type {AbilityController} */ this.abilities = null;
    /** @type {any} */ this.avatar = null;
    /** @type {RewindHistory} */ this.history = null;

    /** Last sampled (sanitised) intent. Persistent object: read, do not keep references to its vectors. */
    this.intent = neutralIntent(this);
    this.initialized = false;

    this._forward = new THREE.Vector3(0, 0, 1);
    this._chest = new THREE.Vector3();
    this._head = new THREE.Vector3();
    this._snapshot = { position: this.root.position, velocity: this._velocity, yaw: 0, hp: 0, state: '' };
    this._teleportFrame = -1;
    this._errors = new Set();
    this._slideVfx = null;
    this._avatarReady = false;
  }

  // ------------------------------------------------------------------ transform readouts
  /** Feet position (alias of root.position). Assigning copies (the alias is never replaced). */
  get position() { return this.root.position; }
  set position(v) { if (finiteVec(v)) this.root.position.copy(v); }
  /** World velocity (m/s), the Motor's vector. Assigning copies (never replaces the shared vector). */
  get velocity() { return this._velocity; }
  set velocity(v) { if (finiteVec(v)) this._velocity.copy(v); }
  /** Body yaw (radians, 0 faces +Z). */
  get yaw() { return this.root.rotation.y; }
  set yaw(v) { this.root.rotation.y = wrapAngle(v); }
  /** Planar unit facing. Per-player cached vector recomputed on every read: clone it to keep it. */
  get forward() { const y = this.root.rotation.y; return this._forward.set(Math.sin(y), 0, Math.cos(y)); }
  /** Effective capsule height (lower while sliding). */
  get height() { return this.baseHeight * (this.motor ? this.motor.heightScale : 1); }
  /** Setting the height changes the standing height (e.g. measured from the loaded model). */
  set height(v) { if (v > 0.5 && v < 3) this.baseHeight = v / (this.motor && this.motor.heightScale > 0 ? this.motor.heightScale : 1); }
  /** World chest point (throw origin height, hit-point default). Cached vector: clone to keep. */
  get chestPosition() { return this._chest.copy(this.root.position).addScaledVector(_up, this.height * PLAYER_TUNING.chestHeightFrac); }
  /** World head point. Cached vector: clone to keep. */
  get headPosition() { return this._head.copy(this.root.position).addScaledVector(_up, this.height * PLAYER_TUNING.headHeightFrac); }
  get isGrounded() { return !!this.motor && this.motor.isGrounded; }

  // ------------------------------------------------------------------ gameplay queries
  /** Can be hit by enemy balls: infield, alive (not pending) and not mid-elimination. */
  get isTargetable() {
    return this.initialized && this.zone === ZONE.INFIELD && !!this.health && this.health.isAlive &&
      !(this.fsm && this.fsm.incapReason === INCAP.ELIMINATED);
  }
  /** Commands are accepted: not stunned/incapacitated and not input-locked. */
  get canAct() { return this.initialized && !!this.fsm && this.fsm.canAct && !this.inputLocked; }
  /** Can receive a pass (infield or outfield): not eliminated-ragdolling/pending, not locked, empty hands. */
  get canReceive() {
    if (!this.initialized || !this.health || this.health.pending) return false;
    if (this.fsm && this.fsm.is(STATE.INCAPACITATED)) return false;
    if (this.zone === ZONE.INFIELD && !this.health.isAlive) return false;
    return !(this.combat && this.combat.hasBall);
  }
  /** Body takes part in player-player separation. */
  get collidable() {
    return this.initialized && !(this.fsm && (this.fsm.incapReason === INCAP.ELIMINATED || this.fsm.incapReason === INCAP.GRABBED));
  }
  get isInfield() { return this.zone === ZONE.INFIELD; }

  // ------------------------------------------------------------------ lifecycle
  /** Builds components, loads the realistic avatar, equips abilities, registers and emits PlayerSpawned. */
  async init() {
    const hero = this.hero || {};
    if (game.scene && !this.root.parent) game.scene.add(this.root);

    // Components (order matters: later ones read earlier ones).
    this.motor = new Motor(this, heroMovement(hero));
    this.status = new Status(this);
    this.health = new Health(this, Number.isFinite(hero.maxHp) ? hero.maxHp : DEFAULT_MAX_HP);
    const CombatCls = CombatModule.Combat || CombatModule.default;
    this.combat = typeof CombatCls === 'function' ? new CombatCls(this, heroCombat(hero)) : null;
    if (!this.combat) console.error('[player] combat/combat.js exports no Combat class');
    this.abilities = new AbilityController(this);
    this.fsm = new PlayerStateMachine(this);
    this.history = new RewindHistory(() => this._capture(), PLAYER_TUNING.historySeconds, PLAYER_TUNING.historyRate);
    this._hookMotor();

    // Realistic human (Rocketbox). Gameplay keeps working if an art asset is broken.
    const AvatarCls = AvatarModule.Avatar || AvatarModule.default;
    if (typeof AvatarCls === 'function') {
      try {
        this.avatar = new AvatarCls(this);
        await this.avatar.load();
        if (this.avatar.root && this.avatar.root.parent !== this.root) this.root.add(this.avatar.root);
        this._avatarReady = true;
      } catch (e) {
        console.error(`[player] avatar load failed for ${hero.id}`, e);
      }
    } else {
      console.error('[player] characters/avatar.js exports no Avatar class');
    }

    // Abilities last: passives may hook health/combat/status/avatar on equip.
    try {
      this.abilities.setup(hero);
    } catch (e) {
      console.error(`[player] ability setup failed for ${hero.id}`, e);
    }

    this.setZone(ZONE.INFIELD);
    this.intent = neutralIntent(this, this.intent);
    this.initialized = true;
    game.registerPlayer(this);
    game.events.emit(EV.PlayerSpawned, { player: this });
    return this;
  }

  /** Per-frame pipeline (scaled dt for gameplay, realDt for the avatar's juice/IK). */
  update(dt, realDt) {
    if (!this.initialized) return;
    this._sampleIntent(dt);
    this._call(this.status, 'update', dt);
    this._call(this.fsm, 'update', dt);
    if (this.canAct) {
      const it = this.intent;
      if (it.skill) this._call(this.abilities, 'tryUseSkill');
      if (it.ultimate) this._call(this.abilities, 'tryUseUltimate');
      // Pass (Houdini's Hat Trick hooks in through combat.passHandler): a pass mid wind-up gives the ball away and
      // ChargingThrow notices the empty hand. Manual pick-up grabs the nearest reachable free ball.
      if (it.pass) this._call(this.combat, 'tryPass');
      if (it.pickup) this._call(this.combat, 'tryPickupNearest');
    }
    this._call(this.combat, 'update', dt);
    this._call(this.abilities, 'update', dt);
    this._call(this.health, 'update', dt);
    if (this._avatarReady) this._call(this.avatar, 'update', dt, realDt);
  }

  /** Fixed 60 Hz step on scaled time. */
  fixedUpdate(dt) {
    if (!this.initialized) return;
    this._call(this.fsm, 'fixedUpdate', dt);
    this._call(this.motor, 'fixedUpdate', dt);
    if (this.history) this.history.record(game.time.now);
  }

  /** Moves between infield and outfield: confinement follows; emits PlayerZone on change. */
  setZone(zone) {
    const changed = this.zone !== zone;
    this.zone = zone;
    this._applyConfinement();
    // An eliminated player placed in the outfield (teleported this same frame) plays on from there.
    if (zone === ZONE.OUTFIELD && this._teleportFrame === game.time.frame) this._releaseEliminationLock();
    if (changed) game.events.emit(EV.PlayerZone, { player: this, zone });
  }

  /** Instant relocation (spawns, outfield transfer, swaps, rewinds). Clears the rewind history. */
  teleport(pos, yaw) {
    if (this.motor) this.motor.teleport(pos, yaw);
    else {
      if (finiteVec(pos)) this.root.position.copy(pos);
      if (Number.isFinite(yaw)) this.yaw = yaw;
    }
    // Rewinds must never cross a relocation.
    if (this.history) this.history.clear();
    this._teleportFrame = game.time.frame;
    // Arriving in the outfield after an elimination: the ragdoll phase is over.
    if (this.zone === ZONE.OUTFIELD) this._releaseEliminationLock();
  }

  /** New round: everything back to a fresh, standing, infield player at `pos` facing `yaw`. */
  resetForRound(pos, yaw) {
    // Effects first: removing 'frozen' releases its incapacitation; passive markers (silentFootsteps) stay.
    this._call(this.status, 'clearForReset');
    this._call(this.combat, 'resetForRound');
    if (this.abilities) this._call(this.abilities, 'resetForRound', false); // the ultimate meter carries over
    this._call(this.health, 'resetForRound');
    this._call(this.fsm, 'resetToGrounded');
    if (this._avatarReady) this._call(this.avatar, 'resetVisual');
    const m = this.motor;
    if (m) {
      m.setFrozen(false);
      if (m.isSliding) m.endSlide();
      m.setMode('walk');
      m.setMove(null, 0);
      m.control = 1;
    }
    this.teleport(pos, yaw);
    this.setZone(ZONE.INFIELD);
    this.intent = neutralIntent(this, this.intent);
  }

  dispose() {
    if (this._slideVfx) { game.vfx?.stop?.(this._slideVfx); this._slideVfx = null; }
    this._call(this.abilities, 'teardown');
    this._call(this.status, 'dispose');
    this._call(this.combat, 'dispose');
    if (this.avatar) this._call(this.avatar, 'dispose');
    if (this.motor) { this.motor.onLand = this.motor.onJump = this.motor.onSlideStart = this.motor.onSlideEnd = null; }
    if (this.root.parent) this.root.parent.remove(this.root);
    game.unregisterPlayer(this);
    this.initialized = false;
    this._avatarReady = false;
  }

  // ------------------------------------------------------------------ allocation-free readouts
  getForward(out) { const y = this.root.rotation.y; return out.set(Math.sin(y), 0, Math.cos(y)); }
  getChestPosition(out) { return out.copy(this.root.position).addScaledVector(_up, this.height * PLAYER_TUNING.chestHeightFrac); }
  getHeadPosition(out) { return out.copy(this.root.position).addScaledVector(_up, this.height * PLAYER_TUNING.headHeightFrac); }

  // ------------------------------------------------------------------ internals
  /** Samples the intent source into this.intent (neutral commands when the player cannot act). */
  _sampleIntent(dt) {
    let src = null;
    const source = this.intentSource;
    if (source && typeof source.sample === 'function') {
      try { src = source.sample(this, dt); } catch (e) { this._report('intentSource.sample', e); }
    }
    const it = neutralIntent(this, this.intent);
    if (!src) return;
    // Look / aim / target always follow the source (camera, AI perception, head LookAt).
    // (Foreign vectors may be plain {x,y,z}: only read their components.)
    const aim = src.aimDir;
    const aimOk = finiteVec(aim) && aim.x * aim.x + aim.y * aim.y + aim.z * aim.z > 1e-8;
    if (aimOk) it.aimDir.set(aim.x, aim.y, aim.z).normalize();
    if (finiteVec(src.aimPoint)) it.aimPoint.set(src.aimPoint.x, src.aimPoint.y, src.aimPoint.z);
    else if (aimOk) this.getChestPosition(it.aimPoint).addScaledVector(it.aimDir, PLAYER_TUNING.aimPointDistance);
    it.target = src.target && src.target !== this ? src.target : null;
    if (!this.canAct) return;
    const mv = src.move;
    if (finiteVec(mv)) {
      it.move.set(mv.x, 0, mv.z);
      const len = it.move.length();
      if (len > 1) it.move.multiplyScalar(1 / len);
    }
    it.sprint = !!src.sprint; it.jump = !!src.jump; it.slide = !!src.slide;
    it.throwPressed = !!src.throwPressed; it.throwHeld = !!src.throwHeld; it.throwReleased = !!src.throwReleased;
    it.catchPressed = !!src.catchPressed; it.pass = !!src.pass; it.pickup = !!src.pickup;
    it.skill = !!src.skill; it.ultimate = !!src.ultimate; it.cycleTarget = !!src.cycleTarget;
  }

  _applyConfinement() {
    if (!this.motor || !game.court || (this.team !== TEAM.HOME && this.team !== TEAM.AWAY)) return;
    this.motor.setConfinement(game.court.confinement(this.team, this.zone));
  }

  _releaseEliminationLock() {
    const fsm = this.fsm;
    if (!fsm || !fsm.is(STATE.INCAPACITATED) || fsm.incapReason !== INCAP.ELIMINATED) return;
    fsm.release(INCAP.ELIMINATED);
  }

  /** RewindHistory snapshot (reused object; history copies the values). */
  _capture() {
    const s = this._snapshot;
    s.yaw = this.yaw;
    s.hp = this.health ? this.health.hp : 0;
    s.state = this.fsm ? this.fsm.current : '';
    return s;
  }

  // ---- movement feedback (sound + dust); silent for Gale's Silent Footsteps
  _hookMotor() {
    const m = this.motor;
    // Positions are cloned: these are rare events and fx systems may keep the vector.
    m.onJump = () => {
      if (!this._silent) game.audio?.play?.('jump', this.position.clone(), 0.45, 1);
    };
    m.onLand = (speed) => {
      const t = PLAYER_TUNING;
      if (speed < t.landFeedbackMinSpeed) return;
      const k = Math.min(1, (speed - t.landFeedbackMinSpeed) / (t.landFeedbackMaxSpeed - t.landFeedbackMinSpeed));
      game.vfx?.play?.('floorDust', this.position.clone(), { scale: 0.6 + 0.7 * k });
      if (!this._silent) game.audio?.play?.('land', this.position.clone(), 0.35 + 0.65 * k, 1.05 - 0.15 * k);
    };
    m.onSlideStart = () => {
      const vfx = game.vfx;
      if (vfx && vfx.attach) {
        if (this._slideVfx && vfx.stop) vfx.stop(this._slideVfx);
        this._slideVfx = vfx.attach('slideDust', this.root, { duration: Math.max(0.2, m.slideRemaining) }) || null;
      }
      if (!this._silent) game.audio?.play?.('slide', this.position.clone(), 0.7, 1);
    };
    m.onSlideEnd = () => {
      if (this._slideVfx) { game.vfx?.stop?.(this._slideVfx); this._slideVfx = null; }
    };
  }

  get _silent() { return !!(this.status && this.status.has('silentFootsteps')); }

  /** Calls obj[method](a, b) with per-method error isolation (one broken module must not stall the player). */
  _call(obj, method, a, b) {
    if (!obj || typeof obj[method] !== 'function') return undefined;
    try { return obj[method](a, b); } catch (e) { this._report(`${obj.constructor ? obj.constructor.name : 'obj'}.${method}`, e); }
    return undefined;
  }

  _report(key, e) {
    if (this._errors.has(key)) return;
    this._errors.add(key);
    console.error(`[player ${this.id} ${this.hero && this.hero.id}] ${key} threw`, e);
  }
}

export default Player;
