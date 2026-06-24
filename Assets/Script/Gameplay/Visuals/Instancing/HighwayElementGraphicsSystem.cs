using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Rendering;
using YARG.Themes;

namespace YARG.Gameplay.Visuals.Instancing
{
    /// <summary>
    /// Interface for note trackers that this system renders.
    /// Implemented by NoteTracker in section 3.
    /// </summary>
    internal interface INoteTracker
    {
        void UpdatePositions();
        void RemoveExpired();
        void UpdateBatchAssignments();
        void UploadToGPU(Matrix4x4 trackLocalToWorld);
        void Reset();
    }

    /// <summary>
    /// Manages instanced rendering of highway elements (notes, holds, etc.)
    /// using Unity's BatchRendererGroup API with SoA layout in GPU memory.
    /// </summary>
    internal class HighwayElementGraphicsSystem : IDisposable
    {
        private BatchRendererGroup _brg;
        private GraphicsBuffer _gpuBuffer;
        private GraphicsBufferHandle _gpuBufferHandle;
        private HeapAllocator _heapAllocator;
        private SparseUploader _sparseUploader;

        // Batch registry: BatchKey → ElementBatch
        private readonly Dictionary<BatchKey, ElementBatch> _batches = new();

        // Tracker registry
        private readonly List<INoteTracker> _trackers = new();

        // Mesh/material ID caches
        private readonly Dictionary<int, BatchMeshID> _meshIDs = new();
        private readonly Dictionary<int, BatchMaterialID> _materialIDs = new();

        private const int InitialBufferSize = 2 * 1024 * 1024; // 2MB initial
        private const int BytesPerInstance = 112; // 48 + 48 + 16
        private const int DefaultBatchCapacity = 256;
        private const int ZeroMatrixSize = 64; // float4x4 at offset 0

        private bool _disposed;

        #region ElementBatch (Task 2.2)

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
            public Matrix4x4 meshLocalOffset;
        }

        #endregion

        #region BatchKey (Task 2.3)

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

        #region Construction / Destruction (Tasks 2.4, 2.10)

        /// <summary>
        /// Initializes the graphics system. Call once after construction.
        /// </summary>
        internal void OnCreate()
        {
            // Allocate GPU buffer
            _gpuBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Raw, InitialBufferSize / sizeof(int), sizeof(int));
            _gpuBuffer.name = "HighwayElementGPUBuffer";

            // Write zero Matrix4x4 at offset 0 (placeholder for unity_ObjectToWorld)
            _gpuBuffer.SetData(new[] { Matrix4x4.zero }, 0, 0, 1);

            // Initialize heap allocator
            _heapAllocator = new HeapAllocator((ulong)InitialBufferSize, 16);

            // Initialize sparse uploader
            _sparseUploader = new SparseUploader(_gpuBuffer, bufferChunkSize: 256 * 1024);

            // Create BatchRendererGroup using BatchRendererGroupCreateInfo
            var createInfo = new BatchRendererGroupCreateInfo
            {
                cullingCallback = OnPerformCullingCallback,
                userContext = IntPtr.Zero
            };
            _brg = new BatchRendererGroup(createInfo);

            // Enable camera view type
            _brg.SetEnabledViewTypes(new[] { BatchCullingViewType.Camera });

            // Get buffer handle for BRG
            _gpuBufferHandle = _gpuBuffer.bufferHandle;
        }

        /// <summary>
        /// Disposes all resources.
        /// </summary>
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
            _trackers.Clear();
            _meshIDs.Clear();
            _materialIDs.Clear();
        }

        #endregion

        #region Batch Management (Tasks 2.5, 2.6, 2.7)

        /// <summary>
        /// Gets or creates a batch for the given mesh, material, and submesh.
        /// Registers mesh/material with BRG if not already registered.
        /// </summary>
        internal ElementBatch GetOrCreateBatch(
            Mesh mesh,
            Material material,
            int submeshIndex,
            int sourceRendererID,
            int capacity = -1,
            Matrix4x4? meshLocalOffset = null)
        {
            // Build key from registered IDs
            int meshKey = mesh.GetInstanceID();
            int matKey = material.GetInstanceID();

            // Register with BRG if needed
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

            if (capacity < 0)
                capacity = DefaultBatchCapacity;

            // Allocate GPU memory from the heap
            var block = _heapAllocator.Allocate((ulong)(BytesPerInstance * capacity), 16);

            if (block.Empty)
            {
                Debug.LogError($"[HighwayElementGraphicsSystem] Heap allocation failed for {capacity} instances ({BytesPerInstance * capacity} bytes).");
                return null;
            }

            // Compute SoA offsets within the allocation
            int objectToWorldOffset = (int)block.begin;
            int worldToObjectOffset = objectToWorldOffset + 48 * capacity;
            int baseColorOffset = worldToObjectOffset + 48 * capacity;

            // Build metadata array for BRG
            var metadata = new NativeArray<MetadataValue>(3, Allocator.Temp);
            metadata[0] = new MetadataValue
            {
                NameID = Shader.PropertyToID("unity_ObjectToWorld"),
                Value = 0x80000000u | (uint)objectToWorldOffset
            };
            metadata[1] = new MetadataValue
            {
                NameID = Shader.PropertyToID("unity_WorldToObject"),
                Value = 0x80000000u | (uint)worldToObjectOffset
            };
            metadata[2] = new MetadataValue
            {
                NameID = Shader.PropertyToID("_BaseColor"),
                Value = 0x80000000u | (uint)baseColorOffset
            };

            // Register batch with BRG
            BatchID batchID = _brg.AddBatch(metadata, _gpuBufferHandle);

            // Dispose the metadata array (Temp allocator)
            metadata.Dispose();

            var batch = new ElementBatch
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
                meshLocalOffset = meshLocalOffset ?? Matrix4x4.identity
            };

            _batches[key] = batch;
            return batch;
        }

        /// <summary>
        /// Removes a batch and releases its GPU memory.
        /// </summary>
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
        /// Removes all batches that have no active instances.
        /// </summary>
        internal void GarbageCollectEmptyBatches()
        {
            if (_disposed) return;
            // Collect keys to remove (avoids modifying dictionary during iteration)
            var keysToRemove = new List<BatchKey>(_batches.Count);
            foreach (var kvp in _batches)
            {
                if (kvp.Value.activeCount == 0)
                    keysToRemove.Add(kvp.Key);
            }

            foreach (var key in keysToRemove)
                RemoveBatch(key);
        }

        #endregion

        #region Data Upload (Task 2.8)

        /// <summary>
        /// Flushes pending uploads to the GPU.
        /// </summary>
        internal JobHandle UploadDirtyData(JobHandle dependency)
        {
            if (_sparseUploader != null)
                _sparseUploader.Commit();
            return dependency;
        }

        #endregion

        #region Culling Callback (Task 2.9)

        private unsafe JobHandle OnPerformCullingCallback(
            BatchRendererGroup rendererGroup,
            BatchCullingContext cullingContext,
            BatchCullingOutput cullingOutput,
            IntPtr userContext)
        {
            // First pass: count total visible instances
            int totalVisible = 0;
            foreach (var batch in _batches.Values)
            {
                totalVisible += batch.activeCount;
            }

            if (totalVisible == 0)
            {
                return default;
            }

            // Allocate draw command and visibility arrays
            int drawCommandsSize = totalVisible * UnsafeUtility.SizeOf<BatchDrawCommand>();
            int instancesSize = totalVisible * sizeof(int);

            void* drawCommandsPtr = UnsafeUtility.Malloc(drawCommandsSize, 4, Allocator.TempJob);
            void* instancesPtr = UnsafeUtility.Malloc(instancesSize, 4, Allocator.TempJob);

            int visibleInstanceOffset = 0;
            int drawCommandIndex = 0;

            // Fill draw commands for each active batch
            foreach (var batch in _batches.Values)
            {
                if (batch.activeCount <= 0)
                    continue;

                var cmd = new BatchDrawCommand
                {
                    batchID = batch.batchID,
                    materialID = batch.materialID,
                    meshID = batch.meshID,
                    submeshIndex = (ushort)batch.submeshIndex,
                    visibleCount = (uint)batch.activeCount,
                    visibleOffset = (uint)visibleInstanceOffset,
                    splitVisibilityMask = 0xff,
                    flags = 0,
                    sortingPosition = 0
                };

                UnsafeUtility.WriteArrayElement(drawCommandsPtr, drawCommandIndex, cmd);
                drawCommandIndex++;

                // Fill visibility instances (0..activeCount-1)
                for (int i = 0; i < batch.activeCount; i++)
                {
                    UnsafeUtility.WriteArrayElement(instancesPtr, visibleInstanceOffset + i, i);
                }
                visibleInstanceOffset += batch.activeCount;
            }

            // Write draw commands to culling output
            var output = cullingOutput.drawCommands[0];
            output.drawCommands = (BatchDrawCommand*)drawCommandsPtr;
            output.drawRanges = (BatchDrawRange*)UnsafeUtility.Malloc(UnsafeUtility.SizeOf<BatchDrawRange>(), 4, Allocator.TempJob);
            output.drawRanges[0].drawCommandsBegin = 0;
            output.drawRanges[0].drawCommandsCount = (uint)drawCommandIndex;
            output.drawRanges[0].filterSettings = new BatchFilterSettings { renderingLayerMask = 0xffffffff };
            output.drawCommandCount = drawCommandIndex;
            output.drawRangeCount = 1;
            output.visibleInstanceCount = visibleInstanceOffset;
            output.visibleInstances = (int*)instancesPtr;
            output.instanceSortingPositions = null;
            output.instanceSortingPositionFloatCount = 0;
#pragma warning disable CS0618 // drawCommandPickingInstanceIDs is deprecated
            output.drawCommandPickingInstanceIDs = null;
#pragma warning restore CS0618
            cullingOutput.drawCommands[0] = output;

            return default;
        }

        #endregion

        #region Mesh/Material Registration

        /// <summary>
        /// Registers a mesh with the BRG and caches the ID.
        /// </summary>
        internal BatchMeshID RegisterMesh(Mesh mesh)
        {
            if (_disposed) return default;
            int id = mesh.GetInstanceID();
            if (_meshIDs.TryGetValue(id, out var cached))
                return cached;

            var meshID = _brg.RegisterMesh(mesh);
            _meshIDs[id] = meshID;
            return meshID;
        }

        /// <summary>
        /// Registers a material with the BRG and caches the ID.
        /// </summary>
        internal BatchMaterialID RegisterMaterial(Material material)
        {
            if (_disposed) return default;
            int id = material.GetInstanceID();
            if (_materialIDs.TryGetValue(id, out var cached))
                return cached;

            var matID = _brg.RegisterMaterial(material);
            _materialIDs[id] = matID;
            return matID;
        }

        #endregion

        #region Tracker Management

        /// <summary>
        /// Registers a note tracker for rendering.
        /// </summary>
        internal void RegisterNoteTracker(INoteTracker tracker)
        {
            if (_disposed) return;
            _trackers.Add(tracker);
        }

        /// <summary>
        /// Unregisters a note tracker.
        /// </summary>
        internal void UnregisterNoteTracker(INoteTracker tracker)
        {
            if (_disposed) return;
            _trackers.Remove(tracker);
        }

        #endregion

        #region Instance Upload Helpers

        /// <summary>
        /// Uploads instance data for a single element in a batch.
        /// Writes objectToWorld, worldToObject, and baseColor to the GPU buffer.
        /// Uses SoA layout: each property has its own contiguous array.
        /// </summary>
        /// <param name="batch">The batch to upload into (class reference, mutations persist).</param>
        /// <param name="instanceIndex">The index within the batch (0-based).</param>
        /// <param name="objectToWorld">The object-to-world matrix.</param>
        /// <param name="baseColor">The base color (RGBA).</param>
        internal void UploadInstance(ElementBatch batch, int instanceIndex,
            Matrix4x4 objectToWorld, Vector4 baseColor)
        {
            if (instanceIndex >= batch.capacity)
            {
                Debug.LogError($"[HighwayElementGraphicsSystem] Instance index {instanceIndex} exceeds batch capacity {batch.capacity}.");
                return;
            }

            // SoA layout: each property has its own contiguous array
            // objectToWorld: 48 bytes per instance (packed float3x4)
            // worldToObject: 48 bytes per instance (packed float3x4)
            // baseColor: 16 bytes per instance (float4)
            int owtOffset = batch.objectToWorldOffset + instanceIndex * 48;
            int wtoOffset = batch.worldToObjectOffset + instanceIndex * 48;
            int colorOffset = batch.baseColorOffset + instanceIndex * 16;

            // Object-to-world matrix (48 bytes = 12 floats, packed float3x4)
            PackedMatrix packedOW = PackedMatrix.FromMatrix4x4(objectToWorld);
            _sparseUploader.AddUpload(packedOW, owtOffset);

            // World-to-object matrix (48 bytes = 12 floats, packed float3x4)
            PackedMatrix packedWO = PackedMatrix.FromInverse(objectToWorld);
            _sparseUploader.AddUpload(packedWO, wtoOffset);

            // Base color (16 bytes = 4 floats)
            _sparseUploader.AddUpload(baseColor, colorOffset);

            // Update active count (persists because ElementBatch is a class)
            if (instanceIndex >= batch.activeCount)
                batch.activeCount = instanceIndex + 1;
        }

        /// <summary>
        /// Resets the active count of a batch (called when instances are removed).
        /// </summary>
        internal void ResetBatchActiveCount(ElementBatch batch)
        {
            batch.activeCount = 0;
        }

        #endregion
    }
}
