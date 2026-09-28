// Dodgeball Ultra (web) - design constants from the game design document (mirrors DodgeballUltra.Core.GameConstants).
export const KMH_TO_MS = 1 / 3.6;
export const MS_TO_KMH = 3.6;
/** Rally Boost hard cap: 220 km/h. */
export const MAX_BALL_SPEED_KMH = 220;
export const MAX_BALL_SPEED_MS = MAX_BALL_SPEED_KMH * KMH_TO_MS;
/** +10% per consecutive catch-and-rethrow without touching the floor. */
export const RALLY_BOOST_PER_COUNT = 0.10;
/** Perfect Catch <=> 0 <= t_input <= 0.15 s before impact. */
export const PERFECT_CATCH_WINDOW = 0.15;
/** Bear's Iron Mitts: +50% (0.225 s). */
export const IRON_MITTS_MULTIPLIER = 1.5;
/** A normal catch is accepted up to this many seconds before impact. */
export const NORMAL_CATCH_WINDOW = 0.40;
/** Perfect Catch rewards. */
export const PERFECT_CATCH_ULT_GAIN = 0.15;
export const PERFECT_CATCH_COUNTER_BOOST = 0.20;
export const DEFAULT_MAX_HP = 100;
export const THICK_HIDE_MAX_HP = 200;
export const STANDARD_HIT_DAMAGE = 100;
/** Juice spec. */
export const HITSTOP_MIN = 0.03;
export const HITSTOP_MAX = 0.10;
export const HIT_FLASH_DURATION = 0.05;
export const GRAVITY = 9.81;
/** Regulation foam dodgeball: 8.25" diameter. */
export const BALL_RADIUS = 0.105;
export const BALL_MASS = 0.35;

/**
 * Court: 18 x 9 m, Home defends -Z, Away +Z. Each team's OUTFIELD is a U around the OPPONENT's half (Taiwanese /
 * Japanese 外野): both sideline strips alongside that half (sideOutfieldWidth wide, centre line -> baseline) plus the
 * strip behind its baseline (outfieldDepth deep), corners included. runOff = sideline -> padded wall.
 */
export const COURT = Object.freeze({ length: 18, width: 9, outfieldDepth: 3, sideOutfieldWidth: 2.5, runOff: 4 });

export const TEAM = Object.freeze({ HOME: 0, AWAY: 1, NONE: -1 });
export const ZONE = Object.freeze({ INFIELD: 'infield', OUTFIELD: 'outfield' });
export const opponent = (team) => (team === TEAM.HOME ? TEAM.AWAY : team === TEAM.AWAY ? TEAM.HOME : TEAM.NONE);
export const TEAM_COLORS = Object.freeze({ [TEAM.HOME]: 0xff6a2b, [TEAM.AWAY]: 0x2b8cff });
export const TEAM_NAMES = Object.freeze({ [TEAM.HOME]: 'HOME', [TEAM.AWAY]: 'AWAY' });

export const HERO_IDS = Object.freeze(['Rayne', 'Shadow', 'Gale', 'Bear', 'Gouki', 'Screws', 'Houdini', 'Elsa', 'Specter', 'Chrono']);
