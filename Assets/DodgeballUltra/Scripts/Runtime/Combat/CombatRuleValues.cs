using DodgeballUltra.Core;
using DodgeballUltra.Match;

namespace DodgeballUltra.Combat
{
    /// <summary>
    /// Null-safe access to the match rules the combat code needs (<see cref="MatchRules"/> via
    /// <see cref="MatchManager.Instance"/>), with spec fallbacks for sandbox scenes and tests that have no match running.
    /// <para>
    /// Reward ownership: when a <see cref="MatchManager"/> exists it grants the "play" rewards itself from the published
    /// events (ultimate for landed hits and normal catches, the optional catch-eliminates-thrower rule, and it
    /// de-duplicates the perfect-catch revive). Combat always grants the Perfect Catch rewards that belong to the catcher
    /// (+15 % ultimate, +20 % counter throw, the revive call) and only falls back to granting play rewards when no match
    /// manager is present (<see cref="MatchOwnsPlayRewards"/>), so nothing is ever awarded twice.
    /// </para>
    /// </summary>
    public static class CombatRuleValues
    {
        /// <summary>MatchRules default for a landed hit.</summary>
        public const float DefaultUltGainOnHit = 0.12f;

        /// <summary>MatchRules default bonus for an elimination.</summary>
        public const float DefaultUltGainOnEliminate = 0.08f;

        /// <summary>MatchRules default for a normal (non-perfect) catch.</summary>
        public const float DefaultUltGainOnCatch = 0.06f;

        /// <summary>Seconds before a ball that left the arena reappears (MatchRules default).</summary>
        public const float DefaultOutOfBoundsRespawnDelay = 2f;

        /// <summary>Active rules or null.</summary>
        public static MatchRules Rules
        {
            get
            {
                var match = MatchManager.Instance;
                return match != null ? match.Rules : null;
            }
        }

        /// <summary>True when the MatchManager awards hit/catch ultimate charge from the events (combat must not).</summary>
        public static bool MatchOwnsPlayRewards => MatchManager.Instance != null;

        public static float UltGainOnHit
        {
            get
            {
                var r = Rules;
                return r != null ? r.ultGainOnHit : DefaultUltGainOnHit;
            }
        }

        public static float UltGainOnEliminate
        {
            get
            {
                var r = Rules;
                return r != null ? r.ultGainOnEliminate : DefaultUltGainOnEliminate;
            }
        }

        public static float UltGainOnCatch
        {
            get
            {
                var r = Rules;
                return r != null ? r.ultGainOnCatch : DefaultUltGainOnCatch;
            }
        }

        /// <summary>Spec: a Perfect Catch instantly adds +15 % ultimate meter.</summary>
        public static float UltGainOnPerfectCatch
        {
            get
            {
                var r = Rules;
                return r != null ? r.ultGainOnPerfectCatch : GameConstants.PerfectCatchUltGain;
            }
        }

        public static float OutOfBoundsRespawnDelay
        {
            get
            {
                var r = Rules;
                return r != null ? r.outOfBoundsRespawnDelay : DefaultOutOfBoundsRespawnDelay;
            }
        }

        /// <summary>True while a round is being played (or when there is no match manager at all: sandbox).</summary>
        public static bool IsLivePlay
        {
            get
            {
                var match = MatchManager.Instance;
                return match == null || match.IsPlaying;
            }
        }
    }
}
