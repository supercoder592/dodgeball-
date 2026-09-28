// ---------------------------------------------------------------------------------------------------------------
// Bot skill presets + hero play-style traits (pure data, no three.js - unit tested in node).
//
// A profile never grants super-human powers: it only changes how fast and how accurately the bot perceives, decides
// and presses the SAME buttons a human presses (the intent goes through the normal PlayerStateMachine). Spec anchors:
//   reaction time 0.45 s (easy) -> 0.12 s (pro); catch-press timing sigma 0.12 s -> 0.03 s around ~0.08 s before
//   impact (inside the 0.15 s Perfect Catch window); aim error, catch attempt probability, dodge skill, ability usage
//   and decision interval (+ jitter) scale with the tier.
// Ported from the Unity BotDifficultyProfile / BotHeroTraits (Assets/DodgeballUltra/Scripts/Runtime/AI).
// ---------------------------------------------------------------------------------------------------------------

/** Difficulty ids accepted by `new Bot(difficulty, seed)`. */
export const DIFFICULTY_IDS = Object.freeze(['easy', 'normal', 'hard', 'pro']);

/**
 * Every skill knob of a bot (units: seconds, metres, degrees, probabilities 0..1).
 * @typedef {object} BotProfile
 * @property {number} reactionTime          mean delay between a throw and the bot noticing it (s)
 * @property {number} reactionJitter        +/- fraction applied to every reaction sample
 * @property {number} rearReactionMul       reaction multiplier for throws from outside the field of view
 * @property {number} rearBlindChance       chance to not notice a rear throw until it is within the peripheral radius
 * @property {number} fieldOfView           half-angle (deg) of the bot's field of view
 * @property {number} dangerSenseReactionMul reaction multiplier while Specter's Danger Sense warns about a fastball
 * @property {number} cloakDetectionRadius  distance (m) inside which a cloaked enemy is still noticed
 * @property {number} obscuredMistargetChance chance to aim at a decoy beside an Obscured (cloned) enemy
 * @property {number} decisionInterval      seconds between utility decisions (threats are handled every frame)
 * @property {number} decisionJitter        +/- fraction applied to each decision interval
 * @property {number} hysteresis            score bonus of the running behaviour / target / ball
 * @property {number} minBehaviourDwell     minimum seconds a non-threat behaviour is kept
 * @property {number} aggression            0 = stays deep, 1 = lives on the centre line
 * @property {number} aimErrorDeg           std-dev (deg) of the angular aim error at release
 * @property {number} leadAccuracy          fraction of the ideal intercept lead actually applied
 * @property {number} chargeTimeMin         shortest planned charge (s)
 * @property {number} chargeTimeMax         longest planned charge (s)
 * @property {number} throwHesitationMin    settle time after gaining a ball before charging (s)
 * @property {number} throwHesitationMax
 * @property {number} opportunismSkill      chance to release early when the target commits (jump / charge / stun)
 * @property {number} targetSelectionSkill  chance to pick the best-scored target (else a score-weighted random one)
 * @property {number} passChance            chance per possession to consider a pass
 * @property {number} strafeAmplitude       lateral strafe amplitude (m) while attacking
 * @property {number} catchAttemptProbability base chance to catch instead of dodging
 * @property {number} idealCatchLead        ideal seconds before impact to press Catch
 * @property {number} catchTimingSigma      std-dev (s) of the Catch press around the ideal moment
 * @property {number} dodgeSkill            0..1 quality of the evasive manoeuvre (right option, right side, no hesitation)
 * @property {number} maneuverTimingSigma   std-dev (s) of jump / slide presses
 * @property {number} abilityUsageFactor    multiplier on AbilityBase.evaluateAI()
 * @property {number} abilityUtilityThreshold utility (after the factor) required to press Skill / Ultimate
 * @property {number} abilityRetryInterval  minimum seconds between two presses of the same ability button
 * @property {number} abilityFailBackoff    seconds an ability is left alone after it failed to activate
 * @property {number} preferredDepth        preferred distance (m) from the centre line when not attacking
 * @property {number} holderAvoidDistance   distance (m) kept from enemies holding a ball
 * @property {number} teammateSpacing       distance (m) kept from teammates
 */

/** Normal tier: the reference values every other tier is tuned against. */
const NORMAL = {
  reactionTime: 0.30, reactionJitter: 0.22, rearReactionMul: 1.8, rearBlindChance: 0.35, fieldOfView: 105,
  dangerSenseReactionMul: 0.45, cloakDetectionRadius: 2.5, obscuredMistargetChance: 0.5,
  decisionInterval: 0.32, decisionJitter: 0.30, hysteresis: 0.15, minBehaviourDwell: 0.35, aggression: 0.5,
  aimErrorDeg: 4.0, leadAccuracy: 0.8, chargeTimeMin: 0.2, chargeTimeMax: 1.2, throwHesitationMin: 0.35, throwHesitationMax: 0.8,
  opportunismSkill: 0.35, targetSelectionSkill: 0.65, passChance: 0.25, strafeAmplitude: 0.9,
  catchAttemptProbability: 0.5, idealCatchLead: 0.08, catchTimingSigma: 0.08,
  dodgeSkill: 0.55, maneuverTimingSigma: 0.06,
  abilityUsageFactor: 0.8, abilityUtilityThreshold: 0.45, abilityRetryInterval: 1.8, abilityFailBackoff: 4,
  preferredDepth: 5.0, holderAvoidDistance: 8, teammateSpacing: 3.0,
};

/** Tuned presets (spec: reaction 0.45 -> 0.12 s, catch sigma 0.12 -> 0.03 s). */
export const BOT_DIFFICULTY = Object.freeze({
  easy: Object.freeze({
    ...NORMAL,
    reactionTime: 0.45, reactionJitter: 0.30, rearReactionMul: 2.0, rearBlindChance: 0.5, fieldOfView: 95,
    dangerSenseReactionMul: 0.5, cloakDetectionRadius: 1.5, obscuredMistargetChance: 0.65,
    decisionInterval: 0.45, decisionJitter: 0.35, hysteresis: 0.2, minBehaviourDwell: 0.5, aggression: 0.35,
    aimErrorDeg: 6.5, leadAccuracy: 0.55, throwHesitationMin: 0.6, throwHesitationMax: 1.3, opportunismSkill: 0.1,
    targetSelectionSkill: 0.4, passChance: 0.1, strafeAmplitude: 0.5,
    catchAttemptProbability: 0.3, catchTimingSigma: 0.12, dodgeSkill: 0.35, maneuverTimingSigma: 0.1,
    abilityUsageFactor: 0.55, abilityRetryInterval: 2.5, abilityFailBackoff: 5,
    preferredDepth: 5.5, holderAvoidDistance: 7, teammateSpacing: 2.5,
  }),
  normal: Object.freeze({ ...NORMAL }),
  hard: Object.freeze({
    ...NORMAL,
    reactionTime: 0.20, reactionJitter: 0.16, rearReactionMul: 1.6, rearBlindChance: 0.2, fieldOfView: 115,
    dangerSenseReactionMul: 0.4, cloakDetectionRadius: 3.5, obscuredMistargetChance: 0.4,
    decisionInterval: 0.22, decisionJitter: 0.25, hysteresis: 0.12, minBehaviourDwell: 0.3, aggression: 0.65,
    aimErrorDeg: 2.2, leadAccuracy: 0.93, throwHesitationMin: 0.2, throwHesitationMax: 0.5, opportunismSkill: 0.6,
    targetSelectionSkill: 0.85, passChance: 0.35, strafeAmplitude: 1.3,
    catchAttemptProbability: 0.68, catchTimingSigma: 0.05, dodgeSkill: 0.75, maneuverTimingSigma: 0.045,
    abilityUsageFactor: 1.0, abilityRetryInterval: 1.2, abilityFailBackoff: 3.5,
    preferredDepth: 4.5, holderAvoidDistance: 9, teammateSpacing: 3.2,
  }),
  pro: Object.freeze({
    ...NORMAL,
    reactionTime: 0.12, reactionJitter: 0.10, rearReactionMul: 1.4, rearBlindChance: 0.1, fieldOfView: 125,
    dangerSenseReactionMul: 0.35, cloakDetectionRadius: 4.5, obscuredMistargetChance: 0.3,
    decisionInterval: 0.15, decisionJitter: 0.2, hysteresis: 0.1, minBehaviourDwell: 0.25, aggression: 0.75,
    aimErrorDeg: 1.0, leadAccuracy: 1.0, throwHesitationMin: 0.1, throwHesitationMax: 0.3, opportunismSkill: 0.85,
    targetSelectionSkill: 0.95, passChance: 0.45, strafeAmplitude: 1.6,
    catchAttemptProbability: 0.85, catchTimingSigma: 0.03, dodgeSkill: 0.92, maneuverTimingSigma: 0.03,
    abilityUsageFactor: 1.15, abilityRetryInterval: 0.8, abilityFailBackoff: 3,
    preferredDepth: 4.2, holderAvoidDistance: 9.5, teammateSpacing: 3.4,
  }),
});

/** Canonical difficulty id ('Hard', 'PRO ', unknown -> 'normal'). */
export function normalizeDifficulty(difficulty) {
  const id = typeof difficulty === 'string' ? difficulty.trim().toLowerCase() : '';
  return DIFFICULTY_IDS.includes(id) ? id : 'normal';
}

/**
 * Mutable copy of a preset. `difficulty` may also be a partial profile object (custom tuning) which is layered over
 * the preset named by its `base` field (default normal).
 * @param {string|object} difficulty
 * @returns {BotProfile}
 */
export function createProfile(difficulty) {
  if (difficulty && typeof difficulty === 'object') {
    const base = BOT_DIFFICULTY[normalizeDifficulty(difficulty.base)];
    const out = { ...base };
    for (const k of Object.keys(base)) if (typeof difficulty[k] === 'number' && Number.isFinite(difficulty[k])) out[k] = difficulty[k];
    return out;
  }
  return { ...BOT_DIFFICULTY[normalizeDifficulty(difficulty)] };
}

/**
 * Hero-specific biases layered on top of the difficulty profile so each bot plays its kit.
 * @typedef {object} HeroTraits
 * @property {number} chargeTimeMin  0 = use the profile range (Rayne's Overcharge holds up to 2 s)
 * @property {number} chargeTimeMax
 * @property {number} catchBias      added to the catch attempt probability
 * @property {number} dodgeBias      added to the dodge skill
 * @property {number} aggressionBias added to the aggression
 * @property {number} passBias       added to the pass chance
 * @property {boolean} hasDangerSense reacts to EV.DangerSense (Specter)
 */
const NO_TRAITS = Object.freeze({
  chargeTimeMin: 0, chargeTimeMax: 0, catchBias: 0, dodgeBias: 0, aggressionBias: 0, passBias: 0, hasDangerSense: false,
});

export const HERO_TRAITS = Object.freeze({
  // Overcharge: +50% velocity / +20% radius over 2 s of charge -> hold longer, especially at range.
  Rayne: Object.freeze({ ...NO_TRAITS, chargeTimeMin: 0.6, chargeTimeMax: 2.0, aggressionBias: 0.1 }),
  Shadow: Object.freeze({ ...NO_TRAITS, aggressionBias: 0.1 }),
  // Assassin: pushes forward (stealth throws gain +30% speed).
  Gale: Object.freeze({ ...NO_TRAITS, aggressionBias: 0.15, dodgeBias: 0.05 }),
  // Iron Mitts: 0.225 s perfect window -> much more willing to catch.
  Bear: Object.freeze({ ...NO_TRAITS, catchBias: 0.25, aggressionBias: -0.05 }),
  // Thick Hide: survives a hit -> brawls near the line and risks catches.
  Gouki: Object.freeze({ ...NO_TRAITS, catchBias: 0.1, aggressionBias: 0.2, dodgeBias: -0.1 }),
  Screws: NO_TRAITS,
  // Hat Trick: passes teleport straight into the teammate's hands.
  Houdini: Object.freeze({ ...NO_TRAITS, passBias: 0.35 }),
  Elsa: Object.freeze({ ...NO_TRAITS, passBias: 0.05 }),
  // Danger Sense: reacts faster to locked-on fastballs.
  Specter: Object.freeze({ ...NO_TRAITS, dodgeBias: 0.1, hasDangerSense: true }),
  Chrono: Object.freeze({ ...NO_TRAITS, catchBias: 0.05 }),
});

/** @returns {HeroTraits} */
export function heroTraits(heroId) { return HERO_TRAITS[heroId] || NO_TRAITS; }
