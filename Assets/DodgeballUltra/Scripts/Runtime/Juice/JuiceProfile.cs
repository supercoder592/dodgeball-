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
