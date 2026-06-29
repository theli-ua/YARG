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

        // Per-note batch assignment: flat index → batch reference
        // Each note maps to 3 batches (Colored, NoStarPower, Metal)
        private HighwayElementGraphicsSystem.ElementBatch[] _coloredBatches;
        private HighwayElementGraphicsSystem.ElementBatch[] _noStarPowerBatches;
        private HighwayElementGraphicsSystem.ElementBatch[] _metalBatches;
        // Per-note local indices within each batch
        private int[] _coloredLocalIndices;
        private int[] _noStarPowerLocalIndices;
        private int[] _metalLocalIndices;

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
            _coloredBatches = new HighwayElementGraphicsSystem.ElementBatch[capacity];
            _noStarPowerBatches = new HighwayElementGraphicsSystem.ElementBatch[capacity];
            _metalBatches = new HighwayElementGraphicsSystem.ElementBatch[capacity];
            _coloredLocalIndices = new int[capacity];
            _noStarPowerLocalIndices = new int[capacity];
            _metalLocalIndices = new int[capacity];
            _activeCount = 0;
        }

        /// <summary>
        /// Add a new note to the tracker.
        /// Returns the flat index, or -1 if at capacity.
        /// </summary>
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

            // Colored batch
            if (renderData.Colored != null && renderData.Colored.Length > 0)
            {
                var group = renderData.Colored[0];
                var batch = _graphicsSystem.GetOrCreateBatch(group.Mesh, group.Material, group.SubmeshIndex, group.SourceRendererID, _capacity, group.MeshLocalOffset);
                if (batch != null)
                {
                    _coloredBatches[index] = batch;
                    _coloredLocalIndices[index] = batch.activeCount;
                    batch.activeCount++;
                }
            }

            // NoStarPower batch
            if (renderData.NoStarPower != null && renderData.NoStarPower.Length > 0)
            {
                var group = renderData.NoStarPower[0];
                var batch = _graphicsSystem.GetOrCreateBatch(group.Mesh, group.Material, group.SubmeshIndex, group.SourceRendererID, _capacity, group.MeshLocalOffset);
                if (batch != null)
                {
                    _noStarPowerBatches[index] = batch;
                    _noStarPowerLocalIndices[index] = batch.activeCount;
                    batch.activeCount++;
                }
            }

            // Metal batch
            if (renderData.Metal != null && renderData.Metal.Length > 0)
            {
                var group = renderData.Metal[0];
                var batch = _graphicsSystem.GetOrCreateBatch(group.Mesh, group.Material, group.SubmeshIndex, group.SourceRendererID, _capacity, group.MeshLocalOffset);
                if (batch != null)
                {
                    _metalBatches[index] = batch;
                    _metalLocalIndices[index] = batch.activeCount;
                    batch.activeCount++;
                }
            }

            if (renderData.Colored == null || renderData.Colored.Length == 0)
            {
                _coloredBatches[index] = null;
                _coloredLocalIndices[index] = 0;
            }
            if (renderData.NoStarPower == null || renderData.NoStarPower.Length == 0)
            {
                _noStarPowerBatches[index] = null;
                _noStarPowerLocalIndices[index] = 0;
            }
            if (renderData.Metal == null || renderData.Metal.Length == 0)
            {
                _metalBatches[index] = null;
                _metalLocalIndices[index] = 0;
            }

            if ((renderData.Colored == null || renderData.Colored.Length == 0) &&
                (renderData.NoStarPower == null || renderData.NoStarPower.Length == 0) &&
                (renderData.Metal == null || renderData.Metal.Length == 0))
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
                if (_coloredBatches[i] == batch)
                    indices.Add(_coloredLocalIndices[i]);
                else if (_noStarPowerBatches[i] == batch)
                    indices.Add(_noStarPowerLocalIndices[i]);
                else if (_metalBatches[i] == batch)
                    indices.Add(_metalLocalIndices[i]);
            }
            return indices.ToArray();
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
                _coloredBatches[flatIndex] = _coloredBatches[last];
                _noStarPowerBatches[flatIndex] = _noStarPowerBatches[last];
                _metalBatches[flatIndex] = _metalBatches[last];
                _coloredLocalIndices[flatIndex] = _coloredLocalIndices[last];
                _noStarPowerLocalIndices[flatIndex] = _noStarPowerLocalIndices[last];
                _metalLocalIndices[flatIndex] = _metalLocalIndices[last];

                // Fixup reverse lookup for swapped-in element
                object swappedObj = _noteObjects[flatIndex];
                if (swappedObj != null)
                    _noteToIndex[swappedObj] = flatIndex;
            }

            // Clear last slot
            _noteObjects[last] = null;

            // Decrement batch active counts
            DecrementBatchActiveCount(_coloredBatches[last]);
            DecrementBatchActiveCount(_noStarPowerBatches[last]);
            DecrementBatchActiveCount(_metalBatches[last]);

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
                DecrementBatchActiveCount(_coloredBatches[last]);
                DecrementBatchActiveCount(_noStarPowerBatches[last]);
                DecrementBatchActiveCount(_metalBatches[last]);
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

            for (int i = 0; i < _activeCount; i++)
            {
                var spawn = _spawnData[i];
                var data = _notes[i];

                // Compute Z position
                float visualTime = (float)(UnityEngine.Object.FindAnyObjectByType<GameManager>()?.VisualTime ?? 0);
                float noteSpeed = _trackPlayer?.NoteSpeed ?? 1f;
                float z = TrackPlayer.STRIKE_LINE_POS + (spawn.noteHitTime - visualTime) * noteSpeed;

                // Build note element local matrix: T(baseX, 0, z) * S(scale)
                float scale = spawn.noteHeight;
                Matrix4x4 noteLocal = Matrix4x4.TRS(
                    new Vector3(spawn.baseX, 0f, z),
                    Quaternion.identity,
                    new Vector3(1f, scale, 1f)
                );

                // Upload to colored batch
                if (_coloredBatches[i] != null)
                {
                    var cb = _coloredBatches[i];
                    Matrix4x4 worldMatrix = trackLocalToWorld * noteLocal * cb.meshLocalOffset;
                    _graphicsSystem.UploadInstance(cb, _coloredLocalIndices[i], worldMatrix, data.color);
                }

                // Upload to no-star-power batch
                if (_noStarPowerBatches[i] != null)
                {
                    var nsb = _noStarPowerBatches[i];
                    Matrix4x4 worldMatrixNS = trackLocalToWorld * noteLocal * nsb.meshLocalOffset;
                    _graphicsSystem.UploadInstance(nsb, _noStarPowerLocalIndices[i], worldMatrixNS, data.colorNoStarPower);
                }
                // Upload to metal batch
                if (_metalBatches[i] != null)
                {
                    var mb = _metalBatches[i];
                    Matrix4x4 worldMatrixM = trackLocalToWorld * noteLocal * mb.meshLocalOffset;
                    _graphicsSystem.UploadInstance(mb, _metalLocalIndices[i], worldMatrixM, data.metalColor);
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
            Array.Clear(_coloredBatches, 0, _coloredBatches.Length);
            Array.Clear(_noStarPowerBatches, 0, _noStarPowerBatches.Length);
            Array.Clear(_metalBatches, 0, _metalBatches.Length);
            Array.Clear(_coloredLocalIndices, 0, _coloredLocalIndices.Length);
            Array.Clear(_noStarPowerLocalIndices, 0, _noStarPowerLocalIndices.Length);
            Array.Clear(_metalLocalIndices, 0, _metalLocalIndices.Length);

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
            (_coloredBatches[a], _coloredBatches[b]) = (_coloredBatches[b], _coloredBatches[a]);
            (_noStarPowerBatches[a], _noStarPowerBatches[b]) = (_noStarPowerBatches[b], _noStarPowerBatches[a]);
            (_metalBatches[a], _metalBatches[b]) = (_metalBatches[b], _metalBatches[a]);
            (_coloredLocalIndices[a], _coloredLocalIndices[b]) = (_coloredLocalIndices[b], _coloredLocalIndices[a]);
            (_noStarPowerLocalIndices[a], _noStarPowerLocalIndices[b]) = (_noStarPowerLocalIndices[b], _noStarPowerLocalIndices[a]);
            (_metalLocalIndices[a], _metalLocalIndices[b]) = (_metalLocalIndices[b], _metalLocalIndices[a]);

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
