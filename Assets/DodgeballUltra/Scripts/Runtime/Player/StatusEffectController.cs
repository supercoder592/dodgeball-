using System;
using UnityEngine;

namespace DodgeballUltra.Player
{
    /// <summary>
    /// CONTRACT (kernel) - timed status effects keyed by (type, source). Duration &lt;= 0 means permanent until removed.
    /// Applies side-effects: Frozen -> Incapacitate(Frozen) + motor frozen + CharacterVisual.SetFrozen;
    /// Slow/Haste -> motor speed modifier; Slippery -> motor traction; Cloaked -> CharacterVisual.SetCloaked;
    /// Stunned mirrors the state machine. Publishes StatusEffectChangedEvent.
    /// <para>Owner module: Player.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class StatusEffectController : MonoBehaviour
    {
        public event Action<StatusEffectType, bool> Changed;

        // ------------------------------------------------------------------ IMPLEMENT: Player module
        public void Initialize(DodgeballPlayer owner) => throw new NotImplementedException();

        /// <summary>
        /// Applies (or refreshes) an effect. <paramref name="source"/> distinguishes stacks from different causes;
        /// the same (type, source) pair refreshes duration and keeps the larger magnitude.
        /// </summary>
        public void Apply(StatusEffectType type, float duration, float magnitude = 1f, object source = null) => throw new NotImplementedException();

        /// <summary>Removes the effect from <paramref name="source"/> (or every source when null).</summary>
        public void Remove(StatusEffectType type, object source = null) => throw new NotImplementedException();

        public void RemoveAll() => throw new NotImplementedException();

        public bool Has(StatusEffectType type) => throw new NotImplementedException();

        /// <summary>Strongest active magnitude for <paramref name="type"/> (0 when absent).</summary>
        public float GetMagnitude(StatusEffectType type) => throw new NotImplementedException();

        /// <summary>Longest remaining duration for <paramref name="type"/> (0 when absent, +inf when permanent).</summary>
        public float GetRemaining(StatusEffectType type) => throw new NotImplementedException();

        /// <summary>Combined Slow/Haste multiplier for the motor.</summary>
        public float MoveSpeedMultiplier => throw new NotImplementedException();

        public void Tick(float deltaTime) => throw new NotImplementedException();
    }
}
