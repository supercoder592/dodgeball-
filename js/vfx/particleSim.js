// ---------------------------------------------------------------------------------------------------------------
// CPU side of the particle system (owner: fx). Pure JS, no three.js import, so it is unit tested (particleSim.test.js).
//
// Struct-of-arrays storage in Float32Arrays, dense live range [0, count): a dead particle is swap-removed with the last
// live one, so simulation and GPU upload only ever touch live particles. Nothing allocates after construction.
//
// Per particle physics: velocity, exponential air drag, gravity multiplier (negative = buoyant smoke), cheap
// divergence-free-looking turbulence, spin, optional orbit around a vertical axis (magnetic swirl, vortices), and floor
// interaction (pass through / slide / bounce with restitution / stick). Over life: size (ease-out growth), colour (linear
// start->end, HDR allowed), alpha (fade in, then (1-t)^fadePow fade out).
// ---------------------------------------------------------------------------------------------------------------

/** Floor behaviours stored in `bounce`: values in (0, 1] are restitution coefficients. */
export const FLOOR = Object.freeze({
  /** Ignores the floor (smoke, glows, mist). */
  NONE: -1,
  /** Stops falling and slides along the floor, planar speed decays through drag (dust hugging the floor). */
  SLIDE: 0,
  /** Stops dead on contact (glue droplets, frost flakes). */
  STICK: -2,
});

export const GRAVITY = 9.81;

/**
 * Reusable spawn description. Effects fill ONE shared instance and call `sim.spawn(spec)` (no allocations).
 * Colours are linear RGB and may exceed 1 (HDR, feeds bloom on additive layers).
 */
export class ParticleSpec {
  constructor() { this.reset(); }
  /** Restores defaults. @returns {ParticleSpec} this */
  reset() {
    this.x = 0; this.y = 0; this.z = 0;
    this.vx = 0; this.vy = 0; this.vz = 0;
    this.life = 1;             // seconds
    this.size0 = 0.2;          // diameter at birth (m)
    this.size1 = 0.4;          // diameter at death (m)
    this.rot = 0;              // radians (sprite roll)
    this.spin = 0;             // rad/s
    this.drag = 1;             // 1/s  (v *= e^{-drag dt})
    this.grav = 0;             // x g (negative rises)
    this.r0 = 1; this.g0 = 1; this.b0 = 1;
    this.r1 = 1; this.g1 = 1; this.b1 = 1;
    this.alpha = 1;            // peak opacity
    this.fadeIn = 0.1;         // fraction of life spent fading in
    this.fadePow = 1;          // fade-out exponent
    this.frame = 0;            // atlas frame (see textures.js FRAME)
    this.stretch = 0;          // > 0: velocity aligned streak, extra length = speed * stretch (s)
    this.bounce = FLOOR.NONE;  // see FLOOR
    this.turb = 0;             // turbulence acceleration (m/s^2)
    this.orbitW = 0;           // rad/s around (cx, cz); 0 = no orbit
    this.orbitPull = 0;        // m/s radial speed while orbiting (negative = inward)
    this.cx = 0; this.cz = 0;  // orbit axis
    return this;
  }
  /** Convenience setters (chainable). */
  at(x, y, z) { this.x = x; this.y = y; this.z = z; return this; }
  vel(x, y, z) { this.vx = x; this.vy = y; this.vz = z; return this; }
  color(r, g, b) { this.r0 = this.r1 = r; this.g0 = this.g1 = g; this.b0 = this.b1 = b; return this; }
  colorEnd(r, g, b) { this.r1 = r; this.g1 = g; this.b1 = b; return this; }
  sizes(a, b) { this.size0 = a; this.size1 = b; return this; }
}

/** Number of floats per particle field group, for readability. */
const V3 = 3;

export class ParticleSim {
  /** @param {number} capacity maximum simultaneously live particles */
  constructor(capacity) {
    this.capacity = Math.max(1, capacity | 0);
    this.count = 0;
    this._steal = 0;
    this._dirty = false;
    const n = this.capacity;
    const F = (k = 1) => new Float32Array(n * k);
    this.p = F(V3); this.v = F(V3);
    this.age = F(); this.life = F();
    this.s0 = F(); this.s1 = F(); this.size = F();
    this.rot = F(); this.spin = F(); this.drag = F(); this.grav = F();
    this.c0 = F(V3); this.c1 = F(V3);
    this.alpha = F(); this.fadeIn = F(); this.fadePow = F();
    this.frame = F(); this.stretch = F(); this.bounce = F(); this.turb = F(); this.seed = F();
    this.orbit = F(4); // cx, cz, w, pull
    this.gravity = GRAVITY;
    this._seedCounter = 0;
  }

  /** Kills every particle. */
  clear() { this.count = 0; this._steal = 0; this._dirty = true; }

  /**
   * Adds one particle. When saturated, the oldest slots are recycled round-robin (never throws, never allocates).
   * @param {ParticleSpec} s
   * @returns {number} slot index
   */
  spawn(s) {
    let i;
    if (this.count < this.capacity) i = this.count++;
    else { i = this._steal; this._steal = (this._steal + 1) % this.capacity; }
    const i3 = i * 3, i4 = i * 4;
    this.p[i3] = s.x; this.p[i3 + 1] = s.y; this.p[i3 + 2] = s.z;
    this.v[i3] = s.vx; this.v[i3 + 1] = s.vy; this.v[i3 + 2] = s.vz;
    this.age[i] = 0; this.life[i] = s.life > 0.001 ? s.life : 0.001;
    this.s0[i] = s.size0; this.s1[i] = s.size1; this.size[i] = s.size0;
    this.rot[i] = s.rot; this.spin[i] = s.spin; this.drag[i] = s.drag; this.grav[i] = s.grav;
    this.c0[i3] = s.r0; this.c0[i3 + 1] = s.g0; this.c0[i3 + 2] = s.b0;
    this.c1[i3] = s.r1; this.c1[i3 + 1] = s.g1; this.c1[i3 + 2] = s.b1;
    this.alpha[i] = s.alpha; this.fadeIn[i] = s.fadeIn; this.fadePow[i] = s.fadePow;
    this.frame[i] = s.frame; this.stretch[i] = s.stretch; this.bounce[i] = s.bounce; this.turb[i] = s.turb;
    this.seed[i] = (this._seedCounter = (this._seedCounter + 0.618034) % 1) * 6.2831853;
    this.orbit[i4] = s.cx; this.orbit[i4 + 1] = s.cz; this.orbit[i4 + 2] = s.orbitW; this.orbit[i4 + 3] = s.orbitPull;
    this._dirty = true;
    return i;
  }

  /** Copies particle `src` into slot `dst` (swap-remove). */
  _move(dst, src) {
    const d3 = dst * 3, s3 = src * 3, d4 = dst * 4, s4 = src * 4;
    for (let k = 0; k < 3; k++) {
      this.p[d3 + k] = this.p[s3 + k]; this.v[d3 + k] = this.v[s3 + k];
      this.c0[d3 + k] = this.c0[s3 + k]; this.c1[d3 + k] = this.c1[s3 + k];
    }
    for (let k = 0; k < 4; k++) this.orbit[d4 + k] = this.orbit[s4 + k];
    this.age[dst] = this.age[src]; this.life[dst] = this.life[src];
    this.s0[dst] = this.s0[src]; this.s1[dst] = this.s1[src]; this.size[dst] = this.size[src];
    this.rot[dst] = this.rot[src]; this.spin[dst] = this.spin[src]; this.drag[dst] = this.drag[src]; this.grav[dst] = this.grav[src];
    this.alpha[dst] = this.alpha[src]; this.fadeIn[dst] = this.fadeIn[src]; this.fadePow[dst] = this.fadePow[src];
    this.frame[dst] = this.frame[src]; this.stretch[dst] = this.stretch[src]; this.bounce[dst] = this.bounce[src];
    this.turb[dst] = this.turb[src]; this.seed[dst] = this.seed[src];
  }

  /**
   * Integrates every live particle and (optionally) writes the GPU instance data.
   * @param {number} dt scaled seconds (0 while paused / frozen by hitstop -> particles hold still)
   * @param {number} time running effect clock (turbulence phase)
   * @param {number} floorY court floor height
   * @param {{posSize:Float32Array, color:Float32Array, vel:Float32Array, rotFrame:Float32Array}|null} out
   * @returns {boolean} true when `out` was rewritten (caller uploads [0, count))
   */
  update(dt, time, floorY, out) {
    if (dt <= 0 && !this._dirty) return false;
    const p = this.p, v = this.v, g = this.gravity;
    let i = 0;
    while (i < this.count) {
      const age = this.age[i] + dt;
      const life = this.life[i];
      if (age >= life) {
        // swap-remove: the last live particle takes this slot and is processed next iteration
        const last = --this.count;
        if (i !== last) this._move(i, last);
        continue;
      }
      this.age[i] = age;
      const t = age / life;
      const i3 = i * 3, i4 = i * 4;
      let px = p[i3], py = p[i3 + 1], pz = p[i3 + 2];
      let vx = v[i3], vy = v[i3 + 1], vz = v[i3 + 2];

      if (dt > 0) {
        const k = Math.exp(-this.drag[i] * dt);
        vy = vy * k - g * this.grav[i] * dt;
        const w = this.orbit[i4 + 2], pull = this.orbit[i4 + 3];
        if (w !== 0 || pull !== 0) {
          // Orbit in polar coordinates around a vertical axis; the planar velocity is derived for streak rendering.
          const cx = this.orbit[i4], cz = this.orbit[i4 + 1];
          const dx = px - cx, dz = pz - cz;
          const ang = Math.atan2(dz, dx) + w * dt;
          const r = Math.max(0, Math.sqrt(dx * dx + dz * dz) + pull * dt);
          const nx = cx + Math.cos(ang) * r, nz = cz + Math.sin(ang) * r;
          vx = (nx - px) / dt; vz = (nz - pz) / dt;
          px = nx; pz = nz; py += vy * dt;
        } else {
          vx *= k; vz *= k;
          const tb = this.turb[i];
          if (tb !== 0) {
            // Smooth pseudo-curl: gradients of low-frequency sines of position/time, phase-shifted per particle.
            const ph = this.seed[i];
            vx += Math.sin(py * 1.9 + time * 1.3 + ph) * tb * dt;
            vz += Math.cos(px * 1.7 - time * 1.1 + ph * 1.3) * tb * dt;
            vy += Math.sin(pz * 2.3 + time * 0.9 + ph * 0.7) * tb * 0.5 * dt;
          }
          px += vx * dt; py += vy * dt; pz += vz * dt;
        }
        this.rot[i] += this.spin[i] * dt;
      }

      // size over life (ease-out growth reads as expanding puffs decelerating)
      const e = 1 - (1 - t) * (1 - t);
      const size = this.s0[i] + (this.s1[i] - this.s0[i]) * e;
      this.size[i] = size;

      // floor interaction
      const b = this.bounce[i];
      if (b !== FLOOR.NONE) {
        const rad = Math.min(size * 0.3, 0.25);
        if (py < floorY + rad) {
          py = floorY + rad;
          if (b === FLOOR.STICK) { vx = 0; vy = 0; vz = 0; this.spin[i] = 0; }
          else if (vy < 0) {
            if (b > 0) {
              vy = -vy * b;
              if (vy < 0.35) vy = 0;
              vx *= 0.72; vz *= 0.72; this.spin[i] *= 0.5;
            } else vy = 0;
          }
        }
      }
      p[i3] = px; p[i3 + 1] = py; p[i3 + 2] = pz;
      v[i3] = vx; v[i3 + 1] = vy; v[i3 + 2] = vz;

      if (out) {
        const fi = this.fadeIn[i];
        let a = this.alpha[i] * (fi > 0 && t < fi ? t / fi : 1);
        const fp = this.fadePow[i];
        const rem = 1 - t;
        a *= fp === 1 ? rem : fp === 0 ? 1 : Math.pow(rem, fp);
        const o4 = i * 4;
        out.posSize[o4] = px; out.posSize[o4 + 1] = py; out.posSize[o4 + 2] = pz; out.posSize[o4 + 3] = size;
        out.color[o4] = this.c0[i3] + (this.c1[i3] - this.c0[i3]) * t;
        out.color[o4 + 1] = this.c0[i3 + 1] + (this.c1[i3 + 1] - this.c0[i3 + 1]) * t;
        out.color[o4 + 2] = this.c0[i3 + 2] + (this.c1[i3 + 2] - this.c0[i3 + 2]) * t;
        out.color[o4 + 3] = a;
        out.vel[o4] = vx; out.vel[o4 + 1] = vy; out.vel[o4 + 2] = vz; out.vel[o4 + 3] = this.stretch[i];
        out.rotFrame[i * 2] = this.rot[i]; out.rotFrame[i * 2 + 1] = this.frame[i];
      }
      i++;
    }
    this._dirty = false;
    return !!out;
  }
}
