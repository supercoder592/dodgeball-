namespace DodgeballUltra.Player.States
{
    /// <summary>
    /// Catch stance. Entering it arms <c>PlayerCombatController</c>'s catch window (t_input is recorded by
    /// <c>TryStartCatch</c>, which also publishes <c>CatchAttemptEvent</c>). The motor slows to
    /// <see cref="MovementMode.Catching"/> and the body faces the aim so the frontal catch cone points at the threat; hands
    /// reach for the ball through <c>CharacterIKController</c>. The state lasts while the stance is armed: a successful
    /// catch or a whiff disarms it and the player returns to Grounded / Airborne.
    /// </summary>
    public sealed class CatchingState : PlayerStateBase
    {
        public CatchingState(PlayerStateMachine machine) : base(machine)
        {
        }

        public override PlayerStateId Id => PlayerStateId.Catching;

        public override void Enter(PlayerStateId previous)
        {
            var motor = Motor;
            if (motor != null) motor.SetMode(MovementMode.Catching);

            // Entered directly (not through TryBeginCatch): arm the stance now or leave.
            var combat = Combat;
            if (combat == null) return;
            if (!combat.IsCatchArmed && (combat.HasBall || combat.CatchingBlocked || !combat.TryStartCatch()))
                Machine.ChangeState(Machine.LocomotionFallback());
        }

        public override void Tick(float deltaTime)
        {
            var motor = Motor;
            var combat = Combat;
            if (motor == null || combat == null || Owner == null) return;
            var intent = Owner.Intent;

            motor.SetMode(MovementMode.Catching);
            DriveMovement(intent);
            FaceAim(intent);

            // Caught, whiffed, or blocked (frozen) -> back to moving. A throw pressed on the very frame of the catch starts
            // the counter-throw wind-up straight away (the perfect-catch +20% boost is waiting in the combat controller).
            if (!combat.IsCatchArmed || combat.CatchingBlocked)
            {
                if (combat.HasBall && TryBeginThrowCharge(intent)) return;
                ExitToLocomotion(intent);
            }
        }

        public override void Exit(PlayerStateId next)
        {
            // Forced out (stun, freeze...): disarm without the whiff penalty.
            var combat = Combat;
            if (combat != null && combat.IsCatchArmed) combat.CancelCatch();
        }
    }
}
