// ---------------------------------------------------------------------------------------------------------------
// Pure geometry for Houdini's tricks (Swap Places, Grand Vanish). NO three.js import: every function works on plain
// {x, y, z} objects (THREE.Vector3 satisfies that shape) so it is unit tested with node:test (houdiniMath.test.js).
//
// Court conventions (js/world/court.js): long axis = Z, centre line at z = centreZ (0), Home defends z < 0 (side -1),
// Away defends z > 0 (side +1).
// ---------------------------------------------------------------------------------------------------------------

/**
 * Mirrored, court-relative swap destinations (Houdini's Swap Places design choice).
 *
 * A literal position swap would put Houdini inside the enemy half and the enemy inside Houdini's half, breaking the core
 * dodgeball rule that each team stays on its own side (and the motor confinement would immediately shove both back with a
 * jarring pop). Instead each player takes the OTHER player's spot relative to the court:
 *
 *   houdini -> (enemy.x,   centre + houdiniSide * |enemy.z   - centre|)   enemy's spot mirrored into Houdini's half
 *   enemy   -> (houdini.x, centre + enemySide   * |houdini.z - centre|)   Houdini's spot mirrored into the enemy half
 *
 * Lane (x) and depth (distance from the centre line) are exchanged, so it still reads as a swap - an aggressive enemy at
 * the centre line is flung back to their baseline while Houdini takes the front, lanes cross over - but both stay legal.
 * Heights (y) are kept per player. Results are written into `outH` / `outE` (allocation-free).
 *
 * @param {{x:number,y:number,z:number}} h Houdini's feet position
 * @param {{x:number,y:number,z:number}} e enemy's feet position
 * @param {number} hSide -1 (Home) or +1 (Away): side of the centre line Houdini defends
 * @param {number} eSide side of the centre line the enemy defends
 * @param {number} centreZ z of the centre line
 * @param {{x:number,y:number,z:number}} outH receives Houdini's destination
 * @param {{x:number,y:number,z:number}} outE receives the enemy's destination
 */
export function mirroredSwapDestinations(h, e, hSide, eSide, centreZ, outH, outE) {
  const hx = h.x, hy = h.y, hz = h.z;
  const ex = e.x, ey = e.y, ez = e.z;
  const hs = hSide < 0 ? -1 : 1;
  const es = eSide < 0 ? -1 : 1;
  outH.x = ex; outH.y = hy; outH.z = centreZ + hs * Math.abs(ez - centreZ);
  outE.x = hx; outE.y = ey; outE.z = centreZ + es * Math.abs(hz - centreZ);
  return outH;
}

/**
 * Pushes `dest` (planar XZ) out of a circle of radius `minDist` around `other`. When the two points coincide the push
 * goes along (fallbackX, fallbackZ). Mutates and returns `dest`.
 */
export function pushAwayPlanar(dest, other, minDist, fallbackX = 1, fallbackZ = 0) {
  let dx = dest.x - other.x, dz = dest.z - other.z;
  const d2 = dx * dx + dz * dz;
  if (d2 >= minDist * minDist) return dest;
  let d = Math.sqrt(d2);
  if (d < 1e-4) {
    const fl = Math.hypot(fallbackX, fallbackZ) || 1;
    dx = fallbackX / fl; dz = fallbackZ / fl; d = 1;
    dest.x = other.x + dx * minDist; dest.z = other.z + dz * minDist;
    return dest;
  }
  dest.x = other.x + (dx / d) * minDist;
  dest.z = other.z + (dz / d) * minDist;
  return dest;
}

/**
 * Angular offset (radians) of slot `k` on a fan in front of a player: 0, +s, -s, +2s, -2s, ...
 * @param {number} k slot index (0 = straight ahead)
 * @param {number} spacing radians between neighbouring slots
 */
export function fanAngle(k, spacing) {
  if (k <= 0) return 0;
  const step = Math.ceil(k / 2);
  return (k % 2 === 1 ? 1 : -1) * step * spacing;
}

/**
 * Round-robin distribution of item `index` over `receiverCount` receivers.
 * Writes { receiver, slot } into `out`: receiver index and the item's slot around that receiver.
 */
export function roundRobin(index, receiverCount, out = { receiver: 0, slot: 0 }) {
  const n = Math.max(1, receiverCount | 0);
  out.receiver = index % n;
  out.slot = Math.floor(index / n);
  return out;
}

/**
 * X coordinate of respawn point `index` of `count` spread evenly across the court width (like the opening rush),
 * keeping `margin` metres from each sideline.
 */
export function spreadX(index, count, width, margin = 0.8) {
  if (count <= 1) return 0;
  const t = index / (count - 1);
  return (t - 0.5) * Math.max(0, width - 2 * margin);
}

/**
 * Point on a ring of `radius` around (cx, cz) at yaw `angle` (yaw 0 faces +Z, as everywhere in the game).
 * Writes x/z into `out` (y untouched).
 */
export function ringPoint(cx, cz, angle, radius, out) {
  out.x = cx + Math.sin(angle) * radius;
  out.z = cz + Math.cos(angle) * radius;
  return out;
}
