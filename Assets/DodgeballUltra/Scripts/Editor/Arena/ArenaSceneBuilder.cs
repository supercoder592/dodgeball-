using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DodgeballUltra.Arena;
using DodgeballUltra.Editor.Data;
using DodgeballUltra.Editor.Pipeline;
using DodgeballUltra.Editor.Setup;
using DodgeballUltra.Match;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace DodgeballUltra.Editor.ArenaScene
{
    /// <summary>Tuning for <see cref="ArenaSceneBuilder.Build"/>. Defaults describe a realistic indoor sports hall.</summary>
    [Serializable]
    public sealed class ArenaSceneSettings
    {
        /// <summary>Per-fixture flux that yields ~1000 lux on the court with the runtime arena's rig (matches RuntimeArenaBuilder).</summary>
        public const float DefaultFloodlightLumen = 55000f;

        /// <summary>Exposure for ~1000 lux court illuminance (matches RuntimeArenaBuilder's recommendation).</summary>
        public const float DefaultExposureEV100 = 9.5f;

        [Header("Textures")]
        [Tooltip("Resolution of the procedural PBR textures (per map).")]
        public int textureResolution = 1024;
        [Tooltip("Regenerate textures even if an up-to-date set exists on disk (only needed after changing the generator).")]
        public bool regenerateTextures;

        [Header("Floodlights (physical units)")]
        [Tooltip("Luminous flux of each LED sports floodlight (lm). The default matches the runtime arena " +
                 "(RuntimeArenaBuilder): 8 fixtures x 55 000 lm on 9.5 m trusses give ~1000 lux on the court, " +
                 "the level of a competition hall.")]
        [Min(0f)] public float floodlightLumen = DefaultFloodlightLumen;
        [Tooltip("Colour temperature of the floodlights (K). 5600 K = daylight-balanced broadcast lighting.")]
        [Range(2700f, 7500f)] public float floodlightTemperatureK = 5600f;

        [Header("Overhead fill (light bounced off roof and walls)")]
        [Tooltip("Illuminance of the soft overhead fill (lux). Indoors the directional light only acts as bounce fill.")]
        [Min(0f)] public float fillIlluminanceLux = 150f;
        [Range(4000f, 9000f)] public float fillTemperatureK = 5600f;
        [Tooltip("Direction of the fill light (steep, from above).")]
        public Vector3 fillEuler = new Vector3(70f, -35f, 0f);

        [Header("Camera")]
        [Tooltip("Fixed exposure (EV100) for the lit arena; lower = brighter. ~9.5 suits ~1000 lux court illuminance " +
                 "(EV100 = log2(L·100/12.5) for the court's luminance L). Raise it together with the floodlight flux.")]
        [Range(6f, 16f)] public float exposureEV100 = DefaultExposureEV100;
        [Tooltip("Optional HDRI (cubemap) for sky reflections; unused indoors when left empty.")]
        public Cubemap hdri;

        [Header("Global illumination (bake is optional)")]
        [Tooltip("Generate lightmap UVs (UV2) for procedural arena meshes so Generate Lighting works out of the box.")]
        public bool generateLightmapUVs = true;
        [Tooltip("Mark architecture Contribute GI / Occluder / Occludee / Batching / Reflection Probe static.")]
        public bool markStatic = true;
        [Tooltip("Create a Light Probe Group covering the court volume (players are lit by probes after a bake).")]
        public bool createLightProbes = true;
        [Tooltip("Horizontal spacing of light probes (m).")]
        [Range(1f, 6f)] public float lightProbeSpacing = 2.5f;
        [Tooltip("Lightmap texels per metre for the optional bake (the hall is large: 8-16 is plenty).")]
        [Range(2f, 40f)] public float lightmapTexelsPerUnit = 10f;

        [Header("Scene")]
        [Tooltip("Add the scene to Build Settings as the first scene.")]
        public bool addToBuildSettings = true;
        [Tooltip("Ask to save modified open scenes before replacing them.")]
        public bool promptToSaveOpenScenes = true;
    }

    /// <summary>Outcome of <see cref="ArenaSceneBuilder.Build"/>.</summary>
    public sealed class ArenaBuildResult
    {
        public bool Success;
        public bool Cancelled;
        public string ScenePath;
        public readonly List<string> Log = new List<string>();

        public string Summary => Success
            ? "Arena scene saved to " + ScenePath + "."
            : Cancelled ? "Arena build cancelled." : (Log.Count > 0 ? Log[Log.Count - 1] : "Arena build failed.");

        public string Report
        {
            get
            {
                var sb = new StringBuilder(Summary);
                foreach (string line in Log) sb.Append('\n').Append("• ").Append(line);
                return sb.ToString();
            }
        }
    }

    /// <summary>
    /// Builds <c>Assets/DodgeballUltra/Scenes/Arena.unity</c>: the realistic indoor arena from <see cref="RuntimeArenaBuilder"/>
    /// with saved PBR materials, physically based lighting from <see cref="EditorRenderingHooks.Active"/> (HDRP when available),
    /// light probes, a configured main camera and the <see cref="GameBootstrap"/> wired to the generated <see cref="GameConfig"/>.
    /// <para>
    /// Baking is optional and left to the user: the scene is fully lit in real time. For baked indirect light open
    /// <c>Window ▸ Rendering ▸ Lighting</c> and press <c>Generate Lighting</c> (architecture is static, lightmap UVs exist,
    /// light probes cover the court, and a <c>LightingSettings</c> asset with sensible values is assigned).
    /// </para>
    /// </summary>
    public static class ArenaSceneBuilder
    {
        /// <summary>Probe layer heights above the floor (m): ankles, hips, head, raised arms / lobs, high lobs.</summary>
        private static readonly float[] ProbeHeights = { 0.3f, 1.1f, 2.0f, 3.5f, 6.5f };

        /// <summary>Renderers smaller than this (largest bounds extent, m) receive GI from probes instead of lightmaps.</summary>
        private const float SmallObjectExtent = 0.6f;

        public static string ScenePath => DodgeballEditorPaths.ArenaScenePath;

        /// <summary>True when the arena scene exists on disk.</summary>
        public static bool SceneExists => AssetDatabase.LoadAssetAtPath<SceneAsset>(DodgeballEditorPaths.ArenaScenePath) != null;

        /// <summary>True when the arena scene is listed (enabled) in Build Settings.</summary>
        public static bool IsInBuildSettings()
        {
            foreach (EditorBuildSettingsScene s in EditorBuildSettings.scenes)
                if (s.enabled && s.path == DodgeballEditorPaths.ArenaScenePath) return true;
            return false;
        }

        /// <summary>Builds and saves the arena scene. Never throws: problems are reported in the result.</summary>
        public static ArenaBuildResult Build(ArenaSceneSettings settings = null)
        {
            settings ??= new ArenaSceneSettings();
            var result = new ArenaBuildResult { ScenePath = DodgeballEditorPaths.ArenaScenePath };

            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                result.Log.Add("Exit Play mode before building the arena scene.");
                return result;
            }

            if (settings.promptToSaveOpenScenes && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                result.Cancelled = true;
                return result;
            }

            try
            {
                // ------------------------------------------------------------ data
                GameConfig config = GameDataGenerator.LoadGameConfig();
                if (config == null)
                {
                    DataGenerationResult data = GameDataGenerator.Generate();
                    config = data.Config;
                    result.Log.Add(data.Success ? "Generated missing game data." : "Game data could not be generated: " + data.Summary);
                }

                // ------------------------------------------------------------ materials (before touching the scene)
                var materials = new ArenaMaterialLibrary(settings.textureResolution, settings.regenerateTextures);
                if (!materials.PrepareAll(true))
                {
                    result.Cancelled = true;
                    return result;
                }
                result.Log.AddRange(materials.Log);

                EditorUtility.DisplayProgressBar("Dodgeball Ultra · Arena", "Building arena geometry…", 0.2f);
                Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

                // ------------------------------------------------------------ geometry
                Court court = RuntimeArenaBuilder.Build(null, materials, false);
                if (court == null) throw new InvalidOperationException("RuntimeArenaBuilder.Build returned no Court.");
                Transform arenaRoot = court.transform;
                while (arenaRoot.parent != null) arenaRoot = arenaRoot.parent;
                result.Log.Add($"Arena built: court {court.length:0.#} × {court.width:0.#} m, outfield {court.outfieldDepth:0.#} m, run-off {court.runOff:0.#} m.");

                EditorUtility.DisplayProgressBar("Dodgeball Ultra · Arena", "Preparing static geometry & lightmap UVs…", 0.4f);
                int unwrapped = 0, staticObjects = 0;
                if (settings.markStatic) staticObjects = MarkStatic(arenaRoot, settings.generateLightmapUVs, out unwrapped);
                if (staticObjects > 0) result.Log.Add($"{staticObjects} objects marked static ({unwrapped} meshes got lightmap UVs).");

                EditorUtility.DisplayProgressBar("Dodgeball Ultra · Arena", "Saving generated meshes & materials…", 0.55f);
                int persisted = PersistGeneratedContent(arenaRoot);
                if (persisted > 0) result.Log.Add($"{persisted} generated meshes/materials saved as assets (keeps the scene file small).");

                // ------------------------------------------------------------ lighting
                EditorUtility.DisplayProgressBar("Dodgeball Ultra · Arena", "Building physically based lighting…", 0.7f);
                Transform lightingRoot = new GameObject("Lighting").transform;
                Bounds bounds = ComputeBounds(arenaRoot, court);
                var lightingDesc = new ArenaLightingDesc
                {
                    Indoor = true,
                    SunEuler = settings.fillEuler,
                    SunIlluminanceLux = settings.fillIlluminanceLux,
                    SunTemperatureK = settings.fillTemperatureK,
                    FloodlightLumen = settings.floodlightLumen,
                    FloodlightTemperatureK = settings.floodlightTemperatureK,
                    FloodlightPositions = GetFloodlightPositions(court),
                    FloodlightAimPoint = court.Center,
                    ArenaBounds = bounds,
                    Hdri = settings.hdri,
                    ExposureEV100 = settings.exposureEV100,
                };
                IEditorRenderingHooks hooks = EditorRenderingHooks.Active;
                DodgeballEditorPaths.EnsureFolder(DodgeballEditorPaths.ArenaLightingFolder);
                hooks.BuildLighting(lightingRoot, lightingDesc, DodgeballEditorPaths.ArenaLightingFolder);
                result.Log.Add($"Lighting ({hooks.PipelineName}): {lightingDesc.FloodlightPositions.Length} floodlights × " +
                               $"{settings.floodlightLumen:0} lm @ {settings.floodlightTemperatureK:0} K, exposure EV100 {settings.exposureEV100:0.#}.");

                if (settings.createLightProbes)
                {
                    int probes = CreateLightProbes(lightingRoot, court, settings.lightProbeSpacing);
                    result.Log.Add($"Light Probe Group with {probes} probes over the court volume.");
                }
                AssignLightingSettings(settings);

                // ------------------------------------------------------------ camera + bootstrap
                Camera camera = CreateMainCamera(court, hooks);
                var bootstrapGo = new GameObject("GameBootstrap");
                var bootstrap = bootstrapGo.AddComponent<GameBootstrap>();
                bootstrap.config = config;
                if (config == null) result.Log.Add("Warning: no GameConfig - GameBootstrap will use the in-memory default roster.");

                bootstrapGo.transform.SetSiblingIndex(0);
                camera.transform.SetSiblingIndex(1);
                lightingRoot.SetSiblingIndex(2);

                // ------------------------------------------------------------ save
                EditorUtility.DisplayProgressBar("Dodgeball Ultra · Arena", "Saving scene…", 0.9f);
                DodgeballEditorPaths.EnsureFolder(DodgeballEditorPaths.ScenesFolder);
                SceneManager.SetActiveScene(scene);
                if (!EditorSceneManager.SaveScene(scene, DodgeballEditorPaths.ArenaScenePath))
                    throw new IOException("Could not save " + DodgeballEditorPaths.ArenaScenePath);

                if (settings.addToBuildSettings) AddToBuildSettings(DodgeballEditorPaths.ArenaScenePath);
                AssetDatabase.SaveAssets();

                result.Log.Add("Optional: bake indirect lighting via Window ▸ Rendering ▸ Lighting ▸ Generate Lighting (not required to play).");
                result.Success = true;
                Debug.Log("[Dodgeball Ultra] " + result.Report);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                result.Success = false;
                result.Log.Add("Arena build failed: " + e.Message);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            return result;
        }

        /// <summary>Puts <paramref name="scenePath"/> first in Build Settings (enabled) and keeps QuickPlay listed after it.</summary>
        public static void AddToBuildSettings(string scenePath)
        {
            var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            scenes.RemoveAll(s => s.path == scenePath);
            scenes.Insert(0, new EditorBuildSettingsScene(scenePath, true));

            string quickPlay = DodgeballEditorPaths.QuickPlayScenePath;
            if (!scenes.Exists(s => s.path == quickPlay) && AssetDatabase.LoadAssetAtPath<SceneAsset>(quickPlay) != null)
                scenes.Add(new EditorBuildSettingsScene(quickPlay, true));

            EditorBuildSettings.scenes = scenes.ToArray();
        }

        // ------------------------------------------------------------------ geometry helpers

        private static Vector3[] GetFloodlightPositions(Court court)
        {
            Vector3[] positions = null;
            try
            {
                positions = RuntimeArenaBuilder.GetFloodlightPositions();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Dodgeball Ultra] RuntimeArenaBuilder.GetFloodlightPositions failed: " + e.Message);
            }
            if (positions == null || positions.Length == 0) return Array.Empty<Vector3>(); // hooks fall back to their own rig

            // Positions are relative to a court centred at the origin; follow the court if the builder moved it.
            var result = new Vector3[positions.Length];
            for (int i = 0; i < positions.Length; i++) result[i] = positions[i] + court.Center;
            return result;
        }

        private static Bounds ComputeBounds(Transform root, Court court)
        {
            bool any = false;
            var bounds = new Bounds(court.Center, Vector3.zero);
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r is ParticleSystemRenderer) continue;
                if (!any) { bounds = r.bounds; any = true; }
                else bounds.Encapsulate(r.bounds);
            }

            if (!any || bounds.size.x < court.width || bounds.size.z < court.length)
            {
                // No (or implausibly small) geometry: court + run-off, 12 m high.
                float w = court.width + 2f * court.runOff;
                float l = court.length + 2f * court.runOff;
                bounds = new Bounds(court.Center + Vector3.up * 6f, new Vector3(w, 12f, l));
            }
            return bounds;
        }

        /// <summary>
        /// Marks architecture static. Objects that may move or animate (rigidbodies, animators, particles, lights, the
        /// purely visual layer) are left dynamic. Small props receive GI from light probes instead of lightmaps.
        /// </summary>
        private static int MarkStatic(Transform root, bool generateUVs, out int unwrapped)
        {
            unwrapped = 0;
            int count = 0;
            var unwrappedMeshes = new HashSet<Mesh>();
            const StaticEditorFlags architecture = StaticEditorFlags.ContributeGI | StaticEditorFlags.OccluderStatic |
                                                   StaticEditorFlags.OccludeeStatic | StaticEditorFlags.BatchingStatic |
                                                   StaticEditorFlags.ReflectionProbeStatic;

            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                GameObject go = t.gameObject;
                if (!CanBeStatic(go)) continue;

                StaticEditorFlags flags = go.layer == GameLayers.Visual ? StaticEditorFlags.OccludeeStatic : architecture;
                var renderer = go.GetComponent<MeshRenderer>();
                var filter = go.GetComponent<MeshFilter>();
                bool contributesGI = (flags & StaticEditorFlags.ContributeGI) != 0;

                if (renderer != null && contributesGI)
                {
                    Vector3 extents = renderer.bounds.extents;
                    float maxExtent = Mathf.Max(extents.x, Mathf.Max(extents.y, extents.z));
                    // Thin/small props (seats, railings) light better and cheaper from probes.
                    bool useLightmaps = maxExtent >= SmallObjectExtent;
                    EditorOnlyApi.SetReceiveGIFromLightmaps(renderer, useLightmaps);
                    if (!useLightmaps) flags &= ~StaticEditorFlags.OccluderStatic;

                    Mesh mesh = filter != null ? filter.sharedMesh : null;
                    if (generateUVs && useLightmaps && mesh != null && !EditorUtility.IsPersistent(mesh)
                        && unwrappedMeshes.Add(mesh) && mesh.vertexCount > 0 && mesh.vertexCount < 200000 && !HasUV2(mesh))
                    {
                        Unwrapping.GenerateSecondaryUVSet(mesh);
                        unwrapped++;
                    }
                }

                GameObjectUtility.SetStaticEditorFlags(go, flags);
                count++;
            }
            return count;
        }

        private static bool HasUV2(Mesh mesh)
        {
            var uv2 = new List<Vector2>();
            mesh.GetUVs(1, uv2);
            return uv2.Count == mesh.vertexCount;
        }

        private static bool CanBeStatic(GameObject go)
        {
            if (go.GetComponent<Rigidbody>() != null) return false;
            if (go.GetComponent<Light>() != null) return false;
            if (go.GetComponent<ParticleSystem>() != null) return false;
            if (go.GetComponentInParent<Animator>() != null) return false;
            int layer = go.layer;
            return layer != GameLayers.Ball && layer != GameLayers.Player && layer != GameLayers.Ragdoll &&
                   layer != GameLayers.AbilityVolume && layer != GameLayers.Hittable;
        }

        /// <summary>
        /// Saves meshes / materials / textures the runtime builder created in memory as assets under Generated/Arena
        /// (meshes in one container asset). Returns how many objects were persisted.
        /// </summary>
        private static int PersistGeneratedContent(Transform root)
        {
            int count = 0;

            // ---- meshes -> Generated/Arena/ArenaMeshes.asset (one file, sub-assets)
            var meshes = new List<Mesh>();
            var seenMeshes = new HashSet<Mesh>();
            void AddMesh(Mesh m)
            {
                if (m != null && !EditorUtility.IsPersistent(m) && (m.hideFlags & HideFlags.DontSave) == 0 && seenMeshes.Add(m)) meshes.Add(m);
            }
            foreach (MeshFilter mf in root.GetComponentsInChildren<MeshFilter>(true)) AddMesh(mf.sharedMesh);
            foreach (MeshCollider mc in root.GetComponentsInChildren<MeshCollider>(true)) AddMesh(mc.sharedMesh);
            foreach (SkinnedMeshRenderer smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true)) AddMesh(smr.sharedMesh);

            string meshPath = DodgeballEditorPaths.ArenaMeshesPath;
            if (AssetDatabase.LoadMainAssetAtPath(meshPath) != null) AssetDatabase.DeleteAsset(meshPath);
            if (meshes.Count > 0)
            {
                DodgeballEditorPaths.EnsureFolder(DodgeballEditorPaths.ArenaRoot);
                for (int i = 0; i < meshes.Count; i++)
                    if (string.IsNullOrEmpty(meshes[i].name)) meshes[i].name = "ArenaMesh_" + i.ToString("000");
                AssetDatabase.CreateAsset(meshes[0], meshPath);
                for (int i = 1; i < meshes.Count; i++) AssetDatabase.AddObjectToAsset(meshes[i], meshPath);
                count += meshes.Count;
            }

            // ---- materials/textures the builder created itself (not from the provider)
            string folder = DodgeballEditorPaths.ArenaRuntimeContentFolder;
            if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
            var savedMaterials = new HashSet<Material>();
            var savedTextures = new HashSet<Texture>();
            int index = 0;
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
            {
                Material[] mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    Material m = mats[i];
                    if (m == null || EditorUtility.IsPersistent(m) || (m.hideFlags & HideFlags.DontSave) != 0 || savedMaterials.Contains(m)) continue;

                    DodgeballEditorPaths.EnsureFolder(folder);
                    foreach (int id in m.GetTexturePropertyNameIDs())
                    {
                        Texture tex = m.GetTexture(id);
                        if (tex == null || EditorUtility.IsPersistent(tex) || (tex.hideFlags & HideFlags.DontSave) != 0 || !savedTextures.Add(tex)) continue;
                        string texName = SanitizeFileName(string.IsNullOrEmpty(tex.name) ? "Texture" : tex.name);
                        AssetDatabase.CreateAsset(tex, AssetDatabase.GenerateUniqueAssetPath($"{folder}/{texName}.asset"));
                        count++;
                    }

                    string matName = SanitizeFileName(string.IsNullOrEmpty(m.name) ? "Material_" + index : m.name);
                    AssetDatabase.CreateAsset(m, AssetDatabase.GenerateUniqueAssetPath($"{folder}/{matName}.mat"));
                    savedMaterials.Add(m);
                    index++;
                    count++;
                }
            }

            return count;
        }

        private static string SanitizeFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Replace('/', '_').Replace('\\', '_').Trim();
        }

        // ------------------------------------------------------------------ lighting helpers

        /// <summary>
        /// A regular probe lattice over court + run-off (inset from the walls) at player-relevant heights. Probes only matter
        /// after a bake; until then players use the ambient / sky lighting.
        /// </summary>
        private static int CreateLightProbes(Transform parent, Court court, float spacing)
        {
            var go = new GameObject("Light Probes");
            go.transform.SetParent(parent, false);
            go.transform.position = Vector3.zero;
            var group = go.AddComponent<LightProbeGroup>();

            float halfW = court.width * 0.5f + Mathf.Max(0f, court.runOff - 0.5f);
            float halfL = court.length * 0.5f + Mathf.Max(court.outfieldDepth, court.runOff - 0.5f);
            int nx = Mathf.Max(2, Mathf.CeilToInt(2f * halfW / spacing) + 1);
            int nz = Mathf.Max(2, Mathf.CeilToInt(2f * halfL / spacing) + 1);

            var positions = new List<Vector3>(nx * nz * ProbeHeights.Length);
            Vector3 c = court.Center;
            foreach (float h in ProbeHeights)
            {
                // Sparser lattice for the high layer: light varies slowly up there.
                int step = h > 5f ? 2 : 1;
                for (int ix = 0; ix < nx; ix += step)
                for (int iz = 0; iz < nz; iz += step)
                {
                    float x = Mathf.Lerp(-halfW, halfW, ix / (float)(nx - 1));
                    float z = Mathf.Lerp(-halfL, halfL, iz / (float)(nz - 1));
                    positions.Add(new Vector3(c.x + x, c.y + h, c.z + z));
                }
            }

            if (!EditorOnlyApi.SetProbePositions(group, positions.ToArray()))
            {
                // Fallback through serialization (field name of LightProbeGroup's source positions).
                var so = new SerializedObject(group);
                SerializedProperty array = so.FindProperty("m_SourcePositions");
                if (array == null) return 0;
                array.arraySize = positions.Count;
                for (int i = 0; i < positions.Count; i++) array.GetArrayElementAtIndex(i).vector3Value = positions[i];
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            return positions.Count;
        }

        /// <summary>Assigns a LightingSettings asset tuned for an optional indirect bake (auto-generate off).</summary>
        private static void AssignLightingSettings(ArenaSceneSettings settings)
        {
            string path = DodgeballEditorPaths.ArenaLightingFolder + "/ArenaLightingSettings.lighting";
            var lighting = AssetDatabase.LoadAssetAtPath<LightingSettings>(path);
            if (lighting == null)
            {
                lighting = new LightingSettings { name = "ArenaLightingSettings" };
                AssetDatabase.CreateAsset(lighting, path);
            }

            lighting.bakedGI = true;
            lighting.realtimeGI = false;
            // Editor-only members (stripped from the player reference assemblies) are set by their public names.
            EditorOnlyApi.TrySet(lighting, "autoGenerate", false);                   // baking is an explicit, optional step
            EditorOnlyApi.TrySet(lighting, "lightmapper", "ProgressiveGPU");         // falls back to CPU automatically
            EditorOnlyApi.TrySet(lighting, "directionalityMode", LightmapsMode.CombinedDirectional);
            EditorOnlyApi.TrySet(lighting, "lightmapResolution", settings.lightmapTexelsPerUnit);
            EditorOnlyApi.TrySet(lighting, "lightmapPadding", 4);
            EditorOnlyApi.TrySet(lighting, "lightmapMaxSize", 2048);
            EditorOnlyApi.TrySet(lighting, "mixedBakeMode", MixedLightingMode.IndirectOnly); // real-time floodlights + baked bounce
            EditorUtility.SetDirty(lighting);

            Lightmapping.lightingSettings = lighting;
        }

        private static Camera CreateMainCamera(Court court, IEditorRenderingHooks hooks)
        {
            var go = new GameObject("Main Camera") { tag = "MainCamera" };
            // Broadcast-style start framing behind the Home baseline; the runtime camera rig takes over at play time.
            float back = court.length * 0.5f + court.outfieldDepth + 3f;
            go.transform.position = court.Center + new Vector3(0f, 6f, -back);
            go.transform.LookAt(court.Center + Vector3.up * 1f);

            var camera = go.AddComponent<Camera>();
            camera.fieldOfView = 50f;
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 500f;
            go.AddComponent<AudioListener>();

            try
            {
                hooks.ConfigureCamera(camera);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            return camera;
        }
    }
}
