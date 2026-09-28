// ---------------------------------------------------------------------------------------------------------------
// Vfx (system, owner: fx) - pooled GPU particles + mesh effects for Dodgeball Ultra. Contract: WEB_ARCHITECTURE.md §3.9
//
//   game.vfx.play(id, position, { scale, color, direction, radius, size, duration, style, target })
//   const h = game.vfx.attach(id, object3D | player | ball, { duration, color, scale, radius, size, offset })
//   game.vfx.stop(h)
//
// Ids (effects.js): hit catch perfectCatch floorDust slideDust shockwave fireTrail beamTrail iceBurst iceTrail frozenMist
// glueSplat teleport swapFlash vanishSmoke cloneSpawn cloneDissolve magnetField shieldImpact tackleDust earthquake
// turretMuzzle stasisBubble rewindTrail temporalZone reviveBeam ultReady elimination (+ cloak, glueTrail, aliases).
//
// Event driven: BallHitPlayer -> hit, BallCaught -> catch / perfectCatch, BallBounced (floor > 6 m/s, walls > 8 m/s)
// -> floorDust, PlayerEliminated -> elimination, PlayerRevived -> reviveBeam, UltCharge becameReady -> ultReady aura,
// PlayerState airborne->grounded -> landing dust, sliding -> slide dust (+ polled trail while the slide lasts).
//
// Time: effects live in the world, so they run on SCALED dt (hitstop freezes the spark mid-air, pause freezes
// everything, round-end slow motion slows smoke) - the camera/juice/audio stay on real time.
// Rendering: two InstancedMesh particle layers (alpha, additive) + shape pools, all in one `vfx` group, excluded from
// AO override passes, fog/tonemapping aware. Nothing allocates per frame; attach() allocates one tiny handle.
// ---------------------------------------------------------------------------------------------------------------
import * as THREE from 'three';
import { game } from '../game.js';
import { EV } from '../core/events.js';
import { TEAM_COLORS } from '../core/constants.js';
import { createParticleAtlas } from './textures.js';
import { ParticleLayer } from './particles.js';
import { ShapePool, RibbonPool, LightPool } from './shapes.js';
import { EFFECTS, EFFECT_ALIASES, EFFECT_KEYWORDS } from './effects.js';

/** Tuning (per quality where it matters). */
export const VFX_TUNING = Object.freeze({
  particleCapacity: { high: 2600, medium: 1800, low: 1000 },
  emission: { high: 1, medium: 0.75, low: 0.45 },
  lights: { high: 3, medium: 2, low: 0 },
  rings: 12, spheres: 4, columns: 5, ribbons: 8, emitters: 48,
  /** Floor bounces below this impact speed (m/s) leave no dust (rolling balls). */
  floorDustMinSpeed: 6,
  /** Wall bounces below this impact speed leave no scuff puff. */
  wallDustMinSpeed: 8,
  /** Metres slid between two slide dust puffs. */
  slideDustSpacing: 0.35,
  /** Duplicate play() of the same id within this real-time window and distance is dropped (event + ability overlap). */
  dedupeWindow: 0.06,
  /** A target that jumps farther than this per frame (+ 70 m/s * dt) teleported: trails restart instead of streaking. */
  teleportJump: 0.5,
  /** Visual scale from ball speed: scale = speedKmh / hitScaleRef (clamped). */
  hitScaleRef: 90,
});

const _pos = new THREE.Vector3();
const _white = new THREE.Color(0xffffff);

/** One live attached / stationary effect instance. */
class Emitter {
  constructor() {
    this.active = false; this.gen = 0; this.def = null; this.id = '';
    this.target = null; this.player = null; this.ball = null; this.isBall = false; this.onPlayer = false; this.wasLive = false;
    this.pos = new THREE.Vector3(); this.prev = new THREE.Vector3(); this.vel = new THREE.Vector3();
    this.dir = new THREE.Vector3(0, 1, 0); this.offset = new THREE.Vector3(); this.staticPos = new THREE.Vector3();
    this.color = new THREE.Color(); this.colA = new THREE.Color(); this.colB = new THREE.Color();
    this.hasColor = false; this.age = 0; this.duration = 1; this.scale = 1; this.radius = 0; this.size = 0;
    this.moved = 0; this.acc = 0; this.acc2 = 0; this.acc3 = 0; this.paused = false;
    this.light = null; this.ribbons = [null, null]; this.shapes = [null, null, null];
  }
}

export class Vfx {
  static defaults = VFX_TUNING;

  constructor() {
    this.ready = false;
    this.enabled = true;
    this.time = 0;
    this.q = 1;
    this.floorY = 0;
    this.root = null;
    this.A = null; this.X = null;
    this.ringsA = null; this.ringsX = null; this.spheres = null; this.columns = null; this.ribbons = null; this.lights = null;
    this._emitters = [];
    this._unsubs = [];
    this._camPos = new THREE.Vector3(0, 5, 10);
    this._camQuat = new THREE.Quaternion();
    this._o = {
      scale: 1, color: new THREE.Color(), hasColor: false, dir: new THREE.Vector3(0, 1, 0), hasDir: false,
      radius: 0, size: 0, duration: 0, style: null, target: null,
    };
    this._targetV = new THREE.Vector3();
    this._evOpts = { scale: 1, color: null, direction: null, style: null };
    this._evDir = new THREE.Vector3();
    this._evPos = new THREE.Vector3();
    // dedupe ring buffer (id, x, y, z, realTime)
    this._recentId = new Array(24).fill('');
    this._recent = new Float32Array(24 * 4);
    this._recentHead = 0;
    this._warned = new Set();
    this._resolved = new Map();
    /** player -> { aura handle, slide metres, wasSliding } */
    this._pstate = new Map();
  }

  async init() {
    if (!game.scene) { console.warn('[vfx] no scene; effects disabled'); return; }
    const quality = (game.renderer && game.renderer.quality) || (game.config && game.config.quality) || 'high';
    const T = VFX_TUNING;
    this.quality = quality;
    this.q = T.emission[quality] ?? 1;
    this.floorY = game.court ? game.court.floorY : 0;

    this.root = new THREE.Group();
    this.root.name = 'vfx';
    const atlas = createParticleAtlas();
    this.atlas = atlas;
    const cap = T.particleCapacity[quality] ?? T.particleCapacity.high;
    this.A = new ParticleLayer({ name: 'alpha', capacity: cap, additive: false, atlas, renderOrder: 10 });
    this.X = new ParticleLayer({ name: 'additive', capacity: cap, additive: true, atlas, renderOrder: 11 });
    this.ringsA = new ShapePool('ring', T.rings, false, 5);
    this.ringsX = new ShapePool('ring', T.rings, true, 6);
    this.spheres = new ShapePool('sphere', T.spheres, true, 12);
    this.columns = new ShapePool('column', T.columns, true, 12);
    this.ribbons = new RibbonPool(T.ribbons, 12);
    this.lights = new LightPool(T.lights[quality] ?? 0);
    this.root.add(this.ringsA.group, this.ringsX.group, this.A.mesh, this.X.mesh, this.spheres.group, this.columns.group,
      this.ribbons.group, this.lights.group);
    game.scene.add(this.root);
    for (let i = 0; i < T.emitters; i++) this._emitters.push(new Emitter());

    this._precompile();

    const on = (name, fn) => this._unsubs.push(game.events.on(name, fn));
    on(EV.BallHitPlayer, (e) => this._onBallHit(e));
    on(EV.BallCaught, (e) => this._onCaught(e));
    on(EV.BallBounced, (e) => this._onBounced(e));
    on(EV.PlayerEliminated, (e) => this._onEliminated(e));
    on(EV.PlayerRevived, (e) => this._onRevived(e));
    on(EV.UltCharge, (e) => this._onUltCharge(e));
    on(EV.PlayerState, (e) => this._onPlayerState(e));
    on(EV.MatchStarted, () => this.stopAll(true));
    this.ready = true;
  }

  /** Compiles every effect shader during boot so the first hit of the match does not hitch. */
  _precompile() {
    const r = game.renderer && game.renderer.three;
    if (!r || !r.compile || !game.camera) return;
    const hidden = [];
    this.root.traverse((o) => { if (o.isMesh && !o.visible) { hidden.push(o); o.visible = true; } });
    try { r.compile(this.root, game.camera, game.scene); } catch (e) { /* optional optimisation */ }
    for (const o of hidden) o.visible = false;
  }

  // ------------------------------------------------------------------------------------------------ public API
  /**
   * One-shot effect at a world position. Emitter-type ids play their one-shot variant (or run stationary for their
   * default duration and return a handle).
   * @param {string} id
   * @param {THREE.Vector3|{x:number,y:number,z:number}} position
   * @param {{scale?:number, color?:number|string|THREE.Color, direction?:THREE.Vector3, radius?:number, size?:number,
   *          duration?:number, style?:string, target?:THREE.Vector3}} [opts]
   * @returns {object|null} handle for stationary emitters, else null
   */
  play(id, position, opts) {
    if (!this.ready || !this.enabled || !position || !Number.isFinite(position.x)) return null;
    const key = this._resolve(id);
    if (!key) return null;
    const def = EFFECTS[key];
    const o = this._norm(opts);
    if (def.dedupe && this._isDuplicate(key, position, def.dedupe)) return null;
    _pos.set(position.x, position.y, position.z);
    try {
      if (def.kind === 'burst') { def.play(this, _pos, o); return null; }
      if (def.burst && !(opts && opts.duration > 0)) { def.burst(this, _pos, o); return null; }
      const dur = o.duration > 0 ? o.duration : (Number.isFinite(def.duration) ? def.duration : 1.5);
      return this._startEmitter(key, def, null, _pos, o, dur);
    } catch (e) { this._warn(`play:${key}`, e); return null; }
  }

  /**
   * Effect that follows an object until its duration ends or stop(handle).
   * Accepts an Object3D (bone, root, ball.root...), a Player or a Ball.
   * @param {string} id
   * @param {THREE.Object3D|object} object3D
   * @param {{duration?:number, color?:any, scale?:number, radius?:number, size?:number, offset?:THREE.Vector3}} [opts]
   * @returns {{active:boolean}|null} handle for stop()
   */
  attach(id, object3D, opts) {
    if (!this.ready || !this.enabled || !object3D) return null;
    const key = this._resolve(id);
    if (!key) return null;
    const def = EFFECTS[key];
    const o = this._norm(opts);
    let target = null, player = null, ball = null;
    if (object3D.isObject3D) target = object3D;
    else if (object3D.root && object3D.root.isObject3D) {
      target = object3D.root;
      if (object3D.hero !== undefined && object3D.team !== undefined) player = object3D;
      else if (object3D.style !== undefined && object3D.state !== undefined) ball = object3D;
    } else if (Number.isFinite(object3D.x)) return this.play(id, object3D, opts);
    if (!target) return null;
    if (!player) player = this._playerByObject(target);
    if (!ball && !player) ball = this._ballByObject(target);
    try {
      if (def.kind === 'burst') {
        this._worldPos(target, player, def, _pos);
        def.play(this, _pos, o);
        return null;
      }
      const dur = o.duration > 0 ? o.duration : def.duration;
      const em = this._startEmitter(key, def, target, null, o, dur, player, ball, opts && opts.offset);
      return em;
    } catch (e) { this._warn(`attach:${key}`, e); return null; }
  }

  /** Stops an attached / stationary effect (its particles finish naturally). Safe with null or stale handles. */
  stop(handle) {
    if (!handle || !handle._em) return;
    const em = handle._em;
    if (em.active && em.gen === handle._gen) this._endEmitter(em);
    handle.active = false;
  }

  /** Stops every emitter; `clearParticles` also wipes live particles and shapes (match restart). */
  stopAll(clearParticles = false) {
    for (const em of this._emitters) if (em.active) this._endEmitter(em);
    for (const st of this._pstate.values()) st.aura = null;
    if (clearParticles && this.ready) {
      this.A.clear(); this.X.clear(); this.ringsA.clear(); this.ringsX.clear(); this.spheres.clear(); this.columns.clear();
      this.ribbons.clear(); this.lights.clear();
    }
  }

  /** Live counts (debug overlay / tests). */
  get stats() {
    let em = 0;
    for (const e of this._emitters) if (e.active) em++;
    return this.ready ? { alpha: this.A.count, additive: this.X.count, emitters: em } : { alpha: 0, additive: 0, emitters: 0 };
  }

  // ------------------------------------------------------------------------------------------------ frame
  update(dt, realDt) {
    if (!this.ready) return;
    this.time += dt;
    this.floorY = game.court ? game.court.floorY : 0;
    const cam = game.camera;
    if (cam) { cam.getWorldPosition(this._camPos); cam.getWorldQuaternion(this._camQuat); }
    if (dt > 0) this._pollPlayers(dt);
    this._updateEmitters(dt);
    const t = this.time, fy = this.floorY;
    this.A.update(dt, t, fy);
    this.X.update(dt, t, fy);
    this.ringsA.update(dt, t, this._camQuat, fy);
    this.ringsX.update(dt, t, this._camQuat, fy);
    this.spheres.update(dt, t, this._camQuat, fy);
    this.columns.update(dt, t, this._camQuat, fy);
    this.ribbons.update(dt, t, this._camPos);
    this.lights.update(dt, t);
  }

  dispose() {
    for (const u of this._unsubs) u();
    this._unsubs = [];
    if (!this.ready) return;
    this.stopAll(true);
    this.A.dispose(); this.X.dispose();
    this.ringsA.dispose(); this.ringsX.dispose(); this.spheres.dispose(); this.columns.dispose();
    this.ribbons.dispose(); this.lights.dispose();
    if (this.atlas) this.atlas.dispose();
    if (this.root) this.root.removeFromParent();
    this._pstate.clear();
    this.ready = false;
  }

  // ------------------------------------------------------------------------------------------------ emitters
  _startEmitter(key, def, target, staticPos, o, duration, player = null, ball = null, offset = null) {
    let em = null;
    for (const e of this._emitters) if (!e.active) { em = e; break; }
    if (!em) {
      // saturated: recycle the oldest finite-duration emitter
      let best = -1;
      for (const e of this._emitters) if (Number.isFinite(e.duration) && e.age > best) { best = e.age; em = e; }
      if (!em) return null;
      this._endEmitter(em);
    }
    em.active = true; em.gen++; em.def = def; em.id = key;
    em.target = target; em.player = player; em.ball = ball;
    em.onPlayer = !!player && (target === player.root || (player.avatar && target === player.avatar.root));
    em.isBall = !!ball || (!player && !!target && !target.isBone && !!def.ballTrail);
    em.wasLive = false;
    em.duration = duration > 0 ? duration : 1.5;
    em.scale = o.scale; em.radius = o.radius; em.size = o.size;
    em.hasColor = o.hasColor; em.color.copy(o.hasColor ? o.color : _white);
    em.dir.copy(o.dir);
    em.age = 0; em.acc = 0; em.acc2 = 0; em.acc3 = 0; em.moved = 0; em.paused = false;
    em.light = null; em.ribbons[0] = em.ribbons[1] = null; em.shapes[0] = em.shapes[1] = em.shapes[2] = null;
    em.offset.set(0, em.onPlayer ? (def.playerOffsetY ?? DEFAULT_PLAYER_OFFSET[key] ?? 0) : 0, 0);
    if (offset && Number.isFinite(offset.x)) em.offset.add(offset);
    if (target) this._targetPos(em, em.pos);
    else { em.staticPos.copy(staticPos); em.pos.copy(staticPos); }
    em.prev.copy(em.pos); em.vel.set(0, 0, 0);
    if (def.start) def.start(this, em);
    const handle = { _em: em, _gen: em.gen, id: key, get active() { return em.active && em.gen === this._gen; }, set active(v) { /* read only */ } };
    return handle;
  }

  _endEmitter(em) {
    if (!em.active) return;
    try { if (em.def && em.def.end) em.def.end(this, em); } catch (e) { this._warn(`end:${em.id}`, e); }
    for (let i = 0; i < em.shapes.length; i++) {
      const sh = em.shapes[i];
      if (sh && sh.follow === em) ShapePool.release(sh, 0.35);
      em.shapes[i] = null;
    }
    for (let i = 0; i < em.ribbons.length; i++) {
      const r = em.ribbons[i];
      if (r && r.follow === em) RibbonPool.release(r);
      em.ribbons[i] = null;
    }
    if (em.light && em.light.follow === em) LightPool.release(em.light);
    em.light = null;
    em.active = false;
    em.target = null; em.player = null; em.ball = null; em.def = null;
  }

  _updateEmitters(dt) {
    const local = game.localPlayer;
    for (const em of this._emitters) {
      if (!em.active) continue;
      em.age += dt;
      if (em.age >= em.duration) { this._endEmitter(em); continue; }
      em.prev.copy(em.pos);
      if (em.target) {
        const tg = em.target;
        if (!tg.parent) { this._endEmitter(em); continue; } // removed from the scene graph
        if (em.ball && em.def.ballTrail) {
          const st = em.ball.state;
          if (st === 'live') em.wasLive = true;
          else if (st === 'despawned' || (em.wasLive && st !== 'stasis')) { this._endEmitter(em); continue; }
        }
        this._targetPos(em, em.pos);
        const p = em.player;
        em.paused = !visibleInScene(tg) ||
          (!!p && !!p.status && typeof p.status.has === 'function' && p.status.has('cloaked') && !!local && game.areEnemies(local, p));
      }
      const moved = em.pos.distanceTo(em.prev);
      if (moved > 70 * Math.max(dt, 1 / 60) + VFX_TUNING.teleportJump) {
        em.prev.copy(em.pos); em.moved = 0; // teleported: do not smear a trail across the court
      } else em.moved = moved;
      if (dt > 0) em.vel.subVectors(em.pos, em.prev).divideScalar(dt);
      for (const sh of em.shapes) if (sh && sh.follow === em) sh.hidden = em.paused;
      if (em.paused || dt <= 0 || !em.def.tick) continue;
      try { em.def.tick(this, em, dt); } catch (e) { this._warn(`tick:${em.id}`, e); this._endEmitter(em); }
    }
  }

  /** World position of an emitter's target (+ offset). */
  _targetPos(em, out) {
    em.target.getWorldPosition(out);
    return out.add(em.offset);
  }

  /** Where a one-shot attached to `target` plays: chest height for player roots. */
  _worldPos(target, player, def, out) {
    target.getWorldPosition(out);
    if (player && (target === player.root || (player.avatar && target === player.avatar.root))) out.y += 1.1;
    return out;
  }

  _playerByObject(obj) {
    for (const p of game.players) {
      if (p.root === obj || (p.avatar && p.avatar.root === obj)) return p;
    }
    return null;
  }

  _ballByObject(obj) {
    const list = game.balls && game.balls.active;
    if (!list) return null;
    for (const b of list) if (b.root === obj || b.visual === obj) return b;
    return null;
  }

  // ------------------------------------------------------------------------------------------------ helpers
  /** Id -> canonical key (aliases, keyword fallback, cached). */
  _resolve(id) {
    if (typeof id !== 'string' || !id) return null;
    if (EFFECTS[id]) return id;
    if (this._resolved.has(id)) return this._resolved.get(id);
    let key = EFFECT_ALIASES[id] || null;
    if (!key) {
      const low = id.toLowerCase();
      for (const [kw, k] of EFFECT_KEYWORDS) if (low.includes(kw)) { key = k; break; }
      if (!this._warned.has(`id:${id}`)) {
        this._warned.add(`id:${id}`);
        console.warn(`[vfx] unknown effect id '${id}'${key ? ` -> using '${key}'` : ' (ignored)'}`);
      }
    }
    this._resolved.set(id, key);
    return key;
  }

  /** Normalises caller options into the shared scratch object (never retained). */
  _norm(opts) {
    const o = this._o;
    o.scale = 1; o.hasColor = false; o.hasDir = false; o.dir.set(0, 1, 0); o.radius = 0; o.size = 0; o.duration = 0;
    o.style = null; o.target = null;
    if (!opts) return o;
    if (Number.isFinite(opts.scale)) o.scale = Math.max(0.05, opts.scale);
    if (opts.color !== undefined && opts.color !== null) {
      try {
        if (opts.color.isColor) o.color.copy(opts.color); else o.color.set(opts.color);
        o.hasColor = true;
      } catch (e) { o.hasColor = false; }
    }
    const d = opts.direction || opts.normal || null;
    if (d && Number.isFinite(d.x) && Number.isFinite(d.y) && Number.isFinite(d.z)) {
      o.dir.set(d.x, d.y, d.z);
      const l = o.dir.length();
      if (l > 1e-6) { o.dir.divideScalar(l); o.hasDir = true; } else o.dir.set(0, 1, 0);
    }
    if (Number.isFinite(opts.radius)) o.radius = opts.radius;
    if (Number.isFinite(opts.size)) o.size = opts.size;
    if (Number.isFinite(opts.duration)) o.duration = opts.duration;
    if (typeof opts.style === 'string') o.style = opts.style;
    const tg = opts.target;
    if (tg && Number.isFinite(tg.x)) { this._targetV.set(tg.x, tg.y, tg.z); o.target = this._targetV; }
    return o;
  }

  _isDuplicate(key, p, radius) {
    const now = game.time.realNow;
    const win = VFX_TUNING.dedupeWindow;
    const r2 = radius * radius;
    const n = this._recentId.length;
    for (let i = 0; i < n; i++) {
      if (this._recentId[i] !== key) continue;
      const k = i * 4;
      if (now - this._recent[k + 3] > win) continue;
      const dx = this._recent[k] - p.x, dy = this._recent[k + 1] - p.y, dz = this._recent[k + 2] - p.z;
      if (dx * dx + dy * dy + dz * dz <= r2) return true;
    }
    const h = this._recentHead;
    this._recentId[h] = key;
    this._recent[h * 4] = p.x; this._recent[h * 4 + 1] = p.y; this._recent[h * 4 + 2] = p.z; this._recent[h * 4 + 3] = now;
    this._recentHead = (h + 1) % n;
    return false;
  }

  _warn(key, e) {
    if (this._warned.has(key)) return;
    this._warned.add(key);
    console.warn(`[vfx] ${key} failed`, e);
  }

  _pst(p) {
    let st = this._pstate.get(p);
    if (!st) { st = { aura: null, slide: 0, wasSliding: false }; this._pstate.set(p, st); }
    return st;
  }

  /** True when the player's movement effects should not be seen (cloaked enemy of the local player). */
  _concealed(p) {
    const local = game.localPlayer;
    return !!(p.status && typeof p.status.has === 'function' && p.status.has('cloaked') && local && game.areEnemies(local, p));
  }

  // ------------------------------------------------------------------------------------------------ polling
  /** Slide dust trail while a player slides (metres based, frame-rate independent). */
  _pollPlayers(dt) {
    for (const p of game.players) {
      const m = p.motor;
      if (!m || !p.position) continue;
      const sliding = !!m.isSliding && m.isGrounded !== false;
      const st = this._pst(p);
      if (!sliding) { st.wasSliding = false; st.slide = 0; continue; }
      const speed = Number.isFinite(m.planarSpeed) ? m.planarSpeed : 0;
      if (speed < 1.2 || this._concealed(p)) continue;
      st.slide += speed * dt;
      if (!st.wasSliding) { st.wasSliding = true; st.slide = VFX_TUNING.slideDustSpacing; }
      if (st.slide >= VFX_TUNING.slideDustSpacing) {
        st.slide = 0;
        const v = p.velocity;
        const o = this._evOpts;
        o.scale = Math.min(1.6, 0.5 + speed / 8); o.color = null; o.style = null;
        o.direction = v && (v.x * v.x + v.z * v.z) > 0.01 ? this._evDir.set(v.x, 0, v.z) : null;
        this.play('slideDust', p.position, o);
      }
    }
  }

  // ------------------------------------------------------------------------------------------------ events
  _onBallHit(e) {
    if (!e) return;
    const victim = e.victim;
    const point = e.point || (victim && victim.chestPosition);
    if (!point) return;
    const o = this._evOpts;
    o.scale = clamp((e.speedKmh || 80) / VFX_TUNING.hitScaleRef, 0.5, 1.8);
    o.color = null;
    o.style = e.ball && e.ball.style && e.ball.style !== 'standard' ? e.ball.style : null;
    if (e.normal && e.normal.lengthSq && e.normal.lengthSq() > 1e-6) o.direction = e.normal;
    else if (e.velocity && e.velocity.lengthSq && e.velocity.lengthSq() > 1e-6) o.direction = this._evDir.copy(e.velocity).negate();
    else o.direction = null;
    this.play('hit', point, o);
  }

  _onCaught(e) {
    if (!e) return;
    const c = e.catcher;
    const point = e.point || (c && c.chestPosition);
    if (!point) return;
    const o = this._evOpts;
    o.scale = clamp((e.speedKmh || 60) / VFX_TUNING.hitScaleRef, 0.6, 1.5); o.color = null; o.direction = null; o.style = null;
    this.play(e.quality === 'perfect' ? 'perfectCatch' : 'catch', point, o);
  }

  _onBounced(e) {
    if (!e || !e.point) return;
    const s = e.impactSpeed || 0;
    const o = this._evOpts;
    o.color = null; o.style = null;
    if (e.floor) {
      if (s < VFX_TUNING.floorDustMinSpeed) return;
      o.scale = clamp(s / 12, 0.5, 2.2); o.direction = null;
      this.play('floorDust', e.point, o);
    } else {
      if (s < VFX_TUNING.wallDustMinSpeed) return;
      o.scale = clamp(s / 20, 0.4, 1); o.direction = e.normal || null;
      this.play('floorDust', e.point, o);
    }
  }

  _onEliminated(e) {
    const p = e && e.player;
    if (!p) return;
    const point = e.point || p.chestPosition || p.position;
    if (!point) return;
    const o = this._evOpts;
    o.scale = 1; o.style = null;
    o.color = TEAM_COLORS[p.team] ?? null;
    o.direction = e.impulse && e.impulse.lengthSq && e.impulse.lengthSq() > 1e-6 ? e.impulse : null;
    this.play('elimination', point, o);
    const st = this._pstate.get(p);
    if (st && st.aura) { this.stop(st.aura); st.aura = null; }
  }

  _onRevived(e) {
    const p = e && e.player;
    if (!p || !p.position) return;
    const o = this._evOpts;
    o.scale = 1; o.style = null; o.direction = null; o.color = TEAM_COLORS[p.team] ?? null;
    this.play('reviveBeam', p.position, o);
  }

  _onUltCharge(e) {
    const p = e && e.player;
    if (!p || !p.root) return;
    const st = this._pst(p);
    if (e.becameReady) {
      if (st.aura && st.aura.active) return;
      const c = p.hero && p.hero.color !== undefined ? p.hero.color : (TEAM_COLORS[p.team] ?? 0xffffff);
      st.aura = this.attach('ultReady', p.root, { color: c, scale: 1 });
    } else if (st.aura && Number.isFinite(e.value) && e.value < 0.999) {
      this.stop(st.aura); st.aura = null;
    }
  }

  _onPlayerState(e) {
    const p = e && e.player;
    if (!p || !p.position || this._concealed(p)) return;
    if (e.previous === 'airborne' && e.current !== 'airborne' && e.current !== 'incapacitated') {
      const o = this._evOpts;
      o.scale = 0.55; o.color = null; o.direction = null; o.style = null;
      this.play('floorDust', p.position, o);
    }
  }
}

/** Default attach offsets (m above a player's feet) when an effect is attached to a player root. */
const DEFAULT_PLAYER_OFFSET = {
  rewindTrail: 1.05, stasisBubble: 1.1, beamTrail: 1.2, fireTrail: 1.2, iceTrail: 1.0, glueTrail: 1.0,
};

/** Visible when the object and every ancestor are visible. */
function visibleInScene(o) {
  for (let n = o; n; n = n.parent) if (!n.visible) return false;
  return true;
}

function clamp(x, a, b) { return x < a ? a : x > b ? b : x; }
