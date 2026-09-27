using System.Collections.Generic;
using DodgeballUltra.Rendering;
using UnityEngine;

namespace DodgeballUltra.Arena
{
    /// <summary>
    /// Runtime <see cref="IArenaMaterialProvider"/>: lit PBR materials built by <see cref="MaterialFactory"/> from the
    /// procedural <see cref="ArenaTextureGenerator"/> sets. Materials are cached per surface, resolution and render pipeline,
    /// so rebuilding the arena (scene reload, rematch) costs no texture generation.
    /// </summary>
    /// <remarks>
    /// This is the fallback path. The editor scene builder passes a provider backed by saved material assets (created via
    /// EditorRenderingHooks from the same deterministic textures), which is preferred for player builds because saved
    /// materials keep their shader variants from being stripped.
    /// </remarks>
    public sealed class RuntimeArenaMaterials : IArenaMaterialProvider
    {
        private static readonly Dictionary<int, Material> s_cache = new Dictionary<int, Material>();

        private readonly int m_resolution;
        private readonly bool m_keepReadable;

        /// <param name="resolution">Hardwood texture resolution (other surfaces scale down, see ArenaTextureGenerator).</param>
        public RuntimeArenaMaterials(int resolution = 1024)
        {
            m_resolution = Mathf.Clamp(resolution, ArenaTextureGenerator.MinResolution, ArenaTextureGenerator.MaxResolution);
            // Play mode releases CPU copies after upload; edit mode keeps them readable so tools can save PNGs.
            m_keepReadable = !Application.isPlaying;
        }

        public Material GetMaterial(ArenaSurface surface)
        {
            int key = ((int)RenderPipelineUtil.Current << 28) | ((int)surface << 20) | (m_resolution << 1) | (m_keepReadable ? 1 : 0);
            if (s_cache.TryGetValue(key, out Material cached) && cached != null) return cached;

            Material m = CreateMaterial(surface, m_resolution, m_keepReadable);
            s_cache[key] = m;
            return m;
        }

        /// <summary>Creates a new (uncached) material for <paramref name="surface"/> on the active pipeline.</summary>
        public static Material CreateMaterial(ArenaSurface surface, int resolution, bool keepReadable)
        {
            ArenaTextureSet set = ArenaTextureGenerator.Generate(surface, resolution, keepReadable);
            var desc = new LitMaterialDesc
            {
                BaseColor = set.Tint,
                BaseMap = set.BaseColor,
                NormalMap = set.Normal,
                NormalScale = set.NormalStrength > 0f ? set.NormalStrength : 1f,
                MaskMap = set.Mask,
                Smoothness = set.Smoothness,
                Metallic = set.Metallic,
                Tiling = set.Tiling,
                EmissiveColor = set.EmissiveColor,
                EmissiveIntensity = set.EmissiveIntensity,
                // The LED pattern doubles as emission mask so only the emitters glow.
                EmissiveMap = set.EmissiveIntensity > 0f ? set.BaseColor : null,
                Transparent = false,
                DoubleSided = false,
            };
            return MaterialFactory.CreateLit("DU_Arena_" + surface, in desc);
        }

        /// <summary>Forgets cached materials (optionally destroying them). Textures stay cached in the generator.</summary>
        public static void ClearCache(bool destroyMaterials)
        {
            if (destroyMaterials)
            {
                foreach (var kv in s_cache)
                {
                    if (kv.Value == null) continue;
                    if (Application.isPlaying) Object.Destroy(kv.Value);
                    else Object.DestroyImmediate(kv.Value);
                }
            }
            s_cache.Clear();
        }
    }
}
