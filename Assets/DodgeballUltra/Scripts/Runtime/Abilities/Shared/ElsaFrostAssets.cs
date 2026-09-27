using DodgeballUltra.Rendering;
using UnityEngine;

namespace DodgeballUltra.Abilities.Heroes
{
    /// <summary>
    /// Procedurally generated, cached render resources shared by Elsa's frost visuals
    /// (<see cref="ElsaIceTrailSegment"/> floor patches and the <see cref="ElsaAbsoluteZeroField"/> frost sheen).
    /// <para>
    /// Everything is created lazily, once, through <see cref="MaterialFactory"/> so the look stays pipeline-correct
    /// (HDRP Lit in production, URP / Built-in fallbacks elsewhere). The frost texture is a tileable, crystalline
    /// hoar-frost pattern: a fractal value-noise base (frost density) overlaid with feathery dendrite streaks - the
    /// fern-like growth real frost shows on cold floors. RGB carries the subtle blue-white albedo variation of ice,
    /// A carries frost density so transparent overlays read as patchy frost instead of a flat tint.
    /// </para>
    /// <para>All getters are null-safe against Unity-side destruction (scene unloads, play mode exit) and regenerate.</para>
    /// </summary>
    public static class ElsaFrostAssets
    {
        /// <summary>Resolution of the generated frost texture (square, power of two for mipmaps).</summary>
        private const int FrostTextureSize = 128;

        /// <summary>World size (m) one repetition of the frost texture covers on large overlays.</summary>
        public const float FrostTileWorldSize = 1.6f;

        // Physically plausible frost albedos (sRGB). Fresh hoar frost is highly scattering (~0.9 albedo) with a faint
        // blue cast; clear ice patches are darker and bluer where the floor shows through.
        private static readonly Color IceAlbedo = new Color(0.64f, 0.77f, 0.88f, 1f);
        private static readonly Color FrostAlbedo = new Color(0.93f, 0.96f, 0.99f, 1f);

        private static Texture2D s_frostTexture;
        private static Mesh s_patchMesh;
        private static Mesh s_quadMesh;
        private static Material s_iceMaterial;
        private static Material s_hazeMaterial;

        // Colour property names used by the different pipelines' unlit / particle shaders. Checked with HasProperty so
        // alpha fades work whichever shader MaterialFactory picked.
        private static readonly int[] s_colorPropertyIds =
        {
            Shader.PropertyToID("_UnlitColor"),  // HDRP Unlit
            Shader.PropertyToID("_BaseColor"),   // URP / HDRP Lit
            Shader.PropertyToID("_TintColor"),   // Built-in legacy particles
            Shader.PropertyToID("_Color"),       // Built-in Standard / fallback
        };

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            s_frostTexture = null;
            s_patchMesh = null;
            s_quadMesh = null;
            s_iceMaterial = null;
            s_hazeMaterial = null;
        }

        // ------------------------------------------------------------------ public resources

        /// <summary>Tileable crystalline frost texture (RGB = albedo variation, A = frost density).</summary>
        public static Texture2D FrostTexture
        {
            get
            {
                if (s_frostTexture == null) s_frostTexture = BuildFrostTexture();
                return s_frostTexture;
            }
        }

        /// <summary>
        /// Irregular, organically edged oval of unit size in the XZ plane (centred, facing +Y). Scaled per patch to the
        /// trail width/length; the ragged rim avoids the "rectangle decal" look.
        /// </summary>
        public static Mesh PatchMesh
        {
            get
            {
                if (s_patchMesh == null) s_patchMesh = BuildPatchMesh();
                return s_patchMesh;
            }
        }

        /// <summary>Unit quad in the XZ plane (centred, facing +Y), UV 0..1.</summary>
        public static Mesh QuadMesh
        {
            get
            {
                if (s_quadMesh == null) s_quadMesh = BuildQuadMesh();
                return s_quadMesh;
            }
        }

        /// <summary>
        /// Shared opaque Lit material for ice-trail patches: glossy frosted ice (smoothness 0.8, dielectric) with the
        /// frost texture as albedo so specular highlights break up over the crystalline surface.
        /// </summary>
        public static Material IceMaterial
        {
            get
            {
                if (s_iceMaterial == null)
                {
                    s_iceMaterial = MaterialFactory.CreateLit("DU_ElsaIceTrail", new Color(0.88f, 0.93f, 0.97f), 0.8f, 0f,
                        FrostTexture, null, Vector2.one);
                }
                return s_iceMaterial;
            }
        }

        /// <summary>Shared soft, alpha-blended cold haze that feathers the edge of every ice patch into the floor.</summary>
        public static Material HazeMaterial
        {
            get
            {
                if (s_hazeMaterial == null)
                {
                    s_hazeMaterial = MaterialFactory.CreateParticle("DU_ElsaFrostHaze", new Color(0.84f, 0.92f, 1f, 0.3f), false,
                        MaterialFactory.SoftParticleTexture);
                }
                return s_hazeMaterial;
            }
        }

        /// <summary>
        /// A NEW (caller-owned) alpha-blended frost sheen material, used by large overlays that fade in/out on their own.
        /// The caller must destroy it.
        /// </summary>
        public static Material CreateSheenMaterial(Color tint) =>
            MaterialFactory.CreateParticle("DU_ElsaFrostSheen", tint, false, FrostTexture);

        /// <summary>Generates the frost texture now (avoids a first-use hitch). Safe to call repeatedly.</summary>
        public static void Prewarm()
        {
            _ = FrostTexture;
            _ = PatchMesh;
            _ = QuadMesh;
        }

        /// <summary>
        /// Sets the tint colour (with alpha) on whichever colour property the material's shader exposes. Used for fades of
        /// caller-owned materials. No allocations.
        /// </summary>
        public static void SetMaterialColor(Material material, Color color)
        {
            if (material == null) return;
            for (int i = 0; i < s_colorPropertyIds.Length; i++)
            {
                int id = s_colorPropertyIds[i];
                if (material.HasProperty(id)) material.SetColor(id, color);
            }
        }

        // ------------------------------------------------------------------ generation

        private static Texture2D BuildFrostTexture()
        {
            const int n = FrostTextureSize;
            var density = new float[n * n];

            // 1) Fractal (fBm) tileable noise: large soft variations of frost thickness.
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    float u = x / (float)n;
                    float v = y / (float)n;
                    float sum = 0f, norm = 0f, amp = 0.6f, freq = 4f;
                    for (int o = 0; o < 3; o++)
                    {
                        sum += amp * TileablePerlin(u, v, freq, 13.7f + o * 29.3f);
                        norm += amp;
                        amp *= 0.5f;
                        freq *= 2f;
                    }
                    float f = sum / norm;
                    // Contrast curve: bare icy spots next to thick frost.
                    density[y * n + x] = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.3f, 0.72f, f)) * 0.75f;
                }
            }

            // 2) Dendritic frost feathers: short crystalline streaks with 60-degree side branches (hexagonal ice habit).
            var rng = new System.Random(7331);
            const int featherCount = 70;
            for (int s = 0; s < featherCount; s++)
            {
                float px = (float)rng.NextDouble() * n;
                float py = (float)rng.NextDouble() * n;
                float angle = (float)rng.NextDouble() * Mathf.PI * 2f;
                int length = 7 + rng.Next(18);
                float strength = 0.18f + (float)rng.NextDouble() * 0.22f;
                DrawFeather(density, n, px, py, angle, length, strength, rng, true);
            }

            // 3) Compose colour + alpha.
            var pixels = new Color32[n * n];
            for (int i = 0; i < pixels.Length; i++)
            {
                float d = Mathf.Clamp01(density[i]);
                Color c = Color.Lerp(IceAlbedo, FrostAlbedo, d);
                c.a = Mathf.Lerp(0.2f, 1f, d);
                pixels[i] = c;
            }

            var tex = new Texture2D(n, n, TextureFormat.RGBA32, true)
            {
                name = "DU_ElsaFrost",
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 4,
            };
            tex.SetPixels32(pixels);
            tex.Apply(true, true);
            return tex;
        }

        /// <summary>Draws one feathery frost streak (and optionally its side branches) into the density buffer (wrapping).</summary>
        private static void DrawFeather(float[] density, int n, float x, float y, float angle, int length, float strength,
            System.Random rng, bool branch)
        {
            float dx = Mathf.Cos(angle);
            float dy = Mathf.Sin(angle);
            for (int step = 0; step < length; step++)
            {
                float falloff = 1f - step / (float)length;
                Deposit(density, n, x, y, strength * (0.4f + 0.6f * falloff));

                if (branch && step > 1 && step % 3 == 0)
                {
                    // Side branches at +-60 degrees, shorter and fainter.
                    float side = (rng.Next(2) == 0 ? 1f : -1f) * Mathf.PI / 3f;
                    DrawFeather(density, n, x, y, angle + side, Mathf.Max(2, (length - step) / 3), strength * 0.6f, rng, false);
                }

                x += dx;
                y += dy;
                // Slight wander so streaks look grown, not ruled.
                angle += ((float)rng.NextDouble() - 0.5f) * 0.12f;
                dx = Mathf.Cos(angle);
                dy = Mathf.Sin(angle);
            }
        }

        private static void Deposit(float[] density, int n, float x, float y, float amount)
        {
            int ix = ((int)Mathf.Floor(x) % n + n) % n;
            int iy = ((int)Mathf.Floor(y) % n + n) % n;
            density[iy * n + ix] += amount;
            // Soft 1-pixel spread keeps streaks anti-aliased.
            density[iy * n + (ix + 1) % n] += amount * 0.35f;
            density[((iy + 1) % n) * n + ix] += amount * 0.35f;
        }

        /// <summary>
        /// Perlin noise that tiles over the unit square: four offset samples blended bilinearly (classic seamless-noise trick).
        /// </summary>
        private static float TileablePerlin(float u, float v, float frequency, float offset)
        {
            float x = u * frequency;
            float y = v * frequency;
            float w = frequency;
            float h = frequency;
            float a = Mathf.PerlinNoise(x + offset, y + offset * 1.7f);
            float b = Mathf.PerlinNoise(x - w + offset, y + offset * 1.7f);
            float c = Mathf.PerlinNoise(x - w + offset, y - h + offset * 1.7f);
            float d = Mathf.PerlinNoise(x + offset, y - h + offset * 1.7f);
            return (a * (w - x) * (h - y) + b * x * (h - y) + c * x * y + d * (w - x) * y) / (w * h);
        }

        private static Mesh BuildPatchMesh()
        {
            const int rim = 28;
            var vertices = new Vector3[rim + 1];
            var uvs = new Vector2[rim + 1];
            var normals = new Vector3[rim + 1];
            var triangles = new int[rim * 3];

            vertices[0] = Vector3.zero;
            uvs[0] = new Vector2(0.5f, 0.5f);
            normals[0] = Vector3.up;

            for (int i = 0; i < rim; i++)
            {
                float a = i / (float)rim * Mathf.PI * 2f;
                // Ragged rim: two octaves of angular noise (deterministic).
                float wobble = 1f + 0.1f * Mathf.Sin(a * 3f + 0.7f) + 0.06f * Mathf.Sin(a * 7f + 2.1f) + 0.035f * Mathf.Sin(a * 13f);
                // Mean radius 0.47 so the ragged rim stays within ~unit size (0.38..0.56).
                float r = 0.47f * wobble;
                var p = new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
                vertices[i + 1] = p;
                uvs[i + 1] = new Vector2(p.x + 0.5f, p.z + 0.5f);
                normals[i + 1] = Vector3.up;

                // Clockwise seen from above (Unity front face) -> faces +Y.
                int t = i * 3;
                triangles[t] = 0;
                triangles[t + 1] = 1 + (i + 1) % rim;
                triangles[t + 2] = 1 + i;
            }

            var mesh = new Mesh { name = "DU_ElsaIcePatch" };
            mesh.vertices = vertices;
            mesh.uv = uvs;
            mesh.normals = normals;
            mesh.triangles = triangles;
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Mesh BuildQuadMesh()
        {
            var mesh = new Mesh { name = "DU_ElsaFrostQuad" };
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, 0f, -0.5f), new Vector3(0.5f, 0f, -0.5f),
                new Vector3(-0.5f, 0f, 0.5f), new Vector3(0.5f, 0f, 0.5f),
            };
            mesh.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f) };
            mesh.normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
            mesh.triangles = new[] { 0, 2, 1, 2, 3, 1 };
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
