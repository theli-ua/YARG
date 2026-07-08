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

        private int _gcFrameCounter;

        // 8MB default — multiplayer + multi-mesh themes need headroom beyond 2MB
        private const int InitialBufferSize = 8 * 1024 * 1024;
        private const int MaxBufferSize = 64 * 1024 * 1024;

        // SoA strides must match shader property sizes (DOTS metadata):
        //   O2W 48 + W2O 48 + BaseColor 16 + Emission 16 + RandomFloat 4 + RandomVector 16 = 148
        // RandomVector uploaded as float4 (Shader Graph / SetVector); RandomFloat is float.
        // Region starts are 16-aligned; overall alloc rounds up per instance for heap sizing.
        private const int BytesPerInstance = 160; // 148 payload + pad for alignment headroom
        private const int StrideO2W = 48;
        private const int StrideW2O = 48;
        private const int StrideFloat4 = 16;
        private const int StrideFloat = 4;
        private const int DefaultBatchCapacity = 256;
        /// <summary>Minimum multiplayer multiplier when sizing new batches.</summary>
        private const int MinPlayerHeadroom = 4;

        private int _cullFrameCounter;
        private const int ZeroMatrixSize = 64; // float4x4 at offset 0

        // Frame counter for BeginUploadFrame — ensures batches reset activeCount once per frame.
        private int _uploadFrame = -1;
        private bool _uploadsOpen;

        private static readonly int ObjectToWorldID = Shader.PropertyToID("unity_ObjectToWorld");
        private static readonly int WorldToObjectID = Shader.PropertyToID("unity_WorldToObject");
        private static readonly int BaseColorID = Shader.PropertyToID("_BaseColor");
        private static readonly int EmissionColorID = Shader.PropertyToID("_EmissionColor");
        private static readonly int EmissionID = Shader.PropertyToID("_Emission");
        private static readonly int RandomFloatID = Shader.PropertyToID("_RandomFloat");
        private static readonly int RandomVectorID = Shader.PropertyToID("_RandomVector");

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
            public HeapBlock gpuAllocation;
            public int capacity;
            public int activeCount;
            public int objectToWorldOffset;
            public int worldToObjectOffset;
            public int baseColorOffset;
            public int emissionOffset;
            public int randomFloatOffset;
            public int randomVectorOffset;
            public Matrix4x4 meshLocalOffset;
            /// <summary>Theme emission addition baked into uploaded color (rgb).</summary>
            public float emissionAddition;
            /// <summary>Theme emission multiplier applied to uploaded emission color.</summary>
            public float emissionMultiplier;
            /// <summary>Frames since this batch had activeCount &gt; 0 at EndUploadFrame.</summary>
            public int framesUnused;
            /// <summary>Unity instance IDs used for BatchKey (for re-key after grow).</summary>
            public int meshKey;
            public int matKey;
            public int sourceRendererID;
        }

        #endregion

        #region BatchKey

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

        #endregion

        #region Construction / Destruction

        /// <summary>
        /// Initializes the graphics system. Call once after construction.
        /// </summary>
        internal void OnCreate()
        {
            bool useConstantBuffer = BatchRendererGroup.BufferTarget == BatchBufferTarget.ConstantBuffer;
            var bufferTarget = useConstantBuffer
                ? GraphicsBuffer.Target.Constant
                : GraphicsBuffer.Target.Raw;
            var stride = useConstantBuffer ? 16 : sizeof(int);

            _gpuBuffer = new GraphicsBuffer(bufferTarget, InitialBufferSize / stride, stride);
            _gpuBuffer.name = "HighwayElementGPUBuffer";

            // Write 64 bytes of zeros at offset 0 (BRG convention: unset metadata reads from addr 0)
            var zeroInit = new int[ZeroMatrixSize / sizeof(int)];
            _gpuBuffer.SetData(zeroInit, 0, 0, zeroInit.Length);

            // Heap starts after 64-byte zero prefix (logical offsets add ZeroMatrixSize)
            _heapAllocator = new HeapAllocator((ulong)(InitialBufferSize - ZeroMatrixSize), 16);

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

            _batches.Clear();
            _meshIDs.Clear();
            _materialIDs.Clear();
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

            var block = AllocateHeapBlock(BytesPerInstance * capacity);
            if (block.Empty)
            {
                Debug.LogError(
                    $"[HighwayElementGraphicsSystem] Heap allocation failed for {capacity} instances " +
                    $"({BytesPerInstance * capacity} bytes) after growth attempts.");
                return null;
            }

            var batch = CreateBatchFromBlock(
                key, meshID, materialID, submeshIndex, block, capacity,
                meshLocalOffset ?? Matrix4x4.identity,
                emissionAddition, emissionMultiplier,
                meshKey, matKey, sourceRendererID);

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
                int newSize = Mathf.Min(MaxBufferSize, Mathf.Max(currentSize * 2, currentSize + sizeBytes + ZeroMatrixSize));
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

            bool useConstantBuffer = BatchRendererGroup.BufferTarget == BatchBufferTarget.ConstantBuffer;
            var bufferTarget = useConstantBuffer
                ? GraphicsBuffer.Target.Constant
                : GraphicsBuffer.Target.Raw;
            var stride = useConstantBuffer ? 16 : sizeof(int);

            int oldCount = _gpuBuffer.count;
            int newCount = newSizeBytes / stride;
            var newBuffer = new GraphicsBuffer(bufferTarget, newCount, stride);
            newBuffer.name = "HighwayElementGPUBuffer";

            // Copy existing contents (zeros + any committed instance data).
            int copyInts = Mathf.Min(oldCount, newCount);
            if (copyInts > 0)
            {
                var tmp = new int[copyInts];
                _gpuBuffer.GetData(tmp);
                newBuffer.SetData(tmp);
            }

            ulong newHeapSize = (ulong)(newSizeBytes - ZeroMatrixSize);
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

        private ElementBatch CreateBatchFromBlock(
            BatchKey key,
            BatchMeshID meshID,
            BatchMaterialID materialID,
            int submeshIndex,
            HeapBlock block,
            int capacity,
            Matrix4x4 meshLocalOffset,
            float emissionAddition,
            float emissionMultiplier,
            int meshKey,
            int matKey,
            int sourceRendererID)
        {
            ComputeSoAOffsets((int)block.begin, capacity,
                out int objectToWorldOffset, out int worldToObjectOffset,
                out int baseColorOffset, out int emissionOffset,
                out int randomFloatOffset, out int randomVectorOffset);

            // _Emission and _EmissionColor share the same SoA region for SG + URP mat parity.
            var metadata = new NativeArray<MetadataValue>(7, Allocator.Temp);
            metadata[0] = new MetadataValue { NameID = ObjectToWorldID, Value = 0x80000000u | (uint)objectToWorldOffset };
            metadata[1] = new MetadataValue { NameID = WorldToObjectID, Value = 0x80000000u | (uint)worldToObjectOffset };
            metadata[2] = new MetadataValue { NameID = BaseColorID, Value = 0x80000000u | (uint)baseColorOffset };
            metadata[3] = new MetadataValue { NameID = EmissionColorID, Value = 0x80000000u | (uint)emissionOffset };
            metadata[4] = new MetadataValue { NameID = EmissionID, Value = 0x80000000u | (uint)emissionOffset };
            metadata[5] = new MetadataValue { NameID = RandomFloatID, Value = 0x80000000u | (uint)randomFloatOffset };
            metadata[6] = new MetadataValue { NameID = RandomVectorID, Value = 0x80000000u | (uint)randomVectorOffset };

            BatchID batchID = _brg.AddBatch(metadata, _gpuBufferHandle);
            metadata.Dispose();

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
                framesUnused = 0,
                meshKey = meshKey,
                matKey = matKey,
                sourceRendererID = sourceRendererID
            };
        }

        /// <summary>
        /// Grow an existing batch's capacity. Safe during upload because instance data is
        /// fully rewritten every frame — old GPU contents are not preserved.
        /// </summary>
        internal bool EnsureCapacity(ElementBatch batch, int needed)
        {
            if (_disposed || batch == null)
                return false;
            if (needed <= batch.capacity)
                return true;

            int newCapacity = Mathf.Max(needed, batch.capacity * 2);
            var newBlock = AllocateHeapBlock(BytesPerInstance * newCapacity);
            if (newBlock.Empty)
            {
                Debug.LogError($"[HEGS] EnsureCapacity failed: need {needed}, had {batch.capacity}");
                return false;
            }

            // Tear down old BRG batch + heap region
            _heapAllocator.Release(batch.gpuAllocation);
            _brg.RemoveBatch(batch.batchID);

            ComputeSoAOffsets((int)newBlock.begin, newCapacity,
                out int objectToWorldOffset, out int worldToObjectOffset,
                out int baseColorOffset, out int emissionOffset,
                out int randomFloatOffset, out int randomVectorOffset);

            var metadata = new NativeArray<MetadataValue>(7, Allocator.Temp);
            metadata[0] = new MetadataValue { NameID = ObjectToWorldID, Value = 0x80000000u | (uint)objectToWorldOffset };
            metadata[1] = new MetadataValue { NameID = WorldToObjectID, Value = 0x80000000u | (uint)worldToObjectOffset };
            metadata[2] = new MetadataValue { NameID = BaseColorID, Value = 0x80000000u | (uint)baseColorOffset };
            metadata[3] = new MetadataValue { NameID = EmissionColorID, Value = 0x80000000u | (uint)emissionOffset };
            metadata[4] = new MetadataValue { NameID = EmissionID, Value = 0x80000000u | (uint)emissionOffset };
            metadata[5] = new MetadataValue { NameID = RandomFloatID, Value = 0x80000000u | (uint)randomFloatOffset };
            metadata[6] = new MetadataValue { NameID = RandomVectorID, Value = 0x80000000u | (uint)randomVectorOffset };

            batch.batchID = _brg.AddBatch(metadata, _gpuBufferHandle);
            metadata.Dispose();

            batch.gpuAllocation = newBlock;
            batch.capacity = newCapacity;
            batch.objectToWorldOffset = objectToWorldOffset;
            batch.worldToObjectOffset = worldToObjectOffset;
            batch.baseColorOffset = baseColorOffset;
            batch.emissionOffset = emissionOffset;
            batch.randomFloatOffset = randomFloatOffset;
            batch.randomVectorOffset = randomVectorOffset;
            // Preserve activeCount so concurrent appends keep writing at correct slots.

            if (DebugLogging)
                Debug.Log($"[HEGS] Grew batch capacity to {newCapacity}");

            return true;
        }

        internal void RemoveBatch(BatchKey key)
        {
            if (_disposed) return;
            if (!_batches.TryGetValue(key, out var batch))
                return;

            _heapAllocator.Release(batch.gpuAllocation);
            _brg.RemoveBatch(batch.batchID);
            _batches.Remove(key);
        }

        /// <summary>
        /// Removes batches unused for many frames. Must NOT use live activeCount alone —
        /// BeginUploadFrame zeros all counts every frame.
        /// </summary>
        internal void GarbageCollectEmptyBatches(int unusedFrameThreshold = 600)
        {
            if (_disposed) return;

            var keysToRemove = new List<BatchKey>();
            foreach (var kvp in _batches)
            {
                if (kvp.Value.framesUnused >= unusedFrameThreshold)
                    keysToRemove.Add(kvp.Key);
            }

            foreach (var key in keysToRemove)
                RemoveBatch(key);
        }

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

                foreach (var batch in _batches.Values)
                {
                    if (batch.activeCount > 0)
                        batch.framesUnused = 0;
                    else
                        batch.framesUnused++;
                }

                // Periodic GC of long-unused batches (not every frame; uses framesUnused).
                _gcFrameCounter++;
                if (_gcFrameCounter >= 300)
                {
                    _gcFrameCounter = 0;
                    GarbageCollectEmptyBatches(600);
                }
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
        /// (relative to heap; ZeroMatrixSize is added so GPU offsets skip the safety zone).
        /// </summary>
        private static void ComputeSoAOffsets(
            int heapBlockBegin,
            int capacity,
            out int objectToWorldOffset,
            out int worldToObjectOffset,
            out int baseColorOffset,
            out int emissionOffset,
            out int randomFloatOffset,
            out int randomVectorOffset)
        {
            objectToWorldOffset = heapBlockBegin + ZeroMatrixSize;
            worldToObjectOffset = objectToWorldOffset + StrideO2W * capacity;
            baseColorOffset = worldToObjectOffset + StrideW2O * capacity;
            emissionOffset = baseColorOffset + StrideFloat4 * capacity;
            // Align region starts; float RandomFloat uses 4-byte instance stride.
            randomFloatOffset = Align16(emissionOffset + StrideFloat4 * capacity);
            randomVectorOffset = Align16(randomFloatOffset + StrideFloat * capacity);
        }

        /// <summary>
        /// Uploads instance data for a single element in a batch (SoA).
        /// Grows batch capacity if needed. Uses affine inverse for worldToObject.
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

            if (instanceIndex >= batch.capacity)
            {
                if (!EnsureCapacity(batch, instanceIndex + 1))
                {
                    if (DebugLogging)
                        Debug.LogWarning(
                            $"[HEGS] BATCH OVERFLOW: instanceIndex={instanceIndex} >= capacity={batch.capacity}");
                    return;
                }
            }

            if (batch.objectToWorldOffset < ZeroMatrixSize)
                return;

            int owtOffset = batch.objectToWorldOffset + instanceIndex * StrideO2W;
            int wtoOffset = batch.worldToObjectOffset + instanceIndex * StrideW2O;
            int colorOffset = batch.baseColorOffset + instanceIndex * StrideFloat4;
            int emissionOffset = batch.emissionOffset + instanceIndex * StrideFloat4;
            // Stride must match shader property size (float / float4).
            int randomFloatOffset = batch.randomFloatOffset + instanceIndex * StrideFloat;
            int randomVectorOffset = batch.randomVectorOffset + instanceIndex * StrideFloat4;

            PackedMatrix packedOW = PackedMatrix.FromMatrix4x4(objectToWorld);
            _sparseUploader.AddUpload(packedOW, owtOffset);

            PackedMatrix packedWO = PackedMatrix.FromAffineInverse(objectToWorld);
            _sparseUploader.AddUpload(packedWO, wtoOffset);

            _sparseUploader.AddUpload(baseColor, colorOffset);
            _sparseUploader.AddUpload(emissionColor, emissionOffset);
            _sparseUploader.AddUpload(randomFloat, randomFloatOffset);

            var rv = new Vector4(randomVector.x, randomVector.y, 0f, 0f);
            _sparseUploader.AddUpload(rv, randomVectorOffset);

            if (instanceIndex >= batch.activeCount)
                batch.activeCount = instanceIndex + 1;
        }

        #endregion
    }
}
