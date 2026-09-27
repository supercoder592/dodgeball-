using DodgeballUltra.Combat;
using DodgeballUltra.Core;
using UnityEngine;

namespace DodgeballUltra.Player.States
{
    /// <summary>
    /// Base class of the eight player behaviour states driven by <see cref="PlayerStateMachine"/>.
    /// <para>
    /// A state reads <see cref="DodgeballPlayer.Intent"/> every frame and translates it into motor and combat commands
    /// (<see cref="PlayerMotor.SetMode"/>, <see cref="PlayerMotor.SetMoveInput"/>, <see cref="PlayerMotor.SetFacing"/>,
    /// <see cref="PlayerCombatController.BeginCharge"/> ...). Transitions are requested through
    /// <see cref="PlayerStateMachine.ChangeState"/>; a transition requested from <see cref="Tick"/> happens immediately, so
    /// the calling code must <c>return</c> right after a successful request.
    /// </para>
    /// <para>States are plain C# objects (no MonoBehaviour), allocated once per player in <see cref="PlayerStateMachine.Initialize"/>.</para>
    /// </summary>
    public abstract class PlayerStateBase : IFsmState<PlayerStateId>
    {
        protected PlayerStateBase(PlayerStateMachine machine)
        {
            Machine = machine;
        }

        public abstract PlayerStateId Id { get; }

        /// <summary>The machine that owns this state.</summary>
        public PlayerStateMachine Machine { get; }

        protected DodgeballPlayer Owner => Machine.Owner;
        protected PlayerMotor Motor => Owner != null ? Owner.Motor : null;
        protected PlayerCombatController Combat => Owner != null ? Owner.Combat : null;
        protected StatusEffectController Status => Owner != null ? Owner.Status : null;

        /// <summary>Seconds spent in this state (valid while active).</summary>
        protected float TimeInState => Machine.TimeInState;

        // ------------------------------------------------------------------ IFsmState

        public virtual void Enter(PlayerStateId previous)
        {
        }

        public virtual void Exit(PlayerStateId next)
        {
        }

        public virtual void Tick(float deltaTime)
        {
        }

        public virtual void FixedTick(float fixedDeltaTime)
        {
        }

        /// <summary>Default: any transition is allowed. Locking states override this to accept only forced transitions.</summary>
        public virtual bool CanExitTo(PlayerStateId next) => true;

        // ------------------------------------------------------------------ helpers

        /// <summary>Feeds the intent's planar move vector to the motor.</summary>
        protected void DriveMovement(in PlayerIntent intent)
        {
            var motor = Motor;
            if (motor == null) return;
            Vector3 move = intent.Move;
            move.y = 0f;
            motor.SetMoveInput(move, Mathf.Clamp01(move.magnitude));
        }

        /// <summary>Zero movement input (locked states).</summary>
        protected void StopMovement()
        {
            var motor = Motor;
            if (motor != null) motor.SetMoveInput(Vector3.zero, 0f);
        }

        /// <summary>Planar aim direction of the intent (falls back to the body forward).</summary>
        protected Vector3 PlanarAim(in PlayerIntent intent)
        {
            Vector3 aim = intent.AimDirection;
            aim.y = 0f;
            if (aim.sqrMagnitude < 1e-6f) return Owner != null ? Owner.Forward : Vector3.forward;
            return aim.normalized;
        }

        /// <summary>Turns the body toward the aim this frame (charging, catching, idle).</summary>
        protected void FaceAim(in PlayerIntent intent, bool instant = false)
        {
            var motor = Motor;
            if (motor == null) return;
            motor.SetFacing(PlanarAim(intent), instant);
        }

        /// <summary>True when the stick is deflected enough to count as moving.</summary>
        protected static bool IsMoving(in PlayerIntent intent, float threshold = 0.1f)
        {
            Vector3 m = intent.Move;
            m.y = 0f;
            return m.sqrMagnitude >= threshold * threshold;
        }

        /// <summary>
        /// Sprint rule: sprint held, stick pushed far enough and (when starting) the move direction roughly matches the body
        /// forward. Rooted players cannot sprint.
        /// </summary>
        protected bool WantsSprint(in PlayerIntent intent, bool continuing)
        {
            if (!intent.SprintHeld) return false;
            var status = Status;
            if (status != null && status.Has(StatusEffectType.Rooted)) return false;

            Vector3 m = intent.Move;
            m.y = 0f;
            float mag = m.magnitude;
            if (continuing) return mag >= Machine.SprintStopInput;
            if (mag < Machine.SprintMinInput) return false;
            return Owner == null || Vector3.Dot(m / mag, Owner.Forward) >= Machine.SprintForwardDot;
        }

        /// <summary>The locomotion state matching the current ground contact and sprint intent.</summary>
        protected PlayerStateId LocomotionStateFor(in PlayerIntent intent)
        {
            var motor = Motor;
            if (motor != null && !motor.IsGrounded) return PlayerStateId.Airborne;
            return WantsSprint(intent, true) ? PlayerStateId.Sprinting : PlayerStateId.Grounded;
        }

        /// <summary>Throw pressed with a ball in hand: starts charging and enters ChargingThrow.</summary>
        protected bool TryBeginThrowCharge(in PlayerIntent intent)
        {
            if (!intent.ThrowPressed || Owner == null || !Owner.CanAct) return false;
            var combat = Combat;
            if (combat == null || !combat.HasBall) return false;
            if (!combat.BeginCharge()) return false;
            if (Machine.ChangeState(PlayerStateId.ChargingThrow)) return true;
            combat.CancelCharge();
            return false;
        }

        /// <summary>Catch pressed with free hands: arms the catch stance (t_input recorded) and enters Catching.</summary>
        protected bool TryBeginCatch(in PlayerIntent intent)
        {
            if (!intent.CatchPressed || Owner == null || !Owner.CanAct) return false;
            var combat = Combat;
            if (combat == null || combat.HasBall || combat.CatchingBlocked) return false;
            if (!combat.TryStartCatch()) return false;
            if (Machine.ChangeState(PlayerStateId.Catching)) return true;
            combat.CancelCatch();
            return false;
        }

        /// <summary>Jump pressed and the motor allows it: jumps and enters Airborne.</summary>
        protected bool TryJump(in PlayerIntent intent)
        {
            if (!intent.JumpPressed || Owner == null || !Owner.CanAct) return false;
            var motor = Motor;
            if (motor == null || !motor.TryJump()) return false;
            Machine.ChangeState(PlayerStateId.Airborne);
            return true;
        }

        /// <summary>
        /// Slide pressed: when moving fast enough (or unconditionally from a sprint) starts a momentum-preserving slide along
        /// the stick direction (or current velocity) and enters Sliding.
        /// </summary>
        protected bool TrySlide(in PlayerIntent intent, bool requireSpeed)
        {
            if (!intent.SlidePressed || Owner == null || !Owner.CanAct) return false;
            var motor = Motor;
            if (motor == null || !motor.CanSlide) return false;

            if (requireSpeed)
            {
                float walk = motor.Profile != null ? motor.Profile.walkSpeed : 4.6f;
                if (motor.PlanarSpeed < walk * Machine.SlideMinSpeedFraction) return false;
            }

            Vector3 dir = IsMoving(intent) ? intent.Move : motor.PlanarVelocity;
            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-4f) dir = Owner.Forward;
            if (!motor.TryStartSlide(dir)) return false;
            Machine.ChangeState(PlayerStateId.Sliding);
            return true;
        }

        /// <summary>Returns to Grounded / Sprinting / Airborne as appropriate.</summary>
        protected void ExitToLocomotion(in PlayerIntent intent)
        {
            Machine.ChangeState(LocomotionStateFor(intent));
        }
    }
}
