using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace DodgeballUltra.Editor.Pipeline
{
    /// <summary>How a texture is sampled by a physically based shader; drives its import settings.</summary>
    public enum TextureRole
    {
        /// <summary>Albedo / base colour: sRGB, opaque (alpha ignored).</summary>
        Color = 0,
        /// <summary>Albedo with a coverage mask in alpha (hair cards, eyelashes): sRGB, coverage-preserving mips.</summary>
        ColorWithAlpha,
        /// <summary>Tangent-space normal map (imported as Normal Map, BC5).</summary>
        Normal,
        /// <summary>Packed linear data (HDRP mask: R metallic, G AO, B detail, A smoothness). Never sRGB.</summary>
        Mask,
    }

    /// <summary>
    /// Editor helpers to turn generated / combined textures into correctly imported PNG assets, and to read back
    /// pixels of any texture (including non-readable or compressed imports) through the GPU.
    /// <para>
    /// Import settings are applied during the <em>first</em> import through <see cref="TextureRoleImportPostprocessor"/>, so each
    /// PNG is imported exactly once with the right colour space (sRGB for base colour, linear for normal and mask data).
    /// </para>
    /// </summary>
    public static class TextureAssetUtility
    {
        /// <summary>Pending import settings keyed by asset path (consumed by the postprocessor).</summary>
        internal static readonly Dictionary<string, TextureImportRequest> PendingImports = new Dictionary<string, TextureImportRequest>();

        /// <summary>Absolute file-system path of a project-relative asset path ("Assets/...").</summary>
        public static string ToAbsolutePath(string assetPath)
        {
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            return Path.Combine(projectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));
        }

        /// <summary>Creates every missing folder of a project-relative folder path ("Assets/A/B/C").</summary>
        public static void EnsureAssetFolder(string assetFolder)
        {
            if (string.IsNullOrEmpty(assetFolder)) return;
            assetFolder = assetFolder.Replace('\\', '/').TrimEnd('/');
            if (AssetDatabase.IsValidFolder(assetFolder)) return;

            int slash = assetFolder.LastIndexOf('/');
            if (slash <= 0) return; // "Assets" itself always exists
            string parent = assetFolder.Substring(0, slash);
            string leaf = assetFolder.Substring(slash + 1);
            EnsureAssetFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }

        /// <summary>
        /// Encodes <paramref name="texture"/> to PNG at <paramref name="assetPath"/> and imports it for <paramref name="role"/>.
        /// Returns the imported asset (never the in-memory source). Existing files are overwritten in place (GUID kept).
        /// </summary>
        /// <param name="texture">Source texture (readable or not, any uncompressed or compressed format).</param>
        /// <param name="assetPath">Project-relative path ending in .png.</param>
        /// <param name="role">Colour space / importer type.</param>
        /// <param name="maxSize">Import max size (power of two).</param>
        /// <param name="anisoLevel">Anisotropic filtering level (floors seen at grazing angles benefit from 8+).</param>
        /// <param name="alphaCutoff">Alpha-test reference used to preserve coverage in mips (<see cref="TextureRole.ColorWithAlpha"/>).</param>
        public static Texture2D SavePng(Texture2D texture, string assetPath, TextureRole role, int maxSize = 2048, int anisoLevel = 4,
            float alphaCutoff = 0.5f)
        {
            if (texture == null) throw new ArgumentNullException(nameof(texture));
            bool linear = role == TextureRole.Normal || role == TextureRole.Mask;
            byte[] png = EncodePng(texture, linear);
            return WritePngAsset(png, assetPath, role, maxSize, anisoLevel, alphaCutoff);
        }

        /// <summary>Writes already-encoded PNG bytes as an asset imported for <paramref name="role"/>.</summary>
        public static Texture2D WritePngAsset(byte[] png, string assetPath, TextureRole role, int maxSize = 2048, int anisoLevel = 4,
            float alphaCutoff = 0.5f)
        {
            EnsureAssetFolder(Path.GetDirectoryName(assetPath)?.Replace('\\', '/'));
            File.WriteAllBytes(ToAbsolutePath(assetPath), png);

            var request = new TextureImportRequest
            {
                Role = role,
                MaxSize = Mathf.ClosestPowerOfTwo(Mathf.Clamp(maxSize, 32, 8192)),
                AnisoLevel = Mathf.Clamp(anisoLevel, 0, 16),
                AlphaCutoff = Mathf.Clamp01(alphaCutoff),
            };

            if (AssetImporter.GetAtPath(assetPath) is TextureImporter existing)
            {
                // Re-import of an existing file (GUID kept): one import that picks up both the new pixels and, if they
                // changed, the new settings.
                if (!request.Matches(existing))
                {
                    request.Apply(existing);
                    existing.SaveAndReimport();
                }
                else
                {
                    AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                }
            }
            else
            {
                // First import: the postprocessor applies the settings before Unity processes the pixels (single import).
                PendingImports[assetPath] = request;
                try
                {
                    AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                }
                finally
                {
                    PendingImports.Remove(assetPath);
                }

                // Safety net: if the postprocessor did not run (e.g. asset database paused), enforce the settings now.
                if (AssetImporter.GetAtPath(assetPath) is TextureImporter imported && !request.Matches(imported))
                {
                    request.Apply(imported);
                    imported.SaveAndReimport();
                }
            }

            return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
        }

        /// <summary>Applies the role's import settings to an existing texture asset (reimports only when something changed).</summary>
        public static void ApplyImportSettings(string assetPath, TextureRole role, int maxSize = 2048, int anisoLevel = 4, float alphaCutoff = 0.5f)
        {
            if (!(AssetImporter.GetAtPath(assetPath) is TextureImporter importer)) return;
            var request = new TextureImportRequest { Role = role, MaxSize = maxSize, AnisoLevel = anisoLevel, AlphaCutoff = alphaCutoff };
            if (request.Matches(importer)) return;
            request.Apply(importer);
            importer.SaveAndReimport();
        }

        /// <summary>PNG bytes of any texture. Linear textures are read back without colour conversion.</summary>
        public static byte[] EncodePng(Texture2D texture, bool linear)
        {
            // Fast path: readable, uncompressed 8-bit textures encode directly.
            if (texture.isReadable && IsPngEncodable(texture.format))
            {
                try { return texture.EncodeToPNG(); }
                catch (Exception) { /* fall back to a converted copy below */ }
            }

            Texture2D copy = CreateReadableCopy(texture, linear);
            try { return copy.EncodeToPNG(); }
            finally { UnityEngine.Object.DestroyImmediate(copy); }
        }

        /// <summary>
        /// A readable RGBA32 copy of <paramref name="texture"/> (caller destroys it). Uses the CPU when the source is readable,
        /// otherwise a GPU blit + ReadPixels whose colour conversion matches the texture's own colour space so bytes are preserved.
        /// </summary>
        public static Texture2D CreateReadableCopy(Texture2D texture, bool linear)
        {
            int w = texture.width, h = texture.height;
            var copy = new Texture2D(w, h, TextureFormat.RGBA32, false, linear) { name = texture.name + " (Readable)" };

            if (texture.isReadable)
            {
                try
                {
                    copy.SetPixels32(texture.GetPixels32(0));
                    copy.Apply(false, false);
                    return copy;
                }
                catch (Exception)
                {
                    // Some formats cannot be decoded on the CPU: use the GPU path.
                }
            }

            bool sourceIsSrgb = GraphicsFormatUtility.IsSRGBFormat(texture.graphicsFormat);
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32,
                sourceIsSrgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
            RenderTexture previous = RenderTexture.active;
            try
            {
                Graphics.Blit(texture, rt);
                RenderTexture.active = rt;
                copy.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                copy.Apply(false, false);
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
            }
            return copy;
        }

        /// <summary>True if the importer reports an alpha channel in the source file (falls back to the texture format).</summary>
        public static bool HasAlpha(Texture2D texture)
        {
            if (texture == null) return false;
            string path = AssetDatabase.GetAssetPath(texture);
            if (!string.IsNullOrEmpty(path) && AssetImporter.GetAtPath(path) is TextureImporter importer)
                return importer.DoesSourceTextureHaveAlpha();
            return GraphicsFormatUtility.HasAlphaChannel(texture.graphicsFormat);
        }

        private static bool IsPngEncodable(TextureFormat format)
        {
            switch (format)
            {
                case TextureFormat.RGBA32:
                case TextureFormat.ARGB32:
                case TextureFormat.BGRA32:
                case TextureFormat.RGB24:
                case TextureFormat.R8:
                case TextureFormat.Alpha8:
                    return true;
                default:
                    return false;
            }
        }
    }

    /// <summary>Import settings for one generated texture.</summary>
    internal struct TextureImportRequest
    {
        public TextureRole Role;
        public int MaxSize;
        public int AnisoLevel;
        public float AlphaCutoff;

        private bool IsLinear => Role == TextureRole.Normal || Role == TextureRole.Mask;
        private bool KeepsAlpha => Role == TextureRole.ColorWithAlpha || Role == TextureRole.Mask;

        public void Apply(TextureImporter importer)
        {
            importer.textureType = Role == TextureRole.Normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
            importer.textureShape = TextureImporterShape.Texture2D;
            importer.sRGBTexture = !IsLinear;
            importer.alphaSource = KeepsAlpha ? TextureImporterAlphaSource.FromInput : TextureImporterAlphaSource.None;
            importer.alphaIsTransparency = Role == TextureRole.ColorWithAlpha;
            importer.mipmapEnabled = true;
            importer.mipMapsPreserveCoverage = Role == TextureRole.ColorWithAlpha;
            importer.alphaTestReferenceValue = AlphaCutoff;
            importer.streamingMipmaps = true;
            importer.wrapMode = TextureWrapMode.Repeat;
            importer.filterMode = FilterMode.Trilinear;
            importer.anisoLevel = AnisoLevel;
            importer.maxTextureSize = MaxSize;
            importer.textureCompression = TextureImporterCompression.CompressedHQ; // BC7 / BC5 on desktop
            importer.isReadable = false;
        }

        public bool Matches(TextureImporter importer)
        {
            var expectedType = Role == TextureRole.Normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
            return importer.textureType == expectedType
                   && importer.sRGBTexture == !IsLinear
                   && importer.alphaSource == (KeepsAlpha ? TextureImporterAlphaSource.FromInput : TextureImporterAlphaSource.None)
                   && importer.alphaIsTransparency == (Role == TextureRole.ColorWithAlpha)
                   && importer.mipmapEnabled
                   && importer.anisoLevel == AnisoLevel
                   && importer.maxTextureSize == MaxSize
                   && importer.textureCompression == TextureImporterCompression.CompressedHQ;
        }
    }

    /// <summary>Applies <see cref="TextureAssetUtility"/> import requests during the first import of generated PNGs.</summary>
    internal sealed class TextureRoleImportPostprocessor : AssetPostprocessor
    {
        private void OnPreprocessTexture()
        {
            if (TextureAssetUtility.PendingImports.Count == 0) return;
            if (!TextureAssetUtility.PendingImports.TryGetValue(assetPath, out TextureImportRequest request)) return;
            if (assetImporter is TextureImporter importer) request.Apply(importer);
        }
    }
}
