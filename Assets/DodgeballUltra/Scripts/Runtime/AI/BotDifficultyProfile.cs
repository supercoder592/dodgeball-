using System;
using UnityEngine;

namespace DodgeballUltra.AI
{
    /// <summary>
    /// Every skill knob of a bot. <see cref="CreatePreset"/> returns the tuned values for Easy / Normal / Hard / Pro; a
    /// <see cref="BotBrain"/> can also use a custom profile edited in the Inspector.
    /// <para>
    /// The profile never gives a bot super-human powers: it only changes how fast and how accurately the bot perceives,
    /// decides and presses the same buttons a human presses (the intent goes through the normal player state machine).
    /// </para>
    /// </summary>
    [Serializable]
    public sealed class BotDifficultyProfile
    {
        // ------------------------------------------------------------------ perception
        [Header("Perception")]
        [Tooltip("Mean delay (s) between a ball being thrown and the bot reacting to it. Spec range: 0.45 s (Easy) to 0.12 s (Pro).")]
        [Range(0.05f, 1f)] public float reactionTime = 0.30f;

        [Tooltip("Random +/- fraction applied to every reaction time sample.")]
        [Range(0f, 0.6f)] public float reactionJitter = 0.22f;

        [Tooltip("Reaction multiplier for balls thrown from behind / outside the field of view (outfield throws at the back).")]
        [Range(1f, 3f)] public float rearReactionMultiplier = 1.8f;

        [Tooltip("Chance to not notice a ball thrown from behind at all until it is within the peripheral radius.")]
        [Range(0f, 1f)] public float rearBlindChance = 0.35f;

        [Tooltip("Half-angle (deg) of the bot's field of view. Balls thrown from outside it count as 'from behind'.")]
        [Range(45f, 180f)] public float fieldOfView = 105f;

        [Tooltip("Reaction multiplier while Specter's Danger Sense warns the bot about a locked-on fastball.")]
        [Range(0.1f, 1f)] public float dangerSenseReactionMultiplier = 0.45f;

        [Tooltip("Distance (m) inside which a cloaked enemy (Gale's Optical Camouflage) is still noticed.")]
        [Min(0f)] public float cloakDetectionRadius = 2.5f;

        [Tooltip("Chance to aim at a decoy position near an Obscured enemy (Shadow's Mirage Formation / Night Parade clones).")]
        [Range(0f, 1f)] public float obscuredMistargetChance = 0.5f;

        // ------------------------------------------------------------------ decisions
        [Header("Decision making")]
        [Tooltip("Seconds between two utility decisions (threat reactions are event-driven and not bound to this).")]
        [Range(0.05f, 1f)] public float decisionInterval = 0.32f;

        [Tooltip("Random +/- fraction applied to each decision interval so bots do not act in lock-step.")]
        [Range(0f, 0.6f)] public float decisionJitter = 0.3f;

        [Tooltip("Score bonus of the current behaviour / target / ball. Prevents flip-flopping between near-equal options.")]
        [Range(0f, 0.6f)] public float hysteresis = 0.15f;

        [Tooltip("Minimum seconds a non-threat behaviour is kept before another one may replace it.")]
        [Range(0f, 1.5f)] public float minBehaviourDwell = 0.35f;

        [Tooltip("0 = cautious (stays deep, avoids the centre line), 1 = reckless (lives on the centre line).")]
        [Range(0f, 1f)] public float aggression = 0.5f;

        // ------------------------------------------------------------------ throwing
        [Header("Throwing")]
        [Tooltip("Standard deviation (deg) of the angular aim error applied to the lead point at release.")]
        [Range(0f, 15f)] public float aimErrorDegrees = 4f;

        [Tooltip("Fraction of the ideal lead (intercept point) actually applied: 0 = aims at the current chest, 1 = perfect lead.")]
        [Range(0f, 1f)] public float leadAccuracy = 0.8f;

        [Tooltip("Charge time range (s) for a standard throw. Short for close targets, long for distant ones.")]
        public Vector2 chargeTimeRange = new Vector2(0.2f, 1.2f);

        [Tooltip("Seconds the bot waits after gaining a ball before it starts charging (human 'settle' time).")]
        public Vector2 throwHesitation = new Vector2(0.35f, 0.8f);

        [Tooltip("Chance to release early when the target commits (jumps, charges, gets stunned).")]
        [Range(0f, 1f)] public float opportunismSkill = 0.35f;

        [Tooltip("Chance to pick the best-scored target instead of a random acceptable one.")]
        [Range(0f, 1f)] public float targetSelectionSkill = 0.65f;

        [Tooltip("Chance per possession to consider passing to a better-positioned teammate.")]
        [Range(0f, 1f)] public float passChance = 0.25f;

        [Tooltip("Amplitude (m) of the unpredictable lateral strafing while attacking.")]
        [Range(0f, 3f)] public float strafeAmplitude = 0.9f;

        // ------------------------------------------------------------------ catching
        [Header("Catching")]
        [Tooltip("Base chance to try catching an incoming ball (hands free, ball catchable) instead of dodging.")]
        [Range(0f, 1f)] public float catchAttemptProbability = 0.5f;

        [Tooltip("Ideal seconds before impact to press Catch (inside the 0.15 s perfect window). Scaled by Iron Mitts.")]
        [Range(0f, 0.15f)] public float idealCatchLead = 0.08f;

        [Tooltip("Standard deviation (s) of the catch press around the ideal moment. Spec: 0.12 s (Easy) to 0.03 s (Pro).")]
        [Range(0f, 0.3f)] public float catchTimingSigma = 0.08f;

        // ------------------------------------------------------------------ dodging
        [Header("Dodging")]
        [Tooltip("0..1: picks the best evasive manoeuvre, the correct side and executes it without hesitation.")]
        [Range(0f, 1f)] public float dodgeSkill = 0.55f;

        [Tooltip("Standard deviation (s) of the jump / slide press around its ideal moment.")]
        [Range(0f, 0.2f)] public float maneuverTimingSigma = 0.06f;

        // ------------------------------------------------------------------ abilities
        [Header("Abilities")]
        [Tooltip("Multiplier on AbilityBase.EvaluateAIUtility (how eagerly the bot uses its skill / ultimate).")]
        [Range(0f, 2f)] public float abilityUsageFactor = 0.8f;

        [Tooltip("Utility (after the usage factor) required to press Skill / Ultimate.")]
        [Range(0f, 1f)] public float abilityUtilityThreshold = 0.45f;

        [Tooltip("Minimum seconds between two presses of the same ability button.")]
        [Min(0f)] public float abilityRetryInterval = 1.8f;

        [Tooltip("Seconds the bot leaves an ability alone after it failed to activate (never spams failing abilities).")]
        [Min(0f)] public float abilityFailBackoff = 4f;

        // ------------------------------------------------------------------ positioning
        [Header("Positioning")]
        [Tooltip("Preferred distance (m) from the centre line when not attacking (infield).")]
        [Range(1f, 8.5f)] public float preferredDepth = 5f;

        [Tooltip("Distance (m) the bot tries to keep from enemies that hold a ball.")]
        [Range(2f, 14f)] public float holderAvoidDistance = 8f;

        [Tooltip("Distance (m) the bot tries to keep from teammates (spreading makes double kills harder).")]
        [Range(1f, 6f)] public float teammateSpacing = 3f;

        /// <summary>Deep copy (the profile only holds value types).</summary>
        public BotDifficultyProfile Clone() => (BotDifficultyProfile)MemberwiseClone();

        /// <summary>Tuned preset for <paramref name="difficulty"/>.</summary>
        public static BotDifficultyProfile CreatePreset(BotDifficulty difficulty)
        {
            switch (difficulty)
            {
                case BotDifficulty.Easy:
                    return new BotDifficultyProfile
                    {
                        reactionTime = 0.45f, reactionJitter = 0.30f, rearReactionMultiplier = 2.0f, rearBlindChance = 0.5f,
                        fieldOfView = 95f, dangerSenseReactionMultiplier = 0.5f, cloakDetectionRadius = 1.5f,
                        obscuredMistargetChance = 0.65f,
                        decisionInterval = 0.45f, decisionJitter = 0.35f, hysteresis = 0.2f, minBehaviourDwell = 0.5f,
                        aggression = 0.35f,
                        aimErrorDegrees = 6.5f, leadAccuracy = 0.55f, chargeTimeRange = new Vector2(0.2f, 1.2f),
                        throwHesitation = new Vector2(0.6f, 1.3f), opportunismSkill = 0.1f, targetSelectionSkill = 0.4f,
                        passChance = 0.1f, strafeAmplitude = 0.5f,
                        catchAttemptProbability = 0.3f, idealCatchLead = 0.08f, catchTimingSigma = 0.12f,
                        dodgeSkill = 0.35f, maneuverTimingSigma = 0.1f,
                        abilityUsageFactor = 0.55f, abilityUtilityThreshold = 0.45f, abilityRetryInterval = 2.5f,
                        abilityFailBackoff = 5f,
                        preferredDepth = 5.5f, holderAvoidDistance = 7f, teammateSpacing = 2.5f,
                    };

                case BotDifficulty.Hard:
                    return new BotDifficultyProfile
                    {
                        reactionTime = 0.20f, reactionJitter = 0.16f, rearReactionMultiplier = 1.6f, rearBlindChance = 0.2f,
                        fieldOfView = 115f, dangerSenseReactionMultiplier = 0.4f, cloakDetectionRadius = 3.5f,
                        obscuredMistargetChance = 0.4f,
                        decisionInterval = 0.22f, decisionJitter = 0.25f, hysteresis = 0.12f, minBehaviourDwell = 0.3f,
                        aggression = 0.65f,
                        aimErrorDegrees = 2.2f, leadAccuracy = 0.93f, chargeTimeRange = new Vector2(0.2f, 1.2f),
                        throwHesitation = new Vector2(0.2f, 0.5f), opportunismSkill = 0.6f, targetSelectionSkill = 0.85f,
                        passChance = 0.35f, strafeAmplitude = 1.3f,
                        catchAttemptProbability = 0.68f, idealCatchLead = 0.08f, catchTimingSigma = 0.05f,
                        dodgeSkill = 0.75f, maneuverTimingSigma = 0.045f,
                        abilityUsageFactor = 1f, abilityUtilityThreshold = 0.45f, abilityRetryInterval = 1.2f,
                        abilityFailBackoff = 3.5f,
                        preferredDepth = 4.5f, holderAvoidDistance = 9f, teammateSpacing = 3.2f,
                    };

                case BotDifficulty.Pro:
                    return new BotDifficultyProfile
                    {
                        reactionTime = 0.12f, reactionJitter = 0.10f, rearReactionMultiplier = 1.4f, rearBlindChance = 0.1f,
                        fieldOfView = 125f, dangerSenseReactionMultiplier = 0.35f, cloakDetectionRadius = 4.5f,
                        obscuredMistargetChance = 0.3f,
                        decisionInterval = 0.15f, decisionJitter = 0.2f, hysteresis = 0.1f, minBehaviourDwell = 0.25f,
                        aggression = 0.75f,
                        aimErrorDegrees = 1f, leadAccuracy = 1f, chargeTimeRange = new Vector2(0.2f, 1.2f),
                        throwHesitation = new Vector2(0.1f, 0.3f), opportunismSkill = 0.85f, targetSelectionSkill = 0.95f,
                        passChance = 0.45f, strafeAmplitude = 1.6f,
                        catchAttemptProbability = 0.85f, idealCatchLead = 0.08f, catchTimingSigma = 0.03f,
                        dodgeSkill = 0.92f, maneuverTimingSigma = 0.03f,
                        abilityUsageFactor = 1.15f, abilityUtilityThreshold = 0.45f, abilityRetryInterval = 0.8f,
                        abilityFailBackoff = 3f,
                        preferredDepth = 4.2f, holderAvoidDistance = 9.5f, teammateSpacing = 3.4f,
                    };

                default: // Normal (field initialisers)
                    return new BotDifficultyProfile();
            }
        }
    }
}
