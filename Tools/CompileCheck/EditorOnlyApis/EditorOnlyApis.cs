// Mirrors the editor-only engine members used by the Dodgeball Ultra editor code, type-checked against the
// editor-flavoured UnityEngine.dll (the module reference assemblies used for everything else are player-flavoured and
// omit these members). Source code using them is fenced with #if !DU_CC_PLAYER_REFS; keep this file in sync.
//   Assets/DodgeballUltra/Scripts/Editor.HDRP/HdrpLightingBuilder.cs
using UnityEngine;

internal static class EditorOnlyApis
{
    private static void HdrpLightingBuilder(Light light, LightProbeGroup group, Vector3[] local)
    {
        light.lightmapBakeType = LightmapBakeType.Mixed;
        group.probePositions = local;
        var settings = new LightingSettings
        {
            name = "DU_ArenaLightingSettings",
            bakedGI = true,
            realtimeGI = false,
            mixedBakeMode = MixedLightingMode.IndirectOnly,
            lightmapper = LightingSettings.Lightmapper.ProgressiveGPU,
            lightmapResolution = 12f,
            lightmapPadding = 2,
            lightmapMaxSize = 2048,
            directionalityMode = LightmapsMode.CombinedDirectional,
            ao = true,
            aoMaxDistance = 1f,
            directSampleCount = 32,
            indirectSampleCount = 512,
            environmentSampleCount = 256,
            maxBounces = 3,
        };
        Object.DestroyImmediate(settings);
    }
}
