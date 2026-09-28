using UnityEngine;

namespace DodgeballUltra.Editor.Pipeline
{
    /// <summary>What a character material is used for (drives SSS/alpha/smoothness choices per pipeline).</summary>
    public enum CharacterMaterialKind
    {
        /// <summary>Head/face texture: skin with subsurface scattering.</summary>
        Skin = 0,
        /// <summary>Rocketbox "body" texture: clothing with exposed skin (arms, legs).</summary>
        Body,
        /// <summary>Hair / eyelashes cards: alpha-clipped, double-sided.</summary>
        Hair,
        /// <summary>Anything else.</summary>
        Generic,
    }

    /// <summary>Description of a physically based environment material.</summary>
    public struct EnvironmentMaterialDesc
    {
        public string Name;
        public Color BaseColor;
        public Texture2D BaseMap;
        public Texture2D NormalMap;
        /// <summary>Packed mask: R metallic, G ambient occlusion, B detail mask, A smoothness (HDRP layout).</summary>
        public Texture2D MaskMap;
        public float Smoothness;
        public float Metallic;
        public Vector2 Tiling;
        public float NormalStrength;
        /// <summary>Emissive colour (linear) and intensity in nits for HDRP / multiplier elsewhere.</summary>
        public Color EmissiveColor;
        public float EmissiveIntensity;
        public bool Transparent;
        public bool DoubleSided;
    }

    /// <summary>Lighting setup requested by the arena builder.</summary>
    public struct ArenaLightingDesc
    {
        /// <summary>Indoor arena (floodlights + ambient) vs. open-air (sun + sky).</summary>
        public bool Indoor;
        public Vector3 SunEuler;
        public float SunIlluminanceLux;       // e.g. 100000 lux clear-sky noon, ~30000 golden hour
        public float SunTemperatureK;
        public float FloodlightLumen;         // per stadium floodlight
        public float FloodlightTemperatureK;
        public Vector3[] FloodlightPositions;
        public Vector3 FloodlightAimPoint;
        public Bounds ArenaBounds;            // for reflection probes / fog volume
        /// <summary>Optional HDRI (equirectangular .hdr imported as Cubemap) for sky + ambient.</summary>
        public Cubemap Hdri;
        public float ExposureEV100;           // fixed exposure, e.g. 12.5 for a lit arena
    }

    /// <summary>
    /// Implemented once per render pipeline. The HDRP implementation lives in DodgeballUltra.Editor.HDRP
    /// (<c>HdrpEditorRenderingHooks</c>) and registers itself via [InitializeOnLoad]; <see cref="EditorRenderingHooks"/> falls back to
    /// a Built-in (Standard shader) implementation owned by the Editor module.
    /// </summary>
    public interface IEditorRenderingHooks
    {
        string PipelineName { get; }

        /// <summary>True when this pipeline is the active one (pipeline asset assigned).</summary>
        bool IsActive { get; }

        /// <summary>
        /// Creates/assigns the pipeline asset and global settings with realistic features on (SSS, SSR, SSAO, contact
        /// shadows, volumetrics), linear colour space, quality defaults. Safe to call repeatedly.
        /// </summary>
        void ConfigureProject();

        /// <summary>
        /// Creates (or overwrites) a character material asset at <paramref name="assetPath"/>. <paramref name="maskMap"/>
        /// uses the HDRP layout (R metallic, G occlusion, B detail, A smoothness) in <b>absolute</b> units: the character
        /// pipeline has already remapped smoothness into the per-surface range, so implementations must not remap it again.
        /// </summary>
        Material CreateCharacterMaterial(string assetPath, CharacterMaterialKind kind, Texture2D baseColor, Texture2D normalMap,
            Texture2D maskMap, Texture2D opacityMap);

        /// <summary>Creates (or overwrites) an environment material asset at <paramref name="assetPath"/>.</summary>
        Material CreateEnvironmentMaterial(string assetPath, in EnvironmentMaterialDesc desc);

        /// <summary>
        /// Builds sun/sky/fog/exposure/post-processing and reflection probes under <paramref name="root"/> in the open scene.
        /// Volume profiles are saved next to <paramref name="profileFolder"/>.
        /// </summary>
        void BuildLighting(Transform root, in ArenaLightingDesc desc, string profileFolder);

        /// <summary>Pipeline-specific camera setup (AA, post, physical camera).</summary>
        void ConfigureCamera(Camera camera);

        /// <summary>Sets physically-based intensity (lux for directional, lumen for punctual) and colour temperature.</summary>
        void ConfigureLight(Light light, float physicalIntensity, float temperatureKelvin);
    }

    /// <summary>Registry for the active <see cref="IEditorRenderingHooks"/>.</summary>
    public static class EditorRenderingHooks
    {
        private static IEditorRenderingHooks s_pipelineHooks;
        private static IEditorRenderingHooks s_fallback;

        /// <summary>Called by pipeline adapter assemblies from an [InitializeOnLoad] static constructor.</summary>
        public static void Register(IEditorRenderingHooks hooks) => s_pipelineHooks = hooks;

        /// <summary>Set by the Editor module to its Built-in implementation.</summary>
        public static void RegisterFallback(IEditorRenderingHooks hooks) => s_fallback = hooks;

        /// <summary>
        /// The pipeline implementation when registered (even if not yet active: ConfigureProject activates it), else the fallback.
        /// Never null: if nothing registered yet (e.g. a static constructor ordering edge case in batch mode) the Built-in
        /// fallback is created on demand.
        /// </summary>
        public static IEditorRenderingHooks Active => s_pipelineHooks ?? s_fallback ?? (s_fallback = new BuiltInEditorRenderingHooks());

        public static bool HasPipelineHooks => s_pipelineHooks != null;

        /// <summary>The Built-in (Standard shader) fallback implementation, created on demand.</summary>
        public static IEditorRenderingHooks Fallback => s_fallback ?? (s_fallback = new BuiltInEditorRenderingHooks());
    }
}
