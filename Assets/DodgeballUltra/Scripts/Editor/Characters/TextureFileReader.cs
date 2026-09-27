using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DodgeballUltra.Editor.Characters
{
    /// <summary>Decoded 8-bit RGBA image, rows bottom-to-top (Unity's texture convention).</summary>
    public sealed class DecodedImage
    {
        public int Width;
        public int Height;
        /// <summary>Width * Height pixels, index = y * Width + x with y = 0 at the bottom.</summary>
        public Color32[] Pixels;
        /// <summary>True when the source file carries a real alpha channel.</summary>
        public bool HasAlpha;
    }

    /// <summary>
    /// Reads source texture files at full precision without touching their import settings:
    /// <list type="bullet">
    /// <item>TGA (the Rocketbox format) is decoded here: uncompressed / RLE, true-colour / grey, 8/16/24/32 bpp, any origin;</item>
    /// <item>PNG / JPG go through <c>ImageConversion.LoadImage</c>;</item>
    /// <item>anything else is read back from the imported asset on the GPU (blit + ReadPixels).</item>
    /// </list>
    /// Reading the source file (instead of toggling <c>TextureImporter.isReadable</c> and re-importing 12 MB textures twice)
    /// keeps the pipeline fast, exact (no block-compression artefacts in generated masks) and batch-mode friendly.
    /// </summary>
    public static class TextureFileReader
    {
        /// <summary>Decodes the image at <paramref name="assetPath"/>. Returns false with an error message on failure.</summary>
        public static bool TryRead(string assetPath, out DecodedImage image, out string error)
        {
            image = null;
            error = null;
            string absolute = RocketboxAssetSet.ToAbsolutePath(assetPath);
            try
            {
                if (!File.Exists(absolute))
                {
                    error = $"file not found: {assetPath}";
                    return false;
                }

                string ext = Path.GetExtension(assetPath).ToLowerInvariant();
                switch (ext)
                {
                    case ".tga":
                        image = DecodeTga(File.ReadAllBytes(absolute));
                        return true;
                    case ".png":
                    case ".jpg":
                    case ".jpeg":
                        image = DecodeWithImageConversion(File.ReadAllBytes(absolute), ext != ".png" ? false : (bool?)null);
                        return true;
                    default:
                        image = ReadBackImportedTexture(assetPath);
                        if (image == null) error = $"unsupported or unimported texture: {assetPath}";
                        return image != null;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is FormatException || ex is UnauthorizedAccessException ||
                                       ex is IndexOutOfRangeException || ex is ArgumentException)
            {
                error = $"{assetPath}: {ex.Message}";
                image = null;
                return false;
            }
        }

        // ------------------------------------------------------------------------------------------------------ TGA

        /// <summary>Decodes a Truevision TGA file (types 2, 3, 10, 11).</summary>
        public static DecodedImage DecodeTga(byte[] data)
        {
            if (data == null || data.Length < 18) throw new FormatException("TGA: file too small");
            int idLength = data[0];
            int colorMapType = data[1];
            int imageType = data[2];
            int colorMapLength = data[5] | (data[6] << 8);
            int colorMapEntryBits = data[7];
            int width = data[12] | (data[13] << 8);
            int height = data[14] | (data[15] << 8);
            int bpp = data[16];
            int descriptor = data[17];
            int alphaBits = descriptor & 0x0F;
            bool rightToLeft = (descriptor & 0x10) != 0;
            bool topToBottom = (descriptor & 0x20) != 0;

            bool rle = imageType == 10 || imageType == 11;
            bool grey = imageType == 3 || imageType == 11;
            if (!(imageType == 2 || imageType == 3 || imageType == 10 || imageType == 11))
                throw new FormatException($"TGA: unsupported image type {imageType} (colour-mapped TGAs are not supported)");
            if (width <= 0 || height <= 0) throw new FormatException("TGA: invalid dimensions");
            if (grey && bpp != 8 && bpp != 16) throw new FormatException($"TGA: unsupported grey depth {bpp}");
            if (!grey && bpp != 16 && bpp != 24 && bpp != 32) throw new FormatException($"TGA: unsupported depth {bpp}");

            int bytesPerPixel = bpp / 8;
            int offset = 18 + idLength;
            if (colorMapType == 1) offset += colorMapLength * ((colorMapEntryBits + 7) / 8); // skip an unused palette

            int count = width * height;
            var stream = new Color32[count];
            bool hasAlpha = (bpp == 32 && alphaBits > 0) || (grey && bpp == 16) || (bpp == 16 && alphaBits > 0);

            if (!rle)
            {
                if (offset + count * bytesPerPixel > data.Length) throw new FormatException("TGA: truncated pixel data");
                for (int i = 0; i < count; i++, offset += bytesPerPixel)
                    stream[i] = ReadTgaPixel(data, offset, bpp, grey, hasAlpha);
            }
            else
            {
                int i = 0;
                while (i < count)
                {
                    if (offset >= data.Length) throw new FormatException("TGA: truncated RLE data");
                    int header = data[offset++];
                    int run = (header & 0x7F) + 1;
                    if (i + run > count) run = count - i; // tolerate encoders that overrun the last packet
                    if ((header & 0x80) != 0)
                    {
                        if (offset + bytesPerPixel > data.Length) throw new FormatException("TGA: truncated RLE packet");
                        Color32 c = ReadTgaPixel(data, offset, bpp, grey, hasAlpha);
                        offset += bytesPerPixel;
                        for (int k = 0; k < run; k++) stream[i++] = c;
                    }
                    else
                    {
                        if (offset + run * bytesPerPixel > data.Length) throw new FormatException("TGA: truncated raw packet");
                        for (int k = 0; k < run; k++, offset += bytesPerPixel) stream[i++] = ReadTgaPixel(data, offset, bpp, grey, hasAlpha);
                    }
                }
            }

            // Re-order into Unity's layout (row 0 = bottom, left to right).
            Color32[] pixels;
            if (!topToBottom && !rightToLeft)
            {
                pixels = stream;
            }
            else
            {
                pixels = new Color32[count];
                for (int y = 0; y < height; y++)
                {
                    int srcRow = topToBottom ? height - 1 - y : y;
                    for (int x = 0; x < width; x++)
                    {
                        int srcX = rightToLeft ? width - 1 - x : x;
                        pixels[y * width + x] = stream[srcRow * width + srcX];
                    }
                }
            }

            // A 32-bit TGA whose alpha is uniformly opaque is treated as RGB (some exporters always write alpha).
            if (hasAlpha && IsAlphaUniformlyOpaque(pixels)) hasAlpha = false;

            return new DecodedImage { Width = width, Height = height, Pixels = pixels, HasAlpha = hasAlpha };
        }

        private static Color32 ReadTgaPixel(byte[] d, int o, int bpp, bool grey, bool hasAlpha)
        {
            if (grey)
            {
                byte g = d[o];
                byte a = bpp == 16 ? d[o + 1] : (byte)255;
                return new Color32(g, g, g, a);
            }
            switch (bpp)
            {
                case 16:
                {
                    // A1 R5 G5 B5, little endian.
                    int v = d[o] | (d[o + 1] << 8);
                    byte r = (byte)(((v >> 10) & 0x1F) * 255 / 31);
                    byte g = (byte)(((v >> 5) & 0x1F) * 255 / 31);
                    byte b = (byte)((v & 0x1F) * 255 / 31);
                    byte a = hasAlpha ? ((v & 0x8000) != 0 ? (byte)255 : (byte)0) : (byte)255;
                    return new Color32(r, g, b, a);
                }
                case 24:
                    return new Color32(d[o + 2], d[o + 1], d[o], 255);
                default:
                    return new Color32(d[o + 2], d[o + 1], d[o], hasAlpha ? d[o + 3] : (byte)255);
            }
        }

        private static bool IsAlphaUniformlyOpaque(Color32[] pixels)
        {
            for (int i = 0; i < pixels.Length; i++)
                if (pixels[i].a != 255) return false;
            return true;
        }

        // ------------------------------------------------------------------------------------------- PNG / JPG / GPU

        private static DecodedImage DecodeWithImageConversion(byte[] bytes, bool? knownAlpha)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            try
            {
                if (!tex.LoadImage(bytes, false)) throw new FormatException("image could not be decoded");
                Color32[] px = tex.GetPixels32();
                bool alpha = knownAlpha ?? !IsAlphaUniformlyOpaque(px);
                return new DecodedImage { Width = tex.width, Height = tex.height, Pixels = px, HasAlpha = alpha };
            }
            finally
            {
                Object.DestroyImmediate(tex);
            }
        }

        /// <summary>GPU read-back of an imported texture (handles compressed / non-readable imports and exotic formats).</summary>
        private static DecodedImage ReadBackImportedTexture(string assetPath)
        {
            var source = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
            if (source == null) return null;
            bool srgb = AssetImporter.GetAtPath(assetPath) is TextureImporter ti && ti.sRGBTexture;
            int w = source.width, h = source.height;
            // Blit into a render target with the same colour space as the texture so stored bytes round-trip unchanged.
            RenderTexture rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32,
                srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
            RenderTexture previous = RenderTexture.active;
            var copy = new Texture2D(w, h, TextureFormat.RGBA32, false, !srgb);
            try
            {
                Graphics.Blit(source, rt);
                RenderTexture.active = rt;
                copy.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                copy.Apply(false, false);
                Color32[] px = copy.GetPixels32();
                bool alpha = AssetImporter.GetAtPath(assetPath) is TextureImporter imp ? imp.DoesSourceTextureHaveAlpha() : !IsAlphaUniformlyOpaque(px);
                return new DecodedImage { Width = w, Height = h, Pixels = px, HasAlpha = alpha };
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
                Object.DestroyImmediate(copy);
            }
        }

        // ------------------------------------------------------------------------------------------------- helpers

        /// <summary>Box-filter downscale by an integer factor so the longest side is at most <paramref name="maxSize"/>.</summary>
        public static DecodedImage DownscaleToFit(DecodedImage src, int maxSize)
        {
            int factor = 1;
            while (Math.Max(src.Width, src.Height) / factor > maxSize) factor *= 2;
            if (factor == 1) return src;

            int w = Math.Max(1, src.Width / factor), h = Math.Max(1, src.Height / factor);
            var dst = new Color32[w * h];
            int area = factor * factor;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int r = 0, g = 0, b = 0, a = 0;
                    for (int fy = 0; fy < factor; fy++)
                    {
                        int row = (y * factor + fy) * src.Width;
                        for (int fx = 0; fx < factor; fx++)
                        {
                            Color32 c = src.Pixels[row + x * factor + fx];
                            r += c.r;
                            g += c.g;
                            b += c.b;
                            a += c.a;
                        }
                    }
                    dst[y * w + x] = new Color32((byte)(r / area), (byte)(g / area), (byte)(b / area), (byte)(a / area));
                }
            }
            return new DecodedImage { Width = w, Height = h, Pixels = dst, HasAlpha = src.HasAlpha };
        }

        /// <summary>Rec. 709 luma of an 8-bit colour (0..255).</summary>
        public static int Luma(Color32 c) => (c.r * 54 + c.g * 183 + c.b * 19) >> 8;

        /// <summary>Encodes 8-bit RGBA pixels to PNG bytes (raw values, no colour-space conversion).</summary>
        public static byte[] EncodePng(int width, int height, Color32[] pixels)
        {
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
            try
            {
                tex.SetPixels32(pixels);
                tex.Apply(false, false);
                return tex.EncodeToPNG();
            }
            finally
            {
                Object.DestroyImmediate(tex);
            }
        }
    }
}
