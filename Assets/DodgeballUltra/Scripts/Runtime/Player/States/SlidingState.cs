namespace DodgeballUltra.Player.States
{
    /// <summary>
    /// Momentum-preserving slide (lowered capsule, friction loss handled by <see cref="PlayerMotor"/>). The stick only
    /// steers slightly; throwing and catching are not allowed. Ends when the motor ends the slide (duration elapsed, speed
    /// too low, wall hit), returning to Grounded / Sprinting / Airborne.
    /// <para>Can also be entered directly (e.g. by an ability) - the slide is then started along the stick or body forward.</para>
    /// </summary>
    public sealed class SlidingState : PlayerStateBase
    {
        public SlidingState(PlayerStateMachine machine) : base(machine)
        {
        }

        public override PlayerStateId Id => PlayerStateId.Sliding;

        public override void Enter(PlayerStateId previous)
        {
            var motor = Motor;
            if (motor == null || Owner == null) return;
            if (motor.IsSliding) return;

            // Entered without a running slide (forced by someone else): start one now, or bail out.
            var intent = Owner.Intent;
            var dir = IsMoving(intent) ? intent.Move : Owner.Forward;
            if (!motor.TryStartSlide(dir)) Machine.ChangeState(Machine.LocomotionFallback());
        }

        public override void Tick(float deltaTime)
        {
            var motor = Motor;
            if (motor == null || Owner == null) return;
            var intent = Owner.Intent;

            if (!motor.IsSliding)
            {
                ExitToLocomotion(intent);
                return;
            }

            motor.SetMode(MovementMode.Slide);
            DriveMovement(intent); // slight steering only
        }

        public override void Exit(PlayerStateId next)
        {
            // Forced out mid-slide (stun, freeze...): stand back up; the remaining momentum is braked normally.
            var motor = Motor;
            if (motor != null && motor.IsSliding) motor.EndSlide();
        }
    }
}
