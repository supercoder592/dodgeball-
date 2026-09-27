// -----------------------------------------------------------------------------------------------------------------
// Compile-check stubs: com.unity.render-pipelines.high-definition 17.0.4 - Runtime/RenderPipeline/Camera/HDAdditionalCameraData.cs
// Signatures copied verbatim from the package source; only the members used by DodgeballUltra are declared.
// -----------------------------------------------------------------------------------------------------------------
namespace UnityEngine.Rendering.HighDefinition
{
    [RequireComponent(typeof(Camera))]
    public partial class HDAdditionalCameraData : MonoBehaviour
    {
        public enum ClearColorMode
        {
            Sky,
            Color,
            None
        };

        public enum AntialiasingMode
        {
            [InspectorName("No Anti-aliasing")]
            None,
            [InspectorName("Fast Approximate Anti-aliasing (FXAA)")]
            FastApproximateAntialiasing,
            [InspectorName("Temporal Anti-aliasing (TAA)")]
            TemporalAntialiasing,
            [InspectorName("Subpixel Morphological Anti-aliasing (SMAA)")]
            SubpixelMorphologicalAntiAliasing
        }

        public enum SMAAQualityLevel
        {
            Low,
            Medium,
            High
        }

        public enum TAAQualityLevel
        {
            Low,
            Medium,
            High
        }

        public ClearColorMode clearColorMode = ClearColorMode.Sky;
        public LayerMask volumeLayerMask = 1;
        public Transform volumeAnchorOverride;
        public AntialiasingMode antialiasing = AntialiasingMode.None;
        public SMAAQualityLevel SMAAQuality = SMAAQualityLevel.High;
        public bool dithering = false;
        public bool stopNaNs = false;
        [Range(0, 2)]
        public float taaSharpenStrength = 0.5f;
        public TAAQualityLevel TAAQuality = TAAQualityLevel.Medium;
        [Range(0.0f, 1.0f)]
        public float taaAntiFlicker = 0.5f;
        [Range(0.0f, 1.0f)]
        public float taaMotionVectorRejection = 0.0f;
        public bool allowDynamicResolution = false;
    }
}
