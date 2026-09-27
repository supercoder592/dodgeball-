namespace DodgeballUltra.Player.States
{
    /// <summary>
    /// Hard lock for an <see cref="IncapacitationReason"/> (see <see cref="PlayerStateMachine.IncapacitationReason"/>).
    /// <list type="table">
    /// <item><term>Eliminated</term><description>motor frozen (kinematic), capsule collider disabled - the ragdoll is the body now.</description></item>
    /// <item><term>Frozen</term><description>Elsa's ice: motor frozen once grounded (a mid-air freeze falls like an ice block first).</description></item>
    /// <item><term>Grabbed</term><description>Gouki's carry: motor frozen, the ability moves the body.</description></item>
    /// <item><term>Rewinding</term><description>rewind playback: motor frozen and the rewind recorder paused.</description></item>
    /// <item><term>Teleporting / Channeling / RoundTransition</term><description>locked input, physics (gravity) still runs.</description></item>
    /// </list>
    /// The state never leaves on its own request: only <see cref="PlayerStateMachine.ReleaseIncapacitation"/>,
    /// <see cref="PlayerStateMachine.ResetToGrounded"/> or a timed incapacitation expiring (all forced) exit it.
    /// </summary>
    public sealed class IncapacitatedState : PlayerStateBase
    {
        private bool _frozeMotor;
        private bool _pendingGroundFreeze;
        private bool _disabledCollider;
        private bool _pausedRecorder;
        private float _pendingFreezeTime;

        public IncapacitatedState(PlayerStateMachine machine) : base(machine)
        {
        }

        public override PlayerStateId Id => PlayerStateId.Incapacitated;

        /// <summary>Reason whose rules are currently applied.</summary>
        public IncapacitationReason AppliedReason { get; private set; }

        public override void Enter(PlayerStateId previous)
        {
            var motor = Motor;
            if (motor != null && motor.IsSliding) motor.EndSlide();
            ApplyReason(Machine.IncapacitationReason);
        }

        /// <summary>(Re)applies the motor / collider / recorder rules of <paramref name="reason"/>.</summary>
        public void ApplyReason(IncapacitationReason reason)
        {
            AppliedReason = reason;
            var motor = Motor;
            if (motor != null)
            {
                motor.SetMode(MovementMode.Locked);
                StopMovement();
            }

            // ---- motor freeze
            bool freezeNow = reason == IncapacitationReason.Eliminated || reason == IncapacitationReason.Grabbed ||
                             reason == IncapacitationReason.Rewinding;
            bool freezeOnGround = reason == IncapacitationReason.Frozen;
            _pendingGroundFreeze = false;
            _pendingFreezeTime = 0f;

            if (freezeNow || (freezeOnGround && (motor == null || motor.IsGrounded)))
            {
                SetMotorFrozen(true);
            }
            else if (freezeOnGround)
            {
                SetMotorFrozen(false);
                _pendingGroundFreeze = true; // fall first, lock on landing
            }
            else
            {
                SetMotorFrozen(false);
            }

            // ---- collider: an eliminated player's body is the ragdoll; the upright capsule must not block anyone.
            SetColliderDisabled(reason == IncapacitationReason.Eliminated);

            // ---- rewind history must not record the playback itself
            SetRecorderPaused(reason == IncapacitationReason.Rewinding);
        }

        public override void Tick(float deltaTime)
        {
            var motor = Motor;
            if (motor == null) return;

            if (!motor.IsFrozen)
            {
                motor.SetMode(MovementMode.Locked);
                StopMovement();
            }

            if (_pendingGroundFreeze)
            {
                _pendingFreezeTime += deltaTime;
                if (motor.IsGrounded || _pendingFreezeTime >= Machine.FrozenAirborneFallTimeout)
                {
                    _pendingGroundFreeze = false;
                    SetMotorFrozen(true);
                }
            }
        }

        /// <summary>Only forced transitions (release, reset, timeout) may leave.</summary>
        public override bool CanExitTo(PlayerStateId next) => false;

        public override void Exit(PlayerStateId next)
        {
            _pendingGroundFreeze = false;
            SetMotorFrozen(false);
            SetColliderDisabled(false);
            SetRecorderPaused(false);
            AppliedReason = IncapacitationReason.None;
        }

        private void SetMotorFrozen(bool frozen)
        {
            var motor = Motor;
            if (motor == null) return;
            if (frozen)
            {
                motor.SetFrozen(true);
                _frozeMotor = true;
            }
            else if (_frozeMotor)
            {
                motor.SetFrozen(false);
                _frozeMotor = false;
            }
        }

        private void SetColliderDisabled(bool disabled)
        {
            var capsule = Owner != null ? Owner.Capsule : null;
            if (capsule == null) return;
            if (disabled)
            {
                if (capsule.enabled)
                {
                    capsule.enabled = false;
                    _disabledCollider = true;
                }
            }
            else if (_disabledCollider)
            {
                capsule.enabled = true;
                _disabledCollider = false;
            }
        }

        private void SetRecorderPaused(bool paused)
        {
            var recorder = Owner != null ? Owner.Rewind : null;
            if (recorder == null) return;
            if (paused)
            {
                if (!recorder.Paused)
                {
                    recorder.Paused = true;
                    _pausedRecorder = true;
                }
            }
            else if (_pausedRecorder)
            {
                recorder.Paused = false;
                _pausedRecorder = false;
            }
        }
    }
}
