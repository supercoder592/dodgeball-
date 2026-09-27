using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace DodgeballUltra.Editor.Pipeline
{
    /// <summary>
    /// Built-in Render Pipeline fallback of <see cref="IEditorRenderingHooks"/> (used when the HDRP adapter assembly is not
    /// loaded, e.g. the HDRP package was removed). Produces a physically plausible look with the Standard shader:
    /// <list type="bullet">
    /// <item>Materials: albedo, tangent normal map, metallic (R) + smoothness (A) and occlusion (G) from the HDRP-layout mask.</item>
    /// <item>Lighting: spot floodlights auto-exposed for the court (the Built-in pipeline has no physical exposure), a soft
    /// directional fill / sun, procedural or HDRI sky, tri-light indoor ambient, box-projected reflection probes and
    /// exponential-squared haze.</item>
    /// <item>Camera: deferred shading (many floodlights), HDR, occlusion culling.</item>
    /// </list>
    /// Registered as the fallback from this [InitializeOnLoad] static constructor.
    /// </summary>
    [InitializeOnLoad]
    public sealed class BuiltInEditorRenderingHooks : IEditorRenderingHooks
    {
        // ------------------------------------------------------------------ tuning

        /// <summary>Illuminance (lux) that maps to Built-in intensity 1 for directional lights (noon sun 100 klx ≈ 1.25).</summary>
        public const float DirectionalLuxPerIntensity = 80000f;

        /// <summary>Luminous flux (lm) that maps to Built-in intensity 1 for spot/point lights (stateless conversion).</summary>
        public const float PunctualLumenPerIntensity = 10000f;

        /// <summary>Emissive luminance (nits) that maps to an emission multiplier of 1 in the Standard shader.</summary>
        public const float NitsPerEmissionUnit = 5000f;

        /// <summary>Linear brightness the floodlights should produce at the court centre (a white floor would read ~1.0).</summary>
        public const float CourtTargetBrightness = 1.0f;

        /// <summary>At most this many floodlights cast (soft) shadows - shadowed spots are expensive in the Built-in pipeline.</summary>
        public const int MaxShadowedFloodlights = 8;

        private const string StandardShaderName = "Standard";

        static BuiltInEditorRenderingHooks()
        {
            EditorRenderingHooks.RegisterFallback(new BuiltInEditorRenderingHooks());
        }

        // ------------------------------------------------------------------ IEditorRenderingHooks

        public string PipelineName => "Built-in Render Pipeline (Standard shader)";

        /// <summary>Active when no scriptable render pipeline asset is assigned.</summary>
        public bool IsActive => GraphicsSettings.currentRenderPipeline == null;

        /// <summary>Only switches the project to linear colour space (required for physically based lighting).</summary>
        public void ConfigureProject()
        {
            if (PlayerSettings.colorSpace != ColorSpace.Linear)
            {
                PlayerSettings.colorSpace = ColorSpace.Linear;
                Debug.Log("[Dodgeball Ultra] Colour space set to Linear (physically based lighting).");
            }

            if (GraphicsSettings.currentRenderPipeline != null)
            {
                Debug.LogWarning("[Dodgeball Ultra] A scriptable render pipeline asset (" + GraphicsSettings.currentRenderPipeline.name +
                                 ") is assigned but no editor hooks for it are loaded. Built-in Standard materials will not render " +
                                 "correctly with it. Install the HDRP package (com.unity.render-pipelines.high-definition) so the " +
                                 "HDRP hooks can configure the project.");
            }
        }

        public Material CreateCharacterMaterial(string assetPath, CharacterMaterialKind kind, Texture2D baseColor, Texture2D normalMap,
            Texture2D maskMap, Texture2D opacityMap)
        {
            Material m = LoadOrCreateMaterial(assetPath);

            // Hair / lashes need coverage in the albedo alpha (Standard samples alpha from _MainTex only). When the opacity
            // mask ships as a separate texture (Rocketbox "opacity" maps), bake it into a combined RGBA texture next to the material.
            Texture2D albedo = baseColor;
            bool cutout = kind == CharacterMaterialKind.Hair;
            const float hairCutoff = 0.35f; // low threshold keeps thin strands; coverage-preserving mips avoid thinning at distance
            if (cutout && opacityMap != null && opacityMap != baseColor && baseColor != null)
            {
                string combinedPath = Path.ChangeExtension(assetPath, null) + "_BaseOpacity.png";
                albedo = CombineAlbedoAndOpacity(baseColor, opacityMap, combinedPath, hairCutoff) ?? baseColor;
            }
            else if (cutout && baseColor == null && opacityMap != null)
            {
                albedo = opacityMap;
            }

            m.SetColor("_Color", Color.white);
            m.SetTexture("_MainTex", albedo);
            m.SetTextureScale("_MainTex", Vector2.one);

            SetNormalMap(m, normalMap, 1f);

            // Plausible defaults when no mask is supplied: skin is moderately glossy (sebum), fabric is rough, hair has a sheen.
            float smoothness;
            switch (kind)
            {
                case CharacterMaterialKind.Skin: smoothness = 0.48f; break;
                case CharacterMaterialKind.Body: smoothness = 0.35f; break;
                case CharacterMaterialKind.Hair: smoothness = 0.42f; break;
                default: smoothness = 0.4f; break;
            }
            SetMask(m, maskMap, smoothness, 0f);
            SetEmission(m, Color.black, 0f);
            SetBlendMode(m, cutout ? StandardBlendMode.Cutout : StandardBlendMode.Opaque, hairCutoff);

            m.enableInstancing = true;
            EditorUtility.SetDirty(m);
            return m;
        }

        public Material CreateEnvironmentMaterial(string assetPath, in EnvironmentMaterialDesc desc)
        {
            Material m = LoadOrCreateMaterial(assetPath);

            Color baseColor = desc.BaseColor;
            if (baseColor.maxColorComponent <= 0f && baseColor.a <= 0f) baseColor = Color.white; // default(Color) guard
            m.SetColor("_Color", baseColor);
            m.SetTexture("_MainTex", desc.BaseMap);

            // The Standard shader applies _MainTex_ST to albedo, normal, metallic and occlusion maps alike.
            Vector2 tiling = desc.Tiling.sqrMagnitude > 0f ? desc.Tiling : Vector2.one;
            m.SetTextureScale("_MainTex", tiling);
            m.SetTextureOffset("_MainTex", Vector2.zero);

            SetNormalMap(m, desc.NormalMap, desc.NormalStrength > 0f ? desc.NormalStrength : 1f);
            SetMask(m, desc.MaskMap, Mathf.Clamp01(desc.Smoothness), Mathf.Clamp01(desc.Metallic));

            float emissionMultiplier = desc.EmissiveIntensity > 50f
                ? Mathf.Clamp(desc.EmissiveIntensity / NitsPerEmissionUnit, 1f, 16f) // HDRP nits -> Standard multiplier
                : desc.EmissiveIntensity;
            SetEmission(m, desc.EmissiveColor, emissionMultiplier);

            // Standard has no double-sided option (always back-face culled): DoubleSided is ignored in this fallback.
            SetBlendMode(m, desc.Transparent ? StandardBlendMode.Transparent : StandardBlendMode.Opaque, 0.5f);

            m.enableInstancing = true;
            EditorUtility.SetDirty(m);
            return m;
        }

        public void BuildLighting(Transform root, in ArenaLightingDesc desc, string profileFolder)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));
            if (!string.IsNullOrEmpty(profileFolder)) TextureAssetUtility.EnsureAssetFolder(profileFolder);

            Bounds bounds = desc.ArenaBounds.size.sqrMagnitude > 1f
                ? desc.ArenaBounds
                : new Bounds(new Vector3(0f, 6f, 0f), new Vector3(26f, 12f, 34f));

            float floodKelvin = desc.FloodlightTemperatureK > 1000f ? desc.FloodlightTemperatureK : 5600f;
            float floodLumen = desc.FloodlightLumen > 0f ? desc.FloodlightLumen : 20000f;
            Vector3 aimPoint = desc.FloodlightAimPoint;

            // ---------------------------------------------------------------- sky
            RenderSettings.skybox = CreateSkyboxMaterial(profileFolder, desc.Hdri);
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Skybox;
            RenderSettings.reflectionIntensity = 1f;

            // ---------------------------------------------------------------- floodlights (auto-exposed)
            Vector3[] positions = desc.FloodlightPositions != null && desc.FloodlightPositions.Length > 0
                ? desc.FloodlightPositions
                : DefaultFloodlightPositions(bounds);

            Transform floodRoot = CreateChild(root, "Floodlights");
            var lights = new Light[positions.Length];
            var physicalLux = new float[positions.Length];
            var builtInResponse = new float[positions.Length];
            float courtRadius = Mathf.Max(6f, Mathf.Min(bounds.extents.x, bounds.extents.z) * 0.9f);
            int shadowStride = Mathf.Max(1, Mathf.CeilToInt(positions.Length / (float)MaxShadowedFloodlights));
            float totalLux = 0f;

            for (int i = 0; i < positions.Length; i++)
            {
                Vector3 p = positions[i];

                // Spread the aim a little toward each light's own side of the court (real rigs overlap their beams for
                // uniform vertical illuminance instead of all hitting the centre spot).
                Vector3 toLight = new Vector3(p.x - aimPoint.x, 0f, p.z - aimPoint.z);
                Vector3 aim = aimPoint + Vector3.ClampMagnitude(toLight * 0.3f, courtRadius * 0.5f);
                Vector3 dir = aim - p;
                float distance = Mathf.Max(1f, dir.magnitude);
                dir /= distance;

                var go = new GameObject("Floodlight_" + i.ToString("00"));
                go.transform.SetParent(floodRoot, false);
                go.transform.position = p;
                go.transform.rotation = Quaternion.LookRotation(dir, Mathf.Abs(dir.y) > 0.99f ? Vector3.forward : Vector3.up);

                var light = go.AddComponent<Light>();
                light.type = LightType.Spot;
                // Cone wide enough that every light covers the court from where it hangs.
                float spot = Mathf.Clamp(2f * Mathf.Atan(courtRadius / distance) * Mathf.Rad2Deg, 30f, 120f);
                light.spotAngle = spot;
                light.innerSpotAngle = spot * 0.6f;
                light.range = distance * 1.9f;
                light.bounceIntensity = 1f;
                bool shadowed = i % shadowStride == 0;
                light.shadows = shadowed ? LightShadows.Soft : LightShadows.None;
                light.shadowStrength = 0.85f;
                light.shadowBias = 0.03f;
                light.shadowNormalBias = 0.3f;
                ConfigureLight(light, floodLumen, floodKelvin); // colour temperature (+ stateless intensity, replaced below)
                lights[i] = light;

                // Physical illuminance this light delivers at the court centre: E = I / d^2 * cos(theta), with the
                // luminous intensity of a reflector spot I = flux / solid angle of its cone.
                Vector3 toCentre = aimPoint - p;
                float dCentre = Mathf.Max(1f, toCentre.magnitude);
                float cosIncidence = Mathf.Clamp01(-toCentre.normalized.y);
                float solidAngle = 2f * Mathf.PI * (1f - Mathf.Cos(spot * 0.5f * Mathf.Deg2Rad));
                float candela = floodLumen / Mathf.Max(0.05f, solidAngle);
                float offAxis = Vector3.Angle(dir, toCentre);
                float coneFactor = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(light.innerSpotAngle * 0.5f, spot * 0.5f, offAxis));
                physicalLux[i] = candela / (dCentre * dCentre) * cosIncidence * coneFactor;
                totalLux += physicalLux[i];

                // What a Built-in light of intensity 1 contributes at the same point (legacy range attenuation).
                builtInResponse[i] = LegacyAttenuation(dCentre, light.range) * cosIncidence * coneFactor;
            }

            // Scale so the summed contribution at the court centre reads CourtTargetBrightness, while every light keeps its
            // physically correct share (brighter/closer lights stay brighter).
            float k = totalLux > 0.001f ? CourtTargetBrightness / totalLux : 0f;
            for (int i = 0; i < lights.Length; i++)
            {
                if (builtInResponse[i] <= 1e-4f)
                {
                    lights[i].intensity = Mathf.Clamp(floodLumen / PunctualLumenPerIntensity, 0f, 8f);
                    continue;
                }
                float share = physicalLux[i] * k;                        // desired linear brightness from this light
                lights[i].intensity = Mathf.Clamp(share / builtInResponse[i], 0f, 8f);
            }

            // ---------------------------------------------------------------- sun / skylight fill
            var sunGo = new GameObject(desc.Indoor ? "Skylight Fill (Directional)" : "Sun");
            sunGo.transform.SetParent(root, false);
            Vector3 sunEuler = desc.SunEuler.sqrMagnitude > 0f ? desc.SunEuler : new Vector3(50f, -30f, 0f);
            sunGo.transform.rotation = Quaternion.Euler(sunEuler);
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            float sunKelvin = desc.SunTemperatureK > 1000f ? desc.SunTemperatureK : 6000f;
            ConfigureLight(sun, Mathf.Max(0f, desc.SunIlluminanceLux), sunKelvin);
            if (desc.Indoor)
            {
                // Indoors the roof would shadow a real sun completely: use it as an unshadowed soft fill (light bounced off
                // the roof and walls, or daylight through clerestory windows) whose strength follows its physical share.
                float share = desc.SunIlluminanceLux / Mathf.Max(1f, desc.SunIlluminanceLux + totalLux);
                sun.intensity = Mathf.Clamp(share * CourtTargetBrightness * 0.6f, 0.04f, 0.35f);
                sun.shadows = LightShadows.None;
            }
            else
            {
                sun.shadows = LightShadows.Soft;
                sun.shadowStrength = 1f;
                sun.shadowBias = 0.02f;
                sun.shadowNormalBias = 0.4f;
            }
            RenderSettings.sun = sun;

            // ---------------------------------------------------------------- ambient
            Color floodTint = NormalizedTint(Mathf.CorrelatedColorTemperatureToRGB(floodKelvin));
            if (desc.Indoor)
            {
                // Tri-light approximates the diffuse bounce inside a closed hall (ceiling, walls, lacquered floor) until
                // the user bakes GI. Values are ~30 % of the direct court brightness.
                RenderSettings.ambientMode = AmbientMode.Trilight;
                RenderSettings.ambientSkyColor = floodTint * 0.28f;
                RenderSettings.ambientEquatorColor = floodTint * 0.2f;
                RenderSettings.ambientGroundColor = new Color(0.14f, 0.1f, 0.07f); // warm bounce from the maple floor
                RenderSettings.ambientIntensity = 1f;
            }
            else
            {
                RenderSettings.ambientMode = AmbientMode.Skybox;
                RenderSettings.ambientIntensity = 1f;
            }

            // ---------------------------------------------------------------- reflection probes
            Transform probeRoot = CreateChild(root, "Reflection Probes");
            CreateReflectionProbe(probeRoot, "Arena Reflection Probe", bounds.center, bounds.size + Vector3.one, 0, 256);
            var courtCentre = new Vector3(aimPoint.x, bounds.min.y + 2.5f, aimPoint.z);
            var courtSize = new Vector3(bounds.size.x * 0.75f, 5f, bounds.size.z * 0.75f);
            CreateReflectionProbe(probeRoot, "Court Reflection Probe", courtCentre, courtSize, 1, 256);

            // ---------------------------------------------------------------- haze
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogDensity = desc.Indoor ? 0.01f : 0.004f;
            RenderSettings.fogColor = desc.Indoor
                ? new Color(0.36f, 0.35f, 0.33f) * floodTint
                : new Color(0.62f, 0.68f, 0.76f);

            DynamicGI.UpdateEnvironment();
            if (root.gameObject.scene.IsValid()) EditorSceneManager.MarkSceneDirty(root.gameObject.scene);
        }

        public void ConfigureCamera(Camera camera)
        {
            if (camera == null) return;
            camera.renderingPath = RenderingPath.DeferredShading; // dozens of floodlights without per-light forward passes
            camera.allowHDR = true;
            camera.allowMSAA = false;                             // not supported with deferred; use post AA if available
            camera.useOcclusionCulling = true;
            camera.clearFlags = CameraClearFlags.Skybox;
            if (camera.nearClipPlane > 0.1f) camera.nearClipPlane = 0.05f;
            if (camera.farClipPlane < 300f) camera.farClipPlane = 500f;
            EditorUtility.SetDirty(camera);
        }

        public void ConfigureLight(Light light, float physicalIntensity, float temperatureKelvin)
        {
            if (light == null) return;

            if (light.type == LightType.Directional)
            {
                light.intensity = Mathf.Clamp(physicalIntensity / DirectionalLuxPerIntensity, 0f, 8f);
            }
            else
            {
                light.intensity = Mathf.Clamp(physicalIntensity / PunctualLumenPerIntensity, 0f, 8f);
                // Legacy lights stop at their range: make sure brighter sources reach far enough.
                float reach = Mathf.Clamp(Mathf.Sqrt(Mathf.Max(0f, physicalIntensity)) * 0.2f, 5f, 80f);
                if (light.range < reach) light.range = reach;
            }

            float kelvin = Mathf.Clamp(temperatureKelvin > 0f ? temperatureKelvin : 6500f, 1500f, 20000f);
            if (GraphicsSettings.lightsUseLinearIntensity && GraphicsSettings.lightsUseColorTemperature)
            {
                light.color = Color.white;
                light.useColorTemperature = true;
                light.colorTemperature = kelvin;
            }
            else
            {
                light.useColorTemperature = false;
                light.color = NormalizedTint(Mathf.CorrelatedColorTemperatureToRGB(kelvin));
            }
            EditorUtility.SetDirty(light);
        }

        // ------------------------------------------------------------------ materials

        private enum StandardBlendMode
        {
            Opaque = 0,
            Cutout = 1,
            Fade = 2,
            Transparent = 3,
        }

        private static Shader FindStandardShader()
        {
            Shader shader = Shader.Find(StandardShaderName);
            if (shader == null) throw new InvalidOperationException("The Built-in 'Standard' shader was not found.");
            return shader;
        }

        /// <summary>Loads the material at <paramref name="assetPath"/> (keeping its GUID) or creates it.</summary>
        private static Material LoadOrCreateMaterial(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) throw new ArgumentException("Material asset path is empty.", nameof(assetPath));
            Shader shader = FindStandardShader();
            TextureAssetUtility.EnsureAssetFolder(Path.GetDirectoryName(assetPath)?.Replace('\\', '/'));

            var existing = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
            if (existing != null)
            {
                if (existing.shader != shader) existing.shader = shader;
                existing.shaderKeywords = Array.Empty<string>(); // keywords are rebuilt from the properties below
                return existing;
            }

            var material = new Material(shader) { name = Path.GetFileNameWithoutExtension(assetPath) };
            AssetDatabase.CreateAsset(material, assetPath);
            return material;
        }

        private static void SetNormalMap(Material m, Texture2D normalMap, float strength)
        {
            m.SetTexture("_BumpMap", normalMap);
            m.SetFloat("_BumpScale", strength);
            SetKeyword(m, "_NORMALMAP", normalMap != null);
        }

        /// <summary>HDRP mask layout (R metallic, G occlusion, A smoothness) maps directly onto Standard's inputs.</summary>
        private static void SetMask(Material m, Texture2D mask, float smoothness, float metallic)
        {
            m.SetFloat("_SmoothnessTextureChannel", 0f); // smoothness from the metallic map's alpha
            if (mask != null)
            {
                m.SetTexture("_MetallicGlossMap", mask);
                m.SetFloat("_GlossMapScale", 1f);
                m.SetTexture("_OcclusionMap", mask);     // Standard reads occlusion from the G channel
                m.SetFloat("_OcclusionStrength", 1f);
                SetKeyword(m, "_METALLICGLOSSMAP", true);
            }
            else
            {
                m.SetTexture("_MetallicGlossMap", null);
                m.SetTexture("_OcclusionMap", null);
                m.SetFloat("_Glossiness", smoothness);
                m.SetFloat("_Metallic", metallic);
                SetKeyword(m, "_METALLICGLOSSMAP", false);
            }
        }

        private static void SetEmission(Material m, Color color, float multiplier)
        {
            bool emissive = multiplier > 0f && color.maxColorComponent > 0f;
            Color linear = color * multiplier;
            linear.a = 1f;
            m.SetColor("_EmissionColor", emissive ? linear : Color.black);
            SetKeyword(m, "_EMISSION", emissive);
            m.globalIlluminationFlags = emissive ? MaterialGlobalIlluminationFlags.BakedEmissive : MaterialGlobalIlluminationFlags.EmissiveIsBlack;
        }

        private static void SetBlendMode(Material m, StandardBlendMode mode, float cutoff)
        {
            m.SetFloat("_Mode", (float)mode);
            m.SetFloat("_Cutoff", cutoff);
            switch (mode)
            {
                case StandardBlendMode.Opaque:
                    m.SetOverrideTag("RenderType", "");
                    m.SetInt("_SrcBlend", (int)BlendMode.One);
                    m.SetInt("_DstBlend", (int)BlendMode.Zero);
                    m.SetInt("_ZWrite", 1);
                    SetKeyword(m, "_ALPHATEST_ON", false);
                    SetKeyword(m, "_ALPHABLEND_ON", false);
                    SetKeyword(m, "_ALPHAPREMULTIPLY_ON", false);
                    m.renderQueue = -1;
                    break;
                case StandardBlendMode.Cutout:
                    m.SetOverrideTag("RenderType", "TransparentCutout");
                    m.SetInt("_SrcBlend", (int)BlendMode.One);
                    m.SetInt("_DstBlend", (int)BlendMode.Zero);
                    m.SetInt("_ZWrite", 1);
                    SetKeyword(m, "_ALPHATEST_ON", true);
                    SetKeyword(m, "_ALPHABLEND_ON", false);
                    SetKeyword(m, "_ALPHAPREMULTIPLY_ON", false);
                    m.renderQueue = (int)RenderQueue.AlphaTest;
                    break;
                case StandardBlendMode.Fade:
                    m.SetOverrideTag("RenderType", "Transparent");
                    m.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                    m.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                    m.SetInt("_ZWrite", 0);
                    SetKeyword(m, "_ALPHATEST_ON", false);
                    SetKeyword(m, "_ALPHABLEND_ON", true);
                    SetKeyword(m, "_ALPHAPREMULTIPLY_ON", false);
                    m.renderQueue = (int)RenderQueue.Transparent;
                    break;
                case StandardBlendMode.Transparent:
                    m.SetOverrideTag("RenderType", "Transparent");
                    m.SetInt("_SrcBlend", (int)BlendMode.One);
                    m.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                    m.SetInt("_ZWrite", 0);
                    SetKeyword(m, "_ALPHATEST_ON", false);
                    SetKeyword(m, "_ALPHABLEND_ON", false);
                    SetKeyword(m, "_ALPHAPREMULTIPLY_ON", true);
                    m.renderQueue = (int)RenderQueue.Transparent;
                    break;
            }
        }

        private static void SetKeyword(Material m, string keyword, bool enabled)
        {
            if (enabled) m.EnableKeyword(keyword);
            else m.DisableKeyword(keyword);
        }

        /// <summary>
        /// Bakes base colour RGB + opacity (alpha channel if present, else red) into one sRGB RGBA PNG imported with
        /// coverage-preserving mip maps. Returns null if the textures cannot be combined.
        /// </summary>
        private static Texture2D CombineAlbedoAndOpacity(Texture2D baseColor, Texture2D opacity, string pngPath, float cutoff)
        {
            Texture2D baseCopy = null, opacityCopy = null, combined = null;
            try
            {
                baseCopy = TextureAssetUtility.CreateReadableCopy(baseColor, false);
                opacityCopy = TextureAssetUtility.CreateReadableCopy(opacity, false);
                bool opacityHasAlpha = TextureAssetUtility.HasAlpha(opacity);

                int w = baseCopy.width, h = baseCopy.height;
                Color32[] rgb = baseCopy.GetPixels32();
                Color32[] a;
                if (opacityCopy.width == w && opacityCopy.height == h)
                {
                    a = opacityCopy.GetPixels32();
                }
                else
                {
                    // Resample the mask to the albedo resolution (bilinear).
                    a = new Color32[w * h];
                    for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        a[y * w + x] = opacityCopy.GetPixelBilinear((x + 0.5f) / w, (y + 0.5f) / h);
                }

                for (int i = 0; i < rgb.Length; i++) rgb[i].a = opacityHasAlpha ? a[i].a : a[i].r;

                combined = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
                combined.SetPixels32(rgb);
                combined.Apply(false, false);
                int maxSize = Mathf.Max(w, h);
                return TextureAssetUtility.SavePng(combined, pngPath, TextureRole.ColorWithAlpha, maxSize, 4, cutoff);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Dodgeball Ultra] Could not combine albedo and opacity for " + pngPath + ": " + e.Message);
                return null;
            }
            finally
            {
                if (baseCopy != null) UnityEngine.Object.DestroyImmediate(baseCopy);
                if (opacityCopy != null) UnityEngine.Object.DestroyImmediate(opacityCopy);
                if (combined != null) UnityEngine.Object.DestroyImmediate(combined);
            }
        }

        // ------------------------------------------------------------------ lighting helpers

        private static Material CreateSkyboxMaterial(string profileFolder, Cubemap hdri)
        {
            string folder = string.IsNullOrEmpty(profileFolder) ? "Assets" : profileFolder;
            string path = folder + "/Skybox_BuiltIn.mat";
            Shader shader = hdri != null ? Shader.Find("Skybox/Cubemap") : Shader.Find("Skybox/Procedural");
            if (shader == null) return RenderSettings.skybox;

            var sky = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (sky == null)
            {
                sky = new Material(shader) { name = "Skybox_BuiltIn" };
                AssetDatabase.CreateAsset(sky, path);
            }
            else if (sky.shader != shader)
            {
                sky.shader = shader;
            }

            if (hdri != null)
            {
                sky.SetTexture("_Tex", hdri);
                sky.SetFloat("_Exposure", 1f);
                sky.SetFloat("_Rotation", 0f);
            }
            else
            {
                sky.SetFloat("_SunDisk", 2f);          // high quality sun disk
                sky.SetFloat("_SunSize", 0.035f);
                sky.SetFloat("_SunSizeConvergence", 6f);
                sky.SetFloat("_AtmosphereThickness", 1f);
                sky.SetColor("_SkyTint", new Color(0.5f, 0.5f, 0.5f));
                sky.SetColor("_GroundColor", new Color(0.37f, 0.35f, 0.34f));
                sky.SetFloat("_Exposure", 1.2f);
            }
            EditorUtility.SetDirty(sky);
            return sky;
        }

        private static void CreateReflectionProbe(Transform parent, string name, Vector3 centre, Vector3 size, int importance, int resolution)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.position = centre;
            var probe = go.AddComponent<ReflectionProbe>();
            probe.center = Vector3.zero;
            probe.size = new Vector3(Mathf.Max(1f, size.x), Mathf.Max(1f, size.y), Mathf.Max(1f, size.z));
            probe.boxProjection = true;
            probe.importance = importance;
            probe.blendDistance = 2f;
            probe.resolution = resolution;
            probe.hdr = true;
            probe.intensity = 1f;
            probe.clearFlags = ReflectionProbeClearFlags.Skybox;
            // Captured once when the scene starts: correct indoor reflections without requiring a bake. Switch to Baked
            // (and Generate Lighting) for zero runtime cost.
            probe.mode = ReflectionProbeMode.Realtime;
            probe.refreshMode = ReflectionProbeRefreshMode.OnAwake;
            probe.timeSlicingMode = ReflectionProbeTimeSlicingMode.AllFacesAtOnce;
            probe.cullingMask = ~(1 << GameLayers.Player | 1 << GameLayers.Ball | 1 << GameLayers.Ragdoll | 1 << GameLayers.AbilityVolume);
        }

        private static Vector3[] DefaultFloodlightPositions(Bounds bounds)
        {
            // Two rows along the long sides, hung just below the ceiling.
            float y = Mathf.Max(bounds.max.y - 1.5f, 8f);
            float x = Mathf.Max(3f, bounds.extents.x - 2f);
            float zSpan = Mathf.Max(6f, bounds.extents.z - 4f);
            var result = new Vector3[8];
            for (int i = 0; i < 4; i++)
            {
                float z = Mathf.Lerp(-zSpan, zSpan, i / 3f) + bounds.center.z;
                result[i * 2] = new Vector3(bounds.center.x - x, y, z);
                result[i * 2 + 1] = new Vector3(bounds.center.x + x, y, z);
            }
            return result;
        }

        /// <summary>Legacy (Built-in) light falloff: 1 / (1 + 25 r^2) with a fade to zero over the last 20 % of the range.</summary>
        private static float LegacyAttenuation(float distance, float range)
        {
            float r = Mathf.Clamp01(distance / Mathf.Max(0.01f, range));
            float fade = Mathf.Clamp01((1f - r) / 0.2f);
            return fade / (1f + 25f * r * r);
        }

        private static Color NormalizedTint(Color c)
        {
            float max = Mathf.Max(0.0001f, c.maxColorComponent);
            return new Color(c.r / max, c.g / max, c.b / max, 1f);
        }

        private static Transform CreateChild(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            return go.transform;
        }
    }
}
