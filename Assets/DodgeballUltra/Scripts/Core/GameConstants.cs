// -----------------------------------------------------------------------------
// Dodgeball Ultra - Core rules layer (engine-agnostic, no UnityEngine references)
// -----------------------------------------------------------------------------
// Everything in DodgeballUltra.Core is pure C# so it can be unit-tested outside
// Unity (see Tools/CoreTests) and re-used by a future dedicated server.
// -----------------------------------------------------------------------------
namespace DodgeballUltra.Core
{
    /// <summary>
    /// Design constants taken directly from the Dodgeball Ultra game design document.
    /// Runtime tuning lives in ScriptableObjects; these are the *spec* defaults those
    /// assets are initialised with and the hard caps the rules layer enforces.
    /// </summary>
    public static class GameConstants
    {
        /// <summary>Multiply km/h by this to get m/s.</summary>
        public const float KmhToMs = 1f / 3.6f;

        /// <summary>Multiply m/s by this to get km/h.</summary>
        public const float MsToKmh = 3.6f;

        /// <summary>Absolute ball speed cap from the Rally Boost spec: 220 km/h.</summary>
        public const float MaxBallSpeedKmh = 220f;

        /// <summary>220 km/h expressed in m/s (~61.11 m/s).</summary>
        public const float MaxBallSpeedMs = MaxBallSpeedKmh * KmhToMs;

        /// <summary>Rally Boost: +10% velocity per consecutive rally (catch + re-throw without touching the floor).</summary>
        public const float RallyBoostPerCount = 0.10f;

        /// <summary>Perfect Catch window: input must land 0..0.15 s before impact.</summary>
        public const float PerfectCatchWindow = 0.15f;

        /// <summary>Bear's [Iron Mitts] passive: +50% perfect window (0.225 s).</summary>
        public const float IronMittsWindowMultiplier = 1.5f;

        /// <summary>A regular (non-perfect) catch is accepted up to this many seconds before impact.</summary>
        public const float NormalCatchWindow = 0.40f;

        /// <summary>Perfect Catch reward: instantly add 15% ultimate meter.</summary>
        public const float PerfectCatchUltGain = 0.15f;

        /// <summary>Perfect Catch reward: the counter-throw is 20% faster.</summary>
        public const float PerfectCatchCounterBoost = 0.20f;

        /// <summary>Standard HP for most heroes.</summary>
        public const int DefaultMaxHp = 100;

        /// <summary>Gouki's [Thick Hide]: survives two standard hits.</summary>
        public const int ThickHideMaxHp = 200;

        /// <summary>Damage dealt by one standard ball hit (one-hit elimination for 100 HP heroes).</summary>
        public const int StandardHitDamage = 100;

        /// <summary>Hitstop duration range from the Juice spec (seconds, unscaled).</summary>
        public const float HitstopMin = 0.03f;
        public const float HitstopMax = 0.10f;

        /// <summary>Hit-flash duration from the Juice spec (seconds, unscaled).</summary>
        public const float HitFlashDuration = 0.05f;

        /// <summary>Standard gravity used by the ballistic solvers (m/s^2, positive magnitude).</summary>
        public const float Gravity = 9.81f;

        /// <summary>Regulation foam dodgeball radius (8.25" diameter) in metres.</summary>
        public const float BallRadius = 0.105f;
    }
}
