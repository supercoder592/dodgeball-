using UnityEngine;

namespace DodgeballUltra.Characters
{
    /// <summary>
    /// CONTRACT (kernel) - Animator parameter and state names shared by the runtime <c>PlayerAnimatorDriver</c> and the editor
    /// <c>AnimatorControllerBuilder</c>. Never type these strings anywhere else.
    /// </summary>
    public static class AnimatorParams
    {
        // Floats
        public const string Speed = "Speed";                   // planar speed (m/s)
        public const string VerticalSpeed = "VerticalSpeed";   // m/s
        public const string ChargeAmount = "ChargeAmount";     // 0..1
        public const string MotionSpeed = "MotionSpeed";       // playback multiplier for the locomotion blend
        public const string Lean = "Lean";                     // -1..1 (optional, for authored lean clips)

        // Bools
        public const string Grounded = "Grounded";
        public const string Sprinting = "Sprinting";
        public const string Sliding = "Sliding";
        public const string Charging = "Charging";
        public const string Catching = "Catching";
        public const string Stunned = "Stunned";
        public const string Frozen = "Frozen";
        public const string Outfield = "Outfield";
        public const string HoldingBall = "HoldingBall";

        // Triggers
        public const string Throw = "Throw";
        public const string Catch = "Catch";
        public const string Hit = "Hit";
        public const string Jump = "Jump";
        public const string Cheer = "Cheer";
        public const string Defeat = "Defeat";

        // State names in the base layer
        public const string StateLocomotion = "Locomotion";
        public const string StateAirborne = "Airborne";
        public const string StateCrouch = "Crouch";      // slides and the catch stance (arms driven by IK)
        public const string StateStunned = "Stunned";    // motion-capture "dizzy" idle
        public const string StateCheer = "Cheer";
        public const string StateDefeat = "Defeat";

        // Optional upper-body layer (only if authored clips exist: Mixamo throw/catch etc.)
        public const string LayerUpperBody = "UpperBody";
        public const string StateThrow = "Throw";
        public const string StateCatch = "Catch";

        // Locomotion blend thresholds (m/s) used by the builder for Idle / Walk / Run / Sprint.
        public const float IdleSpeed = 0f;
        public const float WalkSpeed = 1.6f;
        public const float RunSpeed = 4.6f;
        public const float SprintSpeed = 7.4f;

        public static readonly int SpeedHash = Animator.StringToHash(Speed);
        public static readonly int VerticalSpeedHash = Animator.StringToHash(VerticalSpeed);
        public static readonly int ChargeAmountHash = Animator.StringToHash(ChargeAmount);
        public static readonly int MotionSpeedHash = Animator.StringToHash(MotionSpeed);
        public static readonly int LeanHash = Animator.StringToHash(Lean);
        public static readonly int GroundedHash = Animator.StringToHash(Grounded);
        public static readonly int SprintingHash = Animator.StringToHash(Sprinting);
        public static readonly int SlidingHash = Animator.StringToHash(Sliding);
        public static readonly int ChargingHash = Animator.StringToHash(Charging);
        public static readonly int CatchingHash = Animator.StringToHash(Catching);
        public static readonly int StunnedHash = Animator.StringToHash(Stunned);
        public static readonly int FrozenHash = Animator.StringToHash(Frozen);
        public static readonly int OutfieldHash = Animator.StringToHash(Outfield);
        public static readonly int HoldingBallHash = Animator.StringToHash(HoldingBall);
        public static readonly int ThrowHash = Animator.StringToHash(Throw);
        public static readonly int CatchHash = Animator.StringToHash(Catch);
        public static readonly int HitHash = Animator.StringToHash(Hit);
        public static readonly int JumpHash = Animator.StringToHash(Jump);
        public static readonly int CheerHash = Animator.StringToHash(Cheer);
        public static readonly int DefeatHash = Animator.StringToHash(Defeat);
    }
}
