namespace YARG.Gameplay.Visuals.Instancing
{
    /// <summary>
    /// Fixed upper bounds for highway BRG instancing. No runtime grow / heap GC.
    /// Batches are shared across players, so GPU capacity is per-batch totals.
    /// Tracker CPU arrays stay per-player.
    /// </summary>
    public static class HighwayInstancingLimits
    {
        /// <summary>YARG multiplayer ceiling used for shared-batch sizing.</summary>
        public const int MaxPlayers = 4;

        /// <summary>
        /// Max simultaneous note heads on one highway.
        /// Dense expert + long highway + chords; far below old ObjectCap 2000.
        /// </summary>
        public const int MaxNotesPerPlayer = 512;

        /// <summary>Max simultaneous sustain strips on one highway.</summary>
        public const int MaxSustainsPerPlayer = 256;

        /// <summary>Max simultaneous beatlines on one highway.</summary>
        public const int MaxBeatlinesPerPlayer = 64;

        /// <summary>
        /// Hard cap on BRG batches (mesh×material×submesh×source combos) for one song.
        /// Theme complexity must fit; no buffer grow.
        /// </summary>
        public const int MaxBatches = 96;

        /// <summary>Shared note batch capacity (all players append into same batches).</summary>
        public const int MaxNoteInstances = MaxPlayers * MaxNotesPerPlayer;

        /// <summary>Shared sustain batch capacity.</summary>
        public const int MaxSustainInstances = MaxPlayers * MaxSustainsPerPlayer;

        /// <summary>Shared beatline batch capacity.</summary>
        public const int MaxBeatlineInstances = MaxPlayers * MaxBeatlinesPerPlayer;
    }
}
