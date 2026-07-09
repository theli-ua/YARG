using UnityEngine;

namespace YARG.Gameplay.Visuals.Instancing
{
    /// <summary>
    /// Unit sustain strip: X in [-0.5, 0.5], Z in [0, 1], Y = 0.
    /// Length and width applied via instance scale (S(width, 1, length)).
    /// Start offset applied via translation along Z.
    /// </summary>
    internal static class SustainUnitMesh
    {
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
            // Simple quad strip (1 subdivision) matching SustainLine topology.
            var mesh = new Mesh { name = "SustainUnitStrip" };

            // Start edge Z=0, end edge Z=1. Slight Y lift on end like SustainLine.
            var vertices = new[]
            {
                new Vector3(-0.5f, 0f, 0f),
                new Vector3( 0.5f, 0f, 0f),
                new Vector3(-0.5f, 0.01f, 1f),
                new Vector3( 0.5f, 0.01f, 1f),
            };

            var normals = new[]
            {
                Vector3.up, Vector3.up, Vector3.up, Vector3.up
            };

            // UV.x: 1 at start → 0 at end (relative length; scale Z carries world length)
            var uvs = new[]
            {
                new Vector2(1f, 1f),
                new Vector2(1f, 0f),
                new Vector2(0f, 1f),
                new Vector2(0f, 0f),
            };

            var triangles = new[]
            {
                0, 2, 1,
                1, 2, 3
            };

            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.uv = uvs;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
            mesh.UploadMeshData(markNoLongerReadable: true);
            return mesh;
        }
    }
}
