using System.Runtime.InteropServices;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using YARG.Themes;

namespace YARG.Gameplay.Visuals.Instancing
{
    /// <summary>
    /// Packed note data for DOTS-style instanced rendering. 68 bytes total.
    /// Stored in a StructOfArrays layout alongside NoteSpawnData for GPU instancing.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NoteData
    {
        /// <summary>SP/miss-aware color for ColoredMaterials (WITHOUT EmissionAddition — shader adds it).</summary>
        public Vector4 color;

        /// <summary>Always non-SP fret color for ColoredMaterialsNoStarPower.</summary>
        public Vector4 colorNoStarPower;

        /// <summary>Color for ColoredMetalMaterials (shader uses as both albedo and emission).</summary>
        public Vector4 metalColor;

        /// <summary>From BasePlayer.HighwayIndex.</summary>
        public int highwayIndex;

        /// <summary>Random value [-1, 1] from UnityEngine.Random.</summary>
        public float randomFloat;

        /// <summary>Random 2D vector for theme variation.</summary>
        public Vector2 randomVector;

        /// <summary>
        /// Packed bitfield flags:
        /// - bits 0-7  = noteType (ThemeNoteType cast to byte)
        /// - bit 8     = isStarPower
        /// - bit 9     = isSustain
        /// - bit 10    = isOpenNote
        /// - bits 11-31 = reserved (0)
        /// </summary>
        public uint packedFlags;

        public static readonly int Size = UnsafeUtility.SizeOf<NoteData>();

        static NoteData()
        {
            Debug.Assert(Size == 68, $"NoteData.Size must be 68 bytes, got {Size}");
        }

        /// <summary>
        /// Packs noteType, isStarPower, isSustain, and isOpenNote into a single uint bitfield.
        /// </summary>
        public static uint PackFlags(ThemeNoteType noteType, bool isStarPower, bool isSustain, bool isOpenNote)
        {
            uint flags = (uint)((byte)noteType & 0xFF);

            if (isStarPower)
                flags |= 1u << 8;
            if (isSustain)
                flags |= 1u << 9;
            if (isOpenNote)
                flags |= 1u << 10;

            return flags;
        }

        /// <summary>Extracts the noteType from a packedFlags uint (bits 0-7).</summary>
        public static ThemeNoteType GetNoteType(uint packedFlags)
            => (ThemeNoteType)(packedFlags & 0xFF);

        /// <summary>Extracts the isStarPower flag from a packedFlags uint (bit 8).</summary>
        public static bool GetIsStarPower(uint packedFlags)
            => (packedFlags & (1u << 8)) != 0;

        /// <summary>Extracts the isSustain flag from a packedFlags uint (bit 9).</summary>
        public static bool GetIsSustain(uint packedFlags)
            => (packedFlags & (1u << 9)) != 0;

        /// <summary>Extracts the isOpenNote flag from a packedFlags uint (bit 10).</summary>
        public static bool GetIsOpenNote(uint packedFlags)
            => (packedFlags & (1u << 10)) != 0;
    }

    /// <summary>
    /// Per-note spawn-time data for instanced rendering. 28 bytes total.
    /// Stored in a StructOfArrays layout alongside NoteData for GPU instancing.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NoteSpawnData
    {
        /// <summary>Chart note's hit time (used for Z position).</summary>
        public float noteHitTime;

        /// <summary>Pre-computed X from GetElementX(lane, laneCount) with lefty-flip applied.</summary>
        public float baseX;

        /// <summary>Non-uniform scale for the note instance (X, Y, Z components).</summary>
        public Vector3 scale;

        /// <summary>For render group lookup in ThemeMeshCache.</summary>
        public ThemeNoteType noteType;

        /// <summary>Captured at spawn, updated on SP toggle.</summary>
        public bool isStarPowerVisible;

        /// <summary>True if this note is an SP activator (e.g., drum activation gems).</summary>
        public bool isStarPowerActivator;

        /// <summary>Color index for fret/pad color lookups (fret for guitar/keys, pad for drums, key for ProKeys).</summary>
        public byte colorIndex;

        public static readonly int Size = UnsafeUtility.SizeOf<NoteSpawnData>();

        static NoteSpawnData()
        {
            Debug.Assert(Size == 28, $"NoteSpawnData.Size must be 28 bytes, got {Size}");
        }
    }
}
