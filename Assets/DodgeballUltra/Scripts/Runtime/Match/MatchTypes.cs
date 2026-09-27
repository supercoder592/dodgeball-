using DodgeballUltra.AI;
using UnityEngine;

// CONTRACT FILE (kernel).
namespace DodgeballUltra.Match
{
    public enum MatchPhase
    {
        None = 0,
        HeroSelect,
        PreRound,   // players placed, balls at centre, cinematic intro
        Countdown,  // 3-2-1
        Playing,
        RoundEnd,
        MatchEnd,
    }

    /// <summary>What the player chose on the hero select screen.</summary>
    public struct MatchSetup
    {
        public HeroId LocalHero;
        public TeamId LocalTeam;
        /// <summary>Heroes for each slot of each team (length = playersPerTeam). The local player takes slot 0 of LocalTeam.</summary>
        public HeroId[] HomeHeroes;
        public HeroId[] AwayHeroes;
        public BotDifficulty Difficulty;
        /// <summary>True = no human player (attract mode / AI vs AI).</summary>
        public bool Spectate;
    }

    /// <summary>Tunable match rules.</summary>
    [CreateAssetMenu(fileName = "MatchRules", menuName = "Dodgeball Ultra/Match Rules", order = 20)]
    public sealed class MatchRules : ScriptableObject
    {
        [Header("Format")]
        [Min(1)] public int playersPerTeam = 3;
        [Tooltip("Rounds needed to win the match (best of 3 = 2).")]
        [Min(1)] public int roundsToWin = 2;
        [Tooltip("Round length (s). At time-out the team with more infield players (then more total HP) wins.")]
        [Min(10f)] public float roundDuration = 150f;
        [Min(0f)] public float preRoundDuration = 2.5f;
        [Min(0)] public int countdownSeconds = 3;
        [Min(0f)] public float roundEndDuration = 4f;

        [Header("Balls")]
        [Min(1)] public int ballCount = 6;
        [Tooltip("Seconds before a ball that left the arena reappears at the centre line.")]
        [Min(0f)] public float outOfBoundsRespawnDelay = 2f;

        [Header("Rules (Taiwanese 躲避球 style infield / outfield)")]
        [Tooltip("An outfield player who hits an infield enemy returns to the infield.")]
        public bool outfieldHitRevives = true;
        [Tooltip("Classic rule: catching a ball eliminates its thrower. Off by default (Dodgeball Ultra rewards perfect catches instead).")]
        public bool catchEliminatesThrower;
        [Tooltip("Seconds the eliminated player ragdolls before moving to the outfield.")]
        [Min(0.2f)] public float eliminationRagdollTime = 2f;
        [Tooltip("HP fraction restored when returning from the outfield.")]
        [Range(0.1f, 1f)] public float reviveHpFraction = 1f;

        [Header("Ultimate meter (0..1)")]
        public float ultGainPerSecond = 0.008f;
        public float ultGainOnHit = 0.12f;
        public float ultGainOnEliminate = 0.08f;
        public float ultGainOnCatch = 0.06f;
        [Tooltip("Spec: Perfect Catch instantly adds +15% ultimate meter.")]
        public float ultGainOnPerfectCatch = Core.GameConstants.PerfectCatchUltGain;
        public bool keepUltimateBetweenRounds = true;
    }
}
