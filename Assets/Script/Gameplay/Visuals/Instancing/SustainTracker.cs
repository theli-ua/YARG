using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using YARG.Gameplay.Player;
using YARG.Gameplay.Visuals;

namespace YARG.Gameplay.Visuals.Instancing
{
    /// <summary>
    /// Instanced sustain strips via unit mesh + per-instance TRS.
    /// Length = scale.z, width = scale.x, start = translation.z relative to note head Z.
    /// No per-frame mesh rebuild.
    /// </summary>
    public class SustainTracker : IDisposable
    {
        private const float MissedLuminance = 0.25f;
        private static readonly Vector4 MissedColor = new(MissedLuminance, MissedLuminance, MissedLuminance, 1f);

        private NativeArray<SustainInstanceData> _data;
        private object[] _noteObjects;
        private Dictionary<object, int> _noteToIndex = new();
        private HighwayElementGraphicsSystem.ElementBatch[] _batches; // per active index

        private readonly string _themeName;
        private readonly int _capacity;
        private int _activeCount;
        private HighwayElementGraphicsSystem _graphics;
        private TrackPlayer _trackPlayer;
        private GameManager _gameManager;
        private bool _disposed;

        // Cached batches per kind
        private HighwayElementGraphicsSystem.ElementBatch _batchNormal;
        private HighwayElementGraphicsSystem.ElementBatch _batchOpen;
        private HighwayElementGraphicsSystem.ElementBatch _batchWildcard;
        private float _widthNormal = 0.1f;
        private float _widthOpen = 0.1f;
        private float _widthWildcard = 0.1f;

        internal int ActiveCount => _activeCount;

        internal SustainTracker(
            int capacity,
            string themeName,
            HighwayElementGraphicsSystem graphics,
            TrackPlayer trackPlayer,
            GameManager gameManager)
        {
            _capacity = capacity;
            _themeName = themeName;
            _graphics = graphics;
            _trackPlayer = trackPlayer;
            _gameManager = gameManager;

            _data = new NativeArray<SustainInstanceData>(capacity, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            _noteObjects = new object[capacity];
            _batches = new HighwayElementGraphicsSystem.ElementBatch[capacity];

            EnsureBatches();
        }

        private void EnsureBatches()
        {
            if (_graphics == null) return;

            int playerHint = 1;
            if (_gameManager?.Players != null)
                playerHint = Mathf.Max(1, _gameManager.Players.Count);

            if (SustainMaterialCache.TryGet(_themeName, SustainKind.Normal, out var matN, out _widthNormal))
                _batchNormal = _graphics.GetOrCreateSustainBatch(matN, _capacity, playerHint);

            if (SustainMaterialCache.TryGet(_themeName, SustainKind.Open, out var matO, out _widthOpen))
                _batchOpen = _graphics.GetOrCreateSustainBatch(matO, _capacity, playerHint);
            else
            {
                _batchOpen = _batchNormal;
                _widthOpen = _widthNormal;
            }

            if (SustainMaterialCache.TryGet(_themeName, SustainKind.Wildcard, out var matW, out _widthWildcard))
                _batchWildcard = _graphics.GetOrCreateSustainBatch(matW, _capacity, playerHint);
            else
            {
                _batchWildcard = _batchNormal;
                _widthWildcard = _widthNormal;
            }

            if (_batchNormal == null)
            {
                Debug.LogError(
                    $"[SustainTracker] No Normal sustain batch for theme '{_themeName}' — " +
                    "strips will not render (material cache miss or HEGS batch create failed)");
            }
        }

        internal int Add(object noteObject, SustainInstanceData data)
        {
            if (_disposed) return -1;
            if (_activeCount >= _capacity)
            {
                Debug.LogWarning($"[SustainTracker] capacity {_capacity} hit, dropping sustain");
                return -1;
            }

            int index = _activeCount;
            _data[index] = data;
            _noteObjects[index] = noteObject;
            _noteToIndex[noteObject] = index;
            _batches[index] = BatchFor(data.kind);
            _activeCount++;
            return index;
        }

        private HighwayElementGraphicsSystem.ElementBatch BatchFor(SustainKind kind) => kind switch
        {
            SustainKind.Open => _batchOpen,
            SustainKind.Wildcard => _batchWildcard,
            _ => _batchNormal
        };

        private float WidthFor(SustainKind kind) => kind switch
        {
            SustainKind.Open => _widthOpen,
            SustainKind.Wildcard => _widthWildcard,
            _ => _widthNormal
        };

        internal bool TryRemoveByNote(object noteObject)
        {
            if (_disposed || noteObject == null) return false;
            if (!_noteToIndex.TryGetValue(noteObject, out int idx))
                return false;
            Remove(idx);
            return true;
        }

        internal void SetState(object noteObject, SustainHitState state, Vector4 color)
        {
            if (_disposed || noteObject == null) return;
            if (!_noteToIndex.TryGetValue(noteObject, out int idx))
                return;

            var d = _data[idx];
            d.state = state;
            if (state == SustainHitState.Missed)
                d.color = MissedColor;
            else
                d.color = color;
            if (state != SustainHitState.Hitting)
                d.whammy = 0f;
            _data[idx] = d;
        }

        internal void SetWhammy(float whammy)
        {
            if (_disposed) return;
            for (int i = 0; i < _activeCount; i++)
            {
                if (_data[i].state != SustainHitState.Hitting)
                    continue;
                var d = _data[i];
                d.whammy = whammy;
                _data[i] = d;
            }
        }

        private void Remove(int flatIndex)
        {
            if (flatIndex < 0 || flatIndex >= _activeCount) return;

            object noteObj = _noteObjects[flatIndex];
            _noteToIndex.Remove(noteObj);

            int last = _activeCount - 1;
            if (flatIndex != last)
            {
                _data[flatIndex] = _data[last];
                _noteObjects[flatIndex] = _noteObjects[last];
                _batches[flatIndex] = _batches[last];
                object swapped = _noteObjects[flatIndex];
                if (swapped != null)
                    _noteToIndex[swapped] = flatIndex;
            }

            _noteObjects[last] = null;
            _batches[last] = null;
            _data[last] = default;
            _activeCount--;
        }

        /// <summary>Drop sustains whose end has passed remove line.</summary>
        public void RemoveExpired()
        {
            if (_disposed || _gameManager == null) return;

            double visualTime = _gameManager.VisualTime;
            float noteSpeed = _trackPlayer?.NoteSpeed ?? 1f;

            for (int i = _activeCount - 1; i >= 0; i--)
            {
                var d = _data[i];
                // End Z of sustain in track space
                float noteZ = TrackPlayer.STRIKE_LINE_POS +
                    ((float)(d.noteHitTime - visualTime)) * noteSpeed;
                float endZ = noteZ + d.fullLength;
                if (endZ < -4f)
                    Remove(i);
            }
        }

        public void UploadToGPU(Matrix4x4 trackLocalToWorld)
        {
            if (_disposed || _graphics == null || _activeCount == 0 || _gameManager == null)
                return;

            double visualTime = _gameManager.VisualTime;
            float noteSpeed = _trackPlayer?.NoteSpeed ?? 1f;

            for (int i = 0; i < _activeCount; i++)
            {
                var d = _data[i];
                var batch = _batches[i];
                if (batch == null) continue;

                float noteZ = TrackPlayer.STRIKE_LINE_POS +
                    ((float)(d.noteHitTime - visualTime)) * noteSpeed;

                // When hitting: clip start to strike line (relative to note head).
                float startZ = 0f;
                if (d.state == SustainHitState.Hitting)
                {
                    // Same as SustainLine: start so world Z of start == STRIKE_LINE
                    startZ = -noteZ + TrackPlayer.STRIKE_LINE_POS;
                    if (startZ < 0f) startZ = 0f;
                    if (startZ > d.fullLength) startZ = d.fullLength;
                }

                float visibleLen = d.fullLength - startZ;
                if (visibleLen < 0.001f)
                    continue;

                float width = WidthFor(d.kind);

                // Unit mesh: X[-0.5,0.5] Z[0,1] → scale (width,1,visibleLen).
                // Y lift matches prefab SustainLine (0.01) so strip is not z-fought into the track.
                Matrix4x4 local = Matrix4x4.TRS(
                    new Vector3(d.baseX, 0.02f, noteZ + startZ),
                    Quaternion.identity,
                    new Vector3(width, 1f, visibleLen));
                Matrix4x4 world = trackLocalToWorld * local;

                Vector4 color = d.color;
                Vector4 emission;
                float isActive;
                switch (d.state)
                {
                    case SustainHitState.Hitting:
                        emission = color * 3f;
                        isActive = 1f;
                        break;
                    case SustainHitState.Missed:
                        color = MissedColor;
                        emission = MissedColor * 0.4f;
                        isActive = 0f;
                        break;
                    default:
                        emission = color;
                        isActive = 0f;
                        break;
                }

                int pos = batch.activeCount;
                _graphics.UploadSustainInstance(
                    batch, pos, world, color, emission, isActive, d.whammy);
            }
        }

        public void Reset()
        {
            _activeCount = 0;
            _noteToIndex.Clear();
            Array.Clear(_noteObjects, 0, _noteObjects.Length);
            Array.Clear(_batches, 0, _batches.Length);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_data.IsCreated) _data.Dispose();
            _noteToIndex.Clear();
        }
    }
}
