namespace DodgeballUltra.Player.States
{
    /// <summary>
    /// Dazed: no voluntary movement (<see cref="MovementMode.Locked"/> - existing momentum is braked, knockbacks still
    /// carry), no actions. Entered through <see cref="PlayerStateMachine.Stun"/>, which already cancelled charging/catching
    /// and interrupted abilities. Leaves on its own when <see cref="PlayerStateMachine.StunRemaining"/> reaches zero.
    /// The Stunned status effect mirrors this state for the HUD.
    /// </summary>
    public sealed class StunnedState : PlayerStateBase
    {
        public StunnedState(PlayerStateMachine machine) : base(machine)
        {
        }

        public override PlayerStateId Id => PlayerStateId.Stunned;

        public override void Enter(PlayerStateId previous)
        {
            var motor = Motor;
            if (motor == null) return;
            if (motor.IsSliding) motor.EndSlide();
            motor.SetMode(MovementMode.Locked);
            StopMovement();
        }

        public override void Tick(float deltaTime)
        {
            var motor = Motor;
            if (motor == null) return;

            motor.SetMode(MovementMode.Locked);
            StopMovement();

            if (Machine.StunRemaining <= 0f)
            {
                Machine.ChangeState(Machine.LocomotionFallback(), true);
            }
        }

        /// <summary>Only a harder lock (Incapacitated) or the end of the stun may leave this state.</summary>
        public override bool CanExitTo(PlayerStateId next) =>
            next == PlayerStateId.Incapacitated || Machine.StunRemaining <= 0f;

        public override void Exit(PlayerStateId next)
        {
            Machine.ClearStunStatus();
        }
    }
}
