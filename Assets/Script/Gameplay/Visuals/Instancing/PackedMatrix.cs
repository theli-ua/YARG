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
                c0x = m.m00, c0y = m.m10, c0z = m.m20,
                c1x = m.m01, c1y = m.m11, c1z = m.m21,
                c2x = m.m02, c2y = m.m12, c2z = m.m22,
                c3x = m.m03, c3y = m.m13, c3z = m.m23,
            };
        }

        /// <summary>
        /// Full inverse via <see cref="Matrix4x4.inverse"/> then pack. Prefer
        /// <see cref="FromAffineInverse"/> for highway notes (pure TRS, no shear).
        /// </summary>
        public static PackedMatrix FromInverse(Matrix4x4 m)
        {
            return FromMatrix4x4(m.inverse);
        }

        /// <summary>
        /// Fast inverse for affine TRS matrices used by highway notes
        /// (orthogonal rotation + non-uniform scale + translation). Avoids
        /// general 4x4 inverse. Falls back to full inverse if scale near-zero.
        /// </summary>
        public static PackedMatrix FromAffineInverse(Matrix4x4 m)
        {
            // Columns of the upper-left 3x3 are scaled basis vectors.
            Vector3 axisX = new Vector3(m.m00, m.m10, m.m20);
            Vector3 axisY = new Vector3(m.m01, m.m11, m.m21);
            Vector3 axisZ = new Vector3(m.m02, m.m12, m.m22);

            float sx2 = axisX.sqrMagnitude;
            float sy2 = axisY.sqrMagnitude;
            float sz2 = axisZ.sqrMagnitude;

            const float eps = 1e-12f;
            if (sx2 < eps || sy2 < eps || sz2 < eps)
                return FromInverse(m);

            // Inverse of R*S is (1/s^2) * R^T for each scaled axis column.
            Vector3 invRow0 = axisX / sx2;
            Vector3 invRow1 = axisY / sy2;
            Vector3 invRow2 = axisZ / sz2;

            Vector3 t = new Vector3(m.m03, m.m13, m.m23);
            // inv translation = -R^T * S^-1 * t
            float tx = -(invRow0.x * t.x + invRow0.y * t.y + invRow0.z * t.z);
            float ty = -(invRow1.x * t.x + invRow1.y * t.y + invRow1.z * t.z);
            float tz = -(invRow2.x * t.x + invRow2.y * t.y + invRow2.z * t.z);

            // Pack as columns of inverse (rows of invRow become columns when transposed back).
            // inv M upper-left = [invRow0; invRow1; invRow2] as rows = columns (invRow0.x, invRow1.x, invRow2.x), ...
            return new PackedMatrix
            {
                c0x = invRow0.x, c0y = invRow1.x, c0z = invRow2.x,
                c1x = invRow0.y, c1y = invRow1.y, c1z = invRow2.y,
                c2x = invRow0.z, c2y = invRow1.z, c2z = invRow2.z,
                c3x = tx,        c3y = ty,        c3z = tz,
            };
        }
    }
}
