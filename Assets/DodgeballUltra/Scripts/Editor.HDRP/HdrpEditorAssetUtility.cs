using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace DodgeballUltra.Editor.HDRP
{
    /// <summary>Asset-database helpers for the HDRP editor hooks (folders, in-place asset overwrite, texture import fixes).</summary>
    public static class HdrpEditorAssetUtility
    {
        /// <summary>Creates every missing folder of an "Assets/..." folder path.</summary>
        public static void EnsureFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return;
            folder = folder.Replace('\\', '/').TrimEnd('/');
            if (AssetDatabase.IsValidFolder(folder)) return;

            string parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }

        /// <summary>Creates the folder containing <paramref name="assetPath"/>.</summary>
        public static void EnsureFolderForAsset(string assetPath)
        {
            string folder = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
            EnsureFolder(folder);
        }

        /// <summary>Absolute file-system path of an "Assets/..." path.</summary>
        public static string ToAbsolutePath(string assetPath)
        {
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? string.Empty;
            return Path.Combine(projectRoot, assetPath);
        }

        /// <summary>
        /// Saves <paramref name="built"/> at <paramref name="assetPath"/>. When a material already exists there it is
        /// overwritten <b>in place</b> (same GUID) so prefabs and renderers referencing it stay linked; the temporary
        /// material is then destroyed. Returns the persistent material.
        /// </summary>
        public static Material SaveMaterial(Material built, string assetPath)
        {
            EnsureFolderForAsset(assetPath);
            string assetName = Path.GetFileNameWithoutExtension(assetPath);

            var existing = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
            if (existing != null)
            {
                EditorUtility.CopySerialized(built, existing);
                existing.name = assetName;
                Object.DestroyImmediate(built);
                EditorUtility.SetDirty(existing);
                return existing;
            }

            // Something that is not a material occupies the path: replace it.
            if (AssetDatabase.LoadMainAssetAtPath(assetPath) != null) AssetDatabase.DeleteAsset(assetPath);

            built.name = assetName;
            AssetDatabase.CreateAsset(built, assetPath);
            return built;
        }

        /// <summary>Writes a single asset to disk now when the editor supports it, else marks it for the next save.</summary>
        public static void SaveAsset(Object asset)
        {
            if (asset == null) return;
            EditorUtility.SetDirty(asset);
#if UNITY_6000_0_OR_NEWER
            AssetDatabase.SaveAssetIfDirty(asset);
#endif
        }

        /// <summary>
        /// Makes sure a texture is imported the way HDRP expects for its slot (sRGB colour, linear mask, normal map).
        /// Only reimports when a setting actually changes. Returns the (possibly reloaded) texture.
        /// </summary>
        public static Texture2D EnsureImport(Texture2D texture, bool sRGB, bool normalMap, bool alphaIsTransparency = false)
        {
            if (texture == null) return null;
            string path = AssetDatabase.GetAssetPath(texture);
            if (string.IsNullOrEmpty(path) || !(AssetImporter.GetAtPath(path) is TextureImporter importer)) return texture;

            bool changed = false;
            if (normalMap)
            {
                if (importer.textureType != TextureImporterType.NormalMap)
                {
                    importer.textureType = TextureImporterType.NormalMap;
                    changed = true;
                }
            }
            else
            {
                if (importer.textureType == TextureImporterType.NormalMap)
                {
                    importer.textureType = TextureImporterType.Default;
                    changed = true;
                }

                if (importer.sRGBTexture != sRGB)
                {
                    importer.sRGBTexture = sRGB;
                    changed = true;
                }

                if (alphaIsTransparency && importer.DoesSourceTextureHaveAlpha() && !importer.alphaIsTransparency)
                {
                    // Dilates colour into transparent texels so alpha-clipped hair has no dark fringes.
                    importer.alphaIsTransparency = true;
                    changed = true;
                }
            }

            if (!changed) return texture;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        /// <summary>True when the texture's source file has an alpha channel.</summary>
        public static bool SourceHasAlpha(Texture2D texture)
        {
            if (texture == null) return false;
            string path = AssetDatabase.GetAssetPath(texture);
            if (!string.IsNullOrEmpty(path) && AssetImporter.GetAtPath(path) is TextureImporter importer)
                return importer.DoesSourceTextureHaveAlpha();
            return GraphicsFormatUtility.HasAlphaChannel(texture.graphicsFormat);
        }

        /// <summary>
        /// Bakes <paramref name="color"/>.rgb and the opacity of <paramref name="opacity"/> (its alpha channel when it has
        /// one, else its red channel) into a new sRGB PNG at <paramref name="outputAssetPath"/>, because HDRP/Lit reads
        /// alpha-clip opacity from the base colour map's alpha. Works with compressed / non-readable sources (GPU
        /// read-back). Returns null when no GPU is available (batch mode with -nographics).
        /// </summary>
        public static Texture2D BakeColorWithOpacity(Texture2D color, Texture2D opacity, string outputAssetPath)
        {
            if (color == null || opacity == null) return null;
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                Debug.LogWarning($"[DU HDRP] Cannot bake opacity into '{outputAssetPath}' without a graphics device; hair will use the base colour alpha.");
                return null;
            }

            int width = color.width, height = color.height;
            Color32[] rgb = ReadBack(color, width, height);
            Color32[] mask = ReadBack(opacity, width, height);
            bool useAlpha = SourceHasAlpha(opacity);

            var pixels = new Color32[rgb.Length];
            for (int i = 0; i < pixels.Length; i++)
            {
                Color32 c = rgb[i];
                c.a = useAlpha ? mask[i].a : mask[i].r;
                pixels[i] = c;
            }

            var output = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
            output.SetPixels32(pixels);
            output.Apply(false, false);
            byte[] png = output.EncodeToPNG();
            Object.DestroyImmediate(output);

            EnsureFolderForAsset(outputAssetPath);
            File.WriteAllBytes(ToAbsolutePath(outputAssetPath), png);
            AssetDatabase.ImportAsset(outputAssetPath, ImportAssetOptions.ForceUpdate);

            if (AssetImporter.GetAtPath(outputAssetPath) is TextureImporter importer)
            {
                importer.textureType = TextureImporterType.Default;
                importer.sRGBTexture = true;
                importer.alphaSource = TextureImporterAlphaSource.FromInput;
                importer.alphaIsTransparency = true;
                importer.mipmapEnabled = true;
                importer.maxTextureSize = Mathf.Max(importer.maxTextureSize, Mathf.NextPowerOfTwo(Mathf.Max(width, height)));
                importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(outputAssetPath);
        }

        /// <summary>Reads a texture's raw (still-encoded) texels at a given size through a matching-colour-space RT.</summary>
        private static Color32[] ReadBack(Texture2D source, int width, int height)
        {
            // Blit into a render texture with the same colour space as the source so the sample -> write round trip
            // returns the stored bytes unchanged (sRGB decode on read, re-encode on write).
            bool sRGB = GraphicsFormatUtility.IsSRGBFormat(source.graphicsFormat);
            RenderTexture rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32,
                sRGB ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
            RenderTexture previous = RenderTexture.active;
            try
            {
                Graphics.Blit(source, rt);
                RenderTexture.active = rt;
                var readable = new Texture2D(width, height, TextureFormat.RGBA32, false, !sRGB);
                readable.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                readable.Apply(false, false);
                Color32[] texels = readable.GetPixels32();
                Object.DestroyImmediate(readable);
                return texels;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
            }
        }
    }
}
