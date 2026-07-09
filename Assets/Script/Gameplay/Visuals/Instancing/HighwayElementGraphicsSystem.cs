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
    /// Manages instanced rendering of highway elements (notes, holds, etc.)
    /// using Unity's BatchRendererGroup API with SoA layout in GPU memory.
    ///
    /// Access pattern is dense per-frame rewrite (highway "particle" model),
    /// with EGS-inspired GPU buffer layout + SparseUploader.
    /// Trackers call UploadInstance directly; this system does not own tracker lists.
    /// </summary>
    internal class HighwayElementGraphicsSystem : IDisposable
    {
        private BatchRendererGroup _brg;

        /// <summary>Gets the underlying BatchRendererGroup for camera assignment.</summary>
        internal BatchRendererGroup BatchRendererGroup => _brg;
        private GraphicsBuffer _gpuBuffer;
        private GraphicsBufferHandle _gpuBufferHandle;
        private HeapAllocator _heapAllocator;
        private SparseUploader _sparseUploader;

        // Batch registry: BatchKey → ElementBatch
        private readonly Dictionary<BatchKey, ElementBatch> _batches = new();

        // Mesh/material ID caches
        private readonly Dictionary<int, BatchMeshID> _meshIDs = new();
        private readonly Dictionary<int, BatchMaterialID> _materialIDs = new();

        // 8MB default — multiplayer + multi-mesh themes need headroom beyond 2MB
        private const int InitialBufferSize = 8 * 1024 * 1024;
        private const int MaxBufferSize = 64 * 1024 * 1024;

        // SoA strides must match shader property sizes (DOTS metadata):
        //   O2W 48 + W2O 48 + BaseColor 16 + Emission 16 + RandomFloat 4 + RandomVector 16 = 148
        // RandomVector uploaded as float4 (Shader Graph / SetVector); RandomFloat is float.
        // Region starts are 16-aligned; overall alloc rounds up per instance for heap sizing.
        private const int BytesPerInstance = 160; // full SoA (transforms + colors) + pad
        private const int StrideO2W = 48;
        private const int StrideW2O = 48;
        private const int StrideFloat4 = 16;
        private const int StrideFloat = 4;
        private const int DefaultBatchCapacity = 256;
        /// <summary>Minimum multiplayer multiplier when sizing new batches.</summary>
        private const int MinPlayerHeadroom = 4;

        // Transform SoA share: Colored/NoSP/Metal batches for same mesh+submesh+source
        // share one O2W/W2O stream (Raw buffer only — ConstantBuffer windows can't span
        // distant heap regions safely).
        private readonly Dictionary<TransformShareKey, SharedTransformRegion> _transformShares = new();

        private int _cullFrameCounter;
        // Zero prefix at buffer start (BRG safety). Sized to also satisfy ConstantBuffer
        // offset alignment so heap block.begin + _zeroMatrixSize is window-aligned.
        private int _zeroMatrixSize = 64;

        // Frame counter for BeginUploadFrame — ensures batches reset activeCount once per frame.
        private int _uploadFrame = -1;
        private bool _uploadsOpen;

        // Highway render camera only — BRG culling fires for every Camera view.
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
        private static readonly int ColorID = Shader.PropertyToID("_Color"); // legacy sustain SG
        private static readonly int IsActiveID = Shader.PropertyToID("_IsActive");
        private static readonly int WhammyAmountID = Shader.PropertyToID("_WhammyAmount");

        private const int BytesPerSustainInstance = 144; // O2W+W2O+color+em+isActive+whammy + pad

        /// <summary>When true, periodic HEGS diagnostics log every 300 cull frames.</summary>
        internal static bool DebugLogging { get; set; }

        private bool _disposed;

        #region ElementBatch

        /// <summary>
        /// Metadata for a single batch of highway elements sharing the same mesh/material.
        /// Class (not struct) so mutations via UploadInstance persist in the registry.
        /// </summary>
        internal class ElementBatch
        {
            public BatchID batchID;
            public BatchMeshID meshID;
            public BatchMaterialID materialID;
            public int submeshIndex;
            public HeapBlock gpuAllocation; // full SoA, or color-only when sharing transforms
            public int capacity;
            public int activeCount;
            public int objectToWorldOffset;
            public int worldToObjectOffset;
            public int baseColorOffset;
            public int emissionOffset;
            public int randomFloatOffset;
            public int randomVectorOffset;
            /// <summary>-1 if batch has no sustain props.</summary>
            public int isActiveOffset = -1;
            /// <summary>-1 if batch has no sustain props.</summary>
            public int whammyOffset = -1;
            public Matrix4x4 meshLocalOffset;
            /// <summary>Theme emission addition baked into uploaded color (rgb).</summary>
            public float emissionAddition;
            /// <summary>Theme emission multiplier applied to uploaded emission color.</summary>
            public float emissionMultiplier;
            /// <summary>Unity instance IDs used for BatchKey (for re-key after grow).</summary>
            public int meshKey;
            public int matKey;
            public int sourceRendererID;
            /// <summary>Shared O2W/W2O region across material categories; null = private transforms.</summary>
            public SharedTransformRegion sharedTransforms;
        }

        /// <summary>
        /// Shared transform streams for all material-category batches of one mesh/submesh/source.
        /// </summary>
        internal class SharedTransformRegion
        {
            public TransformShareKey key;
            public int capacity;
            public int objectToWorldOffset;
            public int worldToObjectOffset;
            public HeapBlock allocation;
            public int refCount;
            /// <summary>Dense fill watermark this frame (reset in BeginUploadFrame).</summary>
            public int transformsFilled;
        }

        #endregion

        #region BatchKey / TransformShareKey

        /// <summary>
        /// Unique key for grouping instances into batches.
        /// Instances with the same mesh, material, submesh, and source renderer are batched together.
        /// </summary>
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

        /// <summary>
        /// Share key for O2W/W2O only — excludes material so Colored/NoSP/Metal share transforms.
        /// </summary>
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        internal struct TransformShareKey : IEquatable<TransformShareKey>
        {
            public int meshID;
            public int submeshIndex;
            public int sourceRendererID;

            public bool Equals(TransformShareKey other) =>
                meshID == other.meshID &&
                submeshIndex == other.submeshIndex &&
                sourceRendererID == other.sourceRendererID;

            public override bool Equals(object obj) => obj is TransformShareKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = 17;
                    hash = hash * 31 + meshID;
                    hash = hash * 31 + submeshIndex;
                    hash = hash * 31 + sourceRendererID;
                    return hash;
                }
            }
        }

        #endregion

        #region Construction / Destruction

        /// <summary>
        /// Initializes the graphics system. Call once after construction.
        /// </summary>
        internal void OnCreate()
        {
            var bufferTarget = UseConstantBuffer
                ? GraphicsBuffer.Target.Constant
                : GraphicsBuffer.Target.Raw;
            var stride = UseConstantBuffer ? 16 : sizeof(int);

            _gpuBuffer = new GraphicsBuffer(bufferTarget, InitialBufferSize / stride, stride);
            _gpuBuffer.name = "HighwayElementGPUBuffer";

            // Zero prefix: BRG safety (unset metadata → addr 0). Also pad so heap starts at
            // ConstantBuffer-aligned absolute offset (bindOffset = block.begin + _zeroMatrixSize).
            int cbufferAlign = UseConstantBuffer
                ? Mathf.Max(16, BatchRendererGroup.GetConstantBufferOffsetAlignment())
                : 16;
            _zeroMatrixSize = Mathf.Max(64, cbufferAlign);

            var zeroInit = new int[_zeroMatrixSize / sizeof(int)];
            _gpuBuffer.SetData(zeroInit, 0, 0, zeroInit.Length);

            _heapAllocator = new HeapAllocator(
                (ulong)(InitialBufferSize - _zeroMatrixSize), (uint)cbufferAlign);

            _sparseUploader = new SparseUploader(_gpuBuffer);

            var createInfo = new BatchRendererGroupCreateInfo
            {
                cullingCallback = OnPerformCullingCallback,
                userContext = IntPtr.Zero
            };
            _brg = new BatchRendererGroup(createInfo);

            _brg.SetEnabledViewTypes(new[] { BatchCullingViewType.Camera });
            _brg.SetGlobalBounds(new Bounds(Vector3.zero, new Vector3(1048576f, 1048576f, 1048576f)));

            _gpuBufferHandle = _gpuBuffer.bufferHandle;
        }

        /// <summary>
        /// Restrict BRG draws to the highway render camera. Must be called after OnCreate
        /// with <c>highwayRenderCamera.GetInstanceID()</c>.
        /// </summary>
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

            // Tear down BRG first (owns draw state). Heap/buffer freed below;
            // no mid-gameplay batch GC — lifetime is whole song/session.
            if (_brg != null)
            {
                _brg.Dispose();
                _brg = null;
            }

            _batches.Clear();
            _transformShares.Clear();
            _meshIDs.Clear();
            _materialIDs.Clear();

            if (_gpuBuffer != null)
            {
                _gpuBuffer.Dispose();
                _gpuBuffer = null;
            }

            if (_heapAllocator.IsCreated)
            {
                _heapAllocator.Dispose();
                _heapAllocator = default;
            }

            if (_sparseUploader != null)
            {
                _sparseUploader.Dispose();
                _sparseUploader = null;
            }
        }

        #endregion

        #region Batch Management

        /// <summary>
        /// Gets or creates a batch for the given mesh, material, and submesh.
        /// </summary>
        /// <param name="playerCountHint">
        /// Number of concurrent players that may share this batch. Capacity is at least
        /// <paramref name="capacityPerPlayer"/> × max(hint, <see cref="MinPlayerHeadroom"/>).
        /// </param>
        internal ElementBatch GetOrCreateBatch(
            Mesh mesh,
            Material material,
            int submeshIndex,
            int sourceRendererID,
            int capacityPerPlayer = -1,
            Matrix4x4? meshLocalOffset = null,
            float emissionAddition = 0f,
            float emissionMultiplier = 1f,
            int playerCountHint = 1)
        {
            if (_disposed) return null;

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

            if (capacityPerPlayer < 0)
                capacityPerPlayer = DefaultBatchCapacity;

            int players = Mathf.Max(MinPlayerHeadroom, Mathf.Max(1, playerCountHint));
            int capacity = capacityPerPlayer * players;

            var meshLocal = meshLocalOffset ?? Matrix4x4.identity;

            // Prefer shared transforms (Raw only). ConstantBuffer keeps full private SoA.
            ElementBatch batch = null;
            if (!UseConstantBuffer)
            {
                batch = TryCreateBatchWithSharedTransforms(
                    key, meshID, materialID, submeshIndex, capacity,
                    meshLocal, emissionAddition, emissionMultiplier,
                    meshKey, matKey, sourceRendererID);
            }

            if (batch == null)
            {
                batch = CreateBatchFullSoA(
                    key, meshID, materialID, submeshIndex, capacity,
                    meshLocal, emissionAddition, emissionMultiplier,
                    meshKey, matKey, sourceRendererID);
            }

            if (batch == null)
                return null;

            _batches[key] = batch;
            return batch;
        }

        private HeapBlock AllocateHeapBlock(int sizeBytes)
        {
            var block = _heapAllocator.Allocate((ulong)sizeBytes, 16);
            if (!block.Empty)
                return block;

            // Grow GPU buffer + heap and retry once (and again up to max).
            while (block.Empty)
            {
                int currentSize = _gpuBuffer != null ? _gpuBuffer.count * 4 : InitialBufferSize;
                int newSize = Mathf.Min(MaxBufferSize, Mathf.Max(currentSize * 2, currentSize + sizeBytes + _zeroMatrixSize));
                if (newSize <= currentSize)
                    break;

                if (!TryGrowGpuBuffer(newSize))
                    break;

                block = _heapAllocator.Allocate((ulong)sizeBytes, 16);
            }

            return block;
        }

        private bool TryGrowGpuBuffer(int newSizeBytes)
        {
            if (_disposed || _gpuBuffer == null || _brg == null)
                return false;

            // Flush pending scatter ops into the old buffer before swapping destinations.
            if (_uploadsOpen)
                _sparseUploader?.Commit();

            var bufferTarget = UseConstantBuffer
                ? GraphicsBuffer.Target.Constant
                : GraphicsBuffer.Target.Raw;
            var stride = UseConstantBuffer ? 16 : sizeof(int);

            int oldCount = _gpuBuffer.count;
            int newCount = newSizeBytes / stride;
            var newBuffer = new GraphicsBuffer(bufferTarget, newCount, stride);
            newBuffer.name = "HighwayElementGPUBuffer";

            // GPU-side copy — avoid managed GetData/SetData hitch on large buffers.
            try
            {
                Graphics.CopyBuffer(_gpuBuffer, newBuffer);
            }
            catch (Exception ex)
            {
                // Fallback if sizes/targets reject CopyBuffer
                int copyInts = Mathf.Min(oldCount, newCount);
                if (copyInts > 0)
                {
                    var tmp = new int[copyInts];
                    _gpuBuffer.GetData(tmp);
                    newBuffer.SetData(tmp);
                }
                if (DebugLogging)
                    Debug.LogWarning($"[HEGS] CopyBuffer failed, used SetData fallback: {ex.Message}");
            }

            ulong newHeapSize = (ulong)(newSizeBytes - _zeroMatrixSize);
            if (!_heapAllocator.Resize(newHeapSize))
            {
                newBuffer.Dispose();
                return false;
            }

            _gpuBuffer.Dispose();
            _gpuBuffer = newBuffer;
            _gpuBufferHandle = _gpuBuffer.bufferHandle;

            foreach (var batch in _batches.Values)
                _brg.SetBatchBuffer(batch.batchID, _gpuBufferHandle);

            // Destination changed — new uploader; further AddUploads this frame stay open.
            _sparseUploader?.Dispose();
            _sparseUploader = new SparseUploader(_gpuBuffer);

            if (DebugLogging)
                Debug.Log($"[HEGS] Grew GPU buffer to {newSizeBytes / 1024}KB");

            return true;
        }

        private ElementBatch CreateBatchFullSoA(
            BatchKey key,
            BatchMeshID meshID,
            BatchMaterialID materialID,
            int submeshIndex,
            int capacity,
            Matrix4x4 meshLocalOffset,
            float emissionAddition,
            float emissionMultiplier,
            int meshKey,
            int matKey,
            int sourceRendererID)
        {
            var block = AllocateHeapBlock(BytesPerInstance * capacity);
            if (block.Empty)
            {
                Debug.LogError(
                    $"[HighwayElementGraphicsSystem] Heap allocation failed for {capacity} instances " +
                    $"({BytesPerInstance * capacity} bytes) after growth attempts.");
                return null;
            }

            ComputeSoAOffsets((int)block.begin, capacity,
                out int objectToWorldOffset, out int worldToObjectOffset,
                out int baseColorOffset, out int emissionOffset,
                out int randomFloatOffset, out int randomVectorOffset);

            if (!TryAddBatch(objectToWorldOffset, worldToObjectOffset, baseColorOffset, emissionOffset,
                    randomFloatOffset, randomVectorOffset, (int)block.begin, (int)block.Length,
                    out BatchID batchID))
            {
                _heapAllocator.Release(block);
                return null;
            }

            return new ElementBatch
            {
                batchID = batchID,
                meshID = meshID,
                materialID = materialID,
                submeshIndex = submeshIndex,
                gpuAllocation = block,
                capacity = capacity,
                activeCount = 0,
                objectToWorldOffset = objectToWorldOffset,
                worldToObjectOffset = worldToObjectOffset,
                baseColorOffset = baseColorOffset,
                emissionOffset = emissionOffset,
                randomFloatOffset = randomFloatOffset,
                randomVectorOffset = randomVectorOffset,
                meshLocalOffset = meshLocalOffset,
                emissionAddition = emissionAddition,
                emissionMultiplier = emissionMultiplier,
                meshKey = meshKey,
                matKey = matKey,
                sourceRendererID = sourceRendererID,
                sharedTransforms = null
            };
        }

        /// <summary>
        /// Create batch with shared O2W/W2O + private color streams (Raw path).
        /// Returns null to fall back to full SoA.
        /// </summary>
        private ElementBatch TryCreateBatchWithSharedTransforms(
            BatchKey key,
            BatchMeshID meshID,
            BatchMaterialID materialID,
            int submeshIndex,
            int capacity,
            Matrix4x4 meshLocalOffset,
            float emissionAddition,
            float emissionMultiplier,
            int meshKey,
            int matKey,
            int sourceRendererID)
        {
            var tkey = new TransformShareKey
            {
                meshID = meshKey,
                submeshIndex = submeshIndex,
                sourceRendererID = sourceRendererID
            };

            if (!_transformShares.TryGetValue(tkey, out var share))
            {
                int tBytes = Align16(StrideO2W * capacity + StrideW2O * capacity);
                var tBlock = AllocateHeapBlock(tBytes);
                if (tBlock.Empty)
                    return null;

                int o2w = (int)tBlock.begin + _zeroMatrixSize;
                int w2o = o2w + StrideO2W * capacity;
                share = new SharedTransformRegion
                {
                    key = tkey,
                    capacity = capacity,
                    objectToWorldOffset = o2w,
                    worldToObjectOffset = w2o,
                    allocation = tBlock,
                    refCount = 0,
                    transformsFilled = 0
                };
                _transformShares[tkey] = share;
            }
            else if (share.capacity < capacity)
            {
                // Existing share too small — fall back to private full SoA for this batch.
                return null;
            }

            int colorBytes = ComputeColorRegionBytes(capacity);
            var colorBlock = AllocateHeapBlock(colorBytes);
            if (colorBlock.Empty)
                return null;

            ComputeColorOnlyOffsets((int)colorBlock.begin, capacity,
                out int baseColorOffset, out int emissionOffset,
                out int randomFloatOffset, out int randomVectorOffset);

            // Window / metadata must cover shared transforms AND private colors.
            // Pass a synthetic heap span from min offset region to max end.
            int absMin = Mathf.Min(share.objectToWorldOffset, baseColorOffset) - _zeroMatrixSize;
            if (absMin < 0) absMin = 0;
            int absMax = Mathf.Max(
                share.worldToObjectOffset + StrideW2O * capacity,
                randomVectorOffset + StrideFloat4 * capacity);
            int spanLen = absMax - (absMin + _zeroMatrixSize);
            if (spanLen < colorBytes)
                spanLen = colorBytes;

            if (!TryAddBatch(
                    share.objectToWorldOffset, share.worldToObjectOffset,
                    baseColorOffset, emissionOffset, randomFloatOffset, randomVectorOffset,
                    absMin, spanLen, out BatchID batchID))
            {
                _heapAllocator.Release(colorBlock);
                return null;
            }

            share.refCount++;
            return new ElementBatch
            {
                batchID = batchID,
                meshID = meshID,
                materialID = materialID,
                submeshIndex = submeshIndex,
                gpuAllocation = colorBlock,
                capacity = Mathf.Min(capacity, share.capacity),
                activeCount = 0,
                objectToWorldOffset = share.objectToWorldOffset,
                worldToObjectOffset = share.worldToObjectOffset,
                baseColorOffset = baseColorOffset,
                emissionOffset = emissionOffset,
                randomFloatOffset = randomFloatOffset,
                randomVectorOffset = randomVectorOffset,
                meshLocalOffset = meshLocalOffset,
                emissionAddition = emissionAddition,
                emissionMultiplier = emissionMultiplier,
                meshKey = meshKey,
                matKey = matKey,
                sourceRendererID = sourceRendererID,
                sharedTransforms = share
            };
        }

        /// <summary>
        /// Build metadata + AddBatch. Absolute offsets for Raw; window-relative for ConstantBuffer.
        /// </summary>
        private bool TryAddBatch(
            int objectToWorldOffset,
            int worldToObjectOffset,
            int baseColorOffset,
            int emissionOffset,
            int randomFloatOffset,
            int randomVectorOffset,
            int heapBlockBegin,
            int heapBlockLength,
            out BatchID batchID)
        {
            batchID = default;

            // Physical start of allocation in the full GraphicsBuffer (includes zero prefix).
            int allocationBegin = heapBlockBegin + _zeroMatrixSize;
            int allocationEnd = randomVectorOffset + StrideFloat4; // last region end (conservative)
            // Prefer true end of heap block when larger (padding).
            int physicalEnd = Mathf.Max(allocationEnd, allocationBegin + heapBlockLength);

            uint bindOffset = 0;
            uint windowSize = 0;

            if (UseConstantBuffer)
            {
                int align = Mathf.Max(16, BatchRendererGroup.GetConstantBufferOffsetAlignment());
                int maxWindow = BatchRendererGroup.GetConstantBufferMaxWindowSize();
                // Window must start at allocation and cover all metadata streams.
                bindOffset = (uint)allocationBegin;
                if (bindOffset % (uint)align != 0)
                {
                    Debug.LogError(
                        $"[HEGS] ConstantBuffer bindOffset {bindOffset} not aligned to {align}");
                    return false;
                }

                int neededWindow = physicalEnd - allocationBegin;
                if (neededWindow > maxWindow)
                {
                    Debug.LogError(
                        $"[HEGS] Batch SoA ({neededWindow} B) exceeds ConstantBuffer max window ({maxWindow} B). " +
                        "Reduce capacity or instance payload.");
                    return false;
                }

                // Window must not extend past the GraphicsBuffer end.
                int bufferBytes = _gpuBuffer.count * _gpuBuffer.stride;
                int maxFromOffset = bufferBytes - allocationBegin;
                if (maxFromOffset < neededWindow)
                {
                    Debug.LogError(
                        $"[HEGS] ConstantBuffer window past buffer end " +
                        $"(need {neededWindow}, avail {maxFromOffset} from offset {allocationBegin})");
                    return false;
                }

                windowSize = (uint)Mathf.Min(maxWindow, maxFromOffset);
            }

            // Metadata: Raw indexes from buffer start; ConstantBuffer from bindOffset (EGS pattern).
            int metaBase = UseConstantBuffer ? (int)bindOffset : 0;

            // Dual-bind _BaseColor and _Color so both note SGs and Beatline.mat resolve instance color.
            var metadata = new NativeArray<MetadataValue>(8, Allocator.Temp);
            metadata[0] = Meta(ObjectToWorldID, objectToWorldOffset - metaBase);
            metadata[1] = Meta(WorldToObjectID, worldToObjectOffset - metaBase);
            metadata[2] = Meta(BaseColorID, baseColorOffset - metaBase);
            metadata[3] = Meta(ColorID, baseColorOffset - metaBase);
            metadata[4] = Meta(EmissionColorID, emissionOffset - metaBase);
            metadata[5] = Meta(EmissionID, emissionOffset - metaBase);
            metadata[6] = Meta(RandomFloatID, randomFloatOffset - metaBase);
            metadata[7] = Meta(RandomVectorID, randomVectorOffset - metaBase);

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

        // No mid-frame EnsureCapacity: growing after any write this frame destroys prior
        // shared-batch appends from other trackers. Capacity is fixed at create.
        // No batch GC during gameplay — free everything only in Dispose.

        #endregion

        #region Data Upload

        /// <summary>
        /// Resets <see cref="ElementBatch.activeCount"/> for every batch to zero.
        /// Must be called once per frame BEFORE any tracker uploads instance data.
        /// </summary>
        internal void BeginUploadFrame()
        {
            UnityEngine.Profiling.Profiler.BeginSample("HEGS.BeginUploadFrame");
            try
            {
                if (_disposed) return;
                int frame = Time.frameCount;
                if (_uploadFrame == frame) return;

                // Safety: if EndUploadFrame was skipped last frame, flush leftover first.
                if (_uploadsOpen || (_sparseUploader != null && _sparseUploader.HasPendingComputeUploads))
                    CommitUploadsOnly();

                _uploadFrame = frame;
                _uploadsOpen = true;
                foreach (var batch in _batches.Values)
                    batch.activeCount = 0;
                foreach (var share in _transformShares.Values)
                    share.transformsFilled = 0;
            }
            finally
            {
                UnityEngine.Profiling.Profiler.EndSample();
            }
        }

        /// <summary>
        /// Commits pending SparseUploader ops once per frame after all trackers uploaded.
        /// Updates unused-frame counters for safe GC.
        /// Must be called from a reliable main-thread site after all NoteTracker.UploadToGPU
        /// (GameManager after player loop). HCR LateUpdate is a backup only.
        /// </summary>
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
            if (_sparseUploader == null)
            {
                _uploadsOpen = false;
                return;
            }

            // Commit if we opened a frame OR the uploader still holds a locked buffer
            // (covers auto mid-frame commits that left a new lock open).
            if (_uploadsOpen || _sparseUploader.HasPendingComputeUploads)
                _sparseUploader.Commit();

            _uploadsOpen = false;
        }

        #endregion

        #region Culling Callback

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

                // BRG culling runs for every Camera — only draw into the highway RT camera.
                if (_highwayCameraID == 0)
                {
                    // Fail-closed: never draw if host forgot SetHighwayCamera.
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
                    int totalCapacity = 0;
                    foreach (var b in _batches.Values) totalCapacity += b.capacity;
                    Debug.Log(
                        $"[HEGS] CULL DIAG: batches={_batches.Count}, activeBatches={activeBatchCount}, " +
                        $"visible={totalVisible}, totalCapacity={totalCapacity}, " +
                        $"gpuBuffer={_gpuBuffer.count * 4 / 1024}KB");
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
                        // Camera/shadow split mask — NOT instance count. Visible in all splits.
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
                // layer 0 = Default; highway camera typically sees Default. renderingLayerMask all bits.
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

        #region Instance Upload Helpers

        private static int Align16(int value) => (value + 15) & ~15;

        /// <summary>
        /// Compute SoA region offsets for a batch allocation starting at heap block begin
        /// (relative to heap; _zeroMatrixSize is added so GPU offsets skip the safety zone).
        /// </summary>
        private void ComputeSoAOffsets(
            int heapBlockBegin,
            int capacity,
            out int objectToWorldOffset,
            out int worldToObjectOffset,
            out int baseColorOffset,
            out int emissionOffset,
            out int randomFloatOffset,
            out int randomVectorOffset)
        {
            objectToWorldOffset = heapBlockBegin + _zeroMatrixSize;
            worldToObjectOffset = objectToWorldOffset + StrideO2W * capacity;
            baseColorOffset = worldToObjectOffset + StrideW2O * capacity;
            emissionOffset = baseColorOffset + StrideFloat4 * capacity;
            // Align region starts; float RandomFloat uses 4-byte instance stride.
            randomFloatOffset = Align16(emissionOffset + StrideFloat4 * capacity);
            randomVectorOffset = Align16(randomFloatOffset + StrideFloat * capacity);
        }

        private void ComputeColorOnlyOffsets(
            int heapBlockBegin,
            int capacity,
            out int baseColorOffset,
            out int emissionOffset,
            out int randomFloatOffset,
            out int randomVectorOffset)
        {
            baseColorOffset = heapBlockBegin + _zeroMatrixSize;
            emissionOffset = baseColorOffset + StrideFloat4 * capacity;
            randomFloatOffset = Align16(emissionOffset + StrideFloat4 * capacity);
            randomVectorOffset = Align16(randomFloatOffset + StrideFloat * capacity);
        }

        private static int ComputeColorRegionBytes(int capacity)
        {
            // Mirror ComputeColorOnlyOffsets span from region start (without zero prefix).
            int baseColor = 0;
            int emission = baseColor + StrideFloat4 * capacity;
            int randomFloat = Align16(emission + StrideFloat4 * capacity);
            int randomVector = Align16(randomFloat + StrideFloat * capacity);
            int end = randomVector + StrideFloat4 * capacity;
            return Align16(end);
        }

        /// <summary>
        /// Converts an authoring/UI gamma-space color to linear for BRG GraphicsBuffer upload.
        /// <para>
        /// In Linear color space, <see cref="Material.color"/> and <see cref="Material.SetColor"/>
        /// automatically convert non-HDR Color shader properties from sRGB to linear. Raw
        /// <see cref="GraphicsBuffer"/> uploads used by BatchRendererGroup do not, so callers must
        /// convert explicitly. This matches Entities Graphics
        /// <c>URPMaterialPropertyBaseColorAuthoring</c>, which stores <c>color.linear</c>.
        /// </para>
        /// <para>
        /// Do not use this for HDR properties such as <c>_EmissionColor</c> / <c>_Emission</c>:
        /// <see cref="Material.SetColor"/> leaves those values unchanged. Emission add/mul baking
        /// is performed in gamma space first (same order as <c>NoteGroup</c>), then the HDR result
        /// is uploaded as-is.
        /// </para>
        /// <para>
        /// Contract: note and sustain <c>_BaseColor</c> properties must use Shader Graph ColorMode 0
        /// (Default/sRGB), never HDR, so the GameObject and BRG paths share one conversion rule.
        /// </para>
        /// </summary>
        private static Vector4 ToLinearGpuColor(Vector4 gamma)
        {
            Color linear = ((Color)gamma).linear;
            return new Vector4(linear.r, linear.g, linear.b, linear.a);
        }

        /// <summary>
        /// Uploads instance data for a single element in a batch (SoA).
        /// When transforms are shared across material categories, O2W/W2O are written once
        /// per instance index per frame (first writer wins; dense append keeps indices aligned).
        /// </summary>
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

            // Capacity fixed at create — never grow mid-frame (shared-batch append safety).
            if (instanceIndex >= batch.capacity)
            {
                if (DebugLogging)
                    Debug.LogWarning(
                        $"[HEGS] BATCH OVERFLOW: instanceIndex={instanceIndex} >= capacity={batch.capacity}");
                return;
            }

            if (batch.objectToWorldOffset < _zeroMatrixSize)
                return;

            // Shared transform SoA: write O2W/W2O only once per slot per frame.
            bool writeTransforms = true;
            if (batch.sharedTransforms != null)
            {
                var share = batch.sharedTransforms;
                if (instanceIndex < share.transformsFilled)
                    writeTransforms = false;
                else
                    share.transformsFilled = instanceIndex + 1;
            }

            if (writeTransforms)
            {
                int owtOffset = batch.objectToWorldOffset + instanceIndex * StrideO2W;
                int wtoOffset = batch.worldToObjectOffset + instanceIndex * StrideW2O;
                PackedMatrix packedOW = PackedMatrix.FromMatrix4x4(objectToWorld);
                _sparseUploader.AddUpload(packedOW, owtOffset);
                PackedMatrix packedWO = PackedMatrix.FromAffineInverse(objectToWorld);
                _sparseUploader.AddUpload(packedWO, wtoOffset);
            }

            int colorOffset = batch.baseColorOffset + instanceIndex * StrideFloat4;
            int emissionOffset = batch.emissionOffset + instanceIndex * StrideFloat4;
            int randomFloatOffset = batch.randomFloatOffset + instanceIndex * StrideFloat;
            int randomVectorOffset = batch.randomVectorOffset + instanceIndex * StrideFloat4;

            // Non-HDR _BaseColor must be linear in the GPU buffer (Material.SetColor does this
            // automatically; BRG buffer uploads do not). HDR emission is left in gamma bake space
            // to match Material.SetColor behavior for HDR color properties.
            _sparseUploader.AddUpload(ToLinearGpuColor(baseColor), colorOffset);
            _sparseUploader.AddUpload(emissionColor, emissionOffset);
            _sparseUploader.AddUpload(randomFloat, randomFloatOffset);

            var rv = new Vector4(randomVector.x, randomVector.y, 0f, 0f);
            _sparseUploader.AddUpload(rv, randomVectorOffset);

            if (instanceIndex >= batch.activeCount)
                batch.activeCount = instanceIndex + 1;
        }

        /// <summary>
        /// Batch for unit-mesh sustains: O2W/W2O + color/emission + _IsActive + _WhammyAmount.
        /// Keyed by material only (shared unit mesh).
        /// </summary>
        internal ElementBatch GetOrCreateSustainBatch(
            Material material,
            int capacityPerPlayer = -1,
            int playerCountHint = 1)
        {
            if (_disposed || material == null) return null;

            var mesh = SustainUnitMesh.Mesh;
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
                submeshIndex = 0,
                sourceRendererID = 0 // unit mesh — no source renderer
            };

            if (_batches.TryGetValue(key, out var existing))
                return existing;

            if (capacityPerPlayer < 0)
                capacityPerPlayer = DefaultBatchCapacity;

            int players = Mathf.Max(MinPlayerHeadroom, Mathf.Max(1, playerCountHint));
            int capacity = capacityPerPlayer * players;

            // ConstantBuffer window is often 64KB — large ObjectCap (2000×players) will not fit.
            // Cap sustain SoA so AddBatch succeeds (notes use TryAddBatch which logs; sustains used to fail silent).
            if (UseConstantBuffer)
            {
                int maxWindow = BatchRendererGroup.GetConstantBufferMaxWindowSize();
                // ~136 B/instance SoA (O2W+W2O+color+em+float+float) + align slack
                int maxCap = Mathf.Max(64, (maxWindow - 256) / 140);
                if (capacity > maxCap)
                {
                    Debug.LogWarning(
                        $"[HEGS] Sustain batch capacity {capacity} capped to {maxCap} for ConstantBuffer window {maxWindow}B");
                    capacity = maxCap;
                }
            }

            var block = AllocateHeapBlock(BytesPerSustainInstance * capacity);
            if (block.Empty)
            {
                Debug.LogError("[HEGS] Sustain heap allocation failed");
                return null;
            }

            int begin = (int)block.begin + _zeroMatrixSize;
            int o2w = begin;
            int w2o = o2w + StrideO2W * capacity;
            int baseColor = w2o + StrideW2O * capacity;
            int emission = baseColor + StrideFloat4 * capacity;
            int isActive = Align16(emission + StrideFloat4 * capacity);
            int whammy = Align16(isActive + StrideFloat * capacity);

            // Metadata: color dual-bound for _BaseColor and legacy _Color
            var metadata = new NativeArray<MetadataValue>(7, Allocator.Temp);
            int metaBase = 0;
            uint bindOffset = 0;
            uint windowSize = 0;

            if (UseConstantBuffer)
            {
                int align = Mathf.Max(16, BatchRendererGroup.GetConstantBufferOffsetAlignment());
                int maxWindow = BatchRendererGroup.GetConstantBufferMaxWindowSize();
                bindOffset = (uint)begin;
                if (bindOffset % (uint)align != 0)
                {
                    Debug.LogError($"[HEGS] Sustain CB bindOffset {bindOffset} not aligned to {align}");
                    metadata.Dispose();
                    _heapAllocator.Release(block);
                    return null;
                }

                int needed = (whammy + StrideFloat * capacity) - begin;
                int bufferBytes = _gpuBuffer.count * _gpuBuffer.stride;
                int maxFrom = bufferBytes - begin;
                if (needed > maxWindow || needed > maxFrom)
                {
                    Debug.LogError(
                        $"[HEGS] Sustain batch SoA ({needed}B) exceeds CB window ({maxWindow}B) or buffer tail ({maxFrom}B)");
                    metadata.Dispose();
                    _heapAllocator.Release(block);
                    return null;
                }

                windowSize = (uint)Mathf.Min(maxWindow, maxFrom);
                metaBase = begin;
            }

            metadata[0] = Meta(ObjectToWorldID, o2w - metaBase);
            metadata[1] = Meta(WorldToObjectID, w2o - metaBase);
            metadata[2] = Meta(BaseColorID, baseColor - metaBase);
            metadata[3] = Meta(ColorID, baseColor - metaBase);
            metadata[4] = Meta(EmissionColorID, emission - metaBase);
            metadata[5] = Meta(IsActiveID, isActive - metaBase);
            metadata[6] = Meta(WhammyAmountID, whammy - metaBase);

            BatchID batchID = UseConstantBuffer
                ? _brg.AddBatch(metadata, _gpuBufferHandle, bindOffset, windowSize)
                : _brg.AddBatch(metadata, _gpuBufferHandle);
            metadata.Dispose();

            var batch = new ElementBatch
            {
                batchID = batchID,
                meshID = meshID,
                materialID = materialID,
                submeshIndex = 0,
                gpuAllocation = block,
                capacity = capacity,
                activeCount = 0,
                objectToWorldOffset = o2w,
                worldToObjectOffset = w2o,
                baseColorOffset = baseColor,
                emissionOffset = emission,
                randomFloatOffset = 0,
                randomVectorOffset = 0,
                isActiveOffset = isActive,
                whammyOffset = whammy,
                meshLocalOffset = Matrix4x4.identity,
                emissionAddition = 0f,
                emissionMultiplier = 1f,
                meshKey = meshKey,
                matKey = matKey,
                sourceRendererID = 0,
                sharedTransforms = null
            };

            _batches[key] = batch;
            return batch;
        }

        /// <summary>
        /// Upload one sustain instance (unit mesh × scale/translate + color + active/whammy).
        /// </summary>
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

            if (instanceIndex >= batch.capacity)
            {
                if (DebugLogging)
                    Debug.LogWarning(
                        $"[HEGS] SUSTAIN OVERFLOW: {instanceIndex} >= {batch.capacity}");
                return;
            }

            if (batch.isActiveOffset < 0 || batch.whammyOffset < 0)
                return;

            int owtOffset = batch.objectToWorldOffset + instanceIndex * StrideO2W;
            int wtoOffset = batch.worldToObjectOffset + instanceIndex * StrideW2O;
            int colorOffset = batch.baseColorOffset + instanceIndex * StrideFloat4;
            int emissionOffset = batch.emissionOffset + instanceIndex * StrideFloat4;
            int isActiveOffset = batch.isActiveOffset + instanceIndex * StrideFloat;
            int whammyOffset = batch.whammyOffset + instanceIndex * StrideFloat;

            _sparseUploader.AddUpload(PackedMatrix.FromMatrix4x4(objectToWorld), owtOffset);
            _sparseUploader.AddUpload(PackedMatrix.FromAffineInverse(objectToWorld), wtoOffset);
            // Same color-space rules as UploadInstance: linearize non-HDR _BaseColor; leave HDR
            // _EmissionColor as the gamma-space bake so behavior matches Material.SetColor.
            _sparseUploader.AddUpload(ToLinearGpuColor(baseColor), colorOffset);
            _sparseUploader.AddUpload(emissionColor, emissionOffset);
            _sparseUploader.AddUpload(isActive, isActiveOffset);
            _sparseUploader.AddUpload(whammyAmount, whammyOffset);

            if (instanceIndex >= batch.activeCount)
                batch.activeCount = instanceIndex + 1;
        }

        #endregion
    }
}
