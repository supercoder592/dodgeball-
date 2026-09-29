// ---------------------------------------------------------------------------------------------------------------
// Mobile touch controls (owned by the Input system) - a streamlined five-button layout:
//   * left side  : floating virtual stick (appears under the thumb, push fully to sprint)
//   * right side : drag to look / aim; a short tap (< 220 ms, < 12 px) cycles the soft-lock target (cycleTarget).
//                  A finger landing just outside a button's circle (TOUCH_TUNING.buttonSlopPx) presses that button;
//                  one on the hidden 傳球's spot only looks (never a target cycle).
//   * primary    : ONE big context button, bottom right. Label + colour follow setContext() (Input.lateUpdate, every
//                  frame): 投球 while holding the ball (hold to charge, drag while holding to fine-aim, release to
//                  throw), 撿球 when a loose ball is within manual reach and no enemy ball is coming (tap = pickup),
//                  else 接球 (tap = catch). The action a press triggers is locked at pointerdown: a label flip
//                  mid-press never changes the action or releases a charge; a cancelled 投球 press (system edge
//                  swipe, lost capture) drops the wind-up instead of throwing (sink.cancelTouchThrow).
//   * 閃避       : dodge. Tap while the stick is pushed (> 0.3) -> slide in the move direction; tap while standing
//                  still -> jump; a quick upward swipe that starts on the button -> always jump (sink.touchDodge,
//                  which jumps whenever the state machine would refuse the slide: wind-up = jump throw).
//   * 傳球       : pass, shown only while the local player holds the ball (hidden in place, nothing else moves).
//   * 技能 / 大絕 : skill / ultimate with cooldown sweeps and the ult charge ring (setAbilityState, from the HUD).
//   * pause      : top right.
// Labels are short Chinese only; the stick / aim hints fade out after TOUCH_TUNING.hintSeconds of 'playing' phase
// (once per session). Styles in css/game.css (.tc-*). Pointer Events with pointer capture handle any number of simultaneous
// fingers; per-frame work (update / setContext) is allocation-free.
// ---------------------------------------------------------------------------------------------------------------
import { TOUCH_PRIMARY, DODGE, dodgeGesture, isTap } from './inputMath.js';

/** Stick geometry (CSS px). */
export const TOUCH_STICK = Object.freeze({ radius: 56, sprintThreshold: 0.93, deadzone: 0.1 });

/** Gesture tuning. */
export const TOUCH_TUNING = Object.freeze({
  dodgeMoveThreshold: 0.3,   // stick magnitude above which a 閃避 tap slides instead of jumping
  dodgeSwipePx: 16,          // px of upward travel from the 閃避 press that makes it a jump
  dodgeDecideMs: 110,        // ms a 閃避 press made while moving waits for that swipe before it slides
  tapMaxMs: 220,             // look-zone tap (cycle target): released within this ...
  tapMaxPx: 12,              // ... and never dragged farther than this
  buttonSlopPx: 16,          // px around a button's circle that still press it (near-miss) instead of look / cycle
  hintSeconds: 6,            // s of 'playing' phase before the stick / aim hints fade out (first match only)
});

/** Primary button modes: action pressed, label, colour class, drag-to-aim while held. */
const PRIMARY = Object.freeze({
  [TOUCH_PRIMARY.THROW]: Object.freeze({ action: 'throw', label: '投球', cls: 'tc-m-throw', aimDrag: true }),
  [TOUCH_PRIMARY.PICKUP]: Object.freeze({ action: 'pickup', label: '撿球', cls: 'tc-m-pickup', aimDrag: false }),
  [TOUCH_PRIMARY.CATCH]: Object.freeze({ action: 'catch', label: '接球', cls: 'tc-m-catch', aimDrag: false }),
});

const ICON_SKILL = '<svg viewBox="0 0 24 24" aria-hidden="true"><path fill="currentColor" d="M13.2 2 4.5 13.6h6.1L9.8 22l8.7-11.6h-6.1z"/></svg>';
const ICON_ULT = '<svg viewBox="0 0 24 24" aria-hidden="true"><path fill="currentColor" d="m12 1.8 2.6 6.2 6.7.6-5.1 4.4 1.6 6.6L12 16.1l-5.8 3.5 1.6-6.6-5.1-4.4 6.7-.6z"/></svg>';

/**
 * Button layout. kind: 'primary' (context action, see PRIMARY), 'dodge' (jump / slide gesture), 'hold' (one Input
 * action held while the finger is down).
 */
const BUTTONS = Object.freeze([
  { id: 'primary', kind: 'primary', label: PRIMARY[TOUCH_PRIMARY.CATCH].label, cls: 'tc-primary' },
  { id: 'dodge', kind: 'dodge', label: '閃避', cls: 'tc-dodge' },
  { id: 'pass', kind: 'hold', action: 'pass', label: '傳球', cls: 'tc-pass' },
  { id: 'skill', kind: 'hold', action: 'skill', label: '技能', cls: 'tc-skill', ability: true, icon: ICON_SKILL },
  { id: 'ultimate', kind: 'hold', action: 'ultimate', label: '大絕', cls: 'tc-ult', ability: true, icon: ICON_ULT },
]);
/** Input actions a touch button can hold down (jump / slide / cycleTarget are one-frame pulses). */
const HELD_ACTIONS = Object.freeze(['throw', 'pickup', 'catch', 'pass', 'skill', 'ultimate']);

/**
 * When a pointer event happened on the performance.now() clock: event.timeStamp (a janky frame between pointerdown
 * and pointerup must not turn a tap into a hold), falling back to now for browsers with another time base.
 */
function eventTime(e) {
  const now = performance.now();
  const t = e ? e.timeStamp : 0;
  return t > 0 && t <= now + 1 && now - t < 5000 ? t : now;
}

export class TouchControls {
  /**
   * @param {{ setTouchAction(action:string, on:boolean):void, touchDodge(slide:boolean):void,
   *   cancelTouchThrow():void, requestPause(source?:string):void }} sink the Input system
   */
  constructor(sink) {
    this.sink = sink;
    this.root = null;
    this.visible = false;
    /** Stick output: x = right, y = forward, magnitude <= 1. */
    this.move = { x: 0, y: 0 };
    this.moveMagnitude = 0;
    /** Auto-sprint when the stick is pushed to the rim. */
    this.sprint = false;
    /** Look drag accumulated since the last consume (CSS px). */
    this.lookDx = 0;
    this.lookDy = 0;
    /** TOUCH_PRIMARY mode shown on the primary button (setContext). */
    this.primaryMode = TOUCH_PRIMARY.CATCH;
    /** The 傳球 button is shown (the local player holds the ball). */
    this.passShown = true;
    /** id -> button element */
    this.buttons = {};
    this._primaryLabel = null;
    this._stickId = null;
    this._stickOx = 0; this._stickOy = 0;
    this._lookId = null;
    this._lookX = 0; this._lookY = 0;
    this._lookX0 = 0; this._lookY0 = 0; this._lookT0 = 0; this._lookTravel = 0;
    /** The look finger went down on the hidden 傳球's spot: it may look, but its tap never cycles the target. */
    this._lookNoTap = false;
    /** pointerId -> { def, el, action, aimDrag, x, y, x0, y0, t0, moving, resolved } for fingers on a button */
    this._btnPointers = new Map();
    /** Fingers per button id (tc-down) and per held action (edges only on 0 <-> 1). */
    this._btnCount = {};
    this._actCount = {};
    for (const a of HELD_ACTIONS) this._actCount[a] = 0;
    /** The 閃避 press still waiting to become a slide or a jump. */
    this._dodge = null;
    this._hintT = 0;
    this._hintsDone = false;
    this._abilityCache = { skill: null, ultimate: null };
    this._off = [];
  }

  /** Builds the DOM under `parent` (#ui). Idempotent. */
  build(parent) {
    if (this.root) return this.root;
    const root = document.createElement('div');
    root.id = 'touch';
    root.className = 'tc-layer';
    root.setAttribute('aria-hidden', 'true');
    root.innerHTML = `
      <div class="tc-zone tc-zone-move"></div>
      <div class="tc-zone tc-zone-look"></div>
      <div class="tc-stick"><div class="tc-stick-ring"></div><div class="tc-knob"></div></div>
      <div class="tc-hint tc-hint-move">移動 · 推到底衝刺</div>
      <div class="tc-hint tc-hint-look">拖曳瞄準 · 輕點切換目標</div>
      <button class="tc-pause" type="button" aria-label="Pause">❚❚</button>`;
    for (const def of BUTTONS) {
      const b = document.createElement('button');
      b.type = 'button';
      b.className = `tc-btn ${def.cls}${def.ability ? ' tc-ability' : ''}`;
      b.dataset.act = def.id;
      b.innerHTML = `${def.ability ? '<span class="tc-sweep"></span><span class="tc-cd"></span>' : ''}${def.icon || ''}<b>${def.label}</b>`;
      root.appendChild(b);
      this.buttons[def.id] = b;
      this._btnCount[def.id] = 0;
      this._bindButton(b, def);
    }
    parent.appendChild(root);
    this.root = root;
    this._primaryLabel = this.buttons.primary.querySelector('b');
    this.buttons.primary.classList.add(PRIMARY[this.primaryMode].cls);
    this.buttons.primary.dataset.mode = this.primaryMode;
    this.stick = root.querySelector('.tc-stick');
    this.knob = root.querySelector('.tc-knob');
    this._bindZone(root.querySelector('.tc-zone-move'), 'move');
    this._bindZone(root.querySelector('.tc-zone-look'), 'look');
    const pause = root.querySelector('.tc-pause');
    this._on(pause, 'pointerdown', (e) => { e.preventDefault(); e.stopPropagation(); this.sink.requestPause('touch'); });
    // Never let the browser turn a long press into a context menu / selection.
    this._on(root, 'contextmenu', (e) => e.preventDefault());
    this._placeStickHome();
    this.setContext(TOUCH_PRIMARY.CATCH, false);
    return root;
  }

  setVisible(b) {
    b = !!b;
    if (b === this.visible) return;
    this.visible = b;
    if (this.root) this.root.classList.toggle('tc-visible', b);
    if (!b) this.releaseAll();
  }

  /** Lets go of every finger (menus opening, blur...). */
  releaseAll() {
    this._stickId = null;
    this.move.x = 0; this.move.y = 0; this.moveMagnitude = 0; this.sprint = false;
    this._lookId = null; this.lookDx = 0; this.lookDy = 0;
    for (const a of HELD_ACTIONS) {
      if (this._actCount[a] > 0) { this._actCount[a] = 0; this.sink.setTouchAction(a, false); }
    }
    for (const def of BUTTONS) {
      this._btnCount[def.id] = 0;
      const el = this.buttons[def.id];
      if (el) el.classList.remove('tc-down');
    }
    this._btnPointers.clear();
    this._dodge = null;
    if (this.stick) { this.stick.classList.remove('tc-active'); this._placeStickHome(); }
  }

  /** Reads and clears the accumulated look drag (px). */
  consumeLook(out) {
    out.x = this.lookDx; out.y = this.lookDy;
    this.lookDx = 0; this.lookDy = 0;
    return out;
  }

  /**
   * Per-frame game context (Input.lateUpdate): primary button mode (TOUCH_PRIMARY) and whether 傳球 is shown.
   * Only touches the DOM when something changed. Presses already in progress keep their action.
   */
  setContext(mode, passVisible) {
    const el = this.buttons.primary;
    const next = PRIMARY[mode];
    if (el && next && mode !== this.primaryMode) {
      el.classList.remove(PRIMARY[this.primaryMode].cls);
      el.classList.add(next.cls);
      el.dataset.mode = mode;
      if (this._primaryLabel) this._primaryLabel.textContent = next.label;
      this.primaryMode = mode;
    }
    passVisible = !!passVisible;
    if (passVisible !== this.passShown && this.buttons.pass) {
      this.passShown = passVisible;
      this.buttons.pass.classList.toggle('tc-off', !passVisible);
    }
  }

  /**
   * Per frame (Input.update, before the actions latch): resolves a 閃避 press that waited long enough for a swipe
   * (-> slide) and fades the hints out after the first seconds of play.
   * @param {number} realDt seconds (unscaled)
   * @param {boolean} playing the round is in its 'playing' phase (pre-round / countdown do not use up hint time)
   */
  update(realDt, playing = true) {
    const d = this._dodge;
    if (d && !d.resolved) this._resolveDodge(d, false, d.x, d.y, performance.now());
    if (!this._hintsDone && playing && this.visible && this.root) {
      this._hintT += realDt;
      if (this._hintT >= TOUCH_TUNING.hintSeconds) { this._hintsDone = true; this.root.classList.add('tc-hints-off'); }
    }
  }

  /**
   * Ability button state from the HUD. cd: 0..1 remaining cooldown fraction, seconds: label, ready, active,
   * charge: ult meter 0..1 (ultimate only). Only touches the DOM when something visible changed.
   */
  setAbilityState(id, cd, seconds, ready, active, charge = 1) {
    const el = this.buttons[id];
    if (!el) return;
    let c = this._abilityCache[id];
    if (!c || typeof c !== 'object') c = this._abilityCache[id] = { cd: -1, sec: null, ready: null, active: null, charge: -1, cdEl: el.querySelector('.tc-cd') };
    const qcd = Math.round(cd * 200) / 200, qch = Math.round(charge * 200) / 200;
    if (c.cd !== qcd) { c.cd = qcd; el.style.setProperty('--cd', String(qcd)); }
    if (c.charge !== qch) { c.charge = qch; el.style.setProperty('--charge', String(qch)); }
    if (c.ready !== !!ready) { c.ready = !!ready; el.classList.toggle('tc-ready', c.ready); }
    if (c.active !== !!active) { c.active = !!active; el.classList.toggle('tc-active', c.active); }
    if (c.sec !== seconds && c.cdEl) { c.sec = seconds; c.cdEl.textContent = seconds; }
  }

  dispose() {
    for (const off of this._off) off();
    this._off = [];
    if (this.root) this.root.remove();
    this.root = null;
  }

  // ------------------------------------------------------------------ internals
  _on(el, type, fn, opts) {
    el.addEventListener(type, fn, opts);
    this._off.push(() => el.removeEventListener(type, fn, opts));
  }

  _placeStickHome() {
    if (!this.stick) return;
    // Resting position: bottom-left, clear of the safe area.
    this.stick.style.left = '';
    this.stick.style.top = '';
    this.stick.classList.add('tc-home');
    this.knob.style.transform = 'translate(-50%, -50%)';
  }

  _bindZone(zone, kind) {
    this._on(zone, 'pointerdown', (e) => {
      if (!this.visible) return;
      e.preventDefault();
      if (kind === 'move') {
        if (this._stickId !== null) return;
        this._stickId = e.pointerId;
        const r = TOUCH_STICK.radius;
        // Keep the whole stick base on screen.
        this._stickOx = Math.min(innerWidth - r - 8, Math.max(r + 8, e.clientX));
        this._stickOy = Math.min(innerHeight - r - 8, Math.max(r + 8, e.clientY));
        this.stick.classList.remove('tc-home');
        this.stick.classList.add('tc-active');
        this.stick.style.left = `${this._stickOx}px`;
        this.stick.style.top = `${this._stickOy}px`;
        this._updateStick(e.clientX, e.clientY);
      } else {
        // Near-miss on a round button (its bounding-box corners and a slop ring fall through to this zone): press
        // that button. On the hidden 傳球's spot: look only, a tap there never re-targets.
        const near = this._buttonNear(e.clientX, e.clientY);
        const nearEl = near && this.buttons[near.id];
        if (nearEl && !nearEl.classList.contains('tc-off')) { this._buttonDown(nearEl, near, e); return; }
        if (this._lookId !== null) return;
        this._lookNoTap = !!near;
        this._lookId = e.pointerId;
        this._lookX = this._lookX0 = e.clientX;
        this._lookY = this._lookY0 = e.clientY;
        this._lookT0 = eventTime(e);
        this._lookTravel = 0;
      }
      try { zone.setPointerCapture(e.pointerId); } catch { /* capture unsupported */ }
    });
    this._on(zone, 'pointermove', (e) => {
      if (kind === 'move' && e.pointerId === this._stickId) this._updateStick(e.clientX, e.clientY);
      else if (kind === 'look' && e.pointerId === this._lookId) {
        this.lookDx += e.clientX - this._lookX;
        this.lookDy += e.clientY - this._lookY;
        this._lookX = e.clientX; this._lookY = e.clientY;
        this._trackLookTravel(e.clientX, e.clientY);
      }
    });
    const end = (e) => {
      if (kind === 'move' && e.pointerId === this._stickId) {
        this._stickId = null;
        this.move.x = 0; this.move.y = 0; this.moveMagnitude = 0; this.sprint = false;
        this.stick.classList.remove('tc-active');
        this._placeStickHome();
      } else if (kind === 'look' && e.pointerId === this._lookId) {
        this._lookId = null;
        // A short, still tap on the look side cycles the soft-lock target; any real drag only looks / aims.
        if (e.type === 'pointerup' && !this._lookNoTap) {
          this._trackLookTravel(e.clientX, e.clientY);
          if (isTap(eventTime(e) - this._lookT0, this._lookTravel, TOUCH_TUNING.tapMaxMs, TOUCH_TUNING.tapMaxPx)) this._pulse('cycleTarget');
        }
      }
    };
    this._on(zone, 'pointerup', end);
    this._on(zone, 'pointercancel', end);
    this._on(zone, 'lostpointercapture', end);
  }

  /**
   * The button (def) whose circle, grown by TOUCH_TUNING.buttonSlopPx, contains (x, y) - the closest edge wins - or
   * null. Hidden buttons count too (the caller decides). Runs on pointerdown only (layout reads are fine there).
   */
  _buttonNear(x, y) {
    let best = null, bestD = TOUCH_TUNING.buttonSlopPx;
    for (const def of BUTTONS) {
      const el = this.buttons[def.id];
      if (!el) continue;
      const r = el.getBoundingClientRect();
      if (!(r.width > 0)) continue;
      const d = Math.hypot(x - (r.left + r.width / 2), y - (r.top + r.height / 2)) - r.width / 2;
      if (d <= bestD) { bestD = d; best = def; }
    }
    return best;
  }

  _trackLookTravel(x, y) {
    const d = Math.hypot(x - this._lookX0, y - this._lookY0);
    if (d > this._lookTravel) this._lookTravel = d;
  }

  _updateStick(x, y) {
    const r = TOUCH_STICK.radius;
    let dx = x - this._stickOx, dy = y - this._stickOy;
    const d = Math.hypot(dx, dy);
    if (d > r) { dx *= r / d; dy *= r / d; }
    this.knob.style.transform = `translate(calc(-50% + ${dx.toFixed(1)}px), calc(-50% + ${dy.toFixed(1)}px))`;
    let mx = dx / r, my = -dy / r; // screen up = forward
    let m = Math.hypot(mx, my);
    if (m < TOUCH_STICK.deadzone) { mx = 0; my = 0; m = 0; } else {
      // Rescale past the deadzone so small pushes still walk slowly.
      const n = (m - TOUCH_STICK.deadzone) / (1 - TOUCH_STICK.deadzone);
      mx = (mx / m) * n; my = (my / m) * n; m = n;
    }
    this.move.x = mx; this.move.y = my; this.moveMagnitude = m;
    this.sprint = m >= TOUCH_STICK.sprintThreshold;
    this.stick.classList.toggle('tc-sprint', this.sprint);
  }

  /** A held action went down on one more finger (edge only for the first). */
  _press(action) {
    if (this._actCount[action]++ === 0) this.sink.setTouchAction(action, true);
  }

  _release(action) {
    if (this._actCount[action] > 0 && --this._actCount[action] === 0) this.sink.setTouchAction(action, false);
  }

  /** One-frame press (jump / slide / cycle target): ActionState keeps a press + release inside one frame. */
  _pulse(action) {
    this.sink.setTouchAction(action, true);
    this.sink.setTouchAction(action, false);
  }

  /** Decides a pending 閃避 press (x, y = finger position at time t, performance.now() clock). */
  _resolveDodge(rec, released, x, y, t) {
    const out = dodgeGesture(rec.moving, x - rec.x0, y - rec.y0, t - rec.t0, released,
      TOUCH_TUNING.dodgeSwipePx, TOUCH_TUNING.dodgeDecideMs);
    if (out === DODGE.NONE) return;
    rec.resolved = true;
    if (this._dodge === rec) this._dodge = null;
    this.sink.touchDodge(out === DODGE.SLIDE);
  }

  /**
   * A finger goes down on button `el` (its own pointerdown, or a near-miss forwarded by the look zone): capture the
   * pointer on the button so its move / up / cancel handlers own the finger from here on.
   */
  _buttonDown(el, def, e) {
    if (!this.visible) return;
    e.preventDefault(); e.stopPropagation();
    try { el.setPointerCapture(e.pointerId); } catch { /* ignore */ }
    if (this._btnPointers.has(e.pointerId)) return;
    // The action is fixed here: whatever the primary button shows NOW is what this finger does until it lifts.
    let action = def.action || null, aimDrag = false;
    if (def.kind === 'primary') { const m = PRIMARY[this.primaryMode]; action = m.action; aimDrag = m.aimDrag; }
    const rec = {
      def, el, action, aimDrag, x: e.clientX, y: e.clientY, x0: e.clientX, y0: e.clientY, t0: eventTime(e),
      moving: this.moveMagnitude > TOUCH_TUNING.dodgeMoveThreshold, resolved: def.kind !== 'dodge',
    };
    this._btnPointers.set(e.pointerId, rec);
    if (this._btnCount[def.id]++ === 0) {
      el.classList.add('tc-down');
      if (navigator.vibrate) { try { navigator.vibrate(8); } catch { /* ignore */ } }
    }
    if (action) this._press(action);
    if (def.kind === 'dodge') {
      this._dodge = rec;
      this._resolveDodge(rec, false, e.clientX, e.clientY, rec.t0); // standing still: jump right away
    }
  }

  _bindButton(el, def) {
    this._on(el, 'pointerdown', (e) => this._buttonDown(el, def, e));
    this._on(el, 'pointermove', (e) => {
      const p = this._btnPointers.get(e.pointerId);
      if (!p) return;
      if (p.aimDrag) {
        // Dragging the held 投球 button fine-aims (same gain as the look zone).
        this.lookDx += e.clientX - p.x;
        this.lookDy += e.clientY - p.y;
      }
      p.x = e.clientX; p.y = e.clientY;
      if (!p.resolved) this._resolveDodge(p, false, p.x, p.y, eventTime(e)); // quick upward swipe -> jump
    });
    const up = (e) => {
      const p = this._btnPointers.get(e.pointerId);
      if (!p) return;
      this._btnPointers.delete(e.pointerId);
      // A tap while moving slides; a cancelled gesture (system swipe, lost capture) does nothing.
      if (!p.resolved) {
        if (e.type === 'pointerup') this._resolveDodge(p, true, e.clientX, e.clientY, eventTime(e));
        else { p.resolved = true; if (this._dodge === p) this._dodge = null; }
      }
      if (this._btnCount[def.id] > 0 && --this._btnCount[def.id] === 0) el.classList.remove('tc-down');
      // A cancelled 投球 (the last finger holding it) drops the wind-up first: releasing it must not throw.
      if (p.action === 'throw' && e.type !== 'pointerup' && this._actCount.throw === 1) this.sink.cancelTouchThrow();
      if (p.action) this._release(p.action);
    };
    this._on(el, 'pointerup', up);
    this._on(el, 'pointercancel', up);
    this._on(el, 'lostpointercapture', up);
    this._on(el, 'contextmenu', (e) => e.preventDefault());
  }
}
