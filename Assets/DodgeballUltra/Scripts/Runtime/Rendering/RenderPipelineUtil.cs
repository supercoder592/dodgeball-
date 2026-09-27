using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

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
    /// <remarks>
    /// <see cref="Current"/> is evaluated at most once per rendered frame while playing (a cheap reference compare of the
    /// active pipeline asset decides whether the type-name classification has to run again). In edit mode, where
    /// <c>Time.frameCount</c> does not advance reliably, the reference compare runs on every call. Call
    /// <see cref="Invalidate"/> after swapping pipeline assets from code in the same frame.
    /// </remarks>
    public static class RenderPipelineUtil
    {
        private static PipelineKind s_kind;
        private static bool s_valid;
        private static int s_frame = -1;
        private static RenderPipelineAsset s_asset;
        private static readonly Dictionary<string, Shader> s_shaderCache = new Dictionary<string, Shader>(32, StringComparer.Ordinal);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            // Enter-Play-Mode without domain reload: forget everything that may refer to the previous session.
            s_valid = false;
            s_frame = -1;
            s_asset = null;
            s_shaderCache.Clear();
        }

        /// <summary>The render pipeline currently rendering (quality-level override first, then the graphics default).</summary>
        public static PipelineKind Current
        {
            get
            {
                bool playing = Application.isPlaying;
                int frame = Time.frameCount;
                if (s_valid && playing && frame == s_frame) return s_kind;

                RenderPipelineAsset asset = GraphicsSettings.currentRenderPipeline;
                // ReferenceEquals avoids UnityEngine.Object's (slower) overloaded equality; a destroyed asset that was
                // replaced by a new instance is a different reference and therefore re-classified.
                if (!s_valid || !ReferenceEquals(asset, s_asset))
                {
                    s_kind = Classify(asset);
                    s_asset = asset;
                    s_valid = true;
                }

                s_frame = frame;
                return s_kind;
            }
        }

        /// <summary>True when HDRP renders (the shipping configuration of Dodgeball Ultra).</summary>
        public static bool IsHighDefinition => Current == PipelineKind.HighDefinition;

        /// <summary>True when URP renders.</summary>
        public static bool IsUniversal => Current == PipelineKind.Universal;

        /// <summary>True when the Built-in pipeline renders (no SRP asset assigned).</summary>
        public static bool IsBuiltIn => Current == PipelineKind.BuiltIn;

        /// <summary>Forces <see cref="Current"/> to re-evaluate on its next access (e.g. after swapping pipeline assets).</summary>
        public static void Invalidate()
        {
            s_valid = false;
            s_frame = -1;
        }

        /// <summary>
        /// Classifies a pipeline asset by its type name (and base type names, so project-specific subclasses of the HDRP
        /// or URP assets are recognised). Null means the Built-in pipeline; unknown custom SRPs are reported as Built-in.
        /// </summary>
        public static PipelineKind Classify(RenderPipelineAsset asset)
        {
            if (asset == null) return PipelineKind.BuiltIn;
            for (Type t = asset.GetType(); t != null && t != typeof(RenderPipelineAsset); t = t.BaseType)
            {
                string n = t.Name;
                if (n.IndexOf("HDRenderPipelineAsset", StringComparison.Ordinal) >= 0) return PipelineKind.HighDefinition;
                if (n.IndexOf("UniversalRenderPipelineAsset", StringComparison.Ordinal) >= 0) return PipelineKind.Universal;
            }
            return PipelineKind.BuiltIn;
        }

        /// <summary>First shader found among <paramref name="names"/> (null if none).</summary>
        /// <remarks>
        /// A shader that exists but is not supported by the active pipeline/hardware is only returned when no later
        /// candidate is supported. Found shaders are cached by name; misses are not cached so shaders loaded later (asset
        /// bundles, addressables) are still discovered. Note that <c>Shader.Find</c> only finds shaders included in a
        /// player build (referenced by a material or listed in "Always Included Shaders"): editor-generated material
        /// assets are the preferred path for builds, runtime creation is the fallback.
        /// </remarks>
        public static Shader FindShader(params string[] names)
        {
            if (names == null) return null;
            Shader firstFound = null;
            for (int i = 0; i < names.Length; i++)
            {
                string n = names[i];
                if (string.IsNullOrEmpty(n)) continue;

                if (!s_shaderCache.TryGetValue(n, out Shader shader) || shader == null)
                {
                    shader = Shader.Find(n);
                    if (shader == null) continue;
                    s_shaderCache[n] = shader;
                }

                if (shader.isSupported) return shader;
                if (firstFound == null) firstFound = shader;
            }
            return firstFound;
        }
    }

    /// <summary>Property names that differ between pipelines.</summary>
    /// <remarks>
    /// Names refer to the pipeline's standard lit shader (HDRP/Lit, Universal Render Pipeline/Lit, Standard). The
    /// <c>*Id</c> variants return cached <see cref="Shader.PropertyToID(string)"/> values for per-frame use with
    /// MaterialPropertyBlocks (no string hashing in hot paths).
    /// </remarks>
    public static class ShaderProps
    {
        public static string BaseColor => Pick("_BaseColor", "_BaseColor", "_Color");
        public static string BaseMap => Pick("_BaseColorMap", "_BaseMap", "_MainTex");
        public static string NormalMap => Pick("_NormalMap", "_BumpMap", "_BumpMap");
        public static string Smoothness => Pick("_Smoothness", "_Smoothness", "_Glossiness");
        public static string Metallic => Pick("_Metallic", "_Metallic", "_Metallic");
        public static string EmissionColor => Pick("_EmissiveColor", "_EmissionColor", "_EmissionColor");

        /// <summary>Normal map strength.</summary>
        public static string NormalScale => Pick("_NormalScale", "_BumpScale", "_BumpScale");

        /// <summary>Packed metallic/AO/smoothness map (HDRP mask map; URP/Built-in metallic-gloss map with the same R/A layout).</summary>
        public static string MaskMap => Pick("_MaskMap", "_MetallicGlossMap", "_MetallicGlossMap");

        /// <summary>Emission colour texture.</summary>
        public static string EmissionMap => Pick("_EmissiveColorMap", "_EmissionMap", "_EmissionMap");

        /// <summary>Colour of the pipeline's unlit shader (HDRP/Unlit uses _UnlitColor).</summary>
        public static string UnlitColor => Pick("_UnlitColor", "_BaseColor", "_Color");

        /// <summary>Texture of the pipeline's unlit shader.</summary>
        public static string UnlitMap => Pick("_UnlitColorMap", "_BaseMap", "_MainTex");

        public static int BaseColorId => PickId(s_baseColor);
        public static int BaseMapId => PickId(s_baseMap);
        public static int NormalMapId => PickId(s_normalMap);
        public static int SmoothnessId => PickId(s_smoothness);
        public static int MetallicId => PickId(s_metallic);
        public static int EmissionColorId => PickId(s_emission);
        public static int UnlitColorId => PickId(s_unlitColor);

        // Index order: HighDefinition, Universal, BuiltIn (see Index()).
        private static readonly int[] s_baseColor = Ids("_BaseColor", "_BaseColor", "_Color");
        private static readonly int[] s_baseMap = Ids("_BaseColorMap", "_BaseMap", "_MainTex");
        private static readonly int[] s_normalMap = Ids("_NormalMap", "_BumpMap", "_BumpMap");
        private static readonly int[] s_smoothness = Ids("_Smoothness", "_Smoothness", "_Glossiness");
        private static readonly int[] s_metallic = Ids("_Metallic", "_Metallic", "_Metallic");
        private static readonly int[] s_emission = Ids("_EmissiveColor", "_EmissionColor", "_EmissionColor");
        private static readonly int[] s_unlitColor = Ids("_UnlitColor", "_BaseColor", "_Color");

        /// <summary>Returns the property name for an explicit pipeline (useful for editor tools targeting another pipeline).</summary>
        public static string For(PipelineKind kind, string hdrp, string urp, string builtIn) =>
            kind == PipelineKind.HighDefinition ? hdrp : kind == PipelineKind.Universal ? urp : builtIn;

        private static string Pick(string hdrp, string urp, string builtIn) => For(RenderPipelineUtil.Current, hdrp, urp, builtIn);

        private static int PickId(int[] ids) => ids[Index(RenderPipelineUtil.Current)];

        private static int Index(PipelineKind kind) =>
            kind == PipelineKind.HighDefinition ? 0 : kind == PipelineKind.Universal ? 1 : 2;

        private static int[] Ids(string hdrp, string urp, string builtIn) =>
            new[] { Shader.PropertyToID(hdrp), Shader.PropertyToID(urp), Shader.PropertyToID(builtIn) };
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
