using System;
using DodgeballUltra.Core;
using DodgeballUltra.Events;
using DodgeballUltra.VFX;
using UnityEngine;

namespace DodgeballUltra.Abilities.Heroes
{
    /// <summary>
    /// Bear - PASSIVE [Iron Mitts] (鐵手套): the perfect-catch window is 50 % longer - 0.225 s instead of 0.15 s.
    /// <para>
    /// Implementation: sets <see cref="Combat.PlayerCombatController.PerfectWindowMultiplier"/> to
    /// <see cref="GameConstants.IronMittsWindowMultiplier"/> while equipped (the combat controller derives
    /// <c>PerfectCatchWindow = CatchTiming.ScaledPerfectWindow(multiplier, profile.perfectCatchWindow)</c>) and restores 1
    /// on unequip. The value is re-asserted every frame in case the combat controller is (re)initialised after the
    /// abilities were equipped (hero swap / respawn ordering), so the passive can never silently fall off.
    /// </para>
    /// <para>
    /// Feedback: when a perfect catch only qualified thanks to the extended window (the input was between the base 0.15 s
    /// and the mitts' 0.225 s), a small warm metallic spark flashes at the catch point so players learn what the passive did.
    /// The regular perfect-catch juice (hitstop, shake, flash) is already driven by <see cref="BallCaughtEvent"/>.
    /// </para>
    /// </summary>
    [Serializable]
    public sealed class BearIronMitts : AbilityBase
    {
        [Header("Iron Mitts")]
        [Tooltip("Multiplier applied to the perfect-catch window. Spec: 1.5 (0.15 s -> 0.225 s).")]
        [Range(1f, 3f)] public float perfectWindowMultiplier = GameConstants.IronMittsWindowMultiplier;

        [Header("Feedback")]
        [Tooltip("Flash a small metallic spark when a perfect catch landed only thanks to the extended window.")]
        public bool highlightExtendedCatches = true;

        [Tooltip("Tint of the spark (warm steel glint).")]
        public Color sparkTint = new Color(1f, 0.83f, 0.58f, 1f);

        [Tooltip("Scale of the spark effect.")]
        [Range(0.1f, 2f)] public float sparkScale = 0.4f;

        /// <summary>Required public parameterless constructor (SerializeReference / roster factory).</summary>
        public BearIronMitts() { }

        /// <summary>Perfect window (s) Bear currently enjoys (0.225 s with default tuning).</summary>
        public float CurrentPerfectWindow => Owner != null && Owner.Combat != null
            ? Owner.Combat.PerfectCatchWindow
            : CatchTiming.ScaledPerfectWindow(perfectWindowMultiplier);

        protected override void OnEquip()
        {
            Apply();
            Listen<BallCaughtEvent>(OnBallCaught);
        }

        protected override void OnUnequip()
        {
            if (Owner != null && Owner.Combat != null) Owner.Combat.PerfectWindowMultiplier = 1f;
        }

        /// <summary>Passives are never cast.</summary>
        protected override void OnCast() { }

        protected override void OnTick(float deltaTime) => Apply();

        protected override void OnRoundReset() => Apply();

        /// <summary>A passive: the AI never "uses" it.</summary>
        public override float EvaluateAIUtility(in AbilityAIContext ctx) => 0f;

        private void Apply()
        {
            var combat = Owner != null ? Owner.Combat : null;
            if (combat == null) return;
            float target = Mathf.Max(1f, perfectWindowMultiplier);
            // Only raise: never fight a (hypothetical) stronger buff from another source.
            if (combat.PerfectWindowMultiplier < target - 1e-4f) combat.PerfectWindowMultiplier = target;
        }

        private void OnBallCaught(BallCaughtEvent e)
        {
            if (!highlightExtendedCatches || e.Catcher == null || e.Catcher != Owner) return;
            if (e.Quality != CatchQuality.Perfect) return;

            float baseWindow = Owner.Combat != null ? Owner.Combat.Profile.perfectCatchWindow : GameConstants.PerfectCatchWindow;
            if (e.SecondsBeforeImpact <= baseWindow) return; // would have been perfect anyway

            var rotation = Quaternion.LookRotation(Owner.Forward, Vector3.up);
            VfxManager.Spawn(VfxId.ShieldImpact, e.Point, rotation, sparkScale, sparkTint);
        }
    }
}
