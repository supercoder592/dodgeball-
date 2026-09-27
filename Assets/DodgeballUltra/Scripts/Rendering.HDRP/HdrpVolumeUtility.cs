using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace DodgeballUltra.Rendering.HDRP
{
    /// <summary>
    /// Helpers for building HDRP Volume profiles from code (runtime fallback environment, screen-FX pulses, and the
    /// editor's saved arena profile).
    /// </summary>
    public static class HdrpVolumeUtility
    {
        /// <summary>
        /// Sets a volume parameter and marks it overridden. Goes through the (virtual) <c>value</c> setter so clamped /
        /// min parameters keep their range; <c>VolumeParameter.Override</c> would bypass that clamping.
        /// </summary>
        public static void Set<T>(VolumeParameter<T> parameter, T value)
        {
            if (parameter == null) return;
            parameter.value = value;
            parameter.overrideState = true;
        }

        /// <summary>Returns the profile's component of type <typeparamref name="T"/>, adding it (no overrides) if missing.</summary>
        public static T GetOrAdd<T>(VolumeProfile profile) where T : VolumeComponent
        {
            if (profile.TryGet(out T component)) return component;
            return profile.Add<T>(false);
        }

        /// <summary>
        /// Destroys a runtime-created profile together with its component instances (they are separate
        /// ScriptableObjects and would otherwise leak until the next unload of unused assets).
        /// </summary>
        public static void DestroyProfile(VolumeProfile profile)
        {
            if (profile == null) return;
            for (int i = 0; i < profile.components.Count; i++)
                DestroyObject(profile.components[i]);
            profile.components.Clear();
            DestroyObject(profile);
        }

        private static void DestroyObject(Object obj)
        {
            if (obj == null) return;
            if (Application.isPlaying) Object.Destroy(obj);
            else Object.DestroyImmediate(obj);
        }

        /// <summary>Relative luminance (Rec. 709) of a colour authored in sRGB, evaluated in linear space.</summary>
        public static float LinearLuminance(Color srgb)
        {
            Color c = srgb.linear;
            return 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
        }

        /// <summary>
        /// Multiplier that makes an HDRP Gradient Sky deliver <paramref name="targetLux"/> of horizontal illuminance.
        /// A uniform sky of radiance L produces E = pi * L on an upward-facing surface; the cosine-weighted upper
        /// hemisphere of the gradient is approximated as 60 % zenith colour + 40 % horizon colour.
        /// </summary>
        public static float ComputeGradientSkyMultiplier(Color top, Color middle, float targetLux)
        {
            float radiance = 0.6f * LinearLuminance(top) + 0.4f * LinearLuminance(middle);
            return Mathf.Max(0f, targetLux) / (Mathf.PI * Mathf.Max(radiance, 1e-3f));
        }

        /// <summary>
        /// Fills <paramref name="profile"/> with the indoor-arena environment used when a scene has no global Volume:
        /// sky (physically based or calibrated gradient), fixed exposure, volumetric haze, ACES, subtle bloom, SSAO,
        /// SSR, contact + micro shadows, shadow distance and a slight contrast grade.
        /// </summary>
        /// <param name="profile">Target profile (components are added or updated).</param>
        /// <param name="settings">Tuning.</param>
        /// <param name="physicalSky">True = Physically Based Sky, false = Gradient Sky.</param>
        public static void BuildRuntimeEnvironment(VolumeProfile profile, HdrpRenderingSettings settings, bool physicalSky)
        {
            if (profile == null || settings == null) return;

            // --- Sky & ambient ------------------------------------------------------------------------------------
            var environment = GetOrAdd<VisualEnvironment>(profile);
            Set(environment.skyType, physicalSky ? (int)SkyType.PhysicallyBased : (int)SkyType.Gradient);
            Set(environment.skyAmbientMode, SkyAmbientMode.Dynamic);

            if (physicalSky)
            {
                // Earth defaults; brightness follows the scene's directional light, so indoor "window" key lights
                // naturally give a dim, physically consistent ambient.
                var sky = GetOrAdd<PhysicallyBasedSky>(profile);
                Set(sky.updateMode, EnvironmentUpdateMode.OnChanged);
            }
            else
            {
                var sky = GetOrAdd<GradientSky>(profile);
                Set(sky.top, settings.gradientTop);
                Set(sky.middle, settings.gradientMiddle);
                Set(sky.bottom, settings.gradientBottom);
                Set(sky.gradientDiffusion, settings.gradientDiffusion);
                Set(sky.skyIntensityMode, SkyIntensityMode.Multiplier);
                Set(sky.multiplier, ComputeGradientSkyMultiplier(settings.gradientTop, settings.gradientMiddle, settings.ambientIlluminanceLux));
                Set(sky.updateMode, EnvironmentUpdateMode.OnChanged);
            }

            // --- Exposure -----------------------------------------------------------------------------------------
            var exposure = GetOrAdd<Exposure>(profile);
            Set(exposure.mode, ExposureMode.Fixed);
            Set(exposure.fixedExposure, settings.fixedExposureEV100);
            Set(exposure.compensation, settings.exposureCompensation);

            // --- Fog / volumetric haze ---------------------------------------------------------------------------
            var fog = GetOrAdd<Fog>(profile);
            Set(fog.enabled, settings.enableFog);
            Set(fog.meanFreePath, settings.fogMeanFreePath);
            Set(fog.baseHeight, settings.fogBaseHeight);
            Set(fog.maximumHeight, Mathf.Max(settings.fogBaseHeight + 0.5f, settings.fogMaximumHeight));
            Set(fog.maxFogDistance, settings.fogMaxDistance);
            Set(fog.enableVolumetricFog, settings.volumetricFog);
            Set(fog.albedo, settings.fogAlbedo);
            Set(fog.anisotropy, settings.fogAnisotropy);
            Set(fog.globalLightProbeDimmer, settings.fogGlobalLightProbeDimmer);
            Set(fog.depthExtent, settings.volumetricDepthExtent);

            // --- Tonemapping & bloom ------------------------------------------------------------------------------
            var tonemapping = GetOrAdd<Tonemapping>(profile);
            Set(tonemapping.mode, settings.tonemapping);

            var bloom = GetOrAdd<Bloom>(profile);
            Set(bloom.intensity, settings.bloomIntensity);
            Set(bloom.scatter, settings.bloomScatter);
            Set(bloom.threshold, settings.bloomThreshold);
            Set(bloom.anamorphic, false); // spherical lens: no horizontal streaking

            // --- Screen-space lighting ----------------------------------------------------------------------------
            var ao = GetOrAdd<ScreenSpaceAmbientOcclusion>(profile);
            Set(ao.intensity, settings.ambientOcclusionIntensity);
            Set(ao.radius, settings.ambientOcclusionRadius);
            Set(ao.directLightingStrength, settings.ambientOcclusionDirectLightingStrength);

            var ssr = GetOrAdd<ScreenSpaceReflection>(profile);
            Set(ssr.enabled, settings.screenSpaceReflections);

            // --- Shadows ------------------------------------------------------------------------------------------
            var contact = GetOrAdd<ContactShadows>(profile);
            Set(contact.enable, settings.contactShadows);
            Set(contact.length, settings.contactShadowLength);
            Set(contact.opacity, settings.contactShadowOpacity);

            var micro = GetOrAdd<MicroShadowing>(profile);
            Set(micro.enable, settings.microShadows);
            Set(micro.opacity, settings.microShadowOpacity);

            var shadows = GetOrAdd<HDShadowSettings>(profile);
            Set(shadows.maxShadowDistance, settings.maxShadowDistance);
            Set(shadows.cascadeShadowSplitCount, settings.shadowCascadeCount);

            // --- Grade --------------------------------------------------------------------------------------------
            var grade = GetOrAdd<ColorAdjustments>(profile);
            Set(grade.contrast, settings.contrast);
            Set(grade.saturation, settings.saturation);
        }

        /// <summary>
        /// Fills <paramref name="profile"/> with the overrides of one screen-FX look. Only non-neutral values are
        /// overridden so the pulse volumes compose with each other and with the scene grade.
        /// </summary>
        /// <returns>True when at least one override was written.</returns>
        public static bool BuildScreenFxLook(VolumeProfile profile, HdrpScreenFxLook look)
        {
            if (profile == null || look == null || !look.HasAnyEffect) return false;

            if (look.chromaticAberration > 0f)
                Set(GetOrAdd<ChromaticAberration>(profile).intensity, look.chromaticAberration);

            if (look.vignetteIntensity > 0f)
            {
                var vignette = GetOrAdd<Vignette>(profile);
                Set(vignette.intensity, look.vignetteIntensity);
                Set(vignette.color, look.vignetteColor);
                Set(vignette.smoothness, look.vignetteSmoothness);
            }

            if (Mathf.Abs(look.lensDistortion) > 1e-4f)
                Set(GetOrAdd<LensDistortion>(profile).intensity, look.lensDistortion);

            if (look.bloomIntensity > 0f)
                Set(GetOrAdd<Bloom>(profile).intensity, look.bloomIntensity);

            bool grade = Mathf.Abs(look.postExposure) > 1e-4f || Mathf.Abs(look.saturation) > 1e-3f ||
                         Mathf.Abs(look.contrast) > 1e-3f || look.colorFilter != Color.white;
            if (grade)
            {
                var adjustments = GetOrAdd<ColorAdjustments>(profile);
                if (Mathf.Abs(look.postExposure) > 1e-4f) Set(adjustments.postExposure, look.postExposure);
                if (Mathf.Abs(look.saturation) > 1e-3f) Set(adjustments.saturation, look.saturation);
                if (Mathf.Abs(look.contrast) > 1e-3f) Set(adjustments.contrast, look.contrast);
                if (look.colorFilter != Color.white) Set(adjustments.colorFilter, look.colorFilter);
            }

            if (Mathf.Abs(look.whiteBalanceTemperature) > 1e-3f)
                Set(GetOrAdd<WhiteBalance>(profile).temperature, look.whiteBalanceTemperature);

            return true;
        }
    }
}
