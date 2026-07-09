using UnityEngine;

namespace YARG.Gameplay.Visuals.Instancing
{
    /// <summary>
    /// Unit sustain strip: X in [-0.5, 0.5], Z in [0, 1], slight Y lift toward Z=1.
    /// Subdivided so highway-curve / wave vertex deformation has enough verts
    /// (matches SustainLine: many samples across width + along length).
    /// Instance scale: X = width, Z = visible length; translation places the strip.
    /// </summary>
    internal static class SustainUnitMesh
    {
        /// <summary>Along length (Z) — needed for highway curve in VS.</summary>
        private const int LengthSegments = 16;

        /// <summary>Across width (X) — matches prefab Normal SustainLine subdivisions.</summary>
        private const int WidthSegments = 16;

        private static Mesh s_mesh;

        internal static Mesh Mesh
        {
            get
            {
                if (s_mesh == null)
                    s_mesh = Build();
                return s_mesh;
            }
        }

        private static Mesh Build()
        {
            int nx = WidthSegments + 1;
            int nz = LengthSegments + 1;
            int vertCount = nx * nz;
            var vertices = new Vector3[vertCount];
            var normals = new Vector3[vertCount];
            var uvs = new Vector2[vertCount];

            for (int iz = 0; iz < nz; iz++)
            {
                float z = iz / (float)LengthSegments; // 0..1
                float y = 0.01f * z; // slight lift toward far end (SustainLine)
                // UV.x: 1 at head (z=0) → 0 at tail (z=1), same convention as SustainLine ends
                float ux = 1f - z;

                for (int ix = 0; ix < nx; ix++)
                {
                    float t = ix / (float)WidthSegments; // 0..1 across width
                    float x = Mathf.Lerp(-0.5f, 0.5f, t);
                    int i = iz * nx + ix;
                    vertices[i] = new Vector3(x, y, z);
                    normals[i] = Vector3.up;
                    // UV.y across width (1→0) matches SustainLine edge sampling
                    uvs[i] = new Vector2(ux, 1f - t);
                }
            }

            int quadCount = WidthSegments * LengthSegments;
            var triangles = new int[quadCount * 6];
            int tIndex = 0;
            for (int iz = 0; iz < LengthSegments; iz++)
            {
                for (int ix = 0; ix < WidthSegments; ix++)
                {
                    int i00 = iz * nx + ix;
                    int i10 = i00 + 1;
                    int i01 = i00 + nx;
                    int i11 = i01 + 1;

                    // Winding consistent with +Y normal
                    triangles[tIndex++] = i00;
                    triangles[tIndex++] = i01;
                    triangles[tIndex++] = i10;

                    triangles[tIndex++] = i10;
                    triangles[tIndex++] = i01;
                    triangles[tIndex++] = i11;
                }
            }

            var mesh = new Mesh
            {
                name = "SustainUnitStrip",
                indexFormat = vertCount > 65535
                    ? UnityEngine.Rendering.IndexFormat.UInt32
                    : UnityEngine.Rendering.IndexFormat.UInt16
            };
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.uv = uvs;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
            // Keep readable until first BRG register in case bounds are re-queried
            mesh.UploadMeshData(markNoLongerReadable: false);
            return mesh;
        }
    }
}
