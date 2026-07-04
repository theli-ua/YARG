using System;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;

namespace YARG.Gameplay.Visuals.Instancing
{
    /// <summary>
    /// Uploads data into a GPU GraphicsBuffer.
    /// Uses compute shader when available for efficient scattered writes.
    /// Falls back to direct GraphicsBuffer.SetData when shader is unavailable.
    /// </summary>
    public unsafe class SparseUploader : IDisposable
    {
        private GraphicsBuffer m_DestinationBuffer;
        private bool m_ShaderAvailable;

        // Compute shader path — matches EGS architecture
        private ComputeShader m_Shader;
        private int m_CopyKernelIndex;
        private GraphicsBuffer m_UploadBuffer; // persistent, LockBufferForWrite capable

        // EGS-style: ops grow from buffer start, data grows from buffer end
        private int m_BufferChunkSize;
        private int m_OperationOffset; // bytes consumed from start (ops)
        private int m_DataOffset;      // bytes consumed from end (data)

        // Lock state for compute path
        private byte* m_UploadBufferPtr;

        private readonly int m_SrcBufferID;
        private readonly int m_DstBufferID;
        private readonly int m_OperationsBaseID;

        private bool m_Disposed;

        /// <summary>
        /// Constructs a new sparse uploader with the specified buffer as the target.
        /// </summary>
        public SparseUploader(GraphicsBuffer destinationBuffer, int bufferChunkSize = 256 * 1024)
        {
            m_DestinationBuffer = destinationBuffer;
            m_BufferChunkSize = bufferChunkSize;
            m_DirectMinOffset = int.MaxValue;
            m_DirectMaxOffset = 0;

            m_SrcBufferID = Shader.PropertyToID("srcBuffer");
            m_DstBufferID = Shader.PropertyToID("dstBuffer");
            m_OperationsBaseID = Shader.PropertyToID("operationsBase");

            // Try to load compute shader
            m_Shader = Resources.Load<ComputeShader>("SparseUploader");
            if (m_Shader != null)
            {
                m_CopyKernelIndex = m_Shader.FindKernel("CopyKernel");
                if (m_CopyKernelIndex >= 0)
                {
                    m_ShaderAvailable = true;
                    m_UploadBuffer = new GraphicsBuffer(
                        GraphicsBuffer.Target.Raw,
                        GraphicsBuffer.UsageFlags.LockBufferForWrite,
                        bufferChunkSize / 4,
                        4);
                    Debug.Log($"[SparseUploader] Compute shader active.");
                    return;
                }
            }

            m_ShaderAvailable = false;
            Debug.Log("[SparseUploader] Compute shader unavailable, using direct upload (GraphicsBuffer.SetData).");
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (m_Disposed) return;
            m_Disposed = true;

            if (m_ShaderAvailable)
            {
                m_UploadBuffer?.Dispose();
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
                // Compute path: lock buffer on first AddUpload, write directly
                if (m_UploadBufferPtr == null)
                {
                    var lockResult = m_UploadBuffer.LockBufferForWrite<byte>(0, m_BufferChunkSize);
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

        private void AddUploadLocked(byte* buffer, void* src, int size, int offsetInBytes, int repeatCount)
        {
            int opSize = UnsafeUtility.SizeOf<Operation>();
            for (int r = 0; r < repeatCount; r++)
            {
                int destOffset = offsetInBytes + r * size;
                int srcOffset = m_BufferChunkSize - m_DataOffset - size;
                UnsafeUtility.MemCpy(buffer + srcOffset, src, size);
                m_DataOffset += size;

                var op = new Operation
                {
                    type = 0,
                    srcOffset = (uint)srcOffset,
                    dstOffset = (uint)destOffset,
                    size = (uint)size,
                    count = (uint)repeatCount
                };
                UnsafeUtility.MemCpy(buffer + m_OperationOffset, &op, opSize);
                m_OperationOffset += opSize;
            }
        }

        #region Compute Shader Path (EGS architecture)

        private void CommitCompute()
        {
            if (m_UploadBufferPtr == null)
                return;

            int numOps = m_OperationOffset / UnsafeUtility.SizeOf<Operation>();

            if (numOps > 0)
            {
                // Unlock makes staged data visible to GPU
                m_UploadBuffer.UnlockBufferAfterWrite<byte>(m_BufferChunkSize);

                const int k_MaxThreadGroups = 65535;
                for (int iOp = 0; iOp < numOps; iOp += k_MaxThreadGroups)
                {
                    int opsEnd = Mathf.Min(iOp + k_MaxThreadGroups, numOps);
                    int numThreadGroups = opsEnd - iOp;

                    m_Shader.SetBuffer(m_CopyKernelIndex, m_SrcBufferID, m_UploadBuffer);
                    m_Shader.SetBuffer(m_CopyKernelIndex, m_DstBufferID, m_DestinationBuffer);
                    m_Shader.SetInt(m_OperationsBaseID, iOp);
                    m_Shader.Dispatch(m_CopyKernelIndex, numThreadGroups, 1, 1);
                }
            }
            else
            {
                m_UploadBuffer.UnlockBufferAfterWrite<byte>(0);
            }

            m_UploadBufferPtr = null;
        }

        #endregion

        #region Direct Upload Fallback

        // Op records where in the eager staging buffer the data lives + where it goes on GPU
        [StructLayout(LayoutKind.Sequential)]
        private struct DirectUploadOp
        {
            public int stagingOffset; // byte offset in m_DirectStagingData
            public int size;
            public int offsetInBytes; // byte offset in destination GraphicsBuffer
        }

        private const int InitialDirectOpCapacity = 64;
        private int m_DirectOpCount;
        private int m_DirectOpCapacity;
        private DirectUploadOp* m_DirectOps;

        // Eager data staging — data copied at AddUpload time (like EGS compute path)
        private int m_DirectDataSize;
        private int m_DirectDataCapacity;
        private byte* m_DirectStagingData;

        // Dirty range tracking for single-SetData commit
        private int m_DirectMinOffset;
        private int m_DirectMaxOffset;

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

                // Eagerly copy data into staging buffer (no dangling pointer)
                int stagingOffset = EnsureDirectDataSpace(size, src);

                // Stage operation metadata
                EnsureDirectOpSpace();
                m_DirectOps[m_DirectOpCount].stagingOffset = stagingOffset;
                m_DirectOps[m_DirectOpCount].size = size;
                m_DirectOps[m_DirectOpCount].offsetInBytes = destOffset;
                m_DirectOpCount++;

                // Track dirty range
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

            // Copy source data into staging buffer NOW (eager, like EGS)
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

            // Scatter staged data into a single NativeArray covering [minOffset, maxOffset)
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
            m_DirectMinOffset = int.MaxValue;
            m_DirectMaxOffset = 0;
        }

        #endregion

        // Operation struct matching the compute shader
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
