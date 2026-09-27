// -----------------------------------------------------------------------------------------------------------------
// Compile-check stubs: com.unity.render-pipelines.high-definition 17.0.4 (Unity 6000.0)
//   Runtime/Lighting/Light/HDAdditionalLightData.cs, Runtime/RenderPipeline/Settings/ScalableSettingValue.cs
// Signatures copied verbatim from the package source; bodies are compile-only.
// -----------------------------------------------------------------------------------------------------------------
using System;

namespace UnityEngine.Rendering.HighDefinition
{
    [Serializable]
    public class ScalableSettingValue<T>
    {
        [SerializeField] private T m_Override;
        [SerializeField] private bool m_UseOverride;
        [SerializeField] private int m_Level;

        public int level
        {
            get => m_Level;
            set => m_Level = value;
        }

        public bool useOverride
        {
            get => m_UseOverride;
            set => m_UseOverride = value;
        }

        public T @override
        {
            get => m_Override;
            set => m_Override = value;
        }
    }

    [Serializable] public class IntScalableSettingValue : ScalableSettingValue<int> { }
    [Serializable] public class BoolScalableSettingValue : ScalableSettingValue<bool> { }

    [RequireComponent(typeof(Light))]
    [ExecuteAlways]
    public partial class HDAdditionalLightData : MonoBehaviour
    {
        public const float k_DefaultDirectionalLightIntensity = Mathf.PI;
        public const float k_DefaultPunctualLightIntensity = 600.0f;
        public const float k_MinSpotAngle = 1.0f;
        public const float k_MaxSpotAngle = 179.0f;

        public float innerSpotPercent { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public float lightDimmer { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public float volumetricDimmer { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public bool interactsWithSky { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public float angularDiameter { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public IntScalableSettingValue shadowResolution => throw new NotImplementedException();
        public float shadowDimmer { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public float volumetricShadowDimmer { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public BoolScalableSettingValue useContactShadow => throw new NotImplementedException();
        public float normalBias { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public float slopeBias { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public bool affectsVolumetric { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

        public static void InitDefaultHDAdditionalLightData(HDAdditionalLightData lightData) => throw new NotImplementedException();
        public void EnableShadows(bool enabled) => throw new NotImplementedException();
        public void SetShadowResolution(int resolution) => throw new NotImplementedException();
        public void SetShadowResolutionLevel(int level) => throw new NotImplementedException();
        public void SetShadowResolutionOverride(bool useOverride) => throw new NotImplementedException();
    }
}
