// ---------------------------------------------------------------------------------------------------------------
// Game context + main loop (kernel). Every module imports `game` and reaches other systems through it:
//   game.scene, game.camera, game.renderer (render/renderer.js), game.cameraRig, game.input, game.events, game.time,
//   game.assets, game.court, game.arena, game.balls, game.match, game.juice, game.vfx, game.audio, game.hud,
//   game.physics (cannon-es world for ragdolls), game.players (array), game.rng, game.config, game.roster
// Loop: fixed-step simulation (60 Hz) on SCALED time (hitstop/slow-mo/pause freeze it), variable update, late update,
// render. Systems register with game.addSystem(obj, order) and implement any of:
//   fixedUpdate(dt), update(dt, realDt), lateUpdate(dt, realDt)
// ---------------------------------------------------------------------------------------------------------------
import { EventBus } from './core/events.js';
import { Rng } from './core/rng.js';

/** Update order buckets (lower runs first). */
export const ORDER = Object.freeze({
  INPUT: 0, AI: 10, PLAYERS: 20, ABILITIES: 30, BALLS: 40, PHYSICS: 50, MATCH: 60, VFX: 70, AUDIO: 75,
  JUICE: 80, CAMERA: 90, HUD: 100,
});

/** Scaled/unscaled clock with hitstop + pause support. Owned by the kernel; Juice drives hitstop through it. */
export class GameTime {
  constructor() {
    this.now = 0;          // scaled seconds since start (gameplay clock)
    this.realNow = 0;      // unscaled seconds since start
    this.dt = 0;           // scaled delta of the current frame
    this.realDt = 0;       // unscaled delta of the current frame (clamped)
    this.paused = false;   // pause menu
    this.slowMo = 1;       // designer slow motion (e.g. match point replay); 1 = normal
    this._hitstopUntil = -1;
    this._hitstopScale = 1;
    this.frame = 0;
  }
  /** Effective time scale: 0 when paused, hitstop scale during a hitstop, else slowMo. */
  get scale() {
    if (this.paused) return 0;
    if (this.realNow < this._hitstopUntil) return this._hitstopScale;
    return this.slowMo;
  }
  get hitstopActive() { return this.realNow < this._hitstopUntil; }
  /** Freeze frame for `duration` real seconds at `scale` (overlapping requests extend, never shorten). */
  hitstop(duration, scale = 0.02) {
    const until = this.realNow + duration;
    if (until > this._hitstopUntil) this._hitstopUntil = until;
    this._hitstopScale = Math.min(this.hitstopActive ? this._hitstopScale : 1, scale);
  }
  cancelHitstop() { this._hitstopUntil = -1; this._hitstopScale = 1; }
}

class Game {
  constructor() {
    this.events = new EventBus();
    this.time = new GameTime();
    this.rng = new Rng(1);
    this.players = [];
    /** Non-player things live balls can strike (clones, shields, turrets): { team, intersect(from,to,radius)->{t,point,normal}|null, onBallHit(ball,hit)->'pass'|'block'|'absorb'|'handled' } */
    this.hittables = new Set();
    this.config = { quality: 'high', seed: 1, spectate: false, autoplay: false, debug: false, params: new URLSearchParams() };
    this.fixedStep = 1 / 60;
    this._accumulator = 0;
    this._systems = [];
    this._running = false;
    this._lastTs = 0;
    this.stats = { fps: 0, frameMs: 0, simSteps: 0 };
    // Service slots (assigned during boot by main.js)
    this.scene = null; this.camera = null; this.renderer = null; this.cameraRig = null; this.input = null;
    this.assets = null; this.court = null; this.arena = null; this.balls = null; this.match = null; this.juice = null;
    this.vfx = null; this.audio = null; this.hud = null; this.physics = null; this.roster = null;
  }

  addSystem(system, order = 50) {
    this.removeSystem(system);
    this._systems.push({ system, order });
    this._systems.sort((a, b) => a.order - b.order);
    return system;
  }
  removeSystem(system) { this._systems = this._systems.filter((s) => s.system !== system); }

  start() {
    if (this._running) return;
    this._running = true;
    this._lastTs = performance.now();
    const loop = (ts) => {
      if (!this._running) return;
      requestAnimationFrame(loop);
      this.tick(ts);
    };
    requestAnimationFrame(loop);
  }
  stop() { this._running = false; }

  /** One rendered frame. Exposed for tests (window.__DU.step). */
  tick(ts = performance.now()) {
    const t0 = performance.now();
    const realDt = Math.min(0.1, Math.max(0, (ts - this._lastTs) / 1000));
    this._lastTs = ts;
    const time = this.time;
    time.realDt = realDt;
    time.realNow += realDt;
    const scale = time.scale;
    const dt = realDt * scale;
    time.dt = dt;
    time.frame++;

    // Fixed-step simulation on scaled time (max 5 steps/frame to avoid spirals).
    this._accumulator += dt;
    let steps = 0;
    while (this._accumulator >= this.fixedStep && steps < 5) {
      this._accumulator -= this.fixedStep;
      time.now += this.fixedStep;
      for (const { system } of this._systems) if (system.fixedUpdate) this._safe(system, 'fixedUpdate', this.fixedStep);
      steps++;
    }
    if (steps === 5) this._accumulator = 0;
    this.stats.simSteps = steps;

    for (const { system } of this._systems) if (system.update) this._safe(system, 'update', dt, realDt);
    for (const { system } of this._systems) if (system.lateUpdate) this._safe(system, 'lateUpdate', dt, realDt);
    if (this.renderer) this.renderer.render(realDt);

    const ms = performance.now() - t0;
    this.stats.frameMs = this.stats.frameMs * 0.9 + ms * 0.1;
    if (realDt > 0) this.stats.fps = this.stats.fps * 0.9 + (1 / realDt) * 0.1;
  }

  /** Interpolation alpha between the last two fixed steps (for smooth rendering of physics objects). */
  get alpha() { return this._accumulator / this.fixedStep; }

  _safe(system, method, a, b) {
    try { system[method](a, b); } catch (e) {
      // Never let one system kill the loop; report once per system/method.
      const key = `__err_${method}`;
      if (!system[key]) { system[key] = true; console.error(`[game] ${system.constructor && system.constructor.name}.${method} threw`, e); }
    }
  }

  // ------------------------------------------------------------------ player registry helpers
  registerPlayer(p) { if (!this.players.includes(p)) this.players.push(p); }
  unregisterPlayer(p) { this.players = this.players.filter((x) => x !== p); }
  get localPlayer() { return this.players.find((p) => p.isLocal) || null; }
  teamPlayers(team, zone = null) { return this.players.filter((p) => p.team === team && (zone === null || p.zone === zone)); }
  areEnemies(a, b) { return !!a && !!b && a.team !== b.team && a.team >= 0 && b.team >= 0; }
  areTeammates(a, b) { return !!a && !!b && a !== b && a.team === b.team; }
  /** Nearest targetable enemy of `self` from `pos` within maxDist. */
  nearestEnemy(self, pos, maxDist = Infinity) {
    let best = null, bestD = maxDist * maxDist;
    for (const p of this.players) {
      if (!this.areEnemies(self, p) || !p.isTargetable) continue;
      const d = p.position.distanceToSquared(pos);
      if (d < bestD) { bestD = d; best = p; }
    }
    return best;
  }
  nearestTeammate(self, pos, zone = null) {
    let best = null, bestD = Infinity;
    for (const p of this.players) {
      if (!this.areTeammates(self, p) || (zone && p.zone !== zone) || !p.canReceive) continue;
      const d = p.position.distanceToSquared(pos);
      if (d < bestD) { bestD = d; best = p; }
    }
    return best;
  }
  playersInSphere(center, radius, team = null) {
    const r2 = radius * radius;
    return this.players.filter((p) => (team === null || p.team === team) && p.position.distanceToSquared(center) <= r2);
  }
}

/** The single game context. */
export const game = new Game();
