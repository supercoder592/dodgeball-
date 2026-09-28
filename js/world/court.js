// Court geometry & zone queries (kernel). Court centred at the origin, long axis = Z.
// Home defends z < 0, Away defends z > 0. Each team's OUTFIELD strip lies behind the OPPONENT's baseline (Taiwanese 外場):
//   z in [-L/2-D, -L/2] : Away outfield (behind Home baseline)     z in [-L/2, 0]: Home infield
//   z in [0, L/2]       : Away infield                              z in [L/2, L/2+D]: Home outfield
// Bounds are plain objects { minX, maxX, minZ, maxZ }.
import * as THREE from 'three';
import { COURT, TEAM, ZONE } from '../core/constants.js';

export class Court {
  constructor({ length = COURT.length, width = COURT.width, outfieldDepth = COURT.outfieldDepth, runOff = COURT.runOff } = {}) {
    this.length = length; this.width = width; this.outfieldDepth = outfieldDepth; this.runOff = runOff;
    this.floorY = 0;
    this.inset = 0.35; // keeps capsules inside the painted lines
  }
  get halfL() { return this.length / 2; }
  get halfW() { return this.width / 2; }

  /** -1 for Home (negative Z), +1 for Away. */
  sideSign(team) { return team === TEAM.HOME ? -1 : 1; }
  /** Unit vector from `team`'s half toward the opponent. */
  attackDir(team) { return new THREE.Vector3(0, 0, -this.sideSign(team)); }
  spawnYaw(team) { return team === TEAM.HOME ? 0 : Math.PI; } // yaw 0 faces +Z

  infieldBounds(team) {
    const s = this.sideSign(team);
    const z0 = s < 0 ? -this.halfL : 0, z1 = s < 0 ? 0 : this.halfL;
    return { minX: -this.halfW, maxX: this.halfW, minZ: z0, maxZ: z1 };
  }
  /** Strip where `team`'s eliminated players stand: behind the opponent's baseline. */
  outfieldBounds(team) {
    const s = -this.sideSign(team); // opponent's side
    const z0 = s < 0 ? -this.halfL - this.outfieldDepth : this.halfL;
    const z1 = s < 0 ? -this.halfL : this.halfL + this.outfieldDepth;
    return { minX: -this.halfW - 1.5, maxX: this.halfW + 1.5, minZ: z0, maxZ: z1 };
  }
  /** Movement confinement for a player of `team` in `zone` (inset by the capsule radius). */
  confinement(team, zone) {
    const b = zone === ZONE.OUTFIELD ? this.outfieldBounds(team) : this.infieldBounds(team);
    const i = this.inset;
    return { minX: b.minX + i, maxX: b.maxX - i, minZ: b.minZ + i, maxZ: b.maxZ - i };
  }
  static contains(b, p) { return p.x >= b.minX && p.x <= b.maxX && p.z >= b.minZ && p.z <= b.maxZ; }
  static clamp(b, p) { p.x = Math.min(b.maxX, Math.max(b.minX, p.x)); p.z = Math.min(b.maxZ, Math.max(b.minZ, p.z)); return p; }

  isInInfield(team, p) { return Court.contains(this.infieldBounds(team), p); }
  /** Which team's infield half contains `p` (TEAM.NONE if outside both). */
  halfOwner(p) {
    if (Math.abs(p.x) > this.halfW || Math.abs(p.z) > this.halfL) return TEAM.NONE;
    return p.z < 0 ? TEAM.HOME : TEAM.AWAY;
  }
  /** Spread across the width in the back third of the team's half. */
  spawnPoint(team, slot, count) {
    const s = this.sideSign(team);
    const t = count <= 1 ? 0.5 : slot / (count - 1);
    const x = (t - 0.5) * (this.width - 2.4);
    const z = s * (this.halfL * 0.62 + (slot % 2) * 0.8);
    return new THREE.Vector3(x, this.floorY, z);
  }
  outfieldSpot(team, index) {
    const b = this.outfieldBounds(team);
    const x = ((index % 3) - 1) * (this.width / 3);
    return new THREE.Vector3(x, this.floorY, (b.minZ + b.maxZ) / 2);
  }
  /** Opening-rush balls along the centre line, alternately nudged toward each side. */
  openingBallPositions(count) {
    const out = [];
    for (let i = 0; i < count; i++) {
      const t = count <= 1 ? 0.5 : i / (count - 1);
      out.push(new THREE.Vector3((t - 0.5) * (this.width - 1.6), this.floorY + 0.105, (i % 2 === 0 ? -0.6 : 0.6)));
    }
    return out;
  }
  /** Beyond the run-off walls (+margin) or below the floor. */
  isOutOfArena(p) {
    const mx = this.halfW + this.runOff + 1.5, mz = this.halfL + this.outfieldDepth + this.runOff + 1.5;
    return Math.abs(p.x) > mx || Math.abs(p.z) > mz || p.y < this.floorY - 2 || p.y > 40;
  }
  /** Mirror of a point into the other half (same x, same distance from the centre line). */
  mirrorZ(p) { return new THREE.Vector3(p.x, p.y, -p.z); }
}
