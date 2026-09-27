// -----------------------------------------------------------------------------------------------------------------
// Compile-check stubs: com.unity.render-pipelines.high-definition 17.0.4
//   Runtime/RenderPipeline/HDRenderPipelineAsset.cs, Runtime/RenderPipeline/HDRenderPipeline.cs,
//   Runtime/RenderPipeline/Settings/RenderPipelineSettings.cs, Runtime/Lighting/Shadow/HDShadowManager.cs
// In HDRP 17 HDRenderPipelineAsset derives from the Unity 6 engine type RenderPipelineAsset<HDRenderPipeline>; the
// 2021.3 reference assemblies only have the non-generic RenderPipelineAsset, which the stub derives from instead.
// Field/property signatures are copied verbatim; only the members used by DodgeballUltra are declared.
// -----------------------------------------------------------------------------------------------------------------
using System;
using UnityEngine.Experimental.Rendering;

namespace UnityEngine.Rendering.HighDefinition
{
    public partial class HDRenderPipeline : RenderPipeline
    {
        // NOTE: protected in the engine; the 2021.3 NuGet reference assemblies expose it as public.
        public override void Render(ScriptableRenderContext renderContext, Camera[] cameras) => throw new NotImplementedException();
    }

    public partial class HDRenderPipelineAsset : RenderPipelineAsset
    {
        [SerializeField]
        RenderPipelineSettings m_RenderPipelineSettings;

        public RenderPipelineSettings currentPlatformRenderPipelineSettings { get => m_RenderPipelineSettings; set { m_RenderPipelineSettings = value; } }

        // NOTE: protected in the engine; the 2021.3 NuGet reference assemblies expose it as public.
        public override RenderPipeline CreatePipeline() => throw new NotImplementedException();
    }

    [Serializable]
    public struct RenderPipelineSettings
    {
        public enum SupportedLitShaderMode
        {
            ForwardOnly = 1 << 0,
            DeferredOnly = 1 << 1,
            Both = ForwardOnly | DeferredOnly
        }

        public enum LightProbeSystem
        {
            [InspectorName("Light Probe Groups")]
            LegacyLightProbes = 0,
            AdaptiveProbeVolumes = 1,
        }

        public enum ColorBufferFormat
        {
            R11G11B10 = GraphicsFormat.B10G11R11_UFloatPack32,
            R16G16B16A16 = GraphicsFormat.R16G16B16A16_SFloat
        }

        public bool supportShadowMask;
        public bool supportSSR;
        public bool supportSSRTransparent;
        public bool supportSSAO;
        public bool supportSSGI;
        public bool supportSubsurfaceScattering;
        public bool supportVolumetrics;
        public bool supportVolumetricClouds;
        public bool supportLightLayers;
        public bool renderingLayerMaskBuffer;
        public bool supportDistortion;
        public bool supportTransparentBackface;
        public bool supportTransparentDepthPrepass;
        public bool supportTransparentDepthPostpass;
        public ColorBufferFormat colorBufferFormat;
        public bool supportCustomPass;
        public SupportedLitShaderMode supportedLitShaderMode;
        public bool supportDecals;
        public bool supportDecalLayers;
        public bool supportSurfaceGradient;
        public bool supportMotionVectors;
        public bool supportScreenSpaceLensFlare;
        public bool supportDataDrivenLensFlare;
        public bool supportDitheringCrossFade;
        public LightProbeSystem lightProbeSystem;
        public bool supportRayTracing;
        public HDShadowInitParameters hdShadowInitParams;
    }

    public enum HDShadowFilteringQuality
    {
        Low = 0,
        Medium = 1,
        High = 2
    }

    [Serializable]
    public struct HDShadowInitParameters
    {
        [Serializable]
        public struct HDShadowAtlasInitParams
        {
            public int shadowAtlasResolution;
            public DepthBits shadowAtlasDepthBits;
            public bool useDynamicViewportRescale;
        }

        public int maxShadowRequests;
        public DepthBits directionalShadowsDepthBits;
        public HDShadowFilteringQuality punctualShadowFilteringQuality;
        public HDShadowFilteringQuality directionalShadowFilteringQuality;
        public HDShadowAtlasInitParams punctualLightShadowAtlas;
        public HDShadowAtlasInitParams areaLightShadowAtlas;
        public int cachedPunctualLightShadowAtlas;
        public int cachedAreaLightShadowAtlas;
        public int maxDirectionalShadowMapResolution;
        public int maxPunctualShadowMapResolution;
        public int maxAreaShadowMapResolution;
        public bool supportScreenSpaceShadows;
        public int maxScreenSpaceShadowSlots;
    }
}
