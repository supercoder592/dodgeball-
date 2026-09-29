// ---------------------------------------------------------------------------------------------------------------
// Input system (ORDER.INPUT - runs first every frame) + HumanController (the local player's intent source).
//
// Devices
//   * Keyboard (KeyboardEvent.code, layout independent) + mouse with Pointer Lock (click the canvas to lock, Esc
//     unlocks -> opens the pause menu).
//   * Gamepad (W3C standard mapping, polled every frame, analogue triggers with hysteresis).
//   * Touch (input/touch.js): floating stick, drag-to-look (tap = cycle target), five thumb buttons (context primary
//     投球 / 撿球 / 接球, 閃避 jump-or-slide, 傳球 with the ball, 技能, 大絕) - shown on coarse-pointer devices.
//     lateUpdate() feeds the primary button's mode every frame; touch players also get automatic pickup within the
//     manual reach (touchPickupReady -> intent.pickup, same rules as pressing E).
//
// Every device feeds the same logical actions (ActionState: held + per-frame pressed/released edges). Look deltas go
// straight to game.cameraRig.addLook(dxDeg, dyDeg) once per frame on REAL time (camera/UI rule), so aiming keeps
// working during hitstop. Screen convention for addLook: +dx = turn right, +dy = look down (mouse moved down);
// the "invert Y" setting flips dy.
//
// Menus (hero select, pause, results) push themselves with pushMenu(handler); while a menu is on the stack the
// keyboard / gamepad produce menu actions ('up','down','left','right','confirm','back','team','watch','prev','next',
// 'diff1'..'diff4','start','select') delivered to handler.onMenuAction(action, source, event) and gameplay intents are
// neutral. Anything held when a menu opens or closes is suppressed until released (no leaked throws/jumps).
// ---------------------------------------------------------------------------------------------------------------
import * as THREE from 'three';
import { game } from '../game.js';
import { TEAM } from '../core/constants.js';
import {
  ActionState, SRC, PAD, PAD_BUTTON_COUNT, radialDeadzone, axisCurve, clampMagnitude2, triggerHysteresis, RepeatNav, keyAxes,
  touchPrimaryMode, ballApproaches, dodgeAction, DODGE,
} from './inputMath.js';
import { Settings } from './settings.js';
import { TouchControls } from './touch.js';
import { Pause } from '../ui/pause.js';

/** Logical actions produced by every device. */
export const ACTIONS = Object.freeze(['sprint', 'jump', 'slide', 'throw', 'catch', 'pass', 'pickup', 'skill', 'ultimate', 'cycleTarget', 'pause']);

/** Tuning (units in comments). */
export const INPUT_TUNING = Object.freeze({
  padMoveDeadzone: 0.16,        // radial deadzone of the left stick
  padLookDeadzone: 0.12,        // per-axis deadzone of the right stick
  padLookCurve: 1.8,            // exponential response: fine aim near the centre, fast turns at the rim
  padPitchRatio: 0.68,          // vertical look speed relative to horizontal
  padSprintReleaseTime: 0.3,    // s of a centred left stick that cancels the L3 sprint toggle
  padMenuStick: 0.55,           // stick deflection that counts as a menu direction
  mouseDeltaClamp: 260,         // px per event (drops the spikes some browsers emit when the lock engages)
  pauseDebounceMs: 280,         // ignore a second pause toggle this soon (Esc also exits pointer lock)
  touchMouseGuardMs: 800,       // ignore emulated mouse events right after a touch
  lockFailuresBeforeFallback: 3,
  aimMinDistance: 1.5,          // m: closer aim points fall back to the camera ray direction
  aimFallbackDistance: 30,      // m along the aim ray when the rig exposes no aimPoint
  touchThreatRadius: 2.5,       // m: an enemy live ball passing this close ...
  touchThreatHorizon: 1.5,      // s: ... within this time keeps the touch primary button on 接球 (catch)
});

// ------------------------------------------------------------------ bindings
const KEY_ACTIONS = Object.freeze({
  ShiftLeft: 'sprint', ShiftRight: 'sprint', Space: 'jump', KeyC: 'slide', ControlLeft: 'slide', ControlRight: 'slide',
  KeyQ: 'pass', KeyE: 'pickup', KeyF: 'skill', KeyR: 'ultimate', Tab: 'cycleTarget',
});
const MOVE_KEYS = Object.freeze({ fwd: ['KeyW', 'ArrowUp'], back: ['KeyS', 'ArrowDown'], left: ['KeyA', 'ArrowLeft'], right: ['KeyD', 'ArrowRight'] });
const GAME_KEYS = new Set([...Object.keys(KEY_ACTIONS), 'KeyW', 'KeyA', 'KeyS', 'KeyD', 'ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight']);
/** Mouse button index -> action (0 left, 1 middle, 2 right). */
const MOUSE_ACTIONS = Object.freeze(['throw', 'cycleTarget', 'catch']);
/** Gamepad buttons per action. L3 is a sprint *toggle* handled separately; START pauses. */
const PAD_BINDINGS = Object.freeze({
  jump: [PAD.A], slide: [PAD.B], pickup: [PAD.X], pass: [PAD.Y], skill: [PAD.LB], ultimate: [PAD.RB],
  catch: [PAD.LT], throw: [PAD.RT], cycleTarget: [PAD.R3, PAD.RIGHT],
});
const MENU_KEYS = Object.freeze({
  ArrowUp: 'up', KeyW: 'up', ArrowDown: 'down', KeyS: 'down', ArrowLeft: 'left', KeyA: 'left', ArrowRight: 'right', KeyD: 'right',
  Enter: 'confirm', NumpadEnter: 'confirm', Space: 'confirm', Escape: 'back', Backspace: 'back',
  KeyT: 'team', KeyV: 'watch', KeyQ: 'prev', KeyE: 'next', PageUp: 'prev', PageDown: 'next',
  Digit1: 'diff1', Digit2: 'diff2', Digit3: 'diff3', Digit4: 'diff4', KeyP: 'start',
});
const MENU_REPEATABLE = new Set(['up', 'down', 'left', 'right']);
const MENU_PAD = Object.freeze({
  [PAD.A]: 'confirm', [PAD.B]: 'back', [PAD.X]: 'team', [PAD.Y]: 'watch', [PAD.LB]: 'prev', [PAD.RB]: 'next',
  [PAD.START]: 'start', [PAD.SELECT]: 'select',
});

/** Prompt glyphs per device, used by the HUD / menus. */
export const KEY_HINTS = Object.freeze({
  kbm: Object.freeze({ throw: 'LMB', catch: 'RMB', pass: 'Q', pickup: 'E', skill: 'F', ultimate: 'R', jump: 'Space', slide: 'C', sprint: 'Shift', cycleTarget: 'Tab', pause: 'Esc', confirm: 'Enter' }),
  gamepad: Object.freeze({ throw: 'RT', catch: 'LT', pass: 'Y', pickup: 'X', skill: 'LB', ultimate: 'RB', jump: 'A', slide: 'B', sprint: 'L3', cycleTarget: 'R3', pause: 'START', confirm: 'A' }),
  touch: Object.freeze({ throw: '', catch: '', pass: '', pickup: '', skill: '', ultimate: '', jump: '', slide: '', sprint: '', cycleTarget: '', pause: '', confirm: '' }),
});

const PHASES_OUT_OF_MATCH = new Set(['idle', 'select']);

export class Input {
  constructor() {
    this.settings = Settings;
    /** @type {Record<string, ActionState>} */
    this.actions = {};
    for (const a of ACTIONS) this.actions[a] = new ActionState();
    /** Physical key codes currently held (gameplay context only). */
    this.keys = new Set();
    this._mouseHeld = [false, false, false];
    this._mouseSwallow = [false, false, false];   // the lock-acquiring click / menu clicks never become actions
    this._pad = {
      connected: false, id: '',
      now: new Uint8Array(PAD_BUTTON_COUNT), prev: new Uint8Array(PAD_BUTTON_COUNT), suppressed: new Uint8Array(PAD_BUTTON_COUNT),
      lx: 0, ly: 0, rx: 0, ry: 0,
    };
    this._padSprint = false;
    this._padIdle = 0;
    /** Planar move this frame: x = right, y = forward, |move| <= 1 (device space, camera-relative mapping happens in HumanController). */
    this.move = { x: 0, y: 0 };
    this.moveMagnitude = 0;
    /** Degrees sent to the camera this frame (after sensitivity/invert). */
    this.look = { x: 0, y: 0 };
    this._pendingMouseX = 0;
    this._pendingMouseY = 0;
    this.pointerLocked = false;
    this.lockUnavailable = false;
    this._lockFailures = 0;
    this._lastLockErrorMs = -1e9;
    this._expectUnlock = false;
    /** 'kbm' | 'gamepad' | 'touch' - drives button prompts. */
    this.lastDevice = 'kbm';
    this.touchMode = false;
    /** @type {TouchControls|null} */
    this.touch = null;
    this._lastTouchMs = -1e9;
    /** True during the frame a pause toggle was requested (any device). */
    this.pausePressed = false;
    this._pauseRequested = false;
    this._lastPauseMs = -1e9;
    this._menus = [];
    this._nav = { up: new RepeatNav(), down: new RepeatNav(), left: new RepeatNav(), right: new RepeatNav() };
    this._off = [];
    this._tmpA = { x: 0, y: 0 };
    this._tmpB = { x: 0, y: 0 };
    this._bodyTouchClass = null;
    this.container = null;
    /**
     * Touch players pick up any ball within the manual reach automatically (touch-only assist, like aim assist; only
     * while touch is the device in use - touchActive).
     */
    this.touchAutoPickup = true;
  }

  // ------------------------------------------------------------------ lifecycle
  async init() {
    this.container = document.getElementById('app') || document.body;
    const forced = game.config && game.config.params && game.config.params.get && game.config.params.get('touch');
    const setting = Settings.values.touchControls;
    const coarse = typeof matchMedia === 'function' && matchMedia('(pointer: coarse)').matches;
    const touchCapable = (navigator.maxTouchPoints || 0) > 0 || 'ontouchstart' in window;
    this.touchMode = forced === '1' || setting === 'on' || (setting === 'auto' && coarse && touchCapable);
    if (this.touchMode) this.lastDevice = 'touch';
    if (touchCapable || this.touchMode) this._ensureTouch();

    const on = (target, type, fn, opts) => { target.addEventListener(type, fn, opts); this._off.push(() => target.removeEventListener(type, fn, opts)); };
    on(window, 'keydown', (e) => this._onKeyDown(e));
    on(window, 'keyup', (e) => this._onKeyUp(e));
    on(this.container, 'mousedown', (e) => this._onMouseDown(e));
    on(window, 'mouseup', (e) => this._onMouseUp(e));
    on(document, 'mousemove', (e) => this._onMouseMove(e));
    on(this.container, 'contextmenu', (e) => e.preventDefault());
    on(window, 'pointerdown', (e) => this._onAnyPointerDown(e), { capture: true, passive: true });
    on(document, 'pointerlockchange', () => this._onLockChange());
    on(document, 'pointerlockerror', () => this._onLockError());
    on(window, 'blur', () => this._releaseEverything());
    on(document, 'visibilitychange', () => {
      if (!document.hidden) return;
      this._releaseEverything();
      if (this.inMatch && game.localPlayer && !this._menus.length) this.requestPause('hidden');
    });
    on(window, 'beforeunload', (e) => {
      // Guards against Ctrl+W while Ctrl is held for sliding (the browser shows its "leave site?" prompt).
      if (this.inMatch && game.localPlayer && !game.time.paused) { e.preventDefault(); e.returnValue = ''; }
    });
    on(window, 'gamepadconnected', (e) => {
      this.lastDevice = 'gamepad';
      game.hud?.toast?.(`CONTROLLER CONNECTED · 已連接手把`, '#ffffff', 2.2);
      if (e && e.gamepad) this._pad.id = e.gamepad.id;
    });
    on(window, 'gamepaddisconnected', () => {
      game.hud?.toast?.('CONTROLLER DISCONNECTED · 手把已中斷', '#ff8080', 2.2);
      if (this.inMatch && game.localPlayer && !this._menus.length && this.lastDevice === 'gamepad') this.requestPause('gamepad');
    });
    const unsub = Settings.onChange((key, value) => {
      if (key !== 'touchControls') return;
      if (value === 'on') { this.touchMode = true; this._ensureTouch(); }
      else if (value === 'off') this.touchMode = false;
    });
    this._off.push(unsub);
  }

  dispose() {
    for (const off of this._off) off();
    this._off = [];
    this.releaseLock();
    if (this.touch) this.touch.dispose();
    this.touch = null;
    this._menus = [];
  }

  // ------------------------------------------------------------------ public state
  /** A match is loaded (any phase from preRound to matchEnd). */
  get inMatch() {
    const ph = game.match && game.match.phase;
    return !!ph && !PHASES_OUT_OF_MATCH.has(ph);
  }
  /** Gameplay input is ignored (menu on screen or game paused). */
  get blocked() { return this._menus.length > 0 || !!game.time.paused; }
  get menuOpen() { return this._menus.length > 0; }
  get gamepadConnected() { return this._pad.connected; }

  /** Prompt text for an action on the device used last. */
  hint(action) {
    const table = KEY_HINTS[this.lastDevice] || KEY_HINTS.kbm;
    return table[action] || '';
  }

  /** Menus: push/pop a handler { onMenuAction(action, source, event) -> boolean }. */
  pushMenu(handler) {
    const i = this._menus.indexOf(handler);
    if (i >= 0) this._menus.splice(i, 1);
    this._menus.push(handler);
    this.suppressHeld();
    if (this.touch) this.touch.setVisible(false);
  }
  popMenu(handler) {
    const i = this._menus.indexOf(handler);
    if (i < 0) return;
    this._menus.splice(i, 1);
    this.suppressHeld();
    for (const k in this._nav) this._nav[k].reset();
  }

  /**
   * Forgets everything currently held without generating edges: keys/buttons must be released and pressed again
   * to count. Used when menus open/close so the key that closed a menu does not also jump/throw.
   */
  suppressHeld() {
    this.keys.clear();
    for (let b = 0; b < 3; b++) if (this._mouseHeld[b]) { this._mouseHeld[b] = false; this._mouseSwallow[b] = true; }
    const pad = this._pad;
    for (let i = 0; i < PAD_BUTTON_COUNT; i++) if (pad.now[i]) pad.suppressed[i] = 1;
    this._padSprint = false;
    for (const a of ACTIONS) this.actions[a].clear();
    if (this.touch) this.touch.releaseAll();
    this._pendingMouseX = 0; this._pendingMouseY = 0;
  }

  /** Requests pointer lock on the game canvas (desktop). Needs a user gesture; failures are harmless. */
  requestLock() {
    if (this.pointerLocked || this.touchMode) return;
    const el = this._lockTarget();
    if (!el || typeof el.requestPointerLock !== 'function') { this.lockUnavailable = true; return; }
    try {
      const r = el.requestPointerLock();
      if (r && typeof r.catch === 'function') r.catch(() => this._onLockError());
    } catch { this._onLockError(); }
  }

  releaseLock() {
    if (!document.pointerLockElement) return;
    this._expectUnlock = true;
    try { document.exitPointerLock(); } catch { this._expectUnlock = false; }
  }

  /** Toggles the pause menu (keyboard Esc/P, gamepad START, touch pause button, lost pointer lock, hidden tab). */
  requestPause(source = 'keyboard') {
    const nowMs = performance.now();
    if (nowMs - this._lastPauseMs < INPUT_TUNING.pauseDebounceMs) return;
    this._lastPauseMs = nowMs;
    this._pauseRequested = true;
    if (!this.inMatch) return;
    if (Pause.isOpen) Pause.close(); else Pause.open(source);
  }

  /** Touch buttons -> actions (called by TouchControls). */
  setTouchAction(action, on) {
    const a = this.actions[action];
    if (!a) return;
    this.lastDevice = 'touch';
    if (this._menus.length && on) return;
    a.setSource(SRC.TOUCH, on);
  }

  /**
   * 閃避 (touch dodge) resolved by TouchControls: a slide when asked for AND the state machine would start one
   * (dodgeAction: grounded fast enough or sprinting, off the slide cooldown), otherwise a jump - so a wind-up gets
   * its jump throw and the button is never a dead tap. One-frame press.
   */
  touchDodge(slide) {
    const p = game.localPlayer, m = p && p.motor, fsm = p && p.fsm;
    const walk = (m && m.profile && m.profile.walkSpeed) || 4.6;
    const frac = (fsm && fsm.tuning && fsm.tuning.slideMinSpeedFraction) || 0.6;
    const out = dodgeAction(!!slide && !!m && !!fsm, fsm ? fsm.current : '', !!(m && m.canSlide), (m && m.planarSpeed) || 0, walk * frac);
    const action = out === DODGE.SLIDE ? 'slide' : 'jump';
    this.setTouchAction(action, true);
    this.setTouchAction(action, false);
  }

  /**
   * A 投球 press ended without a pointerup (pointercancel: a system edge swipe, lost capture): drop the wind-up and
   * keep the ball. Called before the touch 'throw' action is released, so ChargingThrow leaves on !isCharging
   * instead of treating the release as a throw.
   */
  cancelTouchThrow() {
    const c = game.localPlayer && game.localPlayer.combat;
    if (c && c.isCharging && typeof c.cancelCharge === 'function') c.cancelCharge();
  }

  /** The touch layer is the device in use (shown, and the last input came from a finger - not a pad / keyboard). */
  get touchActive() { return !!this.touch && this.touch.visible && this.lastDevice === 'touch'; }

  // ------------------------------------------------------------------ per frame
  update(dt, realDt) {
    this._pollGamepad(realDt);

    // Touch layer visibility + body class for the touch HUD layout.
    const touchOn = this.touchMode && Settings.values.touchControls !== 'off';
    if (this._bodyTouchClass !== touchOn) { this._bodyTouchClass = touchOn; document.body.classList.toggle('du-touch', touchOn); }
    if (this.touch) {
      this.touch.setVisible(touchOn && this.inMatch && !this._menus.length && !game.time.paused && !!game.localPlayer);
      this.actions.sprint.setSource(SRC.TOUCH, this.touch.visible && this.touch.sprint);
      // Pending 閃避 -> slide, hint fade (hint time only counts once the round is actually playing). Before the latch.
      this.touch.update(realDt, !!game.match && game.match.phase === 'playing');
    }

    for (const a of ACTIONS) this.actions[a].latch();
    this.pausePressed = this._pauseRequested;
    this._pauseRequested = false;

    this._updateMove();
    this._updateLook(realDt);
  }

  /**
   * After gameplay (first lateUpdate): the touch primary button's mode for this frame's state - 投球 with the ball,
   * 撿球 with a pickable ball in manual reach and nothing incoming, else 接球 - and 傳球 only while holding.
   */
  lateUpdate() {
    const t = this.touch;
    if (!t || !t.visible) return;
    const p = localPlayerOf(game.players);
    const c = p && p.combat;
    const hasBall = !!(c && c.hasBall);
    const inReach = !hasBall && !!c && typeof c.nearestPickable === 'function' && c.nearestPickable() !== null;
    t.setContext(touchPrimaryMode(hasBall, inReach, inReach && enemyBallIncoming(p)), hasBall);
  }

  _updateMove() {
    const m = this.move;
    m.x = 0; m.y = 0;
    if (!this.blocked) {
      const k = this.keys;
      const any = (codes) => k.has(codes[0]) || k.has(codes[1]);
      keyAxes(any(MOVE_KEYS.fwd), any(MOVE_KEYS.back), any(MOVE_KEYS.left), any(MOVE_KEYS.right), this._tmpA);
      m.x += this._tmpA.x; m.y += this._tmpA.y;
      const pad = this._pad;
      if (pad.connected) {
        radialDeadzone(pad.lx, -pad.ly, INPUT_TUNING.padMoveDeadzone, this._tmpB, 1);
        m.x += this._tmpB.x; m.y += this._tmpB.y;
      }
      if (this.touch && this.touch.visible) { m.x += this.touch.move.x; m.y += this.touch.move.y; }
      clampMagnitude2(m, 1);
    }
    this.moveMagnitude = Math.hypot(m.x, m.y);
  }

  _updateLook(realDt) {
    let dx = 0, dy = 0;
    const s = Settings.values;
    if (!this.blocked) {
      dx += this._pendingMouseX * s.mouseSensitivity;
      dy += this._pendingMouseY * s.mouseSensitivity;
      const pad = this._pad;
      if (pad.connected) {
        const rx = axisCurve(pad.rx, INPUT_TUNING.padLookDeadzone, INPUT_TUNING.padLookCurve);
        const ry = axisCurve(pad.ry, INPUT_TUNING.padLookDeadzone, INPUT_TUNING.padLookCurve);
        dx += rx * s.gamepadLookSpeed * realDt;
        dy += ry * s.gamepadLookSpeed * INPUT_TUNING.padPitchRatio * realDt;
      }
      if (this.touch && this.touch.visible) {
        this.touch.consumeLook(this._tmpA);
        dx += this._tmpA.x * s.touchSensitivity;
        dy += this._tmpA.y * s.touchSensitivity;
      }
      if (s.invertY) dy = -dy;
    } else if (this.touch) {
      this.touch.consumeLook(this._tmpA); // drop drags made while blocked
    }
    this._pendingMouseX = 0; this._pendingMouseY = 0;
    this.look.x = dx; this.look.y = dy;
    const rig = game.cameraRig;
    if ((dx !== 0 || dy !== 0) && rig && typeof rig.addLook === 'function') rig.addLook(dx, dy);
  }

  _pollGamepad(realDt) {
    const pad = this._pad;
    pad.prev.set(pad.now);
    let gp = null;
    let list = null;
    try { list = navigator.getGamepads ? navigator.getGamepads() : null; } catch { list = null; }
    if (list) {
      for (let i = 0; i < list.length; i++) {
        const p = list[i];
        if (!p || !p.connected) continue;
        if (!gp || (p.mapping === 'standard' && gp.mapping !== 'standard')) gp = p;
      }
    }
    if (!gp) {
      pad.now.fill(0);
      pad.lx = pad.ly = pad.rx = pad.ry = 0;
      pad.connected = false;
    } else {
      pad.connected = true;
      let activity = false;
      for (let i = 0; i < PAD_BUTTON_COUNT; i++) {
        const b = gp.buttons[i];
        let v = 0, pressed = false;
        if (b !== undefined && b !== null) {
          if (typeof b === 'number') { v = b; pressed = b > 0.5; } else { v = b.value || (b.pressed ? 1 : 0); pressed = !!b.pressed; }
        }
        const down = (i === PAD.LT || i === PAD.RT) ? triggerHysteresis(v, pad.prev[i] === 1) : (pressed || v > 0.5);
        pad.now[i] = down ? 1 : 0;
        if (down && !pad.prev[i]) activity = true;
      }
      const ax = gp.axes;
      pad.lx = ax[0] || 0; pad.ly = ax[1] || 0; pad.rx = ax[2] || 0; pad.ry = ax[3] || 0;
      if (activity || Math.abs(pad.lx) > 0.4 || Math.abs(pad.ly) > 0.4 || Math.abs(pad.rx) > 0.4 || Math.abs(pad.ry) > 0.4) this.lastDevice = 'gamepad';
    }
    for (let i = 0; i < PAD_BUTTON_COUNT; i++) if (pad.suppressed[i] && !pad.now[i]) pad.suppressed[i] = 0;
    const edge = (i) => pad.now[i] === 1 && pad.prev[i] === 0 && pad.suppressed[i] === 0;

    if (this._menus.length) {
      // Menu navigation: face buttons + d-pad / left stick with auto-repeat. No gameplay actions.
      for (let i = 0; i < PAD_BUTTON_COUNT; i++) {
        const act = MENU_PAD[i];
        if (act && edge(i)) { pad.suppressed[i] = 1; this._dispatchMenu(act, 'gamepad', null); }
      }
      const t = INPUT_TUNING.padMenuStick;
      const nav = this._nav;
      if (nav.up.update(pad.connected && (pad.now[PAD.UP] === 1 || pad.ly < -t), realDt)) this._dispatchMenu('up', 'gamepad', null);
      if (nav.down.update(pad.connected && (pad.now[PAD.DOWN] === 1 || pad.ly > t), realDt)) this._dispatchMenu('down', 'gamepad', null);
      if (nav.left.update(pad.connected && (pad.now[PAD.LEFT] === 1 || pad.lx < -t), realDt)) this._dispatchMenu('left', 'gamepad', null);
      if (nav.right.update(pad.connected && (pad.now[PAD.RIGHT] === 1 || pad.lx > t), realDt)) this._dispatchMenu('right', 'gamepad', null);
      for (const action in PAD_BINDINGS) this.actions[action].setSource(SRC.PAD, false);
      this.actions.sprint.setSource(SRC.PAD, false);
      return;
    }
    for (const k in this._nav) this._nav[k].reset();

    if (edge(PAD.START)) { pad.suppressed[PAD.START] = 1; this.requestPause('gamepad'); }
    if (this._menus.length) return; // the pause menu just opened: its suppression already cleared the actions

    // L3 toggles sprint; a centred stick for a moment cancels it.
    if (edge(PAD.L3)) this._padSprint = !this._padSprint;
    if (Math.hypot(pad.lx, pad.ly) < 0.25) {
      this._padIdle += realDt;
      if (this._padIdle > INPUT_TUNING.padSprintReleaseTime) this._padSprint = false;
    } else this._padIdle = 0;
    this.actions.sprint.setSource(SRC.PAD, this._padSprint);

    for (const action in PAD_BINDINGS) {
      const buttons = PAD_BINDINGS[action];
      let held = false;
      for (let j = 0; j < buttons.length; j++) { const b = buttons[j]; if (pad.now[b] && !pad.suppressed[b]) { held = true; break; } }
      this.actions[action].setSource(SRC.PAD, held);
    }
  }

  // ------------------------------------------------------------------ DOM events
  _onKeyDown(e) {
    const code = e.code;
    if (this._menus.length) {
      const act = MENU_KEYS[code];
      if (!act) return;
      // Let a focused <button> in a menu activate natively (Tab + Enter), except inside text-free nav targets.
      const ae = document.activeElement;
      if ((code === 'Enter' || code === 'NumpadEnter' || code === 'Space') && ae && ae.tagName === 'BUTTON' && ae.closest && ae.closest('.du-menu') && !ae.classList.contains('du-nav-item')) return;
      if (e.repeat && !MENU_REPEATABLE.has(act)) { e.preventDefault(); return; }
      this.lastDevice = 'kbm';
      const handled = this._dispatchMenu(act, 'keyboard', e);
      if (handled !== false || code === 'Space' || code === 'Tab') e.preventDefault();
      return;
    }
    if (code === 'Escape' || code === 'KeyP') {
      if (this.inMatch) { e.preventDefault(); if (!e.repeat) this.requestPause('keyboard'); }
      return;
    }
    if (this.inMatch && GAME_KEYS.has(code)) e.preventDefault();
    if (e.repeat || e.metaKey) return;
    this.lastDevice = 'kbm';
    if (this.keys.has(code)) return;
    this.keys.add(code);
    const a = KEY_ACTIONS[code];
    if (a) this.actions[a].keyDown();
  }

  _onKeyUp(e) {
    const code = e.code;
    if (this.keys.delete(code)) {
      const a = KEY_ACTIONS[code];
      if (a) this.actions[a].keyUp();
    }
  }

  _onAnyPointerDown(e) {
    if (e.pointerType === 'touch' || e.pointerType === 'pen') {
      this._lastTouchMs = performance.now();
      this.lastDevice = 'touch';
      if (!this.touchMode && Settings.values.touchControls !== 'off') { this.touchMode = true; this._ensureTouch(); this.releaseLock(); }
    } else if (e.pointerType === 'mouse') {
      if (this.touchMode && Settings.values.touchControls === 'auto' && this.container && this.container.contains(e.target)) this.touchMode = false;
    }
  }

  _onMouseDown(e) {
    if (performance.now() - this._lastTouchMs < INPUT_TUNING.touchMouseGuardMs) return;
    if (this._menus.length || !this.inMatch) return;
    const b = e.button;
    if (b < 0 || b > 2) return;
    this.lastDevice = 'kbm';
    e.preventDefault();
    if (!this.pointerLocked && !this.lockUnavailable && game.localPlayer && !this.touchMode) {
      // The click that grabs the mouse does not throw.
      this.requestLock();
      this._mouseSwallow[b] = true;
      return;
    }
    this._setMouse(b, true);
  }

  _onMouseUp(e) {
    const b = e.button;
    if (b < 0 || b > 2) return;
    if (this._mouseSwallow[b]) { this._mouseSwallow[b] = false; return; }
    this._setMouse(b, false);
  }

  _setMouse(b, on) {
    if (this._mouseHeld[b] === on) return;
    this._mouseHeld[b] = on;
    this.actions[MOUSE_ACTIONS[b]].setSource(SRC.MOUSE, on);
  }

  _onMouseMove(e) {
    if (this._menus.length) return;
    const c = INPUT_TUNING.mouseDeltaClamp;
    const useful = this.pointerLocked || (this.lockUnavailable && this.inMatch && this.container && this.container.contains(e.target));
    if (!useful) return;
    const mx = Math.max(-c, Math.min(c, e.movementX || 0));
    const my = Math.max(-c, Math.min(c, e.movementY || 0));
    this._pendingMouseX += mx;
    this._pendingMouseY += my;
    if (mx || my) this.lastDevice = 'kbm';
  }

  _onLockChange() {
    const locked = !!document.pointerLockElement;
    const was = this.pointerLocked;
    this.pointerLocked = locked;
    if (locked) { this._lockFailures = 0; this.lastDevice = 'kbm'; return; }
    if (!was) return;
    const expected = this._expectUnlock;
    this._expectUnlock = false;
    for (let b = 0; b < 3; b++) this._setMouse(b, false);
    // Esc exits pointer lock without delivering the key to the page: treat an unexpected unlock as "pause".
    if (!expected && this.inMatch && game.localPlayer && !this._menus.length) this.requestPause('pointerlock');
  }

  _onLockError() {
    const nowMs = performance.now();
    if (nowMs - this._lastLockErrorMs < 150) return; // promise rejection + pointerlockerror for the same request
    this._lastLockErrorMs = nowMs;
    if (++this._lockFailures >= INPUT_TUNING.lockFailuresBeforeFallback) this.lockUnavailable = true;
  }

  _lockTarget() {
    const r = game.renderer && game.renderer.three;
    return (r && r.domElement) || (this.container && this.container.querySelector('canvas')) || this.container;
  }

  _releaseEverything() {
    for (const code of this.keys) { const a = KEY_ACTIONS[code]; if (a) this.actions[a].keyUp(); }
    this.keys.clear();
    for (let b = 0; b < 3; b++) this._setMouse(b, false);
    if (this.touch) this.touch.releaseAll();
    this._pendingMouseX = 0; this._pendingMouseY = 0;
  }

  _dispatchMenu(action, source, ev) {
    const m = this._menus[this._menus.length - 1];
    if (!m || typeof m.onMenuAction !== 'function') return false;
    if (source === 'gamepad') this.lastDevice = 'gamepad';
    try { return m.onMenuAction(action, source, ev); } catch (err) { console.error('[input] menu handler threw', err); return false; }
  }

  _ensureTouch() {
    if (this.touch) return;
    const ui = document.getElementById('ui');
    if (!ui) return;
    this.touch = new TouchControls(this);
    this.touch.build(ui);
  }
}

// ------------------------------------------------------------------ touch assist helpers (allocation-free)
/** game.localPlayer without the per-call closure (runs every frame). */
function localPlayerOf(list) {
  if (!list) return null;
  for (let i = 0; i < list.length; i++) if (list[i] && list[i].isLocal) return list[i];
  return null;
}

/** Is an enemy live ball (throw or pass) coming at `player` - close enough and soon enough to catch? */
export function enemyBallIncoming(player) {
  const list = game.balls && game.balls.active;
  if (!player || !player.position || !list) return false;
  const pos = player.position;
  for (let i = 0; i < list.length; i++) {
    const b = list[i];
    if (b.state !== 'live') continue;
    const t = b.lastThrower;
    if (t === player || (t && !game.areEnemies(t, player))) continue;
    if (ballApproaches(pos.x, pos.z, b.position.x, b.position.z, b.velocity.x, b.velocity.z,
      INPUT_TUNING.touchThreatRadius, INPUT_TUNING.touchThreatHorizon)) return true;
  }
  return false;
}

/**
 * Touch pickup (primary button 撿球 + the automatic touch pickup): empty hands, a ball this player may take
 * (Ball.canBePickedUpBy - zone, reservation and pickup-lock rules) within the manual reach, and no enemy ball on
 * its way that the hands should stay free for.
 */
export function touchPickupReady(player) {
  const c = player && player.combat;
  if (!c || c.hasBall || player.canAct === false || typeof c.nearestPickable !== 'function') return false;
  return c.nearestPickable() !== null && !enemyBallIncoming(player);
}

// =================================================================================================================
// HumanController - the local player's intent source (same intent shape as the AI Bot).
// =================================================================================================================
const _fwd = new THREE.Vector3();
const _right = new THREE.Vector3();
const _origin = new THREE.Vector3();
const _delta = new THREE.Vector3();
const _UP = new THREE.Vector3(0, 1, 0);

export class HumanController {
  /** @param {Input|null} input defaults to game.input (resolved lazily so construction order does not matter) */
  constructor(input = null) {
    this.input = input;
    this.isHuman = true;
    /** Reused every frame (no allocations); consumers must not keep references across frames. */
    this.intent = {
      move: new THREE.Vector3(), aimDir: new THREE.Vector3(0, 0, 1), aimPoint: new THREE.Vector3(), target: null,
      sprint: false, jump: false, slide: false, throwPressed: false, throwHeld: false, throwReleased: false,
      catchPressed: false, pass: false, pickup: false, skill: false, ultimate: false, cycleTarget: false, pausePressed: false,
    };
    this._prevThrowHeld = false;
  }

  /**
   * Builds this frame's intent for `player`: camera-relative planar move, aim from the camera rig, edge-triggered
   * action flags. Returns neutral actions while a menu is open or the game is paused.
   */
  sample(player, dt) {
    const input = this.input || game.input;
    const it = this.intent;
    const rig = game.cameraRig;
    this._aim(player, rig, it);
    it.target = (player && player.combat && player.combat.currentTarget) || null;
    it.pausePressed = !!(input && input.pausePressed);

    if (!input || input.blocked) {
      it.move.set(0, 0, 0);
      it.sprint = it.jump = it.slide = it.throwPressed = it.throwHeld = it.throwReleased = false;
      it.catchPressed = it.pass = it.pickup = it.skill = it.ultimate = it.cycleTarget = false;
      this._prevThrowHeld = false; // keep any charge; the next click release throws it
      return it;
    }

    // Camera-relative planar movement.
    this._basis(player, rig);
    const mv = input.move;
    it.move.set(0, 0, 0).addScaledVector(_fwd, mv.y).addScaledVector(_right, mv.x);
    it.move.y = 0;
    const len = it.move.length();
    if (len > 1) it.move.multiplyScalar(1 / len);

    const A = input.actions;
    it.sprint = A.sprint.held;
    it.jump = A.jump.pressed;
    it.slide = A.slide.pressed;
    it.throwPressed = A.throw.pressed;
    it.throwHeld = A.throw.held;
    // A release always follows a hold even when the device release happened while a menu swallowed it.
    it.throwReleased = A.throw.released || (this._prevThrowHeld && !A.throw.held);
    this._prevThrowHeld = A.throw.held;
    it.catchPressed = A.catch.pressed;
    it.pass = A.pass.pressed;
    // Touch assist: walking onto a ball within the manual reach picks it up (the same path as pressing E). Only while
    // touch is the device in use - a keyboard / pad player with the touch overlay on screen keeps the normal rules.
    it.pickup = A.pickup.pressed || (input.touchAutoPickup && input.touchActive && touchPickupReady(player));
    it.skill = A.skill.pressed;
    it.ultimate = A.ultimate.pressed;
    it.cycleTarget = A.cycleTarget.pressed;
    return it;
  }

  /** Planar forward/right of the camera (fallback: the team's attack direction). */
  _basis(player, rig) {
    if (rig && rig.planarForward) _fwd.copy(rig.planarForward);
    else _fwd.set(0, 0, player && player.team === TEAM.AWAY ? -1 : 1);
    _fwd.y = 0;
    if (_fwd.lengthSq() < 1e-8) _fwd.set(0, 0, 1);
    _fwd.normalize();
    if (rig && rig.planarRight) { _right.copy(rig.planarRight); _right.y = 0; if (_right.lengthSq() < 1e-8) _right.crossVectors(_fwd, _UP); }
    else _right.crossVectors(_fwd, _UP); // facing +Z, right is -X (right-handed, Y up)
    _right.normalize();
  }

  /** aimPoint from the rig; aimDir from the player's chest toward it (camera ray direction when degenerate). */
  _aim(player, rig, it) {
    if (player && player.chestPosition) _origin.copy(player.chestPosition);
    else if (player && player.position) { _origin.copy(player.position); _origin.y += 1.3; }
    else _origin.set(0, 1.3, 0);

    const ray = rig && rig.aimRay;
    if (rig && rig.aimPoint) it.aimPoint.copy(rig.aimPoint);
    else if (ray) ray.at(INPUT_TUNING.aimFallbackDistance, it.aimPoint);
    else if (player && player.forward) it.aimPoint.copy(_origin).addScaledVector(player.forward, INPUT_TUNING.aimFallbackDistance);
    else it.aimPoint.copy(_origin).z += INPUT_TUNING.aimFallbackDistance;

    _delta.subVectors(it.aimPoint, _origin);
    const d = _delta.length();
    if (d > INPUT_TUNING.aimMinDistance) {
      it.aimDir.copy(_delta).multiplyScalar(1 / d);
      // Aim point on the camera side of the player (looking back over the shoulder): trust the view ray.
      if (ray && it.aimDir.dot(ray.direction) < 0.2) it.aimDir.copy(ray.direction);
    } else if (ray) it.aimDir.copy(ray.direction);
    else if (player && player.forward) it.aimDir.copy(player.forward);
    if (it.aimDir.lengthSq() < 1e-8) it.aimDir.set(0, 0, 1);
    it.aimDir.normalize();
  }
}
