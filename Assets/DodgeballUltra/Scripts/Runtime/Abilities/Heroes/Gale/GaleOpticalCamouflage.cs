using System;
using DodgeballUltra.Audio;
using DodgeballUltra.Combat;
using DodgeballUltra.Player;
using DodgeballUltra.VFX;
using UnityEngine;

namespace DodgeballUltra.Abilities.Heroes
{
    /// <summary>
    /// Gale's skill <b>[Optical Camouflage]</b>: "cloak 4 s (+20% speed); throws from stealth gain +30% ball speed and
    /// reveal location (CD 14 s)".
    /// <para>
    /// OnCast applies <see cref="StatusEffectType.Cloaked"/> and <see cref="StatusEffectType.Haste"/> (magnitude 0.2) for the
    /// ability's duration (AbilityData.duration, spec 4 s). The status controller turns Cloaked into the refraction look
    /// (<c>CharacterVisual.SetCloaked</c>: near-invisible for enemies, translucent ghost for allies) and Haste into motor speed.
    /// </para>
    /// <para>
    /// While cloaked, this ability's <see cref="IThrowModifier"/> multiplies throw speed by 1.3 and flags the throw with
    /// <see cref="ThrowParams.RevealsThrower"/>. When such a throw is committed Gale decloaks (cloak + haste removed, a short
    /// <see cref="StatusEffectType.Revealed"/> marks her position) and the ability ends, starting the 14 s cooldown.
    /// The modifier itself is side-effect free so throw previews never break the cloak.
    /// </para>
    /// </summary>
    [Serializable]
    public sealed class GaleOpticalCamouflage : AbilityBase, IThrowModifier
    {
        /// <summary>Used only if AbilityData.duration is left at 0 (misconfigured asset): the spec cloak length.</summary>
        private const float FallbackDuration = 4f;

        [Header("Cloak")]
        [Tooltip("Movement speed bonus while cloaked (0.2 = +20%). Spec: +20%.")]
        [Range(0f, 1f)] public float hasteBonus = 0.2f;

        [Header("Ambush throw")]
        [Tooltip("Ball speed multiplier for throws made from stealth. Spec: +30% (x1.3).")]
        [Range(1f, 2f)] public float stealthThrowSpeedMultiplier = 1.3f;

        [Tooltip("Seconds Gale stays Revealed (visible on the enemy mini-map, cannot re-cloak) after an ambush throw.")]
        [Range(0f, 5f)] public float revealDuration = 1.5f;

        [Tooltip("Passing to a teammate from stealth also breaks the cloak (and gets no speed bonus when off).")]
        public bool passesReveal;

        [Header("Feedback")]
        [Tooltip("Tint of the refraction shimmer when Gale cloaks / decloaks.")]
        public Color shimmerTint = new Color(0.78f, 0.86f, 1f, 1f);

        [NonSerialized] private PlayerCombatController _registeredOn;

        public GaleOpticalCamouflage() { }

        /// <summary>After the base charge/overcharge modifiers, before perfect-catch style bonuses.</summary>
        public int Order => 50;

        /// <summary>True while the cloak from this ability is up.</summary>
        public bool IsCloaked => IsActive && Owner != null && Owner.Status != null && Owner.Status.Has(StatusEffectType.Cloaked);

        /// <summary>Registers the stealth-throw modifier once the combat controller is bound (OnCast re-checks it anyway).</summary>
        protected override void OnEquip()
        {
            if (Owner.IsInitialized || (Owner.Combat != null && Owner.Combat.Owner == Owner)) EnsureModifierRegistered();
        }

        protected override void OnUnequip()
        {
            RemoveCloakStatuses();
            if (_registeredOn != null) _registeredOn.RemoveThrowModifier(this);
            _registeredOn = null;
        }

        protected override bool CanActivateCustom(out AbilityFailReason reason)
        {
            // Revealed players cannot cloak (e.g. right after an ambush throw, or revealed by an enemy effect).
            if (Owner.Status == null || Owner.Status.Has(StatusEffectType.Revealed))
            {
                reason = AbilityFailReason.Custom;
                return false;
            }
            reason = AbilityFailReason.None;
            return true;
        }

        protected override void OnCast()
        {
            EnsureModifierRegistered();

            float duration = Duration;
            if (duration <= 0f)
            {
                ExtendActive(FallbackDuration);
                duration = FallbackDuration;
            }

            Owner.Status.Apply(StatusEffectType.Cloaked, duration, 1f, this);
            Owner.Status.Apply(StatusEffectType.Haste, duration, hasteBonus, this);

            // One-shot shimmer at the moment of cloaking (no looping effect: it would give her position away).
            VfxManager.Spawn(VfxId.CloakShimmer, Owner.ChestPosition, Owner.Rotation, 1f, shimmerTint);
            AudioManager.PlayAt(SfxId.Cloak, Owner.ChestPosition, 0.8f, 1f);
        }

        protected override void OnTick(float deltaTime)
        {
            // Stripped by something else (an enemy reveal, status wipe): the ability is over.
            if (Owner.Status == null || !Owner.Status.Has(StatusEffectType.Cloaked)) EndAbility();
        }

        protected override void OnInterrupt(InterruptReason reason) => RemoveCloakStatuses();

        protected override void OnEnd(bool interrupted)
        {
            bool wasCloaked = Owner.Status != null && Owner.Status.Has(StatusEffectType.Cloaked);
            RemoveCloakStatuses();
            if (wasCloaked) Decloak(0.6f); // natural expiry: soft shimmer
        }

        // ------------------------------------------------------------------ IThrowModifier

        public void ModifyThrow(ref ThrowParams throwParams)
        {
            if (!IsCloaked || (throwParams.Thrower != null && throwParams.Thrower != Owner)) return;
            if (throwParams.IsPass && !passesReveal) return;

            if (!throwParams.IsPass) throwParams.SpeedMultiplier *= stealthThrowSpeedMultiplier;
            throwParams.RevealsThrower = true;
        }

        public void OnThrowCommitted(in ThrowParams throwParams, DodgeBall ball)
        {
            if (!throwParams.RevealsThrower || !IsCloaked) return;
            if (throwParams.Thrower != null && throwParams.Thrower != Owner) return;

            // Ambush throw: decloak with a sharp shimmer, mark the position, end the ability (cooldown starts).
            RemoveCloakStatuses();
            if (revealDuration > 0f) Owner.Status.Apply(StatusEffectType.Revealed, revealDuration, 1f, this);
            Decloak(1.2f);
            EndAbility();
        }

        // ------------------------------------------------------------------ AI

        /// <summary>
        /// Best as an ambush: cloak while holding a ball with an enemy in range (the +30% throw lands before they react).
        /// Also used to sneak to loose balls; a weak escape tool against a ball already in the air (it is still locked on).
        /// </summary>
        public override float EvaluateAIUtility(in AbilityAIContext ctx)
        {
            if (Data == null || ctx.Self == null) return 0f;
            float w = Data.aiWeight;
            if (ctx.HoldingBall && ctx.NearestEnemy != null && ctx.NearestEnemyDistance < 14f) return Mathf.Clamp01(w * 0.85f);
            if (!ctx.HoldingBall && ctx.FreeBallsNearby > 0) return Mathf.Clamp01(w * 0.4f);
            if (ctx.IncomingBall != null && ctx.IncomingTimeToImpact < 0.8f) return Mathf.Clamp01(w * 0.25f);
            return Mathf.Clamp01(w * 0.1f);
        }

        // ------------------------------------------------------------------ helpers

        private void EnsureModifierRegistered()
        {
            var combat = Owner.Combat;
            if (combat == null) return;
            if (_registeredOn != null && _registeredOn != combat) _registeredOn.RemoveThrowModifier(this);
            combat.RemoveThrowModifier(this); // idempotent: never registered twice
            combat.AddThrowModifier(this);
            _registeredOn = combat;
        }

        private void RemoveCloakStatuses()
        {
            var status = Owner != null ? Owner.Status : null;
            if (status == null) return;
            status.Remove(StatusEffectType.Cloaked, this);
            status.Remove(StatusEffectType.Haste, this);
        }

        private void Decloak(float intensity)
        {
            VfxManager.Spawn(VfxId.CloakShimmer, Owner.ChestPosition, Owner.Rotation, intensity, shimmerTint);
            AudioManager.PlayAt(SfxId.Cloak, Owner.ChestPosition, 0.7f * Mathf.Clamp01(intensity), 0.8f);
        }
    }
}
