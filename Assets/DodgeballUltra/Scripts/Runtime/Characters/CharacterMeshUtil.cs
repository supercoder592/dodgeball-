using UnityEngine;

namespace DodgeballUltra.Characters
{
    /// <summary>
    /// Small procedural meshes used around the realistic characters (never for the bodies themselves):
    /// the flat team ground ring and the faceted ice crystal of the freeze effect.
    /// </summary>
    public static class CharacterMeshUtil
    {
        private static Mesh s_iceCrystal;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => s_iceCrystal = null;

        /// <summary>
        /// Flat annulus in the XZ plane facing +Y (front faces visible from above). <paramref name="innerRadius"/> 0 gives a
        /// filled disc. UV.x runs around the ring, UV.y from inner (0) to outer (1) edge.
        /// </summary>
        public static Mesh CreateRing(string name, float innerRadius, float outerRadius, int segments = 72)
        {
            segments = Mathf.Clamp(segments, 8, 256);
            innerRadius = Mathf.Max(0f, innerRadius);
            outerRadius = Mathf.Max(innerRadius + 1e-4f, outerRadius);

            int ringVerts = segments + 1;
            var vertices = new Vector3[ringVerts * 2];
            var normals = new Vector3[ringVerts * 2];
            var uvs = new Vector2[ringVerts * 2];
            var triangles = new int[segments * 6];

            for (int i = 0; i < ringVerts; i++)
            {
                float u = (float)i / segments;
                float a = u * Mathf.PI * 2f;
                float c = Mathf.Cos(a);
                float s = Mathf.Sin(a);
                vertices[i] = new Vector3(c * innerRadius, 0f, s * innerRadius);
                vertices[i + ringVerts] = new Vector3(c * outerRadius, 0f, s * outerRadius);
                normals[i] = Vector3.up;
                normals[i + ringVerts] = Vector3.up;
                uvs[i] = new Vector2(u, 0f);
                uvs[i + ringVerts] = new Vector2(u, 1f);
            }

            // Angles grow counter-clockwise seen from above; Unity front faces are clockwise -> (in_i, out_i+1, out_i).
            int t = 0;
            for (int i = 0; i < segments; i++)
            {
                int in0 = i, in1 = i + 1, out0 = i + ringVerts, out1 = i + 1 + ringVerts;
                triangles[t++] = in0;
                triangles[t++] = out1;
                triangles[t++] = out0;
                triangles[t++] = in0;
                triangles[t++] = in1;
                triangles[t++] = out1;
            }

            var mesh = new Mesh { name = name };
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.uv = uvs;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// Shared faceted ice crystal: hexagonal prism (0..0.72) topped by a pyramid (apex at y = 1), radius 1, flat shaded,
        /// no bottom cap (crystals grow out of the floor / the body). Scale it per instance.
        /// </summary>
        public static Mesh IceCrystal
        {
            get
            {
                if (s_iceCrystal != null) return s_iceCrystal;

                const int sides = 6;
                const float prismTop = 0.72f;
                var vertices = new Vector3[sides * 7];
                var triangles = new int[sides * 9];
                int v = 0, t = 0;
                Vector3 apex = new Vector3(0f, 1f, 0f);

                for (int i = 0; i < sides; i++)
                {
                    // Slight irregularity makes the facets catch light differently (natural ice).
                    float a0 = (i + 0f) / sides * Mathf.PI * 2f;
                    float a1 = (i + 1f) / sides * Mathf.PI * 2f;
                    float r0 = 1f - 0.08f * ((i * 7) % 3) / 2f;
                    float r1 = 1f - 0.08f * (((i + 1) % sides * 7) % 3) / 2f;
                    Vector3 b0 = new Vector3(Mathf.Cos(a0) * r0, 0f, Mathf.Sin(a0) * r0);
                    Vector3 b1 = new Vector3(Mathf.Cos(a1) * r1, 0f, Mathf.Sin(a1) * r1);
                    Vector3 t0 = new Vector3(b0.x * 0.93f, prismTop, b0.z * 0.93f);
                    Vector3 t1 = new Vector3(b1.x * 0.93f, prismTop, b1.z * 0.93f);

                    // Side quad (seen from outside: b0 left-bottom, b1 right-bottom) -> clockwise (b0, t0, t1), (b0, t1, b1).
                    int q = v;
                    vertices[v++] = b0;
                    vertices[v++] = t0;
                    vertices[v++] = t1;
                    vertices[v++] = b1;
                    triangles[t++] = q;
                    triangles[t++] = q + 1;
                    triangles[t++] = q + 2;
                    triangles[t++] = q;
                    triangles[t++] = q + 2;
                    triangles[t++] = q + 3;

                    // Tip facet (t0, apex, t1).
                    int p = v;
                    vertices[v++] = t0;
                    vertices[v++] = apex;
                    vertices[v++] = t1;
                    triangles[t++] = p;
                    triangles[t++] = p + 1;
                    triangles[t++] = p + 2;
                }

                var mesh = new Mesh { name = "DU_IceCrystal", hideFlags = HideFlags.DontSave };
                mesh.vertices = vertices;
                mesh.triangles = triangles;
                mesh.RecalculateNormals(); // unique vertices per facet -> flat shading
                mesh.RecalculateBounds();
                s_iceCrystal = mesh;
                return mesh;
            }
        }
    }
}
