// Minimal finite state machine keyed by string ids. States: { id, enter(prev), exit(next), update(dt), fixedUpdate(dt), canExitTo(next) }.
// Transitions requested during enter/exit are queued and applied after the current transition finishes.
export class StateMachine {
  constructor() {
    this.states = new Map();
    this.current = null;
    this.previousId = null;
    this.timeInState = 0;
    this._transitioning = false;
    this._pending = null;
    /** @type {(prev:string,next:string)=>void|null} */
    this.onChange = null;
  }
  get currentId() { return this.current ? this.current.id : null; }
  register(state) { this.states.set(state.id, state); return state; }
  is(id) { return !!this.current && this.current.id === id; }
  start(id) {
    const s = this.states.get(id);
    if (!s) throw new Error(`state ${id} not registered`);
    this.current = s; this.previousId = id; this.timeInState = 0;
    this._transitioning = true; s.enter && s.enter(id); this._transitioning = false;
    this._flush();
  }
  change(id, force = false) {
    const next = this.states.get(id);
    if (!next) throw new Error(`state ${id} not registered`);
    if (this._transitioning) { this._pending = { id, force }; return true; }
    if (!this.current) { this.start(id); return true; }
    if (this.current.id === id) return false;
    if (!force && this.current.canExitTo && !this.current.canExitTo(id)) return false;
    this._transitioning = true;
    const prev = this.current;
    prev.exit && prev.exit(id);
    this.previousId = prev.id;
    this.current = next;
    this.timeInState = 0;
    next.enter && next.enter(prev.id);
    this._transitioning = false;
    this.onChange && this.onChange(prev.id, id);
    this._flush();
    return true;
  }
  update(dt) { if (!this.current) return; this.timeInState += dt; this.current.update && this.current.update(dt); }
  fixedUpdate(dt) { this.current && this.current.fixedUpdate && this.current.fixedUpdate(dt); }
  _flush() {
    for (let guard = 0; this._pending && guard < 8; guard++) {
      const p = this._pending; this._pending = null; this.change(p.id, p.force);
    }
    this._pending = null;
  }
}
