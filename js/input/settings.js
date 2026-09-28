// ---------------------------------------------------------------------------------------------------------------
// Player-facing settings (look sensitivity, invert, volume...) persisted in localStorage. Every storage access is
// wrapped in try/catch: private windows / blocked storage simply fall back to the defaults.
// ---------------------------------------------------------------------------------------------------------------

const STORAGE_KEY = 'dodgeballUltra.settings.v1';

/** Defaults + valid ranges (units in the comments). */
export const SETTINGS_SPEC = Object.freeze({
  mouseSensitivity: { def: 0.085, min: 0.02, max: 0.35 },   // degrees of camera rotation per mouse pixel
  invertY: { def: false },
  gamepadLookSpeed: { def: 210, min: 60, max: 420 },          // degrees / second at full right-stick deflection
  touchSensitivity: { def: 0.26, min: 0.08, max: 0.7 },      // degrees per touch-drag pixel
  touchControls: { def: 'auto', values: ['auto', 'on', 'off'] },
  volume: { def: 0.8, min: 0, max: 1 },
  muted: { def: false },
  showMinimap: { def: true },
});

function sanitize(key, value) {
  const spec = SETTINGS_SPEC[key];
  if (!spec) return undefined;
  if (typeof spec.def === 'boolean') return !!value;
  if (spec.values) return spec.values.includes(value) ? value : spec.def;
  const n = Number(value);
  if (!Number.isFinite(n)) return spec.def;
  return Math.min(spec.max, Math.max(spec.min, n));
}

class SettingsStore {
  constructor() {
    /** Current values (read directly: Settings.values.mouseSensitivity). */
    this.values = {};
    for (const k of Object.keys(SETTINGS_SPEC)) this.values[k] = SETTINGS_SPEC[k].def;
    this._listeners = new Set();
    this.load();
  }

  load() {
    try {
      const raw = globalThis.localStorage && globalThis.localStorage.getItem(STORAGE_KEY);
      if (!raw) return;
      const data = JSON.parse(raw);
      for (const k of Object.keys(SETTINGS_SPEC)) if (k in data) this.values[k] = sanitize(k, data[k]);
    } catch { /* storage unavailable - keep defaults */ }
  }

  save() {
    try { globalThis.localStorage && globalThis.localStorage.setItem(STORAGE_KEY, JSON.stringify(this.values)); } catch { /* ignore */ }
  }

  get(key) { return this.values[key]; }

  /** Sets, clamps, persists and notifies listeners `(key, value)`. */
  set(key, value) {
    const v = sanitize(key, value);
    if (v === undefined || this.values[key] === v) return;
    this.values[key] = v;
    this.save();
    for (const fn of this._listeners) { try { fn(key, v); } catch (e) { console.error('[settings] listener threw', e); } }
  }

  reset() { for (const k of Object.keys(SETTINGS_SPEC)) this.set(k, SETTINGS_SPEC[k].def); }

  /** @returns {() => void} unsubscribe */
  onChange(fn) { this._listeners.add(fn); return () => this._listeners.delete(fn); }
}

/** Singleton settings store. */
export const Settings = new SettingsStore();

/** Small helper for other persisted UI state (last hero pick...). */
export function loadJson(key, fallback) {
  try {
    const raw = globalThis.localStorage && globalThis.localStorage.getItem(key);
    return raw ? { ...fallback, ...JSON.parse(raw) } : { ...fallback };
  } catch { return { ...fallback }; }
}
export function saveJson(key, value) {
  try { globalThis.localStorage && globalThis.localStorage.setItem(key, JSON.stringify(value)); } catch { /* ignore */ }
}
