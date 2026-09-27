using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace DodgeballUltra.Rendering
{
    /// <summary>Full description of a physically based opaque (or transparent) lit material for <see cref="MaterialFactory.CreateLit(string, in LitMaterialDesc)"/>.</summary>
    public struct LitMaterialDesc
    {
        /// <summary>Albedo multiplier (sRGB colour, alpha = opacity for transparent materials).</summary>
        public Color BaseColor;
        /// <summary>Albedo texture (sRGB).</summary>
        public Texture2D BaseMap;
        /// <summary>Tangent-space normal map (linear, RGB = XYZ*0.5+0.5, A = 1; works with RG/AG unpacking).</summary>
        public Texture2D NormalMap;
        /// <summary>Normal map strength (1 = as authored).</summary>
        public float NormalScale;
        /// <summary>Packed mask (linear): R metallic, G ambient occlusion, B detail mask, A smoothness (HDRP layout; URP and
        /// Built-in read R/A as metallic/smoothness and G as occlusion from the same texture).</summary>
        public Texture2D MaskMap;
        /// <summary>Smoothness used when no <see cref="MaskMap"/> is supplied.</summary>
        public float Smoothness;
        /// <summary>Metalness used when no <see cref="MaskMap"/> is supplied.</summary>
        public float Metallic;
        /// <summary>UV repeats of every map (HDRP _BaseColorMap_ST, URP _BaseMap_ST, Built-in _MainTex_ST). Zero = (1,1).</summary>
        public Vector2 Tiling;
        /// <summary>UV offset of every map.</summary>
        public Vector2 Offset;
        /// <summary>Emission colour (sRGB, LDR). Black = no emission.</summary>
        public Color EmissiveColor;
        /// <summary>Emission luminance in nits (cd/m²). HDRP uses it physically; URP/Built-in convert it to an HDR multiplier.</summary>
        public float EmissiveIntensity;
        /// <summary>Optional emission texture (multiplied with <see cref="EmissiveColor"/>).</summary>
        public Texture2D EmissiveMap;
        /// <summary>Alpha-blended surface (albedo fades with alpha, specular reflections are preserved).</summary>
        public bool Transparent;
        /// <summary>Render both faces (thin geometry).</summary>
        public bool DoubleSided;

        /// <summary>A plain opaque material description.</summary>
        public static LitMaterialDesc Opaque(Color baseColor, float smoothness, float metallic = 0f) => new LitMaterialDesc
        {
            BaseColor = baseColor,
            Smoothness = smoothness,
            Metallic = metallic,
            NormalScale = 1f,
            Tiling = Vector2.one,
            EmissiveColor = Color.black,
        };
    }

    /// <summary>
    /// CONTRACT (kernel) - creates materials that look physically plausible on the active pipeline.
    /// <para>Owner module: Rendering.</para>
    /// </summary>
    /// <remarks>
    /// <para>Every <c>Create*</c> call returns a NEW material owned by the caller (callers usually keep and reuse it; use
    /// <see cref="GetOrCreate"/> for a keyed shared instance). Shaders and generated textures are cached.</para>
    /// <para>Runtime creation is the fallback path: player builds strip shader variants no saved material uses, and
    /// <c>Shader.Find</c> only sees shaders that are included in the build. The editor Setup Wizard / scene builder
    /// generate proper material assets (via EditorRenderingHooks) and those are preferred for shipping builds; the
    /// DodgeballUltra resources should keep one material per shader/keyword combination created here so the variants
    /// survive stripping.</para>
    /// <para>After setting pipeline properties, <see cref="RuntimeRenderingHooks.Active"/>.ValidateMaterial lets the HDRP adapter
    /// run HDRP's own keyword/pass/stencil validation.</para>
    /// </remarks>
    public static class MaterialFactory
    {
        /// <summary>
        /// Optional factory a pipeline adapter can install to supply particle materials from a vertex-colour aware shader
        /// (HDRP/Unlit ignores particle vertex colours; a Shader Graph based unlit particle shader does not). Returning
        /// null falls back to the built-in behaviour.
        /// </summary>
        public static Func<string, Color, bool, Texture2D, Material> ParticleMaterialOverride { get; set; }

        /// <summary>Conversion used by URP/Built-in to map an emission luminance in nits to an HDR colour multiplier.</summary>
        public const float NitsPerHdrUnit = 3000f;

        private static Texture2D s_softParticle;
        private static readonly Dictionary<string, Material> s_shared = new Dictionary<string, Material>(StringComparer.Ordinal);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            ParticleMaterialOverride = null;
            s_shared.Clear();
        }

        // ------------------------------------------------------------------------------------------------------------
        // Lit
        // ------------------------------------------------------------------------------------------------------------

        public static Material CreateLit(string name, Color baseColor, float smoothness, float metallic = 0f,
            Texture2D baseMap = null, Texture2D normalMap = null, Vector2? tiling = null)
        {
            var desc = LitMaterialDesc.Opaque(baseColor, smoothness, metallic);
            desc.BaseMap = baseMap;
            desc.NormalMap = normalMap;
            desc.Tiling = tiling ?? Vector2.one;
            return CreateLit(name, in desc);
        }

        /// <summary>Creates a lit PBR material from a full description (mask map, emission, transparency, double sided).</summary>
        public static Material CreateLit(string name, in LitMaterialDesc desc)
        {
            PipelineKind kind = RenderPipelineUtil.Current;
            Shader shader = kind switch
            {
                PipelineKind.HighDefinition => RenderPipelineUtil.FindShader("HDRP/Lit"),
                PipelineKind.Universal => RenderPipelineUtil.FindShader("Universal Render Pipeline/Lit", "Universal Render Pipeline/Simple Lit"),
                _ => RenderPipelineUtil.FindShader("Standard", "Legacy Shaders/Diffuse"),
            };

            var m = NewMaterial(name, shader);
            Vector2 tiling = desc.Tiling == Vector2.zero ? Vector2.one : desc.Tiling;
            float normalScale = desc.NormalScale <= 0f ? 1f : desc.NormalScale;

            switch (kind)
            {
                case PipelineKind.HighDefinition: SetupHdrpLit(m, in desc, tiling, normalScale); break;
                case PipelineKind.Universal: SetupUrpLit(m, in desc, tiling, normalScale); break;
                default: SetupBuiltInLit(m, in desc, tiling, normalScale); break;
            }

            RuntimeRenderingHooks.Active?.ValidateMaterial(m);
            return m;
        }

        private static void SetupHdrpLit(Material m, in LitMaterialDesc d, Vector2 tiling, float normalScale)
        {
            SetColor(m, "_BaseColor", d.BaseColor);
            SetColor(m, "_Color", d.BaseColor);                  // GI baking reads _Color/_MainTex
            SetTexture(m, "_BaseColorMap", d.BaseMap);
            SetTexture(m, "_MainTex", d.BaseMap);
            SetFloat(m, "_Metallic", Mathf.Clamp01(d.Metallic));
            SetFloat(m, "_Smoothness", Mathf.Clamp01(d.Smoothness));
            SetFloat(m, "_MaterialID", 1f);                        // Standard
            SetFloat(m, "_NormalMapSpace", 0f);                    // tangent space
            SetKeyword(m, "_NORMALMAP_TANGENT_SPACE", true);

            if (d.NormalMap != null)
            {
                SetTexture(m, "_NormalMap", d.NormalMap);
                SetFloat(m, "_NormalScale", normalScale);
            }
            SetKeyword(m, "_NORMALMAP", d.NormalMap != null);

            if (d.MaskMap != null)
            {
                // The mask stores absolute metalness/AO/smoothness, so remap ranges stay 0..1.
                SetTexture(m, "_MaskMap", d.MaskMap);
                SetFloat(m, "_MetallicRemapMin", 0f);
                SetFloat(m, "_MetallicRemapMax", 1f);
                SetFloat(m, "_SmoothnessRemapMin", 0f);
                SetFloat(m, "_SmoothnessRemapMax", 1f);
                SetFloat(m, "_AORemapMin", 0f);
                SetFloat(m, "_AORemapMax", 1f);
            }
            SetKeyword(m, "_MASKMAP", d.MaskMap != null);

            ApplyTilingAndOffset(m, PipelineKind.HighDefinition, tiling, d.Offset);
            SetFloat(m, "_InvTilingScale", 2f / Mathf.Max(1e-4f, Mathf.Abs(tiling.x) + Mathf.Abs(tiling.y)));

            SetHdrpEmission(m, d.EmissiveColor, d.EmissiveIntensity, d.EmissiveMap);
            SetHdrpDoubleSided(m, d.DoubleSided);

            if (d.Transparent) SetHdrpTransparent(m, additive: false, preserveSpecular: true);
            else SetHdrpOpaque(m);

            SetFloat(m, "_ReceivesSSR", 1f);
            SetFloat(m, "_SupportDecals", 1f);
        }

        private static void SetupUrpLit(Material m, in LitMaterialDesc d, Vector2 tiling, float normalScale)
        {
            SetColor(m, "_BaseColor", d.BaseColor);
            SetColor(m, "_Color", d.BaseColor);
            SetTexture(m, "_BaseMap", d.BaseMap);
            SetTexture(m, "_MainTex", d.BaseMap);
            SetFloat(m, "_WorkflowMode", 1f);                      // metallic workflow

            if (d.NormalMap != null)
            {
                SetTexture(m, "_BumpMap", d.NormalMap);
                SetFloat(m, "_BumpScale", normalScale);
            }
            SetKeyword(m, "_NORMALMAP", d.NormalMap != null);

            if (d.MaskMap != null)
            {
                // URP multiplies the map's alpha by _Smoothness: keep it at 1 so the mask holds absolute smoothness.
                SetTexture(m, "_MetallicGlossMap", d.MaskMap);
                SetTexture(m, "_OcclusionMap", d.MaskMap);         // G channel = occlusion
                SetFloat(m, "_OcclusionStrength", 1f);
                SetFloat(m, "_SmoothnessTextureChannel", 0f);      // metallic alpha
                SetFloat(m, "_Smoothness", 1f);
                SetFloat(m, "_Metallic", 1f);
            }
            else
            {
                SetFloat(m, "_Smoothness", Mathf.Clamp01(d.Smoothness));
                SetFloat(m, "_Metallic", Mathf.Clamp01(d.Metallic));
            }
            SetKeyword(m, "_METALLICSPECGLOSSMAP", d.MaskMap != null);
            SetKeyword(m, "_OCCLUSIONMAP", d.MaskMap != null);

            ApplyTilingAndOffset(m, PipelineKind.Universal, tiling, d.Offset);
            SetLegacyEmission(m, d.EmissiveColor, d.EmissiveIntensity, d.EmissiveMap);

            SetFloat(m, "_Cull", d.DoubleSided ? (float)CullMode.Off : (float)CullMode.Back);
            if (d.Transparent) SetUrpTransparent(m, additive: false, premultiply: true);
            else SetUrpOpaque(m);
        }

        private static void SetupBuiltInLit(Material m, in LitMaterialDesc d, Vector2 tiling, float normalScale)
        {
            SetColor(m, "_Color", d.BaseColor);
            SetTexture(m, "_MainTex", d.BaseMap);

            if (d.NormalMap != null)
            {
                SetTexture(m, "_BumpMap", d.NormalMap);
                SetFloat(m, "_BumpScale", normalScale);
            }
            SetKeyword(m, "_NORMALMAP", d.NormalMap != null);

            if (d.MaskMap != null)
            {
                // Standard: _MetallicGlossMap R = metallic, A = smoothness (scaled by _GlossMapScale); occlusion from G.
                SetTexture(m, "_MetallicGlossMap", d.MaskMap);
                SetTexture(m, "_OcclusionMap", d.MaskMap);
                SetFloat(m, "_OcclusionStrength", 1f);
                SetFloat(m, "_GlossMapScale", 1f);
                SetFloat(m, "_SmoothnessTextureChannel", 0f);
                SetFloat(m, "_Glossiness", Mathf.Clamp01(d.Smoothness));
                SetFloat(m, "_Metallic", Mathf.Clamp01(d.Metallic));
            }
            else
            {
                SetFloat(m, "_Glossiness", Mathf.Clamp01(d.Smoothness));
                SetFloat(m, "_Metallic", Mathf.Clamp01(d.Metallic));
            }
            SetKeyword(m, "_METALLICGLOSSMAP", d.MaskMap != null);

            ApplyTilingAndOffset(m, PipelineKind.BuiltIn, tiling, d.Offset);
            SetLegacyEmission(m, d.EmissiveColor, d.EmissiveIntensity, d.EmissiveMap);

            // Standard has no cull property; double sided is only honoured by shaders that expose _Cull.
            SetFloat(m, "_Cull", d.DoubleSided ? (float)CullMode.Off : (float)CullMode.Back);
            if (d.Transparent) SetStandardTransparent(m);
            else SetStandardOpaque(m);
        }

        // ------------------------------------------------------------------------------------------------------------
        // Particles / unlit / ghost
        // ------------------------------------------------------------------------------------------------------------

        /// <summary>Transparent unlit (additive or alpha) material for particles, rings, ghosts.</summary>
        /// <remarks>HDRP/Unlit does not multiply particle vertex colours; install <see cref="ParticleMaterialOverride"/> from
        /// the HDRP adapter (Shader Graph particle shader) for colour-over-lifetime support. Built-in uses the legacy
        /// particle shaders (tint x2 convention handled here).</remarks>
        public static Material CreateParticle(string name, Color tint, bool additive, Texture2D texture = null)
        {
            Texture2D tex = texture != null ? texture : SoftParticleTexture;

            var overrideFactory = ParticleMaterialOverride;
            if (overrideFactory != null)
            {
                Material custom = overrideFactory(name, tint, additive, tex);
                if (custom != null) return custom;
            }

            PipelineKind kind = RenderPipelineUtil.Current;
            Material m;
            switch (kind)
            {
                case PipelineKind.HighDefinition:
                {
                    m = NewMaterial(name, RenderPipelineUtil.FindShader("HDRP/Unlit"));
                    SetColor(m, "_UnlitColor", tint);
                    SetColor(m, "_Color", tint);
                    SetTexture(m, "_UnlitColorMap", tex);
                    SetTexture(m, "_MainTex", tex);
                    SetColor(m, "_EmissiveColor", Color.black);
                    SetHdrpDoubleSided(m, true);
                    SetHdrpTransparent(m, additive, preserveSpecular: false);
                    SetFloat(m, "_EnableFogOnTransparent", 1f);
                    SetKeyword(m, "_ENABLE_FOG_ON_TRANSPARENT", true);
                    break;
                }
                case PipelineKind.Universal:
                {
                    m = NewMaterial(name, RenderPipelineUtil.FindShader(
                        "Universal Render Pipeline/Particles/Unlit", "Universal Render Pipeline/Unlit"));
                    SetColor(m, "_BaseColor", tint);
                    SetColor(m, "_Color", tint);
                    SetTexture(m, "_BaseMap", tex);
                    SetTexture(m, "_MainTex", tex);
                    SetFloat(m, "_Cull", (float)CullMode.Off);
                    SetUrpTransparent(m, additive, premultiply: false);
                    break;
                }
                default:
                {
                    Shader shader = additive
                        ? RenderPipelineUtil.FindShader("Legacy Shaders/Particles/Additive", "Particles/Standard Unlit",
                            "Mobile/Particles/Additive", "Sprites/Default")
                        : RenderPipelineUtil.FindShader("Legacy Shaders/Particles/Alpha Blended", "Particles/Standard Unlit",
                            "Mobile/Particles/Alpha Blended", "Sprites/Default");
                    m = NewMaterial(name, shader);
                    SetTexture(m, "_MainTex", tex);
                    string shaderName = m.shader != null ? m.shader.name : string.Empty;
                    if (shaderName.StartsWith("Legacy Shaders/Particles", StringComparison.Ordinal))
                    {
                        // Legacy particle shaders compute 2 * _TintColor * vertexColor * tex.
                        SetColor(m, "_TintColor", new Color(tint.r * 0.5f, tint.g * 0.5f, tint.b * 0.5f, tint.a * 0.5f));
                    }
                    else if (shaderName == "Particles/Standard Unlit")
                    {
                        SetColor(m, "_Color", tint);
                        SetFloat(m, "_Mode", additive ? 4f : 2f);   // 4 = Additive, 2 = Fade
                        SetInt(m, "_SrcBlend", (int)BlendMode.SrcAlpha);
                        SetInt(m, "_DstBlend", additive ? (int)BlendMode.One : (int)BlendMode.OneMinusSrcAlpha);
                        SetInt(m, "_ZWrite", 0);
                        SetKeyword(m, "_ALPHABLEND_ON", true);
                        SetKeyword(m, "_ALPHATEST_ON", false);
                        SetKeyword(m, "_ALPHAPREMULTIPLY_ON", false);
                        SetKeyword(m, "_ALPHAMODULATE_ON", false);
                        m.SetOverrideTag("RenderType", "Transparent");
                    }
                    else
                    {
                        SetColor(m, "_Color", tint);
                    }
                    m.renderQueue = (int)RenderQueue.Transparent;
                    break;
                }
            }

            RuntimeRenderingHooks.Active?.ValidateMaterial(m);
            return m;
        }

        /// <summary>Opaque/transparent unlit material with HDR colour (emissive markers, beams).</summary>
        public static Material CreateUnlit(string name, Color color, bool transparent)
        {
            PipelineKind kind = RenderPipelineUtil.Current;
            Material m;
            switch (kind)
            {
                case PipelineKind.HighDefinition:
                {
                    // HDRP/Unlit writes _UnlitColor without exposure, so values > 1 bloom like on a display.
                    m = NewMaterial(name, RenderPipelineUtil.FindShader("HDRP/Unlit"));
                    SetColor(m, "_UnlitColor", color);
                    SetColor(m, "_Color", color);
                    SetColor(m, "_EmissiveColor", Color.black);
                    if (transparent) SetHdrpTransparent(m, additive: false, preserveSpecular: false);
                    else SetHdrpOpaque(m);
                    break;
                }
                case PipelineKind.Universal:
                {
                    m = NewMaterial(name, RenderPipelineUtil.FindShader("Universal Render Pipeline/Unlit"));
                    SetColor(m, "_BaseColor", color);
                    SetColor(m, "_Color", color);
                    if (transparent) SetUrpTransparent(m, additive: false, premultiply: false);
                    else SetUrpOpaque(m);
                    break;
                }
                default:
                {
                    // Sprites/Default is always included in builds and supports alpha + vertex colour.
                    m = NewMaterial(name, transparent
                        ? RenderPipelineUtil.FindShader("Sprites/Default", "Unlit/Transparent", "Unlit/Color")
                        : RenderPipelineUtil.FindShader("Unlit/Color", "Sprites/Default"));
                    SetColor(m, "_Color", color);
                    m.renderQueue = transparent ? (int)RenderQueue.Transparent : (int)RenderQueue.Geometry;
                    break;
                }
            }

            RuntimeRenderingHooks.Active?.ValidateMaterial(m);
            return m;
        }

        /// <summary>Translucent "ghost" material used for afterimages, clones and cloaks.</summary>
        /// <remarks>
        /// A glossy alpha-blended lit surface whose albedo fades with alpha while specular reflections are preserved:
        /// grazing-angle reflections stay bright (Fresnel) so silhouettes read as a glassy shell, plus a faint emissive
        /// tint so the ghost stays visible in shadow.
        /// </remarks>
        public static Material CreateGhost(string name, Color tint)
        {
            float alpha = tint.a >= 0.999f ? 0.35f : Mathf.Clamp(tint.a, 0.05f, 0.95f);
            var desc = LitMaterialDesc.Opaque(new Color(tint.r, tint.g, tint.b, alpha), 0.92f, 0f);
            desc.Transparent = true;
            desc.EmissiveColor = new Color(tint.r, tint.g, tint.b, 1f);
            desc.EmissiveIntensity = 60f;   // nits: a soft glow, far below lit surfaces in an arena (~300+ nits)
            return CreateLit(name, in desc);
        }

        /// <summary>Soft round particle texture (generated once, cached).</summary>
        public static Texture2D SoftParticleTexture
        {
            get
            {
                if (s_softParticle != null) return s_softParticle;

                const int size = 128;
                var tex = new Texture2D(size, size, TextureFormat.RGBA32, true, false)
                {
                    name = "DU_SoftParticle",
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Trilinear,
                    anisoLevel = 1,
                    hideFlags = HideFlags.DontUnloadUnusedAsset,
                };
                var px = new Color32[size * size];
                float inv = 2f / size;
                for (int y = 0; y < size; y++)
                {
                    float fy = (y + 0.5f) * inv - 1f;
                    for (int x = 0; x < size; x++)
                    {
                        float fx = (x + 0.5f) * inv - 1f;
                        float r = Mathf.Sqrt(fx * fx + fy * fy);
                        // Gaussian core with a smooth edge fade so the quad border is exactly transparent.
                        float a = Mathf.Exp(-r * r * 4.5f) * (1f - Mathf.SmoothStep(0.78f, 1f, r));
                        px[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.Clamp(Mathf.RoundToInt(a * 255f), 0, 255));
                    }
                }
                tex.SetPixels32(px);
                tex.Apply(true, true);
                s_softParticle = tex;
                return tex;
            }
        }

        /// <summary>
        /// Returns the shared material registered under <paramref name="key"/>, creating it with <paramref name="factory"/>
        /// on first use (or after it was destroyed). Use for materials many objects share and nobody mutates.
        /// </summary>
        public static Material GetOrCreate(string key, Func<Material> factory)
        {
            if (string.IsNullOrEmpty(key) || factory == null) return null;
            if (s_shared.TryGetValue(key, out Material existing) && existing != null) return existing;
            Material created = factory();
            if (created != null) s_shared[key] = created;
            return created;
        }

        // ------------------------------------------------------------------------------------------------------------
        // Public helpers (pipeline aware)
        // ------------------------------------------------------------------------------------------------------------

        /// <summary>Sets UV tiling/offset of every map of a material created by this factory (HDRP _BaseColorMap_ST etc.).</summary>
        public static void ApplyTiling(Material material, Vector2 tiling, Vector2 offset = default)
        {
            if (material == null) return;
            ApplyTilingAndOffset(material, RenderPipelineUtil.Current, tiling, offset);
        }

        /// <summary>Sets the albedo colour on a lit or unlit material of any pipeline.</summary>
        public static void SetBaseColor(Material material, Color color)
        {
            if (material == null) return;
            SetColor(material, "_BaseColor", color);
            SetColor(material, "_UnlitColor", color);
            SetColor(material, "_Color", color);
        }

        /// <summary>Sets emission (sRGB colour + luminance in nits) on a lit material of the active pipeline.</summary>
        public static void SetEmission(Material material, Color color, float nits)
        {
            if (material == null) return;
            if (RenderPipelineUtil.Current == PipelineKind.HighDefinition) SetHdrpEmission(material, color, nits, null);
            else SetLegacyEmission(material, color, nits, null);
        }

        // ------------------------------------------------------------------------------------------------------------
        // Pipeline specifics
        // ------------------------------------------------------------------------------------------------------------

        private static void ApplyTilingAndOffset(Material m, PipelineKind kind, Vector2 tiling, Vector2 offset)
        {
            // Every pipeline samples all main maps with the _ST of its main texture.
            string main = kind == PipelineKind.HighDefinition ? "_BaseColorMap" : kind == PipelineKind.Universal ? "_BaseMap" : "_MainTex";
            if (m.HasProperty(main))
            {
                m.SetTextureScale(main, tiling);
                m.SetTextureOffset(main, offset);
            }
            if (main != "_MainTex" && m.HasProperty("_MainTex"))
            {
                m.SetTextureScale("_MainTex", tiling);
                m.SetTextureOffset("_MainTex", offset);
            }
        }

        private static void SetHdrpEmission(Material m, Color color, float nits, Texture2D map)
        {
            bool on = nits > 0f && (color.r > 0f || color.g > 0f || color.b > 0f);
            Color ldr = new Color(color.r, color.g, color.b, 1f);
            // HDRP convention (MaterialExtension.UpdateEmissiveColorFromIntensityAndEmissiveColorLDR): linear LDR * nits.
            SetColor(m, "_EmissiveColor", on ? ldr.linear * nits : Color.black);
            SetColor(m, "_EmissiveColorLDR", on ? ldr : Color.black);
            SetFloat(m, "_EmissiveIntensity", on ? nits : 1f);
            SetFloat(m, "_EmissiveIntensityUnit", 0f);   // nits
            SetFloat(m, "_UseEmissiveIntensity", 0f);
            SetFloat(m, "_EmissiveExposureWeight", 1f);
            SetFloat(m, "_AlbedoAffectEmissive", 0f);
            if (map != null) SetTexture(m, "_EmissiveColorMap", map);
            SetKeyword(m, "_EMISSIVE_COLOR_MAP", on && map != null);
            SetColor(m, "_EmissionColor", on ? Color.white : Color.black);   // lightmapper hint
            m.globalIlluminationFlags = on ? MaterialGlobalIlluminationFlags.RealtimeEmissive : MaterialGlobalIlluminationFlags.EmissiveIsBlack;
        }

        private static void SetLegacyEmission(Material m, Color color, float nits, Texture2D map)
        {
            bool on = nits > 0f && (color.r > 0f || color.g > 0f || color.b > 0f);
            float multiplier = Mathf.Clamp(nits / NitsPerHdrUnit, 0.25f, 16f);
            Color ldr = new Color(color.r, color.g, color.b, 1f);
            SetColor(m, "_EmissionColor", on ? ldr.linear * multiplier : Color.black);
            if (map != null) SetTexture(m, "_EmissionMap", map);
            SetKeyword(m, "_EMISSION", on);
            m.globalIlluminationFlags = on ? MaterialGlobalIlluminationFlags.RealtimeEmissive : MaterialGlobalIlluminationFlags.EmissiveIsBlack;
        }

        private static void SetHdrpDoubleSided(Material m, bool doubleSided)
        {
            SetFloat(m, "_DoubleSidedEnable", doubleSided ? 1f : 0f);
            SetFloat(m, "_CullMode", doubleSided ? (float)CullMode.Off : (float)CullMode.Back);
            SetFloat(m, "_CullModeForward", doubleSided ? (float)CullMode.Off : (float)CullMode.Back);
            SetKeyword(m, "_DOUBLESIDED_ON", doubleSided);
        }

        private static void SetHdrpOpaque(Material m)
        {
            SetFloat(m, "_SurfaceType", 0f);
            SetFloat(m, "_BlendMode", 0f);
            SetInt(m, "_SrcBlend", (int)BlendMode.One);
            SetInt(m, "_DstBlend", (int)BlendMode.Zero);
            SetInt(m, "_DstBlend2", (int)BlendMode.Zero);
            SetInt(m, "_AlphaSrcBlend", (int)BlendMode.One);
            SetInt(m, "_AlphaDstBlend", (int)BlendMode.Zero);
            SetFloat(m, "_ZWrite", 1f);
            SetKeyword(m, "_SURFACE_TYPE_TRANSPARENT", false);
            SetKeyword(m, "_BLENDMODE_ALPHA", false);
            SetKeyword(m, "_BLENDMODE_ADD", false);
            SetKeyword(m, "_BLENDMODE_PRESERVE_SPECULAR_LIGHTING", false);
            m.SetOverrideTag("RenderType", string.Empty);
            m.renderQueue = (int)RenderQueue.Geometry;
        }

        private static void SetHdrpTransparent(Material m, bool additive, bool preserveSpecular)
        {
            // Mirrors BaseUnlitAPI.SetupBaseUnlitKeywords / BaseLitAPI for SurfaceType.Transparent (HDRP 17).
            SetFloat(m, "_SurfaceType", 1f);
            SetFloat(m, "_BlendMode", additive ? 1f : 0f);              // BlendingMode.Additive = 1, Alpha = 0
            SetInt(m, "_SrcBlend", (int)BlendMode.One);                  // src * alpha happens in the shader
            SetInt(m, "_DstBlend", additive ? (int)BlendMode.One : (int)BlendMode.OneMinusSrcAlpha);
            SetInt(m, "_DstBlend2", (int)BlendMode.OneMinusSrcAlpha);
            SetInt(m, "_AlphaSrcBlend", (int)BlendMode.One);
            SetInt(m, "_AlphaDstBlend", additive ? (int)BlendMode.One : (int)BlendMode.OneMinusSrcAlpha);
            SetFloat(m, "_ZWrite", 0f);
            SetFloat(m, "_TransparentZWrite", 0f);
            SetFloat(m, "_EnableBlendModePreserveSpecularLighting", preserveSpecular ? 1f : 0f);
            SetKeyword(m, "_SURFACE_TYPE_TRANSPARENT", true);
            // Legacy (pre-HDRP 10) blend keywords: harmless on HDRP 17, required by older shader versions.
            SetKeyword(m, "_BLENDMODE_ALPHA", !additive);
            SetKeyword(m, "_BLENDMODE_ADD", additive);
            SetKeyword(m, "_BLENDMODE_PRESERVE_SPECULAR_LIGHTING", preserveSpecular);
            SetKeyword(m, "_ENABLE_FOG_ON_TRANSPARENT", m.HasProperty("_EnableFogOnTransparent") && m.GetFloat("_EnableFogOnTransparent") > 0f);
            m.SetOverrideTag("RenderType", "Transparent");
            m.renderQueue = (int)RenderQueue.Transparent;
        }

        private static void SetUrpOpaque(Material m)
        {
            SetFloat(m, "_Surface", 0f);
            SetFloat(m, "_Blend", 0f);
            SetInt(m, "_SrcBlend", (int)BlendMode.One);
            SetInt(m, "_DstBlend", (int)BlendMode.Zero);
            SetInt(m, "_SrcBlendAlpha", (int)BlendMode.One);
            SetInt(m, "_DstBlendAlpha", (int)BlendMode.Zero);
            SetFloat(m, "_ZWrite", 1f);
            SetKeyword(m, "_SURFACE_TYPE_TRANSPARENT", false);
            SetKeyword(m, "_ALPHAPREMULTIPLY_ON", false);
            m.SetOverrideTag("RenderType", "Opaque");
            m.renderQueue = (int)RenderQueue.Geometry;
        }

        private static void SetUrpTransparent(Material m, bool additive, bool premultiply)
        {
            // URP BaseShaderGUI: Alpha = 0, Premultiply = 1, Additive = 2.
            SetFloat(m, "_Surface", 1f);
            SetFloat(m, "_Blend", additive ? 2f : premultiply ? 1f : 0f);
            SetInt(m, "_SrcBlend", premultiply ? (int)BlendMode.One : (int)BlendMode.SrcAlpha);
            SetInt(m, "_DstBlend", additive ? (int)BlendMode.One : (int)BlendMode.OneMinusSrcAlpha);
            SetInt(m, "_SrcBlendAlpha", (int)BlendMode.One);
            SetInt(m, "_DstBlendAlpha", additive ? (int)BlendMode.One : (int)BlendMode.OneMinusSrcAlpha);
            SetFloat(m, "_ZWrite", 0f);
            SetKeyword(m, "_SURFACE_TYPE_TRANSPARENT", true);
            SetKeyword(m, "_ALPHAPREMULTIPLY_ON", premultiply && !additive);
            m.SetOverrideTag("RenderType", "Transparent");
            m.renderQueue = (int)RenderQueue.Transparent;
        }

        private static void SetStandardOpaque(Material m)
        {
            SetFloat(m, "_Mode", 0f);
            SetInt(m, "_SrcBlend", (int)BlendMode.One);
            SetInt(m, "_DstBlend", (int)BlendMode.Zero);
            SetInt(m, "_ZWrite", 1);
            SetKeyword(m, "_ALPHATEST_ON", false);
            SetKeyword(m, "_ALPHABLEND_ON", false);
            SetKeyword(m, "_ALPHAPREMULTIPLY_ON", false);
            m.SetOverrideTag("RenderType", string.Empty);
            m.renderQueue = -1;   // shader default (Geometry)
        }

        private static void SetStandardTransparent(Material m)
        {
            // Standard "Transparent" mode: premultiplied albedo, specular reflections stay at full strength.
            SetFloat(m, "_Mode", 3f);
            SetInt(m, "_SrcBlend", (int)BlendMode.One);
            SetInt(m, "_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            SetInt(m, "_ZWrite", 0);
            SetKeyword(m, "_ALPHATEST_ON", false);
            SetKeyword(m, "_ALPHABLEND_ON", false);
            SetKeyword(m, "_ALPHAPREMULTIPLY_ON", true);
            m.SetOverrideTag("RenderType", "Transparent");
            m.renderQueue = (int)RenderQueue.Transparent;
        }

        // ------------------------------------------------------------------------------------------------------------
        // Low level
        // ------------------------------------------------------------------------------------------------------------

        private static Material NewMaterial(string name, Shader shader)
        {
            if (shader == null)
            {
                // Last resort so callers never get null: always-included shaders first, error shader otherwise.
                shader = RenderPipelineUtil.FindShader("Sprites/Default", "Unlit/Color", "Hidden/InternalErrorShader");
                Debug.LogWarning($"[MaterialFactory] No suitable shader for '{name}' on {RenderPipelineUtil.Current}; " +
                                 "run the Dodgeball Ultra Setup Wizard so material assets (and their shaders) are included in the build.");
            }
            return new Material(shader) { name = string.IsNullOrEmpty(name) ? "DU_RuntimeMaterial" : name };
        }

        private static void SetColor(Material m, string prop, Color c)
        {
            if (m.HasProperty(prop)) m.SetColor(prop, c);
        }

        private static void SetFloat(Material m, string prop, float v)
        {
            if (m.HasProperty(prop)) m.SetFloat(prop, v);
        }

        private static void SetInt(Material m, string prop, int v)
        {
            // Blend/cull state properties are declared as Float in most shaders; SetFloat works for both.
            if (m.HasProperty(prop)) m.SetFloat(prop, v);
        }

        private static void SetTexture(Material m, string prop, Texture tex)
        {
            if (tex != null && m.HasProperty(prop)) m.SetTexture(prop, tex);
        }

        private static void SetKeyword(Material m, string keyword, bool on)
        {
            if (on) m.EnableKeyword(keyword);
            else m.DisableKeyword(keyword);
        }
    }
}
