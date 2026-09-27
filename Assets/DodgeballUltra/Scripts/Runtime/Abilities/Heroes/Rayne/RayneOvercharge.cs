using System;
using DodgeballUltra.Audio;
using DodgeballUltra.Combat;
using DodgeballUltra.Juice;
using DodgeballUltra.VFX;
using UnityEngine;

namespace DodgeballUltra.Abilities.Heroes
{
    /// <summary>
    /// Rayne's passive <b>[Overcharge]</b>: "charging throws increase ball velocity up to +50% and radius +20% over 2 s".
    /// <para>
    /// Implemented as an <see cref="IThrowModifier"/> (Order 0, i.e. before situational boosts) registered on Rayne's
    /// <see cref="PlayerCombatController"/> for as long as the passive is equipped:
    /// <code>
    /// t      = clamp01(ChargeSeconds / 2 s)
    /// speed *= 1 + 0.5 * t          (still capped at 220 km/h by the rally rule)
    /// radius*= 1 + 0.2 * t
    /// </code>
    /// The modifier is side-effect free (BuildThrowParams may be evaluated for previews); feedback happens in
    /// <see cref="OnThrowCommitted"/> and in the passive tick (heat glow while holding past a normal full charge, and a
    /// distinct cue at the moment the 2 s overcharge is reached).
    /// </para>
    /// <para>Also guarantees <c>CombatProfile.maxChargeTime &gt;= 2 s</c> so the full overcharge is reachable.</para>
    /// </summary>
    [Serializable]
    public sealed class RayneOvercharge : AbilityBase, IThrowModifier
    {
        [Header("Overcharge")]
        [Tooltip("Seconds of charging needed to reach the full bonus. Spec: 2 s.")]
        [Min(0.1f)] public float overchargeTime = 2f;

        [Tooltip("Speed bonus at full overcharge (0.5 = +50%). Spec: +50%.")]
        [Range(0f, 2f)] public float maxSpeedBonus = 0.5f;

        [Tooltip("Ball radius bonus at full overcharge (0.2 = +20%). Spec: +20%.")]
        [Range(0f, 1f)] public float maxRadiusBonus = 0.2f;

        [Tooltip("Also apply the charge bonus to conjured ability balls (they count as fully charged at the normal full " +
                 "charge time). Off by default: Overcharge rewards holding a real throw.")]
        public bool applyToAbilityThrows;

        [Header("Feedback")]
        [Tooltip("Heat colour of the hand glow and overcharged release.")]
        public Color heatTint = new Color(1f, 0.52f, 0.16f, 1f);

        [Tooltip("Scale of the hand glow while holding a throw past a normal full charge.")]
        [Range(0.1f, 2f)] public float glowScale = 0.35f;

        [Tooltip("Camera shake amplitude when a fully overcharged throw is released.")]
        [Range(0f, 1f)] public float releaseShake = 0.2f;

        [NonSerialized] private PlayerCombatController _registeredOn;
        [NonSerialized] private CombatProfile _raisedProfile;
        [NonSerialized] private float _originalMaxChargeTime;
        [NonSerialized] private bool _changedMaxChargeTime;
        [NonSerialized] private bool _verifiedAfterSpawn;
        [NonSerialized] private bool _fullCuePlayed;
        [NonSerialized] private VfxHandle _glow;

        public RayneOvercharge() { }

        /// <summary>Runs before situational boosts (stealth, counter...), which multiply on top.</summary>
        public int Order => 0;

        /// <summary>0..1 overcharge fraction for a throw charged for <paramref name="chargeSeconds"/>.</summary>
        public float GetOverchargeFraction(float chargeSeconds) => Mathf.Clamp01(chargeSeconds / Mathf.Max(0.01f, overchargeTime));

        // ------------------------------------------------------------------ equip / unequip

        /// <summary>
        /// Registers right away when the combat controller is already bound to this player (normal spawn order and hero
        /// swaps); otherwise the first passive tick registers instead (before any throw can happen).
        /// </summary>
        protected override void OnEquip()
        {
            if (Owner.IsInitialized || (Owner.Combat != null && Owner.Combat.Owner == Owner)) SyncWithCombat(false);
        }

        protected override void OnUnequip()
        {
            StopGlow();
            if (_registeredOn != null) _registeredOn.RemoveThrowModifier(this);
            _registeredOn = null;
            RestoreProfile();
            _verifiedAfterSpawn = false;
        }

        /// <summary>Passives never cast.</summary>
        protected override void OnCast() { }

        /// <summary>Passive tick (every frame): keeps the modifier registered and drives the charge feedback.</summary>
        protected override void OnTick(float deltaTime)
        {
            // The first tick happens after DodgeballPlayer finished wiring every sub-system: re-register once so the
            // modifier survives a combat controller that was (re)initialised after the abilities were equipped.
            SyncWithCombat(!_verifiedAfterSpawn);
            _verifiedAfterSpawn = true;

            var combat = _registeredOn;
            if (combat == null || !combat.IsCharging)
            {
                _fullCuePlayed = false;
                StopGlow();
                return;
            }

            // Heat builds in the hand once the throw is held past a normal full charge.
            float normalFull = combat.Profile != null ? combat.Profile.fullChargeTime : 0.75f;
            if (combat.ChargeSeconds > normalFull && !_glow.IsValid)
            {
                _glow = VfxManager.SpawnAttached(VfxId.FireTrail, HandTransform(out Vector3 offset), offset, glowScale, heatTint);
            }

            if (!_fullCuePlayed && combat.ChargeSeconds >= overchargeTime)
            {
                _fullCuePlayed = true;
                Vector3 hand = HandTransform(out Vector3 o).TransformPoint(o);
                VfxManager.Spawn(VfxId.ThrowWhoosh, hand, Owner.Rotation, 0.6f, heatTint);
                AudioManager.PlayAt(SfxId.AbilityCast, hand, 0.5f, 1.35f);
            }
        }

        protected override void OnRoundReset()
        {
            _fullCuePlayed = false;
            StopGlow();
        }

        // ------------------------------------------------------------------ IThrowModifier

        public void ModifyThrow(ref ThrowParams throwParams)
        {
            if (!IsEligible(in throwParams)) return;

            float t = GetOverchargeFraction(throwParams.ChargeSeconds);
            throwParams.SpeedMultiplier *= 1f + maxSpeedBonus * t;
            throwParams.RadiusMultiplier *= 1f + maxRadiusBonus * t;
        }

        public void OnThrowCommitted(in ThrowParams throwParams, DodgeBall ball)
        {
            StopGlow();
            _fullCuePlayed = false;
            if (!IsEligible(in throwParams) || GetOverchargeFraction(throwParams.ChargeSeconds) < 0.999f) return;

            // Fully overcharged release: heavier whoosh along the flight direction + a small kick for the thrower's camera.
            Vector3 dir = ball != null && ball.Velocity.sqrMagnitude > 1e-4f ? ball.Velocity.normalized : Owner.Forward;
            VfxManager.Spawn(VfxId.ThrowWhoosh, throwParams.Origin, Quaternion.LookRotation(dir), 1.3f, heatTint);
            AudioManager.PlayAt(SfxId.ThrowHeavy, throwParams.Origin, 0.9f, 0.9f);
            var juice = JuiceManager.Instance;
            if (juice != null && releaseShake > 0f) juice.Shake(releaseShake, 18f, 0.14f, throwParams.Origin);
        }

        /// <summary>Passives are never activated by the AI.</summary>
        public override float EvaluateAIUtility(in AbilityAIContext ctx) => 0f;

        // ------------------------------------------------------------------ helpers

        private bool IsEligible(in ThrowParams p)
        {
            if (p.IsPass) return false;
            if (p.IsAbilityThrow && !applyToAbilityThrows) return false;
            return p.Thrower == null || p.Thrower == Owner;
        }

        /// <summary>Registers the modifier on the current combat controller and raises maxChargeTime to the overcharge time.</summary>
        private void SyncWithCombat(bool forceReregister)
        {
            var combat = Owner != null ? Owner.Combat : null;
            if (combat != _registeredOn || (forceReregister && combat != null))
            {
                if (_registeredOn != null) _registeredOn.RemoveThrowModifier(this);
                if (combat != null)
                {
                    combat.RemoveThrowModifier(this); // idempotent registration
                    combat.AddThrowModifier(this);
                }
                _registeredOn = combat;
            }

            var profile = combat != null ? combat.Profile : null;
            if (profile != null && profile != _raisedProfile)
            {
                RestoreProfile();
                _raisedProfile = profile;
                if (profile.maxChargeTime < overchargeTime)
                {
                    _originalMaxChargeTime = profile.maxChargeTime;
                    profile.maxChargeTime = overchargeTime;
                    _changedMaxChargeTime = true;
                }
            }
        }

        private void RestoreProfile()
        {
            if (_raisedProfile != null && _changedMaxChargeTime) _raisedProfile.maxChargeTime = _originalMaxChargeTime;
            _raisedProfile = null;
            _changedMaxChargeTime = false;
        }

        private Transform HandTransform(out Vector3 localOffset)
        {
            var visual = Owner.Visual;
            if (visual != null && visual.RightHandSocket != null)
            {
                localOffset = Vector3.zero;
                return visual.RightHandSocket;
            }
            localOffset = new Vector3(0.25f, 1.35f, 0.2f);
            return Owner.transform;
        }

        private void StopGlow()
        {
            if (!_glow.IsValid) return;
            VfxManager.StopEffect(_glow);
            _glow = default;
        }
    }
}
