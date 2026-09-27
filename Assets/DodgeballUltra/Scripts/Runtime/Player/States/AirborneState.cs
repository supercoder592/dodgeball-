namespace DodgeballUltra.Player.States
{
    /// <summary>
    /// In the air after a jump, a knockback or walking off an edge. The motor keeps the take-off momentum and only allows
    /// <c>airControl</c> steering. Jump throws (-&gt; ChargingThrow) and air catches (-&gt; Catching) are allowed.
    /// Landing returns to Grounded, or straight into Sprinting when sprint is still held.
    /// </summary>
    public sealed class AirborneState : PlayerStateBase
    {
        public AirborneState(PlayerStateMachine machine) : base(machine)
        {
        }

        public override PlayerStateId Id => PlayerStateId.Airborne;

        public override void Enter(PlayerStateId previous)
        {
            var motor = Motor;
            if (motor != null) motor.SetMode(MovementMode.Airborne);
        }

        public override void Tick(float deltaTime)
        {
            var motor = Motor;
            if (motor == null || Owner == null) return;
            var intent = Owner.Intent;

            motor.SetMode(MovementMode.Airborne);
            DriveMovement(intent);

            if (motor.IsGrounded && TimeInState >= Machine.MinAirTime)
            {
                ExitToLocomotion(intent);
                return;
            }

            if (TryBeginThrowCharge(intent)) return; // jump throw
            TryBeginCatch(intent);                   // air catch
        }
    }
}
