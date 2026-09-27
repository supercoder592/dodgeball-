namespace DodgeballUltra.Player.States
{
    /// <summary>
    /// Full-speed run (<c>sprintSpeed</c>). Behaves like <see cref="GroundedState"/> but faster; a slide can always be started
    /// from a sprint (momentum is carried into it). Releasing sprint or letting go of the stick returns to Grounded.
    /// Shadow's Decoy Dash passive observes this state through <c>PlayerStateChangedEvent</c>.
    /// </summary>
    public sealed class SprintingState : PlayerStateBase
    {
        private float _ungroundedTime;

        public SprintingState(PlayerStateMachine machine) : base(machine)
        {
        }

        public override PlayerStateId Id => PlayerStateId.Sprinting;

        public override void Enter(PlayerStateId previous)
        {
            _ungroundedTime = 0f;
            var motor = Motor;
            if (motor != null && !motor.IsSliding) motor.SetMode(MovementMode.Sprint);
        }

        public override void Tick(float deltaTime)
        {
            var motor = Motor;
            if (motor == null || Owner == null) return;
            var intent = Owner.Intent;

            if (motor.IsSliding)
            {
                Machine.ChangeState(PlayerStateId.Sliding);
                return;
            }

            motor.SetMode(MovementMode.Sprint);
            DriveMovement(intent);

            if (!motor.IsGrounded)
            {
                _ungroundedTime += deltaTime;
                if (_ungroundedTime >= Machine.CoyoteTime)
                {
                    Machine.ChangeState(PlayerStateId.Airborne);
                    return;
                }
            }
            else
            {
                _ungroundedTime = 0f;
            }

            if (TryBeginThrowCharge(intent)) return;
            if (TryBeginCatch(intent)) return;
            if (TryJump(intent)) return;
            if (TrySlide(intent, requireSpeed: false)) return;

            if (!WantsSprint(intent, continuing: true))
            {
                Machine.ChangeState(PlayerStateId.Grounded);
            }
        }
    }
}
