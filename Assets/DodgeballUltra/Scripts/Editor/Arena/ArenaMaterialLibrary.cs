using System;
using System.Collections.Generic;
using System.IO;
using DodgeballUltra.Arena;
using DodgeballUltra.Editor.Pipeline;
using DodgeballUltra.Editor.Setup;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DodgeballUltra.Editor.ArenaScene
{
    /// <summary>
    /// <see cref="IArenaMaterialProvider"/> backed by saved assets, used by <see cref="ArenaSceneBuilder"/>:
    /// <list type="number">
    /// <item><see cref="ArenaTextureGenerator"/> produces the procedural PBR set of each <see cref="ArenaSurface"/> (deterministic, so
    /// the editor PNGs match what the runtime builder would generate).</item>
    /// <item>Each map is written as PNG under <c>Generated/Arena/Textures</c> and imported with the right settings: base colour sRGB,
    /// normal map as Normal Map (BC5), mask linear with alpha (BC7); floors get 16x anisotropic filtering.</item>
    /// <item>Materials are created through <see cref="EditorRenderingHooks.Active"/> (HDRP Lit when the HDRP hooks are loaded,
    /// Standard otherwise) under <c>Generated/Arena/Materials</c>, keeping their GUIDs across rebuilds.</item>
    /// </list>
    /// Surface parameters (tiling, smoothness, metallic, tint) are stored in a small JSON sidecar so an unchanged texture set
    /// is not regenerated on every rebuild.
    /// </summary>
    public sealed class ArenaMaterialLibrary : IArenaMaterialProvider
    {
        /// <summary>Colour temperature of the floodlight lenses (matches the floodlights).</summary>
        public const float EmitterTemperatureK = 5600f;

        /// <summary>Luminance of a floodlight lens in nits (HDRP); bright enough to bloom at arena exposure.</summary>
        public const float EmitterLuminanceNits = 30000f;

        private readonly Dictionary<ArenaSurface, Material> _materials = new Dictionary<ArenaSurface, Material>();

        /// <summary>Resolution of generated textures (square, power of two).</summary>
        public int TextureResolution { get; }

        /// <summary>Regenerate textures even when an up-to-date set exists on disk.</summary>
        public bool RegenerateTextures { get; }

        /// <summary>Human-readable notes collected while preparing (fallbacks, errors).</summary>
        public List<string> Log { get; } = new List<string>();

        public ArenaMaterialLibrary(int textureResolution = 1024, bool regenerateTextures = false)
        {
            TextureResolution = Mathf.ClosestPowerOfTwo(Mathf.Clamp(textureResolution, 128, 4096));
            RegenerateTextures = regenerateTextures;
        }

        public static string MaterialPath(ArenaSurface surface) => $"{DodgeballEditorPaths.ArenaMaterialsFolder}/Arena_{surface}.mat";

        public static string TexturePath(ArenaSurface surface, string map) => $"{DodgeballEditorPaths.ArenaTexturesFolder}/Arena_{surface}_{map}.png";

        private static string MetaPath(ArenaSurface surface) => $"{DodgeballEditorPaths.ArenaTexturesFolder}/Arena_{surface}_Surface.json";

        /// <summary>
        /// Prepares every surface up front (textures + materials) with a cancelable progress bar.
        /// Returns false when the user cancelled.
        /// </summary>
        public bool PrepareAll(bool showProgress)
        {
            var surfaces = (ArenaSurface[])Enum.GetValues(typeof(ArenaSurface));
            try
            {
                for (int i = 0; i < surfaces.Length; i++)
                {
                    if (showProgress && EditorUtility.DisplayCancelableProgressBar("Dodgeball Ultra · Arena",
                            $"Preparing {surfaces[i]} textures & material ({i + 1}/{surfaces.Length})…", i / (float)surfaces.Length))
                        return false;
                    GetMaterial(surfaces[i]);
                }
                return true;
            }
            finally
            {
                if (showProgress) EditorUtility.ClearProgressBar();
            }
        }

        /// <summary><see cref="IArenaMaterialProvider"/>: the saved material asset for <paramref name="surface"/> (never null).</summary>
        public Material GetMaterial(ArenaSurface surface)
        {
            if (_materials.TryGetValue(surface, out Material cached) && cached != null) return cached;
            Material material = Prepare(surface);
            _materials[surface] = material;
            return material;
        }

        // ------------------------------------------------------------------ internals

        [Serializable]
        private sealed class SurfaceMeta
        {
            public int resolution;
            public Vector2 tiling = Vector2.one;
            public float smoothness = 0.5f;
            public float metallic;
            public Color tint = Color.white;
            public bool hasBase;
            public bool hasNormal;
            public bool hasMask;
        }

        private Material Prepare(ArenaSurface surface)
        {
            SurfaceMeta meta = null;
            Texture2D baseMap = null, normalMap = null, maskMap = null;

            try
            {
                meta = LoadMeta(surface);
                bool upToDate = !RegenerateTextures && meta != null && meta.resolution == TextureResolution
                                && (!meta.hasBase || File.Exists(DodgeballEditorPaths.ToAbsolute(TexturePath(surface, "BaseColor"))))
                                && (!meta.hasNormal || File.Exists(DodgeballEditorPaths.ToAbsolute(TexturePath(surface, "Normal"))))
                                && (!meta.hasMask || File.Exists(DodgeballEditorPaths.ToAbsolute(TexturePath(surface, "Mask"))));

                if (upToDate)
                {
                    if (meta.hasBase) baseMap = AssetDatabase.LoadAssetAtPath<Texture2D>(TexturePath(surface, "BaseColor"));
                    if (meta.hasNormal) normalMap = AssetDatabase.LoadAssetAtPath<Texture2D>(TexturePath(surface, "Normal"));
                    if (meta.hasMask) maskMap = AssetDatabase.LoadAssetAtPath<Texture2D>(TexturePath(surface, "Mask"));
                }
                else
                {
                    meta = GenerateTextures(surface, out baseMap, out normalMap, out maskMap);
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Log.Add($"{surface}: texture generation failed ({e.Message}); using a flat material.");
                meta ??= new SurfaceMeta { resolution = TextureResolution };
            }

            var desc = new EnvironmentMaterialDesc
            {
                Name = "Arena_" + surface,
                BaseColor = meta.tint.maxColorComponent > 0f ? meta.tint : Color.white,
                BaseMap = baseMap,
                NormalMap = normalMap,
                MaskMap = maskMap,
                Smoothness = meta.smoothness,
                Metallic = meta.metallic,
                Tiling = meta.tiling.sqrMagnitude > 0f ? meta.tiling : Vector2.one,
                NormalStrength = 1f,
                EmissiveColor = Color.black,
                EmissiveIntensity = 0f,
                Transparent = false,
                DoubleSided = surface == ArenaSurface.Seats, // thin moulded seat shells are visible from behind
            };

            if (surface == ArenaSurface.LightEmitter)
            {
                Color cct = Mathf.CorrelatedColorTemperatureToRGB(EmitterTemperatureK);
                float max = Mathf.Max(0.0001f, cct.maxColorComponent);
                desc.EmissiveColor = new Color(cct.r / max, cct.g / max, cct.b / max, 1f);
                desc.EmissiveIntensity = EmitterLuminanceNits;
            }

            try
            {
                Material material = EditorRenderingHooks.Active.CreateEnvironmentMaterial(MaterialPath(surface), desc);
                if (material != null) return material;
                Log.Add($"{surface}: the rendering hooks returned no material.");
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Log.Add($"{surface}: material creation failed ({e.Message}).");
            }

            // Last resort so the builder always gets a material: whatever exists at the path, else the fallback hooks.
            var existing = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath(surface));
            if (existing != null) return existing;
            return EditorRenderingHooks.Fallback.CreateEnvironmentMaterial(MaterialPath(surface), desc);
        }

        private SurfaceMeta GenerateTextures(ArenaSurface surface, out Texture2D baseMap, out Texture2D normalMap, out Texture2D maskMap)
        {
            baseMap = normalMap = maskMap = null;
            ArenaTextureSet set = ArenaTextureGenerator.Generate(surface, TextureResolution);
            var meta = new SurfaceMeta
            {
                resolution = TextureResolution,
                tiling = set.Tiling,
                smoothness = set.Smoothness,
                metallic = set.Metallic,
                tint = set.Tint,
                hasBase = set.BaseColor != null,
                hasNormal = set.Normal != null,
                hasMask = set.Mask != null,
            };

            // Surfaces seen at grazing angles (the floors) need strong anisotropic filtering to stay crisp.
            bool floor = surface == ArenaSurface.CourtWood || surface == ArenaSurface.CourtLinePaint ||
                         surface == ArenaSurface.OutfieldZone || surface == ArenaSurface.RunOffFloor;
            int aniso = floor ? 16 : 4;

            try
            {
                if (set.BaseColor != null)
                    baseMap = TextureAssetUtility.SavePng(set.BaseColor, TexturePath(surface, "BaseColor"), TextureRole.Color, TextureResolution, aniso);
                if (set.Normal != null)
                    normalMap = TextureAssetUtility.SavePng(set.Normal, TexturePath(surface, "Normal"), TextureRole.Normal, TextureResolution, aniso);
                if (set.Mask != null)
                    maskMap = TextureAssetUtility.SavePng(set.Mask, TexturePath(surface, "Mask"), TextureRole.Mask, TextureResolution, aniso);
            }
            finally
            {
                // The generator's in-memory textures are no longer needed once the PNG assets exist.
                if (set.BaseColor != null) Object.DestroyImmediate(set.BaseColor);
                if (set.Normal != null) Object.DestroyImmediate(set.Normal);
                if (set.Mask != null) Object.DestroyImmediate(set.Mask);
            }

            SaveMeta(surface, meta);
            return meta;
        }

        private static SurfaceMeta LoadMeta(ArenaSurface surface)
        {
            string file = DodgeballEditorPaths.ToAbsolute(MetaPath(surface));
            if (!File.Exists(file)) return null;
            try
            {
                return JsonUtility.FromJson<SurfaceMeta>(File.ReadAllText(file));
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void SaveMeta(ArenaSurface surface, SurfaceMeta meta)
        {
            DodgeballEditorPaths.EnsureFolder(DodgeballEditorPaths.ArenaTexturesFolder);
            string path = MetaPath(surface);
            File.WriteAllText(DodgeballEditorPaths.ToAbsolute(path), JsonUtility.ToJson(meta, true));
            AssetDatabase.ImportAsset(path);
        }
    }
}
