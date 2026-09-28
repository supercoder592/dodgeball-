using System;
using System.Collections.Generic;
using System.IO;
using DodgeballUltra.Editor.Pipeline;
using UnityEditor;
using UnityEngine;

namespace DodgeballUltra.Editor.Characters
{
    /// <summary>
    /// Physically based materials for a Rocketbox avatar, one per FBX material slot (<c>&lt;id&gt;_body</c>,
    /// <c>&lt;id&gt;_head</c>, <c>&lt;id&gt;_opacity</c>, <c>&lt;id&gt;_helmet</c> ...):
    /// <list type="number">
    /// <item>Textures are grouped by slot from the Rocketbox naming convention
    /// <c>&lt;id&gt;_&lt;part&gt;_&lt;color|normal|specular&gt;[_&lt;variant&gt;].tga</c> (e.g. <c>sm002_body_color_acu.tga</c>,
    /// <c>m111_tools_normal.tga</c>, <c>sm002_combat_knife_specular.tga</c>); the un-suffixed variant wins.</item>
    /// <item>A linear HDRP-layout mask map (R metallic 0, G occlusion 1, B detail 0, <b>A smoothness</b>) is generated from the
    /// 3ds Max specular-level map: robust percentiles -> normalised -> gamma -> remapped into the per-surface smoothness
    /// range of <see cref="CharacterPipelineSettings"/> (skin, clothing, hair, props).</item>
    /// <item>Hair / eyelash cards (<c>*_opacity*</c> parts) get a base colour PNG whose alpha is the coverage mask
    /// (imported with coverage-preserving mips and colour dilation).</item>
    /// <item><see cref="EditorRenderingHooks.Active"/> creates the material for the active render pipeline
    /// (HDRP Lit with SSS for skin / alpha-clipped double-sided hair, or the Built-in Standard fallback).</item>
    /// </list>
    /// Everything is written to <c>Assets/DodgeballUltra/Generated/Characters/&lt;Avatar&gt;/</c>. Generated PNGs are only
    /// rewritten (and re-imported) when their content changes, so rebuilding is cheap.
    /// </summary>
    public static class CharacterMaterialBuilder
    {
        private const string MapColor = "color";
        private const string MapNormal = "normal";
        private const string MapSpecular = "specular";

        /// <summary>Portrait output size (square, pixels).</summary>
        public const int PortraitSize = 512;

        /// <summary>Suffix of generated portrait crops (imported as UI sprites by <see cref="CharacterTextureRules"/>).</summary>
        public const string PortraitSuffix = "_portrait";

        /// <summary>Textures of one material slot.</summary>
        public sealed class SlotTextures
        {
            /// <summary>Texture set id, e.g. "m301" / "sm002".</summary>
            public string Id;
            /// <summary>Part, e.g. "body", "head", "opacity", "combat_knife".</summary>
            public string Part;
            /// <summary>Asset paths by map ("color" / "normal" / "specular").</summary>
            public readonly Dictionary<string, string> Maps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            /// <summary>Variant of each chosen map ("" = the default texture).</summary>
            public readonly Dictionary<string, string> Variants = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            /// <summary>FBX material slot name (<c>&lt;id&gt;_&lt;part&gt;</c>).</summary>
            public string SlotName => string.IsNullOrEmpty(Part) ? Id : Id + "_" + Part;

            /// <summary>Surface type of the slot.</summary>
            public CharacterMaterialKind Kind => ClassifyPart(Part);

            public string Get(string map) => Maps.TryGetValue(map, out string p) ? p : null;
        }

        // ====================================================================================================== API

        /// <summary>
        /// Builds (or refreshes) every material of <paramref name="avatar"/>. Returns slot name -> material
        /// (case-insensitive); problems are appended to <paramref name="log"/>.
        /// </summary>
        public static Dictionary<string, Material> BuildMaterials(string avatar, List<string> log)
        {
            var result = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);
            List<SlotTextures> slots = FindSlotTextures(avatar);
            if (slots.Count == 0)
            {
                log?.Add($"{avatar}: no textures found in {RocketboxAssetSet.AvatarTexturesFolder(avatar)} (materials keep the FBX defaults).");
                return result;
            }

            CharacterPipelineSettings settings = CharacterPipelineSettings.Instance;
            string folder = RocketboxAssetSet.GeneratedAvatarFolder(avatar);
            TextureAssetUtility.EnsureAssetFolder(folder);

            // ---- 1. generate derived maps (file IO only, no imports yet) --------------------------------------------
            var maskPaths = new Dictionary<SlotTextures, string>();
            var baseColorPaths = new Dictionary<SlotTextures, string>();
            var toImport = new List<string>();
            foreach (SlotTextures slot in slots)
            {
                string maskPath = folder + "/" + RocketboxAssetSet.SanitizeFileName(slot.SlotName) + CharacterTextureRules.MaskSuffix + ".png";
                if (TryWriteMaskMap(slot, maskPath, settings, log, out bool maskWritten))
                {
                    maskPaths[slot] = maskPath;
                    if (maskWritten) toImport.Add(maskPath);
                }

                if (slot.Kind == CharacterMaterialKind.Hair && slot.Get(MapColor) != null)
                {
                    string basePath = folder + "/" + RocketboxAssetSet.SanitizeFileName(slot.SlotName) + CharacterTextureRules.BaseColorSuffix + ".png";
                    if (TryWriteBaseColorWithAlpha(slot, basePath, settings, log, out bool baseWritten))
                    {
                        baseColorPaths[slot] = basePath;
                        if (baseWritten) toImport.Add(basePath);
                    }
                }
            }

            // ---- 2. import new maps and enforce import rules on the sources (one batched import) -------------------
            using (new AssetEditingScope())
            {
                foreach (string path in toImport) AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                foreach (SlotTextures slot in slots)
                {
                    foreach (string source in slot.Maps.Values) CharacterTextureRules.Ensure(source);
                    if (maskPaths.TryGetValue(slot, out string m) && !toImport.Contains(m)) CharacterTextureRules.Ensure(m);
                    if (baseColorPaths.TryGetValue(slot, out string b) && !toImport.Contains(b)) CharacterTextureRules.Ensure(b);
                }
            }

            // ---- 3. materials ---------------------------------------------------------------------------------------
            foreach (SlotTextures slot in slots)
            {
                CharacterMaterialKind kind = slot.Kind;
                Texture2D color = Load(slot.Get(MapColor));
                Texture2D normal = Load(slot.Get(MapNormal));
                Texture2D mask = maskPaths.TryGetValue(slot, out string maskPath) ? Load(maskPath) : null;
                Texture2D opacity = null;
                if (kind == CharacterMaterialKind.Hair)
                {
                    // Coverage lives in the base colour alpha: hand the same texture as opacity so no pipeline re-bakes it.
                    Texture2D combined = baseColorPaths.TryGetValue(slot, out string basePath) ? Load(basePath) : null;
                    if (combined != null) color = combined;
                    opacity = color;
                }
                if (color == null && normal == null)
                {
                    log?.Add($"{avatar}: slot '{slot.SlotName}' has no colour or normal map - skipped.");
                    continue;
                }

                string materialPath = folder + "/" + RocketboxAssetSet.SanitizeFileName(slot.SlotName) + ".mat";
                Material material = CreateMaterial(materialPath, kind, color, normal, mask, opacity, log);
                if (material != null) result[slot.SlotName] = material;
            }
            return result;
        }

        /// <summary>
        /// Head-and-shoulders portrait sprite cropped from the Rocketbox full-body render (<c>&lt;Name&gt;.png</c>, black
        /// background). Falls back to the uncropped portrait, or null when the avatar ships none.
        /// </summary>
        public static Sprite BuildPortrait(string avatar, RocketboxAvatarEntry entry, List<string> log)
        {
            string source = RocketboxAssetSet.PortraitAssetPath(avatar, entry);
            if (!RocketboxAssetSet.FileExistsNonEmpty(source)) return null;

            string folder = RocketboxAssetSet.GeneratedAvatarFolder(avatar);
            string target = folder + "/" + RocketboxAssetSet.SanitizeFileName(avatar) + PortraitSuffix + ".png";
            try
            {
                if (!TextureFileReader.TryRead(source, out DecodedImage image, out string error))
                {
                    log?.Add($"{avatar}: portrait not readable ({error}); using the full render.");
                    return LoadSprite(source);
                }

                DecodedImage crop = CropHeadAndShoulders(image, PortraitSize);
                byte[] png = TextureFileReader.EncodePng(crop.Width, crop.Height, crop.Pixels);
                TextureAssetUtility.EnsureAssetFolder(folder);
                if (WriteIfChanged(target, png)) AssetDatabase.ImportAsset(target, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                else CharacterTextureRules.Ensure(target);
                return LoadSprite(target) ?? LoadSprite(source);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException)
            {
                log?.Add($"{avatar}: portrait crop failed ({e.Message}); using the full render.");
                return LoadSprite(source);
            }
        }

        // ================================================================================================ textures

        /// <summary>Groups the avatar's downloaded textures by material slot (see class docs).</summary>
        public static List<SlotTextures> FindSlotTextures(string avatar)
        {
            var bySlot = new Dictionary<string, SlotTextures>(StringComparer.OrdinalIgnoreCase);
            string texturesFolder = RocketboxAssetSet.AvatarTexturesFolder(avatar);
            string absolute = RocketboxAssetSet.ToAbsolutePath(texturesFolder);
            if (!Directory.Exists(absolute)) return new List<SlotTextures>();

            string[] files = Directory.GetFiles(absolute);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (string file in files)
            {
                string name = Path.GetFileName(file);
                if (!RocketboxAssetSet.IsWantedTexture("Textures/" + name)) continue;
                if (!TryParseTextureName(Path.GetFileNameWithoutExtension(name), out string id, out string part, out string map, out string variant))
                    continue;

                string key = string.IsNullOrEmpty(part) ? id : id + "_" + part;
                if (!bySlot.TryGetValue(key, out SlotTextures slot))
                {
                    slot = new SlotTextures { Id = id, Part = part };
                    bySlot.Add(key, slot);
                }

                string assetPath = texturesFolder + "/" + name;
                // The default texture (no variant suffix) wins; otherwise the alphabetically first variant.
                if (!slot.Variants.TryGetValue(map, out string existingVariant) ||
                    (existingVariant.Length > 0 && (variant.Length == 0 || string.CompareOrdinal(variant, existingVariant) < 0)))
                {
                    slot.Maps[map] = assetPath;
                    slot.Variants[map] = variant;
                }
            }

            var list = new List<SlotTextures>(bySlot.Values);
            list.Sort((a, b) => string.Compare(a.SlotName, b.SlotName, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        /// <summary>
        /// Parses <c>&lt;id&gt;_&lt;part&gt;_&lt;map&gt;[_&lt;variant&gt;]</c> (map = color / normal / specular). The part may
        /// contain underscores (<c>combat_knife</c>, <c>glasses_opacity</c>) or be empty (<c>f204_color</c>).
        /// </summary>
        public static bool TryParseTextureName(string stem, out string id, out string part, out string map, out string variant)
        {
            id = part = map = variant = null;
            if (string.IsNullOrEmpty(stem)) return false;
            string[] tokens = stem.Split('_');
            for (int i = 1; i < tokens.Length; i++)
            {
                string t = tokens[i].ToLowerInvariant();
                if (t != MapColor && t != MapNormal && t != MapSpecular) continue;
                id = tokens[0];
                part = string.Join("_", tokens, 1, i - 1).ToLowerInvariant();
                map = t;
                variant = i + 1 < tokens.Length ? string.Join("_", tokens, i + 1, tokens.Length - i - 1).ToLowerInvariant() : string.Empty;
                return !string.IsNullOrEmpty(id);
            }
            return false;
        }

        /// <summary>Surface type of a Rocketbox part: head = skin, *opacity* = hair cards, body = clothing + skin, else props.</summary>
        public static CharacterMaterialKind ClassifyPart(string part)
        {
            string p = (part ?? string.Empty).ToLowerInvariant();
            if (p == "head") return CharacterMaterialKind.Skin;
            if (p.Contains("opacity")) return CharacterMaterialKind.Hair;
            if (p == "body" || p.Length == 0) return CharacterMaterialKind.Body;
            return CharacterMaterialKind.Generic;
        }

        // ============================================================================================== mask maps

        private static bool TryWriteMaskMap(SlotTextures slot, string maskPath, CharacterPipelineSettings settings, List<string> log,
            out bool written)
        {
            written = false;
            CharacterMaterialKind kind = slot.Kind;
            string specular = slot.Get(MapSpecular);
            byte[] png;
            if (specular != null)
            {
                if (!TextureFileReader.TryRead(specular, out DecodedImage spec, out string error))
                {
                    log?.Add($"{slot.SlotName}: specular map not readable ({error}); default smoothness used.");
                    return false;
                }
                spec = TextureFileReader.DownscaleToFit(spec, settings.TextureSizePowerOfTwo);
                Color32[] mask = BuildMaskPixels(spec, SmoothnessRange(kind, settings), settings);
                png = TextureFileReader.EncodePng(spec.Width, spec.Height, mask);
            }
            else if (kind == CharacterMaterialKind.Hair)
            {
                // No opacity specular map: a constant hair smoothness keeps the sheen consistent across pipelines.
                const int size = 4;
                byte a = ToByte(settings.hairFallbackSmoothness);
                var px = new Color32[size * size];
                for (int i = 0; i < px.Length; i++) px[i] = new Color32(0, 255, 0, a);
                png = TextureFileReader.EncodePng(size, size, px);
            }
            else
            {
                return false; // the pipeline's per-kind default smoothness applies
            }

            try
            {
                written = WriteIfChanged(maskPath, png);
                return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                log?.Add($"{slot.SlotName}: could not write {maskPath}: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Mask pixels from a specular-level image: A = smoothness in <paramref name="range"/>, R = 0 (dielectric),
        /// G = 1 (no baked occlusion), B = 0 (no detail).
        /// </summary>
        public static Color32[] BuildMaskPixels(DecodedImage specular, Vector2 range, CharacterPipelineSettings settings)
        {
            int count = specular.Pixels.Length;
            var histogram = new int[256];
            var luma = new byte[count];
            for (int i = 0; i < count; i++)
            {
                int l = Mathf.Clamp(TextureFileReader.Luma(specular.Pixels[i]), 0, 255);
                luma[i] = (byte)l;
                histogram[l]++;
            }

            int lo = Percentile(histogram, count, settings.specularLowPercentile);
            int hi = Percentile(histogram, count, settings.specularHighPercentile);
            bool flat = hi - lo < 2;
            float gamma = Mathf.Max(0.01f, settings.smoothnessGamma);

            var lut = new byte[256];
            for (int v = 0; v < 256; v++)
            {
                float t = flat ? 0.5f : Mathf.Clamp01((v - lo) / (float)(hi - lo));
                t = Mathf.Pow(t, gamma);
                lut[v] = ToByte(Mathf.Lerp(range.x, range.y, t));
            }

            var mask = new Color32[count];
            for (int i = 0; i < count; i++) mask[i] = new Color32(0, 255, 0, lut[luma[i]]);
            return mask;
        }

        private static int Percentile(int[] histogram, int total, float percent)
        {
            long target = (long)Math.Round(total * Mathf.Clamp(percent, 0f, 100f) / 100.0);
            long cumulative = 0;
            for (int v = 0; v < histogram.Length; v++)
            {
                cumulative += histogram[v];
                if (cumulative >= target && cumulative > 0) return v;
            }
            return histogram.Length - 1;
        }

        private static Vector2 SmoothnessRange(CharacterMaterialKind kind, CharacterPipelineSettings settings)
        {
            switch (kind)
            {
                case CharacterMaterialKind.Skin: return settings.skinSmoothness;
                case CharacterMaterialKind.Body: return settings.bodySmoothness;
                case CharacterMaterialKind.Hair: return settings.hairSmoothness;
                default: return settings.genericSmoothness;
            }
        }

        // ================================================================================================ hair cards

        private static bool TryWriteBaseColorWithAlpha(SlotTextures slot, string targetPath, CharacterPipelineSettings settings,
            List<string> log, out bool written)
        {
            written = false;
            string colorPath = slot.Get(MapColor);
            if (!TextureFileReader.TryRead(colorPath, out DecodedImage color, out string error))
            {
                log?.Add($"{slot.SlotName}: hair texture not readable ({error}); using it as imported.");
                return false;
            }
            if (!color.HasAlpha)
            {
                log?.Add($"{slot.SlotName}: '{Path.GetFileName(colorPath)}' has no alpha channel - hair cards will render opaque.");
            }

            color = TextureFileReader.DownscaleToFit(color, settings.TextureSizePowerOfTwo);
            Color32[] px = color.Pixels;
            if (!color.HasAlpha)
            {
                px = (Color32[])px.Clone();
                for (int i = 0; i < px.Length; i++) px[i].a = 255;
            }

            try
            {
                written = WriteIfChanged(targetPath, TextureFileReader.EncodePng(color.Width, color.Height, px));
                return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                log?.Add($"{slot.SlotName}: could not write {targetPath}: {e.Message}");
                return false;
            }
        }

        // ================================================================================================= portrait

        /// <summary>
        /// Square crop of the head and shoulders from a full-body render on a dark background (same framing as the web
        /// build: 30 % of the figure height, starting just above the head), resampled to <paramref name="size"/>.
        /// </summary>
        public static DecodedImage CropHeadAndShoulders(DecodedImage src, int size)
        {
            int w = src.Width, h = src.Height;
            int minX = w, maxX = -1, minY = h, maxY = -1;
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    if (TextureFileReader.Luma(src.Pixels[row + x]) <= 12) continue;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
            if (maxX < 0) // empty image: centre crop
            {
                minX = 0;
                maxX = w - 1;
                minY = 0;
                maxY = h - 1;
            }

            int figureHeight = Math.Max(1, maxY - minY + 1);
            int side = Mathf.Clamp(Mathf.RoundToInt(figureHeight * 0.30f), 8, Math.Min(w, h));
            int centerX = (minX + maxX) / 2;
            // Rows are bottom-to-top: the top of the head is maxY.
            int top = Math.Min(h, maxY + 1 + Mathf.RoundToInt(figureHeight * 0.02f));
            int x0 = Mathf.Clamp(centerX - side / 2, 0, w - side);
            int y0 = Mathf.Clamp(top - side, 0, h - side);

            var dst = new Color32[size * size];
            float scale = side / (float)size;
            for (int y = 0; y < size; y++)
            {
                float sy = y0 + (y + 0.5f) * scale - 0.5f;
                for (int x = 0; x < size; x++)
                {
                    float sx = x0 + (x + 0.5f) * scale - 0.5f;
                    dst[y * size + x] = SampleBilinear(src, sx, sy);
                }
            }
            return new DecodedImage { Width = size, Height = size, Pixels = dst, HasAlpha = false };
        }

        private static Color32 SampleBilinear(DecodedImage img, float x, float y)
        {
            int x0 = Mathf.Clamp(Mathf.FloorToInt(x), 0, img.Width - 1), y0 = Mathf.Clamp(Mathf.FloorToInt(y), 0, img.Height - 1);
            int x1 = Math.Min(x0 + 1, img.Width - 1), y1 = Math.Min(y0 + 1, img.Height - 1);
            float fx = Mathf.Clamp01(x - x0), fy = Mathf.Clamp01(y - y0);
            Color32 a = img.Pixels[y0 * img.Width + x0], b = img.Pixels[y0 * img.Width + x1];
            Color32 c = img.Pixels[y1 * img.Width + x0], d = img.Pixels[y1 * img.Width + x1];
            Color32 top = Color32.Lerp(a, b, fx), bottom = Color32.Lerp(c, d, fx);
            Color32 r = Color32.Lerp(top, bottom, fy);
            r.a = 255;
            return r;
        }

        // =================================================================================================== helpers

        private static Material CreateMaterial(string path, CharacterMaterialKind kind, Texture2D color, Texture2D normal, Texture2D mask,
            Texture2D opacity, List<string> log)
        {
            IEditorRenderingHooks hooks = EditorRenderingHooks.Active;
            Material material = null;
            try
            {
                material = hooks.CreateCharacterMaterial(path, kind, color, normal, mask, opacity);
            }
            catch (Exception e)
            {
                log?.Add($"{Path.GetFileNameWithoutExtension(path)}: {hooks.PipelineName} material failed ({e.Message}).");
            }

            if (material == null && hooks != EditorRenderingHooks.Fallback)
            {
                // e.g. HDRP hooks registered but its Lit shader is unavailable: the Standard material still looks right.
                material = EditorRenderingHooks.Fallback.CreateCharacterMaterial(path, kind, color, normal, mask, opacity);
                if (material != null) log?.Add($"{Path.GetFileNameWithoutExtension(path)}: created with the Built-in fallback shader.");
            }
            if (material != null) EditorUtility.SetDirty(material);
            else log?.Add($"{Path.GetFileNameWithoutExtension(path)}: material could not be created.");
            return material;
        }

        /// <summary>Writes <paramref name="bytes"/> to the asset path unless the file already holds exactly them.</summary>
        private static bool WriteIfChanged(string assetPath, byte[] bytes)
        {
            string absolute = RocketboxAssetSet.ToAbsolutePath(assetPath);
            if (File.Exists(absolute))
            {
                var info = new FileInfo(absolute);
                if (info.Length == bytes.Length && BytesEqual(File.ReadAllBytes(absolute), bytes)) return false;
            }
            string directory = Path.GetDirectoryName(absolute);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllBytes(absolute, bytes);
            return true;
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        private static Texture2D Load(string assetPath)
            => string.IsNullOrEmpty(assetPath) ? null : AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);

        private static Sprite LoadSprite(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return null;
            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
            if (sprite != null) return sprite;
            foreach (UnityEngine.Object o in AssetDatabase.LoadAllAssetsAtPath(assetPath))
                if (o is Sprite s) return s;
            return null;
        }

        private static byte ToByte(float v) => (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(v) * 255f), 0, 255);
    }
}
