using System;
using DodgeballUltra.Core;
using DodgeballUltra.Player;
using UnityEngine;

// CONTRACT FILE (kernel). Shared combat types.
namespace DodgeballUltra.Combat
{
    public enum BallState
    {
        /// <summary>On the court (rolling or resting). Can be picked up. Cannot hit anybody.</summary>
        Free = 0,
        /// <summary>In a player's hand.</summary>
        Held,
        /// <summary>Thrown and not yet touched the floor/wall: can hit or be caught.</summary>
        Live,
        /// <summary>Frozen mid-air by Chrono's Stasis Field. Any player may snatch it.</summary>
        Stasis,
        /// <summary>Temporarily removed from play (Houdini's Grand Vanish / out of bounds / pooled).</summary>
        Despawned,
    }

    /// <summary>Visual + behavioural flavour of a ball (match balls are <see cref="Standard"/>).</summary>
    public enum BallStyle
    {
        Standard = 0,
        Meteor,     // Rayne: fire-infused fastball
        Beam,       // Rayne ult: unblockable piercing beam-ball
        Glue,       // Screws: viscous ball
        Freeze,     // Elsa: freezing ball
        Turret,     // Screws' turret shots
    }

    /// <summary>How a non-player <see cref="IBallHittable"/> responds to a live ball.</summary>
    public enum BallHitResponse
    {
        /// <summary>Not affected; the sweep continues past this collider.</summary>
        PassThrough = 0,
        /// <summary>Ball bounces off and stops being live (shields, turret body).</summary>
        Block,
        /// <summary>Ball is stopped dead at the contact point and drops (clones popping, magnetic catch).</summary>
        Absorb,
        /// <summary>The hittable handled the ball completely itself (e.g. re-launched it). The ball's sweep stops.</summary>
        Handled,
    }

    /// <summary>
    /// Everything that defines one throw. Built by <see cref="PlayerCombatController.BuildThrowParams"/>, then run through
    /// every registered <see cref="IThrowModifier"/> (Overcharge, stealth bonus, perfect-catch counter...), then solved
    /// into a velocity by <see cref="ThrowSolver"/>.
    /// </summary>
    public struct ThrowParams
    {
        public DodgeballPlayer Thrower;
        public DodgeballPlayer Target;           // soft-lock / AI target, may be null
        public Vector3 Origin;                   // release point (hand)
        public Vector3 AimDirection;             // normalised
        public Vector3 AimPoint;                 // world point when there is no target
        public float BaseSpeed;                  // m/s before multipliers (CombatProfile.baseThrowSpeedKmh)
        public float ChargeNormalized;           // 0..1 (can exceed 1 for Rayne's overcharge time, see ChargeSeconds)
        public float ChargeSeconds;              // raw seconds the throw was charged
        public float SpeedMultiplier;            // product of modifiers (charge curve, overcharge, stealth, counter...)
        public float RadiusMultiplier;           // ball radius scale (Overcharge +20%)
        public int RallyCount;                   // rally count the thrown ball will carry
        public float GravityScale;               // 1 = real gravity; fastballs use less drop
        public bool Unblockable;
        public bool Pierce;
        public bool IsAbilityThrow;
        public bool IsPass;
        public bool IsCounterThrow;              // perfect-catch counter (+20%)
        public bool RevealsThrower;              // Gale: throwing from stealth reveals
        public BallStyle Style;
        public IBallPayload Payload;             // optional special behaviour (Meteor shockwave, glue, freeze...)

        /// <summary>
        /// Final launch speed (m/s): BaseSpeed * SpeedMultiplier * rally boost, clamped to 220 km/h.
        /// <c>Velocity_current = Velocity_base * (1 + 0.10 * RallyCount)</c>
        /// </summary>
        public float FinalSpeed => RallyMath.ComputeSpeed(BaseSpeed * Mathf.Max(0f, SpeedMultiplier), RallyCount);

        public float FinalSpeedKmh => FinalSpeed * GameConstants.MsToKmh;
    }

    /// <summary>Modifies throws of the player it is registered on (abilities, passives, buffs).</summary>
    public interface IThrowModifier
    {
        /// <summary>Lower runs first.</summary>
        int Order { get; }

        void ModifyThrow(ref ThrowParams throwParams);

        /// <summary>Called after the ball was launched with the final params.</summary>
        void OnThrowCommitted(in ThrowParams throwParams, DodgeBall ball);
    }

    /// <summary>Special behaviour attached to a single throw (Meteor shockwave, glue puddle, freeze...).</summary>
    public interface IBallPayload
    {
        void OnLaunched(DodgeBall ball);

        void OnTick(DodgeBall ball, float deltaTime);

        /// <summary>Called before the hit is applied. May modify the hit (e.g. add freeze). Return false to suppress the default hit.</summary>
        bool OnHitPlayer(DodgeBall ball, ref HitContext hit);

        /// <summary>Called after the hit was resolved.</summary>
        void OnAfterHitPlayer(DodgeBall ball, in HitContext hit, HitOutcome outcome);

        /// <summary>Hit a non-player surface (floor/wall). <paramref name="isFloor"/> when the normal points up.</summary>
        void OnHitSurface(DodgeBall ball, Vector3 point, Vector3 normal, Collider surface, bool isFloor);

        void OnCaught(DodgeBall ball, DodgeballPlayer catcher, CatchQuality quality);

        /// <summary>The ball stopped being live (any reason). Clean up trails etc.</summary>
        void OnEnded(DodgeBall ball);
    }

    /// <summary>Base class with no-op implementations so payloads only override what they need.</summary>
    public abstract class BallPayloadBase : IBallPayload
    {
        public virtual void OnLaunched(DodgeBall ball) { }
        public virtual void OnTick(DodgeBall ball, float deltaTime) { }
        public virtual bool OnHitPlayer(DodgeBall ball, ref HitContext hit) => true;
        public virtual void OnAfterHitPlayer(DodgeBall ball, in HitContext hit, HitOutcome outcome) { }
        public virtual void OnHitSurface(DodgeBall ball, Vector3 point, Vector3 normal, Collider surface, bool isFloor) { }
        public virtual void OnCaught(DodgeBall ball, DodgeballPlayer catcher, CatchQuality quality) { }
        public virtual void OnEnded(DodgeBall ball) { }
    }

    /// <summary>
    /// Non-player objects that live balls can strike (Shadow's clones, Bear's Aegis Barrier, Screws' turret).
    /// Put it on a component whose collider is on layer <see cref="GameLayers.Hittable"/>.
    /// </summary>
    public interface IBallHittable
    {
        /// <summary>Team that owns this object (balls from the same team pass through unless it says otherwise).</summary>
        TeamId OwnerTeam { get; }

        BallHitResponse OnBallHit(DodgeBall ball, in RaycastHit hit);
    }

    /// <summary>
    /// Anything that bends the flight of nearby live balls each physics step (Bear's Magnetic Pull, Chrono's stasis).
    /// Registered with <see cref="BallManager.RegisterFieldEffect"/>.
    /// </summary>
    public interface IBallFieldEffect
    {
        /// <summary>Called for every live ball each FixedUpdate before the ball integrates its motion.</summary>
        void ApplyToBall(DodgeBall ball, float fixedDeltaTime);
    }

    /// <summary>
    /// Overrides how a player passes (Houdini's Hat Trick teleports the ball straight into a teammate's hands).
    /// Assigned to <see cref="PlayerCombatController.PassHandler"/>.
    /// </summary>
    public interface IPassHandler
    {
        /// <summary>Return true if the pass was fully handled (the default lob pass is then skipped).</summary>
        bool TryHandlePass(DodgeBall ball, DodgeballPlayer from, DodgeballPlayer to);
    }

    /// <summary>Throw/catch tuning. Lives inside CharacterData.</summary>
    [Serializable]
    public class CombatProfile
    {
        [Header("Throwing")]
        [Tooltip("Speed of a fully charged standard throw (km/h) before rally boost.")] public float baseThrowSpeedKmh = 88f;
        [Tooltip("Multiplier for an instant (uncharged) throw.")] [Range(0.3f, 1f)] public float minChargeMultiplier = 0.72f;
        [Tooltip("Seconds to reach full charge.")] public float fullChargeTime = 0.75f;
        [Tooltip("Hard limit on how long a throw can be held (s). Rayne's Overcharge raises it to 2 s.")] public float maxChargeTime = 2.5f;
        [Tooltip("Charge curve (0..1 time -> 0..1 power).")] public AnimationCurve chargeCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
        [Tooltip("Gravity multiplier applied to thrown balls (lower = flatter fastballs).")] public float thrownGravityScale = 0.65f;
        [Tooltip("Minimum seconds between two throws.")] public float throwCooldown = 0.3f;
        [Tooltip("Pass speed (km/h).")] public float passSpeedKmh = 45f;

        [Header("Aim assist")]
        [Tooltip("Cone half-angle (deg) inside which enemies are soft-locked.")] public float aimAssistAngle = 16f;
        [Tooltip("Maximum soft-lock distance (m).")] public float aimAssistRange = 32f;

        [Header("Catching")]
        [Tooltip("Perfect window (s). Spec: 0.15 s. Bear's Iron Mitts multiplies it by 1.5.")] public float perfectCatchWindow = GameConstants.PerfectCatchWindow;
        [Tooltip("Normal catch window (s): how long the catch stance stays armed after pressing Catch.")] public float catchWindow = GameConstants.NormalCatchWindow;
        [Tooltip("Recovery after a whiffed catch (s) during which you cannot catch again.")] public float whiffRecovery = 0.5f;
        [Tooltip("Half-angle (deg) of the frontal cone in which balls can be caught.")] public float catchConeAngle = 75f;
        [Tooltip("Radius (m) of the catch zone around the chest.")] public float catchRadius = 0.85f;
        [Tooltip("Seconds the +20% perfect-catch counter boost stays available.")] public float counterBoostDuration = 3f;

        [Header("Pick up")]
        [Tooltip("Radius (m) within which free balls are grabbed automatically when running over them.")] public float autoPickupRadius = 0.9f;
        [Tooltip("Radius (m) for a manual pick-up press.")] public float manualPickupRadius = 1.6f;
    }
}
