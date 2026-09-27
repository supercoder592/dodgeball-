using System.Collections.Generic;
using DodgeballUltra.Core;
using NUnit.Framework;

namespace DodgeballUltra.Tests.Core
{
    public class FiniteStateMachineTests
    {
        private enum S { A, B, C }

        private sealed class TestState : IFsmState<S>
        {
            private readonly List<string> _log;
            public bool AllowExit = true;
            public System.Action<FiniteStateMachine<S>> OnEnterAction;
            public FiniteStateMachine<S> Machine;

            public TestState(S id, List<string> log) { Id = id; _log = log; }
            public S Id { get; }
            public void Enter(S previous) { _log.Add($"enter {Id}"); OnEnterAction?.Invoke(Machine); }
            public void Exit(S next) => _log.Add($"exit {Id}");
            public void Tick(float dt) => _log.Add($"tick {Id}");
            public void FixedTick(float dt) { }
            public bool CanExitTo(S next) => AllowExit;
        }

        [Test]
        public void Transitions_CallExitThenEnter_AndRaiseEvent()
        {
            var log = new List<string>();
            var fsm = new FiniteStateMachine<S>();
            fsm.Register(new TestState(S.A, log));
            fsm.Register(new TestState(S.B, log));
            S prev = S.C, next = S.C;
            fsm.StateChanged += (p, n) => { prev = p; next = n; };
            fsm.Start(S.A);
            Assert.IsTrue(fsm.ChangeState(S.B));
            CollectionAssert.AreEqual(new[] { "enter A", "exit A", "enter B" }, log);
            Assert.AreEqual(S.A, prev);
            Assert.AreEqual(S.B, next);
            Assert.AreEqual(S.A, fsm.PreviousId);
        }

        [Test]
        public void Veto_BlocksUnlessForced()
        {
            var log = new List<string>();
            var fsm = new FiniteStateMachine<S>();
            var a = new TestState(S.A, log) { AllowExit = false };
            fsm.Register(a);
            fsm.Register(new TestState(S.B, log));
            fsm.Start(S.A);
            Assert.IsFalse(fsm.ChangeState(S.B));
            Assert.IsTrue(fsm.IsIn(S.A));
            Assert.IsTrue(fsm.ChangeState(S.B, force: true));
            Assert.IsTrue(fsm.IsIn(S.B));
        }

        [Test]
        public void TransitionRequestedDuringEnter_IsQueued()
        {
            var log = new List<string>();
            var fsm = new FiniteStateMachine<S>();
            var b = new TestState(S.B, log) { Machine = fsm };
            b.OnEnterAction = m => m.ChangeState(S.C);
            fsm.Register(new TestState(S.A, log));
            fsm.Register(b);
            fsm.Register(new TestState(S.C, log));
            fsm.Start(S.A);
            fsm.ChangeState(S.B);
            Assert.IsTrue(fsm.IsIn(S.C));
            CollectionAssert.AreEqual(new[] { "enter A", "exit A", "enter B", "exit B", "enter C" }, log);
        }

        [Test]
        public void TimeInState_ResetsOnTransition()
        {
            var log = new List<string>();
            var fsm = new FiniteStateMachine<S>();
            fsm.Register(new TestState(S.A, log));
            fsm.Register(new TestState(S.B, log));
            fsm.Start(S.A);
            fsm.Tick(0.5f);
            Assert.AreEqual(0.5f, fsm.TimeInState, 1e-6f);
            fsm.ChangeState(S.B);
            Assert.AreEqual(0f, fsm.TimeInState, 1e-6f);
        }
    }
}
