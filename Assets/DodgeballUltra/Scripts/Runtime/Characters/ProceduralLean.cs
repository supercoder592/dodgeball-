using DodgeballUltra.Player;
using UnityEngine;

namespace DodgeballUltra.Characters
{
    /// <summary>
    /// CONTRACT (kernel) - leans the character mesh into turns: roll = f(yaw rate x speed), pitch = f(planar acceleration),
    /// critically-damped smoothing, clamped angles. Rotates only the LeanPivot (physics capsule stays upright).
    /// <para>
    /// Model: a runner leans into a turn so the resultant of gravity and the centripetal force passes through the feet
    /// (<c>tan(roll) = v * omega / g</c>); a sprinter pitches forward when accelerating and back when braking. The tuning
    /// maps those quantities linearly (roll = <see cref="rollPerYawSpeed"/> x yawRate x speed, pitch =
    /// <see cref="pitchPerAcceleration"/> x forward acceleration), clamps them and smooths them with
    /// <see cref="Mathf.SmoothDamp(float, float, ref float, float, float, float)"/> (critically damped, no overshoot).
    /// </para>
    /// <para>
    /// Sign convention: <see cref="CurrentRoll"/> &gt; 0 leans to the character's right (a right turn, positive
    /// <see cref="PlayerMotor.YawRate"/>), <see cref="CurrentPitch"/> &gt; 0 leans forward. The pivot sits at the feet, so the
    /// whole body tilts around the planted soles. The lean fades to zero while frozen, stunned or incapacitated, and snaps
    /// to zero while ragdolled (physics owns the body). Runs in LateUpdate on scaled time: hitstop holds the pose.
    /// </para>
    /// <para>Owner module: Characters.</para>
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(0)] // after the IK spine twist (-10), before the ragdoll velocity tracker (100)
    public sealed class ProceduralLean : MonoBehaviour
    {
        [Tooltip("Degrees of roll per (deg/s of yaw rate * m/s of speed).")] public float rollPerYawSpeed = 0.0045f;
        [Tooltip("Maximum roll into a turn (deg).")] public float maxRoll = 16f;
        [Tooltip("Degrees of pitch per m/s^2 of forward acceleration.")] public float pitchPerAcceleration = 0.55f;
        [Tooltip("Maximum pitch (deg).")] public float maxPitch = 10f;
        [Tooltip("Smoothing time (s).")] public float smoothTime = 0.12f;

        [Header("Filters")]
        [Tooltip("Lean multiplier while airborne (the feet cannot push against the floor).")]
        [Range(0f, 1f)] public float airborneFactor = 0.3f;

        [Tooltip("Lean multiplier while sliding (the crouch pose already sells the motion).")]
        [Range(0f, 1f)] public float slideFactor = 0.5f;

        [Tooltip("Below this planar speed (m/s) turning on the spot produces proportionally less roll.")]
        [Min(0.01f)] public float fullRollSpeed = 1.2f;

        [Tooltip("Yaw rates above this (deg/s) are clamped (snap turns and teleports must not throw the body sideways).")]
        [Min(1f)] public float maxYawRate = 720f;

        [Tooltip("Accelerations above this (m/s^2) are clamped (knockbacks, teleports, dashes).")]
        [Min(0.1f)] public float maxAcceleration = 30f;

        /// <summary>Current roll (deg). Positive = leaning to the right.</summary>
        public float CurrentRoll { get; private set; }

        /// <summary>Current pitch (deg). Positive = leaning forward.</summary>
        public float CurrentPitch { get; private set; }

        /// <summary>The player whose motion drives the lean.</summary>
        public DodgeballPlayer Owner { get; private set; }

        /// <summary>The transform that is rotated (CharacterVisual.LeanPivot).</summary>
        public Transform Pivot { get; private set; }

        private CharacterVisual _visual;
        private float _rollVelocity;
        private float _pitchVelocity;

        // ------------------------------------------------------------------ contract

        /// <summary>Binds the lean to <paramref name="owner"/>'s motor and the pivot it rotates.</summary>
        public void Initialize(DodgeballPlayer owner, Transform leanPivot)
        {
            Owner = owner;
            Pivot = leanPivot;
            _visual = GetComponentInParent<CharacterVisual>();
            ResetLean();
        }

        // ------------------------------------------------------------------ additions

        /// <summary>Snaps the lean to upright (round reset, teleports).</summary>
        public void ResetLean()
        {
            CurrentRoll = 0f;
            CurrentPitch = 0f;
            _rollVelocity = 0f;
            _pitchVelocity = 0f;
            if (Pivot != null) Pivot.localRotation = Quaternion.identity;
        }

        // ------------------------------------------------------------------ per frame

        private void LateUpdate()
        {
            if (Pivot == null) return;

            // Physics owns the body: the pivot must not carry a stale tilt into the recovery blend.
            RagdollController ragdoll = _visual != null ? _visual.Ragdoll : null;
            if (ragdoll != null && ragdoll.IsRagdolled)
            {
                if (CurrentRoll != 0f || CurrentPitch != 0f) ResetLean();
                return;
            }

            float dt = Time.deltaTime; // scaled: hitstop / pause hold the pose
            if (dt <= 0f) return;

            ComputeTargets(out float targetRoll, out float targetPitch);
            float smooth = Mathf.Max(0.001f, smoothTime);
            CurrentRoll = Mathf.SmoothDamp(CurrentRoll, targetRoll, ref _rollVelocity, smooth, Mathf.Infinity, dt);
            CurrentPitch = Mathf.SmoothDamp(CurrentPitch, targetPitch, ref _pitchVelocity, smooth, Mathf.Infinity, dt);

            // Unity: +X tips the head forward, +Z tips it to the LEFT -> roll to the right is a negative Z rotation.
            Pivot.localRotation = Quaternion.Euler(CurrentPitch, 0f, -CurrentRoll);
        }

        private void ComputeTargets(out float roll, out float pitch)
        {
            roll = 0f;
            pitch = 0f;
            DodgeballPlayer owner = Owner;
            PlayerMotor motor = owner != null ? owner.Motor : null;
            if (motor == null || !IsFreeToLean(owner, motor)) return;

            float speed = motor.PlanarSpeed;
            float yawRate = Mathf.Clamp(motor.YawRate, -maxYawRate, maxYawRate);
            float speedFactor = Mathf.Clamp01(speed / Mathf.Max(0.01f, fullRollSpeed));
            roll = Mathf.Clamp(yawRate * speed * rollPerYawSpeed * speedFactor, -maxRoll, maxRoll);

            Vector3 acceleration = Vector3.ClampMagnitude(motor.PlanarAcceleration, maxAcceleration);
            float forwardAcceleration = Vector3.Dot(acceleration, owner.Forward);
            pitch = Mathf.Clamp(forwardAcceleration * pitchPerAcceleration, -maxPitch, maxPitch);

            float k = 1f;
            if (!motor.IsGrounded) k *= airborneFactor;
            if (motor.IsSliding) k *= slideFactor;
            roll *= k;
            pitch *= k;
        }

        /// <summary>False while the body is a statue or out of control (the lean then fades back to upright).</summary>
        private bool IsFreeToLean(DodgeballPlayer owner, PlayerMotor motor)
        {
            if (motor.IsFrozen) return false;
            if (_visual != null && _visual.IsFrozen) return false;
            PlayerStateMachine fsm = owner.StateMachine;
            if (fsm != null)
            {
                PlayerStateId state = fsm.Current;
                if (state == PlayerStateId.Incapacitated || state == PlayerStateId.Stunned) return false;
            }
            return true;
        }
    }
}
