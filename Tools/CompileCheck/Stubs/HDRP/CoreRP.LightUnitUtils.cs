// -----------------------------------------------------------------------------------------------------------------
// Compile-check stubs: com.unity.render-pipelines.core 17.0.4 - Runtime/Utilities/LightUnitUtils.cs
// Only the members that do not depend on the Unity 6 engine enum UnityEngine.LightUnit are declared (the 2021.3
// reference assemblies used by the compile check do not contain it). Signatures copied verbatim.
// -----------------------------------------------------------------------------------------------------------------
using System;

namespace UnityEngine.Rendering
{
    public static class LightUnitUtils
    {
        public const float SphereSolidAngle = 4.0f * Mathf.PI;

        public static float GetSolidAngleFromPointLight() => throw new NotImplementedException();
        public static float GetSolidAngleFromSpotLight(float spotAngle) => throw new NotImplementedException();
        public static float GetAreaFromRectangleLight(Vector2 rectSize) => throw new NotImplementedException();
        public static float GetAreaFromDiscLight(float discRadius) => throw new NotImplementedException();
        public static float LumenToCandela(float lumen, float solidAngle) => throw new NotImplementedException();
        public static float LumenToNits(float lumen, float area) => throw new NotImplementedException();
    }
}
