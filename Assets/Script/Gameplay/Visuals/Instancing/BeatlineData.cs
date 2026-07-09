using System.Runtime.InteropServices;
using UnityEngine;
using YARG.Core.Chart;

namespace YARG.Gameplay.Visuals.Instancing
{
    /// <summary>
    /// CPU beatline instance. Blittable. Type is baked into scale/alpha at Add time.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BeatlineInstanceData
    {
        /// <summary>Chart time of this beatline (seconds).</summary>
        public float time;

        /// <summary>Mesh local Y scale after Rx90 (line thickness along track).</summary>
        public float yScale;

        /// <summary>White RGB + type alpha (gamma authoring; linearized on BRG upload).</summary>
        public Vector4 color;

        public static BeatlineInstanceData FromBeatline(Beatline beatline)
        {
            float yScale;
            float alpha;
            switch (beatline.Type)
            {
                case BeatlineType.Measure:
                    yScale = 0.07f;
                    alpha = 0.6f;
                    break;
                case BeatlineType.Strong:
                    yScale = 0.05f;
                    alpha = 0.4f;
                    break;
                case BeatlineType.Weak:
                    yScale = 0.03f;
                    alpha = 0.3f;
                    break;
                default:
                    yScale = 0.03f;
                    alpha = 0.3f;
                    break;
            }

            return new BeatlineInstanceData
            {
                time = (float)beatline.Time,
                yScale = yScale,
                color = new Vector4(1f, 1f, 1f, alpha)
            };
        }
    }
}
