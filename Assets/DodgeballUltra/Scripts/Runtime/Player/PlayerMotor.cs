using System;
using UnityEngine;

namespace DodgeballUltra.Player
{
    /// <summary>
    /// CONTRACT (kernel) - Rigidbody-based character motor with momentum preservation.
    /// Smooth acceleration curves, friction loss during slides, air control, knockback impulses,
    /// stackable speed modifiers and traction (Absolute Zero ice) handling.
    /// Ticked by <see cref="DodgeballPlayer"/> (no Update/FixedUpdate of its own).
    /// <para>Owner module: Player.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PlayerMotor : MonoBehaviour
    {
        public MotorProfile Profile { get; set; } = new MotorProfile();

        public Vector3 Velocity { get; private set; }
        public Vector3 PlanarVelocity { get; private set; }
        public float PlanarSpeed { get; private set; }
        public bool IsGrounded { get; private set; }
        public Vector3 GroundNormal { get; private set; } = Vector3.up;

        /// <summary>Signed yaw rate of the body (deg/s). Positive = turning right. Drives procedural leaning.</summary>
        public float YawRate { get; private set; }

        /// <summary>Planar acceleration this step (m/s^2). Drives procedural pitch lean.</summary>
        public Vector3 PlanarAcceleration { get; private set; }

        public MovementMode Mode { get; private set; } = MovementMode.Walk;
        public bool IsSliding { get; private set; }
        public float SlideTimeRemaining { get; private set; }

        /// <summary>Top speed for the current mode after all modifiers (m/s).</summary>
        public float CurrentMaxSpeed { get; private set; }

        /// <summary>Product of all speed modifiers and status effects (Slow/Haste).</summary>
        public float SpeedMultiplier { get; private set; } = 1f;

        /// <summary>1 = full grip, 0 = ice. Lower traction = heavy inertia/sliding (Absolute Zero).</summary>
        public float Traction { get; private set; } = 1f;

        public bool CanJump { get; private set; }
        public bool CanSlide { get; private set; }

        /// <summary>Raised when the character lands: impact vertical speed (m/s, positive).</summary>
        public event Action<float> Landed;
        public event Action Jumped;
        public event Action SlideStarted;
        public event Action SlideEnded;

        // ------------------------------------------------------------------ commands (IMPLEMENT: Player module)

        /// <summary>Desired planar direction (world, y ignored) and magnitude 0..1 for this frame.</summary>
        public void SetMoveInput(Vector3 worldDirection, float magnitude) => throw new NotImplementedException();

        public void SetMode(MovementMode mode) => throw new NotImplementedException();

        /// <summary>Rotates the body toward <paramref name="worldDirection"/> at Profile.turnSpeed (or instantly).</summary>
        public void SetFacing(Vector3 worldDirection, bool instant = false) => throw new NotImplementedException();

        /// <summary>Jumps if grounded and allowed. Returns true on success.</summary>
        public bool TryJump() => throw new NotImplementedException();

        /// <summary>Starts a slide along <paramref name="direction"/>, preserving momentum. Returns true on success.</summary>
        public bool TryStartSlide(Vector3 direction) => throw new NotImplementedException();

        public void EndSlide() => throw new NotImplementedException();

        /// <summary>Instant velocity change (m/s) - knockback, shockwaves, tackles.</summary>
        public void AddImpulse(Vector3 velocityChange) => throw new NotImplementedException();

        /// <summary>Hard-sets the planar velocity (dashes, teleports).</summary>
        public void SetPlanarVelocity(Vector3 planarVelocity) => throw new NotImplementedException();

        public void Teleport(Vector3 position, Quaternion rotation) => throw new NotImplementedException();

        /// <summary>Stops all motion and ignores input while true (frozen / ragdoll).</summary>
        public void SetFrozen(bool frozen) => throw new NotImplementedException();

        /// <summary>Adds or replaces a multiplicative speed modifier identified by <paramref name="source"/>.</summary>
        public void SetSpeedModifier(object source, float multiplier) => throw new NotImplementedException();

        public void RemoveSpeedModifier(object source) => throw new NotImplementedException();

        /// <summary>Adds or replaces a traction modifier (0..1) identified by <paramref name="source"/>. Lowest wins.</summary>
        public void SetTractionModifier(object source, float traction) => throw new NotImplementedException();

        public void RemoveTractionModifier(object source) => throw new NotImplementedException();

        /// <summary>Confines the player's planar position to <paramref name="bounds"/> (court half or outfield strip). Null = free.</summary>
        public void SetConfinement(Bounds? bounds) => throw new NotImplementedException();

        /// <summary>Temporarily scales the capsule height (slides). 1 = default.</summary>
        public void SetCapsuleHeightScale(float scale) => throw new NotImplementedException();

        // ------------------------------------------------------------------ ticking (called by DodgeballPlayer)
        public void Tick(float deltaTime) => throw new NotImplementedException();
        public void FixedTick(float fixedDeltaTime) => throw new NotImplementedException();
    }
}
