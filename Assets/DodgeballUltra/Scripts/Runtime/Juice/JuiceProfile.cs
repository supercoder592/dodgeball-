using DodgeballUltra.Core;
using UnityEngine;

namespace DodgeballUltra.Juice
{
    /// <summary>
    /// Tuning for the hit-feel pipeline. Intensity scales with ball speed between <see cref="minSpeedKmh"/> and
    /// <see cref="maxSpeedKmh"/> (0..1), which then drives every stage below.
    /// </summary>
    [CreateAssetMenu(fileName = "JuiceProfile", menuName = "Dodgeball Ultra/Juice Profile", order = 30)]
    public sealed class JuiceProfile : ScriptableObject
    {
        [Header("Intensity from ball speed")]
        public float minSpeedKmh = 40f;
        public float maxSpeedKmh = GameConstants.MaxBallSpeedKmh;
        [Tooltip("Remaps normalised speed to intensity.")]
        public AnimationCurve speedToIntensity = AnimationCurve.EaseInOut(0f, 0.25f, 1f, 1f);

        [Header("Hitstop (frame freeze)")]
        [Tooltip("Spec: dynamic 0.03 s .. 0.1 s.")]
        [Range(0f, 0.2f)] public float hitstopMin = GameConstants.HitstopMin;
        [Range(0f, 0.2f)] public float hitstopMax = GameConstants.HitstopMax;
        [Tooltip("Time scale during hitstop (0 = full freeze).")]
        [Range(0f, 1f)] public float hitstopTimeScale = 0.02f;
        [Tooltip("Extra hitstop multiplier when a player is eliminated.")]
        public float eliminationHitstopMultiplier = 1.25f;
        [Tooltip("Hitstop for a perfect catch (s).")]
        [Range(0f, 0.2f)] public float perfectCatchHitstop = 0.1f;

        [Header("Camera shake (Perlin)")]
        public float shakeAmplitudeMin = 0.25f;
        public float shakeAmplitudeMax = 0.85f;
        public float shakeFrequency = 24f;
        public float shakeDuration = 0.35f;
        [Tooltip("Distance (m) at which shake falls to zero for hits that do not involve the local player.")]
        public float shakeFalloffDistance = 25f;
        [Tooltip("Multiplier when the local player is the victim or attacker.")]
        public float localPlayerShakeMultiplier = 1.4f;

        [Header("Squash & stretch")]
        [Range(0f, 0.9f)] public float squashMin = 0.2f;
        [Range(0f, 0.9f)] public float squashMax = 0.55f;
        public float squashDuration = 0.18f;

        [Header("Hit flash")]
        public Color hitFlashColor = Color.white;
        [Tooltip("Spec: 0.05 s.")]
        public float hitFlashDuration = GameConstants.HitFlashDuration;
        public Color perfectCatchFlashColor = new Color(1f, 0.93f, 0.55f);

        [Header("Screen pulse")]
        public float screenPulseDuration = 0.28f;
        [Range(0f, 1f)] public float hitScreenPulse = 0.55f;
        [Range(0f, 1f)] public float perfectCatchScreenPulse = 1f;

        // ------------------------------------------------------------------ extended pipeline (Juice module)

        [Header("Screen pulse (extended)")]
        [Tooltip("Heavy pulse strength when a player is eliminated.")]
        [Range(0f, 1f)] public float eliminationScreenPulse = 1f;
        [Tooltip("Multiplier on full-screen pulses for events that do not involve the local player.")]
        [Range(0f, 1f)] public float remoteScreenPulseScale = 0.45f;

        [Header("Catches")]
        [Tooltip("Shake amplitude (trauma-equivalent) of a perfect catch before the local multiplier.")]
        [Range(0f, 1.5f)] public float perfectCatchShakeAmplitude = 0.8f;
        [Tooltip("Shake amplitude (trauma-equivalent) of a normal catch at full ball speed.")]
        [Range(0f, 1f)] public float normalCatchShakeAmplitude = 0.3f;
        [Tooltip("Squash of a normal catch relative to a hit at the same speed.")]
        [Range(0f, 1f)] public float normalCatchSquashScale = 0.6f;

        [Header("Eliminations")]
        [Tooltip("Extra trauma added on top of the hit shake when the hit eliminated the victim.")]
        [Range(0f, 1f)] public float eliminationExtraTrauma = 0.25f;
        [Tooltip("Shake amplitude of an elimination that did not come from a juiced ball hit (delayed impact, tackle...).")]
        [Range(0f, 1.5f)] public float eliminationShakeAmplitude = 0.75f;
        [Tooltip("Window (unscaled s) in which an elimination is considered part of the hit that was just juiced.")]
        [Range(0.05f, 1f)] public float eliminationMergeWindow = 0.35f;

        [Header("Negated hits & blocks")]
        [Tooltip("Scale of the light juice (shake + squash only) for hits negated by invulnerability / evasion. 0 disables.")]
        [Range(0f, 1f)] public float negatedHitScale = 0.3f;
        [Tooltip("Shake amplitude when a shield / clone / turret blocks a live ball (distance falloff applies).")]
        [Range(0f, 1f)] public float blockedShakeAmplitude = 0.35f;

        [Header("Court bounces")]
        [Tooltip("Impacts slower than this (m/s) do not squash the ball.")]
        [Min(0f)] public float minBounceSpeed = 3f;
        [Tooltip("Squash of a court bounce relative to squashMax at the same speed.")]
        [Range(0f, 1f)] public float bounceSquashScale = 0.7f;

        [Header("Local camera accents")]
        [Tooltip("Trauma added to the local camera when the local player throws at full speed.")]
        [Range(0f, 0.5f)] public float localThrowTrauma = 0.18f;
        [Tooltip("FOV kick (deg) when the local player throws at full speed (positive = wider).")]
        [Range(-10f, 10f)] public float localThrowFovKick = 1.5f;
        [Tooltip("FOV kick (deg) when the local player is hit.")]
        [Range(-10f, 10f)] public float localHitFovKick = 3.5f;
        [Tooltip("FOV kick (deg) on a local perfect catch (negative = punch in).")]
        [Range(-10f, 10f)] public float perfectCatchFovKick = -4f;
        [Tooltip("Seconds (unscaled) an FOV kick takes to ease back.")]
        [Range(0.05f, 2f)] public float fovKickDuration = 0.35f;

        [Header("Ultimate cast")]
        [Tooltip("Full-screen pulse when the local player casts an ultimate.")]
        [Range(0f, 1f)] public float ultimateCastScreenPulse = 0.8f;
        [Tooltip("Trauma added to the local camera when the local player casts an ultimate.")]
        [Range(0f, 1f)] public float ultimateCastTrauma = 0.3f;

        /// <summary>0..1 intensity for a ball speed.</summary>
        public float IntensityForSpeed(float speedKmh)
        {
            float t = Mathf.InverseLerp(minSpeedKmh, maxSpeedKmh, speedKmh);
            return Mathf.Clamp01(speedToIntensity.Evaluate(t));
        }

        /// <summary>Default profile with spec values (used when no asset is assigned).</summary>
        public static JuiceProfile CreateDefault()
        {
            var p = CreateInstance<JuiceProfile>();
            p.name = "JuiceProfile (Default)";
            return p;
        }
    }
}
