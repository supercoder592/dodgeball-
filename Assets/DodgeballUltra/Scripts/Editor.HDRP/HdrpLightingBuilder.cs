using System;
using System.Collections.Generic;
using DodgeballUltra.Editor.Pipeline;
using DodgeballUltra.Rendering.HDRP;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using Object = UnityEngine.Object;

namespace DodgeballUltra.Editor.HDRP
{
    /// <summary>
    /// Builds the physically based HDRP lighting of the arena scene (<see cref="IEditorRenderingHooks.BuildLighting"/>):
    /// <list type="bullet">
    /// <item>a global Volume with a saved VolumeProfile: Physically Based Sky (or HDRI Sky when an HDRI is supplied),
    /// fixed exposure, low-density volumetric fog for floodlight shafts, ACES, subtle bloom, SSAO, SSR, contact and micro
    /// shadows, shadow distance, indirect lighting controller, slight contrast grade and the Skin diffusion profile;</item>
    /// <item>a directional key light (indoor: daylight through the hall's windows/skylights, in lux);</item>
    /// <item>spot floodlights in lumen at 5600 K aimed at the court, the two main ones casting PCSS shadows with contact
    /// shadows, all scattering into the volumetric haze;</item>
    /// <item>a box-projected HDRP reflection probe covering the arena bounds and a Light Probe Group grid;</item>
    /// <item>scene LightingSettings for baking (only when the scene has none).</item>
    /// </list>
    /// Everything is created under a single child of the root, which is replaced when the builder runs again.
    /// </summary>
    public static class HdrpLightingBuilder
    {
        /// <summary>Name of the container created under the root.</summary>
        public const string ContainerName = "Lighting (HDRP)";

        /// <summary>File name of the saved volume profile inside the profile folder.</summary>
        public const string ProfileFileName = "DU_ArenaVolumeProfile.asset";

        /// <summary>File name of the lighting settings created when the scene has none.</summary>
        public const string LightingSettingsFileName = "DU_ArenaLightingSettings.lighting";

        /// <summary>True = realtime (rendered once on enable) reflection probe instead of a baked one.</summary>
        public static bool UseRealtimeReflectionProbe = false;

        /// <summary>Horizontal spacing (m) of the light probe grid.</summary>
        public static float LightProbeSpacing = 3f;

        /// <summary>Upper bound of generated light probes (spacing grows to respect it).</summary>
        public static int MaxLightProbes = 3000;

        // Defaults when the desc leaves a value at 0.
        private const float DefaultIndoorEV100 = 11.5f, DefaultOutdoorEV100 = 14f;
        private const float DefaultIndoorKeyLux = 2500f, DefaultOutdoorSunLux = 100000f;
        private const float DefaultKeyTemperature = 6000f;
        private const float DefaultFloodlightLumen = 90000f, DefaultFloodlightTemperature = 5600f;
        private static readonly Vector3 s_DefaultSunEuler = new Vector3(50f, -30f, 0f);
        private static readonly Bounds s_DefaultArenaBounds = new Bounds(new Vector3(0f, 7f, 0f), new Vector3(44f, 14f, 34f));

        /// <summary>The desc with every unset (0) value replaced by a physically plausible default.</summary>
        private readonly struct Resolved
        {
            public readonly bool Indoor;
            public readonly Bounds Bounds;
            public readonly float EV100;
            public readonly Vector3 KeyEuler;
            public readonly float KeyLux, KeyTemperature;
            public readonly float FloodLumen, FloodTemperature;
            public readonly Vector3[] FloodPositions;
            public readonly Vector3 AimPoint;
            public readonly Cubemap Hdri;

            public Resolved(in ArenaLightingDesc desc)
            {
                Indoor = desc.Indoor;
                Bounds = desc.ArenaBounds.size.sqrMagnitude > 1e-4f ? desc.ArenaBounds : s_DefaultArenaBounds;
                EV100 = desc.ExposureEV100 > 0f ? desc.ExposureEV100 : (Indoor ? DefaultIndoorEV100 : DefaultOutdoorEV100);
                KeyEuler = desc.SunEuler == Vector3.zero ? s_DefaultSunEuler : desc.SunEuler;
                KeyLux = desc.SunIlluminanceLux > 0f ? desc.SunIlluminanceLux : (Indoor ? DefaultIndoorKeyLux : DefaultOutdoorSunLux);
                KeyTemperature = desc.SunTemperatureK > 0f ? desc.SunTemperatureK : DefaultKeyTemperature;
                FloodLumen = desc.FloodlightLumen > 0f ? desc.FloodlightLumen : DefaultFloodlightLumen;
                FloodTemperature = desc.FloodlightTemperatureK > 0f ? desc.FloodlightTemperatureK : DefaultFloodlightTemperature;
                FloodPositions = desc.FloodlightPositions ?? Array.Empty<Vector3>();
                AimPoint = desc.FloodlightAimPoint;
                Hdri = desc.Hdri;
            }
        }

        /// <summary>Implements <see cref="IEditorRenderingHooks.BuildLighting"/> for HDRP. Returns the created container.</summary>
        public static GameObject Build(Transform root, in ArenaLightingDesc desc, string profileFolder)
        {
            if (root == null)
            {
                Debug.LogError("[DU HDRP] BuildLighting needs a root transform.");
                return null;
            }

            var r = new Resolved(desc);
            if (string.IsNullOrEmpty(profileFolder)) profileFolder = HdrpProjectConfigurator.GeneratedRenderingFolder;
            HdrpEditorAssetUtility.EnsureFolder(profileFolder);

            // Replace a previous build.
            Transform previous = root.Find(ContainerName);
            if (previous != null) Undo.DestroyObjectImmediate(previous.gameObject);

            var container = new GameObject(ContainerName);
            Undo.RegisterCreatedObjectUndo(container, "Build HDRP Lighting");
            container.transform.SetParent(root, false);

            VolumeProfile profile = BuildVolumeProfile(profileFolder.TrimEnd('/') + "/" + ProfileFileName, r);
            CreateGlobalVolume(container.transform, profile);
            CreateKeyLight(container.transform, r);
            CreateFloodlights(container.transform, r);
            CreateReflectionProbe(container.transform, r.Bounds);
            CreateLightProbeGrid(container.transform, r.Bounds);
            EnsureLightingSettings(profileFolder.TrimEnd('/') + "/" + LightingSettingsFileName);

            AssetDatabase.SaveAssets();
            if (root.gameObject.scene.IsValid()) EditorSceneManager.MarkSceneDirty(root.gameObject.scene);
            return container;
        }

        // ------------------------------------------------------------------------------------------------ Volume

        private static VolumeProfile BuildVolumeProfile(string path, in Resolved r)
        {
            VolumeProfile profile = LoadOrResetProfile(path);

            // Sky: HDRI when supplied (image-based ambient), otherwise a physically based atmosphere driven by the key
            // light, so ambient and reflections stay proportional to the daylight entering the hall.
            var environment = HdrpVolumeUtility.GetOrAdd<VisualEnvironment>(profile);
            HdrpVolumeUtility.Set(environment.skyType, r.Hdri != null ? (int)SkyType.HDRI : (int)SkyType.PhysicallyBased);
            HdrpVolumeUtility.Set(environment.skyAmbientMode, SkyAmbientMode.Dynamic);

            if (r.Hdri != null)
            {
                var hdri = HdrpVolumeUtility.GetOrAdd<HDRISky>(profile);
                HdrpVolumeUtility.Set(hdri.hdriSky, (Texture)r.Hdri);
                // Calibrate the HDRI physically: HDRP integrates its upper hemisphere and scales it to this illuminance.
                HdrpVolumeUtility.Set(hdri.skyIntensityMode, SkyIntensityMode.Lux);
                HdrpVolumeUtility.Set(hdri.desiredLuxValue, r.KeyLux * (r.Indoor ? 0.3f : 0.2f));
                HdrpVolumeUtility.Set(hdri.updateMode, EnvironmentUpdateMode.OnChanged);
            }
            else
            {
                var sky = HdrpVolumeUtility.GetOrAdd<PhysicallyBasedSky>(profile);
                HdrpVolumeUtility.Set(sky.groundTint, new Color(0.12f, 0.1f, 0.09f));
                HdrpVolumeUtility.Set(sky.updateMode, EnvironmentUpdateMode.OnChanged);
            }

            var exposure = HdrpVolumeUtility.GetOrAdd<Exposure>(profile);
            HdrpVolumeUtility.Set(exposure.mode, ExposureMode.Fixed);
            HdrpVolumeUtility.Set(exposure.fixedExposure, r.EV100);

            // Low-density haze: visible shafts under the floodlights without greying out the court.
            Bounds b = r.Bounds;
            float diagonal = b.size.magnitude;
            var fog = HdrpVolumeUtility.GetOrAdd<Fog>(profile);
            HdrpVolumeUtility.Set(fog.enabled, true);
            HdrpVolumeUtility.Set(fog.meanFreePath, r.Indoor ? 90f : 400f);
            HdrpVolumeUtility.Set(fog.baseHeight, b.min.y);
            HdrpVolumeUtility.Set(fog.maximumHeight, r.Indoor ? b.max.y : b.max.y + 50f);
            HdrpVolumeUtility.Set(fog.maxFogDistance, Mathf.Max(200f, diagonal * 2f));
            HdrpVolumeUtility.Set(fog.enableVolumetricFog, true);
            HdrpVolumeUtility.Set(fog.albedo, new Color(0.95f, 0.93f, 0.9f));
            HdrpVolumeUtility.Set(fog.anisotropy, r.Indoor ? 0.65f : 0.5f);
            HdrpVolumeUtility.Set(fog.globalLightProbeDimmer, r.Indoor ? 0.3f : 1f);
            HdrpVolumeUtility.Set(fog.depthExtent, Mathf.Clamp(diagonal, 32f, 128f));
            HdrpVolumeUtility.Set(fog.quality, (int)ScalableSettingLevelParameter.Level.High);

            HdrpVolumeUtility.Set(HdrpVolumeUtility.GetOrAdd<Tonemapping>(profile).mode, TonemappingMode.ACES);

            var bloom = HdrpVolumeUtility.GetOrAdd<Bloom>(profile);
            HdrpVolumeUtility.Set(bloom.intensity, 0.12f);
            HdrpVolumeUtility.Set(bloom.scatter, 0.65f);
            HdrpVolumeUtility.Set(bloom.threshold, 0.8f);
            HdrpVolumeUtility.Set(bloom.anamorphic, false);
            HdrpVolumeUtility.Set(bloom.quality, (int)ScalableSettingLevelParameter.Level.High);

            var ao = HdrpVolumeUtility.GetOrAdd<ScreenSpaceAmbientOcclusion>(profile);
            HdrpVolumeUtility.Set(ao.intensity, 0.85f);
            HdrpVolumeUtility.Set(ao.radius, 1.5f);
            HdrpVolumeUtility.Set(ao.directLightingStrength, 0.2f);
            HdrpVolumeUtility.Set(ao.quality, (int)ScalableSettingLevelParameter.Level.High);

            var ssr = HdrpVolumeUtility.GetOrAdd<ScreenSpaceReflection>(profile);
            HdrpVolumeUtility.Set(ssr.enabled, true);
            HdrpVolumeUtility.Set(ssr.quality, (int)ScalableSettingLevelParameter.Level.Medium);

            var contact = HdrpVolumeUtility.GetOrAdd<ContactShadows>(profile);
            HdrpVolumeUtility.Set(contact.enable, true);
            HdrpVolumeUtility.Set(contact.length, 0.25f);
            HdrpVolumeUtility.Set(contact.opacity, 1f);
            HdrpVolumeUtility.Set(contact.distanceScaleFactor, 0.5f);
            HdrpVolumeUtility.Set(contact.maxDistance, 40f);
            HdrpVolumeUtility.Set(contact.fadeDistance, 5f);
            HdrpVolumeUtility.Set(contact.quality, (int)ScalableSettingLevelParameter.Level.High);

            var micro = HdrpVolumeUtility.GetOrAdd<MicroShadowing>(profile);
            HdrpVolumeUtility.Set(micro.enable, true);
            HdrpVolumeUtility.Set(micro.opacity, 0.8f);

            var shadows = HdrpVolumeUtility.GetOrAdd<HDShadowSettings>(profile);
            HdrpVolumeUtility.Set(shadows.maxShadowDistance, r.Indoor ? 80f : 150f);
            HdrpVolumeUtility.Set(shadows.cascadeShadowSplitCount, 4);

            var indirect = HdrpVolumeUtility.GetOrAdd<IndirectLightingController>(profile);
            HdrpVolumeUtility.Set(indirect.indirectDiffuseLightingMultiplier, 1f);
            HdrpVolumeUtility.Set(indirect.reflectionLightingMultiplier, 1f);

            var grade = HdrpVolumeUtility.GetOrAdd<ColorAdjustments>(profile);
            HdrpVolumeUtility.Set(grade.contrast, 8f);
            HdrpVolumeUtility.Set(grade.saturation, 3f);

            // Register the Skin profile in this scene too, so SSS character materials render correctly even if the
            // project's global default volume profile was customised.
            DiffusionProfileSettings skin = HdrpMaterialBuilder.LoadSkinDiffusionProfile();
            if (skin != null)
                HdrpVolumeUtility.Set(HdrpVolumeUtility.GetOrAdd<DiffusionProfileList>(profile).diffusionProfiles, new[] { skin });

            PersistProfileComponents(profile);
            return profile;
        }

        /// <summary>Loads the profile at <paramref name="path"/> with its components removed (same GUID), or creates it.</summary>
        private static VolumeProfile LoadOrResetProfile(string path)
        {
            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(path);
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<VolumeProfile>();
                AssetDatabase.CreateAsset(profile, path);
                return profile;
            }

            // Components are sub-assets: destroy every one (also orphans not listed any more), then clear the list.
            foreach (Object subAsset in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (subAsset is VolumeComponent component) Object.DestroyImmediate(component, true);
            }
            profile.components.Clear();
            EditorUtility.SetDirty(profile);
            return profile;
        }

        /// <summary>Stores newly added components as sub-assets of the profile asset (required for them to be saved).</summary>
        private static void PersistProfileComponents(VolumeProfile profile)
        {
            for (int i = 0; i < profile.components.Count; i++)
            {
                VolumeComponent component = profile.components[i];
                if (component == null || AssetDatabase.Contains(component)) continue;
                component.hideFlags = HideFlags.HideInInspector | HideFlags.HideInHierarchy;
                AssetDatabase.AddObjectToAsset(component, profile);
            }
            EditorUtility.SetDirty(profile);
        }

        private static void CreateGlobalVolume(Transform parent, VolumeProfile profile)
        {
            var go = new GameObject("Global Volume (Arena)");
            go.transform.SetParent(parent, false);
            var volume = go.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 0f;
            volume.weight = 1f;
            volume.sharedProfile = profile;
        }

        // ------------------------------------------------------------------------------------------------ Lights

        private static void CreateKeyLight(Transform parent, in Resolved r)
        {
            var go = new GameObject(r.Indoor ? "Key Light (Window Daylight)" : "Sun");
            go.transform.SetParent(parent, false);
            go.transform.rotation = Quaternion.Euler(r.KeyEuler);

            var light = go.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = Color.white;
#if !DU_CC_PLAYER_REFS // editor-only engine member (see Tools/CompileCheck/EditorOnlyApis)
            light.lightmapBakeType = LightmapBakeType.Mixed; // realtime direct + baked bounce
#endif
            HdrpLightUtility.ConfigurePhysicalLight(light, r.KeyLux, r.KeyTemperature);
            HdrpLightUtility.EnableShadows(light, 3, true);

            HDAdditionalLightData data = HdrpLightUtility.GetOrAddLightData(light);
            data.interactsWithSky = true;          // drives the physically based sky
            data.volumetricDimmer = r.Indoor ? 1.5f : 1f; // indoor: emphasise the shafts through the windows
        }

        private static void CreateFloodlights(Transform parent, in Resolved r)
        {
            Vector3[] positions = r.FloodPositions;
            if (positions.Length == 0) return;

            FindMainFloodlights(positions, r.AimPoint, out int mainA, out int mainB);

            // Beam width: cover the court region around the aim point (not the whole stands).
            float coverRadius = Mathf.Clamp(0.3f * Mathf.Max(r.Bounds.size.x, r.Bounds.size.z), 6f, 18f);

            for (int i = 0; i < positions.Length; i++)
            {
                bool main = i == mainA || i == mainB;
                Vector3 position = positions[i];
                Vector3 toAim = r.AimPoint - position;
                float distance = Mathf.Max(1f, toAim.magnitude);

                var go = new GameObject(main ? $"Floodlight {i:00} (Shadows)" : $"Floodlight {i:00}");
                go.transform.SetParent(parent, false);
                go.transform.position = position;
                go.transform.rotation = Quaternion.LookRotation(toAim.sqrMagnitude > 1e-6f ? toAim : Vector3.down,
                    Mathf.Abs(Vector3.Dot(toAim.normalized, Vector3.up)) > 0.99f ? Vector3.forward : Vector3.up);

                var light = go.AddComponent<Light>();
                light.type = LightType.Spot;
                light.color = Color.white;
                light.spotAngle = Mathf.Clamp(2f * Mathf.Atan(coverRadius / distance) * Mathf.Rad2Deg, 25f, 110f);
                light.innerSpotAngle = light.spotAngle * 0.45f;
                light.range = Mathf.Max(30f, distance * 3f);
#if !DU_CC_PLAYER_REFS // editor-only engine member (see Tools/CompileCheck/EditorOnlyApis)
                light.lightmapBakeType = LightmapBakeType.Mixed;
#endif
                light.shadows = LightShadows.None;

                // Spot angle is set first: the lumen -> candela conversion depends on the cone.
                HdrpLightUtility.ConfigurePhysicalLight(light, r.FloodLumen, r.FloodTemperature);

                HDAdditionalLightData data = HdrpLightUtility.GetOrAddLightData(light);
                data.innerSpotPercent = 45f;
                data.volumetricDimmer = main ? 0.8f : 0.4f; // unshadowed beams would leak through geometry: keep them dim
                if (main) HdrpLightUtility.EnableShadows(light, 3, true);
            }
        }

        /// <summary>
        /// The two "main" floodlights: the one closest to the aim point, and the one most opposite to it around the aim
        /// point (cross-lighting gives readable double shadows, like broadcast arena rigs).
        /// </summary>
        private static void FindMainFloodlights(Vector3[] positions, Vector3 aim, out int first, out int second)
        {
            first = 0;
            float best = float.MaxValue;
            for (int i = 0; i < positions.Length; i++)
            {
                float d = (positions[i] - aim).sqrMagnitude;
                if (d < best) { best = d; first = i; }
            }

            second = -1;
            if (positions.Length < 2) return;
            Vector3 firstDir = Horizontal(positions[first] - aim);
            float mostOpposite = float.MaxValue;
            for (int i = 0; i < positions.Length; i++)
            {
                if (i == first) continue;
                float dot = Vector3.Dot(firstDir, Horizontal(positions[i] - aim));
                if (dot < mostOpposite) { mostOpposite = dot; second = i; }
            }
        }

        private static Vector3 Horizontal(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 1e-6f ? v.normalized : Vector3.forward;
        }

        // ------------------------------------------------------------------------------------------------ Probes

        private static void CreateReflectionProbe(Transform parent, Bounds bounds)
        {
            var go = new GameObject("Reflection Probe (Arena)");
            go.transform.SetParent(parent, false);
            go.transform.position = bounds.center;

            // Legacy component (required by HDRP) kept consistent for tools that read it.
            var probe = go.AddComponent<ReflectionProbe>();
            probe.mode = UseRealtimeReflectionProbe ? UnityEngine.Rendering.ReflectionProbeMode.Realtime : UnityEngine.Rendering.ReflectionProbeMode.Baked;
            probe.size = bounds.size;
            probe.center = Vector3.zero;
            probe.boxProjection = true;

            // HDRP-side data: box influence = arena bounds, used as the parallax proxy (box projection), 1 m blend.
            var data = go.AddComponent<HDAdditionalReflectionData>();
            data.mode = UseRealtimeReflectionProbe ? ProbeSettings.Mode.Realtime : ProbeSettings.Mode.Baked;
            if (UseRealtimeReflectionProbe)
            {
                data.realtimeMode = ProbeSettings.RealtimeMode.OnEnable;
                data.timeSlicing = true;
            }

            InfluenceVolume influence = data.influenceVolume;
            influence.shape = InfluenceShape.Box;
            influence.boxSize = bounds.size;
            influence.boxBlendDistancePositive = Vector3.one;
            influence.boxBlendDistanceNegative = Vector3.one;
            data.multiplier = 1f;
            data.weight = 1f;
        }

        private static void CreateLightProbeGrid(Transform parent, Bounds bounds)
        {
            var go = new GameObject("Light Probe Grid");
            go.transform.SetParent(parent, false);
            go.transform.position = Vector3.zero;
            go.transform.rotation = Quaternion.identity;

            List<Vector3> world = BuildLightProbePositions(bounds);
            var local = new Vector3[world.Count];
            for (int i = 0; i < local.Length; i++) local[i] = go.transform.InverseTransformPoint(world[i]);

            var group = go.AddComponent<LightProbeGroup>();
#if !DU_CC_PLAYER_REFS // editor-only engine member (see Tools/CompileCheck/EditorOnlyApis)
            group.probePositions = local;
#endif
        }

        /// <summary>
        /// Grid over the arena: dense layers at player heights (ankle, torso, head) where dynamic characters sample GI,
        /// then every 3 m up to just below the roof for balls in flight and the stands.
        /// </summary>
        private static List<Vector3> BuildLightProbePositions(Bounds bounds)
        {
            var heights = new List<float> { 0.3f, 1.2f, 2.2f };
            float top = bounds.size.y - 0.5f;
            for (float h = 5f; h < top; h += 3f) heights.Add(h);

            const float inset = 0.25f;
            float sizeX = Mathf.Max(0f, bounds.size.x - 2f * inset);
            float sizeZ = Mathf.Max(0f, bounds.size.z - 2f * inset);

            float spacing = Mathf.Max(0.5f, LightProbeSpacing);
            int nx, nz;
            while (true)
            {
                nx = Mathf.Max(2, Mathf.CeilToInt(sizeX / spacing) + 1);
                nz = Mathf.Max(2, Mathf.CeilToInt(sizeZ / spacing) + 1);
                if (nx * nz * heights.Count <= Mathf.Max(8, MaxLightProbes)) break;
                spacing *= 1.25f;
            }

            var positions = new List<Vector3>(nx * nz * heights.Count);
            Vector3 min = bounds.min + new Vector3(inset, 0f, inset);
            for (int h = 0; h < heights.Count; h++)
            {
                float y = bounds.min.y + Mathf.Min(heights[h], Mathf.Max(0.3f, top));
                for (int ix = 0; ix < nx; ix++)
                {
                    float x = min.x + sizeX * ix / (nx - 1);
                    for (int iz = 0; iz < nz; iz++)
                    {
                        float z = min.z + sizeZ * iz / (nz - 1);
                        positions.Add(new Vector3(x, y, z));
                    }
                }
            }
            return positions;
        }

        // ------------------------------------------------------------------------------------------------ Baking

        /// <summary>
        /// Gives the scene bake settings suited to the arena (baked indirect with realtime direct lighting from the Mixed
        /// lights, directional lightmaps, GPU progressive lightmapper) - only when the scene has none, so settings chosen
        /// by the scene builder or the user are never overwritten.
        /// </summary>
        private static void EnsureLightingSettings(string path)
        {
            if (Lightmapping.TryGetLightingSettings(out LightingSettings current) && current != null) return;

            var settings = AssetDatabase.LoadAssetAtPath<LightingSettings>(path);
            if (settings == null)
            {
                settings = new LightingSettings
                {
                    name = "DU_ArenaLightingSettings",
                    bakedGI = true,
                    realtimeGI = false,
#if !DU_CC_PLAYER_REFS // editor-only engine members (see Tools/CompileCheck/EditorOnlyApis)
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
#endif
                };
                AssetDatabase.CreateAsset(settings, path);
            }
            Lightmapping.lightingSettings = settings;
        }
    }
}
