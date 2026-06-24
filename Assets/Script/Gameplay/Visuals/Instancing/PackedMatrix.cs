using System.Runtime.InteropServices;
using UnityEngine;

namespace YARG.Gameplay.Visuals.Instancing
{
    /// <summary>
    /// Column-major 4x4 matrix packed into 12 floats (48 bytes).
    /// The w-row (0, 0, 0, 1) is dropped since DOTS instancing shaders
    /// expect a packed float3x4 and implicitly use (0, 0, 0, 1) for the w row.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PackedMatrix
    {
        /// <summary>Column 0: x, y, z (w dropped).</summary>
        public float c0x, c0y, c0z;

        /// <summary>Column 1: x, y, z (w dropped).</summary>
        public float c1x, c1y, c1z;

        /// <summary>Column 2: x, y, z (w dropped).</summary>
        public float c2x, c2y, c2z;

        /// <summary>Column 3 (translation): x, y, z (w dropped).</summary>
        public float c3x, c3y, c3z;

        /// <summary>
        /// Packs a Matrix4x4 into a float3x4 layout by extracting columns
        /// and dropping the w-component (row 3) from each column.
        /// </summary>
        public static PackedMatrix FromMatrix4x4(Matrix4x4 m)
        {
            return new PackedMatrix
            {
                // Column 0: m00, m10, m20 (skip m30)
                c0x = m.m00, c0y = m.m10, c0z = m.m20,
                // Column 1: m01, m11, m21 (skip m31)
                c1x = m.m01, c1y = m.m11, c1z = m.m21,
                // Column 2: m02, m12, m22 (skip m32)
                c2x = m.m02, c2y = m.m12, c2z = m.m22,
                // Column 3: m03, m13, m23 (skip m33)
                c3x = m.m03, c3y = m.m13, c3z = m.m23,
            };
        }

        /// <summary>
        /// Computes the inverse of the given Matrix4x4 and packs it
        /// into a float3x4 layout (same as FromMatrix4x4).
        /// </summary>
        public static PackedMatrix FromInverse(Matrix4x4 m)
        {
            Matrix4x4 inv = m.inverse;
            return new PackedMatrix
            {
                // Column 0: m00, m10, m20 (skip m30)
                c0x = inv.m00, c0y = inv.m10, c0z = inv.m20,
                // Column 1: m01, m11, m21 (skip m31)
                c1x = inv.m01, c1y = inv.m11, c1z = inv.m21,
                // Column 2: m02, m12, m22 (skip m32)
                c2x = inv.m02, c2y = inv.m12, c2z = inv.m22,
                // Column 3: m03, m13, m23 (skip m33)
                c3x = inv.m03, c3y = inv.m13, c3z = inv.m23,
            };
        }
    }
}
