using DodgeballUltra.Combat;
using DodgeballUltra.Player;

// CONTRACT FILE (kernel). Shared ability types.
namespace DodgeballUltra.Abilities
{
    public enum AbilitySlot
    {
        Passive = 0,
        Skill = 1,
        Ultimate = 2,
    }

    public enum AbilityPhase
    {
        /// <summary>Can be activated.</summary>
        Ready = 0,
        /// <summary>Channelling (castTime &gt; 0). Interruptible.</summary>
        Casting,
        /// <summary>Effect running (duration &gt; 0, or held active by the ability).</summary>
        Active,
        /// <summary>Waiting for the cooldown.</summary>
        Cooldown,
        /// <summary>Passives, or abilities disabled by the match.</summary>
        Disabled,
    }

    public enum AbilityFailReason
    {
        None = 0,
        OnCooldown,
        UltimateNotCharged,
        CannotAct,
        Silenced,
        RequiresBall,
        NoTarget,
        NotInfield,
        AlreadyActive,
        MatchNotPlaying,
        IsPassive,
        Custom,
    }

    public enum InterruptReason
    {
        Stunned = 0,
        Eliminated,
        Frozen,
        RoundEnded,
        Cancelled,
        Replaced,
    }

    /// <summary>Why ultimate charge was gained (lets balancing and the HUD distinguish sources).</summary>
    public enum UltGainReason
    {
        Passive = 0,
        HitLanded,
        Catch,
        PerfectCatch,
        Ability,
        Debug,
    }

    /// <summary>Default heuristics the AI uses when an ability does not override EvaluateAIUtility.</summary>
    public enum AbilityAIHint
    {
        Never = 0,
        Anytime,
        WhenHoldingBall,
        WhenThreatened,
        WhenEnemyInRange,
        WhenTeammateOutfield,
        WhenBallsLoose,
        WhenLosing,
    }

    /// <summary>Snapshot of the situation an AI bot evaluates abilities against.</summary>
    public struct AbilityAIContext
    {
        public DodgeballPlayer Self;
        public DodgeballPlayer NearestEnemy;
        public float NearestEnemyDistance;
        public DodgeBall IncomingBall;          // most dangerous live enemy ball, may be null
        public float IncomingTimeToImpact;      // +inf when none
        public int TeammatesInOutfield;
        public int EnemiesInfield;
        public int AlliesInfield;
        public bool HoldingBall;
        public int FreeBallsNearby;
        public float UltimateCharge;            // 0..1
        public float RoundTimeRemaining;
    }
}
