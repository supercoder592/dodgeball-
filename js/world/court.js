// Court geometry & zone queries (kernel). Court centred at the origin, long axis = Z, painted lines in metres.
// Home defends z < 0, Away defends z > 0. Each team's OUTFIELD is a U around the OPPONENT's half (Taiwanese /
// Japanese 外野): both sideline strips alongside that half (halfW <= |x| <= halfW + sideW, centre line -> baseline)
// plus the strip behind its baseline (halfL <= |z| <= halfL + depth, corners included). The two U's make one ring
// around the court, split by the centre line:
//                 z = +12 ┌──────── Home U (back strip) ────────┐
//                         │ Home │                      │ Home │
//                 z = +9  │  U   ├──── Away infield ────┤  U   │      Away's U is the mirror image (z <= 0),
//                         │ arm  │                      │ arm  │      around the Home half.
//                 z = 0   ├──────┼──────────────────────┼──────┤
//                                x = ±4.5 (sidelines)   x = ±7 (outer line)
// Regions are { minX, maxX, minZ, maxZ, hole } (see world/courtMath.js): an infield half has hole null; a U is its
// outer box minus a hole (the opponent's half, overshooting the centre line by 1 m so that side is never an exit).
import * as THREE from 'three';
import { COURT, TEAM, ZONE, opponent } from '../core/constants.js';
import {
  regionContainsXZ, regionClampXZ, regionDistanceXZ, insetRegion, uPathGeometry, uPointXZ, uParamXZ, zoneAtXZ,
} from './courtMath.js';

/** Arm ends of the outfield path stop this far from the centre line (m). */
export const OUTFIELD_FRONT_INSET = 0.8;
/** Outfield spot table (u along the U path): 0 = back centre (starting outfielder), then arms, then back corners. */
export const OUTFIELD_SPOT_U = Object.freeze([0.5, 0.16, 0.84, 0.36, 0.64, 0.05, 0.95]);
/** The hole overshoots the open (centre-line) side of the U by this much (m). */
const HOLE_OVERSHOOT = 1;
/** Venue shell used by isOutOfArena without an arena (reproduces the old run-off based limits). */
const DEFAULT_SHELL = Object.freeze({ halfX: 9.5, halfZ: 17, maxY: 38 });

const _xz = { x: 0, z: 0 };

export class Court {
  constructor({
    length = COURT.length, width = COURT.width, outfieldDepth = COURT.outfieldDepth,
    sideOutfieldWidth = COURT.sideOutfieldWidth, runOff = COURT.runOff,
  } = {}) {
    this.length = length; this.width = width; this.outfieldDepth = outfieldDepth; this.runOff = runOff;
    this.sideOutfieldWidth = sideOutfieldWidth;
    this.floorY = 0;
    this.inset = 0.35; // keeps capsules inside the painted lines
    this._shell = { ...DEFAULT_SHELL };
    this._u = uPathGeometry(this.halfW, this.halfL, sideOutfieldWidth, outfieldDepth, OUTFIELD_FRONT_INSET);
    this._dims = { halfW: this.halfW, halfL: this.halfL, outerHalfW: this.outerHalfW, outerHalfL: this.outerHalfL };
  }
  get halfL() { return this.length / 2; }
  get halfW() { return this.width / 2; }
  /** Outer boundary of the outfield ring: |x| <= outerHalfW, |z| <= outerHalfL. */
  get outerHalfW() { return this.halfW + this.sideOutfieldWidth; }
  get outerHalfL() { return this.halfL + this.outfieldDepth; }
  /** Length (m) of a team's U centre line (outfieldPoint parameter scale). */
  get outfieldPathLength() { return this._u.length; }

  /** -1 for Home (negative Z), +1 for Away. */
  sideSign(team) { return team === TEAM.HOME ? -1 : 1; }
  /** Unit vector from `team`'s half toward the opponent. */
  attackDir(team) { return new THREE.Vector3(0, 0, -this.sideSign(team)); }
  spawnYaw(team) { return team === TEAM.HOME ? 0 : Math.PI; } // yaw 0 faces +Z

  /** Painted infield half of `team` (hole null). */
  infieldBounds(team) {
    const s = this.sideSign(team);
    const z0 = s < 0 ? -this.halfL : 0, z1 = s < 0 ? 0 : this.halfL;
    return { minX: -this.halfW, maxX: this.halfW, minZ: z0, maxZ: z1, hole: null };
  }
  /** `team`'s U outfield around the opponent's half, on the painted lines (outer box + hole). */
  outfieldBounds(team) {
    const s = this.sideSign(opponent(team)); // side of the half the U surrounds
    const L = this.halfL, W = this.halfW, oL = this.outerHalfL, oW = this.outerHalfW;
    return s > 0
      ? { minX: -oW, maxX: oW, minZ: 0, maxZ: oL, hole: { minX: -W, maxX: W, minZ: -HOLE_OVERSHOOT, maxZ: L } }
      : { minX: -oW, maxX: oW, minZ: -oL, maxZ: 0, hole: { minX: -W, maxX: W, minZ: -L, maxZ: HOLE_OVERSHOOT } };
  }
  /** The painted rectangles of `team`'s U: [left arm, right arm, back strip (corners included)] (floor paint, minimap). */
  outfieldRects(team) {
    const s = this.sideSign(opponent(team));
    const L = this.halfL, W = this.halfW, oL = this.outerHalfL, oW = this.outerHalfW;
    const a0 = s > 0 ? 0 : -L, a1 = s > 0 ? L : 0;
    const b0 = s > 0 ? L : -oL, b1 = s > 0 ? oL : -L;
    return [
      { minX: -oW, maxX: -W, minZ: a0, maxZ: a1, hole: null },
      { minX: W, maxX: oW, minZ: a0, maxZ: a1, hole: null },
      { minX: -oW, maxX: oW, minZ: b0, maxZ: b1, hole: null },
    ];
  }
  /** Movement confinement for a player of `team` in `zone`: the region inset by the capsule radius (allocates). */
  confinement(team, zone) {
    const b = zone === ZONE.OUTFIELD ? this.outfieldBounds(team) : this.infieldBounds(team);
    return insetRegion(b, this.inset, b);
  }

  // ------------------------------------------------------------------ region statics (hole-aware)
  /** Inside the outer box (inclusive) and not strictly inside the hole. */
  static contains(b, p) { return regionContainsXZ(b, p.x, p.z); }
  /** Clamp `p` (in place) to the region's closest point (nearest exit edge out of a hole). @returns p */
  static clamp(b, p) { regionClampXZ(b, p.x, p.z, _xz); p.x = _xz.x; p.z = _xz.z; return p; }
  /** Closest region point to `p` into `out` (y copied from p). */
  static closestPoint(b, p, out = new THREE.Vector3()) {
    regionClampXZ(b, p.x, p.z, _xz);
    return out.set(_xz.x, p.y || 0, _xz.z);
  }
  /** Planar distance from `p` to the region (0 inside). */
  static distanceTo(b, p) { return regionDistanceXZ(b, p.x, p.z); }
  /** Region inset by m (outer shrinks, hole grows) into `out`. */
  static inset(b, m, out = {}) { return insetRegion(b, m, out); }

  // ------------------------------------------------------------------ zones
  isInInfield(team, p) { return Court.contains(this.infieldBounds(team), p); }
  /** Which team's infield half contains `p` (TEAM.NONE if outside both). */
  halfOwner(p) {
    if (Math.abs(p.x) > this.halfW || Math.abs(p.z) > this.halfL) return TEAM.NONE;
    return p.z < 0 ? TEAM.HOME : TEAM.AWAY;
  }
  /**
   * Zone owning floor point `p` on the painted lines (dead-ball rule): an infield half, else a team's U (up to the
   * outer line + margin), else null (run-off / unreachable). Allocation-free.
   * @param {{x:number,z:number}} p
   * @param {{team:number, zone:string}} [out]
   * @returns {{team:number, zone:string}|null}
   */
  zoneAt(p, out = { team: TEAM.NONE, zone: ZONE.INFIELD }, margin = 0) {
    return zoneAtXZ(p.x, p.z, this._dims, out, margin);
  }

  // ------------------------------------------------------------------ spawn / serve / outfield points
  /** Spread across the width in the back third of the team's half (pass the INFIELD slot index and count). */
  spawnPoint(team, slot, count) {
    const s = this.sideSign(team);
    const t = count <= 1 ? 0.5 : slot / (count - 1);
    const x = (t - 0.5) * (this.width - 2.4);
    const z = s * (this.halfL * 0.62 + (slot % 2) * 0.8);
    return new THREE.Vector3(x, this.floorY, z);
  }
  /** Where the serving team's server stands with the ball (centre of the half, 40% deep). */
  servePoint(team) {
    return new THREE.Vector3(0, this.floorY, this.sideSign(team) * this.halfL * 0.4);
  }
  /**
   * Point of `team`'s U centre line at u in [0, 1] (arc length; u = 0 is always the -X arm end, absolute - not
   * mirrored per team). Always inside the outfield confinement.
   */
  outfieldPoint(team, u, out = new THREE.Vector3()) {
    uPointXZ(this._u, this.sideSign(opponent(team)), u, _xz);
    return out.set(_xz.x, this.floorY, _xz.z);
  }
  /** u of the point of `team`'s U centre line nearest to `p`. */
  outfieldParam(team, p) {
    return uParamXZ(this._u, this.sideSign(opponent(team)), p.x, p.z);
  }
  /**
   * Steering target that walks `team`'s U toward targetU without cutting across the hole: the path point at most
   * `lookAhead` metres (along the path) from p's own u, toward targetU.
   */
  outfieldWaypoint(team, p, targetU, lookAhead = 1.6, out = new THREE.Vector3()) {
    const u0 = this.outfieldParam(team, p);
    const lim = lookAhead / Math.max(1e-6, this._u.length);
    let du = targetU - u0;
    if (du > lim) du = lim; else if (du < -lim) du = -lim;
    return this.outfieldPoint(team, u0 + du, out);
  }
  /** Outfield standing spot `index` (index 0 = back centre, reserved for the starting outfielder). `count` unused. */
  // eslint-disable-next-line no-unused-vars
  outfieldSpot(team, index, count) {
    const i = Math.max(0, index | 0) % OUTFIELD_SPOT_U.length;
    return this.outfieldPoint(team, OUTFIELD_SPOT_U[i]);
  }
  /** Yaw for an outfielder of `team` at `p`: facing the centre of the opponent's half. */
  outfieldYaw(team, p) {
    const cz = this.sideSign(opponent(team)) * this.halfL / 2;
    const dx = -p.x, dz = cz - p.z;
    if (dx * dx + dz * dz < 1e-8) return this.spawnYaw(opponent(team));
    return Math.atan2(dx, dz);
  }
  /**
   * @deprecated single-ball rules: the ball starts in the server's hand (servePoint). Kept for docs/dev/arena-test.html.
   */
  openingBallPositions(count) {
    const out = [];
    for (let i = 0; i < count; i++) {
      const t = count <= 1 ? 0.5 : i / (count - 1);
      out.push(new THREE.Vector3((t - 0.5) * (this.width - 1.6), this.floorY + 0.105, (i % 2 === 0 ? -0.6 : 0.6)));
    }
    return out;
  }

  // ------------------------------------------------------------------ venue
  /** The arena's shell (inner faces of the outer walls, ceiling) used by isOutOfArena. */
  setShell({ halfX, halfZ, maxY } = {}) {
    if (Number.isFinite(halfX)) this._shell.halfX = halfX;
    if (Number.isFinite(halfZ)) this._shell.halfZ = halfZ;
    if (Number.isFinite(maxY)) this._shell.maxY = maxY;
  }
  /** Beyond the venue shell (+0.5 m, +2 m vertically) or below the floor. */
  isOutOfArena(p) {
    const s = this._shell;
    return Math.abs(p.x) > s.halfX + 0.5 || Math.abs(p.z) > s.halfZ + 0.5 || p.y < this.floorY - 2 || p.y > s.maxY + 2;
  }
  /** Mirror of a point into the other half (same x, same distance from the centre line). */
  mirrorZ(p) { return new THREE.Vector3(p.x, p.y, -p.z); }
}
