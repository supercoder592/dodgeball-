// ---------------------------------------------------------------------------------------------------------------
// Mobile touch controls (owned by the Input system):
//   * left half  : floating virtual stick (appears under the thumb, push fully to sprint)
//   * right half : drag to look / aim
//   * buttons    : THROW (hold to charge, drag while holding to fine-aim), CATCH, JUMP, SLIDE, PASS, GRAB, SKILL,
//                  ULT, TARGET, pause. Big, thumb-friendly and semi-transparent (styles in css/game.css, .tc-*).
// Pointer Events with pointer capture handle any number of simultaneous fingers.
// Skill / ultimate buttons show cooldown sweeps and the ult charge ring (written by the HUD via setAbilityState).
// ---------------------------------------------------------------------------------------------------------------

/** Stick geometry (CSS px). */
export const TOUCH_STICK = Object.freeze({ radius: 56, sprintThreshold: 0.93, deadzone: 0.1 });

/** Button layout: action ids match Input actions. */
const BUTTONS = [
  { id: 'throw', action: 'throw', en: 'THROW', zh: '投球', cls: 'tc-throw', aimDrag: true },
  { id: 'catch', action: 'catch', en: 'CATCH', zh: '接球', cls: 'tc-catch' },
  { id: 'jump', action: 'jump', en: 'JUMP', zh: '跳躍', cls: 'tc-jump' },
  { id: 'slide', action: 'slide', en: 'SLIDE', zh: '滑鏟', cls: 'tc-slide' },
  { id: 'pass', action: 'pass', en: 'PASS', zh: '傳球', cls: 'tc-pass' },
  { id: 'pickup', action: 'pickup', en: 'GRAB', zh: '撿球', cls: 'tc-pickup' },
  { id: 'skill', action: 'skill', en: 'SKILL', zh: '技能', cls: 'tc-skill', ability: true },
  { id: 'ultimate', action: 'ultimate', en: 'ULT', zh: '終極技', cls: 'tc-ult', ability: true },
  { id: 'target', action: 'cycleTarget', en: 'TARGET', zh: '目標', cls: 'tc-target' },
];

export class TouchControls {
  /** @param {{ setTouchAction(action:string, on:boolean):void, requestPause(source?:string):void }} sink */
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
    /** id -> button element */
    this.buttons = {};
    this._stickId = null;
    this._stickOx = 0; this._stickOy = 0;
    this._lookId = null;
    this._lookX = 0; this._lookY = 0;
    /** pointerId -> { def, x, y } for pointers holding a button */
    this._btnPointers = new Map();
    this._btnCount = {};
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
      <div class="tc-hint tc-hint-move">MOVE · 移動</div>
      <div class="tc-hint tc-hint-look">DRAG TO AIM · 拖曳瞄準</div>
      <button class="tc-pause" type="button" aria-label="Pause">❚❚</button>`;
    for (const def of BUTTONS) {
      const b = document.createElement('button');
      b.type = 'button';
      b.className = `tc-btn ${def.cls}${def.ability ? ' tc-ability' : ''}`;
      b.dataset.act = def.id;
      b.innerHTML = `${def.ability ? '<span class="tc-sweep"></span><span class="tc-cd"></span>' : ''}<b>${def.en}</b><small>${def.zh}</small>`;
      root.appendChild(b);
      this.buttons[def.id] = b;
      this._btnCount[def.id] = 0;
      this._bindButton(b, def);
    }
    parent.appendChild(root);
    this.root = root;
    this.stick = root.querySelector('.tc-stick');
    this.knob = root.querySelector('.tc-knob');
    this._bindZone(root.querySelector('.tc-zone-move'), 'move');
    this._bindZone(root.querySelector('.tc-zone-look'), 'look');
    const pause = root.querySelector('.tc-pause');
    this._on(pause, 'pointerdown', (e) => { e.preventDefault(); e.stopPropagation(); this.sink.requestPause('touch'); });
    // Never let the browser turn a long press into a context menu / selection.
    this._on(root, 'contextmenu', (e) => e.preventDefault());
    this._placeStickHome();
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
    for (const def of BUTTONS) {
      if (this._btnCount[def.id] > 0) { this._btnCount[def.id] = 0; this.sink.setTouchAction(def.action, false); }
      const el = this.buttons[def.id];
      if (el) el.classList.remove('tc-down');
    }
    this._btnPointers.clear();
    if (this.stick) { this.stick.classList.remove('tc-active'); this._placeStickHome(); }
  }

  /** Reads and clears the accumulated look drag (px). */
  consumeLook(out) {
    out.x = this.lookDx; out.y = this.lookDy;
    this.lookDx = 0; this.lookDy = 0;
    return out;
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
        if (this._lookId !== null) return;
        this._lookId = e.pointerId;
        this._lookX = e.clientX; this._lookY = e.clientY;
      }
      try { zone.setPointerCapture(e.pointerId); } catch { /* capture unsupported */ }
    });
    this._on(zone, 'pointermove', (e) => {
      if (kind === 'move' && e.pointerId === this._stickId) this._updateStick(e.clientX, e.clientY);
      else if (kind === 'look' && e.pointerId === this._lookId) {
        this.lookDx += e.clientX - this._lookX;
        this.lookDy += e.clientY - this._lookY;
        this._lookX = e.clientX; this._lookY = e.clientY;
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
      }
    };
    this._on(zone, 'pointerup', end);
    this._on(zone, 'pointercancel', end);
    this._on(zone, 'lostpointercapture', end);
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

  _bindButton(el, def) {
    this._on(el, 'pointerdown', (e) => {
      if (!this.visible) return;
      e.preventDefault(); e.stopPropagation();
      try { el.setPointerCapture(e.pointerId); } catch { /* ignore */ }
      if (this._btnPointers.has(e.pointerId)) return;
      this._btnPointers.set(e.pointerId, { def, x: e.clientX, y: e.clientY });
      if (this._btnCount[def.id]++ === 0) {
        el.classList.add('tc-down');
        this.sink.setTouchAction(def.action, true);
        if (navigator.vibrate) { try { navigator.vibrate(8); } catch { /* ignore */ } }
      }
    });
    this._on(el, 'pointermove', (e) => {
      const p = this._btnPointers.get(e.pointerId);
      if (!p || !p.def.aimDrag) return;
      // Dragging the held THROW button fine-aims (same gain as the look zone).
      this.lookDx += e.clientX - p.x;
      this.lookDy += e.clientY - p.y;
      p.x = e.clientX; p.y = e.clientY;
    });
    const up = (e) => {
      const p = this._btnPointers.get(e.pointerId);
      if (!p) return;
      this._btnPointers.delete(e.pointerId);
      if (this._btnCount[def.id] > 0 && --this._btnCount[def.id] === 0) {
        el.classList.remove('tc-down');
        this.sink.setTouchAction(def.action, false);
      }
    };
    this._on(el, 'pointerup', up);
    this._on(el, 'pointercancel', up);
    this._on(el, 'lostpointercapture', up);
    this._on(el, 'contextmenu', (e) => e.preventDefault());
  }
}
