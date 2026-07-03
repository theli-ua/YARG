using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;
using YARG.Core.Chart;
using YARG.Gameplay.Player;
using YARG.Themes;

namespace YARG.Gameplay.Visuals.Instancing
{
    /// <summary>
    /// CPU-side tracker for instanced note rendering.
    /// Manages flat arrays of note data, spawn data, and batch assignments.
    /// Implements INoteTracker for integration with HighwayElementGraphicsSystem.
    /// </summary>
    public class NoteTracker : INoteTracker, IDisposable
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
            public int LocalIndex;
            /// <summary>Which color field to use from NoteData.</summary>
            public NoteDataField ColorField;
        }

        // Per-note batch assignments: flat index → array of all batch assignments (all render groups)
        // Each note may have multiple assignments (one per render group across Colored/NoStarPower/Metal)
        private NoteBatchAssignment[][] _batchAssignments;

        // Metadata
        private string _themeName;
        private int _highwayIndex;
        private HighwayElementGraphicsSystem _graphicsSystem;
        private TrackPlayer _trackPlayer; // for color/profile access

        private bool _disposed;

        // Capacity
        private int _capacity;

        internal NoteTracker(int capacity, string themeName, int highwayIndex,
            HighwayElementGraphicsSystem graphicsSystem, TrackPlayer trackPlayer)
        {
            _capacity = capacity;
            _themeName = themeName;
            _highwayIndex = highwayIndex;
            _graphicsSystem = graphicsSystem;
            _trackPlayer = trackPlayer;

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
            int index = _activeCount;
            if (index >= _capacity)
                return -1;

            _notes[index] = data;
            _spawnData[index] = spawnData;
            _noteObjects[index] = noteObject;
            _noteToIndex[noteObject] = index;

            // Look up render groups from ThemeMeshCache
            var renderData = ThemeMeshCache.GetRenderGroups(_themeName, spawnData.noteType, spawnData.isStarPowerVisible);

            // Collect all batch assignments across ALL render groups (not just [0])
            var assignments = new List<NoteBatchAssignment>();

            // Iterate ALL Colored render groups
            if (renderData.Colored != null)
            {
                for (int i = 0; i < renderData.Colored.Length; i++)
                {
                    var group = renderData.Colored[i];
                    var batch = _graphicsSystem.GetOrCreateBatch(group.Mesh, group.Material, group.SubmeshIndex, group.SourceRendererID, _capacity, group.MeshLocalOffset);
                    if (batch != null)
                    {
                        assignments.Add(new NoteBatchAssignment
                        {
                            Batch = batch,
                            LocalIndex = batch.activeCount,
                            ColorField = NoteDataField.Color
                        });
                        batch.activeCount++;
                    }
                }
            }

            // Iterate ALL NoStarPower render groups
            if (renderData.NoStarPower != null)
            {
                for (int i = 0; i < renderData.NoStarPower.Length; i++)
                {
                    var group = renderData.NoStarPower[i];
                    var batch = _graphicsSystem.GetOrCreateBatch(group.Mesh, group.Material, group.SubmeshIndex, group.SourceRendererID, _capacity, group.MeshLocalOffset);
                    if (batch != null)
                    {
                        assignments.Add(new NoteBatchAssignment
                        {
                            Batch = batch,
                            LocalIndex = batch.activeCount,
                            ColorField = NoteDataField.ColorNoStarPower
                        });
                        batch.activeCount++;
                    }
                }
            }

            // Iterate ALL Metal render groups
            if (renderData.Metal != null)
            {
                for (int i = 0; i < renderData.Metal.Length; i++)
                {
                    var group = renderData.Metal[i];
                    var batch = _graphicsSystem.GetOrCreateBatch(group.Mesh, group.Material, group.SubmeshIndex, group.SourceRendererID, _capacity, group.MeshLocalOffset);
                    if (batch != null)
                    {
                        assignments.Add(new NoteBatchAssignment
                        {
                            Batch = batch,
                            LocalIndex = batch.activeCount,
                            ColorField = NoteDataField.MetalColor
                        });
                        batch.activeCount++;
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
        /// Gets the visible instance indices for a specific batch.
        /// Returns the actual local indices of active notes in this tracker that belong to the given batch.
        /// </summary>
        internal int[] GetVisibleInstancesForBatch(HighwayElementGraphicsSystem.ElementBatch batch)
        {
            var indices = new List<int>();
            for (int i = 0; i < _activeCount; i++)
            {
                var assignments = _batchAssignments[i];
                if (assignments == null) continue;
                for (int j = 0; j < assignments.Length; j++)
                {
                    if (assignments[j].Batch == batch)
                    {
                        indices.Add(assignments[j].LocalIndex);
                        break; // Each note appears at most once per batch
                    }
                }
            }
            return indices.ToArray();
        }

        /// <summary>
        /// Remove a note by its note object reference.
        /// Returns true if the note was found and removed.
        /// </summary>
        internal bool TryRemoveByNote(object noteObject)
        {
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
            if (flatIndex < 0 || flatIndex >= _activeCount) return;

            // Get the note object for reverse lookup cleanup
            object noteObj = _noteObjects[flatIndex];
            _noteToIndex.Remove(noteObj);

            // Swap with last active
            int last = _activeCount - 1;
            if (flatIndex != last)
            {
                // Swap all arrays
                _notes[flatIndex] = _notes[last];
                _spawnData[flatIndex] = _spawnData[last];
                _noteObjects[flatIndex] = _noteObjects[last];
                _batchAssignments[flatIndex] = _batchAssignments[last];

                // Fixup reverse lookup for swapped-in element
                object swappedObj = _noteObjects[flatIndex];
                if (swappedObj != null)
                    _noteToIndex[swappedObj] = flatIndex;
            }

            // Clear last slot
            _noteObjects[last] = null;

            // Decrement batch active counts for ALL assignments of the removed note
            var removedAssignments = _batchAssignments[last];
            if (removedAssignments != null)
            {
                for (int i = 0; i < removedAssignments.Length; i++)
                {
                    DecrementBatchActiveCount(removedAssignments[i].Batch);
                }
            }
            _batchAssignments[last] = null;

            _activeCount--;
        }

        /// <summary>
        /// Update positions for all active notes.
        /// Phase 1: no-op — Z computation happens in UploadToGPU.
        /// </summary>
        public void UpdatePositions()
        {
            // Positions are computed during UploadToGPU, not here.
            // This method is a no-op for Phase 1 — the actual Z computation
            // happens in UploadToGPU where we have the trackLocalToWorld matrix.
        }

        /// <summary>
        /// Remove notes that have passed the remove point.
        /// Uses swap-remove with last active element to preserve batch index integrity.
        /// </summary>
        public void RemoveExpired()
        {
            float visualTime = (float)(UnityEngine.Object.FindAnyObjectByType<GameManager>()?.VisualTime ?? 0);
            float noteSpeed = _trackPlayer?.NoteSpeed ?? 1f;

            // Collect expired indices first (backward scan for safe removal)
            var expired = new System.Collections.Generic.List<int>();
            for (int i = 0; i < _activeCount; i++)
            {
                float z = TrackPlayer.STRIKE_LINE_POS + (_spawnData[i].noteHitTime - visualTime) * noteSpeed;
                if (z < -4f)
                    expired.Add(i);
            }

            // Remove from back to front to avoid index shifting
            for (int e = expired.Count - 1; e >= 0; e--)
            {
                int idx = expired[e];
                // Adjust index if elements were removed after it
                int last = _activeCount - 1;
                if (idx != last)
                {
                    SwapElements(idx, last);
                }

                object noteObj = _noteObjects[last];
                _noteToIndex.Remove(noteObj);
                var removedAssignments = _batchAssignments[last];
                if (removedAssignments != null)
                {
                    for (int i = 0; i < removedAssignments.Length; i++)
                    {
                        DecrementBatchActiveCount(removedAssignments[i].Batch);
                    }
                }
                _batchAssignments[last] = null;
                _noteObjects[last] = null;
                _activeCount--;
            }
        }

        /// <summary>
        /// Update batch assignments for star power state changes.
        /// Phase 1: placeholder — will be implemented with ThemeMeshCache integration.
        /// </summary>
        public void UpdateBatchAssignments()
        {
            // Phase 1: placeholder. SP state change detection and batch reassignment
            // will be implemented when ThemeMeshCache integration is complete (section 5).
        }

        /// <summary>
        /// Upload note data to GPU for the current frame.
        /// Computes Z position and builds world matrices for each active note.
        /// </summary>
        public void UploadToGPU(Matrix4x4 trackLocalToWorld)
        {
            if (_graphicsSystem == null || _activeCount == 0)
                return;

            // Cache GameManager lookup (called every frame)
            var gameManager = UnityEngine.Object.FindAnyObjectByType<GameManager>();
            float visualTime = (float)(gameManager?.VisualTime ?? 0);
            float noteSpeed = _trackPlayer?.NoteSpeed ?? 1f;

            for (int i = 0; i < _activeCount; i++)
            {
                var spawn = _spawnData[i];
                var data = _notes[i];

                // Compute Z position
                float z = TrackPlayer.STRIKE_LINE_POS + (spawn.noteHitTime - visualTime) * noteSpeed;

                // Build note element local matrix: T(baseX, 0, z) * S(scale)
                float scale = spawn.noteHeight;
                Matrix4x4 noteLocal = Matrix4x4.TRS(
                    new Vector3(spawn.baseX, 0f, z),
                    Quaternion.identity,
                    new Vector3(1f, scale, 1f)
                );

                // Upload to ALL batch assignments (all render groups)
                var assignments = _batchAssignments[i];
                if (assignments == null) continue;
                for (int j = 0; j < assignments.Length; j++)
                {
                    var assignment = assignments[j];
                    if (assignment.Batch == null) continue;

                    Matrix4x4 worldMatrix = trackLocalToWorld * noteLocal * assignment.Batch.meshLocalOffset;

                    // Get the correct color based on the color field
                    Vector4 color = assignment.ColorField switch
                    {
                        NoteDataField.Color => data.color,
                        NoteDataField.ColorNoStarPower => data.colorNoStarPower,
                        NoteDataField.MetalColor => data.metalColor,
                        _ => data.color
                    };

                    _graphicsSystem.UploadInstance(assignment.Batch, assignment.LocalIndex, worldMatrix, color);
                }
            }

            // Flush uploads
            _graphicsSystem.UploadDirtyData(default);
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
            _graphicsSystem?.UnregisterNoteTracker(this);
        }

        // ---- Private helpers ----

        private void DecrementBatchActiveCount(HighwayElementGraphicsSystem.ElementBatch batch)
        {
            if (batch != null && batch.activeCount > 0)
                batch.activeCount--;
        }

        private void SwapElements(int a, int b)
        {
            // Swap all arrays and fixup reverse lookup
            (_notes[a], _notes[b]) = (_notes[b], _notes[a]);
            (_spawnData[a], _spawnData[b]) = (_spawnData[b], _spawnData[a]);
            (_noteObjects[a], _noteObjects[b]) = (_noteObjects[b], _noteObjects[a]);
            (_batchAssignments[a], _batchAssignments[b]) = (_batchAssignments[b], _batchAssignments[a]);

            // Fixup reverse lookup
            if (_noteObjects[a] != null) _noteToIndex[_noteObjects[a]] = a;
            if (_noteObjects[b] != null) _noteToIndex[_noteObjects[b]] = b;
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
    }
}
