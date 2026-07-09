using System.Runtime.InteropServices;
using UnityEngine;

namespace YARG.Gameplay.Visuals.Instancing
{
    /// <summary>Which theme sustain material strip to use.</summary>
    public enum SustainKind : byte
    {
        Normal = 0,
        Open = 1,
        Wildcard = 2
    }

    public enum SustainHitState : byte
    {
        Waiting = 0,
        Hitting = 1,
        Missed = 2
    }

    /// <summary>CPU sustain instance. Blittable.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SustainInstanceData
    {
        public Vector4 color;
        public float fullLength;   // world Z length at spawn (TimeLength * NoteSpeed)
        public float baseX;
        public float noteHitTime;
        public float whammy;
        public SustainKind kind;
        public SustainHitState state;
        public byte pad0, pad1;
    }
}
