using UnityEngine;

namespace DodgeballUltra.Combat
{
    /// <summary>
    /// Smooth UV-sphere mesh for the dodgeball (a sphere is the one shape where a procedural mesh is exactly right).
    /// <para>
    /// Parametrisation (shared with <see cref="BallTextureGenerator"/> so the pebble texture wraps without distortion):
    /// <code>
    /// u = longitude / 2π  (0..1 around +Y),  v = latitude / π  (0 = south pole, 1 = north pole)
    /// P(u, v) = r · ( sinθ cosφ,  -cosθ,  sinθ sinφ )   with φ = 2πu, θ = πv
    /// </code>
    /// Tangents follow +u, bitangents +v (tangent.w = -1 for Unity's <c>cross(normal, tangent) * w</c> convention), so the
    /// tangent-space normal map generated for the pebbled rubber lines up with the mesh. The seam column (u = 0 / 1) is
    /// duplicated so UVs wrap cleanly. One shared instance is created lazily and reused by every ball.
    /// </para>
    /// </summary>
    public static class ProceduralBallMesh
    {
        /// <summary>Segments around the equator. 48 keeps the silhouette perfectly round at close-up hero-select distances.</summary>
        public const int LongitudeSegments = 48;

        /// <summary>Segments from pole to pole.</summary>
        public const int LatitudeSegments = 32;

        private static Mesh s_mesh;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => s_mesh = null;

        /// <summary>Shared sphere of radius <see cref="Core.GameConstants.BallRadius"/> (0.105 m).</summary>
        public static Mesh Get()
        {
            if (s_mesh == null) s_mesh = Build(Core.GameConstants.BallRadius, LongitudeSegments, LatitudeSegments);
            return s_mesh;
        }

        /// <summary>Builds a new UV sphere. Callers own the returned mesh.</summary>
        public static Mesh Build(float radius, int longitudeSegments, int latitudeSegments)
        {
            longitudeSegments = Mathf.Max(3, longitudeSegments);
            latitudeSegments = Mathf.Max(2, latitudeSegments);

            int columns = longitudeSegments + 1; // duplicated seam column
            int rows = latitudeSegments + 1;
            int vertexCount = columns * rows;

            var vertices = new Vector3[vertexCount];
            var normals = new Vector3[vertexCount];
            var tangents = new Vector4[vertexCount];
            var uvs = new Vector2[vertexCount];

            for (int j = 0; j < rows; j++)
            {
                float v = (float)j / latitudeSegments;
                float theta = v * Mathf.PI;
                float sinT = Mathf.Sin(theta);
                float cosT = Mathf.Cos(theta);

                for (int i = 0; i < columns; i++)
                {
                    float u = (float)i / longitudeSegments;
                    float phi = u * 2f * Mathf.PI;
                    float sinP = Mathf.Sin(phi);
                    float cosP = Mathf.Cos(phi);

                    var n = new Vector3(sinT * cosP, -cosT, sinT * sinP);
                    int index = j * columns + i;
                    vertices[index] = n * radius;
                    normals[index] = n;
                    // dP/du is (-sinφ, 0, cosφ) everywhere (also valid at the poles, where it keeps the frame continuous).
                    tangents[index] = new Vector4(-sinP, 0f, cosP, -1f);
                    uvs[index] = new Vector2(u, v);
                }
            }

            // Two triangles per quad. Winding (a, c, b) / (b, c, d) makes cross(edge1, edge2) point outward, which is
            // Unity's front face (clockwise when seen from outside).
            var triangles = new int[longitudeSegments * latitudeSegments * 6];
            int t = 0;
            for (int j = 0; j < latitudeSegments; j++)
            {
                for (int i = 0; i < longitudeSegments; i++)
                {
                    int a = j * columns + i;
                    int b = a + 1;
                    int c = a + columns;
                    int d = c + 1;

                    triangles[t++] = a;
                    triangles[t++] = c;
                    triangles[t++] = b;

                    triangles[t++] = b;
                    triangles[t++] = c;
                    triangles[t++] = d;
                }
            }

            var mesh = new Mesh { name = "DU_DodgeballSphere" };
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.tangents = tangents;
            mesh.uv = uvs;
            mesh.triangles = triangles;
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * (radius * 2f));
            mesh.UploadMeshData(false);
            return mesh;
        }
    }
}
