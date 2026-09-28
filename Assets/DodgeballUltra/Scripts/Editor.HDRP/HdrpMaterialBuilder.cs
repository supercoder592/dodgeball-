using System.IO;
using DodgeballUltra.Editor.Pipeline;
using UnityEditor;
using UnityEditor.Rendering.HighDefinition;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace DodgeballUltra.Editor.HDRP
{
    /// <summary>
    /// Creates HDRP/Lit material assets for the realistic Rocketbox humans and the arena surfaces.
    /// <list type="bullet">
    /// <item><b>Skin</b> (head/face): Subsurface Scattering material type with HDRP's shipped Skin diffusion profile.</item>
    /// <item><b>Body</b> (Rocketbox body sheet = clothing + exposed arms/legs): also SSS, but with the subsurface mask
    /// lowered to <see cref="BodySubsurfaceMask"/> so exposed skin gets a soft scatter while fabric is barely blurred.
    /// Set it to 0 to get a standard Lit body.</item>
    /// <item><b>Hair</b> (hair/eyelash cards): alpha clipping from the base colour alpha (cutoff <see cref="HairAlphaCutoff"/>),
    /// double-sided with flipped back-face normals and geometric specular AA against shimmering.</item>
    /// <item><b>Generic</b>: standard Lit.</item>
    /// </list>
    /// Smoothness comes from the packed mask map (HDRP layout, smoothness in A) remapped to physically plausible ranges
    /// per surface; metallic is forced to 0 for skin/fabric/hair (dielectrics).
    /// </summary>
    public static class HdrpMaterialBuilder
    {
        /// <summary>HDRP's default Lit shader.</summary>
        public const string LitShaderName = "HDRP/Lit";

        /// <summary>Skin diffusion profile shipped with HDRP 17 (listed in the HDRP editor assets' default profiles).</summary>
        public const string SkinDiffusionProfilePath =
            "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipelineResources/SkinDiffusionProfile.asset";

        /// <summary>Alpha-clip threshold for hair cards (Rocketbox opacity maps have soft gradients; 0.35 keeps strands full).</summary>
        public static float HairAlphaCutoff = 0.35f;

        /// <summary>Subsurface mask (scatter radius scale) of Body materials. 0 = standard Lit (no SSS).</summary>
        public static float BodySubsurfaceMask = 0.3f;

        // HDRP/Lit property names (verified against Lit.shader, HDRP 17.0.4).
        private const string BaseColor = "_BaseColor", BaseColorMap = "_BaseColorMap";
        private const string NormalMap = "_NormalMap", NormalScale = "_NormalScale";
        private const string MaskMap = "_MaskMap", Smoothness = "_Smoothness", Metallic = "_Metallic";
        private const string SmoothnessRemapMin = "_SmoothnessRemapMin", SmoothnessRemapMax = "_SmoothnessRemapMax";
        private const string MetallicRemapMin = "_MetallicRemapMin", MetallicRemapMax = "_MetallicRemapMax";
        private const string AORemapMin = "_AORemapMin", AORemapMax = "_AORemapMax";
        private const string MaterialID = "_MaterialID", SubsurfaceMask = "_SubsurfaceMask";
        private const string AlphaCutoffEnable = "_AlphaCutoffEnable", AlphaCutoff = "_AlphaCutoff";
        private const string AlphaCutoffShadow = "_AlphaCutoffShadow", UseShadowThreshold = "_UseShadowThreshold";
        private const string DoubleSidedEnable = "_DoubleSidedEnable", DoubleSidedNormalMode = "_DoubleSidedNormalMode";
        private const string GeometricSpecularAA = "_EnableGeometricSpecularAA";
        private const string SpecularAAVariance = "_SpecularAAScreenSpaceVariance", SpecularAAThreshold = "_SpecularAAThreshold";
        private const string BlendMode = "_BlendMode", EnableFogOnTransparent = "_EnableFogOnTransparent";

        private const float DoubleSidedFlip = 0f, DoubleSidedMirror = 1f;

        /// <summary>Per-kind surface description.</summary>
        private struct CharacterSurface
        {
            public Vector2 SmoothnessRemap;  // applied to mask.a
            public float Smoothness;         // used without a mask map
            public float SubsurfaceMask;     // 0 = standard Lit
            public bool AlphaClip;
            public bool DoubleSided;
            public bool SpecularAA;
        }

        private static CharacterSurface GetSurface(CharacterMaterialKind kind)
        {
            switch (kind)
            {
                case CharacterMaterialKind.Skin:
                    // Human skin: oily T-zone ~0.6, dry cheeks ~0.3.
                    return new CharacterSurface { SmoothnessRemap = new Vector2(0.28f, 0.62f), Smoothness = 0.45f, SubsurfaceMask = 1f };
                case CharacterMaterialKind.Body:
                    // Cotton/polyester sportswear 0.05-0.45, exposed skin up to ~0.5.
                    return new CharacterSurface { SmoothnessRemap = new Vector2(0.05f, 0.5f), Smoothness = 0.3f, SubsurfaceMask = Mathf.Clamp01(BodySubsurfaceMask) };
                case CharacterMaterialKind.Hair:
                    return new CharacterSurface
                    {
                        SmoothnessRemap = new Vector2(0.25f, 0.6f), Smoothness = 0.45f, AlphaClip = true, DoubleSided = true, SpecularAA = true,
                    };
                default:
                    return new CharacterSurface { SmoothnessRemap = new Vector2(0f, 0.75f), Smoothness = 0.4f };
            }
        }

        /// <summary>Implements <see cref="IEditorRenderingHooks.CreateCharacterMaterial"/> for HDRP.</summary>
        public static Material CreateCharacterMaterial(string assetPath, CharacterMaterialKind kind, Texture2D baseColor,
            Texture2D normalMap, Texture2D maskMap, Texture2D opacityMap)
        {
            Shader shader = FindLitShader();
            if (shader == null || string.IsNullOrEmpty(assetPath)) return null;

            CharacterSurface surface = GetSurface(kind);
            bool alphaClip = surface.AlphaClip || opacityMap != null;

            // --- Textures (fix import settings the slots require) -----------------------------------------------
            baseColor = HdrpEditorAssetUtility.EnsureImport(baseColor, sRGB: true, normalMap: false, alphaIsTransparency: alphaClip);
            opacityMap = HdrpEditorAssetUtility.EnsureImport(opacityMap, sRGB: true, normalMap: false, alphaIsTransparency: alphaClip);
            normalMap = HdrpEditorAssetUtility.EnsureImport(normalMap, sRGB: false, normalMap: true);
            maskMap = HdrpEditorAssetUtility.EnsureImport(maskMap, sRGB: false, normalMap: false);

            Texture2D albedo = alphaClip ? ResolveAlphaClippedBaseMap(assetPath, baseColor, opacityMap) : baseColor ?? opacityMap;

            var material = new Material(shader) { name = Path.GetFileNameWithoutExtension(assetPath) };
            material.SetColor(BaseColor, Color.white);
            if (albedo != null) material.SetTexture(BaseColorMap, albedo);

            if (normalMap != null)
            {
                material.SetTexture(NormalMap, normalMap);
                material.SetFloat(NormalScale, 1f);
            }

            // Dielectrics: never let a specular-derived mask leak into metallic.
            material.SetFloat(Metallic, 0f);
            material.SetFloat(MetallicRemapMin, 0f);
            material.SetFloat(MetallicRemapMax, 0f);
            if (maskMap != null)
            {
                material.SetTexture(MaskMap, maskMap);
                material.SetFloat(SmoothnessRemapMin, surface.SmoothnessRemap.x);
                material.SetFloat(SmoothnessRemapMax, surface.SmoothnessRemap.y);
                material.SetFloat(AORemapMin, 0f);
                material.SetFloat(AORemapMax, 1f);
            }
            else
            {
                material.SetFloat(Smoothness, surface.Smoothness);
            }

            if (alphaClip)
            {
                material.SetFloat(AlphaCutoffEnable, 1f);
                material.SetFloat(AlphaCutoff, HairAlphaCutoff);
                material.SetFloat(UseShadowThreshold, 1f);
                material.SetFloat(AlphaCutoffShadow, HairAlphaCutoff);
            }

            if (surface.DoubleSided)
            {
                material.SetFloat(DoubleSidedEnable, 1f);
                material.SetFloat(DoubleSidedNormalMode, DoubleSidedFlip); // thin cards: back face lit as its own side
            }

            if (surface.SpecularAA)
            {
                material.SetFloat(GeometricSpecularAA, 1f);
                material.SetFloat(SpecularAAVariance, 0.1f);
                material.SetFloat(SpecularAAThreshold, 0.2f);
            }

            bool subsurface = surface.SubsurfaceMask > 0f;
            material.SetFloat(MaterialID, subsurface ? (float)MaterialId.LitSSS : (float)MaterialId.LitStandard);
            if (subsurface) material.SetFloat(SubsurfaceMask, surface.SubsurfaceMask);

            // --- Persist, then set the diffusion profile (HDRP stores it as a sub-asset reference of the material) ----
            Material persistent = HdrpEditorAssetUtility.SaveMaterial(material, assetPath);
            if (subsurface)
            {
                DiffusionProfileSettings skin = LoadSkinDiffusionProfile();
                if (skin != null)
                {
                    HDMaterial.SetDiffusionProfile(persistent, skin);
                    persistent.SetMaterialType(MaterialId.LitSSS);
                }
                else
                {
                    Debug.LogWarning($"[DU HDRP] Skin diffusion profile not found; '{assetPath}' falls back to standard Lit.");
                    persistent.SetMaterialType(MaterialId.LitStandard);
                }
            }

            FinalizeMaterial(persistent);
            return persistent;
        }

        /// <summary>Implements <see cref="IEditorRenderingHooks.CreateEnvironmentMaterial"/> for HDRP.</summary>
        public static Material CreateEnvironmentMaterial(string assetPath, in EnvironmentMaterialDesc desc)
        {
            Shader shader = FindLitShader();
            if (shader == null || string.IsNullOrEmpty(assetPath)) return null;

            Texture2D baseMap = HdrpEditorAssetUtility.EnsureImport(desc.BaseMap, sRGB: true, normalMap: false);
            Texture2D normal = HdrpEditorAssetUtility.EnsureImport(desc.NormalMap, sRGB: false, normalMap: true);
            Texture2D mask = HdrpEditorAssetUtility.EnsureImport(desc.MaskMap, sRGB: false, normalMap: false);

            string materialName = string.IsNullOrEmpty(desc.Name) ? Path.GetFileNameWithoutExtension(assetPath) : desc.Name;
            var material = new Material(shader) { name = materialName };

            // A default-initialised struct has a black, transparent colour: treat "no colour" as white.
            Color tint = desc.BaseColor == default ? Color.white : desc.BaseColor;
            material.SetColor(BaseColor, tint);

            // HDRP/Lit shares the base map's tiling/offset for all main maps (mask, normal).
            Vector2 tiling = desc.Tiling == Vector2.zero ? Vector2.one : desc.Tiling;
            if (baseMap != null) material.SetTexture(BaseColorMap, baseMap);
            material.SetTextureScale(BaseColorMap, tiling);

            if (normal != null)
            {
                material.SetTexture(NormalMap, normal);
                material.SetFloat(NormalScale, Mathf.Clamp(desc.NormalStrength > 0f ? desc.NormalStrength : 1f, 0f, 8f));
            }

            material.SetFloat(Smoothness, Mathf.Clamp01(desc.Smoothness));
            material.SetFloat(Metallic, Mathf.Clamp01(desc.Metallic));
            if (mask != null)
            {
                // The arena mask maps are authored in absolute HDRP units (R metallic, G AO, A smoothness).
                material.SetTexture(MaskMap, mask);
                material.SetFloat(MetallicRemapMin, 0f);
                material.SetFloat(MetallicRemapMax, 1f);
                material.SetFloat(SmoothnessRemapMin, 0f);
                material.SetFloat(SmoothnessRemapMax, 1f);
                material.SetFloat(AORemapMin, 0f);
                material.SetFloat(AORemapMax, 1f);
            }

            if (desc.DoubleSided)
            {
                material.SetFloat(DoubleSidedEnable, 1f);
                material.SetFloat(DoubleSidedNormalMode, DoubleSidedMirror);
            }

            if (desc.Transparent)
            {
                material.SetFloat(BlendMode, 0f); // alpha blending
                material.SetFloat(EnableFogOnTransparent, 1f);
                HDMaterial.SetSurfaceType(material, true);
            }

            if (desc.EmissiveIntensity > 0f && desc.EmissiveColor.maxColorComponent > 0f)
            {
                // Physical emission: LDR colour x luminance in nits (exposure-weighted, so it reads right at EV ~11.5).
                // HDMaterial converts the LDR colour to linear, so the desc's linear colour is passed gamma-encoded.
                HDMaterial.SetUseEmissiveIntensity(material, true);
                HDMaterial.SetEmissiveColor(material, desc.EmissiveColor.gamma);
                HDMaterial.SetEmissiveIntensity(material, desc.EmissiveIntensity, EmissiveIntensityUnit.Nits);
                // Emission of fixtures stays out of baked GI: the real Light components already provide that energy.
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
            }

            Material persistent = HdrpEditorAssetUtility.SaveMaterial(material, assetPath);
            FinalizeMaterial(persistent);
            return persistent;
        }

        /// <summary>Loads HDRP's Skin diffusion profile (package path first, then any profile named "Skin").</summary>
        public static DiffusionProfileSettings LoadSkinDiffusionProfile()
        {
            var profile = AssetDatabase.LoadAssetAtPath<DiffusionProfileSettings>(SkinDiffusionProfilePath);
            if (profile != null) return profile;

            foreach (string guid in AssetDatabase.FindAssets("Skin t:DiffusionProfileSettings"))
            {
                profile = AssetDatabase.LoadAssetAtPath<DiffusionProfileSettings>(AssetDatabase.GUIDToAssetPath(guid));
                if (profile != null) return profile;
            }
            return null;
        }

        // ------------------------------------------------------------------------------------------------------------

        private static Shader FindLitShader()
        {
            Shader shader = Shader.Find(LitShaderName);
            if (shader == null)
                Debug.LogError($"[DU HDRP] Shader '{LitShaderName}' not found - is the HDRP package installed and compiled?");
            return shader;
        }

        /// <summary>
        /// HDRP/Lit only reads alpha-clip opacity from the base colour map's alpha. Rocketbox ships a separate
        /// "opacity_color" texture for hair/eyelashes, so when both textures are given and differ, they are baked into one.
        /// </summary>
        private static Texture2D ResolveAlphaClippedBaseMap(string materialPath, Texture2D baseColor, Texture2D opacity)
        {
            if (opacity == null || opacity == baseColor) return baseColor ?? opacity;
            if (baseColor == null) return opacity;

            string directory = Path.GetDirectoryName(materialPath)?.Replace('\\', '/') ?? "Assets";
            string bakedPath = $"{directory}/{Path.GetFileNameWithoutExtension(materialPath)}_BaseOpacity.png";
            Texture2D baked = HdrpEditorAssetUtility.BakeColorWithOpacity(baseColor, opacity, bakedPath);
            if (baked != null) return baked;

            // No GPU: prefer whichever texture actually carries an alpha channel.
            return HdrpEditorAssetUtility.SourceHasAlpha(opacity) && !HdrpEditorAssetUtility.SourceHasAlpha(baseColor) ? opacity : baseColor;
        }

        /// <summary>Resets keywords/passes the way the HDRP material inspector does, then saves the asset.</summary>
        private static void FinalizeMaterial(Material material)
        {
            if (material == null) return;
            if (!HDShaderUtils.ResetMaterialKeywords(material))
                HDMaterial.ValidateMaterial(material);
            HdrpEditorAssetUtility.SaveAsset(material);
        }
    }
}
