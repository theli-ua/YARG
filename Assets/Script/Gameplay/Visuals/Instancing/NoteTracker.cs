using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;
using YARG.Core.Chart;
using YARG.Core.Engine.Keys;
using YARG.Core.Game;
using YARG.Gameplay.Player;
using YARG.Helpers.Extensions;
using YARG.Themes;

namespace YARG.Gameplay.Visuals.Instancing
{
    /// <summary>
    /// CPU-side tracker for instanced note rendering.
    /// Manages flat arrays of note data, spawn data, and batch assignments.
    /// Uploads via <see cref="HighwayElementGraphicsSystem.UploadInstance"/>;
    /// commit is owned by GameManager → EndUploadFrame.
    /// </summary>
    public class NoteTracker : IDisposable
    {
        // CPU-side data — flat arrays, one entry per spawned note
        private NativeArray<NoteData> _notes;
        private NativeArray<NoteSpawnData> _spawnData;
        private int _activeCount; // tracks how many notes are active (swap-remove keeps
        internal int ActiveCount => _activeCount; // diagnostic dense)

        // Reverse lookup: chart note object → flat index
        private Dictionary<object, int> _noteToIndex = new();
        private object[] _noteObjects; // parallel array for swap-remove fixup

        /// <summary>Which color field of NoteData to use for upload.</summary>
        internal enum NoteDataField
        {
            Color,
            ColorNoStarPower,
            MetalColor
        }

        /// <summary>Single batch assignment for a note instance in one render group.</summary>
        internal struct NoteBatchAssignment
        {
            public HighwayElementGraphicsSystem.ElementBatch Batch;
            /// <summary>Which color field to use from NoteData.</summary>
            public NoteDataField ColorField;
            /// <summary>True for metal category (no emission addition bake).</summary>
            public bool IsMetal;
        }

        // Per-note batch assignments: flat index → array of all batch assignments (all render groups)
        // Each note may have multiple assignments (one per render group across Colored/NoStarPower/Metal)
        private NoteBatchAssignment[][] _batchAssignments;

        // Metadata
        private string _themeName;
        private int _highwayIndex;
        private HighwayElementGraphicsSystem _graphicsSystem;
        private TrackPlayer _trackPlayer; // for color/profile access
        private GameManager _gameManager;

        private bool _disposed;

        // Capacity
        private int _capacity;

        internal NoteTracker(int capacity, string themeName, int highwayIndex,
            HighwayElementGraphicsSystem graphicsSystem, TrackPlayer trackPlayer,
            GameManager gameManager)
        {
            _capacity = capacity;
            _themeName = themeName;
            _highwayIndex = highwayIndex;
            _graphicsSystem = graphicsSystem;
            _trackPlayer = trackPlayer;
            _gameManager = gameManager;

            _notes = new NativeArray<NoteData>(capacity, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            _spawnData = new NativeArray<NoteSpawnData>(capacity, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            _noteObjects = new object[capacity];
            _batchAssignments = new NoteBatchAssignment[capacity][];
            _activeCount = 0;
        }

        /// <summary>
        /// Add a new note to the tracker.
        /// Returns the flat index, or -1 if at capacity.
        /// </summary>
        internal int Add(NoteData data, NoteSpawnData spawnData, object noteObject)
        {
            if (_disposed) return -1;
            int index = _activeCount;
            if (index >= _capacity)
            {
                Debug.LogWarning($"[NoteTracker{_highwayIndex}] NOTE CAPACITY HIT: {_activeCount}/{_capacity}, dropping note (type={spawnData.noteType})");
                return -1;
            }

            _notes[index] = data;
            _spawnData[index] = spawnData;
            _noteObjects[index] = noteObject;
            _noteToIndex[noteObject] = index;

            // Look up render groups from ThemeMeshCache
            var renderData = ThemeMeshCache.GetRenderGroups(_themeName, spawnData.noteType, spawnData.isStarPowerVisible);

            // Collect all batch assignments across ALL render groups.
            // batch.activeCount is owned exclusively by per-frame uploads.
            int playerHint = 1;
            if (_gameManager?.Players != null)
                playerHint = Mathf.Max(1, _gameManager.Players.Count);

            var assignments = new List<NoteBatchAssignment>();

            if (renderData.Colored != null)
            {
                for (int i = 0; i < renderData.Colored.Length; i++)
                {
                    var group = renderData.Colored[i];
                    var batch = _graphicsSystem.GetOrCreateBatch(
                        group.Mesh, group.Material, group.SubmeshIndex, group.SourceRendererID,
                        _capacity, group.MeshLocalOffset,
                        group.EmissionAddition, group.EmissionMultiplier, playerHint);
                    if (batch != null)
                    {
                        assignments.Add(new NoteBatchAssignment
                        {
                            Batch = batch,
                            ColorField = NoteDataField.Color,
                            IsMetal = false
                        });
                    }
                }
            }

            if (renderData.NoStarPower != null)
            {
                for (int i = 0; i < renderData.NoStarPower.Length; i++)
                {
                    var group = renderData.NoStarPower[i];
                    var batch = _graphicsSystem.GetOrCreateBatch(
                        group.Mesh, group.Material, group.SubmeshIndex, group.SourceRendererID,
                        _capacity, group.MeshLocalOffset,
                        group.EmissionAddition, group.EmissionMultiplier, playerHint);
                    if (batch != null)
                    {
                        assignments.Add(new NoteBatchAssignment
                        {
                            Batch = batch,
                            ColorField = NoteDataField.ColorNoStarPower,
                            IsMetal = false
                        });
                    }
                }
            }

            if (renderData.Metal != null)
            {
                for (int i = 0; i < renderData.Metal.Length; i++)
                {
                    var group = renderData.Metal[i];
                    var batch = _graphicsSystem.GetOrCreateBatch(
                        group.Mesh, group.Material, group.SubmeshIndex, group.SourceRendererID,
                        _capacity, group.MeshLocalOffset,
                        0f, 1f, playerHint);
                    if (batch != null)
                    {
                        assignments.Add(new NoteBatchAssignment
                        {
                            Batch = batch,
                            ColorField = NoteDataField.MetalColor,
                            IsMetal = true
                        });
                    }
                }
            }

            _batchAssignments[index] = assignments.ToArray();

            if (assignments.Count == 0)
            {
                Debug.LogWarning($"[NoteTracker] No render groups found for theme '{_themeName}', noteType={spawnData.noteType}, isStarPower={spawnData.isStarPowerVisible}");
            }

            _activeCount++;
            return index;
        }

        /// <summary>
        /// Remove a note by its note object reference.
        /// Returns true if the note was found and removed.
        /// </summary>
        internal bool TryRemoveByNote(object noteObject)
        {
            if (_disposed) return false;
            if (noteObject == null) return false;
            if (_noteToIndex.TryGetValue(noteObject, out int idx))
            {
                Remove(idx);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Remove a note by flat index using swap-remove pattern.
        /// </summary>
        internal void Remove(int flatIndex)
        {
            if (_disposed) return;
            if (flatIndex < 0 || flatIndex >= _activeCount) return;

            object noteObj = _noteObjects[flatIndex];
            _noteToIndex.Remove(noteObj);

            // Swap with last active
            int last = _activeCount - 1;
            if (flatIndex != last)
            {
                _notes[flatIndex] = _notes[last];
                _spawnData[flatIndex] = _spawnData[last];
                _noteObjects[flatIndex] = _noteObjects[last];
                _batchAssignments[flatIndex] = _batchAssignments[last];

                object swappedObj = _noteObjects[flatIndex];
                if (swappedObj != null)
                    _noteToIndex[swappedObj] = flatIndex;
            }

            // Clear last slot
            if (flatIndex != last)
            {
                _notes[last] = default;
                _spawnData[last] = default;
            }
            _noteObjects[last] = null;
            _batchAssignments[last] = null;

            // NOTE: batch.activeCount is NOT decremented here — it is owned by per-frame
            // uploads. The next UploadToGPU writes a dense [0..count-1] range and sets
            // activeCount to the actual written count, so removed notes naturally drop out.

            _activeCount--;
        }

        /// <summary>
        /// Remove notes that have passed the remove point.
        /// Uses swap-remove with last active element to preserve batch index integrity.
        /// </summary>
        public void RemoveExpired()
        {
            UnityEngine.Profiling.Profiler.BeginSample("NoteTracker.RemoveExpired");
            try
            {
                if (_disposed) return;
                double visualTime = _gameManager != null ? _gameManager.VisualTime : 0.0;
            float noteSpeed = _trackPlayer?.NoteSpeed ?? 1f;

            // Backward iteration: remove in place without collecting indices (no allocation).
            for (int i = _activeCount - 1; i >= 0; i--)
            {
                float z = TrackPlayer.STRIKE_LINE_POS + ((float)(_spawnData[i].noteHitTime - visualTime)) * noteSpeed;
                if (z < -4f)
                    Remove(i);
            }
            }
            finally
            {
                UnityEngine.Profiling.Profiler.EndSample();
            }
        }

        /// <summary>
        /// Upload note data to GPU for the current frame.
        /// Computes Z position and builds world matrices for each active note.
        /// SP mesh variant switching is deferred (would reassign batches here).
        /// </summary>
        public void UploadToGPU(Matrix4x4 trackLocalToWorld)
        {
            UnityEngine.Profiling.Profiler.BeginSample("NoteTracker.UploadToGPU");
            try
            {
                if (_disposed) return;
                if (_graphicsSystem == null || _activeCount == 0 || _gameManager == null)
                    return;

            double visualTime = _gameManager.VisualTime;
            float noteSpeed = _trackPlayer?.NoteSpeed ?? 1f;

            // Reset all batches' activeCount to 0 for this frame (idempotent across trackers).
            // Batches are SHARED across trackers (same theme → same batch), so this must
            // run once per frame; subsequent trackers append to the same batches.
            _graphicsSystem.BeginUploadFrame();

            for (int i = 0; i < _activeCount; i++)
            {
                var spawn = _spawnData[i];
                var data = _notes[i];

                // Compute Z position
                float z = TrackPlayer.STRIKE_LINE_POS + ((float)(spawn.noteHitTime - visualTime)) * noteSpeed;

                // Build note element local matrix: T(baseX, 0, z) * S(scale)
                Matrix4x4 noteLocal = Matrix4x4.TRS(
                    new Vector3(spawn.baseX, 0f, z),
                    Quaternion.identity,
                    spawn.scale
                );

                // Upload to ALL batch assignments. Slot = batch.activeCount (shared append).
                var assignments = _batchAssignments[i];
                if (assignments == null) continue;
                for (int j = 0; j < assignments.Length; j++)
                {
                    var assignment = assignments[j];
                    if (assignment.Batch == null) continue;

                    Matrix4x4 worldMatrix = trackLocalToWorld * noteLocal * assignment.Batch.meshLocalOffset;

                    Vector4 color = assignment.ColorField switch
                    {
                        NoteDataField.Color => data.color,
                        NoteDataField.ColorNoStarPower => data.colorNoStarPower,
                        NoteDataField.MetalColor => data.metalColor,
                        _ => data.color
                    };

                    // Match NoteGroup.SetColorWithEmission / SetMetalColor:
                    // colored: albedo = color + addition, emission = albedo * multiplier
                    // metal:   albedo = emission = metalColor
                    Vector4 baseColor;
                    Vector4 emission;
                    if (assignment.IsMetal)
                    {
                        baseColor = color;
                        emission = color;
                    }
                    else
                    {
                        float add = assignment.Batch.emissionAddition;
                        float mul = assignment.Batch.emissionMultiplier;
                        baseColor = new Vector4(color.x + add, color.y + add, color.z + add, color.w);
                        emission = new Vector4(baseColor.x * mul, baseColor.y * mul, baseColor.z * mul, baseColor.w);
                    }

                    int pos = assignment.Batch.activeCount;
                    _graphicsSystem.UploadInstance(
                        assignment.Batch, pos, worldMatrix, baseColor, emission,
                        data.randomFloat, data.randomVector);
                }
            }

            // Commit is owned by HighwayElementGraphicsSystem.EndUploadFrame (once/frame).
            }
            finally
            {
                UnityEngine.Profiling.Profiler.EndSample();
            }
        }

        /// <summary>
        /// Reset all note data for reuse.
        /// </summary>
        public void Reset()
        {
            _activeCount = 0;
            _noteToIndex.Clear();
            Array.Clear(_noteObjects, 0, _noteObjects.Length);
            Array.Clear(_batchAssignments, 0, _batchAssignments.Length);

            // Reset batch active counts
            // (batches are shared across trackers — don't reset them here)
        }

        /// <summary>
        /// Dispose NativeArrays and unregister from the graphics system.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_notes.IsCreated) _notes.Dispose();
            if (_spawnData.IsCreated) _spawnData.Dispose();

            _noteToIndex.Clear();
        }

        // ---- Task 4.x: Reverse lookup helpers ----

        /// <summary>Get flat index for a chart note. Returns -1 if not found.</summary>
        internal int GetIndex(object note)
        {
            return _noteToIndex.TryGetValue(note, out int index) ? index : -1;
        }

        /// <summary>Get the NoteData for a chart note (for color mutation on miss).</summary>
        internal NoteData? GetData(object note)
        {
            int index = GetIndex(note);
            if (index < 0) return null;
            return _notes[index];
        }

        /// <summary>Set the color of a note by chart note reference (for miss handling).</summary>
        internal void SetColor(object note, Vector4 color)
        {
            int index = GetIndex(note);
            if (index < 0) return;
            var noteData = _notes[index];
            noteData.color = color;
            _notes[index] = noteData;
        }

        /// <summary>Set the color of a note by flat index (for SP activator pulse).</summary>
        internal void SetColorAt(int index, Vector4 color)
        {
            if (index < 0 || index >= _activeCount) return;
            var noteData = _notes[index];
            noteData.color = color;
            _notes[index] = noteData;
        }

        // ---- Task 10.1: SP activation color updates ----

        /// <summary>
        /// Update colors for all in-flight SP-visible notes when star power state changes.
        /// Called from TrackPlayer.GameplayUpdate when Engine.BaseStats.IsStarPowerActive toggles.
        /// </summary>
        internal void UpdateStarPowerColors(bool isStarPowerActive)
        {
            if (_disposed) return;

            // Handle each instrument type separately to avoid type inference issues
            switch (_trackPlayer)
            {
                case DrumsPlayer drums:
                {
                    if (drums.IsFiveLaneMode)
                    {
                        var colors = drums.Player.ColorProfile.FiveLaneDrums;
                        for (int i = 0; i < _activeCount; i++)
                        {
                            if (!_spawnData[i].isStarPowerVisible) continue;
                            var noteData = _notes[i];
                            int idx = _spawnData[i].colorIndex;
                            noteData.color = isStarPowerActive
                                ? colors.GetNoteStarPowerColor(idx).ToUnityColor()
                                : colors.GetNoteColor(idx).ToUnityColor();
                            noteData.metalColor = colors.GetMetalColor(isStarPowerActive).ToUnityColor();
                            _notes[i] = noteData;
                        }
                    }
                    else
                    {
                        var colors = drums.Player.ColorProfile.FourLaneDrums;
                        for (int i = 0; i < _activeCount; i++)
                        {
                            if (!_spawnData[i].isStarPowerVisible) continue;
                            var noteData = _notes[i];
                            int idx = _spawnData[i].colorIndex;
                            noteData.color = isStarPowerActive
                                ? colors.GetNoteStarPowerColor(idx).ToUnityColor()
                                : colors.GetNoteColor(idx).ToUnityColor();
                            noteData.metalColor = colors.GetMetalColor(isStarPowerActive).ToUnityColor();
                            _notes[i] = noteData;
                        }
                    }
                    break;
                }
                case ProKeysPlayer:
                {
                    var colors = _trackPlayer.Player.ColorProfile.ProKeys;
                    for (int i = 0; i < _activeCount; i++)
                    {
                        if (!_spawnData[i].isStarPowerVisible) continue;
                        var noteData = _notes[i];
                        int key = _spawnData[i].colorIndex;
                        bool isWhite = ProKeysUtilities.IsWhiteKey(key % 12);
                        // ProKeys: color based on white/black key, SP uses StarPower variants
                        noteData.color = isStarPowerActive
                            ? (isWhite ? colors.WhiteNoteStarPower : colors.BlackNoteStarPower).ToUnityColor()
                            : (isWhite ? colors.WhiteNote : colors.BlackNote).ToUnityColor();
                        noteData.metalColor = colors.GetMetalColor(isStarPowerActive).ToUnityColor();
                        _notes[i] = noteData;
                    }
                    break;
                }
                case FiveLaneKeysPlayer:
                {
                    var colors = _trackPlayer.Player.ColorProfile.FiveFretGuitar;
                    for (int i = 0; i < _activeCount; i++)
                    {
                        if (!_spawnData[i].isStarPowerVisible) continue;
                        var noteData = _notes[i];
                        int idx = _spawnData[i].colorIndex;
                        noteData.color = isStarPowerActive
                            ? colors.GetNoteStarPowerColor(idx).ToUnityColor()
                            : colors.GetNoteColor(idx).ToUnityColor();
                        // FiveLaneKeys: metalColor uses constant NoteRef.IsStarPower, don't update
                        _notes[i] = noteData;
                    }
                    break;
                }
                case FiveFretGuitarPlayer:
                default:
                {
                    var colors = _trackPlayer.Player.ColorProfile.FiveFretGuitar;
                    for (int i = 0; i < _activeCount; i++)
                    {
                        if (!_spawnData[i].isStarPowerVisible) continue;
                        var noteData = _notes[i];
                        int idx = _spawnData[i].colorIndex;
                        noteData.color = isStarPowerActive
                            ? colors.GetNoteStarPowerColor(idx).ToUnityColor()
                            : colors.GetNoteColor(idx).ToUnityColor();
                        noteData.metalColor = colors.GetMetalColor(isStarPowerActive).ToUnityColor();
                        _notes[i] = noteData;
                    }
                    break;
                }
            }
        }

        // ---- Task 10.2: Drums SP-activator pulse ----

        /// <summary>
        /// Pulse SP-activator notes by lerping their color based on strong beat progress.
        /// Each note's color is computed individually using its colorIndex.
        /// </summary>
        internal void PulseStarPowerActivators(float beatPercentage, System.Func<int, Vector4> getBaseColor, System.Func<int, Vector4> getPulseColor)
        {
            if (_disposed) return;

            for (int i = 0; i < _activeCount; i++)
            {
                if (!_spawnData[i].isStarPowerActivator) continue;

                int colorIdx = _spawnData[i].colorIndex;
                var baseColor = getBaseColor(colorIdx);
                var pulseColor = getPulseColor(colorIdx);

                var noteData = _notes[i];
                noteData.color = Vector4.Lerp(baseColor, pulseColor, beatPercentage);
                _notes[i] = noteData;
            }
        }
    }
}
