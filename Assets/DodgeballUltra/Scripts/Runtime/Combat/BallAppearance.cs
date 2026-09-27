using System.Collections.Generic;
using DodgeballUltra.Core;
using DodgeballUltra.Rendering;
using UnityEngine;

namespace DodgeballUltra.Combat
{
    /// <summary>Physically based surface description of one <see cref="BallStyle"/>.</summary>
    public struct BallSurfaceLook
    {
        /// <summary>Albedo tint (sRGB) multiplied onto the pebbled-rubber base map.</summary>
        public Color BaseColor;
        public float Smoothness;
        public float Metallic;
        /// <summary>HDR emission (black = none). 1.0 ≈ display white on HDRP (emissive pre-exposure weight 1).</summary>
        public Color Emission;
    }

    /// <summary>How the flight trail of a <see cref="BallStyle"/> looks.</summary>
    public struct BallTrailLook
    {
        /// <summary>HDR tint of the trail material (additive trails glow).</summary>
        public Color Tint;
        public bool Additive;
        /// <summary>Trail width as a multiple of the ball diameter at full visibility.</summary>
        public float WidthFactor;
        /// <summary>Trail lifetime (s).</summary>
        public float Time;
        /// <summary>Below this speed (km/h) the trail is invisible (plain balls only streak when really fast).</summary>
        public float MinSpeedKmh;
        /// <summary>At or above this speed (km/h) the trail is fully visible.</summary>
        public float FullSpeedKmh;
        /// <summary>Opacity at full visibility.</summary>
        public float MaxAlpha;
    }

    /// <summary>
    /// Realistic look of the dodgeballs: a classic red rubber playground/dodgeball skin (pebbled grip texture, moulding seam,
    /// inflation valve) generated procedurally and rendered through <see cref="MaterialFactory.CreateLit"/>, plus the
    /// per-style variants used by hero abilities (Meteor fire, Beam energy, Glue, Freeze ice, Turret slug) and their trails.
    /// <para>
    /// Match balls use <see cref="BallManager.MatchBallMaterialOverride"/> / <c>GameConfig.ballMaterial</c> when assigned
    /// (e.g. a scanned ball material saved by the Setup Wizard). All materials and textures are created once and shared.
    /// </para>
    /// </summary>
    public static class BallAppearance
    {
        private static Texture2D s_rubberAlbedo;
        private static Texture2D s_rubberNormal;
        private static Texture2D s_trailTexture;
        private static readonly Dictionary<int, Material> s_ballMaterials = new Dictionary<int, Material>(8);
        private static readonly Dictionary<int, Material> s_trailMaterials = new Dictionary<int, Material>(8);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            s_rubberAlbedo = null;
            s_rubberNormal = null;
            s_trailTexture = null;
            s_ballMaterials.Clear();
            s_trailMaterials.Clear();
        }

        // ------------------------------------------------------------------ textures

        /// <summary>Near-white pebbled rubber albedo (sRGB) tinted by each style's base colour.</summary>
        public static Texture2D RubberAlbedo
        {
            get
            {
                EnsureRubberTextures();
                return s_rubberAlbedo;
            }
        }

        /// <summary>Tangent-space normal map of the pebbles, seam and valve (linear).</summary>
        public static Texture2D RubberNormal
        {
            get
            {
                EnsureRubberTextures();
                return s_rubberNormal;
            }
        }

        private static void EnsureRubberTextures()
        {
            if (s_rubberAlbedo != null && s_rubberNormal != null) return;
            BallTextureGenerator.Generate(BallTextureGenerator.DefaultWidth, BallTextureGenerator.DefaultHeight,
                out s_rubberAlbedo, out s_rubberNormal);
        }

        /// <summary>Soft cross-section profile for trails (u = along the trail, v = across it).</summary>
        public static Texture2D TrailTexture
        {
            get
            {
                if (s_trailTexture != null) return s_trailTexture;
                const int w = 4, h = 32;
                var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, false)
                {
                    name = "DU_BallTrailProfile",
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                    hideFlags = HideFlags.DontSave,
                };
                var pixels = new Color32[w * h];
                for (int y = 0; y < h; y++)
                {
                    // Gaussian falloff from the centre line to the edges: a soft air streak, never a hard ribbon.
                    float d = (y + 0.5f) / h * 2f - 1f;
                    float a = Mathf.Exp(-d * d * 4.5f);
                    byte b = (byte)Mathf.RoundToInt(Mathf.Clamp01(a) * 255f);
                    for (int x = 0; x < w; x++) pixels[y * w + x] = new Color32(b, b, b, b);
                }
                tex.SetPixels32(pixels);
                tex.Apply(false, true);
                s_trailTexture = tex;
                return tex;
            }
        }

        // ------------------------------------------------------------------ looks

        /// <summary>Physically plausible surface values per style.</summary>
        public static BallSurfaceLook GetSurfaceLook(BallStyle style)
        {
            switch (style)
            {
                case BallStyle.Meteor:
                    // Scorched rubber with embers glowing through the pebble valleys.
                    return new BallSurfaceLook { BaseColor = new Color(0.40f, 0.10f, 0.035f), Smoothness = 0.35f, Emission = new Color(1f, 0.36f, 0.07f) * 2.2f };
                case BallStyle.Beam:
                    // Energy-coated ball: pale, glossy and brightly emissive.
                    return new BallSurfaceLook { BaseColor = new Color(0.82f, 0.93f, 0.97f), Smoothness = 0.85f, Emission = new Color(0.55f, 0.88f, 1f) * 4.5f };
                case BallStyle.Glue:
                    // Wet, viscous green coating.
                    return new BallSurfaceLook { BaseColor = new Color(0.19f, 0.44f, 0.10f), Smoothness = 0.9f, Emission = Color.black };
                case BallStyle.Freeze:
                    // Frosted ice shell with a faint cold inner glow.
                    return new BallSurfaceLook { BaseColor = new Color(0.66f, 0.84f, 0.96f), Smoothness = 0.9f, Emission = new Color(0.3f, 0.6f, 1f) * 0.55f };
                case BallStyle.Turret:
                    // Machined gunmetal slug fired by Screws' turret.
                    return new BallSurfaceLook { BaseColor = new Color(0.30f, 0.31f, 0.33f), Smoothness = 0.62f, Metallic = 0.85f, Emission = Color.black };
                default:
                    // Classic red rubber dodgeball (PBR albedo ~ sRGB 158/19/15), semi-gloss skin.
                    return new BallSurfaceLook { BaseColor = new Color(0.62f, 0.075f, 0.06f), Smoothness = 0.42f, Emission = Color.black };
            }
        }

        /// <summary>Trail look per style.</summary>
        public static BallTrailLook GetTrailLook(BallStyle style)
        {
            switch (style)
            {
                case BallStyle.Meteor:
                    return new BallTrailLook { Tint = new Color(1f, 0.42f, 0.1f) * 2.5f, Additive = true, WidthFactor = 1.7f, Time = 0.3f, MinSpeedKmh = 0f, FullSpeedKmh = 60f, MaxAlpha = 1f };
                case BallStyle.Beam:
                    return new BallTrailLook { Tint = new Color(0.6f, 0.92f, 1f) * 4f, Additive = true, WidthFactor = 1.4f, Time = 0.4f, MinSpeedKmh = 0f, FullSpeedKmh = 60f, MaxAlpha = 1f };
                case BallStyle.Glue:
                    return new BallTrailLook { Tint = new Color(0.3f, 0.75f, 0.18f, 1f), Additive = false, WidthFactor = 1f, Time = 0.22f, MinSpeedKmh = 0f, FullSpeedKmh = 50f, MaxAlpha = 0.55f };
                case BallStyle.Freeze:
                    return new BallTrailLook { Tint = new Color(0.55f, 0.85f, 1f) * 1.8f, Additive = true, WidthFactor = 1.3f, Time = 0.3f, MinSpeedKmh = 0f, FullSpeedKmh = 50f, MaxAlpha = 1f };
                case BallStyle.Turret:
                    return new BallTrailLook { Tint = new Color(1f, 0.8f, 0.45f) * 1.6f, Additive = true, WidthFactor = 0.45f, Time = 0.07f, MinSpeedKmh = 0f, FullSpeedKmh = 60f, MaxAlpha = 1f };
                default:
                    // A plain ball has no "effect": only a faint motion streak once it is really fast (reads like motion blur).
                    return new BallTrailLook { Tint = new Color(1f, 1f, 1f, 1f), Additive = false, WidthFactor = 0.85f, Time = 0.08f, MinSpeedKmh = 70f, FullSpeedKmh = 170f, MaxAlpha = 0.22f };
            }
        }

        // ------------------------------------------------------------------ materials

        /// <summary>
        /// Shared material for <paramref name="style"/>. Standard match balls use the configured override material when one
        /// is assigned (<see cref="BallManager.ResolveMatchBallMaterial"/>).
        /// </summary>
        public static Material GetBallMaterial(BallStyle style)
        {
            if (style == BallStyle.Standard)
            {
                var configured = BallManager.ResolveMatchBallMaterial();
                if (configured != null) return configured;
            }

            if (s_ballMaterials.TryGetValue((int)style, out var cached) && cached != null) return cached;

            var look = GetSurfaceLook(style);
            var material = MaterialFactory.CreateLit("DU_Ball_" + style, look.BaseColor, look.Smoothness, look.Metallic,
                RubberAlbedo, RubberNormal);
            if (material != null)
            {
                material.hideFlags = HideFlags.DontSave;
                if (look.Emission.maxColorComponent > 1e-4f) ApplyEmission(material, look.Emission);
                RuntimeRenderingHooks.Active?.ValidateMaterial(material);
            }
            s_ballMaterials[(int)style] = material;
            return material;
        }

        /// <summary>Shared trail material for <paramref name="style"/> (tint carries the HDR intensity).</summary>
        public static Material GetTrailMaterial(BallStyle style)
        {
            if (s_trailMaterials.TryGetValue((int)style, out var cached) && cached != null) return cached;
            var look = GetTrailLook(style);
            var material = MaterialFactory.CreateParticle("DU_BallTrail_" + style, look.Tint, look.Additive, TrailTexture);
            if (material != null)
            {
                material.hideFlags = HideFlags.DontSave;
                RuntimeRenderingHooks.Active?.ValidateMaterial(material);
            }
            s_trailMaterials[(int)style] = material;
            return material;
        }

        /// <summary>
        /// Enables emission on a lit material in a pipeline-neutral way: the colour goes to the pipeline's emission
        /// property (<see cref="ShaderProps.EmissionColor"/>); the Built-in/URP keyword is enabled and HDRP's intensity
        /// mode is set to "colour carries the intensity". The HDRP adapter can finish the setup in ValidateMaterial.
        /// </summary>
        private static void ApplyEmission(Material material, Color hdrColor)
        {
            string property = ShaderProps.EmissionColor;
            if (string.IsNullOrEmpty(property) || !material.HasProperty(property)) return;
            material.SetColor(property, hdrColor);
            material.EnableKeyword("_EMISSION");
            material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            if (material.HasProperty("_UseEmissiveIntensity")) material.SetFloat("_UseEmissiveIntensity", 0f);
        }
    }

    /// <summary>
    /// Generates the pebbled-rubber skin of a dodgeball: albedo and tangent-space normal map in the sphere's UV layout
    /// (see <see cref="ProceduralBallMesh"/>). Every texel is evaluated at its true 3D point on the ball, so pebbles keep a
    /// constant physical size everywhere (no stretching at the poles) and normals are derived from metric height gradients.
    /// <para>
    /// Surface model (all sizes in metres on a 0.105 m ball): Worley-cell pebbles (~4 mm) with soft valleys, fine rubber
    /// grain, a recessed moulding seam around the equator and an inflation valve plug. Deterministic (fixed hash seeds).
    /// </para>
    /// </summary>
    public static class BallTextureGenerator
    {
        public const int DefaultWidth = 512;
        public const int DefaultHeight = 256;

        private const float PebbleSpacing = 0.0042f;   // distance between pebble centres
        private const float PebbleDepth = 0.00035f;    // height of a pebble dome
        private const float GrainScale = 0.0009f;      // micro grain feature size
        private const float MottleScale = 0.03f;       // large-scale colour mottling
        private const float SeamHalfWidth = 0.0011f;   // half width of the moulding seam groove
        private const float ValvePlugRadius = 0.0028f; // inflation valve plug
        private const float ValveRingRadius = 0.0037f;

        private static readonly Vector3 ValveDirection = new Vector3(0.0f, 0.62f, 0.78f).normalized;

        /// <summary>Creates the albedo (sRGB, mip-mapped) and normal map (linear, mip-mapped) textures.</summary>
        public static void Generate(int width, int height, out Texture2D albedo, out Texture2D normalMap)
        {
            width = Mathf.Max(16, width);
            height = Mathf.Max(8, height);
            float radius = GameConstants.BallRadius;

            var heights = new float[width * height];
            var albedoPixels = new Color32[width * height];

            for (int y = 0; y < height; y++)
            {
                float v = (y + 0.5f) / height;
                float theta = v * Mathf.PI;
                float sinT = Mathf.Sin(theta);
                float cosT = Mathf.Cos(theta);

                for (int x = 0; x < width; x++)
                {
                    float u = (x + 0.5f) / width;
                    float phi = u * 2f * Mathf.PI;
                    var n = new Vector3(sinT * Mathf.Cos(phi), -cosT, sinT * Mathf.Sin(phi));
                    var p = n * radius;

                    // Pebbles: F2 - F1 is ~0 on the borders between Worley cells (the valleys) and grows toward the cell
                    // centres, which gives rounded, tightly packed bumps like moulded grip rubber.
                    Worley(p / PebbleSpacing, out float f1, out float f2, out float cellValue);
                    float pebble = SmoothStep(0f, 0.42f, f2 - f1);
                    float grain = ValueNoise(p / GrainScale, 11u);
                    float h = pebble * 0.85f + grain * 0.15f;

                    // Moulding seam around the equator: a shallow groove.
                    float seam = 1f - SmoothStep(SeamHalfWidth * 0.45f, SeamHalfWidth, Mathf.Abs(p.y));
                    h = Mathf.Lerp(h, -0.6f, seam);

                    // Inflation valve: a flat plug in a small recessed ring.
                    float valveDistance = Mathf.Acos(Mathf.Clamp(Vector3.Dot(n, ValveDirection), -1f, 1f)) * radius;
                    float valvePlug = 1f - SmoothStep(ValvePlugRadius * 0.85f, ValvePlugRadius, valveDistance);
                    float valveRing = (1f - SmoothStep(ValvePlugRadius, ValveRingRadius, valveDistance)) * (1f - valvePlug);
                    h = Mathf.Lerp(h, 0.25f, valvePlug);
                    h = Mathf.Lerp(h, -0.5f, valveRing);

                    heights[y * width + x] = h * PebbleDepth;

                    // Tone (multiplied by the style colour): subtle per-pebble variation, low-frequency mottling from the
                    // moulding process, darker valleys (dirt/ambient occlusion baked into albedo sparingly), dark seam/valve.
                    float mottle = Fbm(p / MottleScale, 3, 5u);
                    float tone = 0.93f
                                 + (cellValue - 0.5f) * 0.05f
                                 + (mottle - 0.5f) * 0.07f
                                 - (1f - pebble) * 0.05f
                                 - seam * 0.28f
                                 - valveRing * 0.35f
                                 - valvePlug * 0.2f;
                    byte t = (byte)Mathf.RoundToInt(Mathf.Clamp01(tone) * 255f);
                    albedoPixels[y * width + x] = new Color32(t, t, t, 255);
                }
            }

            // Tangent-space normals from metric height gradients (tangent = +u, bitangent = +v).
            var normalPixels = new Color32[width * height];
            float dv = Mathf.PI * radius / height;
            for (int y = 0; y < height; y++)
            {
                float theta = (y + 0.5f) / height * Mathf.PI;
                float du = 2f * Mathf.PI * radius * Mathf.Max(1e-3f, Mathf.Sin(theta)) / width;
                int yDown = Mathf.Max(0, y - 1);
                int yUp = Mathf.Min(height - 1, y + 1);
                float yStep = (yUp - yDown) * dv;

                for (int x = 0; x < width; x++)
                {
                    int xLeft = (x - 1 + width) % width;
                    int xRight = (x + 1) % width;
                    float dhdx = (heights[y * width + xRight] - heights[y * width + xLeft]) / (2f * du);
                    float dhdy = yStep > 0f ? (heights[yUp * width + x] - heights[yDown * width + x]) / yStep : 0f;
                    var nn = new Vector3(-dhdx, -dhdy, 1f).normalized;
                    normalPixels[y * width + x] = new Color32(
                        (byte)Mathf.RoundToInt((nn.x * 0.5f + 0.5f) * 255f),
                        (byte)Mathf.RoundToInt((nn.y * 0.5f + 0.5f) * 255f),
                        (byte)Mathf.RoundToInt((nn.z * 0.5f + 0.5f) * 255f),
                        255);
                }
            }

            albedo = new Texture2D(width, height, TextureFormat.RGBA32, true, false)
            {
                name = "DU_BallRubber_Albedo",
                wrapModeU = TextureWrapMode.Repeat,
                wrapModeV = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 4,
                hideFlags = HideFlags.DontSave,
            };
            albedo.SetPixels32(albedoPixels);
            albedo.Apply(true, true);

            // Stored as plain XYZ with A = 1: every pipeline's UnpackNormal (RG-or-AG variant) decodes this layout.
            normalMap = new Texture2D(width, height, TextureFormat.RGBA32, true, true)
            {
                name = "DU_BallRubber_Normal",
                wrapModeU = TextureWrapMode.Repeat,
                wrapModeV = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 4,
                hideFlags = HideFlags.DontSave,
            };
            normalMap.SetPixels32(normalPixels);
            normalMap.Apply(true, true);
        }

        // ------------------------------------------------------------------ noise (deterministic, allocation-free)

        private static float SmoothStep(float edge0, float edge1, float x)
        {
            float t = Mathf.Clamp01((x - edge0) / (edge1 - edge0));
            return t * t * (3f - 2f * t);
        }

        private static uint Hash(int x, int y, int z, uint seed)
        {
            unchecked
            {
                uint h = seed * 0x9E3779B1u;
                h ^= (uint)x * 0x85EBCA77u;
                h = (h << 13) | (h >> 19);
                h ^= (uint)y * 0xC2B2AE3Du;
                h = (h << 13) | (h >> 19);
                h ^= (uint)z * 0x27D4EB2Fu;
                h ^= h >> 15;
                h *= 0x2C1B3C6Du;
                h ^= h >> 12;
                h *= 0x297A2D39u;
                h ^= h >> 15;
                return h;
            }
        }

        private static float Hash01(int x, int y, int z, uint seed) => (Hash(x, y, z, seed) & 0xFFFFFFu) / 16777216f;

        /// <summary>3D Worley (cellular) noise: distances to the nearest and second-nearest feature points.</summary>
        private static void Worley(Vector3 p, out float f1, out float f2, out float nearestCellValue)
        {
            int xi = Mathf.FloorToInt(p.x);
            int yi = Mathf.FloorToInt(p.y);
            int zi = Mathf.FloorToInt(p.z);
            f1 = f2 = float.MaxValue;
            nearestCellValue = 0f;

            for (int dz = -1; dz <= 1; dz++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int cx = xi + dx, cy = yi + dy, cz = zi + dz;
                // Jitter kept inside [0.1, 0.9] so neighbouring pebbles never merge.
                float fx = cx + 0.1f + 0.8f * Hash01(cx, cy, cz, 1u);
                float fy = cy + 0.1f + 0.8f * Hash01(cx, cy, cz, 2u);
                float fz = cz + 0.1f + 0.8f * Hash01(cx, cy, cz, 3u);
                float ddx = fx - p.x, ddy = fy - p.y, ddz = fz - p.z;
                float d = Mathf.Sqrt(ddx * ddx + ddy * ddy + ddz * ddz);
                if (d < f1)
                {
                    f2 = f1;
                    f1 = d;
                    nearestCellValue = Hash01(cx, cy, cz, 7u);
                }
                else if (d < f2)
                {
                    f2 = d;
                }
            }
        }

        /// <summary>Trilinear value noise in [0, 1].</summary>
        private static float ValueNoise(Vector3 p, uint seed)
        {
            int xi = Mathf.FloorToInt(p.x);
            int yi = Mathf.FloorToInt(p.y);
            int zi = Mathf.FloorToInt(p.z);
            float tx = p.x - xi, ty = p.y - yi, tz = p.z - zi;
            tx = tx * tx * (3f - 2f * tx);
            ty = ty * ty * (3f - 2f * ty);
            tz = tz * tz * (3f - 2f * tz);

            float c000 = Hash01(xi, yi, zi, seed), c100 = Hash01(xi + 1, yi, zi, seed);
            float c010 = Hash01(xi, yi + 1, zi, seed), c110 = Hash01(xi + 1, yi + 1, zi, seed);
            float c001 = Hash01(xi, yi, zi + 1, seed), c101 = Hash01(xi + 1, yi, zi + 1, seed);
            float c011 = Hash01(xi, yi + 1, zi + 1, seed), c111 = Hash01(xi + 1, yi + 1, zi + 1, seed);

            float x00 = Mathf.Lerp(c000, c100, tx), x10 = Mathf.Lerp(c010, c110, tx);
            float x01 = Mathf.Lerp(c001, c101, tx), x11 = Mathf.Lerp(c011, c111, tx);
            float y0 = Mathf.Lerp(x00, x10, ty), y1 = Mathf.Lerp(x01, x11, ty);
            return Mathf.Lerp(y0, y1, tz);
        }

        /// <summary>Fractal sum of value noise, normalised to [0, 1].</summary>
        private static float Fbm(Vector3 p, int octaves, uint seed)
        {
            float sum = 0f, amplitude = 0.5f, norm = 0f;
            for (int i = 0; i < octaves; i++)
            {
                sum += ValueNoise(p, seed + (uint)i * 17u) * amplitude;
                norm += amplitude;
                amplitude *= 0.5f;
                p *= 2.03f;
            }
            return norm > 0f ? sum / norm : 0f;
        }
    }
}
