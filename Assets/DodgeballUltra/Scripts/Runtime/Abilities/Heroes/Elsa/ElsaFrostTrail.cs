using System;
using DodgeballUltra.Combat;
using UnityEngine;

namespace DodgeballUltra.Abilities.Heroes
{
    /// <summary>
    /// Elsa passive [Frost Trail]: every ball Elsa throws leaves a trail of ice on the court floor beneath its
    /// trajectory; allies standing on the ice move +20% faster.
    /// <para>
    /// Implementation: an <see cref="IThrowModifier"/> registered on Elsa's <see cref="PlayerCombatController"/>. It never
    /// alters the throw itself (<see cref="ModifyThrow"/> is a no-op); in <see cref="OnThrowCommitted"/> it attaches an
    /// <see cref="ElsaFrostTrailEmitter"/> to the launched ball, which lays <see cref="ElsaIceTrailSegment"/> patches every
    /// ~0.9 m under the flight path while the ball is live (3 s lifetime, 0.9 m wide, realistic frosted-ice look plus the
    /// IceTrail VFX) and detaches as soon as the ball is caught, lands or leaves play. The patches refresh
    /// <c>Status Haste 0.2</c> on allies whose feet are on them.
    /// </para>
    /// <para>Ability throws (Glacier Freeze) and passes lay ice too by default - both are "thrown balls".</para>
    /// </summary>
    [Serializable]
    public sealed class ElsaFrostTrail : AbilityBase, IThrowModifier
    {
        [Header("Ice patches")]
        [Tooltip("Distance (m) between two ice patches along the ball's ground track. Spec: ~0.9 m.")]
        [Range(0.3f, 3f)] public float patchSpacing = 0.9f;

        [Tooltip("Width (m) of the ice trail across the direction of travel. Spec: 0.9 m.")]
        [Range(0.3f, 3f)] public float patchWidth = 0.9f;

        [Tooltip("Seconds each ice patch lasts before it has melted away. Spec: 3 s.")]
        [Range(0.5f, 10f)] public float patchLifetime = 3f;

        [Tooltip("Upper bound of patches one throw can lay (a full-court throw needs ~40).")]
        [Range(4, 128)] public int maxPatchesPerThrow = 48;

        [Header("Ally haste")]
        [Tooltip("Movement speed bonus for allies standing on the ice (0.2 = +20%). Spec: +20%.")]
        [Range(0f, 1f)] public float allyHaste = 0.2f;

        [Tooltip("How long (s) the haste lingers after stepping off the ice (it is refreshed continuously while on it).")]
        [Range(0.1f, 1.5f)] public float hasteLinger = 0.35f;

        [Tooltip("Elsa herself also skates faster on her own ice.")]
        public bool casterBenefits = true;

        [Tooltip("Extra margin (m) around a patch that still counts as standing on it (foot size).")]
        [Range(0f, 0.5f)] public float footMargin = 0.15f;

        [Header("Which throws")]
        [Tooltip("Passes also lay ice.")]
        public bool trailOnPasses = true;

        [Tooltip("Ability projectiles (Glacier Freeze) also lay ice.")]
        public bool trailOnAbilityThrows = true;

        [Header("Presentation")]
        [Tooltip("Tint of the IceTrail effect attached to the ball (cold vapour / ice glitter).")]
        public Color trailVfxTint = new Color(0.8f, 0.9f, 1f, 1f);

        [Tooltip("Scale of the attached IceTrail effect.")]
        [Range(0.2f, 3f)] public float trailVfxScale = 1f;

        [NonSerialized] private PlayerCombatController _registeredOn;

        /// <summary>Parameterless constructor (roster factory / SerializeReference).</summary>
        public ElsaFrostTrail()
        {
        }

        /// <summary>Runs late so the trail sees the final throw (never changes it anyway).</summary>
        public int Order => 900;

        /// <summary>The settings every patch of the next throw uses.</summary>
        public ElsaIceTrailSettings BuildSettings() => new ElsaIceTrailSettings
        {
            Spacing = patchSpacing,
            Width = patchWidth,
            Lifetime = patchLifetime,
            HasteMagnitude = allyHaste,
            HasteRefresh = hasteLinger,
            FootMargin = footMargin,
            IncludeCaster = casterBenefits,
            MaxSegmentsPerThrow = maxPatchesPerThrow,
        };

        // ------------------------------------------------------------------ ability hooks

        protected override void OnEquip()
        {
            Register();
            // Build the frost texture/meshes now rather than on the first throw (avoids a hitch mid-rally).
            if (Application.isPlaying) ElsaFrostAssets.Prewarm();
        }

        protected override void OnUnequip()
        {
            Unregister();
            ElsaIceTrailSegment.ClearOwnedBy(Owner);
        }

        protected override void OnTick(float deltaTime)
        {
            // The combat controller may be (re)created after equip: keep the modifier registered.
            if (_registeredOn == null || _registeredOn != Owner.Combat) Register();
        }

        protected override void OnRoundReset()
        {
            // Fresh court every round.
            ElsaIceTrailSegment.ClearAll();
        }

        protected override void OnCast()
        {
            // Passive: never cast.
        }

        /// <summary>Passives are never activated by the AI.</summary>
        public override float EvaluateAIUtility(in AbilityAIContext ctx) => 0f;

        // ------------------------------------------------------------------ IThrowModifier

        public void ModifyThrow(ref ThrowParams throwParams)
        {
            // Frost Trail changes the court, not the throw.
        }

        public void OnThrowCommitted(in ThrowParams throwParams, DodgeBall ball)
        {
            if (ball == null || Owner == null || !ball.IsLive) return;
            if (throwParams.Thrower != null && throwParams.Thrower != Owner) return;
            if (throwParams.IsPass && !trailOnPasses) return;
            if (throwParams.IsAbilityThrow && !trailOnAbilityThrows) return;

            ElsaFrostTrailEmitter.Attach(ball, Owner, BuildSettings(), trailVfxTint, trailVfxScale);
        }

        // ------------------------------------------------------------------ helpers

        private void Register()
        {
            var combat = Owner != null ? Owner.Combat : null;
            if (combat == _registeredOn) return;
            Unregister();
            if (combat == null) return;
            combat.AddThrowModifier(this);
            _registeredOn = combat;
        }

        private void Unregister()
        {
            if (_registeredOn != null) _registeredOn.RemoveThrowModifier(this);
            _registeredOn = null;
        }
    }
}
