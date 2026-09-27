using System;
using DodgeballUltra.Audio;
using DodgeballUltra.Combat;
using DodgeballUltra.Events;
using DodgeballUltra.Player;
using DodgeballUltra.VFX;
using UnityEngine;

namespace DodgeballUltra.Abilities.Heroes
{
    /// <summary>
    /// Bear - SKILL [Magnetic Pull] (磁力牽引): for 1.5 s a 5 m magnetic field pulls any flying enemy ball into Bear's hands
    /// (CD 15 s).
    /// <para>
    /// OnCast spawns a <see cref="BearMagneticField"/>: a <see cref="IBallFieldEffect"/> registered with the
    /// <see cref="BallManager"/> that bends every enemy Live ball within 5 m toward Bear's right-hand socket with a strong
    /// homing acceleration, plus a magnetic catch zone (<see cref="IBallHittable"/>) around the hands. The first enemy match
    /// ball to arrive is caught (a normal catch through the combat pipeline when Bear's catch stance is armed, otherwise
    /// <c>GiveBall</c> + a Normal <see cref="BallCaughtEvent"/>, which feeds the juice/HUD/audio). If Bear is already holding
    /// a ball the others are slapped down to the floor. While the hands are empty the field also levitates the nearest
    /// loose ball into them. Unblockable balls ignore the field.
    /// </para>
    /// <para>Presentation: looping MagneticField VFX on the right hand, Magnet SFX, <see cref="StatusEffectType.Magnetized"/>
    /// on Bear for the HUD, and Bear's head tracks the ball being pulled.</para>
    /// </summary>
    [Serializable]
    public sealed class BearMagneticPull : AbilityBase
    {
        [Header("Magnetic field")]
        [Tooltip("Field radius around Bear (m). Spec: 5 m.")]
        [Range(1f, 12f)] public float radius = 5f;

        [Tooltip("Homing rate (1/s) at the edge of the field; it rises ~3x toward the hands. Higher = balls snap in harder.")]
        [Range(1f, 60f)] public float homingRate = 14f;

        [Tooltip("Speed (m/s) balls are eased down to as they arrive in the hands (a heavy, catchable 'thunk').")]
        [Range(2f, 30f)] public float arrivalSpeed = 11f;

        [Tooltip("Minimum speed (m/s) a steered ball keeps, so slow lobs still fly into the hands.")]
        [Range(1f, 20f)] public float minPullSpeed = 7f;

        [Tooltip("Radius (m) of the magnetic catch zone around the hands and torso.")]
        [Range(0.3f, 1.5f)] public float catchZoneRadius = 0.75f;

        [Tooltip("Where the catch zone sits between the chest (0) and the right hand (1).")]
        [Range(0f, 1f)] public float catchZoneHandBias = 0.5f;

        [Header("Loose balls")]
        [Tooltip("While Bear's hands are empty, levitate the nearest loose ball within the field into them.")]
        public bool pullFreeBalls = true;

        [Tooltip("Acceleration (m/s^2) applied to a loose ball being pulled.")]
        [Range(5f, 120f)] public float freeBallAcceleration = 40f;

        [Tooltip("Top speed (m/s) of a loose ball being pulled.")]
        [Range(1f, 20f)] public float freeBallMaxSpeed = 7f;

        [Tooltip("Distance (m) from the hand at which a pulled loose ball is picked up.")]
        [Range(0.1f, 1.2f)] public float freeBallCaptureDistance = 0.45f;

        [Header("Hands full")]
        [Tooltip("Downward speed (m/s) of additional balls slapped down while Bear already holds one.")]
        [Range(0f, 15f)] public float deflectDownSpeed = 4f;

        [Tooltip("Fraction of its planar speed a slapped-down ball keeps.")]
        [Range(0f, 1f)] public float deflectPlanarRetain = 0.12f;

        [Header("Timing")]
        [Tooltip("Field duration used only when the AbilityData has no duration (spec: 1.5 s).")]
        [Min(0.1f)] public float fallbackDuration = 1.5f;

        [Header("Presentation")]
        [Tooltip("Tint of the magnetic field VFX and deflection sparks (cool steel blue).")]
        public Color fieldTint = new Color(0.55f, 0.75f, 1f, 1f);

        [Tooltip("Scale of the looping MagneticField VFX.")]
        [Range(0.2f, 3f)] public float vfxScale = 1.2f;

        [Tooltip("Magnet SFX volume.")]
        [Range(0f, 1f)] public float sfxVolume = 1f;

        [Header("AI")]
        [Tooltip("Use the field when an enemy ball will arrive within this many seconds.")]
        [Range(0.2f, 3f)] public float aiThreatTime = 1.2f;

        [NonSerialized] private BearMagneticField _field;
        [NonSerialized] private VfxHandle _vfx;
        [NonSerialized] private float _manualRemaining = -1f;
        [NonSerialized] private float _lookTimer;

        /// <summary>Required public parameterless constructor (SerializeReference / roster factory).</summary>
        public BearMagneticPull() { }

        /// <summary>The live field while the skill is active, otherwise null.</summary>
        public BearMagneticField ActiveField => _field;

        protected override void OnEquip()
        {
            // Safety nets: the field must never outlive the round.
            Listen<RoundEndedEvent>(_ => Cleanup());
            Listen<RoundStartedEvent>(_ => Cleanup());
        }

        protected override void OnUnequip() => Cleanup();

        protected override void OnCast()
        {
            Cleanup();

            var settings = new BearMagneticFieldSettings
            {
                Radius = radius,
                HomingRate = homingRate,
                ArrivalSpeed = arrivalSpeed,
                MinPullSpeed = minPullSpeed,
                CatchZoneRadius = catchZoneRadius,
                CatchZoneHandBias = catchZoneHandBias,
                PullFreeBalls = pullFreeBalls,
                FreeBallAcceleration = freeBallAcceleration,
                FreeBallMaxSpeed = freeBallMaxSpeed,
                FreeBallCaptureDistance = freeBallCaptureDistance,
                DeflectDownSpeed = deflectDownSpeed,
                DeflectPlanarRetain = deflectPlanarRetain,
                Tint = fieldTint,
            };
            _field = BearMagneticField.Create(Owner, settings);

            float duration = Duration > 0f ? Duration : fallbackDuration;
            if (Duration <= 0f)
            {
                // Data has no duration: keep the ability active on our own clock.
                HoldActive();
                _manualRemaining = fallbackDuration;
            }

            var hand = Owner.Visual != null ? Owner.Visual.RightHandSocket : null;
            _vfx = hand != null
                ? VfxManager.SpawnAttached(VfxId.MagneticField, hand, Vector3.zero, vfxScale, fieldTint)
                : VfxManager.SpawnAttached(VfxId.MagneticField, Owner.transform, Owner.ChestPosition - Owner.Position, vfxScale, fieldTint);

            AudioManager.PlayAt(SfxId.Magnet, Owner.ChestPosition, sfxVolume);
            if (Owner.Status != null) Owner.Status.Apply(StatusEffectType.Magnetized, duration, 1f, this);
        }

        protected override void OnTick(float deltaTime)
        {
            if (_field == null || !_field.IsActive)
            {
                EndAbility();
                return;
            }

            if (_manualRemaining > 0f)
            {
                _manualRemaining -= deltaTime;
                if (_manualRemaining <= 0f)
                {
                    EndAbility();
                    return;
                }
            }

            // Head follows the ball being reeled in.
            _lookTimer -= deltaTime;
            var focus = _field.FocusBall;
            if (_lookTimer <= 0f && focus != null && (focus.IsLive || focus.IsFree))
            {
                _lookTimer = 0.1f;
                var ik = Owner.Visual != null ? Owner.Visual.IK : null;
                if (ik != null) ik.OverrideLookTarget(focus.transform.position, 0.25f);
            }
        }

        protected override void OnEnd(bool interrupted) => Cleanup();

        protected override void OnRoundReset() => Cleanup();

        public override float EvaluateAIUtility(in AbilityAIContext ctx)
        {
            if (Data == null || ctx.Self == null) return 0f;
            float w = Data.aiWeight;

            // Defensive use: a live enemy ball is about to arrive inside the field.
            if (ctx.IncomingBall != null && ctx.IncomingTimeToImpact <= aiThreatTime)
            {
                float d = Vector3.Distance(ctx.IncomingBall.transform.position, ctx.Self.ChestPosition);
                if (d <= radius + ctx.IncomingBall.Speed * 0.25f) return Mathf.Clamp01(w * 1.2f);
            }

            // Utility use: grab a loose ball with empty hands.
            if (pullFreeBalls && !ctx.HoldingBall && BallManager.Instance != null)
            {
                var nearest = BallManager.Instance.FindNearestBall(ctx.Self.Position, null, radius);
                if (nearest != null && Vector3.Distance(nearest.transform.position, ctx.Self.Position) > 1.6f) return w * 0.35f;
            }
            return 0f;
        }

        private void Cleanup()
        {
            _manualRemaining = -1f;
            if (_field != null) _field.Shutdown();
            _field = null;
            VfxManager.StopEffect(_vfx);
            _vfx = default;
            if (Owner != null && Owner.Status != null) Owner.Status.Remove(StatusEffectType.Magnetized, this);
        }
    }
}
