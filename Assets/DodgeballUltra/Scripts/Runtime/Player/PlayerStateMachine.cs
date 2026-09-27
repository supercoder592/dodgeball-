using System;
using UnityEngine;

namespace DodgeballUltra.Player
{
    /// <summary>
    /// CONTRACT (kernel) - the player's behaviour state machine (spec states: Grounded, Airborne, Sprinting, Sliding,
    /// ChargingThrow, Catching, Stunned, Incapacitated). Built on <c>DodgeballUltra.Core.FiniteStateMachine</c>.
    /// Each state reads <see cref="DodgeballPlayer.Intent"/> and commands the motor / combat controller.
    /// Publishes <c>PlayerStateChangedEvent</c> on every transition.
    /// <para>Owner module: Player.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PlayerStateMachine : MonoBehaviour
    {
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

        // ------------------------------------------------------------------ IMPLEMENT: Player module

        /// <summary>Creates and registers the eight state objects for <paramref name="owner"/> and enters Grounded.</summary>
        public void Initialize(DodgeballPlayer owner) => throw new NotImplementedException();

        /// <summary>Requests a transition; non-forced transitions may be vetoed by the current state.</summary>
        public bool ChangeState(PlayerStateId next, bool force = false) => throw new NotImplementedException();

        /// <summary>Stuns for <paramref name="duration"/> seconds (extends, never shortens). Cancels charging/catching and interrupts abilities.</summary>
        public void Stun(float duration) => throw new NotImplementedException();

        /// <summary>
        /// Enters Incapacitated for <paramref name="reason"/>. <paramref name="duration"/> &lt;= 0 means until
        /// <see cref="ReleaseIncapacitation"/> is called with the same reason.
        /// </summary>
        public void Incapacitate(IncapacitationReason reason, float duration = 0f) => throw new NotImplementedException();

        /// <summary>Leaves Incapacitated if it was caused by <paramref name="reason"/>.</summary>
        public void ReleaseIncapacitation(IncapacitationReason reason) => throw new NotImplementedException();

        /// <summary>Clears stun/incapacitation and returns to Grounded (round reset, revive).</summary>
        public void ResetToGrounded() => throw new NotImplementedException();

        public void Tick(float deltaTime) => throw new NotImplementedException();
        public void FixedTick(float fixedDeltaTime) => throw new NotImplementedException();
    }
}
