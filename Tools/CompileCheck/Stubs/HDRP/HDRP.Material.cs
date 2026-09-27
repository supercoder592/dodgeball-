// -----------------------------------------------------------------------------------------------------------------
// Compile-check stubs: com.unity.render-pipelines.high-definition 17.0.4
//   Runtime/Material/MaterialExtension.cs, Runtime/Material/DiffusionProfile/DiffusionProfileSettings.cs,
//   Runtime/Lighting/DiffusionProfileList.cs
// Signatures copied verbatim from the package source; bodies are compile-only.
// -----------------------------------------------------------------------------------------------------------------
using System;

namespace UnityEngine.Rendering.HighDefinition
{
    public enum MaterialId
    {
        LitSSS = 0,
        LitStandard = 1,
        LitAniso = 2,
        LitIridescence = 3,
        LitSpecular = 4,
        LitTranslucent = 5,
        LitColoredTranslucent = 6,
    }

    public enum EmissiveIntensityUnit
    {
        Nits,
        EV100,
    }

    public static partial class HDMaterial
    {
        public enum RenderingPass
        {
            BeforeRefraction,
            Default,
            AfterPostProcess,
            LowResolution,
        }

        public static bool ValidateMaterial(Material material) => throw new NotImplementedException();
        public static void SetSurfaceType(Material material, bool transparent) => throw new NotImplementedException();
        public static void SetRenderingPass(Material material, RenderingPass value) => throw new NotImplementedException();
        public static void SetEmissiveColor(Material material, Color value) => throw new NotImplementedException();
        public static void SetUseEmissiveIntensity(Material material, bool value) => throw new NotImplementedException();
        public static void SetEmissiveIntensity(Material material, float intensity, EmissiveIntensityUnit unit) => throw new NotImplementedException();
        public static void SetAlphaClipping(Material material, bool value) => throw new NotImplementedException();
        public static void SetAlphaCutoff(Material material, float cutoff) => throw new NotImplementedException();
        public static void SetDiffusionProfile(Material material, DiffusionProfileSettings profile) => throw new NotImplementedException();
        public static MaterialId GetMaterialType(this Material material) => throw new NotImplementedException();
        public static bool SetMaterialType(this Material material, MaterialId type) => throw new NotImplementedException();
    }

    public partial class DiffusionProfileSettings : ScriptableObject
    {
    }

    [Serializable]
    public class DiffusionProfileList : VolumeComponent
    {
        [SerializeField]
        public DiffusionProfileSettingsParameter diffusionProfiles = new DiffusionProfileSettingsParameter(default(DiffusionProfileSettings[]));
    }

    [Serializable]
    public sealed class DiffusionProfileSettingsParameter : VolumeParameter<DiffusionProfileSettings[]>
    {
        public DiffusionProfileSettingsParameter(DiffusionProfileSettings[] value, bool overrideState = true)
            : base(value, overrideState) { }
    }
}
