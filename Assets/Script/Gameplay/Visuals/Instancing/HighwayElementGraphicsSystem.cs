using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Rendering;

namespace YARG.Gameplay.Visuals.Instancing
{
    /// <summary>
    /// Fixed-capacity BRG renderer for highway notes / sustains / beatlines.
    /// Dense per-frame rewrite: CPU staging SoA → contiguous GraphicsBuffer.SetData.
    /// No SparseUploader, no HeapAllocator, no buffer grow.
    /// </summary>
    internal class HighwayElementGraphicsSystem : IDisposable
    {
        private BatchRendererGroup _brg;
        internal BatchRendererGroup BatchRendererGroup => _brg;

        private GraphicsBuffer _gpuBuffer;
        private GraphicsBufferHandle _gpuBufferHandle;

        private readonly Dictionary<BatchKey, ElementBatch> _batches = new();
        private readonly Dictionary<int, BatchMeshID> _meshIDs = new();
        private readonly Dictionary<int, BatchMaterialID> _materialIDs = new();

        // Fixed layout
        private int _zeroPrefixBytes = 64;
        private int _bufferBytes;
        private int _bumpBytes; // next free offset after zero prefix (absolute byte in buffer)
        private int _batchCount;

        // Capacities after ConstantBuffer window clamp
        private int _noteBatchCapacity;
        private int _sustainBatchCapacity;
        private int _beatlineBatchCapacity;

        private int _cullFrameCounter;
        private int _uploadFrame = -1;
        private bool _uploadsOpen;

        private int _highwayCameraID;

        private static readonly bool UseConstantBuffer =
            BatchRendererGroup.BufferTarget == BatchBufferTarget.ConstantBuffer;

        private static readonly int ObjectToWorldID = Shader.PropertyToID("unity_ObjectToWorld");
        private static readonly int WorldToObjectID = Shader.PropertyToID("unity_WorldToObject");
        private static readonly int BaseColorID = Shader.PropertyToID("_BaseColor");
        private static readonly int EmissionColorID = Shader.PropertyToID("_EmissionColor");
        private static readonly int EmissionID = Shader.PropertyToID("_Emission");
        private static readonly int RandomFloatID = Shader.PropertyToID("_RandomFloat");
        private static readonly int RandomVectorID = Shader.PropertyToID("_RandomVector");
        private static readonly int ColorID = Shader.PropertyToID("_Color");
        private static readonly int IsActiveID = Shader.PropertyToID("_IsActive");
        private static readonly int WhammyAmountID = Shader.PropertyToID("_WhammyAmount");

        private const int StrideO2W = 48;
        private const int StrideW2O = 48;
        private const int StrideFloat4 = 16;
        private const int StrideFloat2 = 8; // DOTS float2 (_RandomVector) stride — NOT float4
        private const int StrideFloat = 4;

        // Full note/beatline SoA budget (O2W+W2O+color+em+randF+randV2 + align pad)
        private const int BytesPerNoteInstanceBudget = 160;
        private const int BytesPerSustainInstanceBudget = 144;

        internal static bool DebugLogging { get; set; }

        private bool _disposed;

        #region ElementBatch

        /// <summary>
        /// One mesh/material batch. Staging arrays sized to fixed capacity; GPU offsets fixed at create.
        /// </summary>
        internal class ElementBatch
        {
            public BatchID batchID;
            public BatchMeshID meshID;
            public BatchMaterialID materialID;
            public int submeshIndex;
            public int capacity;
            public int activeCount;

            public int objectToWorldOffset;
            public int worldToObjectOffset;
            public int baseColorOffset;
            public int emissionOffset;
            public int randomFloatOffset;
            public int randomVectorOffset;
            public int isActiveOffset = -1;
            public int whammyOffset = -1;

            public Matrix4x4 meshLocalOffset;
            public float emissionAddition;
            public float emissionMultiplier;

            public int meshKey;
            public int matKey;
            public int sourceRendererID;

            public BatchKind kind;

            // Dense CPU staging (written during Upload*, flushed in EndUploadFrame)
            public NativeArray<PackedMatrix> o2w;
            public NativeArray<PackedMatrix> w2o;
            public NativeArray<Vector4> baseColor;
            public NativeArray<Vector4> emission;
            public NativeArray<float> randomFloat;
            /// <summary>Must match shader DOTS float2 stride (8B), not float4.</summary>
            public NativeArray<Vector2> randomVector;
            public NativeArray<float> isActive;
            public NativeArray<float> whammy;

            public bool dirty;

            public void DisposeStaging()
            {
                if (o2w.IsCreated) o2w.Dispose();
                if (w2o.IsCreated) w2o.Dispose();
                if (baseColor.IsCreated) baseColor.Dispose();
                if (emission.IsCreated) emission.Dispose();
                if (randomFloat.IsCreated) randomFloat.Dispose();
                if (randomVector.IsCreated) randomVector.Dispose();
                if (isActive.IsCreated) isActive.Dispose();
                if (whammy.IsCreated) whammy.Dispose();
            }
        }

        internal enum BatchKind : byte
        {
            NoteOrBeatline = 0,
            Sustain = 1
        }

        #endregion

        #region BatchKey

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        internal struct BatchKey : IEquatable<BatchKey>
        {
            public int meshID;
            public int materialID;
            public int submeshIndex;
            public int sourceRendererID;

            public bool Equals(BatchKey other) =>
                meshID == other.meshID &&
                materialID == other.materialID &&
                submeshIndex == other.submeshIndex &&
                sourceRendererID == other.sourceRendererID;

            public override bool Equals(object obj) => obj is BatchKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = 17;
                    hash = hash * 31 + meshID;
                    hash = hash * 31 + materialID;
                    hash = hash * 31 + submeshIndex;
                    hash = hash * 31 + sourceRendererID;
                    return hash;
                }
            }
        }

        #endregion

        #region Construction / Destruction

        internal void OnCreate()
        {
            int cbufferAlign = UseConstantBuffer
                ? Mathf.Max(16, BatchRendererGroup.GetConstantBufferOffsetAlignment())
                : 16;
            _zeroPrefixBytes = Mathf.Max(64, cbufferAlign);

            // Clamp instance caps so one batch SoA fits ConstantBuffer window (Metal etc.).
            _noteBatchCapacity = ClampCapacityToWindow(
                HighwayInstancingLimits.MaxNoteInstances, BytesPerNoteInstanceBudget, "note");
            _sustainBatchCapacity = ClampCapacityToWindow(
                HighwayInstancingLimits.MaxSustainInstances, BytesPerSustainInstanceBudget, "sustain");
            _beatlineBatchCapacity = ClampCapacityToWindow(
                HighwayInstancingLimits.MaxBeatlineInstances, BytesPerNoteInstanceBudget, "beatline");

            int noteSoA = NoteSoABytes(_noteBatchCapacity);
            int sustainSoA = SustainSoABytes(_sustainBatchCapacity);
            int maxBatchSoA = Mathf.Max(noteSoA, sustainSoA);

            // Fixed buffer: zero prefix + MaxBatches * worst-case SoA (16-aligned).
            _bufferBytes = Align16(_zeroPrefixBytes +
                HighwayInstancingLimits.MaxBatches * Align16(maxBatchSoA + cbufferAlign));

            var bufferTarget = UseConstantBuffer
                ? GraphicsBuffer.Target.Constant
                : GraphicsBuffer.Target.Raw;
            // ConstantBuffer requires 16-byte elements; Raw uses 4-byte for simple float SetData.
            int stride = UseConstantBuffer ? 16 : 4;
            int count = _bufferBytes / stride;
            _gpuBuffer = new GraphicsBuffer(bufferTarget, count, stride);
            _gpuBuffer.name = "HighwayElementGPUBuffer";

            // Zero prefix: BRG safety for unset metadata (addr 0).
            if (UseConstantBuffer)
            {
                var zero = new Vector4[_zeroPrefixBytes / 16];
                _gpuBuffer.SetData(zero);
            }
            else
            {
                var zero = new float[_zeroPrefixBytes / 4];
                _gpuBuffer.SetData(zero);
            }

            _bumpBytes = _zeroPrefixBytes;
            _batchCount = 0;
            _gpuBufferHandle = _gpuBuffer.bufferHandle;

            var createInfo = new BatchRendererGroupCreateInfo
            {
                cullingCallback = OnPerformCullingCallback,
                userContext = IntPtr.Zero
            };
            _brg = new BatchRendererGroup(createInfo);
            _brg.SetEnabledViewTypes(new[] { BatchCullingViewType.Camera });
            _brg.SetGlobalBounds(new Bounds(Vector3.zero, new Vector3(1048576f, 1048576f, 1048576f)));

            if (DebugLogging)
            {
                Debug.Log(
                    $"[HEGS] Fixed init: buffer={_bufferBytes / 1024}KB, " +
                    $"noteCap={_noteBatchCapacity}, sustainCap={_sustainBatchCapacity}, " +
                    $"beatlineCap={_beatlineBatchCapacity}, maxBatches={HighwayInstancingLimits.MaxBatches}, " +
                    $"cb={UseConstantBuffer}");
            }
        }

        private static int ClampCapacityToWindow(int desired, int bytesPerInstanceBudget, string label)
        {
            int cap = desired;

            if (UseConstantBuffer)
            {
                int maxWindow = BatchRendererGroup.GetConstantBufferMaxWindowSize();
                // Leave slack for alignment / metadata tail.
                int maxCap = Mathf.Max(32, (maxWindow - 256) / bytesPerInstanceBudget);
                if (cap > maxCap)
                {
                    Debug.LogWarning(
                        $"[HEGS] {label} capacity {cap} clamped to {maxCap} " +
                        $"(ConstantBuffer window {maxWindow}B, ~{bytesPerInstanceBudget}B/instance)");
                    cap = maxCap;
                }
            }

            // Float SoA regions upload as Vector4 on CB (and pad on Raw harmlessly):
            // capacity must be multiple of 4 so last instances are never truncated.
            cap = (cap / 4) * 4;
            if (cap < 32)
                cap = 32;

            return cap;
        }

        internal void SetHighwayCamera(int cameraInstanceID)
        {
            if (cameraInstanceID == 0)
            {
                Debug.LogError(
                    "[HEGS] SetHighwayCamera(0) — destroyed/null camera. BRG notes will not draw.");
            }
            _highwayCameraID = cameraInstanceID;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            if (_brg != null)
            {
                _brg.Dispose();
                _brg = null;
            }

            foreach (var batch in _batches.Values)
                batch.DisposeStaging();

            _batches.Clear();
            _meshIDs.Clear();
            _materialIDs.Clear();

            if (_gpuBuffer != null)
            {
                _gpuBuffer.Dispose();
                _gpuBuffer = null;
            }
        }

        /// <summary>Fixed shared-batch capacity for note heads (after CB clamp).</summary>
        internal int NoteBatchCapacity => _noteBatchCapacity;

        /// <summary>Fixed shared-batch capacity for sustains (after CB clamp).</summary>
        internal int SustainBatchCapacity => _sustainBatchCapacity;

        /// <summary>Fixed shared-batch capacity for beatlines (after CB clamp).</summary>
        internal int BeatlineBatchCapacity => _beatlineBatchCapacity;

        #endregion

        #region Batch create (bump only)

        /// <summary>
        /// Gets or creates a note/beatline batch. Capacity is fixed (shared across players).
        /// <paramref name="capacityPerPlayer"/> and <paramref name="playerCountHint"/> ignored
        /// (kept for call-site compatibility).
        /// Pass <paramref name="useBeatlineCapacity"/> for beatline batches (smaller fixed cap).
        /// </summary>
        internal ElementBatch GetOrCreateBatch(
            Mesh mesh,
            Material material,
            int submeshIndex,
            int sourceRendererID,
            int capacityPerPlayer = -1,
            Matrix4x4? meshLocalOffset = null,
            float emissionAddition = 0f,
            float emissionMultiplier = 1f,
            int playerCountHint = 1,
            bool useBeatlineCapacity = false)
        {
            if (_disposed || mesh == null || material == null) return null;

            int capacity = useBeatlineCapacity ? _beatlineBatchCapacity : _noteBatchCapacity;

            return GetOrCreateBatchInternal(
                mesh, material, submeshIndex, sourceRendererID,
                capacity, meshLocalOffset ?? Matrix4x4.identity,
                emissionAddition, emissionMultiplier, BatchKind.NoteOrBeatline);
        }

        internal ElementBatch GetOrCreateSustainBatch(
            Material material,
            int capacityPerPlayer = -1,
            int playerCountHint = 1)
        {
            if (_disposed || material == null) return null;

            var mesh = SustainUnitMesh.Mesh;
            return GetOrCreateBatchInternal(
                mesh, material, submeshIndex: 0, sourceRendererID: 0,
                _sustainBatchCapacity, Matrix4x4.identity,
                emissionAddition: 0f, emissionMultiplier: 1f, BatchKind.Sustain);
        }

        private ElementBatch GetOrCreateBatchInternal(
            Mesh mesh,
            Material material,
            int submeshIndex,
            int sourceRendererID,
            int capacity,
            Matrix4x4 meshLocalOffset,
            float emissionAddition,
            float emissionMultiplier,
            BatchKind kind)
        {
            int meshKey = mesh.GetInstanceID();
            int matKey = material.GetInstanceID();

            if (!_meshIDs.TryGetValue(meshKey, out var meshID))
            {
                meshID = _brg.RegisterMesh(mesh);
                _meshIDs[meshKey] = meshID;
            }

            if (!_materialIDs.TryGetValue(matKey, out var materialID))
            {
                materialID = _brg.RegisterMaterial(material);
                _materialIDs[matKey] = materialID;
            }

            var key = new BatchKey
            {
                meshID = meshKey,
                materialID = matKey,
                submeshIndex = submeshIndex,
                sourceRendererID = sourceRendererID
            };

            if (_batches.TryGetValue(key, out var existing))
                return existing;

            if (_batchCount >= HighwayInstancingLimits.MaxBatches)
            {
                Debug.LogError(
                    $"[HEGS] MaxBatches ({HighwayInstancingLimits.MaxBatches}) reached — cannot create more batches");
                return null;
            }

            int soaBytes = kind == BatchKind.Sustain
                ? SustainSoABytes(capacity)
                : NoteSoABytes(capacity);

            if (!TryBump(soaBytes, out int begin))
            {
                Debug.LogError(
                    $"[HEGS] Fixed buffer full ({_bufferBytes}B) — cannot allocate {soaBytes}B batch. " +
                    $"batches={_batchCount}/{HighwayInstancingLimits.MaxBatches}");
                return null;
            }

            int o2w, w2o, baseColor, emission, randomFloat, randomVector, isActive, whammy;
            if (kind == BatchKind.Sustain)
            {
                ComputeSustainOffsets(begin, capacity,
                    out o2w, out w2o, out baseColor, out emission, out isActive, out whammy);
                randomFloat = -1;
                randomVector = -1;
            }
            else
            {
                ComputeNoteOffsets(begin, capacity,
                    out o2w, out w2o, out baseColor, out emission, out randomFloat, out randomVector);
                isActive = -1;
                whammy = -1;
            }

            if (!TryAddBatch(kind, o2w, w2o, baseColor, emission, randomFloat, randomVector,
                    isActive, whammy, begin, soaBytes, out BatchID batchID))
            {
                // Bump already consumed — fixed layout, cannot free. Leave hole.
                Debug.LogError("[HEGS] AddBatch failed after bump — batch slot wasted");
                return null;
            }

            var batch = new ElementBatch
            {
                batchID = batchID,
                meshID = meshID,
                materialID = materialID,
                submeshIndex = submeshIndex,
                capacity = capacity,
                activeCount = 0,
                objectToWorldOffset = o2w,
                worldToObjectOffset = w2o,
                baseColorOffset = baseColor,
                emissionOffset = emission,
                randomFloatOffset = randomFloat,
                randomVectorOffset = randomVector,
                isActiveOffset = isActive,
                whammyOffset = whammy,
                meshLocalOffset = meshLocalOffset,
                emissionAddition = emissionAddition,
                emissionMultiplier = emissionMultiplier,
                meshKey = meshKey,
                matKey = matKey,
                sourceRendererID = sourceRendererID,
                kind = kind,
                dirty = false
            };

            // Fixed staging — allocated once, never resized.
            batch.o2w = new NativeArray<PackedMatrix>(capacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            batch.w2o = new NativeArray<PackedMatrix>(capacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            batch.baseColor = new NativeArray<Vector4>(capacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            batch.emission = new NativeArray<Vector4>(capacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

            if (kind == BatchKind.Sustain)
            {
                batch.isActive = new NativeArray<float>(capacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                batch.whammy = new NativeArray<float>(capacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }
            else
            {
                batch.randomFloat = new NativeArray<float>(capacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                batch.randomVector = new NativeArray<Vector2>(capacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }

            _batches[key] = batch;
            _batchCount++;
            return batch;
        }

        private bool TryBump(int sizeBytes, out int absoluteBegin)
        {
            sizeBytes = Align16(sizeBytes);
            int align = UseConstantBuffer
                ? Mathf.Max(16, BatchRendererGroup.GetConstantBufferOffsetAlignment())
                : 16;

            // Align bump so ConstantBuffer bindOffset is valid.
            int alignedBump = (_bumpBytes + align - 1) / align * align;
            if (alignedBump + sizeBytes > _bufferBytes)
            {
                absoluteBegin = 0;
                return false;
            }

            absoluteBegin = alignedBump;
            _bumpBytes = alignedBump + sizeBytes;
            return true;
        }

        private bool TryAddBatch(
            BatchKind kind,
            int o2w, int w2o, int baseColor, int emission,
            int randomFloat, int randomVector,
            int isActive, int whammy,
            int allocationBegin,
            int allocationBytes,
            out BatchID batchID)
        {
            batchID = default;

            uint bindOffset = 0;
            uint windowSize = 0;
            int metaBase = 0;

            if (UseConstantBuffer)
            {
                int align = Mathf.Max(16, BatchRendererGroup.GetConstantBufferOffsetAlignment());
                int maxWindow = BatchRendererGroup.GetConstantBufferMaxWindowSize();
                bindOffset = (uint)allocationBegin;
                if (bindOffset % (uint)align != 0)
                {
                    Debug.LogError($"[HEGS] CB bindOffset {bindOffset} not aligned to {align}");
                    return false;
                }

                if (allocationBytes > maxWindow)
                {
                    Debug.LogError(
                        $"[HEGS] Batch SoA ({allocationBytes}B) exceeds CB window ({maxWindow}B)");
                    return false;
                }

                int bufferBytes = _gpuBuffer.count * _gpuBuffer.stride;
                int maxFrom = bufferBytes - allocationBegin;
                if (allocationBytes > maxFrom)
                {
                    Debug.LogError(
                        $"[HEGS] Batch SoA past buffer end (need {allocationBytes}, avail {maxFrom})");
                    return false;
                }

                windowSize = (uint)Mathf.Min(maxWindow, maxFrom);
                metaBase = allocationBegin;
            }

            NativeArray<MetadataValue> metadata;
            if (kind == BatchKind.Sustain)
            {
                metadata = new NativeArray<MetadataValue>(7, Allocator.Temp);
                metadata[0] = Meta(ObjectToWorldID, o2w - metaBase);
                metadata[1] = Meta(WorldToObjectID, w2o - metaBase);
                metadata[2] = Meta(BaseColorID, baseColor - metaBase);
                metadata[3] = Meta(ColorID, baseColor - metaBase);
                metadata[4] = Meta(EmissionColorID, emission - metaBase);
                metadata[5] = Meta(IsActiveID, isActive - metaBase);
                metadata[6] = Meta(WhammyAmountID, whammy - metaBase);
            }
            else
            {
                metadata = new NativeArray<MetadataValue>(8, Allocator.Temp);
                metadata[0] = Meta(ObjectToWorldID, o2w - metaBase);
                metadata[1] = Meta(WorldToObjectID, w2o - metaBase);
                metadata[2] = Meta(BaseColorID, baseColor - metaBase);
                metadata[3] = Meta(ColorID, baseColor - metaBase);
                metadata[4] = Meta(EmissionColorID, emission - metaBase);
                metadata[5] = Meta(EmissionID, emission - metaBase);
                metadata[6] = Meta(RandomFloatID, randomFloat - metaBase);
                metadata[7] = Meta(RandomVectorID, randomVector - metaBase);
            }

            batchID = UseConstantBuffer
                ? _brg.AddBatch(metadata, _gpuBufferHandle, bindOffset, windowSize)
                : _brg.AddBatch(metadata, _gpuBufferHandle);
            metadata.Dispose();
            return true;
        }

        private static MetadataValue Meta(int nameId, int byteOffset) => new MetadataValue
        {
            NameID = nameId,
            Value = 0x80000000u | (uint)byteOffset
        };

        private static int Align16(int value) => (value + 15) & ~15;

        private static int NoteSoABytes(int capacity)
        {
            // O2W + W2O + color + emission + randomFloat + randomVector(float2)
            // Strides must match DOTS LoadDOTSInstancedData_* sizeof (float=4, float2=8, float4=16).
            int n = StrideO2W * capacity + StrideW2O * capacity +
                    StrideFloat4 * capacity + StrideFloat4 * capacity;
            n = Align16(n);
            n += Align16(StrideFloat * capacity);
            n += Align16(StrideFloat2 * capacity);
            return Align16(n);
        }

        private static int SustainSoABytes(int capacity)
        {
            int n = StrideO2W * capacity + StrideW2O * capacity +
                    StrideFloat4 * capacity + StrideFloat4 * capacity;
            n = Align16(n);
            n += Align16(StrideFloat * capacity); // isActive
            n += Align16(StrideFloat * capacity); // whammy
            return Align16(n);
        }

        private static void ComputeNoteOffsets(
            int begin, int capacity,
            out int o2w, out int w2o, out int baseColor, out int emission,
            out int randomFloat, out int randomVector)
        {
            o2w = begin;
            w2o = o2w + StrideO2W * capacity;
            baseColor = w2o + StrideW2O * capacity;
            emission = baseColor + StrideFloat4 * capacity;
            randomFloat = Align16(emission + StrideFloat4 * capacity);
            // float2 SoA: 8 bytes/instance (shader Vector2). Packing as float4 broke instance i>0.
            randomVector = Align16(randomFloat + StrideFloat * capacity);
        }

        private static void ComputeSustainOffsets(
            int begin, int capacity,
            out int o2w, out int w2o, out int baseColor, out int emission,
            out int isActive, out int whammy)
        {
            o2w = begin;
            w2o = o2w + StrideO2W * capacity;
            baseColor = w2o + StrideW2O * capacity;
            emission = baseColor + StrideFloat4 * capacity;
            isActive = Align16(emission + StrideFloat4 * capacity);
            whammy = Align16(isActive + StrideFloat * capacity);
        }

        #endregion

        #region Upload frame

        internal void BeginUploadFrame()
        {
            UnityEngine.Profiling.Profiler.BeginSample("HEGS.BeginUploadFrame");
            try
            {
                if (_disposed) return;
                int frame = Time.frameCount;
                if (_uploadFrame == frame) return;

                // Previous frame not flushed — still flush dense staging if marked dirty.
                if (_uploadsOpen)
                    CommitUploadsOnly();

                _uploadFrame = frame;
                _uploadsOpen = true;
                foreach (var batch in _batches.Values)
                {
                    batch.activeCount = 0;
                    batch.dirty = false;
                }
            }
            finally
            {
                UnityEngine.Profiling.Profiler.EndSample();
            }
        }

        internal void EndUploadFrame()
        {
            if (_disposed) return;

            UnityEngine.Profiling.Profiler.BeginSample("HEGS.EndUploadFrame");
            try
            {
                CommitUploadsOnly();
            }
            finally
            {
                UnityEngine.Profiling.Profiler.EndSample();
            }
        }

        private void CommitUploadsOnly()
        {
            if (!_uploadsOpen)
                return;

            foreach (var batch in _batches.Values)
            {
                if (!batch.dirty || batch.activeCount <= 0)
                    continue;

                FlushBatch(batch);
                batch.dirty = false;
            }

            _uploadsOpen = false;
        }

        private void FlushBatch(ElementBatch batch)
        {
            int n = batch.activeCount;
            UploadRegion(batch.o2w, n, batch.objectToWorldOffset);
            UploadRegion(batch.w2o, n, batch.worldToObjectOffset);
            UploadRegion(batch.baseColor, n, batch.baseColorOffset);
            UploadRegion(batch.emission, n, batch.emissionOffset);

            if (batch.kind == BatchKind.Sustain)
            {
                UploadRegion(batch.isActive, n, batch.isActiveOffset);
                UploadRegion(batch.whammy, n, batch.whammyOffset);
            }
            else
            {
                UploadRegion(batch.randomFloat, n, batch.randomFloatOffset);
                UploadRegion(batch.randomVector, n, batch.randomVectorOffset);
            }
        }

        /// <summary>
        /// Contiguous dense upload: staging[0..count) → GPU at byteOffset.
        /// Raw: stride 4 (float indices). ConstantBuffer: stride 16 (Vector4 indices).
        /// Float regions pad upload count up to 16-byte multiple (capacity has headroom).
        /// </summary>
        private void UploadRegion<T>(NativeArray<T> staging, int count, int byteOffset)
            where T : unmanaged
        {
            if (count <= 0 || !staging.IsCreated || _gpuBuffer == null)
                return;

            int elemSize = UnsafeUtility.SizeOf<T>();
            int byteCount = count * elemSize;

            if (UseConstantBuffer)
            {
                // Pad to 16-byte multiple so Vector4 SetData is valid for float streams.
                if ((byteOffset & 15) != 0)
                {
                    Debug.LogError($"[HEGS] CB UploadRegion offset {byteOffset} not 16-aligned");
                    return;
                }

                int paddedBytes = Align16(byteCount);
                int paddedElems = paddedBytes / elemSize;
                if (paddedElems > staging.Length)
                    paddedElems = (staging.Length * elemSize / 16) * 16 / elemSize;

                // Zero pad tail when expanding float/small regions.
                for (int i = count; i < paddedElems; i++)
                    staging[i] = default;

                var v4 = staging.GetSubArray(0, paddedElems).Reinterpret<Vector4>(elemSize);
                int v4Offset = byteOffset / 16;
                int v4Count = paddedElems * elemSize / 16;
                _gpuBuffer.SetData(v4, 0, v4Offset, v4Count);
            }
            else
            {
                if ((byteOffset & 3) != 0)
                {
                    Debug.LogError($"[HEGS] UploadRegion misaligned offset={byteOffset}");
                    return;
                }

                var floats = staging.GetSubArray(0, count).Reinterpret<float>(elemSize);
                _gpuBuffer.SetData(floats, 0, byteOffset / 4, byteCount / 4);
            }
        }

        private static Vector4 ToLinearGpuColor(Vector4 gamma)
        {
            Color linear = ((Color)gamma).linear;
            return new Vector4(linear.r, linear.g, linear.b, linear.a);
        }

        internal void UploadInstance(
            ElementBatch batch,
            int instanceIndex,
            Matrix4x4 objectToWorld,
            Vector4 baseColor,
            Vector4 emissionColor,
            float randomFloat,
            Vector2 randomVector)
        {
            if (_disposed || batch == null)
                return;

            if (instanceIndex < 0 || instanceIndex >= batch.capacity)
            {
                Debug.LogWarning(
                    $"[HEGS] BATCH OVERFLOW: instanceIndex={instanceIndex} >= capacity={batch.capacity}");
                return;
            }

            batch.o2w[instanceIndex] = PackedMatrix.FromMatrix4x4(objectToWorld);
            batch.w2o[instanceIndex] = PackedMatrix.FromAffineInverse(objectToWorld);
            batch.baseColor[instanceIndex] = ToLinearGpuColor(baseColor);
            batch.emission[instanceIndex] = emissionColor;
            batch.randomFloat[instanceIndex] = randomFloat;
            batch.randomVector[instanceIndex] = randomVector;
            batch.dirty = true;

            if (instanceIndex >= batch.activeCount)
                batch.activeCount = instanceIndex + 1;
        }

        internal void UploadSustainInstance(
            ElementBatch batch,
            int instanceIndex,
            Matrix4x4 objectToWorld,
            Vector4 baseColor,
            Vector4 emissionColor,
            float isActive,
            float whammyAmount)
        {
            if (_disposed || batch == null)
                return;

            if (instanceIndex < 0 || instanceIndex >= batch.capacity)
            {
                Debug.LogWarning(
                    $"[HEGS] SUSTAIN OVERFLOW: {instanceIndex} >= {batch.capacity}");
                return;
            }

            if (batch.kind != BatchKind.Sustain)
                return;

            batch.o2w[instanceIndex] = PackedMatrix.FromMatrix4x4(objectToWorld);
            batch.w2o[instanceIndex] = PackedMatrix.FromAffineInverse(objectToWorld);
            batch.baseColor[instanceIndex] = ToLinearGpuColor(baseColor);
            batch.emission[instanceIndex] = emissionColor;
            batch.isActive[instanceIndex] = isActive;
            batch.whammy[instanceIndex] = whammyAmount;
            batch.dirty = true;

            if (instanceIndex >= batch.activeCount)
                batch.activeCount = instanceIndex + 1;
        }

        #endregion

        #region Culling

        private unsafe JobHandle OnPerformCullingCallback(
            BatchRendererGroup rendererGroup,
            BatchCullingContext cullingContext,
            BatchCullingOutput cullingOutput,
            IntPtr userContext)
        {
            UnityEngine.Profiling.Profiler.BeginSample("HEGS.OnPerformCulling");
            try
            {
                if (_disposed || _brg == null)
                    return default;

                if (_highwayCameraID == 0)
                {
                    if (DebugLogging)
                        Debug.LogError("[HEGS] OnPerformCulling: highway camera ID unset");
                    return default;
                }

                if (cullingContext.viewID.GetInstanceID() != _highwayCameraID)
                    return default;

                int totalVisible = 0;
                int activeBatchCount = 0;
                foreach (var batch in _batches.Values)
                {
                    if (batch.activeCount > 0)
                    {
                        totalVisible += batch.activeCount;
                        activeBatchCount++;
                    }
                }

                if (totalVisible == 0)
                    return default;

                _cullFrameCounter++;
                if (DebugLogging && _cullFrameCounter % 300 == 0)
                {
                    Debug.Log(
                        $"[HEGS] CULL: batches={_batches.Count}, active={activeBatchCount}, " +
                        $"visible={totalVisible}, bump={_bumpBytes}/{_bufferBytes}");
                }

                int drawCommandsSize = activeBatchCount * UnsafeUtility.SizeOf<BatchDrawCommand>();
                int instancesSize = totalVisible * sizeof(int);
                int alignment = UnsafeUtility.AlignOf<long>();

                void* drawCommandsPtr = UnsafeUtility.Malloc(drawCommandsSize, alignment, Allocator.TempJob);
                void* instancesPtr = UnsafeUtility.Malloc(instancesSize, alignment, Allocator.TempJob);

                int visibleInstanceOffset = 0;
                int drawCommandIndex = 0;

                foreach (var batch in _batches.Values)
                {
                    int count = batch.activeCount;
                    if (count <= 0)
                        continue;

                    var cmd = new BatchDrawCommand
                    {
                        batchID = batch.batchID,
                        materialID = batch.materialID,
                        meshID = batch.meshID,
                        submeshIndex = (ushort)batch.submeshIndex,
                        visibleCount = (uint)count,
                        visibleOffset = (uint)visibleInstanceOffset,
                        splitVisibilityMask = 0xffff,
                        flags = 0,
                        sortingPosition = 0
                    };

                    UnsafeUtility.WriteArrayElement(drawCommandsPtr, drawCommandIndex, cmd);
                    drawCommandIndex++;

                    for (int i = 0; i < count; i++)
                        UnsafeUtility.WriteArrayElement(instancesPtr, visibleInstanceOffset + i, i);
                    visibleInstanceOffset += count;
                }

                var drawCmdOutput = (BatchCullingOutputDrawCommands*)cullingOutput.drawCommands.GetUnsafePtr();
                drawCmdOutput->drawCommands = (BatchDrawCommand*)drawCommandsPtr;
                drawCmdOutput->drawRanges = (BatchDrawRange*)UnsafeUtility.Malloc(
                    UnsafeUtility.SizeOf<BatchDrawRange>(), UnsafeUtility.AlignOf<long>(), Allocator.TempJob);
                drawCmdOutput->drawRanges[0].drawCommandsType = BatchDrawCommandType.Direct;
                drawCmdOutput->drawRanges[0].drawCommandsBegin = 0;
                drawCmdOutput->drawRanges[0].drawCommandsCount = (uint)drawCommandIndex;
                drawCmdOutput->drawRanges[0].filterSettings = new BatchFilterSettings
                {
                    renderingLayerMask = 0xffffffff,
                    layer = 0,
                    allDepthSorted = false,
                };
                drawCmdOutput->drawCommandCount = drawCommandIndex;
                drawCmdOutput->drawRangeCount = 1;
                drawCmdOutput->visibleInstanceCount = visibleInstanceOffset;
                drawCmdOutput->visibleInstances = (int*)instancesPtr;
                drawCmdOutput->instanceSortingPositions = null;
                drawCmdOutput->instanceSortingPositionFloatCount = 0;
                drawCmdOutput->drawCommandPickingEntityIds = null;

                return default;
            }
            finally
            {
                UnityEngine.Profiling.Profiler.EndSample();
            }
        }

        #endregion
    }
}
