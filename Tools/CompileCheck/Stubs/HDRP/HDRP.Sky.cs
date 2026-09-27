// -----------------------------------------------------------------------------------------------------------------
// Compile-check stubs: com.unity.render-pipelines.high-definition 17.0.4
//   Runtime/Sky/VisualEnvironment.cs, Runtime/Sky/SkySettings.cs, Runtime/Sky/SkyManager.cs (EnvironmentUpdateMode),
//   Runtime/Sky/GradientSky/GradientSky.cs, Runtime/Sky/HDRISky/HDRISky.cs, Runtime/Sky/PhysicallyBasedSky/PhysicallyBasedSky.cs
// Signatures copied verbatim from the package source; only the members used by DodgeballUltra are declared.
// -----------------------------------------------------------------------------------------------------------------
using System;

namespace UnityEngine.Rendering.HighDefinition
{
    public enum SkyType
    {
        HDRI = 1,
        Procedural = 2,
        Gradient = 3,
        PhysicallyBased = 4,
    }

    public enum SkyAmbientMode
    {
        Static,
        Dynamic,
    }

    [Serializable]
    public sealed class SkyAmbientModeParameter : VolumeParameter<SkyAmbientMode>
    {
        public SkyAmbientModeParameter(SkyAmbientMode value, bool overrideState = false)
            : base(value, overrideState) { }
    }

    [Serializable, VolumeComponentMenu("Visual Environment")]
    public sealed partial class VisualEnvironment : VolumeComponent
    {
        public NoInterpIntParameter skyType = new NoInterpIntParameter(0);
        public NoInterpIntParameter cloudType = new NoInterpIntParameter(0);
        public SkyAmbientModeParameter skyAmbientMode = new SkyAmbientModeParameter(SkyAmbientMode.Dynamic);
    }

    public enum EnvironmentUpdateMode
    {
        OnChanged = 0,
        OnDemand,
        Realtime
    }

    [Serializable]
    public sealed class EnvUpdateParameter : VolumeParameter<EnvironmentUpdateMode>
    {
        public EnvUpdateParameter(EnvironmentUpdateMode value, bool overrideState = false)
            : base(value, overrideState) { }
    }

    public enum SkyIntensityMode
    {
        Exposure,
        Lux,
        Multiplier,
    }

    [Serializable]
    public sealed class SkyIntensityParameter : VolumeParameter<SkyIntensityMode>
    {
        public SkyIntensityParameter(SkyIntensityMode value, bool overrideState = false)
            : base(value, overrideState) { }
    }

    public abstract class SkySettings : VolumeComponent
    {
        public ClampedFloatParameter rotation = new ClampedFloatParameter(0.0f, 0.0f, 360.0f);
        public SkyIntensityParameter skyIntensityMode = new SkyIntensityParameter(SkyIntensityMode.Exposure);
        public FloatParameter exposure = new FloatParameter(0.0f);
        public MinFloatParameter multiplier = new MinFloatParameter(1.0f, 0.0f);
        public MinFloatParameter upperHemisphereLuxValue = new MinFloatParameter(1.0f, 0.0f);
        public FloatParameter desiredLuxValue = new FloatParameter(20000);
        public EnvUpdateParameter updateMode = new EnvUpdateParameter(EnvironmentUpdateMode.OnChanged);
        public MinFloatParameter updatePeriod = new MinFloatParameter(0.0f, 0.0f);
        public BoolParameter includeSunInBaking = new BoolParameter(false);
    }

    [VolumeComponentMenu("Sky/Gradient Sky")]
    public class GradientSky : SkySettings
    {
        public ColorParameter top = new ColorParameter(Color.blue, true, false, true);
        public ColorParameter middle = new ColorParameter(new Color(0.3f, 0.7f, 1f), true, false, true);
        public ColorParameter bottom = new ColorParameter(Color.white, true, false, true);
        public MinFloatParameter gradientDiffusion = new MinFloatParameter(1, 0.0f);
    }

    [VolumeComponentMenu("Sky/HDRI Sky")]
    public partial class HDRISky : SkySettings
    {
        public CubemapParameter hdriSky = new CubemapParameter(null);
        public BoolParameter upperHemisphereOnly = new BoolParameter(true);
    }

    [VolumeComponentMenu("Sky/Physically Based Sky")]
    public partial class PhysicallyBasedSky : SkySettings
    {
        public BoolParameter atmosphericScattering = new BoolParameter(true);
        public ColorParameter groundTint = new ColorParameter(new Color(0.12f, 0.10f, 0.09f), hdr: false, showAlpha: false, showEyeDropper: false);
        public ColorParameter horizonTint = new ColorParameter(Color.white, hdr: false, showAlpha: false, showEyeDropper: true);
        public ColorParameter zenithTint = new ColorParameter(Color.white, hdr: false, showAlpha: false, showEyeDropper: true);
    }
}
