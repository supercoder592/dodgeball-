// ---------------------------------------------------------------------------------------------------------------
// Match flow helpers shared by the pause menu, the results panel and hero select (restart / back to hero select /
// audio settings). All cross-module access goes through `game` and is guarded.
// ---------------------------------------------------------------------------------------------------------------
import { game } from '../game.js';
import { TEAM } from '../core/constants.js';
import { Settings } from '../input/settings.js';

let lastDifficulty = 'normal';

/** Hero select remembers the chosen difficulty so a rebuilt setup keeps it. */
export function rememberDifficulty(d) { if (d) lastDifficulty = d; }

/**
 * The setup of the running match: game.match.setup when Match stores it, otherwise rebuilt from the players
 * ({ localHero, localTeam, homeHeroes, awayHeroes, difficulty, spectate }). Null when nothing is loaded.
 */
export function currentSetup() {
  const m = game.match;
  if (!m) return null;
  const stored = m.setup || m.lastSetup || null;
  if (stored && Array.isArray(stored.homeHeroes) && Array.isArray(stored.awayHeroes)) return { ...stored };
  const players = (m.players && m.players.length ? m.players : game.players) || [];
  const home = [], away = [];
  let local = null;
  for (const p of players) {
    if (!p || !p.hero) continue;
    if (p.team === TEAM.HOME) home.push(p.hero.id); else if (p.team === TEAM.AWAY) away.push(p.hero.id);
    if (p.isLocal) local = p;
  }
  if (!home.length && !away.length) return null;
  return {
    localHero: local ? local.hero.id : null,
    localTeam: local ? local.team : TEAM.HOME,
    homeHeroes: home, awayHeroes: away,
    difficulty: m.difficulty || (m.rules && m.rules.difficulty) || lastDifficulty,
    spectate: !local,
  };
}

/** Ends the current match and starts a new one with the same line-ups. */
export async function restartMatch() {
  const setup = currentSetup();
  if (!setup || !game.match || typeof game.match.startMatch !== 'function') { backToHeroSelect(); return; }
  game.time.paused = false;
  try { if (typeof game.match.endMatch === 'function') game.match.endMatch(); } catch (e) { console.error('[ui] endMatch threw', e); }
  try { await game.match.startMatch(setup); } catch (e) { console.error('[ui] restart failed', e); backToHeroSelect(); }
}

/** Ends the match and opens hero select (main.js exposes game.showHeroSelect). */
export function backToHeroSelect() {
  game.time.paused = false;
  try { if (game.match && typeof game.match.endMatch === 'function') game.match.endMatch(); } catch (e) { console.error('[ui] endMatch threw', e); }
  if (typeof game.showHeroSelect === 'function') game.showHeroSelect();
}

/** Pushes the volume / mute settings to the audio system (whatever API it exposes). */
export function applyAudioSettings() {
  const a = game.audio;
  if (!a) return;
  const v = Settings.values.volume, muted = Settings.values.muted;
  try {
    if (typeof a.setVolume === 'function') a.setVolume(v);
    else if (typeof a.setMasterVolume === 'function') a.setMasterVolume(v);
    if (typeof a.setMuted === 'function') a.setMuted(muted);
    else if (typeof a.mute === 'function' && typeof a.unmute === 'function') (muted ? a.mute() : a.unmute());
    else if (a.master && a.master.gain && typeof a.master.gain.setTargetAtTime === 'function' && a.context) {
      a.master.gain.setTargetAtTime(muted ? 0 : v, a.context.currentTime, 0.02);
    }
  } catch (e) { console.warn('[ui] could not apply audio settings', e); }
}

/** First user gesture: let the audio system unlock its AudioContext. */
export function unlockAudio() {
  try { game.audio && typeof game.audio.unlock === 'function' && game.audio.unlock(); } catch { /* ignore */ }
}
