using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace DodgeballUltra.Arena
{
    /// <summary>How <see cref="ArenaMeshBuilder.AddBox"/> assigns UVs (always in metres).</summary>
    public enum BoxUvMode
    {
        /// <summary>Planar projection of builder-space coordinates: adjacent pieces continue the texture seamlessly
        /// (floors, block walls whose courses must line up with the floor).</summary>
        World = 0,
        /// <summary>Every face starts at UV (0,0) in its lower-left corner (panels whose seams must align with the piece:
        /// wall pads, doors, fixtures).</summary>
        FaceLocal = 1,
    }

    /// <summary>Faces of an axis box, combinable as flags (skip hidden faces to save vertices and overdraw).</summary>
    [System.Flags]
    public enum BoxFaces
    {
        None = 0,
        PosX = 1 << 0,
        NegX = 1 << 1,
        PosY = 1 << 2,
        NegY = 1 << 3,
        PosZ = 1 << 4,
        NegZ = 1 << 5,
        All = PosX | NegX | PosY | NegY | PosZ | NegZ,
        AllButBottom = All & ~NegY,
    }

    /// <summary>
    /// Accumulates architectural geometry (boxes, quads, discs, rings, tubes) into one mesh with metre-based UVs, normals
    /// and tangents, so a single material/texture set tiles at real-world scale across every piece (texture tiling is
    /// then just "repeats per metre", see <see cref="ArenaTextureSet.Tiling"/>). One builder per material keeps draw calls
    /// low without runtime static batching.
    /// </summary>
    /// <remarks>
    /// UV conventions (viewed from the front of a face, u to the right, v up): +Y faces map (x, z); -Y faces (-x, z);
    /// +X faces (z, y); -X faces (-z, y); +Z faces (-x, y); -Z faces (x, y). Front faces use Unity's clockwise winding.
    /// Not thread-safe; plain C# (usable by editor tools that save the resulting meshes as assets).
    /// </remarks>
    public sealed class ArenaMeshBuilder
    {
        private readonly List<Vector3> m_vertices;
        private readonly List<Vector3> m_normals;
        private readonly List<Vector4> m_tangents;
        private readonly List<Vector2> m_uvs;
        private readonly List<int> m_triangles;

        public ArenaMeshBuilder(int vertexCapacity = 256)
        {
            m_vertices = new List<Vector3>(vertexCapacity);
            m_normals = new List<Vector3>(vertexCapacity);
            m_tangents = new List<Vector4>(vertexCapacity);
            m_uvs = new List<Vector2>(vertexCapacity);
            m_triangles = new List<int>(vertexCapacity * 3 / 2);
        }

        public int VertexCount => m_vertices.Count;
        public bool IsEmpty => m_vertices.Count == 0;

        public void Clear()
        {
            m_vertices.Clear();
            m_normals.Clear();
            m_tangents.Clear();
            m_uvs.Clear();
            m_triangles.Clear();
        }

        // -------------------------------------------------------------------------------------------------------------
        // Primitives
        // -------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Adds a quad given its corners in clockwise order as seen from the front (bottom-left, top-left, top-right,
        /// bottom-right) with per-corner UVs. Normal comes from the geometry; the tangent follows +u.
        /// </summary>
        public void AddQuad(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Vector2 uv0, Vector2 uv1, Vector2 uv2, Vector2 uv3)
        {
            Vector3 n = Vector3.Cross(p1 - p0, p2 - p0);
            if (n.sqrMagnitude < 1e-12f) n = Vector3.Cross(p2 - p0, p3 - p0);
            if (n.sqrMagnitude < 1e-12f) return;   // degenerate quad
            n.Normalize();
            AddQuad(p0, p1, p2, p3, uv0, uv1, uv2, uv3, n, n, n, n);
        }

        /// <summary>
        /// Adds a quad with explicit per-corner normals (smooth tubes). Winding is chosen so the front face points along the
        /// averaged normal, so corner order only needs to be consistent (either clockwise or counter-clockwise).
        /// </summary>
        public void AddQuad(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Vector2 uv0, Vector2 uv1, Vector2 uv2, Vector2 uv3,
            Vector3 n0, Vector3 n1, Vector3 n2, Vector3 n3)
        {
            Vector3 avgN = n0 + n1 + n2 + n3;
            Vector4 tangent = ComputeTangent(p0, p1, p3, uv0, uv1, uv3, avgN.normalized);

            int b = m_vertices.Count;
            m_vertices.Add(p0); m_vertices.Add(p1); m_vertices.Add(p2); m_vertices.Add(p3);
            m_normals.Add(n0); m_normals.Add(n1); m_normals.Add(n2); m_normals.Add(n3);
            m_uvs.Add(uv0); m_uvs.Add(uv1); m_uvs.Add(uv2); m_uvs.Add(uv3);
            for (int i = 0; i < 4; i++) m_tangents.Add(tangent);

            bool clockwise = Vector3.Dot(Vector3.Cross(p1 - p0, p2 - p0), avgN) >= 0f;
            if (clockwise)
            {
                m_triangles.Add(b); m_triangles.Add(b + 1); m_triangles.Add(b + 2);
                m_triangles.Add(b); m_triangles.Add(b + 2); m_triangles.Add(b + 3);
            }
            else
            {
                m_triangles.Add(b); m_triangles.Add(b + 2); m_triangles.Add(b + 1);
                m_triangles.Add(b); m_triangles.Add(b + 3); m_triangles.Add(b + 2);
            }
        }

        /// <summary>Adds a single triangle facing <paramref name="facing"/>.</summary>
        public void AddTriangle(Vector3 p0, Vector3 p1, Vector3 p2, Vector2 uv0, Vector2 uv1, Vector2 uv2, Vector3 facing)
        {
            Vector3 n = Vector3.Cross(p1 - p0, p2 - p0);
            bool clockwise = Vector3.Dot(n, facing) >= 0f;
            n = clockwise ? n.normalized : -n.normalized;
            Vector4 tangent = ComputeTangent(p0, p1, p2, uv0, uv1, uv2, n);

            int b = m_vertices.Count;
            m_vertices.Add(p0); m_vertices.Add(p1); m_vertices.Add(p2);
            m_normals.Add(n); m_normals.Add(n); m_normals.Add(n);
            m_uvs.Add(uv0); m_uvs.Add(uv1); m_uvs.Add(uv2);
            m_tangents.Add(tangent); m_tangents.Add(tangent); m_tangents.Add(tangent);
            if (clockwise) { m_triangles.Add(b); m_triangles.Add(b + 1); m_triangles.Add(b + 2); }
            else { m_triangles.Add(b); m_triangles.Add(b + 2); m_triangles.Add(b + 1); }
        }

        /// <summary>Horizontal rectangle facing up at height <paramref name="y"/>, UVs = builder-space (x, z) in metres.</summary>
        public void AddFloorRect(float xMin, float xMax, float zMin, float zMax, float y)
        {
            if (xMax - xMin <= 1e-5f || zMax - zMin <= 1e-5f) return;
            var p0 = new Vector3(xMin, y, zMin);
            var p1 = new Vector3(xMin, y, zMax);
            var p2 = new Vector3(xMax, y, zMax);
            var p3 = new Vector3(xMax, y, zMin);
            AddQuad(p0, p1, p2, p3, new Vector2(xMin, zMin), new Vector2(xMin, zMax), new Vector2(xMax, zMax), new Vector2(xMax, zMin));
        }

        /// <summary>
        /// Adds an oriented box. <paramref name="center"/>/<paramref name="rotation"/> are in builder space;
        /// <paramref name="size"/> is the full extent along the box's local axes.
        /// </summary>
        public void AddBox(Vector3 center, Vector3 size, Quaternion rotation, BoxUvMode uvMode = BoxUvMode.World,
            BoxFaces faces = BoxFaces.All, Vector2 uvOffset = default)
        {
            Vector3 h = size * 0.5f;
            bool identity = rotation == Quaternion.identity;
            // Local face frames: (normal, u axis, v axis, half extents along n/u/v)
            if ((faces & BoxFaces.PosY) != 0) AddBoxFace(center, rotation, identity, uvMode, uvOffset, Vector3.up, Vector3.right, Vector3.forward, h.y, h.x, h.z);
            if ((faces & BoxFaces.NegY) != 0) AddBoxFace(center, rotation, identity, uvMode, uvOffset, Vector3.down, Vector3.left, Vector3.forward, h.y, h.x, h.z);
            if ((faces & BoxFaces.PosX) != 0) AddBoxFace(center, rotation, identity, uvMode, uvOffset, Vector3.right, Vector3.forward, Vector3.up, h.x, h.z, h.y);
            if ((faces & BoxFaces.NegX) != 0) AddBoxFace(center, rotation, identity, uvMode, uvOffset, Vector3.left, Vector3.back, Vector3.up, h.x, h.z, h.y);
            if ((faces & BoxFaces.PosZ) != 0) AddBoxFace(center, rotation, identity, uvMode, uvOffset, Vector3.forward, Vector3.left, Vector3.up, h.z, h.x, h.y);
            if ((faces & BoxFaces.NegZ) != 0) AddBoxFace(center, rotation, identity, uvMode, uvOffset, Vector3.back, Vector3.right, Vector3.up, h.z, h.x, h.y);
        }

        /// <summary>Axis-aligned box from min/max corners (builder space).</summary>
        public void AddBoxMinMax(Vector3 min, Vector3 max, BoxUvMode uvMode = BoxUvMode.World, BoxFaces faces = BoxFaces.All,
            Vector2 uvOffset = default)
        {
            Vector3 size = max - min;
            if (size.x <= 1e-5f || size.y <= 1e-5f || size.z <= 1e-5f) return;
            AddBox((min + max) * 0.5f, size, Quaternion.identity, uvMode, faces, uvOffset);
        }

        private void AddBoxFace(Vector3 center, Quaternion rot, bool identity, BoxUvMode uvMode, Vector2 uvOffset,
            Vector3 n, Vector3 uAxis, Vector3 vAxis, float hn, float hu, float hv)
        {
            // Corners (local): BL, TL, TR, BR as seen from the front of the face.
            Vector3 c = n * hn;
            Vector3 lbl = c - uAxis * hu - vAxis * hv;
            Vector3 ltl = c - uAxis * hu + vAxis * hv;
            Vector3 ltr = c + uAxis * hu + vAxis * hv;
            Vector3 lbr = c + uAxis * hu - vAxis * hv;

            Vector3 p0, p1, p2, p3;
            if (identity)
            {
                p0 = center + lbl; p1 = center + ltl; p2 = center + ltr; p3 = center + lbr;
            }
            else
            {
                p0 = center + rot * lbl; p1 = center + rot * ltl; p2 = center + rot * ltr; p3 = center + rot * lbr;
            }

            Vector2 uv0, uv1, uv2, uv3;
            if (uvMode == BoxUvMode.World)
            {
                Vector3 wu = identity ? uAxis : rot * uAxis;
                Vector3 wv = identity ? vAxis : rot * vAxis;
                uv0 = new Vector2(Vector3.Dot(p0, wu), Vector3.Dot(p0, wv)) + uvOffset;
                uv1 = new Vector2(Vector3.Dot(p1, wu), Vector3.Dot(p1, wv)) + uvOffset;
                uv2 = new Vector2(Vector3.Dot(p2, wu), Vector3.Dot(p2, wv)) + uvOffset;
                uv3 = new Vector2(Vector3.Dot(p3, wu), Vector3.Dot(p3, wv)) + uvOffset;
            }
            else
            {
                float w = hu * 2f, hgt = hv * 2f;
                uv0 = uvOffset;
                uv1 = uvOffset + new Vector2(0f, hgt);
                uv2 = uvOffset + new Vector2(w, hgt);
                uv3 = uvOffset + new Vector2(w, 0f);
            }

            Vector3 wn = identity ? n : rot * n;
            AddQuad(p0, p1, p2, p3, uv0, uv1, uv2, uv3, wn, wn, wn, wn);
        }

        /// <summary>Flat annulus (ring) in the XZ plane facing up; UVs are builder-space (x, z) metres.</summary>
        public void AddRing(Vector3 center, float innerRadius, float outerRadius, int segments)
        {
            segments = Mathf.Max(8, segments);
            float step = Mathf.PI * 2f / segments;
            for (int i = 0; i < segments; i++)
            {
                float a0 = i * step, a1 = (i + 1) * step;
                var d0 = new Vector3(Mathf.Cos(a0), 0f, Mathf.Sin(a0));
                var d1 = new Vector3(Mathf.Cos(a1), 0f, Mathf.Sin(a1));
                Vector3 i0 = center + d0 * innerRadius, o0 = center + d0 * outerRadius;
                Vector3 i1 = center + d1 * innerRadius, o1 = center + d1 * outerRadius;
                AddQuad(i0, o0, o1, i1, XZ(i0), XZ(o0), XZ(o1), XZ(i1), Vector3.up, Vector3.up, Vector3.up, Vector3.up);
            }
        }

        /// <summary>Flat disc in the XZ plane facing up; UVs are builder-space (x, z) metres.</summary>
        public void AddDisc(Vector3 center, float radius, int segments)
        {
            segments = Mathf.Max(8, segments);
            float step = Mathf.PI * 2f / segments;
            for (int i = 0; i < segments; i++)
            {
                float a0 = i * step, a1 = (i + 1) * step;
                Vector3 p0 = center + new Vector3(Mathf.Cos(a0), 0f, Mathf.Sin(a0)) * radius;
                Vector3 p1 = center + new Vector3(Mathf.Cos(a1), 0f, Mathf.Sin(a1)) * radius;
                AddTriangle(center, p0, p1, XZ(center), XZ(p0), XZ(p1), Vector3.up);
            }
        }

        /// <summary>
        /// Round tube from <paramref name="a"/> to <paramref name="b"/> (truss chords, rails, cables). UV u runs around the
        /// circumference, v along the length, both in metres.
        /// </summary>
        public void AddTube(Vector3 a, Vector3 b, float radius, int sides, bool caps)
        {
            Vector3 axis = b - a;
            float length = axis.magnitude;
            if (length < 1e-5f || radius <= 0f) return;
            Vector3 d = axis / length;
            Vector3 e1 = Mathf.Abs(Vector3.Dot(d, Vector3.up)) < 0.95f ? Vector3.Cross(d, Vector3.up).normalized : Vector3.Cross(d, Vector3.right).normalized;
            Vector3 e2 = Vector3.Cross(d, e1).normalized;
            sides = Mathf.Max(3, sides);
            float step = Mathf.PI * 2f / sides;
            float arc = step * radius;

            for (int i = 0; i < sides; i++)
            {
                float t0 = i * step, t1 = (i + 1) * step;
                Vector3 r0 = e1 * Mathf.Cos(t0) + e2 * Mathf.Sin(t0);
                Vector3 r1 = e1 * Mathf.Cos(t1) + e2 * Mathf.Sin(t1);
                Vector3 p0 = a + r0 * radius, p1 = b + r0 * radius, p2 = b + r1 * radius, p3 = a + r1 * radius;
                float u0 = i * arc, u1 = (i + 1) * arc;
                AddQuad(p0, p1, p2, p3, new Vector2(u0, 0f), new Vector2(u0, length), new Vector2(u1, length), new Vector2(u1, 0f),
                    r0, r0, r1, r1);
            }

            if (!caps) return;
            for (int i = 0; i < sides; i++)
            {
                float t0 = i * step, t1 = (i + 1) * step;
                Vector3 r0 = (e1 * Mathf.Cos(t0) + e2 * Mathf.Sin(t0)) * radius;
                Vector3 r1 = (e1 * Mathf.Cos(t1) + e2 * Mathf.Sin(t1)) * radius;
                var uv0 = new Vector2(Mathf.Cos(t0), Mathf.Sin(t0)) * radius;
                var uv1 = new Vector2(Mathf.Cos(t1), Mathf.Sin(t1)) * radius;
                AddTriangle(b, b + r0, b + r1, Vector2.zero, uv0, uv1, d);
                AddTriangle(a, a + r1, a + r0, Vector2.zero, uv1, uv0, -d);
            }
        }

        // -------------------------------------------------------------------------------------------------------------
        // Output
        // -------------------------------------------------------------------------------------------------------------

        /// <summary>Creates the mesh (32-bit indices when needed). Returns null when nothing was added.</summary>
        public Mesh ToMesh(string name)
        {
            if (m_vertices.Count == 0) return null;
            var mesh = new Mesh { name = name };
            if (m_vertices.Count > 65000) mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(m_vertices);
            mesh.SetNormals(m_normals);
            mesh.SetTangents(m_tangents);
            mesh.SetUVs(0, m_uvs);
            mesh.SetTriangles(m_triangles, 0, true);
            mesh.RecalculateBounds();
            return mesh;
        }

        // -------------------------------------------------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------------------------------------------------

        private static Vector2 XZ(Vector3 p) => new Vector2(p.x, p.z);

        /// <summary>Tangent (+u direction, orthogonalised against n) with handedness in w, from a triangle's UV derivatives.</summary>
        private static Vector4 ComputeTangent(Vector3 p0, Vector3 p1, Vector3 p2, Vector2 uv0, Vector2 uv1, Vector2 uv2, Vector3 n)
        {
            Vector3 e1 = p1 - p0, e2 = p2 - p0;
            Vector2 d1 = uv1 - uv0, d2 = uv2 - uv0;
            float det = d1.x * d2.y - d2.x * d1.y;
            Vector3 t, bt;
            if (Mathf.Abs(det) < 1e-10f)
            {
                // Degenerate UVs: any tangent perpendicular to n.
                t = Mathf.Abs(Vector3.Dot(n, Vector3.right)) < 0.9f ? Vector3.Cross(Vector3.up, n) : Vector3.Cross(Vector3.forward, n);
                if (t.sqrMagnitude < 1e-8f) t = Vector3.right;
                t.Normalize();
                return new Vector4(t.x, t.y, t.z, 1f);
            }
            float r = 1f / det;
            t = (e1 * d2.y - e2 * d1.y) * r;
            bt = (e2 * d1.x - e1 * d2.x) * r;
            t = (t - n * Vector3.Dot(n, t));
            if (t.sqrMagnitude < 1e-12f) t = Vector3.Cross(n, bt);
            t.Normalize();
            // Unity reconstructs the binormal as cross(n, t) * w.
            float w = Vector3.Dot(Vector3.Cross(n, t), bt) < 0f ? -1f : 1f;
            return new Vector4(t.x, t.y, t.z, w);
        }
    }
}
