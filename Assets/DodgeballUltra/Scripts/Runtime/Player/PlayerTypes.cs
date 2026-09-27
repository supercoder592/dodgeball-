using System;
using DodgeballUltra.Combat;
using UnityEngine;

// CONTRACT FILE (kernel). Shared player-facing types. Do not rename or remove members; additions are fine.
namespace DodgeballUltra.Player
{
    /// <summary>States of <see cref="PlayerStateMachine"/> (spec: Grounded, Airborne, Sprinting, Sliding, ChargingThrow, Catching, Stunned, Incapacitated).</summary>
    public enum PlayerStateId
    {
        Grounded = 0,
        Airborne = 1,
        Sprinting = 2,
        Sliding = 3,
        ChargingThrow = 4,
        Catching = 5,
        Stunned = 6,
        Incapacitated = 7,
    }

    /// <summary>Why a player is in the Incapacitated state.</summary>
    public enum IncapacitationReason
    {
        None = 0,
        Eliminated,   // ragdoll transition before moving to the outfield
        Frozen,       // Elsa's Glacier Freeze / Absolute Zero hard-freeze
        Grabbed,      // Gouki's Tackle Intercept carry
        Teleporting,  // short lock during Houdini/Gale/Chrono relocations
        Channeling,   // abilities with a locking channel (Houdini's Swap Places)
        Rewinding,    // Specter/Chrono rewind playback
        RoundTransition,
    }

    /// <summary>Locomotion regime the motor is asked to use by the active player state.</summary>
    public enum MovementMode
    {
        Walk = 0,
        Sprint,
        Slide,
        Airborne,
        Charging, // reduced speed while charging a throw
        Catching, // reduced speed while in the catch stance
        Locked,   // no voluntary movement (stunned, incapacitated)
    }

    public enum EliminationCause
    {
        BallHit = 0,
        Ability,
        Frozen,        // second hit while frozen (Elsa)
        Tackle,        // thrown into the outfield by Gouki
        OutOfBounds,
        DelayedImpact, // Chrono's delayed elimination resolving
        Forfeit,
    }

    public enum RevivalCause
    {
        PerfectCatch = 0,
        OutfieldHit,
        Ability,
        RoundReset,
        DelayedImpactCancelled,
        TimeReversal,
    }

    public enum HitOutcome
    {
        /// <summary>The ball could not hit this player (teammate, outfield, pass...).</summary>
        Ignored = 0,
        /// <summary>A hit filter cancelled the hit (invulnerability, shield, evasion).</summary>
        Negated,
        /// <summary>HP reduced but the player is still in.</summary>
        Damaged,
        /// <summary>The player was eliminated.</summary>
        Eliminated,
        /// <summary>Elimination was postponed by an interceptor (Chrono's Delayed Impact).</summary>
        EliminationDelayed,
        /// <summary>Elimination prevented by an interceptor (Specter's Time Reversal).</summary>
        EliminationPrevented,
    }

    /// <summary>Status effects that gameplay, the motor and the visuals react to.</summary>
    public enum StatusEffectType
    {
        Slow = 0,          // magnitude = fraction removed (0.6 = 60% slow)
        Haste,             // magnitude = fraction added (0.2 = +20%)
        Frozen,            // cannot move or catch; second hit eliminates
        Stunned,           // mirrors the Stunned state (for UI)
        Invulnerable,      // balls pass through / are auto-evaded
        Cloaked,           // Gale: invisible to enemies
        Revealed,          // cannot be cloaked
        Silenced,          // cannot use Skill/Ultimate
        Rooted,            // cannot move but can act
        Slippery,          // Absolute Zero: heavy inertia/sliding (magnitude = traction loss 0..1)
        DodgeDisabled,     // cannot jump or slide
        SilentFootsteps,   // Gale passive: no footstep audio, hidden from enemy minimap
        Obscured,          // Mirage Formation: real body is disguised among clones
        Magnetized,        // Bear's field: flying balls curve into hands
    }

    /// <summary>
    /// Mutable description of a ball (or ability) hit travelling through <see cref="PlayerHealth.ReceiveHit"/>.
    /// Hit filters may cancel it or change its damage.
    /// </summary>
    public struct HitContext
    {
        public DodgeBall Ball;              // may be null for ability damage
        public DodgeballPlayer Attacker;    // may be null
        public DodgeballPlayer Victim;
        public Vector3 Point;
        public Vector3 Normal;
        public Vector3 BallVelocity;
        public float Damage;
        public bool IsAbilityHit;
        public bool Unblockable;            // Rayne's beam ball: ignores shields and evasion
        public bool ForceEliminate;         // e.g. frozen second hit
        public float KnockbackImpulse;      // m/s velocity change applied to the victim on a non-lethal hit
        public bool Cancelled;
        public string CancelReason;

        public float SpeedKmh => BallVelocity.magnitude * Core.GameConstants.MsToKmh;
    }

    /// <summary>Everything an <see cref="IEliminationInterceptor"/> needs to decide.</summary>
    public struct EliminationContext
    {
        public DodgeballPlayer Victim;
        public DodgeballPlayer Attacker;
        public EliminationCause Cause;
        public Vector3 Impulse;
        public Vector3 Point;
        public bool HasHit;
        public HitContext Hit;
    }

    /// <summary>Intercepts incoming hits before damage is applied (invulnerability, evasion, shields, damage scaling).</summary>
    public interface IIncomingHitFilter
    {
        /// <summary>Lower runs first.</summary>
        int Priority { get; }

        /// <summary>Set <c>hit.Cancelled = true</c> to negate the hit, or adjust <c>hit.Damage</c> / <c>hit.ForceEliminate</c>.</summary>
        void FilterHit(ref HitContext hit);
    }

    /// <summary>Intercepts eliminations (Chrono's Delayed Impact, Specter's Time Reversal).</summary>
    public interface IEliminationInterceptor
    {
        /// <summary>Lower runs first.</summary>
        int Priority { get; }

        /// <summary>
        /// Return <see cref="HitOutcome.EliminationDelayed"/> or <see cref="HitOutcome.EliminationPrevented"/> to take ownership of
        /// the elimination, or <see cref="HitOutcome.Eliminated"/> to let it proceed.
        /// </summary>
        HitOutcome Intercept(PlayerHealth health, ref EliminationContext context);
    }

    /// <summary>
    /// What a controller (human input or AI) wants the player to do this frame. Produced by an <see cref="IIntentSource"/>,
    /// consumed by the player state machine. All vectors are world space.
    /// </summary>
    public struct PlayerIntent
    {
        /// <summary>Desired planar move direction (y = 0), magnitude 0..1.</summary>
        public Vector3 Move;
        /// <summary>Normalised world aim direction (camera forward for humans).</summary>
        public Vector3 AimDirection;
        /// <summary>World point being aimed at (camera ray hit, or AI target chest).</summary>
        public Vector3 AimPoint;
        /// <summary>Explicit target (AI). Humans leave this null and rely on aim assist.</summary>
        public DodgeballPlayer DesiredTarget;

        public bool SprintHeld;
        public bool JumpPressed;
        public bool SlidePressed;
        public bool ThrowPressed;
        public bool ThrowHeld;
        public bool ThrowReleased;
        public bool CatchPressed;
        public bool PassPressed;
        public bool PickupPressed;
        public bool SkillPressed;
        public bool UltimatePressed;
        public bool CycleTargetPressed;

        /// <summary>An intent that does nothing while facing <paramref name="forward"/>.</summary>
        public static PlayerIntent Neutral(Vector3 position, Vector3 forward)
        {
            var f = forward.sqrMagnitude > 1e-6f ? forward.normalized : Vector3.forward;
            return new PlayerIntent { AimDirection = f, AimPoint = position + f * 10f };
        }
    }

    /// <summary>Anything that can drive a player: <c>HumanInputSource</c> or <c>BotBrain</c>.</summary>
    public interface IIntentSource
    {
        /// <summary>Called once per frame (scaled time) by <see cref="DodgeballPlayer"/>.</summary>
        PlayerIntent Sample(DodgeballPlayer player, float deltaTime);
    }

    /// <summary>Data used to spawn and initialise a <see cref="DodgeballPlayer"/>.</summary>
    public struct PlayerSpawnInfo
    {
        public int PlayerId;
        public string DisplayName;
        public TeamId Team;
        public Characters.CharacterData Character;
        public bool IsHuman;
        public bool IsLocal;
        public IIntentSource IntentSource;
        public Vector3 Position;
        public Quaternion Rotation;
    }

    /// <summary>Movement tuning. Lives inside CharacterData so heroes can differ.</summary>
    [Serializable]
    public class MotorProfile
    {
        [Tooltip("Top jog speed (m/s).")] public float walkSpeed = 4.6f;
        [Tooltip("Top sprint speed (m/s).")] public float sprintSpeed = 7.4f;
        [Tooltip("Speed multiplier while charging a throw.")] [Range(0f, 1f)] public float chargingSpeedMultiplier = 0.6f;
        [Tooltip("Speed multiplier while in the catch stance.")] [Range(0f, 1f)] public float catchingSpeedMultiplier = 0.45f;
        [Tooltip("Ground acceleration (m/s^2) at 0 speed; scaled by accelerationCurve.")] public float acceleration = 34f;
        [Tooltip("Ground deceleration when there is no input (m/s^2).")] public float deceleration = 40f;
        [Tooltip("Acceleration multiplier over normalised speed (0 = standing, 1 = top speed). Smooth acceleration curve.")]
        public AnimationCurve accelerationCurve = new AnimationCurve(new Keyframe(0f, 1.25f), new Keyframe(0.7f, 0.9f), new Keyframe(1f, 0.55f));
        [Tooltip("Fraction of ground control available in the air.")] [Range(0f, 1f)] public float airControl = 0.35f;
        [Tooltip("Body turn rate (deg/s).")] public float turnSpeed = 720f;
        [Tooltip("Apex height of a jump (m).")] public float jumpHeight = 1.05f;
        [Tooltip("Extra gravity multiplier for snappy, grounded-feeling jumps.")] public float gravityMultiplier = 1.8f;
        [Tooltip("Speed multiplier applied at the start of a slide (momentum preservation + boost).")] public float slideBoost = 1.2f;
        [Tooltip("Friction deceleration during a slide (m/s^2).")] public float slideFriction = 6.5f;
        [Tooltip("Maximum slide duration (s).")] public float slideDuration = 0.8f;
        [Tooltip("Cooldown between slides (s).")] public float slideCooldown = 0.5f;
        [Tooltip("Capsule height multiplier while sliding.")] [Range(0.3f, 1f)] public float slideHeightMultiplier = 0.55f;
        [Tooltip("Mass of the character rigidbody (kg).")] public float mass = 75f;
    }
}
