using System.Collections.Generic;
using DodgeballUltra.Combat;
using DodgeballUltra.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace DodgeballUltra.Abilities.Heroes
{
    /// <summary>
    /// Small toolbox shared by the deployable props of the Guardian/Brawler/Engineer abilities (Bear's Aegis Barrier,
    /// Screws' Auto-Turret and Glue puddles, Gouki's tackle guard): collider-free mesh parts built from Unity's primitive
    /// meshes, pipeline-agnostic material tinting (HDRP <c>_UnlitColor</c>/<c>_BaseColor</c>, built-in <c>_Color</c>/<c>_TintColor</c>),
    /// cached procedural textures (hexagonal energy lattice, ripple ring, vertical glow ramp) and procedural meshes
    /// (subdivided double-sided sheets, organic splat discs).
    /// <para>
    /// Materials are always created through <see cref="MaterialFactory"/>; this class only tweaks colours and builds geometry.
    /// Every mesh/texture returned by a <c>Build*</c> method is owned by the caller (destroy it in OnDestroy); cached
    /// textures are shared and must not be destroyed by props.
    /// </para>
    /// </summary>
    internal static class ScrewsGadgetKit
    {
        // ------------------------------------------------------------------ shader property ids (all pipelines)
        private static readonly int s_UnlitColor = Shader.PropertyToID("_UnlitColor");   // HDRP Unlit [MainColor]
        private static readonly int s_BaseColor = Shader.PropertyToID("_BaseColor");     // HDRP/URP Lit [MainColor]
        private static readonly int s_Color = Shader.PropertyToID("_Color");             // built-in Standard / legacy
        private static readonly int s_TintColor = Shader.PropertyToID("_TintColor");     // built-in legacy particles
        private static readonly int s_EmissiveColor = Shader.PropertyToID("_EmissiveColor"); // HDRP Lit
        private static readonly int s_EmissionColor = Shader.PropertyToID("_EmissionColor"); // built-in / URP

        private static readonly Dictionary<PrimitiveType, Mesh> s_primitiveMeshes = new Dictionary<PrimitiveType, Mesh>(4);

        private static Texture2D s_hexTexture;
        private static Texture2D s_ringTexture;
        private static Texture2D s_glowRampTexture;

        /// <summary>Hex lattice period in hex "circumradius" units: pointy-top hexes tile every (sqrt3, 3).</summary>
        public const float HexPeriodX = 1.7320508f;
        public const float HexPeriodY = 3f;

        // ------------------------------------------------------------------ materials

        /// <summary>Sets the main colour of <paramref name="material"/> on whichever colour property its shader exposes.</summary>
        public static void SetTint(Material material, Color color)
        {
            if (material == null) return;
            if (material.HasProperty(s_UnlitColor)) material.SetColor(s_UnlitColor, color);
            if (material.HasProperty(s_BaseColor)) material.SetColor(s_BaseColor, color);
            if (material.HasProperty(s_Color)) material.SetColor(s_Color, color);
            if (material.HasProperty(s_TintColor)) material.SetColor(s_TintColor, color);
        }

        /// <summary>Sets an HDR emissive colour when the shader supports emission (HDRP Lit / Standard).</summary>
        public static void SetEmission(Material material, Color hdrColor)
        {
            if (material == null) return;
            if (material.HasProperty(s_EmissiveColor)) material.SetColor(s_EmissiveColor, hdrColor);
            if (material.HasProperty(s_EmissionColor))
            {
                material.SetColor(s_EmissionColor, hdrColor);
                material.EnableKeyword("_EMISSION");
            }
        }

        private static readonly int[] s_mainTextureIds =
        {
            Shader.PropertyToID("_UnlitColorMap"), // HDRP Unlit
            Shader.PropertyToID("_BaseColorMap"),  // HDRP Lit
            Shader.PropertyToID("_BaseMap"),       // URP
            Shader.PropertyToID("_MainTex"),       // built-in
        };

        /// <summary>
        /// Scrolls the main texture of <paramref name="material"/> (whichever texture property its pipeline uses). Silently
        /// does nothing when the shader has none (avoids the per-frame "no texture property" error of mainTextureOffset).
        /// </summary>
        public static void SetMainTextureOffset(Material material, Vector2 offset)
        {
            if (material == null) return;
            for (int i = 0; i < s_mainTextureIds.Length; i++)
            {
                if (!material.HasProperty(s_mainTextureIds[i])) continue;
                material.SetTextureOffset(s_mainTextureIds[i], offset);
                return;
            }
        }

        /// <summary>Destroys a runtime object (material, mesh...) if it still exists.</summary>
        public static void DestroySafe(Object obj)
        {
            if (obj != null) Object.Destroy(obj);
        }

        // ------------------------------------------------------------------ parts

        /// <summary>
        /// Unity's built-in primitive mesh (cube, cylinder, sphere, quad...), fetched once from a throw-away primitive that is
        /// destroyed immediately so its collider never takes part in the simulation.
        /// </summary>
        public static Mesh GetPrimitiveMesh(PrimitiveType type)
        {
            if (s_primitiveMeshes.TryGetValue(type, out var mesh) && mesh != null) return mesh;
            var temp = GameObject.CreatePrimitive(type);
            var collider = temp.GetComponent<Collider>();
            if (collider != null) collider.enabled = false;
            mesh = temp.GetComponent<MeshFilter>().sharedMesh;
            Object.DestroyImmediate(temp);
            s_primitiveMeshes[type] = mesh;
            return mesh;
        }

        /// <summary>
        /// Creates a collider-free visual part (layer <see cref="GameLayers.Visual"/>) using a primitive mesh.
        /// </summary>
        public static Transform CreatePart(string name, PrimitiveType type, Transform parent, Vector3 localPosition,
            Quaternion localRotation, Vector3 localScale, Material material, bool castShadows = true)
        {
            return CreateMeshPart(name, GetPrimitiveMesh(type), parent, localPosition, localRotation, localScale, material, castShadows);
        }

        /// <summary>Creates a collider-free visual part with an explicit mesh.</summary>
        public static Transform CreateMeshPart(string name, Mesh mesh, Transform parent, Vector3 localPosition,
            Quaternion localRotation, Vector3 localScale, Material material, bool castShadows = true)
        {
            var go = new GameObject(name);
            go.layer = GameLayers.Visual;
            var t = go.transform;
            t.SetParent(parent, false);
            t.localPosition = localPosition;
            t.localRotation = localRotation;
            t.localScale = localScale;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = castShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
            renderer.receiveShadows = castShadows;
            renderer.lightProbeUsage = LightProbeUsage.BlendProbes;
            return t;
        }

        /// <summary>Creates an empty pivot transform.</summary>
        public static Transform CreatePivot(string name, Transform parent, Vector3 localPosition, Quaternion localRotation)
        {
            var t = new GameObject(name).transform;
            t.gameObject.layer = GameLayers.Visual;
            t.SetParent(parent, false);
            t.localPosition = localPosition;
            t.localRotation = localRotation;
            return t;
        }

        // ------------------------------------------------------------------ team helpers

        /// <summary>True when <paramref name="ball"/> was thrown by the team opposing <paramref name="ownerTeam"/>.</summary>
        public static bool IsEnemyBall(TeamId ownerTeam, DodgeBall ball)
        {
            if (ball == null || !ownerTeam.IsValid()) return false;
            var throwerTeam = ball.ThrowerTeam;
            return throwerTeam.IsValid() && throwerTeam != ownerTeam;
        }

        /// <summary>Normalised planar (y = 0) copy of <paramref name="v"/>, or <paramref name="fallback"/> when degenerate.</summary>
        public static Vector3 Planar(Vector3 v, Vector3 fallback)
        {
            v.y = 0f;
            return v.sqrMagnitude > 1e-6f ? v.normalized : fallback;
        }

        // ------------------------------------------------------------------ procedural meshes

        /// <summary>
        /// A subdivided, double-sided rectangle in the local XY plane (bottom edge on y = 0, centred on x), facing +Z/-Z.
        /// UVs are world-scaled so a repeating texture keeps a constant physical size: <c>uv = (x / uvSizeX, y / uvSizeY)</c>.
        /// The mesh is marked dynamic because callers may displace its vertices (energy ripples).
        /// </summary>
        public static Mesh BuildSheet(string name, float width, float height, int segmentsX, int segmentsY, float uvSizeX,
            float uvSizeY, out Vector3[] baseVertices)
        {
            segmentsX = Mathf.Max(1, segmentsX);
            segmentsY = Mathf.Max(1, segmentsY);
            int cols = segmentsX + 1, rows = segmentsY + 1;
            int perSide = cols * rows;
            var vertices = new Vector3[perSide * 2];
            var normals = new Vector3[perSide * 2];
            var uvs = new Vector2[perSide * 2];
            var triangles = new int[segmentsX * segmentsY * 12];

            for (int y = 0; y < rows; y++)
            {
                for (int x = 0; x < cols; x++)
                {
                    int i = y * cols + x;
                    var p = new Vector3((x / (float)segmentsX - 0.5f) * width, y / (float)segmentsY * height, 0f);
                    var uv = new Vector2(p.x / Mathf.Max(1e-3f, uvSizeX), p.y / Mathf.Max(1e-3f, uvSizeY));
                    vertices[i] = p;
                    vertices[i + perSide] = p;
                    normals[i] = Vector3.back;
                    normals[i + perSide] = Vector3.forward;
                    uvs[i] = uv;
                    uvs[i + perSide] = uv;
                }
            }

            int t = 0;
            for (int y = 0; y < segmentsY; y++)
            {
                for (int x = 0; x < segmentsX; x++)
                {
                    int a = y * cols + x, b = a + 1, c = a + cols, d = c + 1;
                    // Front face (visible from -Z).
                    triangles[t++] = a; triangles[t++] = c; triangles[t++] = b;
                    triangles[t++] = b; triangles[t++] = c; triangles[t++] = d;
                    // Back face (visible from +Z), reversed winding on the duplicated vertices.
                    triangles[t++] = a + perSide; triangles[t++] = b + perSide; triangles[t++] = c + perSide;
                    triangles[t++] = b + perSide; triangles[t++] = d + perSide; triangles[t++] = c + perSide;
                }
            }

            var mesh = new Mesh { name = name };
            mesh.MarkDynamic();
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.uv = uvs;
            mesh.triangles = triangles;
            // Generous bounds so small vertex displacements never cause culling pops.
            mesh.bounds = new Bounds(new Vector3(0f, height * 0.5f, 0f), new Vector3(width + 0.5f, height + 0.5f, 1f));
            baseVertices = vertices;
            return mesh;
        }

        /// <summary>A double-sided unit quad (-0.5..0.5 in XY) for decals and ripple rings.</summary>
        public static Mesh BuildDoubleSidedQuad(string name)
        {
            var mesh = BuildSheet(name, 1f, 1f, 1, 1, 1f, 1f, out var vertices);
            // Re-centre vertically and use 0..1 UVs.
            var uvs = new Vector2[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                vertices[i].y -= 0.5f;
                uvs[i] = new Vector2(vertices[i].x + 0.5f, vertices[i].y + 0.5f);
            }
            mesh.vertices = vertices;
            mesh.uv = uvs;
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// Organic "splat" disc lying on the XZ plane: a slightly domed centre and a Perlin-perturbed rim so it reads as a
        /// poured viscous liquid rather than a perfect circle. Returns the rest-pose vertices for wobble animation.
        /// </summary>
        /// <param name="radius">Average rim radius (m).</param>
        /// <param name="irregularity">Rim radius variation (fraction of radius).</param>
        /// <param name="rimVertices">Vertices on the outer rim (&gt;= 12).</param>
        /// <param name="domeHeight">Height of the centre above the rim (m).</param>
        /// <param name="seed">Noise seed (different seeds give different shapes).</param>
        public static Mesh BuildSplat(string name, float radius, float irregularity, int rimVertices, float domeHeight, float seed,
            out Vector3[] baseVertices)
        {
            rimVertices = Mathf.Max(12, rimVertices);
            const int rings = 3; // centre + 3 concentric rings: smooth dome shading
            int vertexCount = 1 + rings * rimVertices;
            var vertices = new Vector3[vertexCount];
            var uvs = new Vector2[vertexCount];
            var triangles = new int[rimVertices * 3 + (rings - 1) * rimVertices * 6];

            vertices[0] = new Vector3(0f, domeHeight, 0f);
            uvs[0] = new Vector2(0.5f, 0.5f);
            for (int ring = 0; ring < rings; ring++)
            {
                float f = (ring + 1) / (float)rings; // 1/3, 2/3, 1
                for (int i = 0; i < rimVertices; i++)
                {
                    float a = i / (float)rimVertices * Mathf.PI * 2f;
                    float c = Mathf.Cos(a), s = Mathf.Sin(a);
                    // Two octaves of angular Perlin noise, consistent across rings so the lobes are coherent.
                    float n = Mathf.PerlinNoise(seed + c * 1.3f + 7.1f, seed + s * 1.3f + 3.7f) - 0.5f;
                    n += 0.5f * (Mathf.PerlinNoise(seed * 1.7f + c * 3.1f + 1.3f, seed * 1.7f + s * 3.1f + 9.2f) - 0.5f);
                    float r = radius * f * (1f + irregularity * 2f * n * f);
                    // Viscous profile: flat top that rolls off near the rim (surface tension meniscus).
                    float h = domeHeight * (1f - Mathf.Pow(f, 3.5f));
                    int idx = 1 + ring * rimVertices + i;
                    vertices[idx] = new Vector3(c * r, h, s * r);
                    uvs[idx] = new Vector2(0.5f + c * 0.5f * f, 0.5f + s * 0.5f * f);
                }
            }

            int t = 0;
            for (int i = 0; i < rimVertices; i++)
            {
                int a = 1 + i, b = 1 + (i + 1) % rimVertices;
                triangles[t++] = 0; triangles[t++] = b; triangles[t++] = a;
            }
            for (int ring = 0; ring < rings - 1; ring++)
            {
                int inner = 1 + ring * rimVertices, outer = inner + rimVertices;
                for (int i = 0; i < rimVertices; i++)
                {
                    int i0 = inner + i, i1 = inner + (i + 1) % rimVertices;
                    int o0 = outer + i, o1 = outer + (i + 1) % rimVertices;
                    triangles[t++] = i0; triangles[t++] = i1; triangles[t++] = o0;
                    triangles[t++] = i1; triangles[t++] = o1; triangles[t++] = o0;
                }
            }

            var mesh = new Mesh { name = name };
            mesh.MarkDynamic();
            mesh.vertices = vertices;
            mesh.uv = uvs;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            var b2 = mesh.bounds;
            b2.Expand(new Vector3(radius * 0.2f, 0.05f, radius * 0.2f));
            mesh.bounds = b2;
            baseVertices = vertices;
            return mesh;
        }

        // ------------------------------------------------------------------ procedural textures (cached, shared)

        /// <summary>
        /// One period of a pointy-top hexagonal lattice (white lines with a faint cell fill, alpha = intensity), meant to be
        /// sampled with UVs of <c>(x / (HexPeriodX * s), y / (HexPeriodY * s))</c> for a hex circumradius <c>s</c>.
        /// Repeat-wrapped, mip-mapped (no shimmering at grazing angles).
        /// </summary>
        public static Texture2D HexLatticeTexture
        {
            get
            {
                if (s_hexTexture != null) return s_hexTexture;
                const int size = 256;
                var tex = new Texture2D(size, size, TextureFormat.RGBA32, true, true)
                {
                    name = "DU_HexLattice",
                    wrapMode = TextureWrapMode.Repeat,
                    filterMode = FilterMode.Trilinear,
                    anisoLevel = 4,
                };
                var pixels = new Color32[size * size];
                const float inradius = 0.8660254f; // sqrt(3)/2 for circumradius 1
                const float lineWidth = 0.075f;
                for (int py = 0; py < size; py++)
                {
                    for (int px = 0; px < size; px++)
                    {
                        var p = new Vector2((px + 0.5f) / size * HexPeriodX, (py + 0.5f) / size * HexPeriodY);
                        float edge = HexEdgeDistance(p, inradius);
                        float line = 1f - Mathf.SmoothStep(0f, lineWidth, edge);
                        float fill = 0.08f + 0.22f * Mathf.Pow(1f - Mathf.Clamp01(edge / inradius), 3f);
                        float a = Mathf.Clamp01(line + fill);
                        byte v = (byte)(255f * a);
                        pixels[py * size + px] = new Color32(255, 255, 255, v);
                    }
                }
                tex.SetPixels32(pixels);
                tex.Apply(true, true);
                s_hexTexture = tex;
                return tex;
            }
        }

        /// <summary>Soft ring (radius 0.8 of the half-size) used for energy ripples.</summary>
        public static Texture2D RingTexture
        {
            get
            {
                if (s_ringTexture != null) return s_ringTexture;
                const int size = 128;
                var tex = new Texture2D(size, size, TextureFormat.RGBA32, true, true)
                {
                    name = "DU_RippleRing",
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Trilinear,
                };
                var pixels = new Color32[size * size];
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        float dx = (x + 0.5f) / size * 2f - 1f, dy = (y + 0.5f) / size * 2f - 1f;
                        float r = Mathf.Sqrt(dx * dx + dy * dy);
                        float ring = Mathf.Exp(-Mathf.Pow((r - 0.8f) / 0.07f, 2f));
                        float inner = r < 0.8f ? 0.18f * Mathf.Pow(r / 0.8f, 4f) : 0f;
                        float a = r > 1f ? 0f : Mathf.Clamp01(ring + inner);
                        pixels[y * size + x] = new Color32(255, 255, 255, (byte)(255f * a));
                    }
                }
                tex.SetPixels32(pixels);
                tex.Apply(true, true);
                s_ringTexture = tex;
                return tex;
            }
        }

        /// <summary>Vertical ramp: opaque at v = 0 falling off to transparent at v = 1 (emitter glow at the foot of a field).</summary>
        public static Texture2D GlowRampTexture
        {
            get
            {
                if (s_glowRampTexture != null) return s_glowRampTexture;
                const int height = 64;
                var tex = new Texture2D(4, height, TextureFormat.RGBA32, false, true)
                {
                    name = "DU_GlowRamp",
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                };
                var pixels = new Color32[4 * height];
                for (int y = 0; y < height; y++)
                {
                    float v = (y + 0.5f) / height;
                    byte a = (byte)(255f * Mathf.Pow(1f - v, 2.4f));
                    for (int x = 0; x < 4; x++) pixels[y * 4 + x] = new Color32(255, 255, 255, a);
                }
                tex.SetPixels32(pixels);
                tex.Apply(false, true);
                s_glowRampTexture = tex;
                return tex;
            }
        }

        /// <summary>Distance from <paramref name="p"/> to the nearest hexagon edge of the lattice (0 on an edge).</summary>
        private static float HexEdgeDistance(Vector2 p, float inradius)
        {
            // Candidate centres of one lattice period (pointy-top hexes, circumradius 1) and their neighbours.
            float best = float.MaxValue;
            for (int j = -1; j <= 2; j++)
            {
                float cy = j * 1.5f;
                float offset = (j & 1) != 0 ? HexPeriodX * 0.5f : 0f;
                for (int i = -1; i <= 1; i++)
                {
                    float cx = i * HexPeriodX + offset;
                    float qx = Mathf.Abs(p.x - cx), qy = Mathf.Abs(p.y - cy);
                    // Hex "norm": boundary where max(qx, qx/2 + qy*sqrt3/2) == inradius.
                    float h = Mathf.Max(qx, qx * 0.5f + qy * 0.8660254f);
                    if (h < best) best = h;
                }
            }
            return Mathf.Max(0f, inradius - best);
        }
    }
}
