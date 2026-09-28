// ---------------------------------------------------------------------------------------------------------------
// VFX catalogue (owner: fx). Data + small functions describing how every effect id looks. Vfx (vfx.js) owns the pools
// and calls into here; nothing in this file allocates per call (shared ParticleSpec / ShapeSpec / temporaries).
//
// Two kinds of definitions:
//   burst   : { kind:'burst', play(fx, pos, o) }                      one-shot at a point (play())
//   emitter : { kind:'emitter', duration, start(fx, em), tick(fx, em, dt), end?(fx, em), burst?(fx, pos, o) }
//             attach() follows an Object3D; play() of an emitter id runs its `burst` if it has one, otherwise a
//             stationary emitter for `duration`.
// `o` (normalised options): { scale, color:THREE.Color (linear), hasColor, dir:Vector3 (unit), hasDir, radius, size,
//                             duration, style, target:Vector3|null }
// `em` (Emitter, vfx.js): pos/prev/vel/dir (world), moved (m this frame), age, duration, scale, radius, size, color,
//                         hasColor, onPlayer, acc/acc2/acc3 accumulators, light, ribbons[], shapes[].
// Look: realistic broadcast sports - chalky dust, sweat spray, real fire/smoke/embers, glints, light flashes that
// actually light the players (LightPool). HDR colours (> 1) only on additive layers, so bloom catches them.
// ---------------------------------------------------------------------------------------------------------------
import * as THREE from 'three';
import { ParticleSpec, FLOOR } from './particleSim.js';
import { FRAME } from './textures.js';
import { SHAPE, RING_MODE, ORIENT } from './shapes.js';

const TAU = Math.PI * 2;
const P = new ParticleSpec();
const S = SHAPE;
const rnd = Math.random;
const rr = (a, b) => a + (b - a) * rnd();
const rs = () => (rnd() < 0.5 ? -1 : 1);
const clamp = (x, a, b) => (x < a ? a : x > b ? b : x);

const _up = new THREE.Vector3(0, 1, 0);
const _xAxis = new THREE.Vector3(1, 0, 0);
const _d = new THREE.Vector3();
const _t = new THREE.Vector3();
const _b = new THREE.Vector3();
const _p = new THREE.Vector3();
const _q = new THREE.Vector3();
const _c = new THREE.Color();
const _c2 = new THREE.Color();
const _swapTo = new THREE.Vector3();
/** Scratch options for effects that call other effects (never the caller's object). */
const _o2 = { scale: 1, color: new THREE.Color(), hasColor: false, dir: new THREE.Vector3(0, 1, 0), hasDir: false, radius: 0, size: 0, duration: 0, style: null, target: null };
function subOpts(scale, color = null, dir = null) {
  _o2.scale = scale; _o2.hasColor = !!color; if (color) _o2.color.copy(color);
  _o2.hasDir = !!dir; if (dir) _o2.dir.copy(dir); else _o2.dir.set(0, 1, 0);
  _o2.radius = 0; _o2.size = 0; _o2.duration = 0; _o2.style = null; _o2.target = null;
  return _o2;
}

/** Linear-space palette (hex values are sRGB, THREE.Color converts). */
const COL = {
  dust: new THREE.Color(0xd8cfc0),      // pale maple-floor dust / chalk
  dustDark: new THREE.Color(0x8e8272),  // heavier grime for debris
  chalk: new THREE.Color(0xeeebe6),
  smokeDark: new THREE.Color(0x2c2826),
  smokeLight: new THREE.Color(0x6d6865),
  fire: new THREE.Color(0xff7a2a),
  beam: new THREE.Color(0x5fe8ff),
  ice: new THREE.Color(0xbfe6ff),
  glue: new THREE.Color(0x6fbf1f),
  violet: new THREE.Color(0x7a5cff),
  magnet: new THREE.Color(0x6fb6ff),
  amber: new THREE.Color(0xffc043),
  chrono: new THREE.Color(0x3be08a),
  warm: new THREE.Color(0xfff1d6),
  white: new THREE.Color(0xffffff),
};

// ------------------------------------------------------------------------------------------------ helpers
/** Quality-scaled particle count (always at least `min`). */
function cnt(fx, n, min = 1) { return Math.max(min, Math.round(n * fx.q)); }

function randUnit(out) {
  const z = rr(-1, 1), a = rnd() * TAU, r = Math.sqrt(1 - z * z);
  return out.set(r * Math.cos(a), z, r * Math.sin(a));
}
/** Orthonormal basis (_t, _b) perpendicular to unit `n`. */
function basis(n) {
  _t.crossVectors(Math.abs(n.y) < 0.9 ? _up : _xAxis, n).normalize();
  _b.crossVectors(n, _t);
}
/** Uniform direction inside a cone of half-angle `half` (rad) around unit `axis`. */
function randCone(out, axis, half) {
  basis(axis);
  const z = rr(Math.cos(half), 1), a = rnd() * TAU, r = Math.sqrt(Math.max(0, 1 - z * z));
  return out.copy(axis).multiplyScalar(z).addScaledVector(_t, r * Math.cos(a)).addScaledVector(_b, r * Math.sin(a));
}
/** Chosen colour (option colour when given, else the default), written to `out`. */
function pick(o, def, out = _c) { return out.copy(o && o.hasColor ? o.color : def); }
function emCol(em, def, out = _c) { return out.copy(em.hasColor ? em.color : def); }
/** P colour from a Color times k (+ white lift w). */
function col(c, k = 1, w = 0) { P.color(c.r * k + w, c.g * k + w, c.b * k + w); }
function colEnd(c, k = 1, w = 0) { P.colorEnd(c.r * k + w, c.g * k + w, c.b * k + w); }
/** Point between em.prev and em.pos (path interpolation so fast balls leave continuous trails). */
function along(em, f, out = _p) { return out.copy(em.prev).lerp(em.pos, f); }
/** Emission count this frame from distance + time rates (fractional carry in em[key]). */
function emitCount(fx, em, perMeter, perSecond, dt, key = 'acc', cap = 40) {
  em[key] += (em.moved * perMeter + dt * perSecond) * fx.q;
  const k = Math.floor(em[key]);
  em[key] -= k;
  return Math.min(k, cap);
}
function floorY(fx) { return fx.floorY; }

// ------------------------------------------------------------------------------------------------ building blocks
/** Soft pale dust puffs spreading along the floor. */
function floorPuffs(fx, pos, n, s, speed, alpha, c = COL.dust) {
  const y = floorY(fx);
  for (let i = 0; i < n; i++) {
    const a = rnd() * TAU, cs = Math.cos(a), sn = Math.sin(a);
    const v = rr(0.4, 1) * speed;
    P.reset().at(pos.x + cs * rr(0.03, 0.15) * s, y + rr(0.03, 0.12) * s, pos.z + sn * rr(0.03, 0.15) * s)
      .vel(cs * v, rr(0.15, 0.6) * s, sn * v).sizes(rr(0.12, 0.2) * s, rr(0.45, 0.75) * s);
    P.life = rr(0.7, 1.25); P.drag = 3.2; P.turb = 0.35; P.bounce = FLOOR.SLIDE; P.frame = FRAME.SMOKE;
    P.rot = rnd() * TAU; P.spin = rr(-0.8, 0.8); P.alpha = alpha * rr(0.7, 1); P.fadeIn = 0.08; P.fadePow = 1.4;
    col(c, rr(0.9, 1.05)); colEnd(c, 0.9);
    fx.A.spawn(P);
  }
}
/** Tiny specks / grit kicked up (stretched, gravity, bounce). */
function specks(fx, pos, dir, spread, n, vmin, vmax, c, size = 0.018, bounce = 0.3, grav = 1) {
  for (let i = 0; i < n; i++) {
    randCone(_d, dir, spread);
    const v = rr(vmin, vmax);
    P.reset().at(pos.x, pos.y, pos.z).vel(_d.x * v, _d.y * v, _d.z * v).sizes(size * rr(0.7, 1.3), size * 0.8);
    P.life = rr(0.35, 0.7); P.drag = 0.8; P.grav = grav; P.bounce = bounce; P.stretch = 0.02; P.frame = FRAME.SOFT;
    P.alpha = 0.8; P.fadeIn = 0; P.fadePow = 2; col(c);
    fx.A.spawn(P);
  }
}
/** Additive streak sparks (HDR colour c * k). */
function sparks(fx, pos, dir, spread, n, vmin, vmax, c, k, life = 0.4, size = 0.025, grav = 0.6, stretch = 0.03, bounce = FLOOR.NONE) {
  for (let i = 0; i < n; i++) {
    randCone(_d, dir, spread);
    const v = rr(vmin, vmax);
    P.reset().at(pos.x, pos.y, pos.z).vel(_d.x * v, _d.y * v, _d.z * v).sizes(size * rr(0.8, 1.2), size * 0.5);
    P.life = life * rr(0.6, 1.3); P.drag = 2.2; P.grav = grav; P.stretch = stretch; P.frame = FRAME.SPARK; P.bounce = bounce;
    P.alpha = 1; P.fadeIn = 0; P.fadePow = 1.6; col(c, k); colEnd(c, k * 0.25);
    fx.X.spawn(P);
  }
}
/** Short-lived additive glow (flash core). */
function glow(fx, pos, c, k, s0, s1, life, alpha = 1) {
  P.reset().at(pos.x, pos.y, pos.z).sizes(s0, s1);
  P.life = life; P.drag = 0; P.frame = FRAME.SOFT; P.alpha = alpha; P.fadeIn = 0; P.fadePow = 2; P.rot = rnd() * TAU;
  col(c, k);
  fx.X.spawn(P);
}
/** Rolling smoke puffs (alpha layer). */
function smoke(fx, pos, n, s, speed, c0, c1, alpha, life, rise = 0.15, spread = 0.2) {
  for (let i = 0; i < n; i++) {
    randUnit(_d);
    const v = rr(0.3, 1) * speed;
    P.reset().at(pos.x + _d.x * spread * s, pos.y + _d.y * spread * s, pos.z + _d.z * spread * s)
      .vel(_d.x * v, _d.y * v * 0.6 + rise * 2, _d.z * v).sizes(rr(0.25, 0.4) * s, rr(0.8, 1.2) * s);
    P.life = life * rr(0.75, 1.25); P.drag = 2.2; P.grav = -rise; P.turb = 0.7; P.frame = FRAME.SMOKE;
    P.rot = rnd() * TAU; P.spin = rr(-0.6, 0.6); P.alpha = alpha * rr(0.7, 1); P.fadeIn = 0.12; P.fadePow = 1.3;
    col(c0); colEnd(c1);
    fx.A.spawn(P);
  }
}
/** Ice crystal shards (alpha layer, shiny glint channel). */
function iceShards(fx, pos, dir, spread, n, vmin, vmax, s, c = COL.ice) {
  for (let i = 0; i < n; i++) {
    randCone(_d, dir, spread);
    const v = rr(vmin, vmax);
    P.reset().at(pos.x, pos.y, pos.z).vel(_d.x * v, _d.y * v, _d.z * v).sizes(rr(0.04, 0.11) * s, rr(0.03, 0.07) * s);
    P.life = rr(0.8, 1.5); P.drag = 0.6; P.grav = 1; P.bounce = 0.25; P.frame = FRAME.SHARD;
    P.rot = rnd() * TAU; P.spin = rr(-9, 9); P.alpha = 0.92; P.fadeIn = 0; P.fadePow = 3;
    col(c, 1.05, 0.05);
    fx.A.spawn(P);
  }
}
/** Cold mist (sinks slowly, hugs the floor). */
function coldMist(fx, pos, n, s, speed, alpha, life, c = COL.ice) {
  for (let i = 0; i < n; i++) {
    const a = rnd() * TAU, v = rr(0.3, 1) * speed;
    P.reset().at(pos.x + Math.cos(a) * 0.15 * s, pos.y + rr(-0.1, 0.2) * s, pos.z + Math.sin(a) * 0.15 * s)
      .vel(Math.cos(a) * v, rr(-0.2, 0.3), Math.sin(a) * v).sizes(rr(0.25, 0.4) * s, rr(0.9, 1.4) * s);
    P.life = life * rr(0.75, 1.25); P.drag = 1.6; P.grav = 0.06; P.turb = 0.35; P.bounce = FLOOR.SLIDE; P.frame = FRAME.SMOKE;
    P.rot = rnd() * TAU; P.spin = rr(-0.4, 0.4); P.alpha = alpha * rr(0.7, 1); P.fadeIn = 0.2; P.fadePow = 1.2;
    col(_c2.copy(c).lerp(COL.white, 0.6));
    fx.A.spawn(P);
  }
}
/** Tiny twinkling glints (additive). */
function glints(fx, pos, n, radius, c, k, life = 0.6, vel = 0.6) {
  for (let i = 0; i < n; i++) {
    randUnit(_d);
    const r = rr(0, radius);
    P.reset().at(pos.x + _d.x * r, pos.y + _d.y * r, pos.z + _d.z * r).vel(_d.x * vel * rnd(), _d.y * vel * rnd(), _d.z * vel * rnd())
      .sizes(rr(0.03, 0.06), 0.0);
    P.life = life * rr(0.5, 1.4); P.drag = 1.5; P.frame = FRAME.SOFT; P.alpha = 1; P.fadeIn = 0.15; P.fadePow = 1;
    col(c, k);
    fx.X.spawn(P);
  }
}
/** Floor or oriented ring from the shared SHAPE spec (additive or alpha pool). */
function ring(fx, additive, pos, r0, r1, w0, w1, life, c, k, opacity = 1, orient = ORIENT.FLOOR, normal = null) {
  S.reset(); S.pos.copy(pos); S.r0 = r0; S.r1 = r1; S.w0 = w0; S.w1 = w1; S.life = life;
  S.color.setRGB(c.r * k, c.g * k, c.b * k); S.opacity = opacity; S.orient = orient;
  if (normal) S.normal.copy(normal);
  return S;
}
function spawnRing(fx, additive) { return (additive ? fx.ringsX : fx.ringsA).spawn(S); }

// ------------------------------------------------------------------------------------------------ one-shot effects
/** Ball hits a player: soft cloth-dust puff, sweat spray, a faint impact flash; style accents for ability balls. */
function hit(fx, pos, o) {
  const s = clamp(o.scale, 0.3, 2.5);
  const dir = o.dir;
  glow(fx, pos, pick(o, COL.warm), o.hasColor ? 1.6 : 1.3, 0.16 * s, 0.5 * s, 0.08, 0.45);
  for (let i = 0, n = cnt(fx, 7 * s, 3); i < n; i++) {
    randCone(_d, dir, 1.1);
    const v = rr(1.2, 3.2) * s;
    P.reset().at(pos.x + dir.x * 0.04 + rr(-0.05, 0.05), pos.y + dir.y * 0.04 + rr(-0.05, 0.05), pos.z + dir.z * 0.04 + rr(-0.05, 0.05))
      .vel(_d.x * v, _d.y * v + 0.25, _d.z * v).sizes(0.1 * s, rr(0.4, 0.6) * s);
    P.life = rr(0.45, 0.8); P.drag = 5; P.turb = 0.6; P.frame = FRAME.SMOKE; P.rot = rnd() * TAU; P.spin = rr(-1.2, 1.2);
    P.alpha = 0.3; P.fadeIn = 0.06; P.fadePow = 1.4; col(COL.chalk, 0.95); colEnd(COL.chalk, 0.85);
    fx.A.spawn(P);
  }
  // sweat / moisture spray: small stretched droplets that fall and stick
  specks(fx, pos, dir, 0.8, cnt(fx, 8 * s, 3), 2.5 * s, 6 * s, COL.white, 0.016, FLOOR.STICK, 1);
  switch (o.style) {
    case 'meteor':
      sparks(fx, pos, dir, 1.2, cnt(fx, 14 * s), 2, 7, COL.fire, 4, 0.7, 0.03, 0.5, 0.03, 0.3);
      smoke(fx, pos, cnt(fx, 4), s * 0.8, 1.2, COL.smokeDark, COL.smokeLight, 0.35, 1.2, 0.3);
      break;
    case 'freeze':
      iceShards(fx, pos, dir, 1.0, cnt(fx, 10 * s), 1.5, 4.5, s);
      coldMist(fx, pos, cnt(fx, 4), s * 0.7, 0.8, 0.25, 1.3);
      break;
    case 'glue':
      glueBlobs(fx, pos, dir, cnt(fx, 8 * s), s * 0.8, pick(o, COL.glue));
      break;
    case 'beam':
      sparks(fx, pos, dir, 1.3, cnt(fx, 16 * s), 3, 9, pick(o, COL.beam), 3.5, 0.35, 0.02, 0.3);
      break;
    default:
      if (o.hasColor) sparks(fx, pos, dir, 1.0, cnt(fx, 6 * s), 2, 6, o.color, 2.5, 0.3, 0.02, 0.4);
  }
}

/** Normal catch: small chalk puff from the hands. */
function catchFx(fx, pos, o) {
  const s = clamp(o.scale, 0.3, 2);
  for (let i = 0, n = cnt(fx, 5 * s, 2); i < n; i++) {
    randUnit(_d);
    const v = rr(0.4, 1.1) * s;
    P.reset().at(pos.x, pos.y, pos.z).vel(_d.x * v, _d.y * v + 0.2, _d.z * v).sizes(0.06 * s, rr(0.25, 0.35) * s);
    P.life = rr(0.35, 0.6); P.drag = 4; P.turb = 0.4; P.frame = FRAME.SMOKE; P.rot = rnd() * TAU;
    P.alpha = 0.22; P.fadeIn = 0.1; P.fadePow = 1.5; col(COL.chalk, 0.95);
    fx.A.spawn(P);
  }
  if (o.hasColor) glints(fx, pos, cnt(fx, 5), 0.25 * s, o.color, 2, 0.4, 0.8);
}

/** Perfect catch: bright light burst + expanding billboard ring + radial streaks + a real light flash. */
function perfectCatch(fx, pos, o) {
  const s = clamp(o.scale, 0.5, 2);
  const c = pick(o, COL.warm);
  glow(fx, pos, c, 3.2, 0.4 * s, 1.7 * s, 0.24, 0.85);
  glow(fx, pos, COL.white, 5, 0.2 * s, 0.55 * s, 0.12, 1);
  ring(fx, true, pos, 0.12 * s, 1.35 * s, 0.07 * s, 0.015 * s, 0.38, c, 2.4, 1, ORIENT.BILLBOARD);
  spawnRing(fx, true);
  ring(fx, true, pos, 0.05 * s, 0.8 * s, 0.05 * s, 0.01 * s, 0.3, COL.white, 1.6, 0.8, ORIENT.BILLBOARD);
  S.delay = 0.05; spawnRing(fx, true);
  sparks(fx, pos, _up, Math.PI, cnt(fx, 18 * s, 6), 5, 11, c, 3.2, 0.36, 0.028, 0.15, 0.035);
  catchFx(fx, pos, subOpts(s));
  fx.lights.flash(pos, _c2.copy(c).lerp(COL.warm, 0.5), 28 * s, 0.28, 7);
}

/** Ball impact on the floor (or a wall when o.dir is horizontal): dust puff + grit; heavy hits add a dust ring. */
function floorDust(fx, pos, o) {
  const s = clamp(o.scale, 0.3, 3);
  const c = pick(o, COL.dust);
  const wall = o.hasDir && Math.abs(o.dir.y) < 0.5;
  if (wall) {
    for (let i = 0, n = cnt(fx, 6 * s, 2); i < n; i++) {
      randCone(_d, o.dir, 1.0);
      const v = rr(0.6, 1.8) * s;
      P.reset().at(pos.x, pos.y, pos.z).vel(_d.x * v, _d.y * v, _d.z * v).sizes(0.1 * s, rr(0.4, 0.6) * s);
      P.life = rr(0.6, 1.0); P.drag = 3.5; P.turb = 0.4; P.frame = FRAME.SMOKE; P.rot = rnd() * TAU;
      P.alpha = 0.22; P.fadeIn = 0.1; P.fadePow = 1.4; col(c);
      fx.A.spawn(P);
    }
    return;
  }
  floorPuffs(fx, pos, cnt(fx, 8 * s, 3), s, 1.8 * s, clamp(0.3 * s, 0.12, 0.38), c);
  _p.set(pos.x, floorY(fx) + 0.03, pos.z);
  specks(fx, _p, _up, 0.9, cnt(fx, 4 * s), 1.2 * s, 2.8 * s, COL.dustDark, 0.014, 0.3, 1);
  if (s > 1.25) {
    ring(fx, false, _p, 0.1, 0.65 * s, 0.12 * s, 0.04, 0.35, c, 0.95, 0.3);
    S.noise = 0.12; spawnRing(fx, false);
  }
}

/** Sliding feet: low dust trailing behind + scuffs. o.dir = slide direction. */
function slideDust(fx, pos, o) {
  const s = clamp(o.scale, 0.3, 2.5);
  const c = pick(o, COL.dust);
  const y = floorY(fx);
  const dx = o.hasDir ? o.dir.x : 0, dz = o.hasDir ? o.dir.z : 0;
  for (let i = 0, n = cnt(fx, 4 * s, 2); i < n; i++) {
    const side = rr(-1, 1);
    P.reset().at(pos.x + rr(-0.12, 0.12), y + rr(0.04, 0.12), pos.z + rr(-0.12, 0.12))
      .vel(-dx * rr(0.3, 1.0) - dz * side * 0.6, rr(0.15, 0.45), -dz * rr(0.3, 1.0) + dx * side * 0.6)
      .sizes(0.1 * s, rr(0.35, 0.55) * s);
    P.life = rr(0.5, 0.9); P.drag = 3; P.turb = 0.3; P.bounce = FLOOR.SLIDE; P.frame = FRAME.SMOKE; P.rot = rnd() * TAU;
    P.alpha = 0.2 * Math.min(1, s); P.fadeIn = 0.1; P.fadePow = 1.4; col(c);
    fx.A.spawn(P);
  }
  if (o.hasColor && o.color) glints(fx, _p.set(pos.x, y + 0.4, pos.z), cnt(fx, 2), 0.3, o.color, 1.5, 0.4, 0.4);
}

/** Meteor detonation: fireball, embers, rising smoke, floor shock + dust rings, scorch decal, strong orange light. */
function shockwave(fx, pos, o) {
  const s = clamp(o.scale, 0.3, 3);
  const R = o.radius > 0 ? o.radius : 3 * s;
  const k = R / 3;
  const c = pick(o, COL.fire);
  const y = floorY(fx);
  _p.set(pos.x, Math.max(pos.y, y + 0.35), pos.z);
  fx.lights.flash(_p, _c2.copy(c).lerp(COL.warm, 0.3), 70 * k, 0.5, 10 + 3 * k);
  glow(fx, _p, _c2.copy(c).lerp(COL.warm, 0.6), 6, 0.8 * k, 3.2 * k, 0.16, 1);
  // fireball tongues: hot yellow-white -> deep red, expanding and decelerating
  for (let i = 0, n = cnt(fx, 26 * k, 8); i < n; i++) {
    randUnit(_d); if (_d.y < 0) _d.y *= -0.4;
    const v = rr(2, 6) * k;
    P.reset().at(_p.x + _d.x * 0.2, _p.y + _d.y * 0.2, _p.z + _d.z * 0.2).vel(_d.x * v, _d.y * v + 0.8, _d.z * v)
      .sizes(rr(0.35, 0.5) * k, rr(1.1, 1.6) * k);
    P.life = rr(0.35, 0.65); P.drag = 3.2; P.grav = -0.2; P.frame = FRAME.SMOKE; P.rot = rnd() * TAU; P.spin = rr(-2, 2);
    P.alpha = 0.9; P.fadeIn = 0.02; P.fadePow = 1.3;
    P.color(c.r * 5 + 1.2, c.g * 5 + 0.8, c.b * 5 + 0.3); P.colorEnd(c.r * 1.1, c.g * 0.35, c.b * 0.2);
    fx.X.spawn(P);
  }
  sparks(fx, _p, _up, 1.45, cnt(fx, 36 * k, 10), 4, 11, c, 4.5, 1.1, 0.035, 0.6, 0.03, 0.35);
  smoke(fx, _p, cnt(fx, 14 * k, 5), 1.6 * k, 1.6, COL.smokeDark, COL.smokeLight, 0.45, 2.2, 0.35, 0.3);
  _q.set(pos.x, y, pos.z);
  floorPuffs(fx, _q, cnt(fx, 18 * k, 6), 1.3 * k, R * 1.6, 0.3);
  ring(fx, true, _q, 0.2, R, 0.35 * k, 0.08, 0.45, c, 3.5, 0.9);
  S.noise = 0.06; spawnRing(fx, true);
  ring(fx, false, _q, 0.3, R * 1.1, 0.5 * k, 0.2, 0.8, COL.dust, 1, 0.4);
  S.noise = 0.1; spawnRing(fx, false);
  // scorch mark left on the hardwood
  ring(fx, false, _q, R * 0.3, R * 0.36, 0.25, 0.25, 3.5, COL.smokeDark, 0.12, 0.55);
  S.mode = RING_MODE.DISC; S.noise = 0.35; S.fadeStart = 0.35; S.fadePow = 1.2; S.easePow = 6; spawnRing(fx, false);
  // heat haze: very faint streaky wisps rising
  for (let i = 0, n = cnt(fx, 6 * k, 2); i < n; i++) {
    P.reset().at(_p.x + rr(-0.5, 0.5) * k, _p.y + rr(0, 0.6), _p.z + rr(-0.5, 0.5) * k).vel(0, rr(0.8, 1.6), 0).sizes(0.5 * k, 1.4 * k);
    P.life = rr(0.8, 1.3); P.drag = 0.5; P.turb = 1; P.frame = FRAME.WISP; P.rot = rr(-0.3, 0.3) + Math.PI / 2; P.alpha = 0.05;
    P.fadeIn = 0.3; col(COL.white, 0.9);
    fx.A.spawn(P);
  }
}

/** Glue droplets (blob sprites, stick on the floor). */
function glueBlobs(fx, pos, dir, n, s, c) {
  for (let i = 0; i < n; i++) {
    randCone(_d, dir, 1.2);
    const v = rr(1.2, 4) * s;
    P.reset().at(pos.x, pos.y, pos.z).vel(_d.x * v, _d.y * v + 0.8, _d.z * v).sizes(rr(0.05, 0.12) * s, rr(0.07, 0.14) * s);
    P.life = rr(1.3, 2.2); P.drag = 0.4; P.grav = 1; P.bounce = FLOOR.STICK; P.frame = FRAME.BLOB; P.rot = rnd() * TAU;
    P.alpha = 0.95; P.fadeIn = 0; P.fadePow = 4; col(c, 1);
    fx.A.spawn(P);
  }
}

function glueSplat(fx, pos, o) {
  const s = clamp(o.scale, 0.2, 3);
  const c = pick(o, COL.glue);
  glueBlobs(fx, pos, _up, cnt(fx, 16 * s, 5), s, c);
  _q.set(pos.x, floorY(fx), pos.z);
  ring(fx, false, _q, 0.15 * s, 0.85 * s, 0.12 * s, 0.12 * s, 1.8, c, 0.8, 0.85);
  S.mode = RING_MODE.DISC; S.noise = 0.45; S.easePow = 7; S.fadeStart = 0.55; S.fadePow = 1.5; spawnRing(fx, false);
  glints(fx, _p.set(pos.x, pos.y + 0.1, pos.z), cnt(fx, 4), 0.3 * s, COL.white, 1.2, 0.4, 0.3);
}

/** Ice burst: crystals, cold mist, glints, frost decal, cold light. */
function iceBurst(fx, pos, o) {
  const s = clamp(o.scale, 0.2, 3);
  const c = pick(o, COL.ice);
  iceShards(fx, pos, _up, 1.5, cnt(fx, 18 * s, 5), 2 * s, 5 * s, Math.sqrt(s), c);
  coldMist(fx, pos, cnt(fx, 10 * s, 3), s, 0.9 * s, 0.28, 1.8, c);
  glints(fx, pos, cnt(fx, 14 * s, 4), 0.6 * s, _c2.copy(c).lerp(COL.white, 0.5), 2.8, 0.6, 1.2);
  glow(fx, pos, c, 1.8, 0.3 * s, 1.2 * s, 0.14, 0.6);
  _q.set(pos.x, floorY(fx), pos.z);
  if (pos.y - _q.y < 2.2) {
    ring(fx, false, _q, 0.3 * s, 1.25 * s, 0.35 * s, 0.35 * s, 2.6, _c2.copy(c).lerp(COL.white, 0.55), 1, 0.55);
    S.mode = RING_MODE.DISC; S.noise = 0.35; S.easePow = 5; S.fadeStart = 0.55; spawnRing(fx, false);
  }
  fx.lights.flash(pos, c, 18 * s, 0.3, 6);
}

/** Smoky teleport / poof in a colour (swirling smoke, wisps, faint column, floor ring, dim light). */
function teleport(fx, pos, o) {
  const s = clamp(o.scale, 0.2, 3);
  const c = pick(o, COL.violet);
  const y = Math.min(pos.y, floorY(fx) + 1.0);
  const base = floorY(fx);
  for (let i = 0, n = cnt(fx, 16 * s, 5); i < n; i++) {
    const a = rnd() * TAU, r = rr(0.15, 0.45) * s;
    P.reset().at(pos.x + Math.cos(a) * r, y + rr(-0.8, 0.8) * s, pos.z + Math.sin(a) * r).vel(0, rr(0.3, 0.9), 0)
      .sizes(rr(0.25, 0.4) * s, rr(0.8, 1.1) * s);
    P.cx = pos.x; P.cz = pos.z; P.orbitW = rr(2, 4) * rs(); P.orbitPull = rr(0.3, 0.8) * s;
    P.life = rr(0.8, 1.3); P.drag = 1.5; P.grav = -0.08; P.frame = FRAME.SMOKE; P.rot = rnd() * TAU; P.spin = rr(-1, 1);
    P.alpha = 0.42; P.fadeIn = 0.06; P.fadePow = 1.3;
    _c2.copy(COL.smokeDark).lerp(c, 0.25); col(_c2); colEnd(_c2.copy(COL.smokeLight).lerp(c, 0.2));
    fx.A.spawn(P);
  }
  for (let i = 0, n = cnt(fx, 12 * s, 4); i < n; i++) {
    const a = rnd() * TAU, r = rr(0.2, 0.6) * s;
    P.reset().at(pos.x + Math.cos(a) * r, y + rr(-0.9, 0.9) * s, pos.z + Math.sin(a) * r).vel(0, rr(0.5, 1.5), 0)
      .sizes(rr(0.25, 0.45) * s, 0.1 * s);
    P.cx = pos.x; P.cz = pos.z; P.orbitW = rr(4, 7); P.orbitPull = -0.3 * s;
    P.life = rr(0.35, 0.65); P.drag = 1; P.frame = FRAME.WISP; P.rot = rnd() * TAU; P.alpha = 0.55; P.fadeIn = 0.05; P.fadePow = 1.2;
    col(c, 1.8, 0.1);
    fx.X.spawn(P);
  }
  _q.set(pos.x, base, pos.z);
  S.reset(); S.pos.copy(_q); S.r0 = 0.45 * s; S.r1 = 0.3 * s; S.h0 = 2.3 * s; S.h1 = 2.6 * s; S.life = 0.4; S.fadeIn = 0.05;
  S.color.setRGB(c.r * 1.6 + 0.2, c.g * 1.6 + 0.2, c.b * 1.6 + 0.2); S.opacity = 0.45; S.scroll = 3; S.fadePowV = 1.4; S.edge = 0.7;
  fx.columns.spawn(S);
  ring(fx, true, _q, 0.1, 1.1 * s, 0.08 * s, 0.02, 0.45, c, 2, 0.8); spawnRing(fx, true);
  fx.lights.flash(_p.set(pos.x, y, pos.z), c, 12 * s, 0.3, 5);
}

/** Swap flash: sharper, brighter teleport with a white core; `o.target` also flashes the other end. */
function swapFlash(fx, pos, o) {
  const s = clamp(o.scale, 0.2, 3);
  const c = pick(o, COL.violet);
  glow(fx, pos, _c2.copy(c).lerp(COL.white, 0.5), 4, 0.4 * s, 1.8 * s, 0.18, 0.9);
  ring(fx, true, pos, 0.1 * s, 1.1 * s, 0.06 * s, 0.01, 0.3, c, 2.5, 1, ORIENT.BILLBOARD); spawnRing(fx, true);
  sparks(fx, pos, _up, Math.PI, cnt(fx, 12 * s), 3, 7, c, 3, 0.3, 0.02, 0.1, 0.03);
  teleport(fx, pos, o);
  if (o.target) { _swapTo.copy(o.target); o.target = null; swapFlash(fx, _swapTo, o); }
}

/** Stage-magic smoke puff: dense pale smoke + gold glints. */
function vanishSmoke(fx, pos, o) {
  const s = clamp(o.scale, 0.2, 4);
  const c = o.hasColor ? o.color : COL.chalk;
  smoke(fx, pos, cnt(fx, 22 * s, 6), s, 2.4 * s, _c2.copy(c).lerp(COL.chalk, 0.5), COL.smokeLight, 0.5, 1.4, 0.15, 0.25);
  glints(fx, pos, cnt(fx, 10 * s, 3), 0.5 * s, COL.amber, 2.5, 0.6, 1.5);
}

/** Clone forming: dark wisps spiralling inward + a quick fresnel pop. */
function cloneSpawn(fx, pos, o) {
  const s = clamp(o.scale, 0.2, 3);
  const c = pick(o, COL.violet);
  for (let i = 0, n = cnt(fx, 16 * s, 5); i < n; i++) {
    const a = rnd() * TAU, r = rr(0.8, 1.3) * s;
    P.reset().at(pos.x + Math.cos(a) * r, pos.y + rr(-0.8, 0.8) * s, pos.z + Math.sin(a) * r).vel(0, rr(-0.2, 0.2), 0)
      .sizes(rr(0.2, 0.35) * s, 0.05 * s);
    P.cx = pos.x; P.cz = pos.z; P.orbitW = rr(4, 6); P.orbitPull = -rr(1.4, 2.2) * s;
    P.life = rr(0.4, 0.6); P.frame = FRAME.WISP; P.rot = rnd() * TAU; P.alpha = 0.6; P.fadeIn = 0.2; P.fadePow = 1;
    col(c, 2, 0.05);
    fx.X.spawn(P);
  }
  smoke(fx, pos, cnt(fx, 6 * s, 2), 0.8 * s, 0.6, COL.smokeDark, COL.smokeLight, 0.25, 0.9, 0.1, 0.5);
  S.reset(); S.pos.copy(pos); S.r0 = 0.25 * s; S.r1 = 1.0 * s; S.life = 0.4; S.color.setRGB(c.r * 1.6, c.g * 1.6, c.b * 1.6);
  S.opacity = 0.6; S.power = 2; S.fill = 0.12; S.fadeIn = 0.1; S.fadePow = 2; S.orient = ORIENT.FLOOR;
  fx.spheres.spawn(S);
}

/** Clone breaking apart: glassy shards drifting up + thin smoke + glints. */
function cloneDissolve(fx, pos, o) {
  const s = clamp(o.scale, 0.2, 3);
  const c = pick(o, COL.violet);
  for (let i = 0, n = cnt(fx, 18 * s, 6); i < n; i++) {
    randUnit(_d);
    const v = rr(0.8, 2.8) * s;
    P.reset().at(pos.x + _d.x * 0.3 * s, pos.y + _d.y * 0.7 * s, pos.z + _d.z * 0.3 * s).vel(_d.x * v, _d.y * v + 0.9, _d.z * v)
      .sizes(rr(0.05, 0.11) * s, 0.02 * s);
    P.life = rr(0.5, 0.9); P.drag = 2.5; P.grav = -0.1; P.frame = FRAME.SHARD; P.rot = rnd() * TAU; P.spin = rr(-7, 7);
    P.alpha = 0.9; P.fadeIn = 0; P.fadePow = 1.2; col(c, 2.2, 0.1); colEnd(c, 0.6);
    fx.X.spawn(P);
  }
  smoke(fx, pos, cnt(fx, 7 * s, 2), 0.9 * s, 0.9, _c2.copy(COL.smokeDark).lerp(c, 0.3), COL.smokeLight, 0.28, 1.0, 0.25, 0.4);
  glints(fx, pos, cnt(fx, 8 * s, 2), 0.6 * s, c, 2.5, 0.5, 1);
}

/** Energy-shield impact: ripples on the shield plane (normal = o.dir), sparks, flash, light. */
function shieldImpact(fx, pos, o) {
  const s = clamp(o.scale, 0.2, 3);
  const c = pick(o, COL.amber);
  const n = o.hasDir ? o.dir : _d.set(0, 0, 1);
  ring(fx, true, pos, 0.05 * s, 0.95 * s, 0.08 * s, 0.015 * s, 0.45, c, 2.4, 1, ORIENT.NORMAL, n); spawnRing(fx, true);
  ring(fx, true, pos, 0.03 * s, 0.6 * s, 0.05 * s, 0.01 * s, 0.4, c, 1.8, 0.8, ORIENT.NORMAL, n);
  S.delay = 0.08; spawnRing(fx, true);
  glow(fx, pos, c, 2.5, 0.25 * s, 0.9 * s, 0.12, 0.8);
  sparks(fx, pos, n, 1.1, cnt(fx, 14 * s, 4), 2.5, 7, c, 3, 0.4, 0.022, 0.6, 0.03, 0.3);
  fx.lights.flash(pos, c, 14 * s, 0.25, 5);
}

/** Heavy body charge: dust cone thrown back and sideways from the feet. o.dir = charge direction. */
function tackleDust(fx, pos, o) {
  const s = clamp(o.scale, 0.3, 3);
  const y = floorY(fx);
  const dx = o.hasDir ? o.dir.x : 0, dz = o.hasDir ? o.dir.z : 1;
  for (let i = 0, n = cnt(fx, 12 * s, 4); i < n; i++) {
    const side = rr(-1.5, 1.5), back = rr(0.6, 2.4);
    P.reset().at(pos.x + rr(-0.2, 0.2), y + rr(0.05, 0.2), pos.z + rr(-0.2, 0.2))
      .vel(-dx * back - dz * side, rr(0.2, 0.8), -dz * back + dx * side).sizes(0.2 * s, rr(0.7, 1.0) * s);
    P.life = rr(0.8, 1.3); P.drag = 2.6; P.turb = 0.4; P.bounce = FLOOR.SLIDE; P.frame = FRAME.SMOKE; P.rot = rnd() * TAU;
    P.alpha = 0.3; P.fadeIn = 0.08; P.fadePow = 1.3; col(pick(o, COL.dust));
    fx.A.spawn(P);
  }
  _p.set(pos.x, y + 0.05, pos.z);
  specks(fx, _p, _d.set(-dx, 0.8, -dz).normalize(), 0.8, cnt(fx, 6 * s), 1.5, 3.5, COL.dustDark, 0.016, 0.3, 1);
}

/** Earthquake slam: dust shock ring, cracks decal, flying debris, rolling dust cloud. */
function earthquake(fx, pos, o) {
  const s = clamp(o.scale, 0.3, 3);
  const R = o.radius > 0 ? o.radius : 9 * s;
  const y = floorY(fx);
  _q.set(pos.x, y, pos.z);
  const c = pick(o, COL.dust);
  ring(fx, false, _q, 0.3, R, 0.8, 0.3, 1.0, c, 1, 0.45); S.noise = 0.12; spawnRing(fx, false);
  ring(fx, false, _q, 0.2, R * 0.7, 0.5, 0.2, 0.9, c, 0.9, 0.35); S.noise = 0.15; S.delay = 0.12; spawnRing(fx, false);
  ring(fx, false, _q, 2.4 * s, 2.4 * s, 0.1, 0.1, 3.2, COL.smokeDark, 0.08, 0.7);
  S.mode = RING_MODE.CRACKS; S.fadeIn = 0.02; S.fadeStart = 0.5; S.easePow = 1; spawnRing(fx, false);
  if (o.hasColor) { ring(fx, true, _q, 0.3, R * 0.8, 0.3, 0.08, 0.5, o.color, 2, 0.6); S.noise = 0.1; spawnRing(fx, true); }
  // debris chunks
  for (let i = 0, n = cnt(fx, 30 * s, 8); i < n; i++) {
    const a = rnd() * TAU, r = rr(0, 1.6) * s, out = rr(1, 3);
    P.reset().at(pos.x + Math.cos(a) * r, y + 0.05, pos.z + Math.sin(a) * r).vel(Math.cos(a) * out, rr(3, 7), Math.sin(a) * out)
      .sizes(rr(0.04, 0.1), rr(0.04, 0.1));
    P.life = rr(1.0, 1.7); P.drag = 0.3; P.grav = 1; P.bounce = 0.3; P.frame = FRAME.CHUNK; P.rot = rnd() * TAU; P.spin = rr(-12, 12);
    P.alpha = 1; P.fadeIn = 0; P.fadePow = 5; col(COL.dustDark, rr(0.6, 1));
    fx.A.spawn(P);
  }
  // rolling dust cloud travelling outward with the shock front
  for (let i = 0, n = cnt(fx, 30 * s, 8); i < n; i++) {
    const a = rnd() * TAU, r = rr(0.2, 0.35) * R, v = rr(0.4, 0.8) * R;
    P.reset().at(pos.x + Math.cos(a) * r, y + rr(0.05, 0.3), pos.z + Math.sin(a) * r).vel(Math.cos(a) * v, rr(0.2, 0.9), Math.sin(a) * v)
      .sizes(rr(0.4, 0.6) * s, rr(1.3, 1.8) * s);
    P.life = rr(1.2, 2.0); P.drag = 1.6; P.turb = 0.5; P.bounce = FLOOR.SLIDE; P.frame = FRAME.SMOKE; P.rot = rnd() * TAU;
    P.spin = rr(-0.5, 0.5); P.alpha = 0.28; P.fadeIn = 0.06; P.fadePow = 1.3; col(c);
    fx.A.spawn(P);
  }
}

/** Turret muzzle: compressed-air flash along o.dir, cone sparks, puff of smoke, a very short light. */
function turretMuzzle(fx, pos, o) {
  const s = clamp(o.scale, 0.3, 3);
  const dir = o.hasDir ? o.dir : _d.set(0, 0, 1);
  const c = pick(o, COL.warm);
  P.reset().at(pos.x + dir.x * 0.1, pos.y + dir.y * 0.1, pos.z + dir.z * 0.1).vel(dir.x * 2, dir.y * 2, dir.z * 2)
    .sizes(0.2 * s, 0.32 * s);
  P.life = 0.07; P.drag = 20; P.stretch = 0.12; P.frame = FRAME.SOFT; P.alpha = 1; P.fadeIn = 0; P.fadePow = 2; col(c, 4.5);
  fx.X.spawn(P);
  glow(fx, pos, c, 3, 0.15 * s, 0.4 * s, 0.06, 0.9);
  _t.copy(dir);
  sparks(fx, pos, _t, 0.35, cnt(fx, 8 * s, 3), 8, 14, COL.amber, 3.5, 0.12, 0.018, 0.2, 0.02);
  for (let i = 0, n = cnt(fx, 4 * s, 2); i < n; i++) {
    randCone(_d, _t, 0.6);
    const v = rr(0.6, 1.6);
    P.reset().at(pos.x, pos.y, pos.z).vel(_d.x * v, _d.y * v + 0.2, _d.z * v).sizes(0.08 * s, rr(0.3, 0.45) * s);
    P.life = rr(0.5, 0.8); P.drag = 2.5; P.turb = 0.5; P.frame = FRAME.SMOKE; P.rot = rnd() * TAU; P.alpha = 0.25; P.fadeIn = 0.05;
    col(COL.smokeLight, 1.2);
    fx.A.spawn(P);
  }
  fx.lights.flash(pos, c, 10 * s, 0.08, 4);
}

/** Revive: light shaft from above, floor ring, rising motes, warm light. */
function reviveBeam(fx, pos, o) {
  const s = clamp(o.scale, 0.3, 3);
  const c = pick(o, COL.warm);
  const y = floorY(fx);
  _q.set(pos.x, y, pos.z);
  S.reset(); S.pos.copy(_q); S.r0 = 0.6 * s; S.r1 = 0.4 * s; S.h0 = 7; S.h1 = 7; S.life = 1.3; S.fadeIn = 0.08; S.fadeStart = 0.3;
  S.color.setRGB(c.r * 1.4 + 1.2, c.g * 1.4 + 1.1, c.b * 1.4 + 1.0); S.opacity = 0.5; S.scroll = 2.2; S.fadePowV = 0.8; S.edge = 0.75;
  fx.columns.spawn(S);
  ring(fx, true, _q, 0.2, 1.3 * s, 0.1 * s, 0.02, 0.7, c, 2.2, 0.9); spawnRing(fx, true);
  for (let i = 0, n = cnt(fx, 26 * s, 8); i < n; i++) {
    const a = rnd() * TAU, r = rr(0.2, 0.55) * s;
    P.reset().at(pos.x + Math.cos(a) * r, y + rr(0, 0.5), pos.z + Math.sin(a) * r).vel(0, rr(1.2, 2.8), 0).sizes(rr(0.04, 0.07), 0.01);
    P.cx = pos.x; P.cz = pos.z; P.orbitW = rr(1, 2.2); P.orbitPull = 0;
    P.life = rr(0.8, 1.4); P.drag = 0.4; P.frame = FRAME.SOFT; P.alpha = 1; P.fadeIn = 0.15; P.fadePow = 1;
    col(c, 2.2, 0.4);
    fx.X.spawn(P);
  }
  fx.lights.flash(_p.set(pos.x, y + 1.2, pos.z), _c2.copy(c).lerp(COL.warm, 0.5), 22 * s, 0.9, 7);
}

/** Elimination: bigger dust/chalk burst, spray, team-coloured sparks, floor ring, flash. */
function elimination(fx, pos, o) {
  const s = clamp(o.scale, 0.3, 3);
  const c = pick(o, COL.warm);
  const dir = o.hasDir ? o.dir : _up;
  glow(fx, pos, COL.white, 3, 0.4 * s, 1.5 * s, 0.14, 0.9);
  for (let i = 0, n = cnt(fx, 14 * s, 5); i < n; i++) {
    randUnit(_d);
    const v = rr(1.5, 3.5) * s;
    P.reset().at(pos.x, pos.y, pos.z).vel(_d.x * v + dir.x * 1.5, _d.y * v + 0.4, _d.z * v + dir.z * 1.5).sizes(0.2 * s, rr(0.8, 1.0) * s);
    P.life = rr(0.8, 1.3); P.drag = 3.5; P.turb = 0.5; P.frame = FRAME.SMOKE; P.rot = rnd() * TAU; P.spin = rr(-1, 1);
    P.alpha = 0.35; P.fadeIn = 0.05; P.fadePow = 1.3; col(COL.chalk, 0.95);
    fx.A.spawn(P);
  }
  specks(fx, pos, dir, 1.0, cnt(fx, 12 * s, 4), 3, 7, COL.white, 0.016, FLOOR.STICK, 1);
  sparks(fx, pos, _up, Math.PI * 0.8, cnt(fx, 22 * s, 6), 3, 8, c, 3, 0.8, 0.028, 0.8, 0.03, 0.3);
  _q.set(pos.x, floorY(fx), pos.z);
  ring(fx, true, _q, 0.2, 1.9 * s, 0.14, 0.03, 0.6, c, 2.2, 0.8); spawnRing(fx, true);
  fx.lights.flash(pos, c, 22 * s, 0.35, 6);
}

/** Cloak / decloak shimmer (small refraction-like sparkle dissolve). */
function cloak(fx, pos, o) {
  const s = clamp(o.scale, 0.2, 3);
  const c = pick(o, COL.beam);
  for (let i = 0, n = cnt(fx, 20 * s, 6); i < n; i++) {
    const a = rnd() * TAU, r = rr(0.15, 0.4) * s;
    P.reset().at(pos.x + Math.cos(a) * r, pos.y + rr(-0.9, 0.9) * s, pos.z + Math.sin(a) * r).vel(0, rr(0.2, 0.7), 0).sizes(0.05, 0);
    P.life = rr(0.4, 0.8); P.drag = 1; P.frame = FRAME.SOFT; P.alpha = 0.9; P.fadeIn = 0.2; col(c, 1.6, 0.3);
    fx.X.spawn(P);
  }
}

// ------------------------------------------------------------------------------------------------ emitters
/** Fire colours for an emitter: hot (A), deep (B). */
function fireColors(em) {
  const c = emCol(em, COL.fire);
  em.colA.setRGB(c.r * 4 + 1.3, c.g * 4 + 0.9, c.b * 4 + 0.35);
  em.colB.setRGB(c.r * 0.9, c.g * 0.25, c.b * 0.15);
}

const fireTrail = {
  kind: 'emitter', duration: 1.5, ballTrail: true,
  start(fx, em) {
    fireColors(em);
    const s = em.scale;
    em.light = fx.lights.attach(em, _c.copy(emCol(em, COL.fire)).lerp(COL.warm, 0.3), 8 * s + 3, 4 + 2.5 * s, 0.35);
    if (!em.onPlayer && em.isBall) em.ribbons[0] = fx.ribbons.spawn(em, _c2.set(em.colA.r * 0.6, em.colA.g * 0.5, em.colA.b * 0.4), 0.22 * s, 0.14, 0.75, 0.6);
  },
  tick(fx, em, dt) {
    const s = em.scale;
    const n = emitCount(fx, em, 7, 34 * s, dt);
    for (let i = 0; i < n; i++) {
      along(em, (i + rnd()) / n);
      randUnit(_d);
      P.reset().at(_p.x + _d.x * 0.04 * s, _p.y + _d.y * 0.04 * s, _p.z + _d.z * 0.04 * s)
        .vel(em.vel.x * 0.08 + _d.x * 0.5, em.vel.y * 0.08 + _d.y * 0.5 + 0.7, em.vel.z * 0.08 + _d.z * 0.5)
        .sizes(rr(0.22, 0.34) * s, 0.06 * s);
      P.life = rr(0.16, 0.3); P.drag = 3; P.frame = FRAME.SMOKE; P.rot = rnd() * TAU; P.spin = rr(-3, 3);
      P.alpha = 0.8; P.fadeIn = 0.05; P.fadePow = 1.2;
      P.color(em.colA.r, em.colA.g, em.colA.b); P.colorEnd(em.colB.r, em.colB.g, em.colB.b);
      fx.X.spawn(P);
      if (rnd() < 0.3) {
        P.reset().at(_p.x, _p.y, _p.z).vel(em.vel.x * 0.03 + _d.x * 0.3, 0.5 + _d.y * 0.2, em.vel.z * 0.03 + _d.z * 0.3)
          .sizes(0.12 * s, rr(0.5, 0.8) * s);
        P.life = rr(0.6, 1.0); P.grav = -0.12; P.drag = 1.8; P.turb = 0.8; P.frame = FRAME.SMOKE; P.rot = rnd() * TAU;
        P.alpha = 0.28; P.fadeIn = 0.25; col(COL.smokeDark, 0.9); colEnd(COL.smokeLight);
        fx.A.spawn(P);
      }
      if (rnd() < 0.22) {
        const v = rr(0.8, 2.5);
        P.reset().at(_p.x, _p.y, _p.z).vel(_d.x * v + em.vel.x * 0.1, _d.y * v + 0.5 + em.vel.y * 0.1, _d.z * v + em.vel.z * 0.1)
          .sizes(0.022, 0.012);
        P.life = rr(0.35, 0.8); P.grav = 0.35; P.drag = 1.5; P.stretch = 0.03; P.frame = FRAME.SPARK; P.bounce = 0.3;
        P.alpha = 1; P.fadeIn = 0; P.fadePow = 1.5; P.color(em.colA.r * 1.2, em.colA.g, em.colA.b * 0.6); P.colorEnd(em.colB.r * 1.5, em.colB.g, em.colB.b);
        fx.X.spawn(P);
      }
    }
  },
  burst(fx, pos, o) {
    const s = clamp(o.scale, 0.2, 3);
    const c = pick(o, COL.fire);
    const dir = o.hasDir ? o.dir : _up;
    for (let i = 0, n = cnt(fx, 12 * s, 4); i < n; i++) {
      randCone(_d, dir, 0.7);
      const v = rr(1, 3.5) * s;
      P.reset().at(pos.x, pos.y, pos.z).vel(_d.x * v, _d.y * v + 0.4, _d.z * v).sizes(rr(0.2, 0.3) * s, 0.5 * s);
      P.life = rr(0.2, 0.4); P.drag = 3.5; P.frame = FRAME.SMOKE; P.rot = rnd() * TAU; P.spin = rr(-2, 2); P.alpha = 0.85; P.fadeIn = 0.03;
      P.color(c.r * 4 + 1.3, c.g * 4 + 0.9, c.b * 4 + 0.35); P.colorEnd(c.r * 0.9, c.g * 0.25, c.b * 0.15);
      fx.X.spawn(P);
    }
    sparks(fx, pos, dir, 0.9, cnt(fx, 8 * s, 3), 2, 6, c, 4, 0.6, 0.022, 0.4, 0.03, 0.3);
    smoke(fx, pos, cnt(fx, 3 * s), 0.6 * s, 0.8, COL.smokeDark, COL.smokeLight, 0.25, 1, 0.3, 0.1);
    fx.lights.flash(pos, c, 14 * s, 0.25, 5);
  },
};

const beamTrail = {
  kind: 'emitter', duration: 1.5, ballTrail: true,
  start(fx, em) {
    const c = emCol(em, COL.beam);
    const s = em.scale;
    em.light = fx.lights.attach(em, c, 10 * s + 4, 6 + 2 * s, 0.08);
    em.ribbons[0] = fx.ribbons.spawn(em, _c2.setRGB(c.r * 2.2, c.g * 2.2, c.b * 2.2), 0.5 * s, 0.3, 0.65, 0);
    em.ribbons[1] = fx.ribbons.spawn(em, _c2.setRGB(c.r * 1.5 + 1.8, c.g * 1.5 + 1.8, c.b * 1.5 + 1.8), 0.14 * s, 0.22, 1, 1.5);
  },
  tick(fx, em, dt) {
    const c = emCol(em, COL.beam);
    const s = em.scale;
    const n = emitCount(fx, em, 6, 24 * s, dt);
    for (let i = 0; i < n; i++) {
      along(em, (i + rnd()) / n);
      randUnit(_d);
      const v = rr(0.5, 2.5);
      P.reset().at(_p.x, _p.y, _p.z).vel(_d.x * v + em.vel.x * 0.05, _d.y * v + em.vel.y * 0.05, _d.z * v + em.vel.z * 0.05)
        .sizes(0.018 * s, 0.01);
      P.life = rr(0.15, 0.4); P.drag = 2.5; P.stretch = 0.02; P.frame = FRAME.SPARK; P.alpha = 1; P.fadeIn = 0; P.fadePow = 1.5;
      col(c, 3.2, 0.4);
      fx.X.spawn(P);
    }
    const g = emitCount(fx, em, 3, 10 * s, dt, 'acc2');
    for (let i = 0; i < g; i++) {
      along(em, (i + rnd()) / g);
      P.reset().at(_p.x, _p.y, _p.z).sizes(0.32 * s, 0.06 * s);
      P.life = 0.25; P.drag = 0; P.frame = FRAME.SOFT; P.alpha = 0.35; P.fadeIn = 0; col(c, 1.6);
      fx.X.spawn(P);
    }
  },
  burst(fx, pos, o) {
    const s = clamp(o.scale, 0.2, 3);
    const c = pick(o, COL.beam);
    const dir = o.hasDir ? o.dir : _up;
    glow(fx, pos, c, 3, 0.3 * s, 1.2 * s, 0.15, 0.9);
    sparks(fx, pos, dir, 0.8, cnt(fx, 14 * s, 4), 3, 9, c, 3.2, 0.3, 0.02, 0.2, 0.025);
    fx.lights.flash(pos, c, 14 * s, 0.25, 6);
  },
};

const iceTrail = {
  kind: 'emitter', duration: 1.5, ballTrail: true,
  start(fx, em) {
    const c = emCol(em, COL.ice);
    if (em.isBall) em.ribbons[0] = fx.ribbons.spawn(em, _c2.setRGB(c.r * 1.1 + 0.3, c.g * 1.1 + 0.4, c.b * 1.1 + 0.6), 0.14 * em.scale, 0.22, 0.5, 0.4);
  },
  tick(fx, em, dt) {
    const c = emCol(em, COL.ice);
    const s = em.scale;
    const n = emitCount(fx, em, 5, 10 * s, dt);
    for (let i = 0; i < n; i++) {
      along(em, (i + rnd()) / n);
      P.reset().at(_p.x + rr(-0.05, 0.05), _p.y + rr(-0.05, 0.05), _p.z + rr(-0.05, 0.05)).vel(rr(-0.2, 0.2), rr(-0.1, 0.15), rr(-0.2, 0.2))
        .sizes(0.1 * s, rr(0.4, 0.55) * s);
      P.life = rr(0.6, 1.0); P.drag = 2; P.grav = 0.08; P.turb = 0.3; P.frame = FRAME.SMOKE; P.rot = rnd() * TAU; P.alpha = 0.2;
      P.fadeIn = 0.15; col(_c2.copy(c).lerp(COL.white, 0.6));
      fx.A.spawn(P);
      if (rnd() < 0.7) glints(fx, _p, 1, 0.06, c, 2.4, 0.5, 0.3);
      if (rnd() < 0.25) iceShards(fx, _p, _up, Math.PI, 1, 0.3, 1.2, 0.5 * s, c);
    }
  },
  burst(fx, pos, o) {
    const s = clamp(o.scale, 0.2, 3);
    const c = pick(o, COL.ice);
    coldMist(fx, pos, cnt(fx, 4 * s, 2), 0.6 * s, 0.4, 0.22, 1.1, c);
    glints(fx, pos, cnt(fx, 5 * s, 2), 0.3 * s, c, 2.4, 0.5, 0.4);
    iceShards(fx, pos, _up, 1.2, cnt(fx, 3 * s), 0.6, 1.8, 0.6 * s, c);
  },
};

/** Cold mist: around a body (attached to a player) or pooling on the ground around an anchor. */
const frozenMist = {
  kind: 'emitter', duration: 2.5,
  start(fx, em) {
    const c = emCol(em, COL.ice);
    const s = em.scale;
    S.reset(); S.follow = em; S.pos.copy(em.pos); S.r0 = 0.35 * s; S.r1 = (em.onPlayer ? 0.8 : 1.3) * s; S.w0 = S.w1 = 0.25 * s;
    S.life = Math.max(1.2, Math.min(em.duration, 30)); S.hold = true; S.fadeStart = 0.8; S.easePow = 4; S.fadePow = 1.2;
    S.mode = RING_MODE.DISC; S.noise = 0.35; S.opacity = 0.42; S.color.copy(_c2.copy(c).lerp(COL.white, 0.55));
    em.shapes[0] = fx.ringsA.spawn(S);
  },
  tick(fx, em, dt) {
    const c = emCol(em, COL.ice);
    const s = em.scale;
    const n = emitCount(fx, em, 0, 12 * s, dt);
    const y = floorY(fx);
    for (let i = 0; i < n; i++) {
      const a = rnd() * TAU;
      let r, h;
      if (em.onPlayer) { r = rr(0.18, 0.35); h = y + rr(0.2, 1.6); } else { r = rr(0, 1.1) * s; h = em.pos.y + rr(0, 0.35); }
      const cs = Math.cos(a), sn = Math.sin(a);
      P.reset().at(em.pos.x + cs * r, h, em.pos.z + sn * r).vel(cs * rr(0.1, 0.3), -rr(0.08, 0.3), sn * rr(0.1, 0.3))
        .sizes(rr(0.15, 0.25) * s, rr(0.5, 0.75) * s);
      P.life = rr(1.1, 1.8); P.drag = 1.2; P.grav = 0.03; P.turb = 0.25; P.bounce = FLOOR.SLIDE; P.frame = FRAME.SMOKE;
      P.rot = rnd() * TAU; P.spin = rr(-0.3, 0.3); P.alpha = 0.17; P.fadeIn = 0.3; P.fadePow = 1.2;
      col(_c2.copy(c).lerp(COL.white, 0.65));
      fx.A.spawn(P);
    }
    const g = emitCount(fx, em, 0, 5 * s, dt, 'acc2');
    for (let i = 0; i < g; i++) {
      _p.set(em.pos.x, em.onPlayer ? y + rr(0.3, 1.7) : em.pos.y + rr(0, 0.5), em.pos.z);
      glints(fx, _p, 1, em.onPlayer ? 0.35 : 1.0 * s, c, 2.6, 0.7, 0.2);
    }
  },
  burst(fx, pos, o) {
    const s = clamp(o.scale, 0.2, 4);
    const c = pick(o, COL.ice);
    coldMist(fx, pos, cnt(fx, 5 * s, 2), s, 0.35 * s, 0.2, 1.6, c);
    glints(fx, pos, cnt(fx, 3 * s, 1), 0.6 * s, c, 2.4, 0.7, 0.2);
  },
};

const magnetField = {
  kind: 'emitter', duration: 1.5,
  start(fx, em) {
    const c = emCol(em, COL.magnet);
    const R = em.radius > 0 ? em.radius : 5 * em.scale;
    em.radius = R;
    S.reset(); S.follow = em; S.pos.copy(em.pos); S.r0 = R * 0.9; S.r1 = R; S.w0 = S.w1 = 0.08; S.life = Math.min(em.duration, 30);
    S.hold = true; S.fadeStart = 0.85; S.fadeIn = 0.1; S.mode = RING_MODE.RIPPLE; S.inner = 1; S.opacity = 0.35;
    S.color.setRGB(c.r * 1.8, c.g * 1.8, c.b * 1.8); S.pulseAmp = 0.25; S.pulseFreq = 9;
    em.shapes[0] = fx.ringsX.spawn(S);
    S.reset(); S.follow = em; S.pos.copy(em.pos); S.offset.set(0, 0.2, 0); S.r0 = R * 0.95; S.r1 = R; S.life = Math.min(em.duration, 30);
    S.hold = true; S.fadeStart = 0.85; S.fadeIn = 0.15; S.opacity = 0.14; S.power = 3.5; S.fill = 0; S.bands = 24;
    S.color.setRGB(c.r * 1.5, c.g * 1.5, c.b * 1.5);
    em.shapes[1] = fx.spheres.spawn(S);
    em.light = fx.lights.attach(em, c, 6, 7, 0.15);
  },
  tick(fx, em, dt) {
    const c = emCol(em, COL.magnet);
    const R = em.radius;
    const n = emitCount(fx, em, 0, 42, dt);
    for (let i = 0; i < n; i++) {
      const a = rnd() * TAU, r = rr(0.55, 1) * R, pull = rr(0.6, 1.0) * R;
      P.reset().at(em.pos.x + Math.cos(a) * r, em.pos.y + rr(0.25, 2.2), em.pos.z + Math.sin(a) * r).vel(0, rr(-0.2, 0.3), 0)
        .sizes(0.022, 0.012);
      P.cx = em.pos.x; P.cz = em.pos.z; P.orbitW = rr(1.8, 3.2); P.orbitPull = -pull;
      P.life = Math.min(1.3, (r - 0.3) / pull); P.drag = 0.5; P.stretch = 0.03; P.frame = FRAME.SPARK; P.alpha = 0.9;
      P.fadeIn = 0.25; P.fadePow = 0.6; col(c, 2.6, 0.2);
      fx.X.spawn(P);
    }
  },
};

const stasisBubble = {
  kind: 'emitter', duration: 2,
  start(fx, em) {
    const c = emCol(em, COL.chrono);
    const s = em.scale;
    S.reset(); S.follow = em; S.pos.copy(em.pos); S.r0 = 0.25 * s; S.r1 = 0.55 * s; S.easePow = 5; S.life = Math.min(em.duration, 30) + 0.3;
    S.hold = true; S.fadeStart = 0.9; S.fadeIn = 0.05; S.opacity = 0.7; S.power = 2.2; S.fill = 0.08; S.bands = 30;
    S.color.setRGB(c.r * 1.6 + 0.2, c.g * 1.6 + 0.3, c.b * 1.6 + 0.35);
    em.shapes[0] = fx.spheres.spawn(S);
    S.reset(); S.follow = em; S.pos.copy(em.pos); S.r0 = 0.62 * s; S.r1 = 0.62 * s; S.w0 = S.w1 = 0.02 * s;
    S.life = Math.min(em.duration, 30) + 0.3; S.hold = true; S.fadeStart = 0.9; S.orient = ORIENT.BILLBOARD; S.opacity = 0.6;
    S.mode = RING_MODE.RING; S.noise = 0.04; S.spin = 0.6; S.pulseAmp = 0.4; S.pulseFreq = 6;
    S.color.setRGB(c.r * 2, c.g * 2, c.b * 2);
    em.shapes[1] = fx.ringsX.spawn(S);
    em.light = fx.lights.attach(em, c, 5 * s, 4, 0.1);
    ring(fx, true, em.pos, 0.1 * s, 0.9 * s, 0.05 * s, 0.01, 0.35, c, 2.2, 0.9, ORIENT.BILLBOARD); spawnRing(fx, true);
  },
  tick(fx, em, dt) {
    const c = emCol(em, COL.chrono);
    const s = em.scale;
    const n = emitCount(fx, em, 0, 14 * s, dt);
    for (let i = 0; i < n; i++) {
      randUnit(_d);
      const r = rr(0.1, 0.5) * s;
      // motes hang almost still: time has stopped inside the bubble
      P.reset().at(em.pos.x + _d.x * r, em.pos.y + _d.y * r, em.pos.z + _d.z * r).vel(_d.x * 0.03, _d.y * 0.03, _d.z * 0.03)
        .sizes(0.025, 0.02);
      P.life = rr(0.8, 1.4); P.drag = 0; P.frame = FRAME.SOFT; P.alpha = 1; P.fadeIn = 0.3; P.fadePow = 1; col(c, 2.4, 0.3);
      fx.X.spawn(P);
    }
  },
};

const rewindTrail = {
  kind: 'emitter', duration: 1.2,
  start(fx, em) {
    const c = emCol(em, COL.chrono);
    const s = em.scale;
    em.ribbons[0] = fx.ribbons.spawn(em, _c2.setRGB(c.r * 1.6 + 0.1, c.g * 1.6 + 0.1, c.b * 1.6 + 0.1), 0.42 * s, 0.45, 0.5, 0.35);
    ring(fx, true, em.pos, 0.1 * s, 0.7 * s, 0.03 * s, 0.01, 0.4, c, 2, 0.7, ORIENT.BILLBOARD);
    S.spin = -3; spawnRing(fx, true);
  },
  tick(fx, em, dt) {
    const c = emCol(em, COL.chrono);
    const s = em.scale;
    const n = emitCount(fx, em, 5, 8 * s, dt);
    for (let i = 0; i < n; i++) {
      along(em, (i + rnd()) / n);
      randUnit(_d);
      P.reset().at(_p.x + _d.x * 0.15 * s, _p.y + _d.y * 0.3 * s, _p.z + _d.z * 0.15 * s)
        .vel(-em.vel.x * 0.15 + _d.x * 0.2, -em.vel.y * 0.15 + _d.y * 0.2, -em.vel.z * 0.15 + _d.z * 0.2).sizes(0.08 * s, 0.02);
      P.life = rr(0.3, 0.6); P.drag = 1; P.frame = FRAME.SOFT; P.alpha = 0.8; P.fadeIn = 0.1; col(c, 2.2, 0.2);
      fx.X.spawn(P);
    }
  },
  /** One-shot "ghost streak": wisps sliding backwards along o.dir (time running in reverse). */
  burst(fx, pos, o) {
    const s = clamp(o.scale, 0.1, 3);
    const c = pick(o, COL.chrono);
    const dir = o.hasDir ? o.dir : _up;
    for (let i = 0, n = cnt(fx, 8 * s, 3); i < n; i++) {
      const f = rr(-0.5, 0.5);
      P.reset().at(pos.x + dir.x * f + rr(-0.15, 0.15) * s, pos.y + dir.y * f + rr(-0.6, 0.6) * s, pos.z + dir.z * f + rr(-0.15, 0.15) * s)
        .vel(-dir.x * rr(1, 3), -dir.y * rr(1, 3), -dir.z * rr(1, 3)).sizes(rr(0.15, 0.3) * s, 0.04);
      P.life = rr(0.3, 0.55); P.drag = 3; P.stretch = 0.06; P.frame = FRAME.WISP; P.alpha = 0.6; P.fadeIn = 0.1; col(c, 1.8, 0.15);
      fx.X.spawn(P);
    }
    glints(fx, pos, cnt(fx, 5 * s, 2), 0.5 * s, c, 2.4, 0.5, 0.5);
    ring(fx, true, pos, 0.08 * s, 0.6 * s, 0.03 * s, 0.01, 0.35, c, 1.8, 0.6, ORIENT.BILLBOARD); S.spin = -4; spawnRing(fx, true);
  },
};

/** Chrono's 5 x 5 m temporal zone: square outline + scan lines on the floor, translucent walls, falling motes. */
const temporalZone = {
  kind: 'emitter', duration: 1.8,
  start(fx, em) {
    const c = emCol(em, COL.chrono);
    const half = (em.size > 0 ? em.size : 5 * em.scale) / 2;
    em.radius = half;
    const life = Math.min(em.duration, 30) + 0.4;
    S.reset(); S.follow = em; S.pos.copy(em.pos); S.r0 = half * 0.8; S.r1 = half; S.easePow = 6; S.w0 = S.w1 = 0.06;
    S.life = life; S.hold = true; S.fadeStart = 0.85; S.fadeIn = 0.08; S.mode = RING_MODE.SQUARE; S.inner = 0.12; S.opacity = 0.9;
    S.color.setRGB(c.r * 2.2, c.g * 2.2, c.b * 2.2);
    em.shapes[0] = fx.ringsX.spawn(S);
    S.reset(); S.follow = em; S.pos.set(em.pos.x, fx.floorY, em.pos.z); S.square = true; S.r0 = half * 0.8; S.r1 = half; S.easePow = 6;
    S.h0 = 0.4; S.h1 = 2.4; S.life = life; S.hold = true; S.fadeStart = 0.85; S.fadeIn = 0.1; S.opacity = 0.16; S.scroll = -1.5;
    S.fadePowV = 1.4; S.edge = 0; S.color.setRGB(c.r * 1.6, c.g * 1.6, c.b * 1.6);
    em.shapes[1] = fx.columns.spawn(S);
    if (em.shapes[1]) em.shapes[1].offset.set(0, fx.floorY - em.pos.y, 0);
    em.light = fx.lights.attach(em, c, 7, 7, 0.05);
  },
  tick(fx, em, dt) {
    const c = emCol(em, COL.chrono);
    const half = em.radius;
    const n = emitCount(fx, em, 0, 26 * half / 2.5, dt);
    const y = floorY(fx);
    for (let i = 0; i < n; i++) {
      // motes falling back down: the zone is rewinding
      P.reset().at(em.pos.x + rr(-half, half), y + rr(1.6, 2.5), em.pos.z + rr(-half, half)).vel(0, -rr(0.8, 1.6), 0).sizes(0.03, 0.02);
      P.life = rr(1.0, 1.6); P.drag = 0; P.stretch = 0.05; P.frame = FRAME.SOFT; P.alpha = 0.9; P.fadeIn = 0.2; P.fadePow = 1;
      col(c, 2.4, 0.2);
      fx.X.spawn(P);
    }
  },
};

const ultReady = {
  kind: 'emitter', duration: Infinity, hideWhenCloaked: true,
  start(fx, em) {
    const c = emCol(em, COL.warm);
    S.reset(); S.follow = em; S.pos.copy(em.pos); S.r0 = 0.5; S.r1 = 0.62; S.w0 = S.w1 = 0.07; S.life = 1e6; S.hold = true;
    S.fadeStart = 0.99999; S.fadeIn = 0; S.mode = RING_MODE.RING; S.inner = 0.12; S.noise = 0.05; S.opacity = 0.5;
    S.pulseAmp = 0.45; S.pulseFreq = 3.2; S.color.setRGB(c.r * 1.8, c.g * 1.8, c.b * 1.8);
    em.shapes[0] = fx.ringsX.spawn(S);
    ring(fx, true, em.pos, 0.2, 1.1, 0.08, 0.02, 0.5, c, 2, 0.9); spawnRing(fx, true);
  },
  tick(fx, em, dt) {
    const c = emCol(em, COL.warm);
    const n = emitCount(fx, em, 0, 9, dt);
    const y = floorY(fx);
    for (let i = 0; i < n; i++) {
      const a = rnd() * TAU, r = rr(0.3, 0.5);
      P.reset().at(em.pos.x + Math.cos(a) * r, y + rr(0.05, 0.4), em.pos.z + Math.sin(a) * r).vel(0, rr(0.6, 1.2), 0).sizes(0.05, 0);
      P.cx = em.pos.x; P.cz = em.pos.z; P.orbitW = rr(0.8, 1.6); P.orbitPull = -0.05;
      P.life = rr(0.9, 1.4); P.drag = 0.3; P.frame = FRAME.SOFT; P.alpha = 0.75; P.fadeIn = 0.2; P.fadePow = 1; col(c, 2, 0.15);
      fx.X.spawn(P);
    }
  },
};

const tackleTrail = {
  kind: 'emitter', duration: 0.6,
  start() {},
  tick(fx, em, dt) {
    const n = emitCount(fx, em, 3.5, 0, dt, 'acc', 8);
    if (n > 0 && em.moved > 0.001) {
      _swapTo.copy(em.vel).setY(0);
      const sp = _swapTo.length();
      if (sp > 0.1) _swapTo.divideScalar(sp); else _swapTo.set(0, 0, 1);
      for (let i = 0; i < n; i++) {
        along(em, (i + rnd()) / n, _q);
        tackleDust(fx, _q, subOpts(0.45 * em.scale, em.hasColor ? em.color : null, _swapTo));
      }
    }
  },
  burst: tackleDust,
};

const glueTrail = {
  kind: 'emitter', duration: 1.5, ballTrail: true,
  start() {},
  tick(fx, em, dt) {
    const c = emCol(em, COL.glue);
    const n = emitCount(fx, em, 4, 6, dt);
    for (let i = 0; i < n; i++) {
      along(em, (i + rnd()) / n);
      P.reset().at(_p.x, _p.y - 0.05, _p.z).vel(em.vel.x * 0.1, -0.2, em.vel.z * 0.1).sizes(rr(0.03, 0.06) * em.scale, 0.05 * em.scale);
      P.life = rr(0.8, 1.4); P.grav = 1; P.drag = 0.3; P.bounce = FLOOR.STICK; P.frame = FRAME.BLOB; P.rot = rnd() * TAU;
      P.alpha = 0.95; P.fadeIn = 0; P.fadePow = 4; col(c);
      fx.A.spawn(P);
    }
  },
  burst(fx, pos, o) { glueBlobs(fx, pos, _up, cnt(fx, 6 * o.scale, 2), 0.6 * o.scale, pick(o, COL.glue)); },
};

// ------------------------------------------------------------------------------------------------ registry
/** Effect id -> definition. */
export const EFFECTS = {
  hit: { kind: 'burst', play: hit, dedupe: 0.35 },
  catch: { kind: 'burst', play: catchFx, dedupe: 0.4 },
  perfectCatch: { kind: 'burst', play: perfectCatch, dedupe: 1.0 },
  floorDust: { kind: 'burst', play: floorDust, dedupe: 0.3 },
  slideDust: { kind: 'burst', play: slideDust, dedupe: 0.2 },
  shockwave: { kind: 'burst', play: shockwave, dedupe: 0.8 },
  iceBurst: { kind: 'burst', play: iceBurst },
  glueSplat: { kind: 'burst', play: glueSplat },
  teleport: { kind: 'burst', play: teleport },
  swapFlash: { kind: 'burst', play: swapFlash },
  vanishSmoke: { kind: 'burst', play: vanishSmoke },
  cloneSpawn: { kind: 'burst', play: cloneSpawn },
  cloneDissolve: { kind: 'burst', play: cloneDissolve },
  shieldImpact: { kind: 'burst', play: shieldImpact },
  earthquake: { kind: 'burst', play: earthquake, dedupe: 1.0 },
  turretMuzzle: { kind: 'burst', play: turretMuzzle },
  reviveBeam: { kind: 'burst', play: reviveBeam, dedupe: 2.5 },
  elimination: { kind: 'burst', play: elimination, dedupe: 1.5 },
  cloak: { kind: 'burst', play: cloak },
  tackleDust: tackleTrail,
  fireTrail, beamTrail, iceTrail, frozenMist, magnetField, stasisBubble, rewindTrail, temporalZone, ultReady, glueTrail,
};

/** Friendly aliases (other modules may use natural names). */
export const EFFECT_ALIASES = {
  impact: 'hit', ballHit: 'hit', hitPuff: 'hit', catchPuff: 'catch', perfect: 'perfectCatch', dust: 'floorDust', bounce: 'floorDust',
  land: 'floorDust', landDust: 'floorDust', wallDust: 'floorDust', slide: 'slideDust', explosion: 'shockwave', meteor: 'shockwave',
  meteorImpact: 'shockwave', fire: 'fireTrail', flame: 'fireTrail', beam: 'beamTrail', hyperbeam: 'beamTrail', ice: 'iceBurst',
  freeze: 'iceBurst', frost: 'iceBurst', iceCrack: 'iceBurst', mist: 'frozenMist', frostTrail: 'iceTrail', glue: 'glueSplat',
  gluePuddle: 'glueSplat', smoke: 'vanishSmoke', poof: 'vanishSmoke', vanish: 'vanishSmoke', swap: 'swapFlash', blink: 'teleport',
  clone: 'cloneSpawn', decoy: 'cloneDissolve', clonePop: 'cloneDissolve', magnet: 'magnetField', shield: 'shieldImpact',
  shieldRipple: 'shieldImpact', aegis: 'shieldImpact', tackle: 'tackleDust', charge: 'tackleDust', quake: 'earthquake',
  slam: 'earthquake', muzzle: 'turretMuzzle', turret: 'turretMuzzle', turretFire: 'turretMuzzle', stasis: 'stasisBubble',
  rewind: 'rewindTrail', timeReversal: 'rewindTrail', zone: 'temporalZone', temporalReset: 'temporalZone', revive: 'reviveBeam',
  eliminated: 'elimination', out: 'elimination', ult: 'ultReady', ultimateReady: 'ultReady', decloak: 'cloak', camo: 'cloak',
};

/** Keyword fallback for unknown ids (first match wins). */
export const EFFECT_KEYWORDS = [
  ['perfect', 'perfectCatch'], ['catch', 'catch'], ['meteor', 'shockwave'], ['shock', 'shockwave'], ['explo', 'shockwave'],
  ['fire', 'fireTrail'], ['flame', 'fireTrail'], ['beam', 'beamTrail'], ['laser', 'beamTrail'], ['mist', 'frozenMist'],
  ['frost', 'iceBurst'], ['ice', 'iceBurst'], ['freez', 'iceBurst'], ['glue', 'glueSplat'], ['swap', 'swapFlash'],
  ['teleport', 'teleport'], ['vanish', 'vanishSmoke'], ['smoke', 'vanishSmoke'], ['clone', 'cloneSpawn'], ['magnet', 'magnetField'],
  ['shield', 'shieldImpact'], ['aegis', 'shieldImpact'], ['tackle', 'tackleDust'], ['quake', 'earthquake'], ['slam', 'earthquake'],
  ['turret', 'turretMuzzle'], ['muzzle', 'turretMuzzle'], ['stasis', 'stasisBubble'], ['rewind', 'rewindTrail'],
  ['time', 'rewindTrail'], ['zone', 'temporalZone'], ['revive', 'reviveBeam'], ['ult', 'ultReady'], ['elimin', 'elimination'],
  ['cloak', 'cloak'], ['dust', 'floorDust'], ['slide', 'slideDust'], ['hit', 'hit'], ['impact', 'hit'],
];
