namespace DodgeballUltra.Player.States
{
    /// <summary>
    /// Default on-foot state: jogging/walking at <c>walkSpeed</c>, body turning toward the move direction (or toward the aim
    /// while standing still). Entry point for every action:
    /// <list type="bullet">
    /// <item>Throw pressed with a ball -&gt; <see cref="PlayerStateId.ChargingThrow"/></item>
    /// <item>Catch pressed with free hands -&gt; <see cref="PlayerStateId.Catching"/></item>
    /// <item>Jump -&gt; <see cref="PlayerStateId.Airborne"/>; no ground for longer than the coyote time -&gt; Airborne</item>
    /// <item>Slide while moving fast -&gt; <see cref="PlayerStateId.Sliding"/></item>
    /// <item>Sprint held while moving forward-ish -&gt; <see cref="PlayerStateId.Sprinting"/></item>
    /// </list>
    /// </summary>
    public sealed class GroundedState : PlayerStateBase
    {
        private float _ungroundedTime;

        public GroundedState(PlayerStateMachine machine) : base(machine)
        {
        }

        public override PlayerStateId Id => PlayerStateId.Grounded;

        public override void Enter(PlayerStateId previous)
        {
            _ungroundedTime = 0f;
            var motor = Motor;
            if (motor != null && !motor.IsSliding) motor.SetMode(MovementMode.Walk);
        }

        public override void Tick(float deltaTime)
        {
            var motor = Motor;
            if (motor == null || Owner == null) return;
            var intent = Owner.Intent;

            // An ability started a slide directly on the motor (e.g. an evasive dodge): follow it.
            if (motor.IsSliding)
            {
                Machine.ChangeState(PlayerStateId.Sliding);
                return;
            }

            motor.SetMode(MovementMode.Walk);
            DriveMovement(intent);
            if (!IsMoving(intent) && Machine.FaceAimWhileIdle) FaceAim(intent);

            // Walking off an edge / launched: short coyote time before becoming airborne.
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
            if (TrySlide(intent, requireSpeed: true)) return;

            if (WantsSprint(intent, continuing: false))
            {
                Machine.ChangeState(PlayerStateId.Sprinting);
            }
        }
    }
}
