using System;
using DodgeballUltra.Core;
using DodgeballUltra.Player;
using UnityEngine;

namespace DodgeballUltra.Abilities.Heroes
{
    /// <summary>
    /// Gouki - PASSIVE [Thick Hide] (厚皮): 200 HP, so he takes two standard (100 damage) hits to put down, and his heavy
    /// frame shrugs off half of every knockback.
    /// <para>
    /// The 200 HP comes from <c>CharacterData.maxHp</c> (<see cref="GameConstants.ThickHideMaxHp"/>). As a safety net the
    /// passive also raises <see cref="PlayerHealth.MaxHp"/> to <see cref="minimumMaxHp"/> if a mis-configured hero asset
    /// gives less (refilling only when the player was at full health, so it never heals mid-round).
    /// </para>
    /// <para>
    /// Knockback: an <see cref="IIncomingHitFilter"/> registered on Gouki's <see cref="PlayerHealth"/> multiplies
    /// <see cref="HitContext.KnockbackImpulse"/> by <see cref="knockbackMultiplier"/> (0.5) for ball and ability hits. It runs
    /// late (<see cref="filterPriority"/>) so cancelling filters (invulnerability, shields) decide first.
    /// </para>
    /// </summary>
    [Serializable]
    public sealed class GoukiThickHide : AbilityBase, IIncomingHitFilter
    {
        [Header("Thick Hide")]
        [Tooltip("Gouki's maximum HP is raised to at least this value (spec: 200 = survives one standard hit, falls on the second).")]
        [Min(1f)] public float minimumMaxHp = GameConstants.ThickHideMaxHp;

        [Tooltip("Enforce the minimum max HP even if the CharacterData asset specifies less.")]
        public bool enforceMinimumMaxHp = true;

        [Tooltip("Multiplier on incoming knockback impulses (spec: half).")]
        [Range(0f, 1f)] public float knockbackMultiplier = 0.5f;

        [Tooltip("Hit-filter priority (lower runs first). Runs late so cancelling filters decide first.")]
        public int filterPriority = 200;

        [NonSerialized] private PlayerHealth _registeredOn;
        [NonSerialized] private bool _verified;

        /// <summary>Required public parameterless constructor (SerializeReference / roster factory).</summary>
        public GoukiThickHide() { }

        /// <inheritdoc />
        public int Priority => filterPriority;

        protected override void OnEquip()
        {
            Register();
            EnsureMaxHp();
        }

        protected override void OnUnequip()
        {
            if (_registeredOn != null) _registeredOn.RemoveHitFilter(this);
            _registeredOn = null;
            _verified = false;
        }

        /// <summary>Passives are never cast.</summary>
        protected override void OnCast() { }

        protected override void OnTick(float deltaTime)
        {
            // Robust to initialisation order: Health may be (re)initialised after the abilities were equipped, which could
            // clear its filter list, so the first tick re-registers unconditionally.
            if (!_verified || _registeredOn != Owner.Health)
            {
                _verified = true;
                Register(true);
            }
            EnsureMaxHp();
        }

        protected override void OnRoundReset()
        {
            Register(true);
            EnsureMaxHp();
        }

        /// <inheritdoc />
        public void FilterHit(ref HitContext hit)
        {
            if (hit.Cancelled || hit.Victim != Owner) return;
            hit.KnockbackImpulse *= Mathf.Clamp01(knockbackMultiplier);
        }

        /// <summary>A passive: the AI never "uses" it.</summary>
        public override float EvaluateAIUtility(in AbilityAIContext ctx) => 0f;

        private void Register(bool force = false)
        {
            var health = Owner != null ? Owner.Health : null;
            if (!force && health == _registeredOn) return;
            if (_registeredOn != null) _registeredOn.RemoveHitFilter(this); // remove-then-add: never duplicated
            _registeredOn = health;
            if (health != null) health.AddHitFilter(this);
        }

        private void EnsureMaxHp()
        {
            if (!enforceMinimumMaxHp) return;
            var health = Owner != null ? Owner.Health : null;
            if (health == null || health.MaxHp <= 0f || health.MaxHp >= minimumMaxHp - 0.01f) return;
            bool wasFull = health.CurrentHp >= health.MaxHp - 0.01f && !health.IsEliminated;
            health.SetMaxHp(minimumMaxHp, wasFull);
        }
    }
}
