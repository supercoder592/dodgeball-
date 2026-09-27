using UnityEngine;

namespace DodgeballUltra.Juice
{
    /// <summary>Full-screen post-processing pulses the juice pipeline can request.</summary>
    public enum ScreenPulse
    {
        Hit = 0,          // chromatic aberration + slight vignette punch
        HeavyHit,         // elimination
        PerfectCatch,     // bright bloom/exposure flash + desaturated edges
        UltimateCast,
        Freeze,           // cold tint
        TimeRewind,       // desaturate + lens distortion
        DangerSense,      // red edge vignette (handled by HUD too)
    }

    /// <summary>
    /// Implemented per render pipeline (see DodgeballUltra.Rendering.HDRP: HdrpScreenFxDriver, which drives a Volume).
    /// Runtime code never references a pipeline package directly; it talks to <see cref="ScreenFx"/>.
    /// </summary>
    public interface IScreenFxDriver
    {
        /// <summary>Short pulse that decays over <paramref name="duration"/> unscaled seconds.</summary>
        void Pulse(ScreenPulse pulse, float intensity, float duration);

        /// <summary>Held effect (0 = off). Used for freeze tints, slow-motion desaturation, etc.</summary>
        void SetSustained(ScreenPulse pulse, float amount);
    }

    /// <summary>Null-safe static facade over the active <see cref="IScreenFxDriver"/>.</summary>
    public static class ScreenFx
    {
        public static IScreenFxDriver Driver { get; set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => Driver = null;

        public static void Pulse(ScreenPulse pulse, float intensity = 1f, float duration = 0.25f) =>
            Driver?.Pulse(pulse, Mathf.Clamp01(intensity), Mathf.Max(0.01f, duration));

        public static void SetSustained(ScreenPulse pulse, float amount) => Driver?.SetSustained(pulse, Mathf.Clamp01(amount));
    }
}
