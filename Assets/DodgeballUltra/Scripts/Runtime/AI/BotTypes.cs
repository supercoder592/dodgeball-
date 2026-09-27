using DodgeballUltra.Combat;
using UnityEngine;

// CONTRACT FILE (kernel). Shared AI types. Do not rename or remove members; additions are fine.
namespace DodgeballUltra.AI
{
    /// <summary>Skill tier of a bot. Each tier maps to a <see cref="BotDifficultyProfile"/> preset.</summary>
    public enum BotDifficulty
    {
        Easy = 0,
        Normal = 1,
        Hard = 2,
        Pro = 3,
    }

    /// <summary>
    /// High-level behaviour a <see cref="BotBrain"/> is currently executing. Picked by utility scoring with hysteresis on
    /// every decision tick; <see cref="ThreatResponse"/> pre-empts everything as soon as an incoming ball is perceived.
    /// </summary>
    public enum BotBehaviour
    {
        /// <summary>Match not live / cannot act. Produces a neutral intent.</summary>
        Idle = 0,
        /// <summary>Reacting to an incoming enemy ball (catch or dodge).</summary>
        ThreatResponse,
        /// <summary>Running to a Free / Stasis ball to pick it up (includes the opening rush).</summary>
        Retrieve,
        /// <summary>Holding a ball: approach, strafe, charge and throw at the chosen target.</summary>
        Attack,
        /// <summary>Holding a ball and passing it to a better-positioned teammate.</summary>
        Pass,
        /// <summary>No ball to chase: spread out, keep away from ball holders, stay inside the confinement.</summary>
        Position,
    }

    /// <summary>How a bot answers one perceived incoming ball.</summary>
    public enum BotThreatResponse
    {
        None = 0,
        /// <summary>Stand in, face the ball and press Catch at the planned lead time.</summary>
        Catch,
        /// <summary>Step perpendicular to the ball path.</summary>
        Sidestep,
        /// <summary>Jump over a low ball.</summary>
        Jump,
        /// <summary>Slide under a chest/head-height ball while sprinting.</summary>
        Slide,
        /// <summary>No viable answer (rooted, too late, invulnerable): keep facing the ball and take it.</summary>
        Brace,
    }

    /// <summary>Progress of the human-like throw button sequence (press -> hold for the charge time -> release).</summary>
    public enum BotThrowPhase
    {
        None = 0,
        /// <summary>The next sampled intent sends ThrowPressed + ThrowHeld.</summary>
        Pressing,
        /// <summary>ThrowHeld is sent every frame until the planned charge time is reached.</summary>
        Holding,
    }

    /// <summary>A perceived incoming ball that is predicted to hit the bot.</summary>
    public struct BotThreat
    {
        public DodgeBall Ball;
        /// <summary>DodgeBall.LaunchTime of the throw this threat belongs to (identifies re-launches of the same ball).</summary>
        public float LaunchTime;
        /// <summary>Predicted seconds until the ball touches the bot's body capsule.</summary>
        public float TimeToImpact;
        /// <summary>Predicted contact point on the body capsule.</summary>
        public Vector3 ImpactPoint;
        /// <summary>Predicted seconds until the ball enters the catch zone around the chest (the moment catch timing is judged).</summary>
        public float CatchZoneTime;
        /// <summary>Ball came from outside the bot's field of view (outfield throws at its back).</summary>
        public bool FromBehind;
        public float SpeedKmh;
        /// <summary>Time.time at which the bot became aware of the ball (launch + reaction time).</summary>
        public float NoticedAt;

        public bool IsValid => Ball != null;
    }

    /// <summary>Utility scores of the last decision tick (debugging, HUD overlays, tests).</summary>
    public struct BotDecisionScores
    {
        public float ThreatResponse;
        public float Retrieve;
        public float Attack;
        public float Pass;
        public float Position;
        public float SkillUtility;
        public float UltimateUtility;
    }
}
