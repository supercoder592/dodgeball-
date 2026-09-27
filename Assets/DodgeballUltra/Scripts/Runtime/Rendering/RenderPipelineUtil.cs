using System;
using UnityEngine;

namespace DodgeballUltra.Rendering
{
    public enum PipelineKind
    {
        BuiltIn = 0,
        Universal,
        HighDefinition,
    }

    /// <summary>
    /// CONTRACT (kernel) - pipeline detection and pipeline-agnostic material creation (runtime fallback only; the editor
    /// Setup Wizard creates proper assets). Detection uses GraphicsSettings.currentRenderPipeline's type name so this
    /// assembly needs no SRP package reference.
    /// <para>Owner module: Rendering.</para>
    /// </summary>
    public static class RenderPipelineUtil
    {
        // IMPLEMENT: Rendering module
        public static PipelineKind Current => throw new NotImplementedException();

        /// <summary>First shader found among <paramref name="names"/> (null if none).</summary>
        public static Shader FindShader(params string[] names) => throw new NotImplementedException();
    }

    /// <summary>Property names that differ between pipelines.</summary>
    public static class ShaderProps
    {
        // IMPLEMENT: Rendering module (values depend on RenderPipelineUtil.Current)
        public static string BaseColor => throw new NotImplementedException();
        public static string BaseMap => throw new NotImplementedException();
        public static string NormalMap => throw new NotImplementedException();
        public static string Smoothness => throw new NotImplementedException();
        public static string Metallic => throw new NotImplementedException();
        public static string EmissionColor => throw new NotImplementedException();
    }

    /// <summary>
    /// CONTRACT (kernel) - creates materials that look physically plausible on the active pipeline.
    /// <para>Owner module: Rendering.</para>
    /// </summary>
    public static class MaterialFactory
    {
        // IMPLEMENT: Rendering module
        public static Material CreateLit(string name, Color baseColor, float smoothness, float metallic = 0f,
            Texture2D baseMap = null, Texture2D normalMap = null, Vector2? tiling = null) => throw new NotImplementedException();

        /// <summary>Transparent unlit (additive or alpha) material for particles, rings, ghosts.</summary>
        public static Material CreateParticle(string name, Color tint, bool additive, Texture2D texture = null) => throw new NotImplementedException();

        /// <summary>Opaque/transparent unlit material with HDR colour (emissive markers, beams).</summary>
        public static Material CreateUnlit(string name, Color color, bool transparent) => throw new NotImplementedException();

        /// <summary>Translucent "ghost" material used for afterimages, clones and cloaks.</summary>
        public static Material CreateGhost(string name, Color tint) => throw new NotImplementedException();

        /// <summary>Soft round particle texture (generated once, cached).</summary>
        public static Texture2D SoftParticleTexture => throw new NotImplementedException();
    }

    /// <summary>
    /// Runtime hooks a pipeline adapter assembly (DodgeballUltra.Rendering.HDRP) can install to create pipeline-specific
    /// objects without the runtime assembly referencing the SRP packages.
    /// </summary>
    public interface IRuntimeRenderingHooks
    {
        /// <summary>Configure a light created at runtime (physical units etc.). intensity is in lux (directional) or lumen (punctual).</summary>
        void ConfigureLight(Light light, float physicalIntensity, float temperatureKelvin);

        /// <summary>Configure a gameplay camera (anti-aliasing, post-processing flags).</summary>
        void ConfigureCamera(Camera camera);

        /// <summary>Creates the global volume (sky, exposure, fog, tonemapping, AO, SSR, contact shadows) when a scene has none.</summary>
        void EnsureEnvironmentVolume(Transform parent);

        /// <summary>Called on a material created at runtime so the pipeline can set keywords/passes.</summary>
        void ValidateMaterial(Material material);
    }

    public static class RuntimeRenderingHooks
    {
        public static IRuntimeRenderingHooks Active { get; set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => Active = null;
    }
}
