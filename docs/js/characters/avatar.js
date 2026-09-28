// ---------------------------------------------------------------------------------------------------------------
// Avatar - realistic human character (Microsoft Rocketbox rigged + textured + motion-captured humans).
//
// Per frame (Avatar.update, called by Player.update with SCALED dt; juice effects use realDt):
//   1. restore the pure animation pose of the last frame (the procedural layer never accumulates)
//   2. clip layer: AnimationMixer with explicitly driven clip times/weights:
//        idle/lookaround <-> walk <-> run <-> sprint blended by ground speed, gait phase shared by all gait clips and
//        advanced at a rate matched to the clips' measured stride speed (feet stay planted), crouch for slides and the
//        catch stance, drunk idle while stunned, run pose slowed while airborne, one-shots (cheer/wave/defeat)
//   3. procedural layer on bone quaternions (after mixer.update):
//        lean pivot (roll/pitch from the measured planar acceleration: tan(theta) = a/g, incl. centripetal a = v*yawRate)
//        strafe/back-pedal leg yaw with spine counter-rotation, throw wind-up twist + whip-through arc, catch absorb,
//        additive hit flinch (damped spring), head/neck/eyes LookAt with clamps (incoming ball > aim > target),
//        analytic two-bone arm IK (catch reach to the predicted intercept, wind-up behind the shoulder, ball carry),
//        palm orientation + finger curl
//   4. material juice (flash, tint, frozen, cloak), team ring, afterimages, clones
//   5. ragdoll (ragdoll.js) replaces 2-3 while active; recoverFromRagdoll blends back over 0.35 s
// ---------------------------------------------------------------------------------------------------------------
import * as THREE from 'three';
import { clone as cloneSkinned } from 'three/addons/utils/SkeletonUtils.js';
import { game } from '../game.js';
import { TEAM_COLORS, BALL_RADIUS, GRAVITY, HIT_FLASH_DURATION } from '../core/constants.js';
import { Ragdoll } from './ragdoll.js';
import {
  clamp, damp, dampFactor, smoothstep, frac, locomotionBlend, gaitRate, leanFromAcceleration, springStep,
  twoBoneInteriorAngle, strafeLegYaw, oneShotEnvelope, normalizeWeights,
} from './avatarMath.js';

// =================================================================================================== tuning
/** All avatar tuning (angles in radians, distances in metres at model scale 1, rates in 1/s). */
export const AVATAR = Object.freeze({
  heightScaleRange: [0.9, 1.12],     // model is scaled to hero.height within this range
  crossfade: 0.13,                   // clip weight time constant (s)
  idleSpeed: 0.18,                   // m/s below which the character stands
  gaitRate: [0.55, 1.55],            // playback-rate clamp for stride matching
  airborneRate: 0.18,                // gait phase speed while airborne (run pose nearly frozen)
  idleVariantAfter: 9,               // s standing still before the look-around idle plays
  crouch: { slide: 1, catch: 0.42, charge: 0.16, absorb: 0.3 },
  oneShot: { fadeIn: 0.2, fadeOut: 0.35, cheerMax: 4.5, waveMax: 3.2, defeatMax: 2.4 },
  strafe: { minSpeed: 0.6, maxLegYaw: 1.0, backMaxLegYaw: 0.8, backStart: 1.85, backEnd: 1.6, counter: 0.9, rate: 9 },
  lean: {
    accelRate: 7, rate: 6, pitchGain: 0.5, rollGain: 0.65, maxPitch: 0.2, maxRoll: 0.3, slideBack: 0.26, airborneMul: 0.3,
    teleportDistance: 1.5,
  },
  look: {
    maxYaw: 1.3, maxUp: 0.65, maxDown: 0.8, neckShare: 0.35, rate: 9, ballRate: 20, weightRate: 7,
    ballWeight: 1, aimWeight: 0.95, targetWeight: 0.75, idleWeight: 0.5, idleRange: 16, rescan: 0.3, eyeClamp: 0.38,
    threatHorizon: 1.4,
  },
  ik: {
    reach: 0.985, weightRate: 14, targetRate: 16, catchSpread: 0.095, catchReach: 0.72, readyForward: 0.4,
    readyDown: 0.08, readySide: 0.13,
  },
  throw: {
    whipTime: 0.45, windupTwist: -0.62, followTwist: 0.42, windupBack: -0.1, followFlex: 0.3,
    leftArmWeight: 0.55,
  },
  catch: { absorbTime: 0.32, absorbFlex: -0.14 },
  flinch: { omega: 15, zeta: 0.42, impulse: 7.5, max: 0.55, headShare: 0.6 },
  hold: { curl: 0.5, thumb: 0.3, catchCurl: 0.22, weightIdle: 0.75, weightRun: 0.35 },
  flash: { intensity: 2.4 },
  tint: { emissive: 0.12 },
  frozen: { color: 0x9fd6ff, tint: 0.55, glow: 0x0a2a44, rim: 0.9, rimColor: 0xbfe8ff, roughness: 0.3, fade: 5, inflate: 0.011, frostOpacity: 0.62 },
  cloak: { enemyOpacity: 0.06, allyOpacity: 0.35, fadeRate: 7, enemyRim: 0.7, enemyRimAlpha: 0.32, allyRim: 0.25, rimColor: 0xcfe6ff },
  ring: { size: 1.05, opacity: 0.5, localOpacity: 0.8, y: 0.012 },
  recoverTime: 0.35,
  ghost: { opacity: 0.5, rim: 1.4, rimAlpha: 0.45, tint: 0.55 },
  glassOpacity: 0.5,                 // visors/face shields: texture alpha is dirty/smoked plastic; keep faces readable
  clone: { mirrorSpeedTolerance: 1.5 },
});

/** Humanoid names -> Rocketbox Biped bone names (three.js sanitises spaces to underscores). */
export const BONE_MAP = Object.freeze({
  root: 'Bip01', hips: 'Bip01_Pelvis', spine: 'Bip01_Spine', spine1: 'Bip01_Spine1', chest: 'Bip01_Spine2',
  neck: 'Bip01_Neck', head: 'Bip01_Head', lEye: 'Bip01_LEye', rEye: 'Bip01_REye',
  lClavicle: 'Bip01_L_Clavicle', lUpperArm: 'Bip01_L_UpperArm', lForearm: 'Bip01_L_Forearm', lHand: 'Bip01_L_Hand',
  rClavicle: 'Bip01_R_Clavicle', rUpperArm: 'Bip01_R_UpperArm', rForearm: 'Bip01_R_Forearm', rHand: 'Bip01_R_Hand',
  lThigh: 'Bip01_L_Thigh', lCalf: 'Bip01_L_Calf', lFoot: 'Bip01_L_Foot', lToe: 'Bip01_L_Toe0',
  rThigh: 'Bip01_R_Thigh', rCalf: 'Bip01_R_Calf', rFoot: 'Bip01_R_Foot', rToe: 'Bip01_R_Toe0',
});

const CLIP_KEYS = ['idle', 'breathe', 'lookaround', 'walk', 'run', 'sprint', 'crouch', 'stunned', 'cheer', 'wave', 'defeat'];
const GAIT_KEYS = ['walk', 'run', 'sprint'];
const LOOPING = new Set(['idle', 'breathe', 'lookaround', 'walk', 'run', 'sprint', 'crouch', 'stunned']);
/** Keys driven by the clone's own locomotion instead of being mirrored from the source. */
const CLONE_OWN_KEYS = new Set(['idle', 'breathe', 'lookaround', 'walk', 'run', 'sprint']);
const FINGER_CHAINS = [['Finger0', 'Finger01', 'Finger02'], ['Finger1', 'Finger11', 'Finger12'], ['Finger2', 'Finger21', 'Finger22'],
  ['Finger3', 'Finger31', 'Finger32'], ['Finger4', 'Finger41', 'Finger42']];
const FINGER_JOINT_SCALE = [1, 1.15, 0.9];
const SIDES = Object.freeze(['r', 'l']);

// =================================================================================================== temporaries
const _v1 = new THREE.Vector3(), _v2 = new THREE.Vector3(), _v3 = new THREE.Vector3(), _v4 = new THREE.Vector3();
const _S = new THREE.Vector3(), _E = new THREE.Vector3(), _W = new THREE.Vector3(), _T = new THREE.Vector3();
const _axis = new THREE.Vector3(), _pole = new THREE.Vector3(), _n = new THREE.Vector3();
const _fwd = new THREE.Vector3(), _left = new THREE.Vector3(), _right = new THREE.Vector3();
const _UP = new THREE.Vector3(0, 1, 0);
const _tp = new THREE.Vector3(), _ts = new THREE.Vector3();
const _q1 = new THREE.Quaternion(), _q2 = new THREE.Quaternion(), _q3 = new THREE.Quaternion(), _qs = new THREE.Quaternion();
const _tq = new THREE.Quaternion(), _tq2 = new THREE.Quaternion();
const _qId = new THREE.Quaternion();
const _col = new THREE.Color();
const _WHITE = new THREE.Color(1, 1, 1);
const _BLACK = new THREE.Color(0, 0, 0);
const _ICE = new THREE.Color(AVATAR.frozen.color);
const _ICE_GLOW = new THREE.Color(AVATAR.frozen.glow);
const _ICE_RIM = new THREE.Color(AVATAR.frozen.rimColor);
const _CLOAK_RIM = new THREE.Color(AVATAR.cloak.rimColor);
const _ZERO = new THREE.Vector3();
const _threatPoint = new THREE.Vector3();

// Optional combat dependency (Trajectory.predictImpact) loaded lazily so the avatar works without the combat module.
let Trajectory = null;
let _trajRequested = false;
function requestTrajectory() {
  if (_trajRequested || !game.balls || (game.config && game.config.noCombatImport)) return;
  _trajRequested = true;
  import('../combat/throwSolver.js').then((m) => { Trajectory = m.Trajectory || null; }).catch(() => { Trajectory = null; });
}

// =================================================================================================== bone helpers
/** Parent world rotation (parent.matrixWorld must be fresh). */
function parentWorldQuat(bone, out) {
  const p = bone.parent;
  if (!p) return out.identity();
  p.matrixWorld.decompose(_tp, out, _ts);
  return out;
}
/** World rotation of an object (matrixWorld must be fresh). */
function worldQuat(obj, out) { obj.matrixWorld.decompose(_tp, out, _ts); return out; }
/** World position of an object (matrixWorld must be fresh). */
function worldPos(obj, out) { return out.setFromMatrixPosition(obj.matrixWorld); }
/** Rotate a bone about a WORLD axis by `angle` (local quaternion is premultiplied by the parent-space rotation). */
function rotateBoneWorld(bone, axisWorld, angle) {
  if (Math.abs(angle) < 1e-6) return;
  parentWorldQuat(bone, _tq).invert();
  _v4.copy(axisWorld).applyQuaternion(_tq).normalize();
  bone.quaternion.premultiply(_tq2.setFromAxisAngle(_v4, angle));
}
/** Apply a WORLD rotation to a bone. */
function rotateBoneWorldQ(bone, qWorld) {
  parentWorldQuat(bone, _tq);
  _tq2.copy(_tq).invert().multiply(qWorld).multiply(_tq);
  bone.quaternion.premultiply(_tq2);
}
/** target += color * s (THREE.Color has no addScaledColor). */
function addScaledColor(target, color, s) {
  if (s === 0) return target;
  target.r += color.r * s; target.g += color.g * s; target.b += color.b * s;
  return target;
}
/** Self-only world matrix refresh (parent assumed fresh). */
function refresh(obj) { obj.updateWorldMatrix(false, false); }

/** Pose nodes (Bip01 + every bone) in deterministic traversal order - identical for clones. */
function collectPoseNodes(model) {
  const nodes = [];
  model.traverse((o) => { if (o.isBone || o.name === BONE_MAP.root) nodes.push(o); });
  return nodes;
}
function capturePose(nodes, buf) {
  for (let i = 0, j = 0; i < nodes.length; i++, j += 7) {
    const n = nodes[i];
    buf[j] = n.quaternion.x; buf[j + 1] = n.quaternion.y; buf[j + 2] = n.quaternion.z; buf[j + 3] = n.quaternion.w;
    buf[j + 4] = n.position.x; buf[j + 5] = n.position.y; buf[j + 6] = n.position.z;
  }
}
function applyPose(nodes, buf) {
  for (let i = 0, j = 0; i < nodes.length; i++, j += 7) {
    const n = nodes[i];
    n.quaternion.set(buf[j], buf[j + 1], buf[j + 2], buf[j + 3]);
    n.position.set(buf[j + 4], buf[j + 5], buf[j + 6]);
  }
}

// =================================================================================================== materials
/** Per-material hook uniforms (rim light / cloak shimmer / frost inflation). WeakMap so Material.clone() never copies it. */
const HOOKS = new WeakMap();
const RIM_GLSL = `
{
  vec3 duV = normalize( vViewPosition );
  float duF = 1.0 - clamp( abs( dot( duV, normal ) ), 0.0, 1.0 );
  float duRimK = pow( duF, duRimPower );
  // screen-space heat-haze interference: reads as a refraction shimmer on the cloaked silhouette
  float duHaze = 0.5 + 0.5 * sin( duTime * 9.0 + gl_FragCoord.y * 0.33 + 2.0 * sin( gl_FragCoord.x * 0.07 + duTime * 3.1 ) );
  float duSh = mix( 1.0, 0.25 + 0.75 * duHaze, duShimmer );
  outgoingLight += duRimColor * ( duRimK * duRim * duSh );
  diffuseColor.a = clamp( diffuseColor.a + duRimK * duRimAlpha * duSh, 0.0, 1.0 );
}
`;
function duOnBeforeCompile(shader) {
  const u = HOOKS.get(this);
  if (!u) return;
  Object.assign(shader.uniforms, u);
  shader.vertexShader = 'uniform float duInflate;\n' + shader.vertexShader.replace(
    '#include <skinning_vertex>', '#include <skinning_vertex>\n\ttransformed += normalize( objectNormal ) * duInflate;');
  shader.fragmentShader = 'uniform float duRim;\nuniform vec3 duRimColor;\nuniform float duRimPower;\nuniform float duRimAlpha;\nuniform float duShimmer;\nuniform float duTime;\n'
    + shader.fragmentShader.replace('#include <opaque_fragment>', RIM_GLSL + '\n#include <opaque_fragment>');
}
function duCacheKey() { return 'du-avatar-hook-v1'; }
/** Adds the avatar shader hook to a MeshPhysicalMaterial and returns its uniforms. */
function hookMaterial(mat) {
  const u = {
    duRim: { value: 0 }, duRimColor: { value: new THREE.Color(1, 1, 1) }, duRimPower: { value: 2.5 }, duRimAlpha: { value: 0 },
    duShimmer: { value: 0 }, duTime: { value: 0 }, duInflate: { value: 0 },
  };
  HOOKS.set(mat, u);
  mat.onBeforeCompile = duOnBeforeCompile;
  mat.customProgramCacheKey = duCacheKey;
  return u;
}
function cloneHooked(src) {
  const m = src.clone();
  const u = hookMaterial(m);
  const su = HOOKS.get(src);
  if (su) for (const k of Object.keys(u)) { if (su[k].value && su[k].value.isColor) u[k].value.copy(su[k].value); else u[k].value = su[k].value; }
  return m;
}

/** Waits (polling) until a texture's image has decoded. Resolves false on timeout. */
function waitForImage(tex, timeoutMs = 6000) {
  return new Promise((resolve) => {
    const t0 = performance.now();
    const check = () => {
      const img = tex && tex.image;
      if (img && (img.width > 0 || img.naturalWidth > 0)) return resolve(true);
      if (performance.now() - t0 > timeoutMs) return resolve(false);
      setTimeout(check, 30);
    };
    check();
  });
}

/**
 * Classifies an alpha-textured 'hair' material: hair cards (mostly 0/1 alpha, cut-out + soft edge pass) versus
 * semi-transparent glass (visors, safety glasses: nearly full coverage with mid-range alpha -> blended, no depth write).
 */
const _alphaCache = new Map();
function analyzeAlpha(tex, key) {
  if (_alphaCache.has(key)) return _alphaCache.get(key);
  let result = { mode: 'cards', coverage: 0.5, meanCovered: 1 };
  try {
    const img = tex.image;
    const N = 48;
    const canvas = document.createElement('canvas');
    canvas.width = N; canvas.height = N;
    const ctx = canvas.getContext('2d', { willReadFrequently: true });
    ctx.drawImage(img, 0, 0, N, N);
    const data = ctx.getImageData(0, 0, N, N).data;
    let cov = 0, sum = 0;
    for (let i = 3; i < data.length; i += 4) { const a = data[i] / 255; if (a > 0.05) { cov++; sum += a; } }
    const coverage = cov / (N * N);
    const meanCovered = cov ? sum / cov : 0;
    result = { mode: coverage > 0.9 && meanCovered < 0.93 ? 'glass' : 'cards', coverage, meanCovered };
  } catch (e) { /* cross-origin or no DOM: keep cards */ }
  _alphaCache.set(key, result);
  return result;
}

// Shared procedural textures ------------------------------------------------------------------------------------
let _ringTex = null;
function ringTexture() {
  if (_ringTex) return _ringTex;
  const N = 256, c = document.createElement('canvas');
  c.width = N; c.height = N;
  const g = c.getContext('2d');
  const img = g.createImageData(N, N);
  for (let y = 0; y < N; y++) for (let x = 0; x < N; x++) {
    const r = Math.hypot(x + 0.5 - N / 2, y + 0.5 - N / 2) / (N / 2);
    // crisp outer edge, soft inner falloff (painted-on decal look)
    const outer = 1 - smoothstep(0.9, 0.96, r);
    const inner = smoothstep(0.62, 0.86, r);
    const glow = Math.exp(-((r - 0.88) ** 2) / 0.0012) * 0.35;
    const a = Math.min(1, outer * inner * 0.85 + glow * outer);
    const i = (y * N + x) * 4;
    img.data[i] = 255; img.data[i + 1] = 255; img.data[i + 2] = 255; img.data[i + 3] = Math.round(a * 255);
  }
  g.putImageData(img, 0, 0);
  _ringTex = new THREE.CanvasTexture(c);
  _ringTex.colorSpace = THREE.SRGBColorSpace;
  return _ringTex;
}
const _ringGeo = new THREE.PlaneGeometry(1, 1).rotateX(-Math.PI / 2);

let _frostTex = null;
function frostTexture() {
  if (_frostTex) return _frostTex;
  const N = 256, c = document.createElement('canvas');
  c.width = N; c.height = N;
  const g = c.getContext('2d');
  g.fillStyle = 'rgba(210,235,255,0.55)';
  g.fillRect(0, 0, N, N);
  // crystalline streaks (deterministic pseudo random so every run looks the same)
  let seed = 1337;
  const rnd = () => { seed = (seed * 16807) % 2147483647; return seed / 2147483647; };
  for (let i = 0; i < 420; i++) {
    const x = rnd() * N, y = rnd() * N, len = 4 + rnd() * 22, ang = rnd() * Math.PI;
    g.strokeStyle = `rgba(255,255,255,${0.25 + rnd() * 0.6})`;
    g.lineWidth = 0.5 + rnd() * 1.4;
    g.beginPath(); g.moveTo(x, y); g.lineTo(x + Math.cos(ang) * len, y + Math.sin(ang) * len); g.stroke();
    if (rnd() < 0.35) { g.beginPath(); g.moveTo(x, y); g.lineTo(x + Math.cos(ang + 1.05) * len * 0.5, y + Math.sin(ang + 1.05) * len * 0.5); g.stroke(); }
  }
  _frostTex = new THREE.CanvasTexture(c);
  _frostTex.colorSpace = THREE.SRGBColorSpace;
  _frostTex.wrapS = _frostTex.wrapT = THREE.RepeatWrapping;
  _frostTex.repeat.set(3, 3);
  return _frostTex;
}

// =================================================================================================== clip sets
/** gender -> Promise<{ clips, durations, strides }> (clips processed once and shared by every avatar). */
const _clipSets = new Map();

/**
 * Loads + processes all clips of a gender: measures stride speeds from the raw root motion, removes the horizontal
 * root travel (the game moves the root; the in-place sway around the linear trend is kept so the feet stay planted,
 * Y is kept) and finds each gait clip's left-heel-strike phase so walk/run/sprint blend in phase.
 */
function loadClipSet(gender, probeModel) {
  if (_clipSets.has(gender)) return _clipSets.get(gender);
  const p = (async () => {
    const clips = {}, durations = {}, strides = {};
    await Promise.all(CLIP_KEYS.map(async (key) => {
      const clip = await game.assets.clip(gender, key).catch(() => null);
      if (clip) clips[key] = clip;
    }));
    for (const key of Object.keys(clips)) {
      const clip = clips[key];
      durations[key] = Math.max(1e-3, clip.duration);
      const track = clip.tracks.find((t) => t.name === `${BONE_MAP.root}.position`);
      if (track) {
        const v = track.values, n = v.length / 3, times = track.times;
        const x0 = v[0], z0 = v[2], x1 = v[v.length - 3], z1 = v[v.length - 1];
        if (GAIT_KEYS.includes(key)) strides[key] = { speed: Math.hypot(x1 - x0, z1 - z0) / durations[key], duration: durations[key], offset: 0 };
        const T = times[n - 1] - times[0] || 1;
        for (let i = 0; i < n; i++) {
          const f = (times[i] - times[0]) / T;
          v[i * 3] = clamp(v[i * 3] - (x0 + (x1 - x0) * f), -0.15, 0.15);
          v[i * 3 + 2] = clamp(v[i * 3 + 2] - (z0 + (z1 - z0) * f), -0.15, 0.15);
        }
      }
    }
    // Fallback stride data (typical adult values) if a gait clip is missing.
    const FALLBACK = { walk: { speed: 1.1, duration: 1.1 }, run: { speed: 2.9, duration: 0.72 }, sprint: { speed: 5.6, duration: 0.62 } };
    for (const k of GAIT_KEYS) if (!strides[k] || !(strides[k].speed > 0.2)) strides[k] = { ...FALLBACK[k], offset: 0 };
    measureGaitPhases(probeModel, clips, strides);
    return { clips, durations, strides };
  })();
  _clipSets.set(gender, p);
  return p;
}

/** Phase (0..1) of the left heel strike (left foot furthest forward) for each gait clip, evaluated on a real model. */
function measureGaitPhases(model, clips, strides) {
  const nodes = collectPoseNodes(model);
  const saved = new Float32Array(nodes.length * 7);
  capturePose(nodes, saved);
  const lFoot = model.getObjectByName(BONE_MAP.lFoot), rFoot = model.getObjectByName(BONE_MAP.rFoot);
  if (!lFoot || !rFoot) return;
  const mixer = new THREE.AnimationMixer(model);
  const modelInv = new THREE.Matrix4();
  for (const key of GAIT_KEYS) {
    const clip = clips[key];
    if (!clip) continue;
    const action = mixer.clipAction(clip);
    action.play(); action.timeScale = 0; action.setEffectiveWeight(1);
    let best = -Infinity, bestT = 0;
    const N = 48;
    for (let i = 0; i < N; i++) {
      const t = (i / N) * clip.duration;
      action.time = t;
      mixer.update(0);
      model.updateMatrixWorld(true);
      modelInv.copy(model.matrixWorld).invert();
      const dl = _v1.setFromMatrixPosition(lFoot.matrixWorld).applyMatrix4(modelInv).z;
      const dr = _v2.setFromMatrixPosition(rFoot.matrixWorld).applyMatrix4(modelInv).z;
      if (dl - dr > best) { best = dl - dr; bestT = i / N; }
    }
    strides[key].offset = bestT;
    action.stop();
    mixer.uncacheAction(clip, model);
  }
  mixer.uncacheRoot(model);
  applyPose(nodes, saved);
  model.updateMatrixWorld(true);
}

// =================================================================================================== Avatar
/**
 * Realistic avatar of a Player: the Rocketbox model, animation, procedural animation, visual juice and ragdoll.
 * Contract: WEB_ARCHITECTURE.md §3.3.
 */
export class Avatar {
  static defaults = AVATAR;

  /** Convenience: construct + load. */
  static async load(player) { const a = new Avatar(player); await a.load(); return a; }

  /** @param {import('../gameplay/player.js').Player} player */
  constructor(player) {
    this.player = player;
    /** Added under player.root. Carries the yaw (player.yaw) and the team ring. */
    this.root = new THREE.Group();
    this.root.name = `Avatar:${player && player.id}`;
    /** Lean pivot at the feet (roll/pitch into accelerations); parent of the model. */
    this.leanPivot = new THREE.Group();
    this.leanPivot.name = 'LeanPivot';
    this.root.add(this.leanPivot);
    /** @type {THREE.Object3D|null} */
    this.model = null;
    /** @type {THREE.AnimationMixer|null} */
    this.mixer = null;
    /** Humanoid bone map (see BONE_MAP). */
    this.bones = {};
    /** Ball sockets in the palms (children of the hand bones after load; ball centre = socket). */
    this.rightHandSocket = new THREE.Object3D();
    this.rightHandSocket.name = 'RightHandSocket';
    this.leftHandSocket = new THREE.Object3D();
    this.leftHandSocket.name = 'LeftHandSocket';
    this.rightHandSocket.position.set(-0.25, 1.0, 0.2);
    this.leftHandSocket.position.set(0.25, 1.0, 0.2);
    this.root.add(this.rightHandSocket, this.leftHandSocket);

    this.loaded = false;
    this.gender = 'm';
    this.heroId = player && player.hero ? player.hero.id : null;
    this.worldScale = 1;
    /** @type {Ragdoll|null} */
    this.ragdoll = null;

    // clip layer
    this._clipSet = null;
    this._actions = {};
    this._keys = [];
    this._w = {};          // current (smoothed, normalised) weights
    this._wTarget = {};    // targets
    this._times = {};      // explicit clip times
    this._phase = 0;       // shared gait phase (0..1, 0 = left heel strike)
    this._phaseDir = 1;    // -1 = back-pedal
    this._backwards = false;
    this._blend = {};
    this._strides = null;
    this._oneShot = null;  // { key, t, dur }
    this._idleKey = 'idle';
    this._stillTime = 0;
    this._variantTime = 0;

    // pose buffers
    this._nodes = [];
    this._animPose = null;
    this._bindPose = null;
    this._recoverPose = null;
    this._recover = null;

    // procedural state
    this._yaw = 0;
    this._legYaw = 0;
    this._accel = new THREE.Vector3();
    this._prevVel = new THREE.Vector3();
    this._prevFeet = new THREE.Vector3();
    this._hasPrev = false;
    this._leanPitch = 0;
    this._leanRoll = 0;
    this._lookDir = new THREE.Vector3(0, 0, 1);
    this._lookInit = false;
    this._lookW = 0;
    this._lookTarget = new THREE.Vector3();
    this._lookRescan = 0;
    this._lookEnemy = null;
    this._incoming = [];
    this._threatBall = null;
    this._threatT = Infinity;
    this._threatPoint = new THREE.Vector3();
    this._throwT = -1;
    this._throwDir = new THREE.Vector3(0, 0, 1);
    this._windupW = 0;
    this._absorbT = -1;
    this._absorbFrom = new THREE.Vector3(); // root-local
    this._flinchP = { x: 0, v: 0 };
    this._flinchR = { x: 0, v: 0 };
    this._ik = {
      r: { w: 0, pos: new THREE.Vector3(), pole: new THREE.Vector3(0, -1, 0), init: false },
      l: { w: 0, pos: new THREE.Vector3(), pole: new THREE.Vector3(0, -1, 0), init: false },
    };
    this._curlR = 0; this._curlL = 0;
    this._palmW = 0;
    this._upperActivity = 0;
    this._bind = null;     // bind-pose measurements (face/eye forward, palm normals, finger axes)
    this._inp = {
      speed: 0, grounded: true, state: 'grounded', sliding: false, stunned: false, incap: false, eliminated: false,
      charging: false, charge: 0, hasBall: false, catching: false, airborne: false, teleported: false,
    };

    // materials / juice
    this._mats = [];       // { mat, u, kind, role, baseOpacity, baseTransparent, baseRoughness, baseClearcoat }
    this._meshes = [];     // primary skinned meshes (shadow toggles)
    this._frostMeshes = [];
    this._frostMat = null;
    this._flashT = 0; this._flashDur = HIT_FLASH_DURATION; this._flashColor = new THREE.Color(1, 1, 1);
    this._tintColor = new THREE.Color(1, 1, 1); this._tintAmt = 0;
    this.frozen = false; this._frozenAmt = 0;
    this.cloaked = false; this._cloakAmt = 0; this._cloakEnemy = false;
    this._fxNeutral = true;
    this._shadowsOn = true;

    this._ring = null;
    this._afterimages = [];
    this._clones = new Set();
    this._disposed = false;
  }

  // ------------------------------------------------------------------------------------------------ loading
  /** Loads the hero model (cached GLB, cloned per player), materials, clips, bind data and ragdoll. */
  async load() {
    if (this.loaded) return this;
    const manifest = game.assets && game.assets.manifest;
    if (!manifest) throw new Error('Avatar.load: assets manifest not loaded');
    const hero = this.player && this.player.hero ? this.player.hero : null;
    let heroId = hero ? hero.id : 'Rayne';
    let def = manifest.heroes[heroId];
    if (!def) {
      const fallback = Object.keys(manifest.heroes)[0];
      console.warn(`[avatar] no realistic model for ${heroId}; using ${fallback}`);
      heroId = fallback; def = manifest.heroes[fallback];
    }
    this.heroId = heroId;
    this.gender = def.gender === 'female' ? 'f' : 'm';
    const gltf = await game.assets.gltf(def.folder + def.model);
    const model = cloneSkinned(gltf.scene);
    model.name = `Model:${heroId}`;
    this.model = model;
    this._nodes = collectPoseNodes(model);
    for (const [key, name] of Object.entries(BONE_MAP)) {
      const b = model.getObjectByName(name);
      if (b) this.bones[key] = b;
    }
    if (!this.bones.hips || !this.bones.rHand || !this.bones.lHand) throw new Error(`Avatar.load: ${heroId} is not a Biped rig`);

    await this._buildMaterials(def, hero);

    // Bind pose (model at identity) measurements.
    model.position.set(0, 0, 0); model.quaternion.identity(); model.scale.set(1, 1, 1);
    model.updateMatrixWorld(true);
    const scale = this._computeScale(hero);
    this._bindPose = new Float32Array(this._nodes.length * 7);
    capturePose(this._nodes, this._bindPose);
    this._measureBind(scale);
    this.ragdoll = new Ragdoll(this);

    // Clips (shared, processed once per gender).
    this._clipSet = await loadClipSet(this.gender, model);
    applyPose(this._nodes, this._bindPose);
    this._strides = {};
    for (const k of GAIT_KEYS) {
      const s = this._clipSet.strides[k];
      this._strides[k] = { speed: s.speed * scale, duration: s.duration, offset: s.offset };
    }

    model.scale.setScalar(scale);
    this.worldScale = scale;
    this.leanPivot.add(model);

    // Mixer with every clip playing at weight 0; times are driven explicitly (timeScale 0).
    this.mixer = new THREE.AnimationMixer(model);
    for (const key of CLIP_KEYS) {
      const clip = this._clipSet.clips[key];
      if (!clip) continue;
      const action = this.mixer.clipAction(clip);
      action.setLoop(THREE.LoopRepeat, Infinity);
      action.timeScale = 0;
      action.play();
      action.setEffectiveWeight(0);
      this._actions[key] = action;
      this._keys.push(key);
      this._w[key] = 0; this._wTarget[key] = 0; this._times[key] = 0;
    }
    if (this._actions.idle) this._w.idle = 1;
    this._phase = this.player && typeof this.player.id === 'number' ? frac(this.player.id * 0.37) : Math.random();

    // Sockets in the palms.
    this.bones.rHand.add(this.rightHandSocket);
    this.bones.lHand.add(this.leftHandSocket);
    this.rightHandSocket.position.copy(this._bind.palm.r.socket);
    this.leftHandSocket.position.copy(this._bind.palm.l.socket);

    this._buildRing();
    this._animPose = new Float32Array(this._nodes.length * 7);
    this._recoverPose = new Float32Array(this._nodes.length * 7);
    capturePose(this._nodes, this._animPose);

    if (this.player && this.player.root && this.root.parent !== this.player.root) this.player.root.add(this.root);
    this.loaded = true;
    // First evaluation so the very first rendered frame is animated (not the A-pose).
    this.update(0, 0);
    return this;
  }

  /** Scale that brings the model's visible height to hero.height (clamped to a plausible range). */
  _computeScale(hero) {
    const box = new THREE.Box3(), tmp = new THREE.Box3();
    for (const mesh of this._meshes) {
      if (!mesh.visible) continue;
      if (!mesh.geometry.boundingBox) mesh.geometry.computeBoundingBox();
      tmp.copy(mesh.geometry.boundingBox).applyMatrix4(mesh.matrixWorld);
      box.union(tmp);
    }
    const h = box.isEmpty() ? 1.8 : box.max.y - Math.min(0, box.min.y);
    const target = hero && hero.height > 0 ? hero.height : h;
    return clamp(target / h, AVATAR.heightScaleRange[0], AVATAR.heightScaleRange[1]);
  }

  /** MeshPhysicalMaterials from manifest.heroes[id].materials; hides hero.hiddenParts; shadows. */
  async _buildMaterials(def, hero) {
    const hidden = (hero && hero.hiddenParts) || [];
    const tex = (file, srgb) => (file ? game.assets.texture(def.folder + file, srgb) : null);
    const meshes = [];
    this.model.traverse((o) => { if (o.isMesh) meshes.push(o); });
    const built = new Map();
    for (const mesh of meshes) {
      mesh.castShadow = true;
      mesh.receiveShadow = true;
      mesh.frustumCulled = false; // skinned bounds do not follow ragdolls/animation
      const src = mesh.material;
      const name = src && src.name || '';
      if (hidden.some((part) => name === part || name.endsWith('_' + part) || name.includes(part))) {
        mesh.visible = false;
        continue;
      }
      const spec = (def.materials && def.materials[name]) || { kind: 'body' };
      let entry = built.get(name);
      if (!entry) {
        entry = await this._makeMaterial(name, spec, tex, def);
        built.set(name, entry);
      }
      mesh.material = entry.mat;
      if (entry.role === 'glass') { mesh.renderOrder = 2; mesh.castShadow = false; }
      mesh.userData.duCastShadow = mesh.castShadow;
      this._meshes.push(mesh);
      if (entry.softMat) {
        // Second, blended pass of hair cards for soft anti-aliased edges (shares geometry + skeleton).
        const soft = mesh.clone();
        soft.material = entry.softMat;
        soft.renderOrder = 1;
        soft.castShadow = false;
        soft.name = mesh.name + '_soft';
        mesh.parent.add(soft);
      }
    }
  }

  async _makeMaterial(name, spec, tex, def) {
    const kind = spec.kind || 'body';
    const mat = new THREE.MeshPhysicalMaterial({ name, color: 0xffffff, roughness: 1, metalness: 0 });
    if (spec.map) mat.map = tex(spec.map, true);
    if (spec.normalMap) { mat.normalMap = tex(spec.normalMap, false); mat.normalScale.set(1, -1); } // glTF normal maps without tangents
    if (spec.ormMap) mat.roughnessMap = tex(spec.ormMap, false); // G = roughness (R=AO 1, B=metal 0)
    let role = 'base', softMat = null;
    if (kind === 'skin') {
      mat.sheen = 0.25; mat.sheenRoughness = 0.55; mat.sheenColor.setRGB(1.0, 0.62, 0.5);
      mat.ior = 1.4; mat.specularIntensity = 0.75;
    } else if (kind === 'body') {
      mat.sheen = 0.18; mat.sheenRoughness = 0.8; mat.sheenColor.setRGB(0.75, 0.75, 0.78); // fabric
    } else if (kind === 'gear') {
      mat.clearcoat = 0.12; mat.clearcoatRoughness = 0.45;
    } else if (kind === 'hair') {
      mat.side = THREE.DoubleSide;
      mat.sheen = 0.3; mat.sheenRoughness = 0.45; mat.sheenColor.setRGB(0.55, 0.48, 0.4);
      const analysis = mat.map && (await waitForImage(mat.map)) ? analyzeAlpha(mat.map, def.folder + spec.map) : { mode: 'cards' };
      if (analysis.mode === 'glass') {
        // Visor / safety glasses: alpha-blended, glossy, never writes depth.
        role = 'glass';
        mat.transparent = true; mat.depthWrite = false; mat.alphaTest = 0; mat.opacity = AVATAR.glassOpacity;
        mat.sheen = 0; mat.roughness = 0.35; mat.clearcoat = 1; mat.clearcoatRoughness = 0.04; mat.specularIntensity = 1;
      } else {
        mat.alphaTest = spec.alphaTest || 0.35;
        softMat = new THREE.MeshPhysicalMaterial({
          name: name + '_soft', color: 0xffffff, roughness: 1, metalness: 0, map: mat.map, normalMap: mat.normalMap,
          roughnessMap: mat.roughnessMap, transparent: true, depthWrite: false, side: THREE.DoubleSide, alphaTest: 0.02,
          sheen: mat.sheen, sheenRoughness: mat.sheenRoughness, sheenColor: mat.sheenColor.clone(),
        });
        softMat.normalScale.copy(mat.normalScale);
      }
    }
    const entry = { mat, softMat, role };
    this._registerMat(mat, kind, role);
    if (softMat) this._registerMat(softMat, kind, 'soft');
    return entry;
  }

  _registerMat(mat, kind, role) {
    const u = hookMaterial(mat);
    this._mats.push({
      mat, u, kind, role, baseOpacity: mat.opacity, baseTransparent: mat.transparent, baseRoughness: mat.roughness,
      baseClearcoat: mat.clearcoat, baseColor: mat.color.clone(),
    });
  }

  /** Bind-pose measurements for IK / LookAt / hands (model space, model scale 1). */
  _measureBind(scale) {
    const B = this.bones;
    const bind = { face: new THREE.Vector3(0, 0, 1), eyes: {}, palm: {}, fingers: { l: [], r: [] } };
    const fwd = new THREE.Vector3(0, 0, 1);
    const q = new THREE.Quaternion();
    if (B.head) bind.face.copy(fwd).applyQuaternion(worldQuat(B.head, q).invert());
    for (const side of ['l', 'r']) {
      const eye = B[side + 'Eye'];
      if (eye) bind.eyes[side] = fwd.clone().applyQuaternion(worldQuat(eye, q).invert());
    }
    for (const side of ['l', 'r']) {
      const hand = B[side + 'Hand'];
      const S = side === 'l' ? 'L' : 'R';
      const get = (n) => this.model.getObjectByName(`Bip01_${S}_${n}`);
      const H = worldPos(hand, new THREE.Vector3());
      const mid = get('Finger2'), index = get('Finger1'), pinky = get('Finger4');
      const M = mid ? worldPos(mid, new THREE.Vector3()) : H.clone().add(new THREE.Vector3(side === 'l' ? 0.08 : -0.08, -0.05, 0));
      const I = index ? worldPos(index, new THREE.Vector3()) : M.clone().add(new THREE.Vector3(0, 0, 0.02));
      const P = pinky ? worldPos(pinky, new THREE.Vector3()) : M.clone().add(new THREE.Vector3(0, 0, -0.02));
      const fingerDir = M.clone().sub(H).normalize();
      const across = I.clone().sub(P).normalize();
      const nW = new THREE.Vector3().crossVectors(fingerDir, across).normalize();
      // palm faces the thigh in the A-pose: medial + down
      const medialDown = new THREE.Vector3(side === 'l' ? -1 : 1, -1, 0);
      if (nW.dot(medialDown) < 0) nW.negate();
      const handQInv = worldQuat(hand, new THREE.Quaternion()).invert();
      const palmLocal = nW.clone().applyQuaternion(handQInv);
      const center = H.clone().lerp(M, 0.5);
      const socketW = center.addScaledVector(nW, (0.015 + BALL_RADIUS) / scale);
      const socket = socketW.sub(H).applyQuaternion(handQInv);
      bind.palm[side] = { normal: palmLocal, socket };
      // finger curl axes (bone local): axis = fingerDir x palmNormal
      for (const chain of FINGER_CHAINS) {
        let prevDir = fingerDir.clone();
        for (let j = 0; j < chain.length; j++) {
          const bone = get(chain[j]);
          if (!bone) break;
          const child = j + 1 < chain.length ? get(chain[j + 1]) : null;
          const dir = child ? worldPos(child, new THREE.Vector3()).sub(worldPos(bone, new THREE.Vector3())).normalize() : prevDir.clone();
          prevDir = dir;
          const axisW = new THREE.Vector3().crossVectors(dir, nW);
          if (axisW.lengthSq() < 1e-8) continue;
          axisW.normalize();
          const axisL = axisW.applyQuaternion(worldQuat(bone, new THREE.Quaternion()).invert());
          bind.fingers[side].push({ bone, axis: axisL, scale: FINGER_JOINT_SCALE[j] * (chain[0] === 'Finger0' ? 0.6 : 1), thumb: chain[0] === 'Finger0' });
        }
      }
    }
    this._bind = bind;
  }

  _buildRing() {
    const team = this.player ? this.player.team : -1;
    const color = TEAM_COLORS[team] !== undefined ? TEAM_COLORS[team] : 0xffffff;
    const mat = new THREE.MeshBasicMaterial({
      map: ringTexture(), color, transparent: true, depthWrite: false, opacity: AVATAR.ring.opacity,
      polygonOffset: true, polygonOffsetFactor: -2, polygonOffsetUnits: -2,
    });
    const ring = new THREE.Mesh(_ringGeo, mat);
    ring.name = 'TeamRing';
    ring.scale.setScalar(AVATAR.ring.size * this.worldScale);
    ring.position.y = AVATAR.ring.y;
    ring.renderOrder = 1;
    ring.castShadow = false; ring.receiveShadow = false;
    this.root.add(ring);
    this._ring = ring;
  }

  // ------------------------------------------------------------------------------------------------ public API
  /** True while physics drives the body. */
  get isRagdoll() { return !!(this.ragdoll && this.ragdoll.active); }
  /** Planar forward of the avatar (from player.yaw). */
  getForward(out) { return out.set(Math.sin(this._yaw), 0, Math.cos(this._yaw)); }
  /** World position of a humanoid bone (key of `bones`). */
  getBonePosition(key, out) {
    const b = this.bones[key];
    if (!b) return out.copy(this.player ? this.player.position : _ZERO);
    return b.getWorldPosition(out);
  }
  /** Hips (pelvis) world position - follows the ragdoll. */
  hipsPosition(out = new THREE.Vector3()) { return this.getBonePosition('hips', out); }
  /** Head world position. */
  headPosition(out = new THREE.Vector3()) { return this.getBonePosition('head', out); }

  /** Whip-through throw arc toward `dir` (world direction). */
  playThrow(dir) {
    this._throwT = 0;
    if (dir && dir.lengthSq() > 1e-8) this._throwDir.copy(dir).normalize();
    else this.getForward(this._throwDir);
    this._oneShot = null;
  }

  /** Catch absorb: hands pull the ball into the chest, small crouch/spine give. */
  playCatch() {
    this._absorbT = 0;
    // remember where the hands were (root-local) so the absorb starts from the catch point
    const r = this._ik.r;
    if (r.init && r.w > 0.2) this._absorbFrom.copy(r.pos);
    else this._absorbFrom.set(0, 1.25 * this.worldScale, 0.45 * this.worldScale);
    this._oneShot = null;
  }

  /** Additive hit flinch; `dir` = direction the impact pushes (e.g. ball velocity). */
  playHit(dir) {
    const F = AVATAR.flinch;
    let pf = -1, pr = 0;
    if (dir && dir.lengthSq() > 1e-8) {
      _v1.copy(dir); _v1.y = 0;
      if (_v1.lengthSq() > 1e-8) {
        _v1.normalize();
        this.getForward(_fwd);
        _right.set(-_fwd.z, 0, _fwd.x); // yaw frame right = (-cos yaw, 0, sin yaw)
        pf = _v1.dot(_fwd); pr = _v1.dot(_right);
      }
    }
    this._flinchP.v += pf * F.impulse;
    this._flinchR.v += pr * F.impulse;
  }

  cheer() { this._playOneShot('cheer', AVATAR.oneShot.cheerMax); }
  defeat() { this._playOneShot('defeat', AVATAR.oneShot.defeatMax); }
  wave() { this._playOneShot('wave', AVATAR.oneShot.waveMax); }

  _playOneShot(key, maxDur) {
    if (!this._actions[key]) return;
    const clipDur = this._clipSet.durations[key];
    this._oneShot = { key, t: 0, dur: Math.min(clipDur, maxDur) };
    this._times[key] = 0;
  }

  /** White (or coloured) emissive flash, unscaled time (hit flash 0.05 s by default). */
  flash(color = 0xffffff, duration = HIT_FLASH_DURATION) {
    this._flashColor.set(color);
    this._flashDur = Math.max(1e-3, duration);
    this._flashT = this._flashDur;
    this._fxNeutral = false;
  }

  /** Persistent colour tint (amount 0 clears), e.g. glue slow or ability auras. */
  setTint(color, amount = 0.5) {
    if (color === null || color === undefined || !(amount > 0)) { this._tintAmt = 0; }
    else { this._tintColor.set(color); this._tintAmt = clamp(amount, 0, 1); }
    this._fxNeutral = false;
  }

  /** Cloak: enemies of the local player see a near-invisible refraction shimmer, everyone else a translucent body. */
  setCloaked(b) { this.cloaked = !!b; this._fxNeutral = false; }

  /** Frozen solid: icy tint + frost shell, animation paused (pose held). */
  setFrozen(b) {
    b = !!b;
    if (b && !this._frostMat && this.loaded) this._buildFrost();
    this.frozen = b;
    this._fxNeutral = false;
  }

  setVisible(b) { this.root.visible = !!b; }

  /**
   * Freezes a ghost copy of the current pose in the world that fades out over `lifetime` (scaled seconds).
   * @returns {{ root: THREE.Object3D, dispose(): void }|null}
   */
  spawnAfterimage(lifetime = 1, color = null) {
    if (!this.loaded || !game.scene) return null;
    const tint = new THREE.Color(color !== null && color !== undefined ? color : (this.player && this.player.hero && this.player.hero.color) || 0x8899ff);
    this.root.updateMatrixWorld(true);
    const ghost = this._cloneModel();
    const holder = new THREE.Group();
    holder.name = 'Afterimage';
    this.model.matrixWorld.decompose(holder.position, holder.quaternion, holder.scale);
    ghost.position.set(0, 0, 0); ghost.quaternion.identity(); ghost.scale.set(1, 1, 1);
    holder.add(ghost);
    const G = AVATAR.ghost;
    const mats = new Map();
    ghost.traverse((o) => {
      if (!o.isMesh) return;
      o.castShadow = false; o.receiveShadow = false; o.frustumCulled = false;
      let m = mats.get(o.material);
      if (!m) {
        m = cloneHooked(o.material);
        m.transparent = true; m.depthWrite = false; m.opacity = G.opacity;
        m.color.copy(_WHITE).lerp(tint, G.tint);
        m.emissive.copy(tint).multiplyScalar(0.25);
        m.sheen = 0; m.clearcoat = 0;
        const u = HOOKS.get(m);
        u.duRim.value = G.rim; u.duRimColor.value.copy(tint); u.duRimAlpha.value = G.rimAlpha; u.duShimmer.value = 0; u.duInflate.value = 0;
        mats.set(o.material, m);
      }
      o.material = m;
    });
    game.scene.add(holder);
    holder.updateMatrixWorld(true);
    holder.traverse((o) => { o.matrixAutoUpdate = false; }); // frozen pose: nothing moves
    const entry = {
      root: holder, age: 0, life: Math.max(0.05, lifetime), mats: [...mats.values()], disposed: false,
      dispose: () => this._disposeAfterimage(entry),
    };
    this._afterimages.push(entry);
    return entry;
  }

  /**
   * Animated duplicate (Night Parade / Mirage clones): own locomotion from its own motion, mirrored non-gait clip
   * weights/times and a mirrored upper body (throws, catches, wind-ups). The clone updates itself every frame from
   * this avatar's update; calling handle.update(dt) yourself is also fine (once per frame).
   * @returns {{ root: THREE.Group, model: THREE.Object3D, followYaw: boolean, update(dt:number): void, setOpacity(o:number): void, dispose(): void }|null}
   */
  createClone() {
    if (!this.loaded) return null;
    const model = this._cloneModel();
    model.position.set(0, 0, 0); model.quaternion.identity();
    const root = new THREE.Group();
    root.name = 'AvatarClone';
    const pivot = new THREE.Group();
    root.add(pivot);
    pivot.add(model);
    this.root.getWorldPosition(root.position);
    root.rotation.y = this._yaw;
    const matMap = new Map();
    const mats = [];
    model.traverse((o) => {
      if (!o.isMesh) return;
      o.frustumCulled = false;
      let m = matMap.get(o.material);
      if (!m) {
        m = cloneHooked(o.material);
        matMap.set(o.material, m);
        mats.push({ mat: m, baseOpacity: m.opacity, baseTransparent: m.transparent, baseDepthWrite: m.depthWrite });
      }
      o.material = m;
    });
    if (this._ring) {
      const ring = this._ring.clone();
      ring.material = this._ring.material.clone();
      root.add(ring);
      mats.push({ mat: ring.material, baseOpacity: ring.material.opacity, baseTransparent: true, baseDepthWrite: false, ring: true });
    }
    const nodes = collectPoseNodes(model);
    const mixer = new THREE.AnimationMixer(model);
    const actions = {};
    for (const key of this._keys) {
      const a = mixer.clipAction(this._clipSet.clips[key]);
      a.setLoop(THREE.LoopRepeat, Infinity); a.timeScale = 0; a.play(); a.setEffectiveWeight(0);
      actions[key] = a;
    }
    // Upper body = Spine1 subtree (thighs hang off Spine in the Biped, so they stay with the clone's own gait).
    const upper = [];
    const spine1 = this.bones.spine1;
    for (let i = 0; i < this._nodes.length; i++) {
      let n = this._nodes[i], inUpper = false;
      while (n) { if (n === spine1) { inUpper = true; break; } n = n.parent; }
      if (inUpper) upper.push(i);
    }
    const handle = {
      root, model, mixer, followYaw: true, disposed: false,
      _nodes: nodes, _actions: actions, _w: {}, _times: {}, _phase: this._phase, _speed: 0,
      _lastPos: root.position.clone(), _pose: new Float32Array(nodes.length * 7), _poseInit: false, _frame: -1,
      _upper: upper, _mats: mats, _blend: {},
      update: (dt) => this._updateClone(handle, dt),
      setOpacity: (o) => {
        for (const e of mats) {
          e.mat.opacity = e.baseOpacity * clamp(o, 0, 1);
          const t = e.baseTransparent || o < 0.999;
          if (e.mat.transparent !== t) { e.mat.transparent = t; e.mat.needsUpdate = true; }
        }
      },
      dispose: () => this._disposeClone(handle),
    };
    for (const key of this._keys) { handle._w[key] = this._w[key]; handle._times[key] = this._times[key]; }
    if (game.scene) game.scene.add(root);
    this._clones.add(handle);
    this._updateClone(handle, 0);
    return handle;
  }

  /** Switch to the physics ragdoll (elimination). impulse: N*s-ish vector, point: world hit point. */
  enableRagdoll(impulse = null, point = null) {
    if (!this.loaded || !this.ragdoll) return;
    if (this.ragdoll.active) { this.ragdoll.applyImpulse(impulse, point); return; }
    this._recover = null;
    this._oneShot = null;
    this._throwT = -1;
    this._absorbT = -1;
    this.root.updateMatrixWorld(true);
    this.ragdoll.activate(impulse, point);
  }

  /** Blend from the ragdoll back to animation over AVATAR.recoverTime seconds. */
  recoverFromRagdoll() {
    if (!this.ragdoll || !this.ragdoll.active) return;
    capturePose(this._nodes, this._recoverPose);
    this.ragdoll.deactivate();
    this._recover = { t: 0, dur: AVATAR.recoverTime };
    this._resetMotionFilters();
  }

  /** Full reset (new round, revive): no ragdoll, no effects, idle pose. */
  resetVisual() {
    if (this.ragdoll) this.ragdoll.deactivate();
    this._recover = null;
    this.frozen = false; this._frozenAmt = 0;
    this.cloaked = false; this._cloakAmt = 0;
    this._tintAmt = 0; this._flashT = 0;
    this._oneShot = null; this._throwT = -1; this._absorbT = -1;
    this._flinchP.x = this._flinchP.v = this._flinchR.x = this._flinchR.v = 0;
    this._lookW = 0; this._lookInit = false; this._lookEnemy = null;
    this._ik.r.w = this._ik.l.w = 0; this._ik.r.init = this._ik.l.init = false;
    this._curlR = this._curlL = 0; this._windupW = 0; this._palmW = 0; this._upperActivity = 0;
    this._legYaw = 0; this._backwards = false; this._phaseDir = 1;
    this._stillTime = 0; this._idleKey = 'idle';
    for (const k of this._keys) { this._w[k] = 0; this._times[k] = 0; }
    if (this._actions.idle) this._w.idle = 1;
    this._resetMotionFilters();
    this.leanPivot.rotation.set(0, 0, 0);
    this.root.visible = true;
    if (this._bindPose) { applyPose(this._nodes, this._bindPose); capturePose(this._nodes, this._animPose); }
    this._fxNeutral = false;
    if (this.loaded) { this._updateMaterials(0, 0); this.update(0, 0); }
  }

  dispose() {
    if (this._disposed) return;
    this._disposed = true;
    for (const a of [...this._afterimages]) this._disposeAfterimage(a);
    for (const c of [...this._clones]) this._disposeClone(c);
    if (this.ragdoll) this.ragdoll.dispose();
    if (this.mixer) { this.mixer.stopAllAction(); this.mixer.uncacheRoot(this.model); }
    this.root.removeFromParent();
    for (const e of this._mats) e.mat.dispose();
    if (this._frostMat) this._frostMat.dispose();
    if (this._ring) this._ring.material.dispose();
    if (this.model) this.model.traverse((o) => { if (o.isSkinnedMesh && o.skeleton) o.skeleton.dispose(); });
    this.loaded = false;
  }

  // ------------------------------------------------------------------------------------------------ per frame
  /**
   * @param {number} dt     scaled seconds (gameplay; 0 during hitstop/pause)
   * @param {number} realDt unscaled seconds (flash timing)
   */
  update(dt, realDt = dt) {
    if (!this.loaded || this._disposed) return;
    if (this.player && this.player.root && this.root.parent !== this.player.root) this.player.root.add(this.root);
    this._syncRootYaw();
    this._updateMaterials(dt, realDt);
    this._updateRing();

    if (this.ragdoll && this.ragdoll.active) {
      this.ragdoll.applyToBones(typeof game.alpha === 'number' ? game.alpha : 1);
      this._updateEffects(dt);
      return;
    }
    if (this.frozen) { this._updateEffects(dt); return; } // pose held exactly as it was when frozen

    const inp = this._gatherInput(dt);
    applyPose(this._nodes, this._animPose);        // undo last frame's procedural layer
    this._updateClipLayer(dt, inp);
    this.mixer.update(0);                          // evaluate at the explicit times/weights
    capturePose(this._nodes, this._animPose);
    this._updateLean(dt, inp);
    this.root.updateWorldMatrix(true, true);
    this._procedural(dt, inp);
    if (this._recover) this._blendRecover(dt);
    this.root.updateMatrixWorld(true);             // fresh matrices for sockets (ball attach) and ragdoll tracking
    this.ragdoll.track(dt, inp.teleported);
    this._updateEffects(dt);
  }

  _syncRootYaw() {
    const p = this.player;
    this._yaw = p && typeof p.yaw === 'number' ? p.yaw : 0;
    const parentYaw = p && p.root ? p.root.rotation.y : 0;
    this.root.rotation.set(0, this._yaw - parentYaw, 0);
  }

  _gatherInput(dt) {
    const inp = this._inp;
    const p = this.player || {};
    const vel = p.velocity || _ZERO;
    const motor = p.motor;
    inp.speed = motor && typeof motor.planarSpeed === 'number' ? motor.planarSpeed : Math.hypot(vel.x, vel.z);
    inp.grounded = motor && typeof motor.isGrounded === 'boolean' ? motor.isGrounded : p.isGrounded !== false;
    const cur = p.fsm ? p.fsm.current : null;
    const state = typeof cur === 'string' ? cur : cur && cur.id ? cur.id : 'grounded';
    const reason = p.fsm ? p.fsm.incapReason : null;
    inp.state = state;
    inp.incap = state === 'incapacitated';
    inp.eliminated = inp.incap && reason === 'eliminated';
    inp.sliding = state === 'sliding' || !!(motor && motor.isSliding);
    inp.stunned = state === 'stunned' || (inp.incap && reason === 'grabbed')
      || !!(p.status && typeof p.status.has === 'function' && p.status.has('stunned'));
    const c = p.combat;
    inp.charging = !!(c && c.isCharging);
    inp.charge = c ? clamp(c.charge || 0, 0, 1) : 0;
    inp.hasBall = !!(c && c.hasBall);
    inp.catching = !inp.incap && (state === 'catching' || !!(c && c.catchArmed));
    inp.airborne = !inp.grounded && !inp.sliding && !inp.incap;
    // teleport detection (resets motion filters so a teleport does not look like a huge acceleration)
    const feet = p.position || _ZERO;
    inp.teleported = this._hasPrev && feet.distanceTo(this._prevFeet) > AVATAR.lean.teleportDistance;
    this._prevFeet.copy(feet);
    if (inp.teleported) this._resetMotionFilters();
    return inp;
  }

  _resetMotionFilters() {
    this._accel.set(0, 0, 0);
    const v = this.player && this.player.velocity;
    if (v) this._prevVel.set(v.x, 0, v.z); else this._prevVel.set(0, 0, 0);
    this._leanPitch = 0; this._leanRoll = 0;
    this._hasPrev = true;
    if (this.ragdoll) this.ragdoll.resetTracking();
  }

  // ------------------------------------------------------------------------------------------------ clip layer
  _updateClipLayer(dt, inp) {
    const A = AVATAR, W = this._wTarget;
    for (const k of this._keys) W[k] = 0;
    let rest = 1;

    // One-shot emote (cheer / wave / defeat) - cancelled (faded out) by any gameplay action.
    const os = this._oneShot;
    if (os) {
      os.t += dt;
      const busy = inp.speed > 1.2 || inp.airborne || inp.charging || inp.catching || inp.stunned || inp.sliding || this._throwT >= 0;
      if (busy && os.dur - os.t > A.oneShot.fadeOut) os.dur = os.t + A.oneShot.fadeOut;
      if (os.t >= os.dur) this._oneShot = null;
      else {
        const e = oneShotEnvelope(os.t, os.dur, A.oneShot.fadeIn, A.oneShot.fadeOut);
        W[os.key] = e; rest -= e;
        this._times[os.key] = Math.min(os.t, this._clipSet.durations[os.key] - 1e-3);
      }
    }

    if (inp.stunned && this._actions.stunned) { W.stunned += rest; rest = 0; }

    const C = A.crouch;
    const crouch = inp.sliding ? C.slide : inp.catching ? C.catch : inp.charging ? C.charge : this._absorbT >= 0 ? C.absorb : 0;
    if (crouch > 0 && this._actions.crouch) { const cw = rest * crouch; W.crouch += cw; rest -= cw; }

    // Gait.
    let rate, blend;
    if (inp.airborne) {
      blend = this._blend;
      blend.idle = 0; blend.walk = 0; blend.run = 1; blend.sprint = 0;
      blend.strideSpeed = this._strides.run.speed; blend.cycle = this._strides.run.duration;
      rate = A.airborneRate;
    } else {
      blend = locomotionBlend(inp.speed, this._strides, A.idleSpeed, this._blend);
      rate = gaitRate(inp.speed, blend.strideSpeed, A.gaitRate[0], A.gaitRate[1]);
    }
    // Idle variant (look-around) after standing still for a while.
    const still = blend.idle > 0.95 && rest > 0.95 && !inp.hasBall && !inp.catching;
    if (still) this._stillTime += dt; else this._stillTime = 0;
    if (this._idleKey === 'idle' && this._stillTime > A.idleVariantAfter && this._actions.lookaround) {
      this._idleKey = 'lookaround'; this._times.lookaround = 0; this._variantTime = this._clipSet.durations.lookaround;
    } else if (this._idleKey !== 'idle') {
      this._variantTime -= dt;
      if (this._variantTime <= 0 || !still) { this._idleKey = 'idle'; this._stillTime = 0; }
    }
    const idleKey = this._actions[this._idleKey] ? this._idleKey : 'idle';
    if (W[idleKey] !== undefined) W[idleKey] += rest * blend.idle;
    for (const k of GAIT_KEYS) if (W[k] !== undefined) W[k] += rest * blend[k];

    this._phase = frac(this._phase + (dt * rate * this._phaseDir) / Math.max(0.05, blend.cycle));

    // Cross-fade + normalise (a sum < 1 would blend toward the A-pose).
    const k = dampFactor(1 / A.crossfade, dt);
    const w = this._w;
    for (const key of this._keys) w[key] += (W[key] - w[key]) * k;
    if (normalizeWeights(w, this._keys) <= 1e-6 && this._actions.idle) w.idle = 1;

    // Explicit clip times.
    const durs = this._clipSet.durations;
    for (const key of this._keys) {
      const d = durs[key];
      if (this._strides[key]) this._times[key] = frac(this._phase + this._strides[key].offset) * d;
      else if (!(os && os.key === key) && LOOPING.has(key)) this._times[key] = (this._times[key] + dt) % d;
      const action = this._actions[key];
      action.time = this._times[key];
      action.setEffectiveWeight(w[key]);
    }
  }

  // ------------------------------------------------------------------------------------------------ lean
  _updateLean(dt, inp) {
    const L = AVATAR.lean;
    const p = this.player || {};
    const v = p.velocity || _ZERO;
    if (dt > 1e-4) {
      // planar acceleration from the velocity derivative (includes the centripetal v*yawRate term in turns)
      _v1.set((v.x - this._prevVel.x) / dt, 0, (v.z - this._prevVel.z) / dt);
      if (inp.teleported) _v1.set(0, 0, 0);
      this._accel.lerp(_v1, dampFactor(L.accelRate, dt));
      this._prevVel.set(v.x, 0, v.z);
    }
    this._hasPrev = true;
    this.getForward(_fwd);
    _left.set(_fwd.z, 0, -_fwd.x); // yaw frame left
    const aF = this._accel.dot(_fwd), aL = this._accel.dot(_left);
    let pitch = 0, roll = 0;
    const active = !inp.stunned && !inp.incap && !this._oneShot;
    if (active) {
      const lean = leanFromAcceleration(aF, aL, GRAVITY, L.pitchGain, L.rollGain, L.maxPitch, L.maxRoll, _leanOut);
      pitch = lean.pitch; roll = lean.roll;
      if (inp.airborne) { pitch *= L.airborneMul; roll *= L.airborneMul; }
      if (inp.sliding) pitch -= L.slideBack;
    }
    this._leanPitch = damp(this._leanPitch, pitch, L.rate, dt);
    this._leanRoll = damp(this._leanRoll, roll, L.rate, dt);
    this.leanPivot.rotation.set(this._leanPitch, 0, this._leanRoll);
  }

  // ------------------------------------------------------------------------------------------------ procedural
  _procedural(dt, inp) {
    const B = this.bones, A = AVATAR, s = this.worldScale;
    const p = this.player || {};
    this.getForward(_fwd);
    _left.set(_fwd.z, 0, -_fwd.x);
    _right.copy(_left).negate();

    // ---- timers / springs (scaled time)
    if (this._throwT >= 0) { this._throwT += dt / A.throw.whipTime; if (this._throwT >= 1) this._throwT = -1; }
    if (this._absorbT >= 0) { this._absorbT += dt / A.catch.absorbTime; if (this._absorbT >= 1) this._absorbT = -1; }
    const F = A.flinch;
    springStep(this._flinchP, F.omega, F.zeta, dt);
    springStep(this._flinchR, F.omega, F.zeta, dt);
    this._flinchP.x = clamp(this._flinchP.x, -F.max, F.max);
    this._flinchR.x = clamp(this._flinchR.x, -F.max, F.max);
    const throwing = this._throwT >= 0;
    const windupTarget = inp.charging && !throwing ? Math.min(1, 0.3 + inp.charge) : 0;
    this._windupW = damp(this._windupW, windupTarget, 12, dt);

    // ---- 1. strafe / back-pedal: pelvis + legs turn toward travel, spine counter-rotates
    const St = A.strafe;
    let legYawTarget = 0;
    const v = p.velocity || _ZERO;
    const canStrafe = inp.grounded && !inp.sliding && !inp.stunned && !inp.incap && !this._oneShot && inp.speed > St.minSpeed;
    if (canStrafe) {
      const moveAngle = Math.atan2(v.x * _left.x + v.z * _left.z, v.x * _fwd.x + v.z * _fwd.z);
      const r = strafeLegYaw(moveAngle, St.maxLegYaw, St.backMaxLegYaw, St.backStart, St.backEnd, this._backwards, _strafeOut);
      this._backwards = r.backwards;
      legYawTarget = r.legYaw;
    } else if (inp.speed < 0.3) this._backwards = false;
    this._phaseDir = this._backwards ? -1 : 1;
    this._legYaw = damp(this._legYaw, legYawTarget, St.rate, dt);
    if (Math.abs(this._legYaw) > 1e-4 && B.root) {
      B.root.quaternion.premultiply(_q1.setFromAxisAngle(_UP, this._legYaw)); // Bip01 parent space is model space
      B.root.updateMatrixWorld(true);
    }

    // ---- 2. spine: counter twist, throw twist/flex, catch absorb, hit flinch
    const T = A.throw;
    let twist = -this._legYaw * St.counter + T.windupTwist * this._windupW;
    let flex = T.windupBack * this._windupW + this._flinchP.x;
    let side = this._flinchR.x;
    if (throwing) {
      const t = this._throwT;
      // wind-up -> whip -> follow-through (twist) ; forward flex peaks at release
      const tw = t < 0.45 ? THREE.MathUtils.lerp(T.windupTwist, T.followTwist, smoothstep(0, 0.45, t)) : T.followTwist * (1 - smoothstep(0.45, 1, t));
      twist += tw;
      flex += T.followFlex * Math.sin(Math.PI * clamp(t / 0.85, 0, 1));
    }
    if (this._absorbT >= 0) flex += A.catch.absorbFlex * Math.sin(Math.PI * this._absorbT);
    if (B.spine1 && B.chest && (Math.abs(twist) + Math.abs(flex) + Math.abs(side) > 1e-4)) {
      // distributed over the two upper spine links (Spine1 45 %, Spine2 55 %)
      this._bendSpineLink(B.spine1, 0.45, twist, flex, side);
      this._bendSpineLink(B.chest, 0.55, twist, flex, side);
      B.spine1.updateMatrixWorld(true);
    }

    // ---- 3. head / neck / eyes LookAt
    this._updateThreat(inp);
    const lookW = this._resolveLook(inp, _T);
    this._applyLook(dt, _T, lookW, inp);

    // ---- 4. arms (two-bone IK) + 5. hands
    this._updateArms(dt, inp, s);
  }

  /** Rotates one spine link by its share of twist (about up), flex (+ forward) and side bend (+ right). */
  _bendSpineLink(bone, share, twist, flex, side) {
    rotateBoneWorld(bone, _UP, twist * share);
    rotateBoneWorld(bone, _left, flex * share);
    rotateBoneWorld(bone, _fwd, side * share);
    refresh(bone);
  }

  /** Finds the most urgent incoming live ball (predicted impact) - drives LookAt and the catch reach. */
  _updateThreat(inp) {
    this._threatBall = null; this._threatT = Infinity;
    const balls = game.balls;
    if (!balls || typeof balls.incomingLive !== 'function' || inp.incap) return;
    requestTrajectory();
    this._incoming.length = 0;
    let list = this._incoming;
    try {
      const res = balls.incomingLive(this.player, this._incoming);
      if (Array.isArray(res)) list = res;
    } catch (e) { return; }
    const horizon = AVATAR.look.threatHorizon;
    for (let i = 0; i < list.length; i++) {
      const ball = list[i];
      if (!ball || !ball.position) continue;
      const t = this._predictImpact(ball, horizon, _threatPoint);
      if (t < this._threatT) { this._threatT = t; this._threatBall = ball; this._threatPoint.copy(_threatPoint); }
    }
  }

  /** Time to impact (s) and impact point for a ball heading to this player (Trajectory if available, else linear). */
  _predictImpact(ball, horizon, out) {
    if (Trajectory && typeof Trajectory.predictImpact === 'function') {
      try {
        const r = Trajectory.predictImpact(ball, this.player, horizon);
        if (r && r.point) { out.copy(r.point); return r.t; }
        return Infinity;
      } catch (e) { /* fall through to the linear estimate */ }
    }
    const vel = ball.velocity;
    if (!vel) return Infinity;
    const chest = worldPos(this.bones.chest || this.bones.hips, _v3);
    _v1.subVectors(chest, ball.position);
    const vv = vel.lengthSq();
    if (vv < 1) return Infinity;
    const t = _v1.dot(vel) / vv;
    if (t < 0 || t > horizon) return Infinity;
    out.copy(ball.position).addScaledVector(vel, t);
    if (out.distanceTo(chest) > 1.3 * this.worldScale) return Infinity;
    return t;
  }

  /** Chooses the look target (world point in `out`) and returns the desired look weight. */
  _resolveLook(inp, out) {
    const L = AVATAR.look;
    const p = this.player || {};
    if (inp.incap && !inp.stunned) return 0;
    if (this._oneShot || inp.stunned) return 0;
    let w = 0;
    if (this._threatBall) { out.copy(this._threatBall.position); w = L.ballWeight; }
    else if ((inp.charging || this._throwT >= 0) && p.combat) {
      const tgt = p.combat.currentTarget || (p.intent && p.intent.target);
      if (tgt && tgt.position) { this._headOf(tgt, out); w = L.aimWeight; }
      else if (p.intent && p.intent.aimPoint) { out.copy(p.intent.aimPoint); w = L.aimWeight; }
      else if (this._throwT >= 0) { out.copy(worldPos(this.bones.head, _v1)).addScaledVector(this._throwDir, 10); w = L.aimWeight; }
    } else if (p.intent && p.intent.target && p.intent.target.position) {
      this._headOf(p.intent.target, out); w = L.targetWeight;
    } else {
      // idle awareness: track the nearest opponent (rescanned a few times per second)
      this._lookRescan -= game.time ? game.time.dt : 0.016;
      if (this._lookRescan <= 0) {
        this._lookRescan = L.rescan;
        this._lookEnemy = typeof game.nearestEnemy === 'function' && p.position ? game.nearestEnemy(p, p.position, L.idleRange) : null;
      }
      if (this._lookEnemy && this._lookEnemy.position) { this._headOf(this._lookEnemy, out); w = L.idleWeight; }
    }
    if (inp.sliding) w *= 0.5;
    if (inp.airborne) w *= 0.7;
    return w;
  }

  _headOf(player, out) {
    const av = player.avatar;
    if (av && av.loaded && av.bones.head) return worldPos(av.bones.head, out);
    return out.copy(player.position).setY(player.position.y + 1.6);
  }

  _applyLook(dt, target, weight, inp) {
    const B = this.bones, L = AVATAR.look;
    if (!B.head || !B.neck) return;
    this._lookW = damp(this._lookW, weight, L.weightRate, dt);
    const headPos = worldPos(B.head, _v1);
    const face = _v2.copy(this._bind.face).applyQuaternion(worldQuat(B.head, _q1));
    if (!this._lookInit) { this._lookDir.copy(face); this._lookInit = true; }
    if (weight > 0) {
      _v3.subVectors(target, headPos);
      if (_v3.lengthSq() > 1e-6) {
        _v3.normalize();
        // clamp in the body's yaw frame
        let yaw = Math.atan2(_v3.dot(_left), _v3.dot(_fwd));
        let pitch = Math.asin(clamp(_v3.y, -1, 1));
        yaw = clamp(yaw, -L.maxYaw, L.maxYaw);
        pitch = clamp(pitch, -L.maxDown, L.maxUp);
        const cp = Math.cos(pitch);
        _v3.copy(_fwd).multiplyScalar(cp * Math.cos(yaw)).addScaledVector(_left, cp * Math.sin(yaw)).addScaledVector(_UP, Math.sin(pitch));
        this._lookDir.lerp(_v3, dampFactor(this._threatBall ? L.ballRate : L.rate, dt)).normalize();
      }
    } else {
      this._lookDir.lerp(face, dampFactor(L.rate, dt)).normalize();
    }
    const w = this._lookW;
    let headFlinch = this._flinchP.x * AVATAR.flinch.headShare;
    if (w < 0.01 && Math.abs(headFlinch) < 1e-3) return;

    // swing from the current face direction to the look direction, split neck/head (same world axis)
    _q2.setFromUnitVectors(face, this._lookDir);
    const qw = clamp(_q2.w, -1, 1);
    let angle = 2 * Math.acos(Math.abs(qw));
    const sinHalf = Math.sqrt(Math.max(0, 1 - qw * qw));
    if (sinHalf > 1e-5 && angle > 1e-4) {
      _axis.set(_q2.x, _q2.y, _q2.z).divideScalar(sinHalf);
      if (qw < 0) _axis.negate();
      angle *= w;
      const neckAngle = angle * L.neckShare;
      rotateBoneWorld(B.neck, _axis, neckAngle);
      refresh(B.neck);
      // Biped clavicles hang off the neck: counter-rotate so the shoulders do not follow the head.
      if (B.lClavicle) rotateBoneWorld(B.lClavicle, _axis, -neckAngle);
      if (B.rClavicle) rotateBoneWorld(B.rClavicle, _axis, -neckAngle);
      refresh(B.head);
      rotateBoneWorld(B.head, _axis, angle - neckAngle);
    } else refresh(B.head);
    // whiplash: the head lags the torso flinch
    if (Math.abs(headFlinch) > 1e-4) { refresh(B.head); rotateBoneWorld(B.head, _left, headFlinch); }
    B.neck.updateMatrixWorld(true);

    // eyes: converge on the target (clamped around the face direction)
    if (w > 0.05 && weight > 0) {
      for (const side of SIDES) {
        const eye = B[side + 'Eye'];
        const local = this._bind.eyes[side];
        if (!eye || !local) continue;
        const eyeFwd = _v2.copy(local).applyQuaternion(worldQuat(eye, _q1));
        _v3.subVectors(target, worldPos(eye, _v1)).normalize();
        const faceNow = _v4.copy(this._bind.face).applyQuaternion(worldQuat(B.head, _q3));
        const ang = faceNow.angleTo(_v3);
        if (ang > L.eyeClamp) _v3.lerp(faceNow, 1 - L.eyeClamp / ang).normalize();
        _q2.setFromUnitVectors(eyeFwd, _v3);
        _qs.identity().slerp(_q2, w);
        rotateBoneWorldQ(eye, _qs);
      }
    }
  }

  /** Arm targets (root-local smoothing) + analytic two-bone IK + palm orientation + finger curl. */
  _updateArms(dt, inp, s) {
    const B = this.bones, A = AVATAR, IK = A.ik, T = A.throw;
    if (!B.rUpperArm || !B.lUpperArm) return;
    const S_R = worldPos(B.rUpperArm, _S);
    const S_L = worldPos(B.lUpperArm, _E);
    // chest centre between the shoulders
    const chestC = _v1.addVectors(S_R, S_L).multiplyScalar(0.5);
    const r = this._ik.r, l = this._ik.l;
    let wR = 0, wL = 0, curlR = 0, curlL = 0, palmMode = 0; // 1 = catch, 2 = carry, 3 = wind-up
    let fastTargets = false;
    const throwing = this._throwT >= 0;

    // Desired world targets are built in _tR/_tL and poles in _pR/_pL.
    if (throwing) {
      const t = this._throwT;
      this._whipTarget(t, S_R, s, _tR);
      wR = t < 0.7 ? 1 : 1 - smoothstep(0.7, 1, t);
      _pR.copy(_right).addScaledVector(_UP, -0.2 - 0.6 * t).addScaledVector(_fwd, -0.4 + 0.3 * t);
      // left arm: from the balance point toward the left hip
      _tL.copy(S_L).addScaledVector(_fwd, (0.42 - 0.35 * t) * s).addScaledVector(_UP, (0.02 - 0.4 * t) * s).addScaledVector(_left, 0.08 * s);
      _pL.copy(_left).addScaledVector(_UP, -0.8);
      wL = T.leftArmWeight * (1 - t);
      curlR = A.hold.curl * (1 - smoothstep(0.3, 0.45, t));
      fastTargets = true;
      palmMode = 3;
    } else if (inp.charging || this._windupW > 0.05) {
      // wind-up: ball behind/above the right shoulder, left arm extended toward the target for balance
      _tR.copy(S_R).addScaledVector(_UP, 0.2 * s).addScaledVector(_fwd, -0.3 * s).addScaledVector(_right, 0.12 * s);
      _pR.copy(_right).addScaledVector(_fwd, -0.4).addScaledVector(_UP, 0.1);
      wR = this._windupW;
      _tL.copy(S_L).addScaledVector(_fwd, 0.45 * s).addScaledVector(_UP, 0.02 * s).addScaledVector(_left, 0.1 * s);
      _pL.copy(_left).addScaledVector(_UP, -0.7);
      wL = T.leftArmWeight * this._windupW;
      curlR = A.hold.curl;
      palmMode = 3;
    } else if (inp.catching || this._absorbT >= 0) {
      if (this._absorbT >= 0) {
        // absorb: from the catch point to the chest
        const t = smoothstep(0, 1, this._absorbT);
        _v2.copy(this._absorbFrom).applyMatrix4(this.root.matrixWorld);
        _v3.copy(chestC).addScaledVector(_fwd, 0.22 * s).addScaledVector(_UP, -0.12 * s);
        _v2.lerp(_v3, t);
        const wAbs = this._absorbT < 0.7 ? 1 : 1 - smoothstep(0.7, 1, this._absorbT);
        _tR.copy(_v2).addScaledVector(_right, IK.catchSpread * s);
        _tL.copy(_v2).addScaledVector(_left, IK.catchSpread * s);
        wR = wL = inp.hasBall ? Math.max(wAbs, 0.6) : wAbs;
        curlR = curlL = A.hold.catchCurl + 0.2;
      } else {
        // ready stance or reach to the predicted intercept
        if (this._threatBall) {
          _v2.copy(this._threatPoint).sub(chestC);
          const d = _v2.length(), maxD = IK.catchReach * s;
          if (d > maxD) _v2.multiplyScalar(maxD / d);
          _v2.add(chestC);
        } else {
          _v2.copy(chestC).addScaledVector(_fwd, IK.readyForward * s).addScaledVector(_UP, -IK.readyDown * s);
        }
        _tR.copy(_v2).addScaledVector(_right, (this._threatBall ? IK.catchSpread : IK.readySide) * s);
        _tL.copy(_v2).addScaledVector(_left, (this._threatBall ? IK.catchSpread : IK.readySide) * s);
        wR = wL = 1;
        curlR = curlL = A.hold.catchCurl;
        fastTargets = !!this._threatBall;
      }
      _pR.copy(_right).addScaledVector(_UP, -1).addScaledVector(_fwd, -0.3);
      _pL.copy(_left).addScaledVector(_UP, -1).addScaledVector(_fwd, -0.3);
      palmMode = 1;
    } else if (inp.hasBall) {
      // carry: ball in the right palm in front of the hip
      _tR.copy(S_R).addScaledVector(_UP, -0.46 * s).addScaledVector(_fwd, 0.2 * s).addScaledVector(_right, 0.03 * s);
      _pR.copy(_right).addScaledVector(_UP, -0.6).addScaledVector(_fwd, -0.5);
      wR = inp.speed > 3 ? A.hold.weightRun : A.hold.weightIdle;
      curlR = A.hold.curl;
      palmMode = 2;
    }
    if (inp.incap || inp.stunned) { wR = 0; wL = 0; }

    this._upperActivity = Math.max(wR, wL, this._windupW, throwing ? 1 : 0);
    this._blendTarget(r, _tR, wR, dt, fastTargets);
    this._blendTarget(l, _tL, wL, dt, fastTargets);
    this._curlR = damp(this._curlR, curlR, 12, dt);
    this._curlL = damp(this._curlL, curlL, 12, dt);
    this._palmW = damp(this._palmW, palmMode ? 1 : 0, 10, dt);

    if (r.w > 1e-3) { _v2.copy(r.pos).applyMatrix4(this.root.matrixWorld); this._solveArm('r', _v2, _pR, r.w); }
    if (l.w > 1e-3) { _v2.copy(l.pos).applyMatrix4(this.root.matrixWorld); this._solveArm('l', _v2, _pL, l.w); }

    // palms: face the ball when catching, cradle when carrying, forward on the wind-up
    if (this._palmW > 0.02 && palmMode) {
      for (const side of SIDES) {
        const ikw = side === 'r' ? r.w : l.w;
        if (ikw < 0.05) continue;
        const out = side === 'r' ? _right : _left;
        if (palmMode === 1) {
          if (this._threatBall && this._threatBall.velocity) _n.copy(this._threatBall.velocity).negate().normalize();
          else _n.copy(_fwd);
          _n.addScaledVector(out, -0.35).normalize(); // cup inward
        } else if (palmMode === 2) _n.copy(_UP).multiplyScalar(0.8).addScaledVector(out, -0.5).normalize();
        else _n.copy(_fwd).multiplyScalar(0.8).addScaledVector(_UP, 0.4).normalize();
        this._orientPalm(side, _n, this._palmW * ikw * 0.85);
      }
    }
    this._curlFingers('r', this._curlR);
    this._curlFingers('l', this._curlL);
  }

  /** Right-hand target along the throwing arc (t: 0..1 of the whip). */
  _whipTarget(t, S, s, out) {
    // keyframes relative to the shoulder in the yaw frame: wind-up -> overhead -> release (along the throw) -> follow-through
    const d = this._throwDir;
    const k0 = _k0.set(0.12, 0.2, -0.3);            // (right, up, fwd)
    const k1 = _k1.set(0.1, 0.28, 0.05);
    const dirF = d.dot(_fwd), dirR = d.dot(_right);
    const k2 = _k2.set(dirR * 0.55 + 0.04, d.y * 0.55 + 0.05, dirF * 0.55);
    const k3 = _k3.set(-0.32, -0.55, 0.35);
    let a, b, u;
    if (t < 0.18) { a = k0; b = k1; u = t / 0.18; }
    else if (t < 0.38) { a = k1; b = k2; u = (t - 0.18) / 0.2; }
    else { a = k2; b = k3; u = Math.min(1, (t - 0.38) / 0.4); }
    u = smoothstep(0, 1, u);
    const rr = THREE.MathUtils.lerp(a.x, b.x, u), uu = THREE.MathUtils.lerp(a.y, b.y, u), ff = THREE.MathUtils.lerp(a.z, b.z, u);
    return out.copy(S).addScaledVector(_right, rr * s).addScaledVector(_UP, uu * s).addScaledVector(_fwd, ff * s);
  }

  /** Smooths an IK target in root-local space (no world-space lag when running) and its weight. */
  _blendTarget(state, targetWorld, weight, dt, fast) {
    const IK = AVATAR.ik;
    if (weight > 1e-3) {
      _v4.copy(targetWorld).applyMatrix4(_invRoot.copy(this.root.matrixWorld).invert());
      if (!state.init || state.w < 0.02) { state.pos.copy(_v4); state.init = true; }
      else state.pos.lerp(_v4, fast ? 1 : dampFactor(IK.targetRate, dt));
    }
    state.w = damp(state.w, weight, fast ? IK.weightRate * 2 : IK.weightRate, dt);
    if (state.w < 1e-3) state.w = 0;
  }

  /**
   * Analytic two-bone IK: 1) bend the elbow to the law-of-cosines angle, 2) swing the upper arm so the wrist lies on
   * the shoulder->target line, 3) twist around that line so the elbow points at the pole, 4) blend by weight.
   */
  _solveArm(side, target, poleDir, weight) {
    const B = this.bones;
    const up = B[side + 'UpperArm'], lo = B[side + 'Forearm'], hand = B[side + 'Hand'];
    if (!up || !lo || !hand) return;
    const q0U = _qU0.copy(up.quaternion), q0F = _qF0.copy(lo.quaternion);
    worldPos(up, _S); worldPos(lo, _E); worldPos(hand, _W);
    const a = _S.distanceTo(_E), b = _E.distanceTo(_W);
    if (a < 1e-4 || b < 1e-4) return;
    _T.subVectors(target, _S);
    let d = _T.length();
    if (d < 1e-5) { _T.copy(_fwd); d = 1e-5; }
    const dClamped = clamp(d, Math.abs(a - b) + 0.02 * this.worldScale, (a + b) * AVATAR.ik.reach);
    _T.multiplyScalar(dClamped / d).add(_S);

    // 1) elbow bend
    _v1.subVectors(_S, _E); _v2.subVectors(_W, _E);
    const cur = _v1.angleTo(_v2);
    const want = twoBoneInteriorAngle(a, b, dClamped);
    _axis.crossVectors(_v1, _v2);
    if (_axis.lengthSq() < 1e-10) _axis.crossVectors(_v2, poleDir); // straight arm: bend toward the pole plane
    if (_axis.lengthSq() < 1e-10) return;
    _axis.normalize();
    rotateBoneWorld(lo, _axis, want - cur);
    lo.updateMatrixWorld(true);

    // 2) swing
    worldPos(hand, _W);
    _v1.subVectors(_W, _S).normalize();
    _v2.subVectors(_T, _S).normalize();
    rotateBoneWorldQ(up, _q1.setFromUnitVectors(_v1, _v2));
    up.updateMatrixWorld(true);

    // 3) pole twist around the shoulder->target axis
    worldPos(lo, _E);
    _n.copy(_v2);
    _v1.subVectors(_E, _S); _v1.addScaledVector(_n, -_v1.dot(_n));
    _pole.copy(poleDir).addScaledVector(_n, -poleDir.dot(_n));
    if (_v1.lengthSq() > 1e-8 && _pole.lengthSq() > 1e-8) {
      const ang = Math.atan2(_v3.crossVectors(_v1, _pole).dot(_n), _v1.dot(_pole));
      rotateBoneWorld(up, _n, ang);
    }

    // 4) weight
    if (weight < 0.999) {
      _qs.copy(up.quaternion); up.quaternion.copy(q0U).slerp(_qs, weight);
      _qs.copy(lo.quaternion); lo.quaternion.copy(q0F).slerp(_qs, weight);
    }
    up.updateMatrixWorld(true);
  }

  _orientPalm(side, normalWorld, weight) {
    const hand = this.bones[side + 'Hand'];
    const palm = this._bind.palm[side];
    if (!hand || !palm) return;
    const cur = _v3.copy(palm.normal).applyQuaternion(worldQuat(hand, _q1));
    _q2.setFromUnitVectors(cur, normalWorld);
    _qs.identity().slerp(_q2, clamp(weight, 0, 1));
    rotateBoneWorldQ(hand, _qs);
    hand.updateMatrixWorld(true);
  }

  _curlFingers(side, amount) {
    if (amount < 1e-3) return;
    const H = AVATAR.hold;
    for (const f of this._bind.fingers[side]) {
      const a = amount * f.scale * (f.thumb ? H.thumb / H.curl : 1);
      f.bone.quaternion.multiply(_q1.setFromAxisAngle(f.axis, a));
    }
  }

  _blendRecover(dt) {
    const rec = this._recover;
    rec.t += dt;
    const w = smoothstep(0, 1, rec.t / rec.dur);
    const buf = this._recoverPose, nodes = this._nodes;
    for (let i = 0, j = 0; i < nodes.length; i++, j += 7) {
      const n = nodes[i];
      _qs.set(buf[j], buf[j + 1], buf[j + 2], buf[j + 3]);
      n.quaternion.copy(_qs.slerp(n.quaternion, w));
      _v1.set(buf[j + 4], buf[j + 5], buf[j + 6]);
      // far away (the player was teleported while down): snap the position, blend only rotations
      if (_v1.distanceToSquared(n.position) < 2.25) n.position.lerpVectors(_v1, n.position, w);
    }
    if (rec.t >= rec.dur) this._recover = null;
  }

  // ------------------------------------------------------------------------------------------------ juice
  _cloakIsEnemy() {
    const local = game.localPlayer;
    return !!(local && this.player && local !== this.player && typeof game.areEnemies === 'function' && game.areEnemies(local, this.player));
  }

  _updateMaterials(dt, realDt) {
    const A = AVATAR;
    if (this._flashT > 0) this._flashT = Math.max(0, this._flashT - (realDt > 0 ? realDt : 0));
    // visual fades run on unscaled time (juice): a freeze reads instantly even during the hit's hitstop
    const fadeDt = realDt > 0 ? realDt : dt;
    this._frozenAmt = damp(this._frozenAmt, this.frozen ? 1 : 0, A.frozen.fade, fadeDt);
    this._cloakAmt = damp(this._cloakAmt, this.cloaked ? 1 : 0, A.cloak.fadeRate, fadeDt);
    if (!this.frozen && this._frozenAmt < 1e-3) this._frozenAmt = 0;
    if (!this.cloaked && this._cloakAmt < 2e-3) this._cloakAmt = 0;
    this._cloakEnemy = this._cloakIsEnemy();

    const active = this._flashT > 0 || this._tintAmt > 0 || this._frozenAmt > 0 || this._cloakAmt > 0;
    if (!active && this._fxNeutral) return;

    const flashI = this._flashT > 0 ? A.flash.intensity * Math.sqrt(this._flashT / this._flashDur) : 0;
    const fz = this._frozenAmt, ck = this._cloakAmt, enemy = this._cloakEnemy;
    const opacityMul = 1 + ((enemy ? A.cloak.enemyOpacity : A.cloak.allyOpacity) - 1) * ck;
    const now = game.time ? game.time.realNow : performance.now() / 1000;
    for (const e of this._mats) {
      const m = e.mat, u = e.u;
      m.color.copy(e.baseColor).lerp(this._tintColor, this._tintAmt).lerp(_ICE, fz * A.frozen.tint);
      m.emissive.copy(_BLACK);
      addScaledColor(m.emissive, this._flashColor, flashI);
      addScaledColor(m.emissive, this._tintColor, this._tintAmt * A.tint.emissive);
      addScaledColor(m.emissive, _ICE_GLOW, fz);
      if (e.role !== 'glass') {
        m.roughness = e.baseRoughness + (A.frozen.roughness - e.baseRoughness) * fz;
        const cc = Math.max(e.baseClearcoat, fz > 0.02 ? fz : 0);
        if (m.clearcoat !== cc) m.clearcoat = cc;
      }
      const rimFrozen = fz * A.frozen.rim, rimCloak = ck * (enemy ? A.cloak.enemyRim : A.cloak.allyRim);
      u.duRim.value = Math.max(rimFrozen, rimCloak);
      u.duRimColor.value.copy(rimFrozen >= rimCloak ? _ICE_RIM : _CLOAK_RIM);
      u.duRimAlpha.value = enemy ? ck * A.cloak.enemyRimAlpha : 0;
      u.duShimmer.value = enemy ? ck : 0;
      u.duTime.value = now;
      m.opacity = e.baseOpacity * opacityMul;
      const transparent = e.baseTransparent || ck > 0;
      if (m.transparent !== transparent) { m.transparent = transparent; m.needsUpdate = true; }
    }
    // frost shell
    if (this._frostMat) {
      this._frostMat.opacity = A.frozen.frostOpacity * fz;
      const vis = fz > 0.01;
      for (const fm of this._frostMeshes) fm.visible = vis;
    }
    // shadows: a near-invisible cloaked enemy must not give itself away with a shadow
    const shadows = !(enemy && ck > 0.5);
    if (shadows !== this._shadowsOn) {
      this._shadowsOn = shadows;
      for (const mesh of this._meshes) mesh.castShadow = shadows && mesh.userData.duCastShadow !== false;
    }
    this._fxNeutral = !active;
  }

  _buildFrost() {
    const A = AVATAR.frozen;
    const mat = new THREE.MeshPhysicalMaterial({
      name: 'frost', color: 0xe6f6ff, map: frostTexture(), roughness: 0.22, metalness: 0, transparent: true, opacity: 0,
      depthWrite: false, clearcoat: 1, clearcoatRoughness: 0.08, emissive: 0x0d2233,
    });
    const u = hookMaterial(mat);
    u.duInflate.value = A.inflate / this.worldScale;
    u.duRim.value = 0.9; u.duRimColor.value.set(A.rimColor); u.duRimAlpha.value = 0.35;
    this._frostMat = mat;
    for (const mesh of this._meshes) {
      if (!mesh.visible || !mesh.isSkinnedMesh) continue;
      const e = this._mats.find((x) => x.mat === mesh.material);
      if (e && e.role === 'glass') continue;
      const shell = mesh.clone();
      shell.material = mat;
      shell.renderOrder = 3;
      shell.castShadow = false; shell.receiveShadow = false;
      shell.visible = false;
      shell.name = mesh.name + '_frost';
      mesh.parent.add(shell);
      this._frostMeshes.push(shell);
    }
  }

  _updateRing() {
    const ring = this._ring;
    if (!ring) return;
    const ragdoll = this.ragdoll && this.ragdoll.active;
    const enemyCloak = this._cloakEnemy && this._cloakAmt > 0.3;
    ring.visible = !ragdoll && !enemyCloak;
    const base = this.player && this.player.isLocal ? AVATAR.ring.localOpacity : AVATAR.ring.opacity;
    ring.material.opacity = base * (1 - 0.65 * this._cloakAmt);
  }

  // ------------------------------------------------------------------------------------------------ copies
  /** SkeletonUtils clone of the model without the hand sockets (anything attached to them is not duplicated). */
  _cloneModel() {
    const rs = this.rightHandSocket, ls = this.leftHandSocket;
    const rp = rs.parent, lp = ls.parent;
    rs.removeFromParent(); ls.removeFromParent();
    const copy = cloneSkinned(this.model);
    if (rp) rp.add(rs);
    if (lp) lp.add(ls);
    return copy;
  }

  _updateEffects(dt) {
    for (let i = this._afterimages.length - 1; i >= 0; i--) {
      const a = this._afterimages[i];
      a.age += dt;
      const k = clamp(1 - a.age / a.life, 0, 1);
      const fade = Math.pow(k, 1.4);
      for (const m of a.mats) {
        m.opacity = AVATAR.ghost.opacity * fade;
        const u = HOOKS.get(m);
        if (u) u.duRimAlpha.value = AVATAR.ghost.rimAlpha * fade;
      }
      if (a.age >= a.life) this._disposeAfterimage(a);
    }
    for (const c of this._clones) this._updateClone(c, dt);
  }

  _disposeAfterimage(a) {
    if (a.disposed) return;
    a.disposed = true;
    a.root.removeFromParent();
    for (const m of a.mats) m.dispose();
    a.root.traverse((o) => { if (o.isSkinnedMesh && o.skeleton) o.skeleton.dispose(); });
    const i = this._afterimages.indexOf(a);
    if (i >= 0) this._afterimages.splice(i, 1);
  }

  _updateClone(h, dt) {
    if (h.disposed || !this.loaded) return;
    const frame = game.time ? game.time.frame : -2;
    if (h._frame === frame && frame !== -2) return;
    h._frame = frame;
    // own ground speed from the clone's world motion
    h.root.getWorldPosition(_v1);
    if (dt > 1e-4) {
      const dist = Math.hypot(_v1.x - h._lastPos.x, _v1.z - h._lastPos.z);
      const raw = dist > 2 ? 0 : dist / dt;
      h._speed = damp(h._speed, raw, 10, dt);
    }
    h._lastPos.copy(_v1);
    if (h.followYaw) h.root.rotation.y = this._yaw;
    if (h._poseInit) applyPose(h._nodes, h._pose);

    // mirrored non-gait weights/times + own gait
    let mirrored = 0;
    for (const key of this._keys) if (!CLONE_OWN_KEYS.has(key)) { h._w[key] = this._w[key]; h._times[key] = this._times[key]; mirrored += this._w[key]; }
    const rest = Math.max(0, 1 - mirrored);
    const blend = locomotionBlend(h._speed, this._strides, AVATAR.idleSpeed, h._blend);
    const rate = gaitRate(h._speed, blend.strideSpeed, AVATAR.gaitRate[0], AVATAR.gaitRate[1]);
    h._phase = frac(h._phase + (dt * rate) / Math.max(0.05, blend.cycle));
    h._w.idle = rest * blend.idle; h._w.breathe = 0; h._w.lookaround = 0;
    for (const k of GAIT_KEYS) h._w[k] = rest * blend[k];
    const durs = this._clipSet.durations;
    if (durs.idle) h._times.idle = ((h._times.idle || 0) + dt) % durs.idle;
    for (const key of this._keys) {
      if (this._strides[key]) h._times[key] = frac(h._phase + this._strides[key].offset) * durs[key];
      const a = h._actions[key];
      a.time = h._times[key] || 0;
      a.setEffectiveWeight(h._w[key] || 0);
    }
    h.mixer.update(0);
    capturePose(h._nodes, h._pose);
    h._poseInit = true;

    // mirror the upper body (throws / catches / wind-ups) from the source's final pose
    const sim = 1 - clamp(Math.abs(h._speed - this._inp.speed) / AVATAR.clone.mirrorSpeedTolerance, 0, 1);
    const w = this.ragdoll && this.ragdoll.active ? 0 : Math.max(this._upperActivity, sim);
    if (w > 1e-3) {
      for (const i of h._upper) h._nodes[i].quaternion.slerp(this._nodes[i].quaternion, w);
    }
  }

  _disposeClone(h) {
    if (h.disposed) return;
    h.disposed = true;
    this._clones.delete(h);
    h.mixer.stopAllAction();
    h.mixer.uncacheRoot(h.model);
    h.root.removeFromParent();
    for (const e of h._mats) e.mat.dispose();
    h.model.traverse((o) => { if (o.isSkinnedMesh && o.skeleton) o.skeleton.dispose(); });
  }
}

// procedural temporaries that need names (declared after the class body for readability)
const _leanOut = {};
const _strafeOut = {};
const _tR = new THREE.Vector3(), _tL = new THREE.Vector3(), _pR = new THREE.Vector3(), _pL = new THREE.Vector3();
const _k0 = new THREE.Vector3(), _k1 = new THREE.Vector3(), _k2 = new THREE.Vector3(), _k3 = new THREE.Vector3();
const _qU0 = new THREE.Quaternion(), _qF0 = new THREE.Quaternion();
const _invRoot = new THREE.Matrix4();
