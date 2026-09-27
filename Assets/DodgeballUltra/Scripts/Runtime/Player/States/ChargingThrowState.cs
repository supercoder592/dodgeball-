using UnityEngine;

namespace DodgeballUltra.Player.States
{
    /// <summary>
    /// Winding up a throw. The motor runs in <see cref="MovementMode.Charging"/> (walk speed x chargingSpeedMultiplier; in the
    /// air the jump momentum is kept) and the body turns toward <see cref="PlayerIntent.AimDirection"/>. The charge itself
    /// (seconds, curve, Rayne's Overcharge) is accumulated by <c>PlayerCombatController.Tick</c>.
    /// <list type="bullet">
    /// <item>Throw released (or no longer held) -&gt; <c>Combat.ReleaseThrow()</c> -&gt; Grounded / Airborne.</item>
    /// <item>Ball lost (passed, vanished, stolen) or charge cancelled by combat -&gt; cancel -&gt; locomotion.</item>
    /// <item>Input locked mid-charge (round end) -&gt; the charge is cancelled, never auto-thrown.</item>
    /// <item>Jump while charging keeps the charge (jump throw).</item>
    /// </list>
    /// </summary>
    public sealed class ChargingThrowState : PlayerStateBase
    {
        public ChargingThrowState(PlayerStateMachine machine) : base(machine)
        {
        }

        public override PlayerStateId Id => PlayerStateId.ChargingThrow;

        public override void Enter(PlayerStateId previous)
        {
            var motor = Motor;
            if (motor != null) motor.SetMode(MovementMode.Charging);

            // Entered directly (not through TryBeginThrowCharge): start the charge now or leave.
            var combat = Combat;
            if (combat == null) return;
            if (!combat.IsCharging && (!combat.HasBall || !combat.BeginCharge()))
                Machine.ChangeState(Machine.LocomotionFallback());
        }

        public override void Tick(float deltaTime)
        {
            var motor = Motor;
            var combat = Combat;
            if (motor == null || combat == null || Owner == null) return;
            var intent = Owner.Intent;

            motor.SetMode(MovementMode.Charging);
            DriveMovement(intent);
            FaceAim(intent);

            // Ball gone (pass, Grand Vanish, stolen) -> abort.
            if (!combat.HasBall)
            {
                if (combat.IsCharging) combat.CancelCharge();
                ExitToLocomotion(intent);
                return;
            }

            // Combat ended the charge itself (auto-release at the hold limit, ability consumed it...).
            if (!combat.IsCharging)
            {
                ExitToLocomotion(intent);
                return;
            }

            // Round over / countdown: never auto-throw because the neutral intent reports "not held".
            if (Owner.InputLocked)
            {
                combat.CancelCharge();
                ExitToLocomotion(intent);
                return;
            }

            // Jump throw: leave the ground without losing the charge.
            if (intent.JumpPressed && motor.IsGrounded) motor.TryJump();

            if (intent.ThrowReleased || !intent.ThrowHeld)
            {
                Release(intent);
                ExitToLocomotion(intent);
            }
        }

        public override void Exit(PlayerStateId next)
        {
            // Forced out while still winding up (stun, freeze, elimination): the throw is cancelled, the ball kept.
            var combat = Combat;
            if (combat != null && combat.IsCharging) combat.CancelCharge();
        }

        private void Release(in PlayerIntent intent)
        {
            var combat = Combat;
            var motor = Motor;

            // Square the shoulders to the aim so the release (and procedural throw IK) never goes over the back.
            if (motor != null && Owner != null)
            {
                Vector3 aim = PlanarAim(intent);
                float angle = Vector3.Angle(Owner.Forward, aim);
                if (angle > Machine.ThrowSnapFacingAngle) motor.SetFacing(aim, instant: true);
            }

            combat.ReleaseThrow();
        }
    }
}
