using DodgeballUltra.Abilities;
using DodgeballUltra.Combat;
using DodgeballUltra.Core;
using DodgeballUltra.Match;
using DodgeballUltra.Player;
using UnityEngine;

// -----------------------------------------------------------------------------------------------
// Every gameplay fact that other systems may react to. Publishers: Combat / Player / Abilities /
// Match. Observers: JuiceManager, HUD, AudioManager, VfxManager, AI, abilities (passives).
// Keep these as plain data. Never put behaviour in an event.
// -----------------------------------------------------------------------------------------------
namespace DodgeballUltra.Events
{
    // ---------------------------------------------------------------- Match flow

    /// <summary>A new match begins (after hero select).</summary>
    public struct MatchStartedEvent : IGameEvent
    {
        public int RoundsToWin;
        public int PlayersPerTeam;
    }

    /// <summary>Pre-round countdown tick (3, 2, 1). <see cref="SecondsLeft"/> is the whole number being shown.</summary>
    public struct RoundCountdownEvent : IGameEvent
    {
        public int Round;
        public int SecondsLeft;
    }

    /// <summary>Balls are live, players may move. ("Opening rush")</summary>
    public struct RoundStartedEvent : IGameEvent
    {
        public int Round;
        public float Duration;
    }

    public enum RoundEndReason
    {
        AllEliminated,
        TimeUp,
        Draw,
        Forfeit,
    }

    public struct RoundEndedEvent : IGameEvent
    {
        public int Round;
        public TeamId Winner; // None for a draw
        public RoundEndReason Reason;
        public int HomeScore;
        public int AwayScore;
    }

    public struct MatchEndedEvent : IGameEvent
    {
        public TeamId Winner;
        public int HomeScore;
        public int AwayScore;
    }

    public struct MatchPhaseChangedEvent : IGameEvent
    {
        public MatchPhase Previous;
        public MatchPhase Current;
    }

    // ---------------------------------------------------------------- Players

    public struct PlayerSpawnedEvent : IGameEvent
    {
        public DodgeballPlayer Player;
    }

    public struct PlayerStateChangedEvent : IGameEvent
    {
        public DodgeballPlayer Player;
        public PlayerStateId Previous;
        public PlayerStateId Current;
    }

    public struct PlayerDamagedEvent : IGameEvent
    {
        public DodgeballPlayer Player;
        public DodgeballPlayer Attacker; // may be null (ability/environment)
        public float Damage;
        public float RemainingHp;
        public Vector3 Point;
    }

    /// <summary>A player lost all HP (or was force-eliminated) and is being sent to the outfield.</summary>
    public struct PlayerEliminatedEvent : IGameEvent
    {
        public DodgeballPlayer Player;
        public DodgeballPlayer Attacker; // may be null
        public EliminationCause Cause;
        public Vector3 Impulse; // world-space impulse used for the ragdoll transition
        public Vector3 Point;
    }

    /// <summary>An outfield player returned to the infield.</summary>
    public struct PlayerRevivedEvent : IGameEvent
    {
        public DodgeballPlayer Player;
        public DodgeballPlayer Reviver; // e.g. the perfect catcher; may be null
        public RevivalCause Cause;
    }

    public struct PlayerZoneChangedEvent : IGameEvent
    {
        public DodgeballPlayer Player;
        public CourtZone Zone;
    }

    public struct StatusEffectChangedEvent : IGameEvent
    {
        public DodgeballPlayer Player;
        public StatusEffectType Type;
        public bool Applied; // false = removed / expired
        public float Duration;
        public float Magnitude;
    }

    // ---------------------------------------------------------------- Balls & combat

    public struct BallPickedUpEvent : IGameEvent
    {
        public DodgeBall Ball;
        public DodgeballPlayer Player;
    }

    public struct ThrowChargeStartedEvent : IGameEvent
    {
        public DodgeballPlayer Player;
        public DodgeBall Ball;
    }

    public struct BallThrownEvent : IGameEvent
    {
        public DodgeBall Ball;
        public DodgeballPlayer Thrower;
        public DodgeballPlayer Target; // locked target, may be null
        public Vector3 Origin;
        public Vector3 Velocity;
        public float SpeedKmh;
        public int RallyCount;
        public float ChargeNormalized;
        public bool IsAbilityThrow;
        public bool IsPass;
        public bool IsCounterThrow; // boosted by a perfect catch
    }

    /// <summary>A live ball struck a player (after hit filters ran). Triggers the Juice pipeline.</summary>
    public struct BallHitPlayerEvent : IGameEvent
    {
        public DodgeBall Ball;
        public DodgeballPlayer Attacker;
        public DodgeballPlayer Victim;
        public Vector3 Point;
        public Vector3 Normal;
        public Vector3 BallVelocity;
        public float SpeedKmh;
        public float Damage;
        public HitOutcome Outcome;
        public bool IsLocalPlayerInvolved;
    }

    /// <summary>Catch button pressed (the timing window starts now).</summary>
    public struct CatchAttemptEvent : IGameEvent
    {
        public DodgeballPlayer Player;
    }

    /// <summary>The catch window closed without a ball arriving.</summary>
    public struct CatchWhiffEvent : IGameEvent
    {
        public DodgeballPlayer Player;
    }

    /// <summary>A live ball was caught. Perfect catches trigger the Juice pipeline and the Perfect Catch rewards.</summary>
    public struct BallCaughtEvent : IGameEvent
    {
        public DodgeBall Ball;
        public DodgeballPlayer Catcher;
        public DodgeballPlayer Thrower;
        public CatchQuality Quality;
        public float SecondsBeforeImpact; // t_input measured by the catch timing rule
        public Vector3 Point;
        public float SpeedKmh;
        public int RallyCount;
        public bool IsLocalPlayerInvolved;
    }

    public struct BallPassedEvent : IGameEvent
    {
        public DodgeBall Ball;
        public DodgeballPlayer From;
        public DodgeballPlayer To;
        public bool Teleported; // Houdini's Hat Trick
    }

    /// <summary>A ball touched the court / a wall. <see cref="RallyReset"/> is true when it touched the floor.</summary>
    public struct BallBouncedEvent : IGameEvent
    {
        public DodgeBall Ball;
        public Vector3 Point;
        public Vector3 Normal;
        public float ImpactSpeed;
        public bool HitFloor;
        public bool RallyReset;
        public bool EndedLive;
    }

    /// <summary>A live ball was stopped by something that is not a player (shield, turret, clone, deflect).</summary>
    public struct BallBlockedEvent : IGameEvent
    {
        public DodgeBall Ball;
        public Component Blocker;
        public Vector3 Point;
        public Vector3 Normal;
    }

    /// <summary>A ball left the arena and will be respawned.</summary>
    public struct BallOutOfBoundsEvent : IGameEvent
    {
        public DodgeBall Ball;
        public Vector3 LastPosition;
    }

    /// <summary>
    /// A fast ball is locked on to <see cref="Player"/> (Specter's Danger Sense and the HUD's red screen-edge flash).
    /// Published every time a qualifying throw is made; <see cref="Active"/>=false when the threat has passed.
    /// </summary>
    public struct DangerSenseEvent : IGameEvent
    {
        public DodgeballPlayer Player;
        public DodgeBall Ball;
        public float TimeToImpact;
        public float SpeedKmh;
        public bool Active;
    }

    // ---------------------------------------------------------------- Abilities

    public struct AbilityCastEvent : IGameEvent
    {
        public DodgeballPlayer Player;
        public AbilityBase Ability;
        public AbilitySlot Slot;
    }

    public struct AbilityEndedEvent : IGameEvent
    {
        public DodgeballPlayer Player;
        public AbilityBase Ability;
        public AbilitySlot Slot;
        public bool Interrupted;
    }

    public struct AbilityFailedEvent : IGameEvent
    {
        public DodgeballPlayer Player;
        public AbilityBase Ability;
        public AbilitySlot Slot;
        public AbilityFailReason Reason;
    }

    public struct UltimateChargeChangedEvent : IGameEvent
    {
        public DodgeballPlayer Player;
        public float Normalized;
        public bool BecameReady;
    }

    // ---------------------------------------------------------------- Juice

    /// <summary>Raised when a hitstop begins (audio can duck / pitch down, UI can pulse).</summary>
    public struct HitstopEvent : IGameEvent
    {
        public float Duration;
        public float TimeScale;
    }
}
