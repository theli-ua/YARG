// Copyright (c) Unity Technologies. SPDX-License-Identifier: BSD-3-Clause
// Adapted from Unity.Entities.Graphics SparseUploader.cs
// Changes: simplified for single-threaded main-thread use (no ThreadedSparseUploader/Burst),
// retained direct upload fallback for platforms without compute shader support.

using System;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace YARG.Gameplay.Visuals.Instancing
{
    /// <summary>
    /// Uploads data into a GPU GraphicsBuffer.
    /// Uses compute shader when available for efficient scattered writes.
    /// Falls back to direct GraphicsBuffer.SetData when shader is unavailable.
    ///
    /// Architecture matches Unity.Entities.Graphics SparseUploader:
    /// - Buffer pool with frame-in-flight tracking (prevents CPU overwriting GPU-read buffer)
    /// - LockBufferForWrite → write ops+data → UnlockBufferAfterWrite → Dispatch
    /// </summary>
    public unsafe class SparseUploader : IDisposable
    {
        const int k_MaxThreadGroupsPerDispatch = 65535;

        private int m_BufferChunkSize;

        private GraphicsBuffer m_DestinationBuffer;

        // --- Compute shader path (EGS architecture) ---

        private GraphicsBuffer[] m_UploadBuffers;
        private int m_UploadBufferCount;
        private int m_CurrUploadBufferFrame;
        private int m_CurrBufferIndex;
        private byte* m_UploadBufferPtr;

        private int m_OperationOffset; // bytes consumed from buffer start (ops)
        private int m_DataOffset;      // bytes consumed from buffer end (data)

        private ComputeShader m_SparseUploaderShader;
        private int m_CopyKernelIndex;

        private readonly int m_SrcBufferID;
        private readonly int m_DstBufferID;
        private readonly int m_OperationsBaseID;

        private bool m_ShaderAvailable;

        // --- Direct upload fallback ---

        private DirectUploadOp* m_DirectOps;
        private int m_DirectOpCount;
        private int m_DirectOpCapacity;

        private byte* m_DirectStagingData;
        private int m_DirectDataSize;
        private int m_DirectDataCapacity;

        private int m_DirectMinOffset;
        private int m_DirectMaxOffset;

        private bool m_Disposed;

        /// <summary>
        /// Constructs a new sparse uploader with the specified buffer as the target.
        /// </summary>
        /// <param name="destinationBuffer">The target buffer to write uploads into.</param>
        /// <param name="bufferChunkSize">The upload buffer chunk size in bytes (default 16MB, matches EGS).</param>
        public SparseUploader(GraphicsBuffer destinationBuffer, int bufferChunkSize = 16 * 1024 * 1024)
        {
            m_DestinationBuffer = destinationBuffer;
            m_BufferChunkSize = bufferChunkSize;
            m_DirectMinOffset = int.MaxValue;
            m_DirectMaxOffset = 0;

            m_SrcBufferID = Shader.PropertyToID("srcBuffer");
            m_DstBufferID = Shader.PropertyToID("dstBuffer");
            m_OperationsBaseID = Shader.PropertyToID("operationsBase");

            // Try to load compute shader (exact asset from EGS: Unity.Entities.Graphics/Resources/SparseUploader.compute)
            m_SparseUploaderShader = Resources.Load<ComputeShader>("SparseUploader");
            if (m_SparseUploaderShader != null)
            {
                m_CopyKernelIndex = m_SparseUploaderShader.FindKernel("CopyKernel");
                if (m_CopyKernelIndex >= 0)
                {
                    m_ShaderAvailable = true;

                    // EGS: BufferPool with LockBufferForWrite capable buffers
                    // We use a fixed ring buffer instead of a dynamic pool since we only need one buffer per frame.
                    int numBuffers = NumFramesInFlight + 1;
                    m_UploadBufferCount = numBuffers;
                    m_UploadBuffers = new GraphicsBuffer[numBuffers];

                    for (int i = 0; i < numBuffers; i++)
                    {
                        m_UploadBuffers[i] = new GraphicsBuffer(
                            GraphicsBuffer.Target.Raw,
                            GraphicsBuffer.UsageFlags.LockBufferForWrite,
                            bufferChunkSize / 4,
                            4);
                        m_UploadBuffers[i].name = "SparseUploaderBuffer";
                    }

                    m_CurrBufferIndex = 0;
                    m_UploadBufferPtr = null;
                    return;
                }
            }

            m_ShaderAvailable = false;
            Debug.Log("[SparseUploader] Compute shader unavailable, using direct upload (GraphicsBuffer.SetData).");
        }

        /// <summary>
        /// Returns the number of frames in flight for the current graphics device.
        /// Copied from EGS SparseUploader.NumFramesInFlight.
        /// </summary>
        private static int NumFramesInFlight
        {
            get
            {
                int numFrames = 0;

                switch (SystemInfo.graphicsDeviceType)
                {
                    case GraphicsDeviceType.Vulkan:
                    case GraphicsDeviceType.Direct3D11:
                    case GraphicsDeviceType.Direct3D12:
                    case GraphicsDeviceType.PlayStation4:
                    case GraphicsDeviceType.PlayStation5:
                    case GraphicsDeviceType.XboxOne:
                    case GraphicsDeviceType.GameCoreXboxOne:
                    case GraphicsDeviceType.GameCoreXboxSeries:
                    case GraphicsDeviceType.OpenGLCore:
#if !UNITY_2023_1_OR_NEWER
                    case GraphicsDeviceType.OpenGLES2:
#endif
                    case GraphicsDeviceType.OpenGLES3:
                    case GraphicsDeviceType.PlayStation5NGGC:
                        numFrames = 3;
                        break;
                    case GraphicsDeviceType.Switch:
                    case GraphicsDeviceType.Metal:
                    default:
                        numFrames = 4;
                        break;
                }

                // Use at least as many frames as the quality settings have, but use a platform
                // specific lower limit in any case.
                numFrames = math.max(numFrames, QualitySettings.maxQueuedFrames);

                return numFrames;
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (m_Disposed) return;
            m_Disposed = true;

            if (m_ShaderAvailable)
            {
                for (int i = 0; i < m_UploadBufferCount; i++)
                {
                    m_UploadBuffers[i]?.Dispose();
                }
            }

            // Free direct upload staging
            if (m_DirectOpCapacity > 0 && m_DirectOps != null)
                UnsafeUtility.Free(m_DirectOps, Allocator.Persistent);
            if (m_DirectDataCapacity > 0 && m_DirectStagingData != null)
                UnsafeUtility.Free(m_DirectStagingData, Allocator.Persistent);
        }

        // --- Public API (backward compatible) ---

        /// <summary>
        /// Adds a new pending upload operation.
        /// </summary>
        public void AddUpload(void* src, int size, int offsetInBytes, int repeatCount = 1)
        {
            if (repeatCount <= 0) repeatCount = 1;

            if (m_ShaderAvailable)
            {
                // EGS: allocate fresh buffer from pool each frame, lock for write
                if (m_UploadBufferPtr == null)
                {
                    // Pick next buffer from ring (cycling through pool)
                    m_CurrBufferIndex = (m_CurrBufferIndex + 1) % m_UploadBufferCount;
                    var buffer = m_UploadBuffers[m_CurrBufferIndex];

                    // LockBufferForWrite acts as sync point — blocks until GPU is done with this buffer
                    var lockResult = buffer.LockBufferForWrite<byte>(0, m_BufferChunkSize);
                    m_UploadBufferPtr = (byte*)lockResult.GetUnsafePtr();
                    m_OperationOffset = 0;
                    m_DataOffset = 0;
                }

                AddUploadLocked(m_UploadBufferPtr, src, size, offsetInBytes, repeatCount);
            }
            else
            {
                AddUploadDirect(src, size, offsetInBytes, repeatCount);
            }
        }

        /// <summary>
        /// Adds a new pending upload from a value.
        /// </summary>
        public void AddUpload<T>(T val, int offsetInBytes, int repeatCount = 1) where T : unmanaged
        {
            var size = UnsafeUtility.SizeOf<T>();
            AddUpload(&val, size, offsetInBytes, repeatCount);
        }

        /// <summary>
        /// Adds a new pending upload from a NativeArray.
        /// </summary>
        public void AddUpload<T>(NativeArray<T> array, int offsetInBytes, int repeatCount = 1) where T : unmanaged
        {
            var size = UnsafeUtility.SizeOf<T>() * array.Length;
            AddUpload(array.GetUnsafeReadOnlyPtr(), size, offsetInBytes, repeatCount);
        }

        /// <summary>
        /// Commits all pending upload operations to the GPU.
        /// </summary>
        public void Commit()
        {
            if (m_DestinationBuffer == null || m_Disposed) return;

            if (m_ShaderAvailable)
            {
                CommitCompute();
            }
            else
            {
                CommitDirect();
            }
        }

        #region Compute Shader Path (EGS architecture)

        /// <summary>
        /// EGS: writes operation + data into the locked intermediate buffer.
        /// Operations grow from buffer start, data grows from buffer end.
        /// </summary>
        private void AddUploadLocked(byte* buffer, void* src, int size, int offsetInBytes, int repeatCount)
        {
            int opSize = UnsafeUtility.SizeOf<Operation>();

            for (int r = 0; r < repeatCount; r++)
            {
                int destOffset = offsetInBytes + r * size;

                // Data offset: from the end of the buffer
                int srcOffset = m_BufferChunkSize - m_DataOffset - size;

                // Copy source data into buffer at data region
                UnsafeUtility.MemCpy(buffer + srcOffset, src, size);
                m_DataOffset += size;

                // Write Operation struct at the beginning of the buffer (zeroed first)
                Operation op;
                UnsafeUtility.MemSet(&op, 0, opSize);
                op.type = 0; // OperationType.Upload
                op.srcOffset = (uint)srcOffset;
                op.dstOffset = (uint)destOffset;
                op.size = (uint)size;
                op.count = (uint)repeatCount;

                UnsafeUtility.MemCpy(buffer + m_OperationOffset, &op, opSize);
                m_OperationOffset += opSize;
            }
        }

        private void CommitCompute()
        {
            if (m_UploadBufferPtr == null)
                return;

            int numOps = m_OperationOffset / UnsafeUtility.SizeOf<Operation>();
            var buffer = m_UploadBuffers[m_CurrBufferIndex];

            if (numOps > 0)
            {
                // EGS: UnlockBufferAfterWrite makes staged data visible to GPU
                buffer.UnlockBufferAfterWrite<byte>(m_BufferChunkSize);

                DispatchUploads(numOps, buffer);

                // Track frame for buffer recovery
                m_CurrUploadBufferFrame = Time.renderedFrameCount;
            }
            else
            {
                buffer.UnlockBufferAfterWrite<byte>(0);
            }

            m_UploadBufferPtr = null;
        }

        /// <summary>
        /// EGS: DispatchUploads — each thread group processes one operation.
        /// </summary>
        private void DispatchUploads(int numOps, GraphicsBuffer graphicsBuffer)
        {
            for (int iOp = 0; iOp < numOps; iOp += k_MaxThreadGroupsPerDispatch)
            {
                int opsBegin = iOp;
                int opsEnd = math.min(opsBegin + k_MaxThreadGroupsPerDispatch, numOps);
                int numThreadGroups = opsEnd - opsBegin;

                m_SparseUploaderShader.SetBuffer(m_CopyKernelIndex, m_SrcBufferID, graphicsBuffer);
                m_SparseUploaderShader.SetBuffer(m_CopyKernelIndex, m_DstBufferID, m_DestinationBuffer);
                m_SparseUploaderShader.SetInt(m_OperationsBaseID, opsBegin);

                m_SparseUploaderShader.Dispatch(m_CopyKernelIndex, numThreadGroups, 1, 1);
            }
        }

        #endregion

        #region Direct Upload Fallback

        [StructLayout(LayoutKind.Sequential)]
        private struct DirectUploadOp
        {
            public int stagingOffset;
            public int size;
            public int offsetInBytes;
        }

        private const int InitialDirectOpCapacity = 64;

        private void AddUploadDirect(void* src, int size, int offsetInBytes, int repeatCount)
        {
            if (m_DestinationBuffer == null || m_Disposed)
                return;

            int totalBufferSize = m_DestinationBuffer.count * 4;

            for (int r = 0; r < repeatCount; r++)
            {
                int destOffset = offsetInBytes + r * size;
                if (destOffset < 0 || destOffset + size > totalBufferSize)
                    continue;

                int stagingOffset = EnsureDirectDataSpace(size, src);

                EnsureDirectOpSpace();
                m_DirectOps[m_DirectOpCount].stagingOffset = stagingOffset;
                m_DirectOps[m_DirectOpCount].size = size;
                m_DirectOps[m_DirectOpCount].offsetInBytes = destOffset;
                m_DirectOpCount++;

                if (destOffset < m_DirectMinOffset)
                    m_DirectMinOffset = destOffset;
                int destEnd = destOffset + size;
                if (destEnd > m_DirectMaxOffset)
                    m_DirectMaxOffset = destEnd;
            }
        }

        private void EnsureDirectOpSpace()
        {
            if (m_DirectOpCount < m_DirectOpCapacity)
                return;

            int newCapacity = m_DirectOpCapacity == 0 ? InitialDirectOpCapacity : m_DirectOpCapacity * 2;
            DirectUploadOp* newOps = (DirectUploadOp*)UnsafeUtility.Malloc(
                newCapacity * (int)sizeof(DirectUploadOp), 16, Allocator.Persistent);
            if (m_DirectOpCapacity > 0)
                UnsafeUtility.MemCpy(newOps, m_DirectOps, m_DirectOpCount * (int)sizeof(DirectUploadOp));
            else
                UnsafeUtility.MemSet(newOps, 0, newCapacity * (int)sizeof(DirectUploadOp));

            m_DirectOpCapacity = newCapacity;
            m_DirectOps = newOps;
        }

        private int EnsureDirectDataSpace(int size, void* src)
        {
            int stagingOffset = m_DirectDataSize;
            if (m_DirectDataSize + size > m_DirectDataCapacity)
            {
                int newCapacity = m_DirectDataCapacity == 0 ? 64 * 1024 : m_DirectDataCapacity * 2;
                while (newCapacity < m_DirectDataSize + size)
                    newCapacity *= 2;

                byte* newData = (byte*)UnsafeUtility.Malloc(newCapacity, 64, Allocator.Persistent);
                if (m_DirectDataSize > 0)
                    UnsafeUtility.MemCpy(newData, m_DirectStagingData, m_DirectDataSize);
                if (m_DirectDataCapacity > 0)
                    UnsafeUtility.Free(m_DirectStagingData, Allocator.Persistent);

                m_DirectStagingData = newData;
                m_DirectDataCapacity = newCapacity;
            }

            UnsafeUtility.MemCpy(m_DirectStagingData + stagingOffset, src, size);
            m_DirectDataSize = stagingOffset + size;
            return stagingOffset;
        }

        private void CommitDirect()
        {
            if (m_DirectOpCount == 0 || m_DestinationBuffer == null)
            {
                ResetDirectFrame();
                return;
            }

            int rangeSize = m_DirectMaxOffset - m_DirectMinOffset;
            int rangeSizeInts = (rangeSize + 3) / 4;
            int elementOffset = m_DirectMinOffset / 4;

            var staging = new NativeArray<int>(rangeSizeInts, Allocator.Temp);

            for (int i = 0; i < m_DirectOpCount; i++)
            {
                var op = m_DirectOps[i];
                int relativeOffset = op.offsetInBytes - m_DirectMinOffset;
                int stagingIntOffset = relativeOffset / 4;
                byte* dstPtr = (byte*)staging.GetUnsafePtr();
                UnsafeUtility.MemCpy(dstPtr + stagingIntOffset * 4,
                    m_DirectStagingData + op.stagingOffset, op.size);
            }

            m_DestinationBuffer.SetData(staging, 0, elementOffset, rangeSizeInts);
            staging.Dispose();

            ResetDirectFrame();
        }

        private void ResetDirectFrame()
        {
            m_DirectOpCount = 0;
            m_DirectDataSize = 0;
            m_DirectMinOffset = int.MaxValue;
            m_DirectMaxOffset = 0;
        }

        #endregion

        // Operation struct — exact copy from EGS (Unity.Rendering.Operation)
        [StructLayout(LayoutKind.Sequential)]
        private struct Operation
        {
            public uint type;
            public uint srcOffset;
            public uint srcStride;
            public uint dstOffset;
            public uint dstOffsetExtra;
            public int dstStride;
            public uint size;
            public uint count;
        }
    }
}
