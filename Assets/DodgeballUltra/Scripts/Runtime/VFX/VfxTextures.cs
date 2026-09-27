using System;
using DodgeballUltra.Rendering;
using UnityEngine;

namespace DodgeballUltra.VFX
{
    /// <summary>Procedural particle textures available to the VFX builders.</summary>
    public enum VfxTexture
    {
        /// <summary>Soft radial glow (<see cref="MaterialFactory.SoftParticleTexture"/>): sparks, flashes, motes, energy.</summary>
        Soft = 0,
        /// <summary>Billowy fractal-noise puff with baked top lighting: dust, chalk, smoke, mist, fire bodies.</summary>
        Smoke,
        /// <summary>Soft annulus: shock fronts, ripples, bubble silhouettes, ground pulses.</summary>
        Ring,
        /// <summary>Elongated faceted crystal: ice shards.</summary>
        Shard,
        /// <summary>Glossy liquid disc with a specular highlight: glue droplets and puddles.</summary>
        Droplet,
        /// <summary>Irregular hard-edged chip: floor debris, chalk crumbs, ash flakes.</summary>
        Debris,
    }

    /// <summary>
    /// Generates and caches the particle textures used by the procedural effect library. Every texture is created once
    /// (a few milliseconds in total), made non-readable to free CPU memory, and regenerated automatically if it was
    /// destroyed (e.g. by <c>Resources.UnloadUnusedAssets</c> or when entering play mode without a domain reload).
    /// <para>
    /// The look targets realism: smoke/dust puffs use multi-octave value noise with a soft radial falloff and a baked
    /// "light from above" gradient so unlit alpha-blended particles still read as volumetric dust under the HDRP gym lights.
    /// </para>
    /// </summary>
    public static class VfxTextures
    {
        private static readonly Texture2D[] s_cache = new Texture2D[Enum.GetValues(typeof(VfxTexture)).Length];

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            for (int i = 0; i < s_cache.Length; i++) s_cache[i] = null;
        }

        /// <summary>Returns the cached texture for <paramref name="kind"/>, generating it on first use.</summary>
        public static Texture2D Get(VfxTexture kind)
        {
            int index = (int)kind;
            if (index < 0 || index >= s_cache.Length) index = 0;
            var tex = s_cache[index];
            if (tex != null) return tex; // Unity null check also catches destroyed textures

            switch (kind)
            {
                case VfxTexture.Smoke: tex = CreateSmoke(128); break;
                case VfxTexture.Ring: tex = CreateRing(128); break;
                case VfxTexture.Shard: tex = CreateShard(64); break;
                case VfxTexture.Droplet: tex = CreateDroplet(64); break;
                case VfxTexture.Debris: tex = CreateDebris(32); break;
                default: tex = GetSoft(); break;
            }
            s_cache[index] = tex;
            return tex;
        }

        // ------------------------------------------------------------------ generators

        /// <summary>Soft glow from the rendering module, with a local fallback so VFX never break if it is unavailable.</summary>
        private static Texture2D GetSoft()
        {
            try
            {
                var shared = MaterialFactory.SoftParticleTexture;
                if (shared != null) return shared;
            }
            catch (Exception)
            {
                // Rendering module not available (tests / partial builds): fall through to the local generator.
            }
            return CreateRadial(64);
        }

        private static Texture2D CreateRadial(int size)
        {
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float r = Radius(x, y, size);
                // Gaussian-like core with a smooth tail that reaches exactly zero at the edge (no square cut-off).
                float a = Mathf.Exp(-r * r * 4.5f) * (1f - SmoothStep(0.8f, 1f, r));
                px[y * size + x] = new Color32(255, 255, 255, ToByte(a));
            }
            return Finish("DU_VFX_SoftFallback", size, px);
        }

        private static Texture2D CreateSmoke(int size)
        {
            var px = new Color32[size * size];
            float inv = 1f / size;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float u = (x + 0.5f) * inv * 2f - 1f;
                float v = (y + 0.5f) * inv * 2f - 1f;
                float r = Mathf.Sqrt(u * u + v * v);

                // Large billows + fine detail. Offsets keep the pattern away from the lattice origin.
                float billow = Fbm(u * 2.3f + 5.1f, v * 2.3f + 2.7f, 5, 17);
                float detail = Fbm(u * 6.5f + 11.3f, v * 6.5f + 7.9f, 3, 41);

                // Density: radial body modulated by noise so the silhouette is irregular (cauliflower edge).
                float radial = 1f - SmoothStep(0.15f, 1f, r);
                float density = Mathf.Clamp01(radial * (0.45f + billow * 0.95f) - 0.1f);
                density = SmoothStep(0f, 0.85f, density) * (1f - SmoothStep(0.88f, 1f, r));

                // Baked lighting: brighter towards the top (+v, texture rows go bottom->top) and on noise crests.
                float light = Mathf.Clamp01(0.55f + v * 0.3f - u * 0.1f + (detail - 0.5f) * 0.55f + (billow - 0.5f) * 0.35f);
                float shade = Mathf.Lerp(0.66f, 1f, light);
                byte c = ToByte(shade);
                px[y * size + x] = new Color32(c, c, c, ToByte(density));
            }
            return Finish("DU_VFX_Smoke", size, px);
        }

        private static Texture2D CreateRing(int size)
        {
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float r = Radius(x, y, size);
                float u = (x + 0.5f) / size * 2f - 1f;
                float v = (y + 0.5f) / size * 2f - 1f;
                float d = (r - 0.74f) / 0.085f;
                float band = Mathf.Exp(-d * d);
                // Faint inner haze so the ring reads as a pressure front rather than a flat line.
                float haze = 0.14f * SmoothStep(0.74f, 0.2f, r);
                float breakup = 0.78f + 0.22f * Fbm(u * 4f + 3f, v * 4f + 8f, 2, 5);
                float a = (band + haze) * breakup * (1f - SmoothStep(0.9f, 1f, r));
                px[y * size + x] = new Color32(255, 255, 255, ToByte(a));
            }
            return Finish("DU_VFX_Ring", size, px);
        }

        private static Texture2D CreateShard(int size)
        {
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float u = (x + 0.5f) / size * 2f - 1f;
                float v = (y + 0.5f) / size * 2f - 1f;
                // Elongated diamond (crystal splinter), slightly asymmetric so rotations look varied.
                float halfWidth = v > 0.1f ? 0.26f : 0.32f;
                float shape = Mathf.Abs(u) / halfWidth + Mathf.Abs(v - 0.05f) / 0.92f;
                float a = 1f - SmoothStep(0.86f, 1f, shape);
                // Two facets (lit / shadowed side) and a bright central ridge like refracted light.
                float ridge = 1f - Mathf.Clamp01(Mathf.Abs(u) / halfWidth);
                float facet = u < 0f ? 1f : 0.8f;
                float shade = Mathf.Clamp01((0.62f + ridge * 0.38f) * facet + (0.5f - Mathf.Abs(v)) * 0.12f);
                byte c = ToByte(shade);
                px[y * size + x] = new Color32(c, c, c, ToByte(a));
            }
            return Finish("DU_VFX_Shard", size, px);
        }

        private static Texture2D CreateDroplet(int size)
        {
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float u = (x + 0.5f) / size * 2f - 1f;
                float v = (y + 0.5f) / size * 2f - 1f;
                float r = Mathf.Sqrt(u * u + v * v);
                float a = 1f - SmoothStep(0.72f, 0.95f, r);
                // Darker rim (thicker liquid seen edge-on), lighter body, sharp specular glint top-left.
                float body = Mathf.Lerp(0.95f, 0.55f, SmoothStep(0.2f, 0.9f, r));
                float hx = u + 0.3f, hy = v - 0.34f;
                float spec = Mathf.Exp(-(hx * hx + hy * hy) / 0.012f);
                float shade = Mathf.Clamp01(body + spec * 0.9f);
                byte c = ToByte(shade);
                px[y * size + x] = new Color32(c, c, c, ToByte(a));
            }
            return Finish("DU_VFX_Droplet", size, px);
        }

        private static Texture2D CreateDebris(int size)
        {
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float u = (x + 0.5f) / size * 2f - 1f;
                float v = (y + 0.5f) / size * 2f - 1f;
                float r = Mathf.Sqrt(u * u + v * v);
                float n = Fbm(u * 3f + 1.3f, v * 3f + 9.1f, 3, 77);
                // Thresholded noisy blob -> irregular chip with a crisp but anti-aliased edge.
                float field = (1f - r) * 0.9f + (n - 0.5f) * 0.55f + 0.1f;
                float a = SmoothStep(0.42f, 0.52f, field);
                float shade = Mathf.Lerp(0.55f, 1f, Mathf.Clamp01(n + v * 0.25f));
                byte c = ToByte(shade);
                px[y * size + x] = new Color32(c, c, c, ToByte(a));
            }
            return Finish("DU_VFX_Debris", size, px);
        }

        // ------------------------------------------------------------------ helpers

        private static Texture2D Finish(string name, int size, Color32[] pixels)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, true)
            {
                name = name,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 0,
                hideFlags = HideFlags.DontSave,
            };
            tex.SetPixels32(pixels);
            tex.Apply(true, true); // build mips, release the CPU copy
            return tex;
        }

        private static float Radius(int x, int y, int size)
        {
            float u = (x + 0.5f) / size * 2f - 1f;
            float v = (y + 0.5f) / size * 2f - 1f;
            return Mathf.Sqrt(u * u + v * v);
        }

        private static byte ToByte(float v) => (byte)Mathf.Clamp(Mathf.RoundToInt(v * 255f), 0, 255);

        private static float SmoothStep(float edge0, float edge1, float x)
        {
            float t = Mathf.Clamp01((x - edge0) / (edge1 - edge0));
            return t * t * (3f - 2f * t);
        }

        private static float Hash(int x, int y, int seed)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393 + y * 668265263 + seed * 1442695041);
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xFFFFFF) / 16777215f;
            }
        }

        private static float ValueNoise(float x, float y, int seed)
        {
            int xi = Mathf.FloorToInt(x), yi = Mathf.FloorToInt(y);
            float xf = x - xi, yf = y - yi;
            float sx = xf * xf * (3f - 2f * xf), sy = yf * yf * (3f - 2f * yf);
            float a = Hash(xi, yi, seed), b = Hash(xi + 1, yi, seed);
            float c = Hash(xi, yi + 1, seed), d = Hash(xi + 1, yi + 1, seed);
            return Mathf.Lerp(Mathf.Lerp(a, b, sx), Mathf.Lerp(c, d, sx), sy);
        }

        /// <summary>Fractal Brownian motion of value noise, normalised to 0..1.</summary>
        private static float Fbm(float x, float y, int octaves, int seed)
        {
            float sum = 0f, amp = 0.5f, norm = 0f, freq = 1f;
            for (int i = 0; i < octaves; i++)
            {
                sum += ValueNoise(x * freq, y * freq, seed + i * 31) * amp;
                norm += amp;
                amp *= 0.5f;
                freq *= 2.03f;
            }
            return norm > 0f ? sum / norm : 0f;
        }
    }
}
