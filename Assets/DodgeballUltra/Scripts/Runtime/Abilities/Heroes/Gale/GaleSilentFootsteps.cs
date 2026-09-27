using System;
using DodgeballUltra.Player;

namespace DodgeballUltra.Abilities.Heroes
{
    /// <summary>
    /// Gale's passive <b>[Silent Footsteps]</b>: "silences movement audio and hides mini-map ping".
    /// <para>
    /// Applies a permanent <see cref="StatusEffectType.SilentFootsteps"/> status (duration 0 = until removed, source = this
    /// ability) while equipped. The status is the single source of truth other modules read: the footstep / slide / landing
    /// audio skips players that have it, and the enemy mini-map hides their ping. Because round resets and eliminations may
    /// clear every status, the passive tick re-applies it whenever it went missing; it is removed on unequip.
    /// </para>
    /// </summary>
    [Serializable]
    public sealed class GaleSilentFootsteps : AbilityBase
    {
        public GaleSilentFootsteps() { }

        /// <summary>
        /// Applied right away on a live player (hero swap). During spawn the sub-systems may still be wiring up, so the
        /// first passive tick applies it instead (one frame later, before any gameplay happens).
        /// </summary>
        protected override void OnEquip()
        {
            if (Owner.IsInitialized) EnsureApplied();
        }

        protected override void OnUnequip()
        {
            if (Owner != null && Owner.Status != null) Owner.Status.Remove(StatusEffectType.SilentFootsteps, this);
        }

        /// <summary>Passives never cast.</summary>
        protected override void OnCast() { }

        /// <summary>Every frame: cheap presence check, re-applied after status wipes (round reset, revive...).</summary>
        protected override void OnTick(float deltaTime) => EnsureApplied();

        protected override void OnRoundReset() => EnsureApplied();

        public override float EvaluateAIUtility(in AbilityAIContext ctx) => 0f;

        private void EnsureApplied()
        {
            var status = Owner != null ? Owner.Status : null;
            if (status == null || status.Has(StatusEffectType.SilentFootsteps)) return;
            status.Apply(StatusEffectType.SilentFootsteps, 0f, 1f, this);
        }
    }
}
