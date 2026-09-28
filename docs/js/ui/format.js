// ---------------------------------------------------------------------------------------------------------------
// Pure UI formatting helpers + bilingual label tables (no DOM, no three.js). Unit tested by format.test.js.
// ---------------------------------------------------------------------------------------------------------------

/** Seconds -> "m:ss" (rounded up so the clock shows 0:01 until the very end); under 10 s shows "9.4". */
export function formatClock(seconds, tenthsBelow = 10) {
  const s = Math.max(0, Number.isFinite(seconds) ? seconds : 0);
  if (s > 0 && s < tenthsBelow) return (Math.ceil(s * 10) / 10).toFixed(1);
  const whole = Math.ceil(s - 1e-6);
  const m = Math.floor(whole / 60);
  const r = whole % 60;
  return `${m}:${r < 10 ? '0' : ''}${r}`;
}

/** Cooldown seconds -> short label ("7", "2.4"). */
export function formatCooldown(seconds) {
  if (!(seconds > 0)) return '';
  return seconds < 3 ? seconds.toFixed(1) : String(Math.ceil(seconds));
}

/** CSS hex colour from a 0xRRGGBB number or pass-through string. */
export function cssColor(c, fallback = '#ffffff') {
  if (typeof c === 'number' && Number.isFinite(c)) return '#' + (c >>> 0).toString(16).padStart(6, '0').slice(-6);
  if (typeof c === 'string' && c) return c;
  return fallback;
}

/**
 * Charge bar model. The normal charge fills `base` 0..1 (combat.charge). Heroes with an overcharge passive (Rayne)
 * keep charging up to `overFullTime` seconds: `over` 0..1 fills after the normal charge is full and `bonus` is the
 * current fraction of the passive's full bonus (it scales with the whole charge time, per spec "over 2 s").
 * Pass `out` to reuse an object every frame.
 */
export function chargeModel(charge, chargeSeconds, fullChargeTime, overFullTime = 0, out = { base: 0, over: 0, bonus: 0 }) {
  const base = Math.min(1, Math.max(0, Number.isFinite(charge) ? charge : 0));
  out.base = base; out.over = 0; out.bonus = 0;
  if (!(overFullTime > 0)) return out;
  const cs = Math.max(0, chargeSeconds || 0);
  const start = Math.min(fullChargeTime, overFullTime);
  const span = Math.max(1e-3, overFullTime - start);
  out.over = base >= 0.999 ? Math.min(1, Math.max(0, (cs - start) / span)) : 0;
  out.bonus = Math.min(1, cs / overFullTime);
  return out;
}

/** Status chips: label, 中文, tone ('bad' | 'good' | 'info'). */
export const STATUS_LABELS = Object.freeze({
  slow: { en: 'SLOWED', zh: '減速', tone: 'bad' },
  haste: { en: 'HASTE', zh: '加速', tone: 'good' },
  frozen: { en: 'FROZEN', zh: '冰凍', tone: 'ice' },
  stunned: { en: 'STUNNED', zh: '暈眩', tone: 'bad' },
  invulnerable: { en: 'INVULNERABLE', zh: '無敵', tone: 'good' },
  cloaked: { en: 'CLOAKED', zh: '隱身', tone: 'good' },
  revealed: { en: 'REVEALED', zh: '現形', tone: 'bad' },
  silenced: { en: 'SILENCED', zh: '沉默', tone: 'bad' },
  rooted: { en: 'ROOTED', zh: '定身', tone: 'bad' },
  slippery: { en: 'ICE', zh: '冰面', tone: 'ice' },
  dodgeDisabled: { en: 'NO DODGE', zh: '無法閃避', tone: 'bad' },
  silentFootsteps: { en: 'SILENT', zh: '無聲', tone: 'info' },
  obscured: { en: 'OBSCURED', zh: '視線受阻', tone: 'bad' },
  magnetized: { en: 'MAGNETIZED', zh: '磁化', tone: 'info' },
});
export const STATUS_TYPES = Object.freeze(Object.keys(STATUS_LABELS));

/** AbilityFailed reasons -> short bilingual feedback. */
export const FAIL_LABELS = Object.freeze({
  cooldown: 'ON COOLDOWN · 冷卻中',
  ultNotReady: 'ULTIMATE NOT READY · 終極技未就緒',
  cannotAct: 'CANNOT ACT · 無法行動',
  silenced: 'SILENCED · 沉默中',
  requiresBall: 'NEED A BALL · 需要持球',
  noTarget: 'NO TARGET · 沒有目標',
  notInfield: 'INFIELD ONLY · 僅限內場',
  busy: 'BUSY · 施放中',
  notPlaying: 'WAIT FOR THE WHISTLE · 等待開賽',
  custom: 'NOT NOW · 目前無法使用',
});
export function failLabel(reason) { return FAIL_LABELS[reason] || FAIL_LABELS.custom; }

export const DIFFICULTIES = Object.freeze([
  { id: 'easy', en: 'Easy', zh: '簡單' },
  { id: 'normal', en: 'Normal', zh: '普通' },
  { id: 'hard', en: 'Hard', zh: '困難' },
  { id: 'pro', en: 'Pro', zh: '職業' },
]);

export const TEAM_LABELS = Object.freeze({ 0: { en: 'HOME', zh: '主隊' }, 1: { en: 'AWAY', zh: '客隊' } });

/** Elimination causes -> kill feed tag ('' for the ordinary ball hit). */
export function causeLabel(cause) {
  if (!cause) return '';
  const c = String(cause).toLowerCase();
  if (c === 'hit' || c === 'ball' || c === 'throw') return '';
  if (c.includes('catch') || c.includes('caught')) return 'CAUGHT · 被接殺';
  if (c.includes('freeze') || c.includes('frozen') || c.includes('glacier')) return 'SHATTERED · 碎冰';
  if (c.includes('beam') || c.includes('hyper')) return 'TRANSPIERCED · 貫穿';
  if (c.includes('time')) return 'TIME · 時間到';
  if (c.includes('turret')) return 'TURRET · 砲台';
  if (c.includes('out')) return 'OUT · 出界';
  return String(cause).toUpperCase();
}

/** Human readable name for a loading label (asset path). */
export function prettyAssetLabel(label) {
  if (!label) return '';
  const s = String(label);
  let m = s.match(/heroes\/([^/]+)\//i);
  if (m) return `Realistic avatar · 寫實模型 — ${m[1].charAt(0).toUpperCase()}${m[1].slice(1)}`;
  m = s.match(/anims\/([mf])\/([^/.]+)/i);
  if (m) return `Motion capture · 動作捕捉 — ${m[1] === 'f' ? 'F' : 'M'} ${m[2]}`;
  return s.split('/').pop();
}

/** Clamp helper. */
export const clamp01 = (v) => (v < 0 ? 0 : v > 1 ? 1 : v);
