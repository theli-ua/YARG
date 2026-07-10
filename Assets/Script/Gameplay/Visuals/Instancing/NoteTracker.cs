using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using YARG.Gameplay.Player;
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
        private int _activeCount; // tracks how many notes are active (swap-remove keeps dense)
        internal int ActiveCount => _activeCount;
        /// <summary>Fixed per-player CPU capacity (from ctor / HighwayInstancingLimits + HEGS clamp).</summary>
        internal int Capacity => _capacity;

        // Reverse lookup: chart note object → flat index
        private Dictionary<object, int> _noteToIndex = new();
        private object[] _noteObjects; // parallel array for swap-remove fixup

        /// <summary>Which color field of NoteData to use for upload.</summary>
        internal enum NoteDataField
        {
            Color = 0,
            ColorNoStarPower = 1,
            MetalColor = 2,
            /// <summary>Fixed white — non-colored theme mesh parts (shells/tops).</summary>
            Static = 3
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

        // Recycle assignment arrays to avoid per-spawn GC (exact-size rent/return).
        private const int MaxPooledAssignmentArrays = 64;
        private readonly List<NoteBatchAssignment[]> _assignmentPool = new(MaxPooledAssignmentArrays);

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

            // Exact-size array from pool (no List/ToArray churn).
            int assignmentCount = CountValidGroups(renderData.Colored)
                + CountValidGroups(renderData.NoStarPower)
                + CountValidGroups(renderData.Metal)
                + CountValidGroups(renderData.Static);
            var assignmentArray = RentAssignments(assignmentCount);
            int w = 0;
            if (assignmentCount > 0)
            {
                w = FillCategoryAssignments(assignmentArray, w, renderData.Colored, NoteDataField.Color,
                    isMetal: false, playerHint, applyEmission: true);
                w = FillCategoryAssignments(assignmentArray, w, renderData.NoStarPower, NoteDataField.ColorNoStarPower,
                    isMetal: false, playerHint, applyEmission: true);
                w = FillCategoryAssignments(assignmentArray, w, renderData.Metal, NoteDataField.MetalColor,
                    isMetal: true, playerHint, applyEmission: false);
                w = FillCategoryAssignments(assignmentArray, w, renderData.Static, NoteDataField.Static,
                    isMetal: false, playerHint, applyEmission: false);
            }

            // Batch create can fail → rent exact used size
            if (w != assignmentCount)
            {
                ReturnAssignments(assignmentArray);
                assignmentArray = RentAssignments(w);
                if (w > 0)
                {
                    // Re-fill into correctly sized array (rare: only when GetOrCreateBatch fails)
                    int w2 = 0;
                    w2 = FillCategoryAssignments(assignmentArray, w2, renderData.Colored, NoteDataField.Color,
                        isMetal: false, playerHint, applyEmission: true);
                    w2 = FillCategoryAssignments(assignmentArray, w2, renderData.NoStarPower, NoteDataField.ColorNoStarPower,
                        isMetal: false, playerHint, applyEmission: true);
                    w2 = FillCategoryAssignments(assignmentArray, w2, renderData.Metal, NoteDataField.MetalColor,
                        isMetal: true, playerHint, applyEmission: false);
                    w2 = FillCategoryAssignments(assignmentArray, w2, renderData.Static, NoteDataField.Static,
                        isMetal: false, playerHint, applyEmission: false);
                }
            }
            _batchAssignments[index] = assignmentArray;

            if (assignmentArray.Length == 0)
            {
                Debug.LogWarning($"[NoteTracker] No render groups found for theme '{_themeName}', noteType={spawnData.noteType}, isStarPower={spawnData.isStarPowerVisible}");
            }

            _activeCount++;
            return index;
        }

        private NoteBatchAssignment[] RentAssignments(int length)
        {
            if (length <= 0)
                return Array.Empty<NoteBatchAssignment>();

            for (int i = _assignmentPool.Count - 1; i >= 0; i--)
            {
                var candidate = _assignmentPool[i];
                if (candidate.Length == length)
                {
                    _assignmentPool.RemoveAt(i);
                    return candidate;
                }
            }

            return new NoteBatchAssignment[length];
        }

        private void ReturnAssignments(NoteBatchAssignment[] assignments)
        {
            if (assignments == null || assignments.Length == 0)
                return;

            Array.Clear(assignments, 0, assignments.Length);
            if (_assignmentPool.Count < MaxPooledAssignmentArrays)
                _assignmentPool.Add(assignments);
        }

        private static int CountValidGroups(RenderGroup[] groups)
        {
            if (groups == null) return 0;
            int n = 0;
            for (int i = 0; i < groups.Length; i++)
            {
                if (groups[i].Mesh != null && groups[i].Material != null)
                    n++;
            }
            return n;
        }

        private int FillCategoryAssignments(
            NoteBatchAssignment[] dest,
            int writeIndex,
            RenderGroup[] groups,
            NoteDataField colorField,
            bool isMetal,
            int playerHint,
            bool applyEmission)
        {
            if (groups == null || _graphicsSystem == null)
                return writeIndex;

            for (int i = 0; i < groups.Length; i++)
            {
                var group = groups[i];
                if (group.Mesh == null || group.Material == null)
                    continue;

                float add = applyEmission ? group.EmissionAddition : 0f;
                float mul = applyEmission ? group.EmissionMultiplier : 1f;
                var batch = _graphicsSystem.GetOrCreateBatch(
                    group.Mesh, group.Material, group.SubmeshIndex, group.SourceRendererID,
                    _capacity, group.MeshLocalOffset, add, mul, playerHint);
                if (batch == null)
                    continue;

                if (writeIndex >= dest.Length)
                    break;

                dest[writeIndex++] = new NoteBatchAssignment
                {
                    Batch = batch,
                    ColorField = colorField,
                    IsMetal = isMetal
                };
            }

            return writeIndex;
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

            var removedAssignments = _batchAssignments[flatIndex];

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

            // Clear last slot + recycle assignment array
            if (flatIndex != last)
            {
                _notes[last] = default;
                _spawnData[last] = default;
            }
            _noteObjects[last] = null;
            _batchAssignments[last] = null;
            ReturnAssignments(removedAssignments);

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
        /// Rebuild batch assignments for chart-SP notes when engine SP toggles so theme
        /// SP mesh variants swap (GetRenderGroups with spActive).
        /// Non-SP chart notes keep spawn-time groups.
        /// </summary>
        internal void UpdateBatchAssignments(bool isStarPowerActive)
        {
            if (_disposed || _graphicsSystem == null) return;

            int playerHint = 1;
            if (_gameManager?.Players != null)
                playerHint = Mathf.Max(1, _gameManager.Players.Count);

            for (int i = 0; i < _activeCount; i++)
            {
                var spawn = _spawnData[i];
                if (!spawn.isStarPowerVisible)
                    continue;

                var renderData = ThemeMeshCache.GetRenderGroups(
                    _themeName, spawn.noteType, isStarPowerActive);

                ReturnAssignments(_batchAssignments[i]);

                int assignmentCount = CountValidGroups(renderData.Colored)
                + CountValidGroups(renderData.NoStarPower)
                + CountValidGroups(renderData.Metal)
                + CountValidGroups(renderData.Static);
                var assignmentArray = RentAssignments(assignmentCount);
                int w = 0;
                if (assignmentCount > 0)
                {
                    w = FillCategoryAssignments(assignmentArray, w, renderData.Colored, NoteDataField.Color,
                        isMetal: false, playerHint, applyEmission: true);
                    w = FillCategoryAssignments(assignmentArray, w, renderData.NoStarPower, NoteDataField.ColorNoStarPower,
                        isMetal: false, playerHint, applyEmission: true);
                    w = FillCategoryAssignments(assignmentArray, w, renderData.Metal, NoteDataField.MetalColor,
                    isMetal: true, playerHint, applyEmission: false);
                w = FillCategoryAssignments(assignmentArray, w, renderData.Static, NoteDataField.Static,
                    isMetal: false, playerHint, applyEmission: false);
                }

                if (w != assignmentCount)
                {
                    ReturnAssignments(assignmentArray);
                    assignmentArray = RentAssignments(w);
                    if (w > 0)
                    {
                        int w2 = 0;
                        w2 = FillCategoryAssignments(assignmentArray, w2, renderData.Colored, NoteDataField.Color,
                            isMetal: false, playerHint, applyEmission: true);
                        w2 = FillCategoryAssignments(assignmentArray, w2, renderData.NoStarPower, NoteDataField.ColorNoStarPower,
                            isMetal: false, playerHint, applyEmission: true);
                        w2 = FillCategoryAssignments(assignmentArray, w2, renderData.Metal, NoteDataField.MetalColor,
                        isMetal: true, playerHint, applyEmission: false);
                    w2 = FillCategoryAssignments(assignmentArray, w2, renderData.Static, NoteDataField.Static,
                        isMetal: false, playerHint, applyEmission: false);
                    }
                }

                _batchAssignments[i] = assignmentArray;
            }
        }

        /// <summary>
        /// Upload note data to GPU for the current frame.
        /// Shared transform SoA: first category write per slot uploads O2W/W2O.
        /// </summary>
        public void UploadToGPU(Matrix4x4 trackLocalToWorld)
        {
            UnityEngine.Profiling.Profiler.BeginSample("NoteTracker.UploadToGPU");
            try
            {
                if (_disposed) return;
                // BeginUploadFrame is owned by GameManager/TrackViewManager (always once/frame).
                // Do not early-out on _activeCount==0 before that boundary — and do not Begin here.
                if (_graphicsSystem == null || _activeCount == 0 || _gameManager == null)
                    return;

            float noteSpeed = _trackPlayer?.NoteSpeed ?? 1f;

            for (int i = 0; i < _activeCount; i++)
            {
                var spawn = _spawnData[i];
                var data = _notes[i];

                // Rest-Z: live Z = restZ - visualTime*noteSpeed applied in highways.hlsl for DOTS.
                float restZ = TrackPlayer.STRIKE_LINE_POS + (float)spawn.noteHitTime * noteSpeed;

                // Build note element local matrix: T(baseX, 0, restZ) * S(scale)
                Matrix4x4 noteLocal = Matrix4x4.TRS(
                    new Vector3(spawn.baseX, 0f, restZ),
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
                        NoteDataField.Static => Vector4.one,
                        _ => data.color
                    };

                    // Match NoteGroup.SetColorWithEmission / SetMetalColor:
                    // colored: albedo = color + addition, emission = albedo * multiplier
                    // metal:   albedo = emission = metalColor
                    // static:  material default look (white instance color, no emission bake)
                    Vector4 baseColor;
                    Vector4 emission;
                    if (assignment.ColorField == NoteDataField.Static)
                    {
                        baseColor = color;
                        emission = Vector4.zero;
                    }
                    else if (assignment.IsMetal)
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
            for (int i = 0; i < _activeCount; i++)
                ReturnAssignments(_batchAssignments[i]);

            _activeCount = 0;
            _noteToIndex.Clear();
            Array.Clear(_noteObjects, 0, _noteObjects.Length);
            Array.Clear(_batchAssignments, 0, _batchAssignments.Length);

            // Batches are shared across trackers — don't reset batch.activeCount here.
        }

        /// <summary>
        /// Dispose NativeArrays and unregister from the graphics system.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            for (int i = 0; i < _activeCount; i++)
                ReturnAssignments(_batchAssignments[i]);
            _activeCount = 0;
            _assignmentPool.Clear();

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
            if (_disposed || _trackPlayer == null) return;

            for (int i = 0; i < _activeCount; i++)
            {
                if (!_spawnData[i].isStarPowerVisible) continue;
                var noteData = _notes[i];
                _trackPlayer.ResolveInstancedStarPowerColors(
                    _spawnData[i].colorIndex, isStarPowerActive, ref noteData);
                _notes[i] = noteData;
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
