using System;
using System.Collections.Generic;

namespace DodgeballUltra.Core
{
    /// <summary>A state for <see cref="FiniteStateMachine{TStateId}"/>.</summary>
    public interface IFsmState<TStateId> where TStateId : struct, Enum
    {
        /// <summary>Identifier of this state.</summary>
        TStateId Id { get; }

        /// <summary>Called when the state becomes active.</summary>
        void Enter(TStateId previous);

        /// <summary>Called when the state is left.</summary>
        void Exit(TStateId next);

        /// <summary>Per-frame update (variable delta).</summary>
        void Tick(float deltaTime);

        /// <summary>Fixed-step update (physics delta).</summary>
        void FixedTick(float fixedDeltaTime);

        /// <summary>Return false to veto a non-forced transition out of this state.</summary>
        bool CanExitTo(TStateId next);
    }

    /// <summary>
    /// Minimal, allocation-free finite state machine keyed by an enum.
    /// Transitions requested during Enter/Exit are queued and applied after the current transition finishes.
    /// </summary>
    public sealed class FiniteStateMachine<TStateId> where TStateId : struct, Enum
    {
        private readonly Dictionary<TStateId, IFsmState<TStateId>> _states = new Dictionary<TStateId, IFsmState<TStateId>>();
        private IFsmState<TStateId> _current;
        private bool _transitioning;
        private bool _hasPending;
        private TStateId _pending;
        private bool _pendingForce;

        /// <summary>(previous, next) raised after a transition completes.</summary>
        public event Action<TStateId, TStateId> StateChanged;

        public bool HasState => _current != null;
        public TStateId CurrentId => _current != null ? _current.Id : default;
        public TStateId PreviousId { get; private set; }
        public IFsmState<TStateId> Current => _current;

        /// <summary>Seconds spent in the current state (advanced by <see cref="Tick"/>).</summary>
        public float TimeInState { get; private set; }

        public void Register(IFsmState<TStateId> state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            _states[state.Id] = state;
        }

        public bool TryGetState(TStateId id, out IFsmState<TStateId> state) => _states.TryGetValue(id, out state);

        /// <summary>Enters the initial state without calling Exit on anything.</summary>
        public void Start(TStateId initial)
        {
            if (!_states.TryGetValue(initial, out var state))
                throw new InvalidOperationException($"State {initial} is not registered.");
            _current = state;
            PreviousId = initial;
            TimeInState = 0f;
            _transitioning = true;
            state.Enter(initial);
            _transitioning = false;
            FlushPending();
        }

        /// <summary>
        /// Requests a transition. Non-forced transitions can be vetoed by <see cref="IFsmState{TStateId}.CanExitTo"/>.
        /// Returns true if the transition happened (or was queued during another transition).
        /// </summary>
        public bool ChangeState(TStateId next, bool force = false)
        {
            if (!_states.TryGetValue(next, out var nextState))
                throw new InvalidOperationException($"State {next} is not registered.");

            if (_transitioning)
            {
                _hasPending = true;
                _pending = next;
                _pendingForce = force;
                return true;
            }

            if (_current == null)
            {
                Start(next);
                return true;
            }

            if (EqualityComparer<TStateId>.Default.Equals(_current.Id, next)) return false;
            if (!force && !_current.CanExitTo(next)) return false;

            _transitioning = true;
            var prev = _current;
            prev.Exit(next);
            PreviousId = prev.Id;
            _current = nextState;
            TimeInState = 0f;
            nextState.Enter(prev.Id);
            _transitioning = false;
            StateChanged?.Invoke(prev.Id, next);
            FlushPending();
            return true;
        }

        public void Tick(float deltaTime)
        {
            if (_current == null) return;
            TimeInState += deltaTime;
            _current.Tick(deltaTime);
        }

        public void FixedTick(float fixedDeltaTime) => _current?.FixedTick(fixedDeltaTime);

        public bool IsIn(TStateId id) => _current != null && EqualityComparer<TStateId>.Default.Equals(_current.Id, id);

        private void FlushPending()
        {
            // Bounded to avoid infinite ping-pong between states that keep requesting each other on Enter.
            for (int guard = 0; _hasPending && guard < 8; guard++)
            {
                _hasPending = false;
                ChangeState(_pending, _pendingForce);
            }
            _hasPending = false;
        }
    }
}
