using System;
using UnityEngine;

namespace DodgeballUltra.Rendering.HDRP
{
    /// <summary>
    /// Full-strength target of one <see cref="DodgeballUltra.Juice.ScreenPulse"/> channel of the
    /// <see cref="HdrpScreenFxDriver"/>. Every value is the look reached when the channel's Volume weight is 1; the
    /// driver fades the weight in unscaled time, and HDRP interpolates from whatever the scene's own volumes produce
    /// towards these values (so a pulse always layers <i>on top of</i> the environment grade instead of replacing it).
    /// <para>Only parameters that differ from their neutral value are written into the pulse's VolumeProfile, which keeps
    /// every channel orthogonal: a Freeze tint never resets the Hit chromatic aberration and vice versa.</para>
    /// </summary>
    [Serializable]
    public sealed class HdrpScreenFxLook
    {
        [Tooltip("Chromatic aberration intensity at full pulse weight (0 = untouched).")]
        [Range(0f, 1f)] public float chromaticAberration;

        [Tooltip("Vignette intensity at full pulse weight (0 = untouched).")]
        [Range(0f, 1f)] public float vignetteIntensity;

        [Tooltip("Vignette colour (only used when the vignette intensity is above 0).")]
        public Color vignetteColor = Color.black;

        [Tooltip("Vignette falloff softness (only used when the vignette intensity is above 0).")]
        [Range(0.01f, 1f)] public float vignetteSmoothness = 0.4f;

        [Tooltip("Lens distortion at full pulse weight. Negative = pincushion 'punch in', positive = barrel.")]
        [Range(-1f, 1f)] public float lensDistortion;

        [Tooltip("Bloom intensity at full pulse weight (0 = untouched).")]
        [Range(0f, 1f)] public float bloomIntensity;

        [Tooltip("Post exposure offset in EV at full pulse weight (0 = untouched).")]
        [Range(-3f, 3f)] public float postExposure;

        [Tooltip("Saturation offset at full pulse weight (-100 = greyscale, 0 = untouched).")]
        [Range(-100f, 100f)] public float saturation;

        [Tooltip("Contrast offset at full pulse weight (0 = untouched).")]
        [Range(-100f, 100f)] public float contrast;

        [Tooltip("Multiplicative colour filter at full pulse weight (white = untouched).")]
        [ColorUsage(false, true)] public Color colorFilter = Color.white;

        [Tooltip("White balance temperature shift at full pulse weight (negative = colder/bluer, 0 = untouched).")]
        [Range(-100f, 100f)] public float whiteBalanceTemperature;

        /// <summary>True when the look writes at least one parameter.</summary>
        public bool HasAnyEffect =>
            chromaticAberration > 0f || vignetteIntensity > 0f || Mathf.Abs(lensDistortion) > 1e-4f || bloomIntensity > 0f ||
            Mathf.Abs(postExposure) > 1e-4f || Mathf.Abs(saturation) > 1e-3f || Mathf.Abs(contrast) > 1e-3f ||
            colorFilter != Color.white || Mathf.Abs(whiteBalanceTemperature) > 1e-3f;

        // ---------------------------------------------------------------------------------------------------------
        // Spec defaults. Values are deliberately restrained: the game aims for a broadcast-camera look, so pulses read
        // as lens/sensor artefacts (aberration, vignetting, exposure bloom) rather than cartoon screen tints.
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>Standard hit: short chromatic aberration punch with a slight dark vignette and lens "punch-in".</summary>
        public static HdrpScreenFxLook DefaultHit() => new HdrpScreenFxLook
        {
            chromaticAberration = 0.55f,
            vignetteIntensity = 0.28f,
            vignetteColor = new Color(0.05f, 0.02f, 0.02f),
            vignetteSmoothness = 0.45f,
            lensDistortion = -0.06f,
        };

        /// <summary>Elimination: full aberration, deep red vignette, drained colour and a stronger lens punch.</summary>
        public static HdrpScreenFxLook DefaultHeavyHit() => new HdrpScreenFxLook
        {
            chromaticAberration = 1f,
            vignetteIntensity = 0.45f,
            vignetteColor = new Color(0.28f, 0.01f, 0.01f),
            vignetteSmoothness = 0.5f,
            lensDistortion = -0.16f,
            saturation = -45f,
            postExposure = -0.3f,
        };

        /// <summary>Perfect catch: bright bloom/exposure flash with desaturated, lifted edges.</summary>
        public static HdrpScreenFxLook DefaultPerfectCatch() => new HdrpScreenFxLook
        {
            bloomIntensity = 0.55f,
            postExposure = 0.6f,
            saturation = -20f,
            vignetteIntensity = 0.22f,
            vignetteColor = new Color(0.85f, 0.87f, 0.9f),
            vignetteSmoothness = 0.6f,
        };

        /// <summary>Ultimate cast: lens punch, aberration and an exposure swell.</summary>
        public static HdrpScreenFxLook DefaultUltimateCast() => new HdrpScreenFxLook
        {
            chromaticAberration = 0.5f,
            lensDistortion = -0.22f,
            bloomIntensity = 0.4f,
            postExposure = 0.35f,
            vignetteIntensity = 0.3f,
            vignetteColor = Color.black,
            vignetteSmoothness = 0.45f,
            contrast = 10f,
        };

        /// <summary>Freeze: cold white balance, cyan filter, drained saturation and a frosty edge vignette.</summary>
        public static HdrpScreenFxLook DefaultFreeze() => new HdrpScreenFxLook
        {
            whiteBalanceTemperature = -45f,
            colorFilter = new Color(0.8f, 0.92f, 1.08f),
            saturation = -30f,
            vignetteIntensity = 0.3f,
            vignetteColor = new Color(0.55f, 0.75f, 0.95f),
            vignetteSmoothness = 0.55f,
        };

        /// <summary>Time rewind: near-monochrome with barrel lens distortion and aberration.</summary>
        public static HdrpScreenFxLook DefaultTimeRewind() => new HdrpScreenFxLook
        {
            saturation = -85f,
            lensDistortion = 0.32f,
            chromaticAberration = 0.6f,
            contrast = 12f,
            vignetteIntensity = 0.35f,
            vignetteColor = new Color(0.08f, 0.07f, 0.06f),
            vignetteSmoothness = 0.5f,
        };

        /// <summary>Danger sense: red screen-edge vignette (Specter passive; the HUD may add its own indicator).</summary>
        public static HdrpScreenFxLook DefaultDangerSense() => new HdrpScreenFxLook
        {
            vignetteIntensity = 0.5f,
            vignetteColor = new Color(0.75f, 0.02f, 0.02f),
            vignetteSmoothness = 0.35f,
        };
    }
}
