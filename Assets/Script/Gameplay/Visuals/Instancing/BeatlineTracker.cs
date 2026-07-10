using System;
using Unity.Collections;
using UnityEngine;
using YARG.Core.Chart;
using YARG.Gameplay.Player;

namespace YARG.Gameplay.Visuals.Instancing
{
    /// <summary>
    /// Instanced highway beatlines via BRG. Single shared mesh+material (no theme variants).
    /// Mirrors the GameObject path: Quad mesh, Rx90, type-dependent Y scale + alpha, Z scroll.
    /// </summary>
    public class BeatlineTracker : IDisposable
    {
        // Prefab mesh child: localPosition.y = 0.002, localRotation X=90°, localScale.x = 2.
        private const float MeshLiftY = 0.002f;
        private const float MeshWidthX = 2f;
        private static readonly Quaternion MeshRotation = Quaternion.Euler(90f, 0f, 0f);

        private NativeArray<BeatlineInstanceData> _data;
        private readonly int _capacity;
        private int _activeCount;
        private HighwayElementGraphicsSystem _graphics;
        private HighwayElementGraphicsSystem.ElementBatch _batch;
        private TrackPlayer _trackPlayer;
        private GameManager _gameManager;
        private bool _disposed;

        private bool _topologyDirty = true;
        private bool _appearanceDirty = true;
        private Matrix4x4 _lastTrackMatrix;
        private float _lastNoteSpeed = float.NaN;

        internal int ActiveCount => _activeCount;
        internal int Capacity => _capacity;

        internal BeatlineTracker(
            int capacity,
            Mesh mesh,
            Material material,
            HighwayElementGraphicsSystem graphics,
            TrackPlayer trackPlayer,
            GameManager gameManager)
        {
            _capacity = capacity;
            _graphics = graphics;
            _trackPlayer = trackPlayer;
            _gameManager = gameManager;

            _data = new NativeArray<BeatlineInstanceData>(
                capacity, Allocator.Persistent, NativeArrayOptions.ClearMemory);

            if (_graphics != null && mesh != null && material != null)
            {
                int playerHint = 1;
                if (_gameManager?.Players != null)
                    playerHint = Mathf.Max(1, _gameManager.Players.Count);

                // Ensure GPU instancing is on (Beatline.mat should already be flagged in assets).
                material.enableInstancing = true;

                _batch = _graphics.GetOrCreateBatch(
                    mesh,
                    material,
                    submeshIndex: 0,
                    sourceRendererID: 0,
                    capacityPerPlayer: capacity,
                    meshLocalOffset: Matrix4x4.identity,
                    emissionAddition: 0f,
                    emissionMultiplier: 0f,
                    playerCountHint: playerHint,
                    useBeatlineCapacity: true);

                if (_batch == null)
                {
                    Debug.LogWarning(
                        "[BeatlineTracker] GetOrCreateBatch returned null — " +
                        "beatlines will not draw (fixed buffer full or AddBatch failed)");
                }
            }
            else
            {
                Debug.LogWarning("[BeatlineTracker] missing mesh/material/graphics — beatlines disabled");
            }
        }

        /// <summary>
        /// Extract shared mesh + material from the legacy Beatline pool prefab hierarchy
        /// (<c>Beatline/Parent/Mesh</c> with MeshFilter + MeshRenderer).
        /// </summary>
        internal static bool TryExtractFromPrefab(GameObject prefab, out Mesh mesh, out Material material)
        {
            mesh = null;
            material = null;
            if (prefab == null) return false;

            var filter = prefab.GetComponentInChildren<MeshFilter>(true);
            var renderer = prefab.GetComponentInChildren<MeshRenderer>(true);
            if (filter == null || renderer == null) return false;

            mesh = filter.sharedMesh;
            material = renderer.sharedMaterial;
            return mesh != null && material != null;
        }

        internal int Add(Beatline beatline)
        {
            if (_disposed) return -1;
            if (_activeCount >= _capacity)
            {
                Debug.LogWarning($"[BeatlineTracker] capacity {_capacity} hit, dropping beatline");
                return -1;
            }

            int index = _activeCount;
            _data[index] = BeatlineInstanceData.FromBeatline(beatline);
            _activeCount++;
            _topologyDirty = true;
            _appearanceDirty = true;
            return index;
        }

        private void Remove(int flatIndex)
        {
            if (flatIndex < 0 || flatIndex >= _activeCount) return;

            int last = _activeCount - 1;
            if (flatIndex != last)
                _data[flatIndex] = _data[last];

            _data[last] = default;
            _activeCount--;
            _topologyDirty = true;
            _appearanceDirty = true;
        }

        internal void CollectUploadDirtiness(Matrix4x4 trackLocalToWorld)
        {
            if (_disposed || _graphics == null) return;
            float noteSpeed = _trackPlayer?.NoteSpeed ?? 1f;
            if (_topologyDirty ||
                trackLocalToWorld != _lastTrackMatrix ||
                noteSpeed != _lastNoteSpeed)
            {
                _graphics.RequestTransformUpload();
            }

            if (_appearanceDirty)
                _graphics.RequestAppearanceUpload();
        }

        /// <summary>Drop beatlines past the remove line (z &lt; -4).</summary>
        public void RemoveExpired()
        {
            if (_disposed || _gameManager == null) return;

            double visualTime = _gameManager.VisualTime;
            float noteSpeed = _trackPlayer?.NoteSpeed ?? 1f;

            for (int i = _activeCount - 1; i >= 0; i--)
            {
                float z = TrackPlayer.STRIKE_LINE_POS +
                    ((float)(_data[i].time - visualTime)) * noteSpeed;
                if (z < -4f)
                    Remove(i);
            }
        }

        public void UploadToGPU(Matrix4x4 trackLocalToWorld)
        {
            if (_disposed || _graphics == null || _batch == null || _gameManager == null)
                return;
            if (_graphics.SkipStagingThisFrame)
                return;
            if (_activeCount == 0)
                return;

            float noteSpeed = _trackPlayer?.NoteSpeed ?? 1f;
            bool writeTransforms = _graphics.UploadTransformsThisFrame;

            for (int i = 0; i < _activeCount; i++)
            {
                var d = _data[i];
                Matrix4x4 world = Matrix4x4.identity;
                if (writeTransforms)
                {
                    float restZ = TrackPlayer.STRIKE_LINE_POS + (float)d.time * noteSpeed;
                    Matrix4x4 local = Matrix4x4.TRS(
                        new Vector3(0f, MeshLiftY, restZ),
                        MeshRotation,
                        new Vector3(MeshWidthX, d.yScale, 1f));
                    world = trackLocalToWorld * local;
                }

                int pos = _batch.activeCount;
                _graphics.UploadInstance(
                    _batch, pos, world, d.color, emissionColor: Vector4.zero,
                    randomFloat: 0f, randomVector: Vector2.zero, writeTransforms);
            }

            if (writeTransforms)
            {
                _topologyDirty = false;
                _lastTrackMatrix = trackLocalToWorld;
                _lastNoteSpeed = noteSpeed;
            }

            if (_graphics.UploadAppearanceThisFrame)
                _appearanceDirty = false;
        }

        public void Reset()
        {
            _activeCount = 0;
            _topologyDirty = true;
            _appearanceDirty = true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_data.IsCreated) _data.Dispose();
            _batch = null;
            _graphics = null;
            _trackPlayer = null;
            _gameManager = null;
        }
    }
}
