// -----------------------------------------------------------------------------------------------------------------
// Compile-check stubs: com.unity.render-pipelines.high-definition 17.0.4
//   Runtime/Lighting/Reflection/HDProbe.cs, Runtime/Lighting/Reflection/HDAdditionalReflectionData.cs,
//   Runtime/Utilities/ProbeSettings.cs, Runtime/Lighting/Reflection/Volume/{InfluenceVolume,ShapeType}.cs
// Signatures copied verbatim from the package source; only the members used by DodgeballUltra are declared.
// -----------------------------------------------------------------------------------------------------------------
using System;

namespace UnityEngine.Rendering.HighDefinition
{
    [Serializable]
    public struct ProbeSettings
    {
        public enum ProbeType
        {
            ReflectionProbe,
            PlanarProbe
        }

        public enum Mode
        {
            Baked,
            Realtime,
            Custom
        }

        public enum RealtimeMode
        {
            EveryFrame,
            OnEnable,
            OnDemand
        }
    }

    public enum InfluenceShape
    {
        Box,
        Sphere,
    }

    [Serializable]
    public partial class InfluenceVolume
    {
        public InfluenceShape shape { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public Vector3 boxSize { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public Vector3 boxBlendDistancePositive { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public Vector3 boxBlendDistanceNegative { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public Vector3 boxBlendNormalDistancePositive { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public Vector3 boxBlendNormalDistanceNegative { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public float sphereRadius { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    }

    [ExecuteAlways]
    public abstract partial class HDProbe : MonoBehaviour
    {
        public ProbeSettings.ProbeType type { get => throw new NotImplementedException(); protected set => throw new NotImplementedException(); }
        public ProbeSettings.Mode mode { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public ProbeSettings.RealtimeMode realtimeMode { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public bool timeSlicing { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public int importance { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public float multiplier { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public float weight { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public float fadeDistance { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public InfluenceVolume influenceVolume => throw new NotImplementedException();
        public void RequestRenderNextUpdate() => throw new NotImplementedException();
    }

    [AddComponentMenu("")]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(ReflectionProbe))]
    public sealed partial class HDAdditionalReflectionData : HDProbe
    {
    }
}
