// ---------------------------------------------------------------------------------------------------------------
// Pure input maths (no DOM, no three.js) shared by the Input system, touch controls and menus. Unit tested by
// inputMath.test.js (node:test).
//
//  * ActionState  - per-action edge detection that survives several device sources (keyboard, mouse, gamepad, touch)
//                   and presses shorter than one frame (press + release between two frames still yields `pressed`).
//  * radialDeadzone / axisCurve - analogue stick shaping (deadzone rescale + exponential response curve).
//  * RepeatNav    - menu navigation auto-repeat (initial delay, then a fixed rate) for held d-pad / stick directions.
//  * triggerHysteresis - analogue trigger -> digital button with hysteresis (no chatter around the threshold).
//  * touch helpers - context mode of the primary touch button, the 閃避 (dodge) gesture and what it sends, look-zone
//                   taps and the "is an enemy ball coming at me" test behind the primary button's pickup / catch
//                   choice.
// ---------------------------------------------------------------------------------------------------------------

/** W3C "standard" gamepad mapping button indices. */
export const PAD = Object.freeze({
  A: 0, B: 1, X: 2, Y: 3, LB: 4, RB: 5, LT: 6, RT: 7, SELECT: 8, START: 9, L3: 10, R3: 11,
  UP: 12, DOWN: 13, LEFT: 14, RIGHT: 15, HOME: 16,
});
export const PAD_BUTTON_COUNT = 17;

/** Device source bits an action can be held by. */
export const SRC = Object.freeze({ KEY: 1, MOUSE: 2, PAD: 4, TOUCH: 8 });

/**
 * Edge-triggered + held state of one logical action (throw, catch, jump...).
 * Sources call setSource()/keyDown()/keyUp() from event handlers at any time; latch() is called exactly once per
 * rendered frame and converts the accumulated transitions into this frame's `pressed` / `released` flags.
 */
export class ActionState {
  constructor() {
    /** Held right now (after the last latch). */
    this.held = false;
    /** Went down at least once since the previous latch. */
    this.pressed = false;
    /** Went fully up at least once since the previous latch. */
    this.released = false;
    this.bits = 0;        // SRC bits currently holding the action
    this.keyCount = 0;    // number of physical keys bound to the action that are down
    this._down = false;   // accumulated transitions since the last latch
    this._up = false;
  }

  /** A source started/stopped holding the action. Edges are only produced when the union of sources changes. */
  setSource(bit, on) {
    const was = (this.bits & bit) !== 0;
    if (on && !was) {
      if (this.bits === 0) this._down = true;
      this.bits |= bit;
    } else if (!on && was) {
      this.bits &= ~bit;
      if (this.bits === 0) this._up = true;
    }
  }

  /** Several keys may map to one action (ShiftLeft + ShiftRight): count them. */
  keyDown() { if (this.keyCount++ === 0) this.setSource(SRC.KEY, true); }
  keyUp() { if (this.keyCount > 0 && --this.keyCount === 0) this.setSource(SRC.KEY, false); }

  /** Converts accumulated transitions into this frame's flags. */
  latch() {
    this.held = this.bits !== 0;
    this.pressed = this._down;
    this.released = this._up;
    this._down = false;
    this._up = false;
  }

  /**
   * Drops every source without producing edges (used when a menu opens/closes so a key used in the menu does not
   * leak into gameplay, or on window blur).
   */
  clear() {
    this.bits = 0; this.keyCount = 0; this._down = false; this._up = false;
    this.held = false; this.pressed = false; this.released = false;
  }
}

/**
 * Radial deadzone with rescale: magnitudes inside `deadzone` become 0, the rest is remapped to 0..1 and shaped by
 * `curve` (1 = linear, 2 = quadratic fine-aim). Direction is preserved. Writes into `out` ({x, y}) and returns it.
 */
export function radialDeadzone(x, y, deadzone, out, curve = 1) {
  const m = Math.hypot(x, y);
  if (!(m > deadzone)) { out.x = 0; out.y = 0; return out; }
  const n = Math.min(1, (m - deadzone) / Math.max(1e-6, 1 - deadzone));
  const shaped = curve === 1 ? n : Math.pow(n, curve);
  out.x = (x / m) * shaped;
  out.y = (y / m) * shaped;
  return out;
}

/** Single-axis deadzone + response curve, sign preserving. */
export function axisCurve(v, deadzone = 0.12, curve = 1) {
  const a = Math.abs(v);
  if (!(a > deadzone)) return 0;
  const n = Math.min(1, (a - deadzone) / Math.max(1e-6, 1 - deadzone));
  return Math.sign(v) * (curve === 1 ? n : Math.pow(n, curve));
}

/** Clamps a 2D vector {x, y} to a maximum length in place. */
export function clampMagnitude2(v, max = 1) {
  const m = Math.hypot(v.x, v.y);
  if (m > max && m > 0) { v.x *= max / m; v.y *= max / m; }
  return v;
}

/** Analogue trigger -> digital: goes down above `on`, back up below `off`. */
export function triggerHysteresis(value, wasDown, on = 0.35, off = 0.2) {
  return wasDown ? value > off : value > on;
}

/**
 * Menu auto-repeat for a held direction: fires on the first frame, then after `delay`, then every `interval`.
 * update(active, dt) -> true on frames where the navigation step should happen.
 */
export class RepeatNav {
  constructor(delay = 0.38, interval = 0.11) {
    this.delay = delay; this.interval = interval;
    this.active = false; this._t = 0;
  }
  update(active, dt) {
    if (!active) { this.active = false; this._t = 0; return false; }
    if (!this.active) { this.active = true; this._t = this.delay; return true; }
    this._t -= dt;
    if (this._t <= 0) { this._t += this.interval; if (this._t < 0) this._t = this.interval; return true; }
    return false;
  }
  reset() { this.active = false; this._t = 0; }
}

/** Keyboard move axes from four booleans -> {x: right, y: forward}, normalised (diagonals are not faster). */
export function keyAxes(fwd, back, left, right, out) {
  out.x = (right ? 1 : 0) - (left ? 1 : 0);
  out.y = (fwd ? 1 : 0) - (back ? 1 : 0);
  if (out.x !== 0 && out.y !== 0) { out.x *= Math.SQRT1_2; out.y *= Math.SQRT1_2; }
  return out;
}

// ------------------------------------------------------------------ touch helpers
/** What the big context button of the touch layout does (its label / colour follow the mode). */
export const TOUCH_PRIMARY = Object.freeze({ THROW: 'throw', PICKUP: 'pickup', CATCH: 'catch' });

/**
 * Primary touch button mode: holding the ball -> THROW; a loose ball within manual pickup reach and no enemy ball
 * coming at the player -> PICKUP; otherwise CATCH.
 */
export function touchPrimaryMode(hasBall, ballInReach, threatened) {
  if (hasBall) return TOUCH_PRIMARY.THROW;
  return ballInReach && !threatened ? TOUCH_PRIMARY.PICKUP : TOUCH_PRIMARY.CATCH;
}

/** Outcome of a 閃避 (dodge) touch gesture; NONE while it is still undecided. */
export const DODGE = Object.freeze({ NONE: '', JUMP: 'jump', SLIDE: 'slide' });

/**
 * 閃避 gesture: a quick upward swipe that starts on the button always jumps; pressed while standing still -> jump at
 * once; pressed while moving -> slide on release (a tap) or once `decideMs` passed without an upward swipe.
 * @param {boolean} moving stick deflected past the dodge threshold when the button went down
 * @param {number} dx px travelled to the right since the press
 * @param {number} dy px travelled down since the press (screen y; an upward swipe is negative)
 * @param {number} elapsedMs since the press
 * @param {boolean} released the finger lifted
 * @returns {string} DODGE value
 */
export function dodgeGesture(moving, dx, dy, elapsedMs, released, swipePx = 16, decideMs = 110) {
  if (-dy >= swipePx && -dy >= Math.abs(dx)) return DODGE.JUMP;
  if (!moving) return DODGE.JUMP;
  if (released || elapsedMs >= decideMs) return DODGE.SLIDE;
  return DODGE.NONE;
}

/**
 * What a resolved 閃避 actually sends: a slide only when the player state machine would start one - locomotion
 * state 'grounded' (at least `minSlideSpeed` m/s) or 'sprinting', with the motor able to slide (grounded, off the
 * slide cooldown: motor.canSlide). Anything else jumps: a throw wind-up turns it into a jump throw, and a slow walk
 * or a slide cooldown never leaves the button dead.
 * @param {boolean} wantSlide the gesture asked for a slide (dodgeGesture -> SLIDE)
 * @param {string} fsmState player.fsm.current
 * @returns {string} DODGE.SLIDE or DODGE.JUMP
 */
export function dodgeAction(wantSlide, fsmState, canSlide, planarSpeed, minSlideSpeed) {
  if (!wantSlide || !canSlide) return DODGE.JUMP;
  if (fsmState === 'sprinting') return DODGE.SLIDE;
  return fsmState === 'grounded' && planarSpeed >= minSlideSpeed ? DODGE.SLIDE : DODGE.JUMP;
}

/** A short tap (not a drag): released within `maxMs` and never farther than `maxPx` from where it went down. */
export function isTap(durationMs, travelPx, maxMs = 220, maxPx = 12) {
  return durationMs >= 0 && durationMs <= maxMs && travelPx <= maxPx;
}

/**
 * Planar closest approach: does a ball at (bx, bz) moving with (vx, vz) m/s come within `radius` m of (px, pz) in
 * the next `horizon` seconds? A ball that is moving away (closest approach already behind it) does not count.
 */
export function ballApproaches(px, pz, bx, bz, vx, vz, radius, horizon) {
  const dx = px - bx, dz = pz - bz;
  const v2 = vx * vx + vz * vz;
  if (!(v2 > 1e-6)) return false;
  const t = (dx * vx + dz * vz) / v2;
  if (!(t > 0) || t > horizon) return false;
  const cx = dx - vx * t, cz = dz - vz * t;
  return cx * cx + cz * cz <= radius * radius;
}
