using System;
using DodgeballUltra.Abilities;
using DodgeballUltra.Core;
using DodgeballUltra.Events;
using DodgeballUltra.Player.States;
using UnityEngine;

namespace DodgeballUltra.Player
{
    /// <summary>
    /// CONTRACT (kernel) - the player's behaviour state machine (spec states: Grounded, Airborne, Sprinting, Sliding,
    /// ChargingThrow, Catching, Stunned, Incapacitated). Built on <c>DodgeballUltra.Core.FiniteStateMachine</c>.
    /// Each state reads <see cref="DodgeballPlayer.Intent"/> and commands the motor / combat controller.
    /// Publishes <c>PlayerStateChangedEvent</c> on every transition.
    /// <para>Owner module: Player.</para>
    /// <para>
    /// Transition graph (non-forced transitions are requested by the states themselves; Stun/Incapacitate/Reset force):
    /// <code>
    ///   Grounded  &lt;-&gt; Sprinting        (sprint held while moving forward-ish)
    ///   Grounded/Sprinting -&gt; Airborne   (jump, or no ground for longer than the coyote time)
    ///   Grounded/Sprinting -&gt; Sliding    (slide while moving fast / from a sprint)  -&gt; Grounded when the slide ends
    ///   Grounded/Sprinting/Airborne -&gt; ChargingThrow (throw pressed with a ball)     -&gt; release -&gt; locomotion
    ///   Grounded/Sprinting/Airborne -&gt; Catching      (catch pressed, hands free)     -&gt; window closed -&gt; locomotion
    ///   any -&gt; Stunned (Stun)              -&gt; locomotion when the stun expires
    ///   any -&gt; Incapacitated (Incapacitate) -&gt; only ReleaseIncapacitation / ResetToGrounded / timeout leave it
    /// </code>
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PlayerStateMachine : MonoBehaviour
    {
        // ------------------------------------------------------------------ tuning

        [Header("Locomotion transitions")]
        [Tooltip("Seconds without ground contact before Grounded/Sprinting fall into Airborne (coyote time).")]
        [Min(0f)] [SerializeField] private float coyoteTime = 0.1f;

        [Tooltip("Minimum seconds in the air before a landing is accepted (prevents re-landing on the jump frame).")]
        [Min(0f)] [SerializeField] private float minAirTime = 0.05f;

        [Tooltip("Stick deflection (0..1) needed to start sprinting.")]
        [Range(0f, 1f)] [SerializeField] private float sprintMinInput = 0.5f;

        [Tooltip("Stick deflection (0..1) below which a sprint stops.")]
        [Range(0f, 1f)] [SerializeField] private float sprintStopInput = 0.25f;

        [Tooltip("Dot(move direction, body forward) needed to start a sprint (1 = straight ahead, 0 = sideways).")]
        [Range(-1f, 1f)] [SerializeField] private float sprintForwardDot = 0.3f;

        [Tooltip("Minimum planar speed, as a fraction of walkSpeed, to start a slide from Grounded ('moving fast').")]
        [Range(0f, 2f)] [SerializeField] private float slideMinSpeedFraction = 0.6f;

        [Tooltip("When standing still (Grounded, no stick input) the body slowly turns to face the aim direction, " +
                 "keeping the catch cone pointed at the action.")]
        [SerializeField] private bool faceAimWhileIdle = true;

        [Tooltip("Snap the body toward the aim when releasing a throw whose aim differs more than this (deg) from the facing. " +
                 "Prevents over-the-shoulder throws from quick taps.")]
        [Range(0f, 180f)] [SerializeField] private float throwSnapFacingAngle = 100f;

        [Header("Incapacitation")]
        [Tooltip("Forward speed (m/s) of the ball dropped when eliminated or grabbed.")]
        [Min(0f)] [SerializeField] private float dropBallForwardSpeed = 1.2f;

        [Tooltip("Upward speed (m/s) of the ball dropped when eliminated or grabbed.")]
        [Min(0f)] [SerializeField] private float dropBallUpSpeed = 1.8f;

        [Tooltip("A player frozen in mid-air falls (as an ice block) and is locked in place once grounded, or after this many seconds.")]
        [Min(0f)] [SerializeField] private float frozenAirborneFallTimeout = 1.5f;

        // ------------------------------------------------------------------ contract state

        public PlayerStateId Current { get; private set; } = PlayerStateId.Grounded;
        public PlayerStateId Previous { get; private set; } = PlayerStateId.Grounded;
        public float TimeInState { get; private set; }
        public IncapacitationReason IncapacitationReason { get; private set; } = IncapacitationReason.None;

        /// <summary>Seconds of stun left (0 when not stunned).</summary>
        public float StunRemaining { get; private set; }

        /// <summary>False while Stunned or Incapacitated.</summary>
        public bool CanAct => Current != PlayerStateId.Stunned && Current != PlayerStateId.Incapacitated;

        /// <summary>(previous, current)</summary>
        public event Action<PlayerStateId, PlayerStateId> StateChanged;

        public bool IsIn(PlayerStateId id) => Current == id;

        // ------------------------------------------------------------------ additional public API

        public DodgeballPlayer Owner { get; private set; }
        public bool IsInitialized => _fsm != null;

        /// <summary>Seconds left before a timed incapacitation auto-releases (0 = untimed or not incapacitated).</summary>
        public float IncapacitationRemaining => _incapTimed ? Mathf.Max(0f, _incapRemaining) : 0f;

        public float CoyoteTime => coyoteTime;
        public float MinAirTime => minAirTime;
        public float SprintMinInput => sprintMinInput;
        public float SprintStopInput => sprintStopInput;
        public float SprintForwardDot => sprintForwardDot;
        public float SlideMinSpeedFraction => slideMinSpeedFraction;
        public bool FaceAimWhileIdle => faceAimWhileIdle;
        public float ThrowSnapFacingAngle => throwSnapFacingAngle;
        public float FrozenAirborneFallTimeout => frozenAirborneFallTimeout;

        /// <summary>The state object registered for <paramref name="id"/> (null before Initialize).</summary>
        public PlayerStateBase GetState(PlayerStateId id)
        {
            int i = (int)id;
            return i >= 0 && i < _states.Length ? _states[i] : null;
        }

        // ------------------------------------------------------------------ internals

        private FiniteStateMachine<PlayerStateId> _fsm;
        private readonly PlayerStateBase[] _states = new PlayerStateBase[8];
        private bool _incapTimed;
        private float _incapRemaining;
        private bool _mirroringStun;

        // ------------------------------------------------------------------ lifecycle

        /// <summary>Creates and registers the eight state objects for <paramref name="owner"/> and enters Grounded.</summary>
        public void Initialize(DodgeballPlayer owner)
        {
            if (_fsm != null)
            {
                // Re-initialisation (hero swap): leave whatever we were doing cleanly first.
                Owner = owner;
                ResetToGrounded();
                return;
            }

            Owner = owner;
            _fsm = new FiniteStateMachine<PlayerStateId>();
            Register(new GroundedState(this));
            Register(new AirborneState(this));
            Register(new SprintingState(this));
            Register(new SlidingState(this));
            Register(new ChargingThrowState(this));
            Register(new CatchingState(this));
            Register(new StunnedState(this));
            Register(new IncapacitatedState(this));
            _fsm.StateChanged += OnFsmStateChanged;

            StunRemaining = 0f;
            IncapacitationReason = IncapacitationReason.None;
            Current = Previous = PlayerStateId.Grounded;
            TimeInState = 0f;
            _fsm.Start(PlayerStateId.Grounded);
        }

        /// <summary>
        /// Replaces the behaviour of one state (extensibility hook for modes/mutators). The replacement must report the same
        /// <see cref="PlayerStateBase.Id"/>. Not allowed for the state currently active.
        /// </summary>
        public bool OverrideState(PlayerStateBase state)
        {
            if (state == null || _fsm == null || state.Machine != this) return false;
            if (_fsm.IsIn(state.Id)) return false;
            Register(state);
            return true;
        }

        private void Register(PlayerStateBase state)
        {
            _states[(int)state.Id] = state;
            _fsm.Register(state);
        }

        private void OnDestroy()
        {
            if (_fsm != null) _fsm.StateChanged -= OnFsmStateChanged;
        }

        // ------------------------------------------------------------------ transitions

        /// <summary>Requests a transition; non-forced transitions may be vetoed by the current state.</summary>
        public bool ChangeState(PlayerStateId next, bool force = false)
        {
            if (_fsm == null) return false;
            return _fsm.ChangeState(next, force);
        }

        /// <summary>Stuns for <paramref name="duration"/> seconds (extends, never shortens). Cancels charging/catching and interrupts abilities.</summary>
        public void Stun(float duration)
        {
            if (_fsm == null || duration <= 0f || float.IsNaN(duration)) return;
            if (Current == PlayerStateId.Incapacitated) return; // a harder lock already applies
            if (Owner != null && Owner.Health != null && Owner.Health.IsEliminated) return;

            StunRemaining = Mathf.Max(StunRemaining, duration);

            var combat = Owner != null ? Owner.Combat : null;
            if (combat != null)
            {
                combat.CancelCharge();
                combat.CancelCatch();
            }
            var abilities = Owner != null ? Owner.Abilities : null;
            if (abilities != null) abilities.InterruptAll(InterruptReason.Stunned);

            MirrorStunStatus();
            if (Current != PlayerStateId.Stunned) _fsm.ChangeState(PlayerStateId.Stunned, true);
        }

        /// <summary>
        /// Enters Incapacitated for <paramref name="reason"/>. <paramref name="duration"/> &lt;= 0 means until
        /// <see cref="ReleaseIncapacitation"/> is called with the same reason.
        /// </summary>
        /// <remarks>
        /// Elimination is final: while incapacitated as <see cref="IncapacitationReason.Eliminated"/> no other reason can
        /// take over. Any other reason replaces the current one (latest wins).
        /// </remarks>
        public void Incapacitate(IncapacitationReason reason, float duration = 0f)
        {
            if (_fsm == null) return;
            if (Current == PlayerStateId.Incapacitated && IncapacitationReason == IncapacitationReason.Eliminated &&
                reason != IncapacitationReason.Eliminated)
                return;

            bool sameReason = Current == PlayerStateId.Incapacitated && IncapacitationReason == reason;
            IncapacitationReason = reason;
            _incapTimed = duration > 0f;
            _incapRemaining = _incapTimed ? duration : 0f;

            // A harder lock supersedes any stun.
            if (StunRemaining > 0f)
            {
                StunRemaining = 0f;
                ClearStunStatus();
            }

            ApplyIncapacitationSideEffects(reason, sameReason);

            if (Current == PlayerStateId.Incapacitated)
            {
                // Already locked: re-apply the (new) reason's motor/collider rules.
                if (_states[(int)PlayerStateId.Incapacitated] is IncapacitatedState incap) incap.ApplyReason(reason);
            }
            else
            {
                _fsm.ChangeState(PlayerStateId.Incapacitated, true);
            }
        }

        /// <summary>Leaves Incapacitated if it was caused by <paramref name="reason"/>.</summary>
        public void ReleaseIncapacitation(IncapacitationReason reason)
        {
            if (_fsm == null || Current != PlayerStateId.Incapacitated || IncapacitationReason != reason) return;
            IncapacitationReason = IncapacitationReason.None;
            _incapTimed = false;
            _incapRemaining = 0f;
            _fsm.ChangeState(LocomotionFallback(), true);
        }

        /// <summary>Clears stun/incapacitation and returns to Grounded (round reset, revive).</summary>
        public void ResetToGrounded()
        {
            if (_fsm == null) return;
            StunRemaining = 0f;
            ClearStunStatus();
            IncapacitationReason = IncapacitationReason.None;
            _incapTimed = false;
            _incapRemaining = 0f;

            if (Current != PlayerStateId.Grounded) _fsm.ChangeState(PlayerStateId.Grounded, true);

            // Whatever happened before, the motor must be free and walking again.
            var motor = Owner != null ? Owner.Motor : null;
            if (motor != null)
            {
                motor.SetFrozen(false);
                if (motor.IsSliding) motor.EndSlide();
                motor.SetMode(MovementMode.Walk);
            }
        }

        // ------------------------------------------------------------------ ticking

        public void Tick(float deltaTime)
        {
            if (_fsm == null) return;

            if (StunRemaining > 0f) StunRemaining = Mathf.Max(0f, StunRemaining - deltaTime);

            if (Current == PlayerStateId.Incapacitated && _incapTimed)
            {
                _incapRemaining -= deltaTime;
                if (_incapRemaining <= 0f)
                {
                    ReleaseIncapacitation(IncapacitationReason);
                }
            }

            _fsm.Tick(deltaTime);
            TimeInState = _fsm.TimeInState;
        }

        public void FixedTick(float fixedDeltaTime)
        {
            if (_fsm == null) return;
            _fsm.FixedTick(fixedDeltaTime);
        }

        // ------------------------------------------------------------------ helpers used by the states

        /// <summary>Grounded when the motor has ground contact, otherwise Airborne.</summary>
        public PlayerStateId LocomotionFallback()
        {
            var motor = Owner != null ? Owner.Motor : null;
            return motor == null || motor.IsGrounded ? PlayerStateId.Grounded : PlayerStateId.Airborne;
        }

        /// <summary>Velocity given to a ball dropped on elimination / grab: a small forward-up toss off the body.</summary>
        public Vector3 GetDropBallVelocity()
        {
            if (Owner == null) return Vector3.up * dropBallUpSpeed;
            return Owner.Forward * dropBallForwardSpeed + Vector3.up * dropBallUpSpeed + Owner.Velocity * 0.5f;
        }

        /// <summary>Removes the Stunned status this machine mirrored (called when the Stunned state exits).</summary>
        internal void ClearStunStatus()
        {
            var status = Owner != null ? Owner.Status : null;
            if (status == null) return;
            _mirroringStun = true;
            try
            {
                status.Remove(StatusEffectType.Stunned, this);
            }
            finally
            {
                _mirroringStun = false;
            }
        }

        /// <summary>True while this machine itself is writing the Stunned status (lets StatusEffectController avoid loops).</summary>
        internal bool IsMirroringStun => _mirroringStun;

        private void MirrorStunStatus()
        {
            var status = Owner != null ? Owner.Status : null;
            if (status == null) return;
            _mirroringStun = true;
            try
            {
                status.Apply(StatusEffectType.Stunned, StunRemaining, 1f, this);
            }
            finally
            {
                _mirroringStun = false;
            }
        }

        private void ApplyIncapacitationSideEffects(IncapacitationReason reason, bool sameReason)
        {
            if (Owner == null || sameReason) return;
            var combat = Owner.Combat;
            var abilities = Owner.Abilities;

            // Nothing can be thrown or caught while incapacitated.
            if (combat != null)
            {
                combat.CancelCharge();
                combat.CancelCatch();
            }

            switch (reason)
            {
                case IncapacitationReason.Frozen:
                    if (abilities != null) abilities.InterruptAll(InterruptReason.Frozen);
                    break;

                case IncapacitationReason.Eliminated:
                    if (abilities != null) abilities.InterruptAll(InterruptReason.Eliminated);
                    if (combat != null && combat.HasBall) combat.DropBall(GetDropBallVelocity());
                    break;

                case IncapacitationReason.Grabbed:
                    // A tackled player loses the ball and any interruptible channel.
                    if (abilities != null) abilities.InterruptAll(InterruptReason.Stunned);
                    if (combat != null && combat.HasBall) combat.DropBall(GetDropBallVelocity());
                    break;

                case IncapacitationReason.RoundTransition:
                    if (abilities != null) abilities.InterruptAll(InterruptReason.RoundEnded);
                    break;

                // Teleporting / Channeling / Rewinding are caused by the player's own (or an ally's) ability:
                // interrupting abilities here would cancel the very ability that requested the lock.
            }
        }

        private void OnFsmStateChanged(PlayerStateId previous, PlayerStateId current)
        {
            Previous = previous;
            Current = current;
            TimeInState = 0f;

            if (previous == PlayerStateId.Incapacitated && current != PlayerStateId.Incapacitated)
            {
                IncapacitationReason = IncapacitationReason.None;
                _incapTimed = false;
                _incapRemaining = 0f;
            }

            StateChanged?.Invoke(previous, current);
            if (Owner != null)
                GameEvents.Publish(new PlayerStateChangedEvent { Player = Owner, Previous = previous, Current = current });
        }
    }
}
