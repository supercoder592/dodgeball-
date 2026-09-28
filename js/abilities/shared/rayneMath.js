// ---------------------------------------------------------------------------------------------------------------
// Pure math behind Rayne's kit (Overcharge scaling, Supersonic Meteor shockwave falloff, Hyperbeam corridor test).
// NO three.js import: every function works on plain numbers / {x, y, z} objects so it is unit tested with node:test
// (rayneMath.test.js). The three.js-side code lives in raynePayloads.js and heroes/rayne.js.
// ---------------------------------------------------------------------------------------------------------------

/** Clamp to [0, 1]. */
export const clamp01 = (x) => (x < 0 ? 0 : x > 1 ? 1 : x);

/**
 * Overcharge fraction (0..1) of a throw charged for `chargeSeconds`, reaching 1 at `fullTime` (spec: 2 s).
 * @param {number} chargeSeconds
 * @param {number} fullTime
 */
export function overchargeFraction(chargeSeconds, fullTime) {
  return clamp01((chargeSeconds || 0) / Math.max(0.01, fullTime));
}

/**
 * Speed / radius multipliers of Rayne's Overcharge:
 *   t = clamp01(chargeSeconds / fullTime);  speed x(1 + maxSpeedBonus * t);  radius x(1 + maxRadiusBonus * t)
 * (the rally rule still caps the final speed at 220 km/h inside the throw solver).
 * @param {{speed:number, radius:number}} [out]
 */
export function overchargeMultipliers(chargeSeconds, fullTime, maxSpeedBonus, maxRadiusBonus, out = { speed: 1, radius: 1 }) {
  const t = overchargeFraction(chargeSeconds, fullTime);
  out.speed = 1 + maxSpeedBonus * t;
  out.radius = 1 + maxRadiusBonus * t;
  return out;
}

/**
 * Linear knockback falloff of the meteor shockwave: 1 at the epicentre, `edgeFraction` at the rim (`radius`), 0 beyond.
 * @param {number} distance planar distance from the epicentre (m)
 * @param {number} radius AOE radius (m)
 * @param {number} edgeFraction fraction still applied at the rim (0..1)
 */
export function shockwaveFalloff(distance, radius, edgeFraction) {
  if (!(radius > 0) || distance > radius) return 0;
  const t = clamp01(distance / radius);
  return 1 + (clamp01(edgeFraction) - 1) * t;
}

/**
 * Velocity change (m/s) the shockwave gives to a body whose feet are at planar offset (dx, dz) from the epicentre.
 * Radial outward with falloff plus a small upward component (same falloff) so momentum carries the victim.
 * When the body stands on the epicentre (|d| < 5 cm, e.g. the direct victim) the push uses (fallbackX, fallbackZ)
 * - the meteor's planar flight direction - so the victim is driven along the ball's path.
 * @param {{x:number,y:number,z:number}} out receives the impulse
 * @returns {number} the falloff factor used (0 = outside the AOE, `out` zeroed)
 */
export function shockwaveImpulse(dx, dz, radius, knockback, edgeFraction, upward, fallbackX, fallbackZ, out) {
  const dist = Math.hypot(dx, dz);
  const f = shockwaveFalloff(dist, radius, edgeFraction);
  if (f <= 0) { out.x = 0; out.y = 0; out.z = 0; return 0; }
  let nx, nz;
  if (dist > 0.05) { nx = dx / dist; nz = dz / dist; } else {
    const fl = Math.hypot(fallbackX, fallbackZ);
    if (fl > 1e-6) { nx = fallbackX / fl; nz = fallbackZ / fl; } else { nx = 0; nz = 1; }
  }
  out.x = nx * knockback * f;
  out.y = upward * f;
  out.z = nz * knockback * f;
  return f;
}

/**
 * Planar corridor test used by the Hyperbeam AI: is point p inside a corridor of half width `halfWidth` that starts at
 * `from` and runs along the planar unit axis (ax, az)? Points behind `from` are outside.
 */
export function inCorridor(from, ax, az, p, halfWidth) {
  const rx = p.x - from.x, rz = p.z - from.z;
  const along = rx * ax + rz * az;
  if (along < 0) return false;
  const lx = rx - ax * along, lz = rz - az * along;
  return lx * lx + lz * lz <= halfWidth * halfWidth;
}
