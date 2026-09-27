using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace DodgeballUltra.Editor.Characters
{
    /// <summary>How a character texture is sampled; decides its import settings.</summary>
    public enum CharacterTextureKind
    {
        /// <summary>Opaque albedo (<c>*_color*</c>): sRGB, alpha ignored.</summary>
        Color = 0,
        /// <summary>Albedo with coverage alpha (hair/eyelash cards <c>*_opacity_color*</c>, generated <c>*_basecolor</c>): sRGB.</summary>
        ColorWithAlpha,
        /// <summary>Tangent-space normal map (<c>*_normal*</c>).</summary>
        Normal,
        /// <summary>Rocketbox specular intensity map (<c>*_specular*</c>): linear data, alpha ignored.</summary>
        Specular,
        /// <summary>Generated HDRP mask map (<c>*_mask</c>: R metal, G AO, B detail, A smoothness): linear, alpha used.</summary>
        Mask,
        /// <summary>Avatar portrait (<c>Avatars/&lt;Name&gt;/&lt;Name&gt;.png</c>): UI sprite.</summary>
        Portrait,
    }

    /// <summary>
    /// Import rules for Rocketbox textures and the maps generated from them. Applied by
    /// <see cref="RocketboxImportPostprocessor"/> on every import (so semantic settings - type, colour space, alpha - can
    /// never drift) and verified by the pipeline before building materials. Size/compression/filtering are only written
    /// on the first import so a user can still trade quality for memory per texture.
    /// </summary>
    public static class CharacterTextureRules
    {
        /// <summary>Suffix of generated mask maps.</summary>
        public const string MaskSuffix = "_mask";
        /// <summary>Suffix of generated base-colour-with-alpha maps (hair cards without an alpha channel).</summary>
        public const string BaseColorSuffix = "_basecolor";

        /// <summary>Max import size of portraits (UI).</summary>
        public const int PortraitMaxSize = 1024;

        /// <summary>Alpha-test reference used for coverage-preserving mip maps of hair cards (matches the material cutoff).</summary>
        public const float HairAlphaCutoff = 0.5f;

        /// <summary>True for a texture this module manages (downloaded Rocketbox texture or generated character map).</summary>
        public static bool IsManaged(string assetPath)
            => RocketboxAssetSet.IsRocketboxPath(assetPath) || IsGeneratedCharacterTexture(assetPath);

        /// <summary>True for a PNG generated under <see cref="CharacterPipeline.GeneratedRoot"/>.</summary>
        public static bool IsGeneratedCharacterTexture(string assetPath)
            => !string.IsNullOrEmpty(assetPath)
               && assetPath.StartsWith(CharacterPipeline.GeneratedRoot + "/", StringComparison.OrdinalIgnoreCase)
               && assetPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase);

        /// <summary>Classifies a texture by its path / Rocketbox naming convention.</summary>
        public static CharacterTextureKind Classify(string assetPath)
        {
            if (RocketboxAssetSet.IsPortraitPath(assetPath)) return CharacterTextureKind.Portrait;
            string stem = (Path.GetFileNameWithoutExtension(assetPath) ?? string.Empty).ToLowerInvariant();
            if (stem.EndsWith(MaskSuffix, StringComparison.Ordinal)) return CharacterTextureKind.Mask;
            if (stem.EndsWith(BaseColorSuffix, StringComparison.Ordinal)) return CharacterTextureKind.ColorWithAlpha;
            if (stem.Contains("_normal")) return CharacterTextureKind.Normal;
            if (stem.Contains("_specular")) return CharacterTextureKind.Specular;
            if (stem.Contains("_opacity")) return CharacterTextureKind.ColorWithAlpha;
            return CharacterTextureKind.Color;
        }

        /// <summary>
        /// Applies the rules to <paramref name="importer"/>. Returns true when something changed.
        /// <paramref name="includeQualitySettings"/> also writes size / compression / filtering (first import).
        /// </summary>
        public static bool Apply(TextureImporter importer, string assetPath, bool includeQualitySettings)
        {
            CharacterTextureKind kind = Classify(assetPath);
            bool changed = false;

            // ---- semantic settings (always enforced) ----
            TextureImporterType type = kind == CharacterTextureKind.Normal ? TextureImporterType.NormalMap
                : kind == CharacterTextureKind.Portrait ? TextureImporterType.Sprite
                : TextureImporterType.Default;
            bool srgb = kind == CharacterTextureKind.Color || kind == CharacterTextureKind.ColorWithAlpha ||
                        kind == CharacterTextureKind.Portrait;
            TextureImporterAlphaSource alpha = kind == CharacterTextureKind.ColorWithAlpha || kind == CharacterTextureKind.Mask ||
                                               kind == CharacterTextureKind.Portrait
                ? TextureImporterAlphaSource.FromInput
                : TextureImporterAlphaSource.None;
            bool alphaIsTransparency = kind == CharacterTextureKind.ColorWithAlpha || kind == CharacterTextureKind.Portrait;
            bool mips = kind != CharacterTextureKind.Portrait;

            changed |= Set(importer.textureType, type, v => importer.textureType = v);
            changed |= Set(importer.sRGBTexture, srgb, v => importer.sRGBTexture = v);
            changed |= Set(importer.alphaSource, alpha, v => importer.alphaSource = v);
            changed |= Set(importer.alphaIsTransparency, alphaIsTransparency, v => importer.alphaIsTransparency = v);
            changed |= Set(importer.mipmapEnabled, mips, v => importer.mipmapEnabled = v);
            changed |= Set(importer.isReadable, false, v => importer.isReadable = v);
            if (kind == CharacterTextureKind.ColorWithAlpha)
            {
                // Hair cards are alpha-tested: keep their coverage in distant mips so hair does not thin out / vanish.
                changed |= Set(importer.mipMapsPreserveCoverage, true, v => importer.mipMapsPreserveCoverage = v);
                changed |= Set(importer.alphaTestReferenceValue, HairAlphaCutoff, v => importer.alphaTestReferenceValue = v);
            }
            if (kind == CharacterTextureKind.Portrait)
                changed |= Set(importer.spriteImportMode, SpriteImportMode.Single, v => importer.spriteImportMode = v);

            // ---- quality settings (first import only, user-tweakable afterwards) ----
            if (includeQualitySettings)
            {
                CharacterPipelineSettings settings = CharacterPipelineSettings.instance;
                int maxSize = kind == CharacterTextureKind.Portrait ? PortraitMaxSize : settings.TextureSizePowerOfTwo;
                changed |= Set(importer.maxTextureSize, maxSize, v => importer.maxTextureSize = v);
                changed |= Set(importer.textureCompression, TextureImporterCompression.CompressedHQ, v => importer.textureCompression = v);
                changed |= Set(importer.filterMode, FilterMode.Trilinear, v => importer.filterMode = v);
                changed |= Set(importer.wrapMode, kind == CharacterTextureKind.Portrait ? TextureWrapMode.Clamp : TextureWrapMode.Repeat,
                    v => importer.wrapMode = v);
                if (kind != CharacterTextureKind.Portrait)
                {
                    changed |= Set(importer.anisoLevel, settings.anisoLevel, v => importer.anisoLevel = v);
                    changed |= Set(importer.streamingMipmaps, true, v => importer.streamingMipmaps = v);
                }
            }
            return changed;
        }

        /// <summary>
        /// Verifies the import settings of an existing texture asset and fixes them (SaveAndReimport, deferred when inside
        /// <see cref="AssetDatabase.StartAssetEditing"/>). Returns true when a re-import was requested.
        /// </summary>
        public static bool Ensure(string assetPath)
        {
            if (!(AssetImporter.GetAtPath(assetPath) is TextureImporter importer)) return false;
            if (!Apply(importer, assetPath, false)) return false;
            importer.SaveAndReimport();
            return true;
        }

        private static bool Set<T>(T current, T wanted, Action<T> assign)
        {
            if (Equals(current, wanted)) return false;
            assign(wanted);
            return true;
        }
    }
}
