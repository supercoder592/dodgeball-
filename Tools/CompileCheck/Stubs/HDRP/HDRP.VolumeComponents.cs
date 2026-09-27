// -----------------------------------------------------------------------------------------------------------------
// Compile-check stubs: com.unity.render-pipelines.high-definition 17.0.4 - lighting / shadow / post-process overrides
//   Runtime/Utilities/VolumeComponentWithQuality.cs, Runtime/RenderPipeline/GlobalPostProcessingQualitySettings.cs,
//   Runtime/Lighting/AtmosphericScattering/Fog.cs, Runtime/Lighting/ScreenSpaceLighting/ScreenSpaceAmbientOcclusion.cs,
//   Runtime/Lighting/ScreenSpaceLighting/ScreenSpaceReflection.cs, Runtime/Lighting/Shadow/{ContactShadows,
//   MicroShadowing,HDShadowSettings}.cs, Runtime/Lighting/IndirectLightingController.cs,
//   Runtime/PostProcessing/Components/{Exposure,Tonemapping,Bloom,ColorAdjustments,ChromaticAberration,Vignette,
//   LensDistortion,WhiteBalance}.cs
// Signatures copied verbatim from the package source; only the members used by DodgeballUltra are declared.
// -----------------------------------------------------------------------------------------------------------------
using System;

namespace UnityEngine.Rendering.HighDefinition
{
    [Serializable]
    public sealed class ScalableSettingLevelParameter : NoInterpIntParameter
    {
        public const int LevelCount = 3;

        public enum Level
        {
            Low,
            Medium,
            High
        }

        public ScalableSettingLevelParameter(int level, bool useOverride, bool overrideState = false)
            : base(useOverride ? LevelCount : (int)level, overrideState)
        {
        }

        public (int level, bool useOverride) levelAndOverride
        {
            get => value == LevelCount ? ((int)Level.Low, true) : (value, false);
            set
            {
                var (level, useOverride) = value;
                this.value = useOverride ? LevelCount : (int)level;
            }
        }
    }

    public abstract class VolumeComponentWithQuality : VolumeComponent
    {
        public ScalableSettingLevelParameter quality = new ScalableSettingLevelParameter((int)ScalableSettingLevelParameter.Level.Medium, false);
    }

    // ---- Fog ------------------------------------------------------------------------------------------------------
    public enum FogColorMode
    {
        ConstantColor,
        SkyColor,
    }

    [Serializable]
    public sealed class FogColorParameter : VolumeParameter<FogColorMode>
    {
        public FogColorParameter(FogColorMode value, bool overrideState = false)
            : base(value, overrideState) { }
    }

    [Serializable, VolumeComponentMenu("Fog")]
    public class Fog : VolumeComponentWithQuality
    {
        public BoolParameter enabled = new BoolParameter(false, BoolParameter.DisplayType.EnumPopup);
        public FogColorParameter colorMode = new FogColorParameter(FogColorMode.SkyColor);
        public ColorParameter color = new ColorParameter(Color.grey, hdr: true, showAlpha: false, showEyeDropper: true);
        public ColorParameter tint = new ColorParameter(Color.white, hdr: true, showAlpha: false, showEyeDropper: true);
        public MinFloatParameter maxFogDistance = new MinFloatParameter(5000.0f, 0.0f);
        public FloatParameter baseHeight = new FloatParameter(0.0f);
        public FloatParameter maximumHeight = new FloatParameter(50.0f);
        public MinFloatParameter meanFreePath = new MinFloatParameter(400.0f, 1.0f);
        public BoolParameter enableVolumetricFog = new BoolParameter(false);
        public ColorParameter albedo = new ColorParameter(Color.white);
        public ClampedFloatParameter globalLightProbeDimmer = new ClampedFloatParameter(1.0f, 0.0f, 1.0f);
        public MinFloatParameter depthExtent = new MinFloatParameter(64.0f, 0.1f);
        public ClampedFloatParameter anisotropy = new ClampedFloatParameter(0.0f, -1.0f, 1.0f);
        public ClampedFloatParameter multipleScatteringIntensity = new ClampedFloatParameter(0.0f, 0.0f, 2.0f);
    }

    // ---- Screen-space lighting ------------------------------------------------------------------------------------
    [Serializable, VolumeComponentMenu("Lighting/Ambient Occlusion")]
    public sealed class ScreenSpaceAmbientOcclusion : VolumeComponentWithQuality
    {
        public ClampedFloatParameter intensity = new ClampedFloatParameter(0f, 0f, 4f);
        public ClampedFloatParameter directLightingStrength = new ClampedFloatParameter(0f, 0f, 1f);
        public ClampedFloatParameter radius = new ClampedFloatParameter(2.0f, 0.25f, 5.0f);
        public BoolParameter temporalAccumulation = new BoolParameter(true);
        public ClampedFloatParameter specularOcclusion = new ClampedFloatParameter(0.5f, 0.0f, 1.0f);
    }

    [Serializable, VolumeComponentMenu("Lighting/Screen Space Reflection")]
    public class ScreenSpaceReflection : VolumeComponentWithQuality
    {
        public BoolParameter enabled = new BoolParameter(true, BoolParameter.DisplayType.EnumPopup);
        public BoolParameter enabledTransparent = new BoolParameter(true, BoolParameter.DisplayType.EnumPopup);
        public BoolParameter reflectSky = new BoolParameter(true);
        public ClampedFloatParameter screenFadeDistance = new ClampedFloatParameter(0.1f, 0.0f, 1.0f);
    }

    // ---- Shadows ----------------------------------------------------------------------------------------------------
    [Serializable, VolumeComponentMenu("Shadowing/Contact Shadows")]
    public class ContactShadows : VolumeComponentWithQuality
    {
        public BoolParameter enable = new BoolParameter(false, BoolParameter.DisplayType.EnumPopup);
        public ClampedFloatParameter length = new ClampedFloatParameter(0.15f, 0.0f, 1.0f);
        public ClampedFloatParameter opacity = new ClampedFloatParameter(1.0f, 0.0f, 1.0f);
        public ClampedFloatParameter distanceScaleFactor = new ClampedFloatParameter(0.5f, 0.0f, 1.0f);
        public MinFloatParameter maxDistance = new MinFloatParameter(50.0f, 0.0f);
        public MinFloatParameter minDistance = new MinFloatParameter(0.0f, 0.0f);
        public MinFloatParameter fadeDistance = new MinFloatParameter(5.0f, 0.0f);
        public MinFloatParameter fadeInDistance = new MinFloatParameter(0.0f, 0.0f);
        public ClampedFloatParameter rayBias = new ClampedFloatParameter(0.2f, 0.0f, 1.0f);
        public ClampedFloatParameter thicknessScale = new ClampedFloatParameter(0.15f, 0.02f, 1.0f);
    }

    [Serializable, VolumeComponentMenu("Shadowing/Micro Shadows")]
    public class MicroShadowing : VolumeComponent
    {
        public BoolParameter enable = new BoolParameter(false, BoolParameter.DisplayType.EnumPopup);
        public ClampedFloatParameter opacity = new ClampedFloatParameter(1.0f, 0.0f, 1.0f);
    }

    [Serializable, VolumeComponentMenu("Shadowing/Shadows")]
    public class HDShadowSettings : VolumeComponent
    {
        public NoInterpMinFloatParameter maxShadowDistance = new NoInterpMinFloatParameter(500.0f, 0.0f);
        public ClampedFloatParameter directionalTransmissionMultiplier = new ClampedFloatParameter(1.0f, 0.0f, 1.0f);
        public NoInterpClampedIntParameter cascadeShadowSplitCount = new NoInterpClampedIntParameter(4, 1, 4);
    }

    [Serializable, VolumeComponentMenu("Lighting/Indirect Lighting Controller")]
    public class IndirectLightingController : VolumeComponent
    {
        public MinFloatParameter indirectDiffuseLightingMultiplier = new MinFloatParameter(1.0f, 0.0f);
        public MinFloatParameter reflectionLightingMultiplier = new MinFloatParameter(1.0f, 0.0f);
        public MinFloatParameter reflectionProbeIntensityMultiplier = new MinFloatParameter(1.0f, 0.0f);
    }

    // ---- Post-processing --------------------------------------------------------------------------------------------
    public enum ExposureMode
    {
        Fixed = 0,
        Automatic = 1,
        AutomaticHistogram = 4,
        CurveMapping = 2,
        [InspectorName("Physical Camera")]
        UsePhysicalCamera = 3
    }

    [Serializable]
    public sealed class ExposureModeParameter : VolumeParameter<ExposureMode>
    {
        public ExposureModeParameter(ExposureMode value, bool overrideState = false) : base(value, overrideState) { }
    }

    [Serializable, VolumeComponentMenu("Exposure")]
    public sealed class Exposure : VolumeComponent
    {
        public ExposureModeParameter mode = new ExposureModeParameter(ExposureMode.Fixed);
        public FloatParameter fixedExposure = new FloatParameter(0f);
        public FloatParameter compensation = new FloatParameter(0f);
        public FloatParameter limitMin = new FloatParameter(-1f);
        public FloatParameter limitMax = new FloatParameter(14f);
    }

    public enum TonemappingMode
    {
        None,
        Neutral,
        ACES,
        Custom,
        External
    }

    [Serializable]
    public sealed class TonemappingModeParameter : VolumeParameter<TonemappingMode>
    {
        public TonemappingModeParameter(TonemappingMode value, bool overrideState = false) : base(value, overrideState) { }
    }

    [Serializable, VolumeComponentMenu("Post-processing/Tonemapping")]
    public sealed class Tonemapping : VolumeComponent
    {
        public TonemappingModeParameter mode = new TonemappingModeParameter(TonemappingMode.None);
    }

    [Serializable, VolumeComponentMenu("Post-processing/Bloom")]
    public sealed class Bloom : VolumeComponentWithQuality
    {
        public MinFloatParameter threshold = new MinFloatParameter(0f, 0f);
        public ClampedFloatParameter intensity = new ClampedFloatParameter(0f, 0f, 1f);
        public ClampedFloatParameter scatter = new ClampedFloatParameter(0.7f, 0f, 1f);
        public ColorParameter tint = new ColorParameter(Color.white, false, false, true);
        public MinFloatParameter dirtIntensity = new MinFloatParameter(0f, 0f);
        public BoolParameter anamorphic = new BoolParameter(true);
    }

    [Serializable, VolumeComponentMenu("Post-processing/Color Adjustments")]
    public sealed class ColorAdjustments : VolumeComponent
    {
        public FloatParameter postExposure = new FloatParameter(0f);
        public ClampedFloatParameter contrast = new ClampedFloatParameter(0f, -100f, 100f);
        public ColorParameter colorFilter = new ColorParameter(Color.white, true, false, true);
        public ClampedFloatParameter hueShift = new ClampedFloatParameter(0f, -180f, 180f);
        public ClampedFloatParameter saturation = new ClampedFloatParameter(0f, -100f, 100f);
    }

    [Serializable, VolumeComponentMenu("Post-processing/Chromatic Aberration")]
    public sealed class ChromaticAberration : VolumeComponentWithQuality
    {
        public ClampedFloatParameter intensity = new ClampedFloatParameter(0f, 0f, 1f);
    }

    public enum VignetteMode
    {
        Procedural,
        Masked
    }

    [Serializable]
    public sealed class VignetteModeParameter : VolumeParameter<VignetteMode>
    {
        public VignetteModeParameter(VignetteMode value, bool overrideState = false) : base(value, overrideState) { }
    }

    [Serializable, VolumeComponentMenu("Post-processing/Vignette")]
    public sealed class Vignette : VolumeComponent
    {
        public VignetteModeParameter mode = new VignetteModeParameter(VignetteMode.Procedural);
        public ColorParameter color = new ColorParameter(Color.black, false, false, true);
        public Vector2Parameter center = new Vector2Parameter(new Vector2(0.5f, 0.5f));
        public ClampedFloatParameter intensity = new ClampedFloatParameter(0f, 0f, 1f);
        public ClampedFloatParameter smoothness = new ClampedFloatParameter(0.2f, 0.01f, 1f);
        public ClampedFloatParameter roundness = new ClampedFloatParameter(1f, 0f, 1f);
        public BoolParameter rounded = new BoolParameter(false);
    }

    [Serializable, VolumeComponentMenu("Post-processing/Lens Distortion")]
    public sealed class LensDistortion : VolumeComponent
    {
        public ClampedFloatParameter intensity = new ClampedFloatParameter(0f, -1f, 1f);
        public ClampedFloatParameter xMultiplier = new ClampedFloatParameter(1f, 0f, 1f);
        public ClampedFloatParameter yMultiplier = new ClampedFloatParameter(1f, 0f, 1f);
        public Vector2Parameter center = new Vector2Parameter(new Vector2(0.5f, 0.5f));
        public ClampedFloatParameter scale = new ClampedFloatParameter(1f, 0.01f, 5f);
    }

    [Serializable, VolumeComponentMenu("Post-processing/White Balance")]
    public sealed class WhiteBalance : VolumeComponent
    {
        public ClampedFloatParameter temperature = new ClampedFloatParameter(0f, -100, 100f);
        public ClampedFloatParameter tint = new ClampedFloatParameter(0f, -100, 100f);
    }
}
