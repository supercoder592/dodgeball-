using DodgeballUltra.Juice;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace DodgeballUltra.Rendering.HDRP
{
    /// <summary>
    /// Tuning for the HDRP runtime adapter: gameplay camera anti-aliasing, the fallback environment Volume created when a
    /// scene has none (indoor sports-arena look: fixed exposure, volumetric haze, ACES, AO/SSR/contact shadows) and the
    /// full-screen juice pulses of <see cref="HdrpScreenFxDriver"/>.
    /// <para>Loaded from <c>Resources/DodgeballUltra/HdrpRenderingSettings</c> (the editor's HDRP project configuration
    /// creates one under <c>Assets/DodgeballUltra/Generated/Resources</c>); when absent, an in-memory instance with the
    /// defaults below is used, so the game never depends on the asset existing.</para>
    /// <para>All photometric values are physical: exposure in EV100, illuminance in lux, distances in metres.</para>
    /// </summary>
    [CreateAssetMenu(menuName = "Dodgeball Ultra/Rendering/HDRP Rendering Settings", fileName = "HdrpRenderingSettings")]
    public sealed class HdrpRenderingSettings : ScriptableObject
    {
        /// <summary>Resources path (without extension) the installer loads the settings from.</summary>
        public const string ResourcesPath = "DodgeballUltra/HdrpRenderingSettings";

        /// <summary>Which sky the fallback environment Volume uses.</summary>
        public enum RuntimeSkyModel
        {
            /// <summary>Physically Based Sky when the scene has a directional light bright enough to drive it, else Gradient.</summary>
            Auto = 0,
            /// <summary>HDRP Physically Based Sky (lit by the scene's directional light).</summary>
            PhysicallyBased,
            /// <summary>Gradient sky calibrated to <see cref="ambientIlluminanceLux"/>: emulates indoor bounce light.</summary>
            Gradient,
        }

        // ------------------------------------------------------------------------------------------------ Camera
        [Header("Gameplay camera")]
        [Tooltip("Anti-aliasing of gameplay cameras. TAA also stabilises SSR/SSAO/volumetrics; SMAA avoids any ghosting.")]
        public HDAdditionalCameraData.AntialiasingMode antialiasing = HDAdditionalCameraData.AntialiasingMode.TemporalAntialiasing;

        [Tooltip("TAA quality preset.")]
        public HDAdditionalCameraData.TAAQualityLevel taaQuality = HDAdditionalCameraData.TAAQualityLevel.High;

        [Tooltip("TAA sharpening (counters the slight softness of temporal accumulation).")]
        [Range(0f, 2f)] public float taaSharpenStrength = 0.5f;

        [Tooltip("How strongly TAA rejects history along motion vectors. Raised above HDRP's default to avoid ghost trails " +
                 "behind balls travelling at up to 220 km/h.")]
        [Range(0f, 1f)] public float taaMotionVectorRejection = 0.35f;

        [Tooltip("TAA anti-flicker on thin, high-contrast details (court lines, hair cards).")]
        [Range(0f, 1f)] public float taaAntiFlicker = 0.5f;

        [Tooltip("SMAA quality preset (used when anti-aliasing is SMAA).")]
        public HDAdditionalCameraData.SMAAQualityLevel smaaQuality = HDAdditionalCameraData.SMAAQualityLevel.High;

        [Tooltip("8-bit dithering: removes banding in the dark, hazy parts of the arena.")]
        public bool dithering = true;

        [Tooltip("Replace NaN/Inf pixels (protects bloom/TAA history from a single bad pixel).")]
        public bool stopNaNs = true;

        [Tooltip("Layers whose Volumes affect gameplay cameras (added to the camera's existing mask). The environment and " +
                 "screen-FX volumes live on the Default layer.")]
        public LayerMask volumeLayerMask = 1;

        // ------------------------------------------------------------------------------------------------ Environment
        [Header("Fallback environment volume (only when the scene has no global Volume)")]
        [Tooltip("Create a global environment Volume when RuntimeRenderingHooks.EnsureEnvironmentVolume finds none.")]
        public bool createEnvironmentVolume = true;

        [Tooltip("Priority of the created environment Volume (scene volumes with a higher priority override it).")]
        public float environmentVolumePriority = 0f;

        [Tooltip("Sky used for ambient lighting and reflections.")]
        public RuntimeSkyModel skyModel = RuntimeSkyModel.Auto;

        [Tooltip("Auto sky: minimum directional-light illuminance (lux) required to use the Physically Based Sky.")]
        [Min(0f)] public float physicalSkyMinSunLux = 500f;

        [Tooltip("Gradient sky: zenith radiance colour (ceiling bounce).")]
        public Color gradientTop = new Color(0.62f, 0.6f, 0.57f);

        [Tooltip("Gradient sky: horizon radiance colour (walls and stands).")]
        public Color gradientMiddle = new Color(0.55f, 0.52f, 0.48f);

        [Tooltip("Gradient sky: nadir radiance colour (warm bounce off the maple court).")]
        public Color gradientBottom = new Color(0.52f, 0.4f, 0.28f);

        [Tooltip("Gradient sky: blend width between the three colours.")]
        [Min(0.01f)] public float gradientDiffusion = 1f;

        [Tooltip("Gradient sky: horizontal illuminance (lux) produced by the ambient 'sky' - i.e. indirect bounce light " +
                 "inside the hall. ~20-30 % of the court's direct illuminance is typical for a light-coloured arena.")]
        [Min(0f)] public float ambientIlluminanceLux = 1200f;

        [Header("Exposure")]
        [Tooltip("Fixed exposure (EV100). 11.5 exposes a ~5-6 klux TV-lit arena floor at mid-grey. Must match the arena lights.")]
        [Range(-2f, 18f)] public float fixedExposureEV100 = 11.5f;

        [Tooltip("Exposure compensation (EV) applied on top of the fixed exposure.")]
        [Range(-5f, 5f)] public float exposureCompensation = 0f;

        [Header("Fog / volumetric haze")]
        [Tooltip("Enable HDRP fog.")]
        public bool enableFog = true;

        [Tooltip("Enable volumetric fog (visible floodlight shafts).")]
        public bool volumetricFog = true;

        [Tooltip("Distance (m) at which the haze attenuates light to 1/e. Lower = denser haze. Arena haze machines ~100-200 m.")]
        [Min(1f)] public float fogMeanFreePath = 150f;

        [Tooltip("World height (m) where the fog density starts falling off.")]
        public float fogBaseHeight = 0f;

        [Tooltip("World height (m) above which the fog is at its minimum density (roughly the hall's roof).")]
        public float fogMaximumHeight = 25f;

        [Tooltip("Distance (m) beyond which fog stops accumulating.")]
        [Min(1f)] public float fogMaxDistance = 300f;

        [Tooltip("Single-scattering albedo of the haze particles.")]
        public Color fogAlbedo = new Color(0.92f, 0.9f, 0.86f);

        [Tooltip("Phase function anisotropy. Positive = forward scattering: beams glow when looking towards the lights.")]
        [Range(-1f, 1f)] public float fogAnisotropy = 0.6f;

        [Tooltip("How much the global ambient probe lights the haze. Kept low indoors so the floodlight shafts dominate.")]
        [Range(0f, 1f)] public float fogGlobalLightProbeDimmer = 0.4f;

        [Tooltip("Range (m) of the volumetric fog buffer from the camera.")]
        [Min(0.1f)] public float volumetricDepthExtent = 64f;

        [Header("Tonemapping & bloom")]
        [Tooltip("Tonemapper. ACES gives the filmic highlight roll-off of broadcast cameras.")]
        public TonemappingMode tonemapping = TonemappingMode.ACES;

        [Tooltip("Bloom intensity. Keep subtle for a realistic lens.")]
        [Range(0f, 1f)] public float bloomIntensity = 0.1f;

        [Tooltip("Bloom scatter (spread).")]
        [Range(0f, 1f)] public float bloomScatter = 0.65f;

        [Tooltip("Bloom threshold: only pixels above this brightness bloom (lenses, speculars).")]
        [Min(0f)] public float bloomThreshold = 0.8f;

        [Header("Screen-space lighting & shadows")]
        [Tooltip("Screen-space ambient occlusion intensity.")]
        [Range(0f, 4f)] public float ambientOcclusionIntensity = 0.9f;

        [Tooltip("SSAO radius (m).")]
        [Range(0.25f, 5f)] public float ambientOcclusionRadius = 1.5f;

        [Tooltip("How much SSAO also darkens direct lighting (grounds players on the court).")]
        [Range(0f, 1f)] public float ambientOcclusionDirectLightingStrength = 0.2f;

        [Tooltip("Screen-space reflections (lacquered court floor, wet-look ball).")]
        public bool screenSpaceReflections = true;

        [Tooltip("Contact shadows (feet, hands, ball on the floor).")]
        public bool contactShadows = true;

        [Tooltip("Contact shadow ray length (m).")]
        [Range(0f, 1f)] public float contactShadowLength = 0.2f;

        [Tooltip("Contact shadow opacity.")]
        [Range(0f, 1f)] public float contactShadowOpacity = 0.9f;

        [Tooltip("Micro shadows from normal/AO maps (fabric folds, skin pores, wood grain).")]
        public bool microShadows = true;

        [Tooltip("Micro shadow opacity.")]
        [Range(0f, 1f)] public float microShadowOpacity = 0.75f;

        [Tooltip("Maximum shadow distance (m) - covers an indoor arena.")]
        [Min(1f)] public float maxShadowDistance = 80f;

        [Tooltip("Directional shadow cascade count.")]
        [Range(1, 4)] public int shadowCascadeCount = 4;

        [Header("Colour grading")]
        [Tooltip("Global contrast (slight S-curve for a broadcast look).")]
        [Range(-100f, 100f)] public float contrast = 6f;

        [Tooltip("Global saturation offset.")]
        [Range(-100f, 100f)] public float saturation = 2f;

        // ------------------------------------------------------------------------------------------------ Screen FX
        [Header("Screen FX (juice pulses)")]
        [Tooltip("Install HdrpScreenFxDriver as Juice.ScreenFx.Driver.")]
        public bool enableScreenFx = true;

        [Tooltip("Base priority of the screen-FX volumes (one per pulse type, above every scene volume).")]
        [Min(0f)] public float screenFxPriority = 1000f;

        [Tooltip("Rise time (unscaled s) of a pulse before it decays over the rest of its duration.")]
        [Range(0.001f, 0.2f)] public float pulseAttackTime = 0.025f;

        [Tooltip("Global multiplier on every pulse (accessibility: lower to reduce flashing/aberration).")]
        [Range(0f, 1f)] public float pulseStrength = 1f;

        [Tooltip("ScreenPulse.Hit")] public HdrpScreenFxLook hit = HdrpScreenFxLook.DefaultHit();
        [Tooltip("ScreenPulse.HeavyHit (elimination)")] public HdrpScreenFxLook heavyHit = HdrpScreenFxLook.DefaultHeavyHit();
        [Tooltip("ScreenPulse.PerfectCatch")] public HdrpScreenFxLook perfectCatch = HdrpScreenFxLook.DefaultPerfectCatch();
        [Tooltip("ScreenPulse.UltimateCast")] public HdrpScreenFxLook ultimateCast = HdrpScreenFxLook.DefaultUltimateCast();
        [Tooltip("ScreenPulse.Freeze")] public HdrpScreenFxLook freeze = HdrpScreenFxLook.DefaultFreeze();
        [Tooltip("ScreenPulse.TimeRewind")] public HdrpScreenFxLook timeRewind = HdrpScreenFxLook.DefaultTimeRewind();
        [Tooltip("ScreenPulse.DangerSense")] public HdrpScreenFxLook dangerSense = HdrpScreenFxLook.DefaultDangerSense();

        /// <summary>Full-strength look of <paramref name="pulse"/> (null for an unknown value).</summary>
        public HdrpScreenFxLook GetLook(ScreenPulse pulse)
        {
            switch (pulse)
            {
                case ScreenPulse.Hit: return hit;
                case ScreenPulse.HeavyHit: return heavyHit;
                case ScreenPulse.PerfectCatch: return perfectCatch;
                case ScreenPulse.UltimateCast: return ultimateCast;
                case ScreenPulse.Freeze: return freeze;
                case ScreenPulse.TimeRewind: return timeRewind;
                case ScreenPulse.DangerSense: return dangerSense;
                default: return null;
            }
        }

        /// <summary>
        /// Loads the project's settings asset from Resources, or creates an in-memory instance with the spec defaults
        /// (not saved, so it never leaks into the project).
        /// </summary>
        public static HdrpRenderingSettings LoadOrCreateDefault()
        {
            var settings = Resources.Load<HdrpRenderingSettings>(ResourcesPath);
            if (settings != null) return settings;

            settings = CreateInstance<HdrpRenderingSettings>();
            settings.name = "HdrpRenderingSettings (defaults)";
            settings.hideFlags = HideFlags.DontSave;
            return settings;
        }

        private void OnValidate()
        {
            // Keep the height band well-formed; HDRP expects maximumHeight above baseHeight.
            if (fogMaximumHeight < fogBaseHeight + 0.5f) fogMaximumHeight = fogBaseHeight + 0.5f;
        }
    }
}
