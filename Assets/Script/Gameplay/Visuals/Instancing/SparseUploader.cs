using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;

namespace YARG.Gameplay.Visuals.Instancing
{
    /// <summary>
    /// Column-major 3x4 matrix (12 floats, 48 bytes).
    /// Used for packed matrix uploads to GPU.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct float3x4
    {
        public float c0x, c0y, c0z;
        public float c1x, c1y, c1z;
        public float c2x, c2y, c2z;
        public float c3x, c3y, c3z;
    }

    internal enum OperationType : int
    {
        Upload = 0,
        Matrix_4x4 = 1,
        Matrix_Inverse_4x4 = 2,
        Matrix_3x4 = 3,
        Matrix_Inverse_3x4 = 4,
        StridedUpload = 5,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Operation
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

    /// <summary>
    /// Provides utility methods that you can use to upload data into GPU memory.
    /// </summary>
    /// <remarks>
    /// Phase 1: single-buffer synchronous mode. Add operations via AddUpload/AddMatrixUpload,
    /// then call Commit() to dispatch all pending uploads at once.
    /// </remarks>
    public unsafe class SparseUploader : IDisposable
    {
        const int k_MaxThreadGroupsPerDispatch = 65535;

        private int m_BufferChunkSize;
        private GraphicsBuffer m_DestinationBuffer;

        private readonly List<GraphicsBuffer> m_UploadBuffers;
        private readonly List<byte[]> m_BufferData;
        private readonly List<long> m_BufferMarkers;
        private readonly List<int> m_BufferOperationCount;

        private int m_OperationCount;
        private int m_OperationCapacity;
        private Operation* m_Operations;

        private byte* m_DataBase;
        private long m_DataWriteOffset;

        private ComputeShader m_SparseUploaderShader;
        private int m_CopyKernelIndex;
        private int m_ReplaceKernelIndex;

        private readonly int m_SrcBufferID;
        private readonly int m_DstBufferID;
        private readonly int m_OperationsBaseID;
        private readonly int m_ReplaceOperationSize;

        private long m_CurrentFrameUploadSize;
        private long m_MaxUploadSize;

        /// <summary>
        /// Constructs a new sparse uploader with the specified buffer as the target.
        /// </summary>
        /// <param name="destinationBuffer">The target buffer to write uploads into.</param>
        /// <param name="bufferChunkSize">The upload buffer chunk size.</param>
        public SparseUploader(GraphicsBuffer destinationBuffer, int bufferChunkSize = 256 * 1024)
        {
            m_BufferChunkSize = bufferChunkSize;
            m_DestinationBuffer = destinationBuffer;

            m_UploadBuffers = new List<GraphicsBuffer>();
            m_BufferData = new List<byte[]>();
            m_BufferMarkers = new List<long>();
            m_BufferOperationCount = new List<int>();

            m_OperationCapacity = 4096;
            m_Operations = (Operation*)UnsafeUtility.Malloc(m_OperationCapacity * UnsafeUtility.SizeOf<Operation>(), 8, Allocator.Persistent);
            m_OperationCount = 0;

            var dataBufferSize = bufferChunkSize;
            m_DataBase = (byte*)UnsafeUtility.Malloc(dataBufferSize, 64, Allocator.Persistent);
            m_DataWriteOffset = dataBufferSize;

            m_SparseUploaderShader = Resources.Load<ComputeShader>("SparseUploader");
            if (m_SparseUploaderShader == null)
            {
                Debug.LogWarning("[SparseUploader] SparseUploader compute shader not found in Resources. Uploads will be no-ops.");
            }
            else
            {
                m_CopyKernelIndex = m_SparseUploaderShader.FindKernel("CopyKernel");
                m_ReplaceKernelIndex = m_SparseUploaderShader.FindKernel("ReplaceKernel");
            }

            m_SrcBufferID = Shader.PropertyToID("srcBuffer");
            m_DstBufferID = Shader.PropertyToID("dstBuffer");
            m_OperationsBaseID = Shader.PropertyToID("operationsBase");
            m_ReplaceOperationSize = Shader.PropertyToID("replaceOperationSize");

            m_CurrentFrameUploadSize = 0;
            m_MaxUploadSize = 0;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            UnsafeUtility.Free(m_Operations, Allocator.Persistent);
            UnsafeUtility.Free(m_DataBase, Allocator.Persistent);

            for (int i = 0; i < m_UploadBuffers.Count; ++i)
            {
                if (m_UploadBuffers[i] != null && m_UploadBuffers[i].IsValid())
                    m_UploadBuffers[i].Dispose();
            }
        }

        /// <summary>
        /// Replaces the destination GPU buffer with a new one.
        /// </summary>
        public void ReplaceBuffer(GraphicsBuffer buffer, bool copyFromPrevious = false)
        {
            if (copyFromPrevious && m_DestinationBuffer != null && m_SparseUploaderShader != null)
            {
                var srcSize = m_DestinationBuffer.count * m_DestinationBuffer.stride;
                m_SparseUploaderShader.SetBuffer(m_ReplaceKernelIndex, m_SrcBufferID, m_DestinationBuffer);
                m_SparseUploaderShader.SetBuffer(m_ReplaceKernelIndex, m_DstBufferID, buffer);
                m_SparseUploaderShader.SetInt(m_ReplaceOperationSize, (int)srcSize);
                m_SparseUploaderShader.Dispatch(m_ReplaceKernelIndex, 1, 1, 1);
            }

            m_DestinationBuffer = buffer;
        }

        /// <summary>
        /// Adds a new pending upload operation.
        /// </summary>
        public void AddUpload(void* src, int size, int offsetInBytes, int repeatCount = 1)
        {
            if (m_SparseUploaderShader == null) return;

            if (repeatCount <= 0) repeatCount = 1;

            EnsureOperationsSpace(1);
            EnsureDataSpace(size);

            int dataOffset = (int)(m_DataBase + m_BufferChunkSize - m_DataWriteOffset - size);
            UnsafeUtility.MemCpy(m_DataBase + dataOffset, src, size);

            var op = new Operation
            {
                type = (uint)OperationType.Upload,
                srcOffset = (uint)dataOffset,
                dstOffset = (uint)offsetInBytes,
                dstOffsetExtra = 0,
                size = (uint)size,
                count = (uint)repeatCount
            };

            UnsafeUtility.MemCpy(m_Operations + m_OperationCount, &op, UnsafeUtility.SizeOf<Operation>());
            m_OperationCount++;
            m_CurrentFrameUploadSize += size * repeatCount;
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
        /// Options for matrix upload type.
        /// </summary>
        public enum MatrixType
        {
            MatrixType4x4,
            MatrixType3x4,
        }

        /// <summary>
        /// Adds a pending matrix upload operation.
        /// </summary>
        public void AddMatrixUpload(void* src, int numMatrices, int offset, MatrixType srcType, MatrixType dstType)
        {
            AddMatrixHelper(src, numMatrices, offset, -1, srcType, dstType);
        }

        /// <summary>
        /// Adds a pending matrix upload with inverse computation.
        /// </summary>
        public void AddMatrixUploadAndInverse(void* src, int numMatrices, int offset, int offsetInverse, MatrixType srcType, MatrixType dstType)
        {
            AddMatrixHelper(src, numMatrices, offset, offsetInverse, srcType, dstType);
        }

        private void AddMatrixHelper(void* src, int numMatrices, int offset, int offsetInverse, MatrixType srcType, MatrixType dstType)
        {
            if (m_SparseUploaderShader == null) return;

            var size = numMatrices * sizeof(float3x4);
            EnsureOperationsSpace(1);
            EnsureDataSpace(size);

            int dataOffset = (int)(m_DataBase + m_BufferChunkSize - m_DataWriteOffset - size);

            if (srcType == MatrixType.MatrixType4x4)
            {
                var srcLocal = (byte*)src;
                var dstLocal = m_DataBase + dataOffset;
                for (int i = 0; i < numMatrices; ++i)
                {
                    for (int j = 0; j < 4; ++j)
                    {
                        UnsafeUtility.MemCpy(dstLocal, srcLocal, 12);
                        dstLocal += 12;
                        srcLocal += 16;
                    }
                }
            }
            else
            {
                UnsafeUtility.MemCpy(m_DataBase + dataOffset, src, size);
            }

            uint uploadType = (offsetInverse == -1) ? (uint)OperationType.Matrix_4x4 : (uint)OperationType.Matrix_Inverse_4x4;
            uploadType += (dstType == MatrixType.MatrixType3x4) ? 2u : 0u;

            var op = new Operation
            {
                type = uploadType,
                srcOffset = (uint)dataOffset,
                dstOffset = (uint)offset,
                dstOffsetExtra = (uint)offsetInverse,
                size = (uint)size,
                count = 1,
            };

            UnsafeUtility.MemCpy(m_Operations + m_OperationCount, &op, UnsafeUtility.SizeOf<Operation>());
            m_OperationCount++;
            m_CurrentFrameUploadSize += size;
        }

        /// <summary>
        /// Adds a strided upload operation.
        /// </summary>
        public void AddStridedUpload(void* src, uint elemSize, uint srcStride, uint count, uint dstOffset, int dstStride)
        {
            if (m_SparseUploaderShader == null) return;

            uint dataSize = count * srcStride;
            EnsureOperationsSpace(1);
            EnsureDataSpace((int)dataSize);

            int dataOffset = (int)(m_DataBase + m_BufferChunkSize - m_DataWriteOffset - (int)dataSize);
            UnsafeUtility.MemCpy(m_DataBase + dataOffset, src, dataSize);

            var op = new Operation
            {
                type = (uint)OperationType.StridedUpload,
                srcOffset = (uint)dataOffset,
                srcStride = srcStride,
                dstOffset = (uint)dstOffset,
                dstOffsetExtra = 0,
                dstStride = dstStride,
                size = elemSize,
                count = count,
            };

            UnsafeUtility.MemCpy(m_Operations + m_OperationCount, &op, UnsafeUtility.SizeOf<Operation>());
            m_OperationCount++;
            m_CurrentFrameUploadSize += (long)dataSize;
        }

        /// <summary>
        /// Commits all pending upload operations to the GPU.
        /// </summary>
        public void Commit()
        {
            if (m_SparseUploaderShader == null || m_DestinationBuffer == null)
            {
                ResetFrame();
                return;
            }

            if (m_OperationCount == 0)
            {
                ResetFrame();
                return;
            }

            int operationSize = UnsafeUtility.SizeOf<Operation>();
            int totalOpSize = m_OperationCount * operationSize;
            int bufferSize = m_BufferChunkSize;

            var uploadBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Raw, bufferSize / 4, 4);
            uploadBuffer.name = "SparseUploaderUploadBuffer";

            var bufferData = uploadBuffer.LockBufferForWrite<byte>(0, bufferSize);
            byte* bufferPtr = (byte*)bufferData.GetUnsafePtr();

            UnsafeUtility.MemCpy(bufferPtr, m_Operations, totalOpSize);

            int dataWritePos = bufferSize;
            for (int i = m_OperationCount - 1; i >= 0; i--)
            {
                var op = m_Operations[i];
                int dataSize = (int)op.size * (int)op.count;
                dataWritePos -= dataSize;
                UnsafeUtility.MemCpy(bufferPtr + dataWritePos, m_DataBase + (int)op.srcOffset, dataSize);
            }

            uploadBuffer.UnlockBufferAfterWrite<byte>(bufferSize);

            int numOps = m_OperationCount;
            for (int iOp = 0; iOp < numOps; iOp += k_MaxThreadGroupsPerDispatch)
            {
                int opsBegin = iOp;
                int opsEnd = Mathf.Min(opsBegin + k_MaxThreadGroupsPerDispatch, numOps);
                int numThreadGroups = opsEnd - opsBegin;

                m_SparseUploaderShader.SetBuffer(m_CopyKernelIndex, m_SrcBufferID, uploadBuffer);
                m_SparseUploaderShader.SetBuffer(m_CopyKernelIndex, m_DstBufferID, m_DestinationBuffer);
                m_SparseUploaderShader.SetInt(m_OperationsBaseID, opsBegin);

                m_SparseUploaderShader.Dispatch(m_CopyKernelIndex, numThreadGroups, 1, 1);
            }

            uploadBuffer.Dispose();
            ResetFrame();
        }

        /// <summary>
        /// Calculates statistics about the current and previous frame uploads.
        /// </summary>
        public SparseUploaderStats ComputeStats()
        {
            var stats = default(SparseUploaderStats);
            stats.BytesGPUMemoryUsed = m_BufferChunkSize;
            stats.BytesGPUMemoryUploadedCurr = m_CurrentFrameUploadSize;
            stats.BytesGPUMemoryUploadedMax = m_MaxUploadSize;
            return stats;
        }

        private void EnsureOperationsSpace(int count)
        {
            if (m_OperationCount + count > m_OperationCapacity)
            {
                int newCapacity = Mathf.Max(m_OperationCapacity * 2, m_OperationCount + count);
                var newOps = (Operation*)UnsafeUtility.Malloc(newCapacity * UnsafeUtility.SizeOf<Operation>(), 8, Allocator.Persistent);
                UnsafeUtility.MemCpy(newOps, m_Operations, m_OperationCount * UnsafeUtility.SizeOf<Operation>());
                UnsafeUtility.Free(m_Operations, Allocator.Persistent);
                m_Operations = newOps;
                m_OperationCapacity = newCapacity;
            }
        }

        private void EnsureDataSpace(int size)
        {
            if ((long)size > m_DataWriteOffset)
            {
                int oldSize = m_BufferChunkSize;
                int newSize = Mathf.Max(oldSize * 2, oldSize + size);
                byte* newData = (byte*)UnsafeUtility.Malloc(newSize, 64, Allocator.Persistent);
                int usedDataSize = oldSize - (int)m_DataWriteOffset;
                if (usedDataSize > 0)
                {
                    UnsafeUtility.MemCpy(newData + newSize - usedDataSize, m_DataBase + oldSize - usedDataSize, usedDataSize);
                }
                m_DataWriteOffset = newSize - usedDataSize;
                UnsafeUtility.Free(m_DataBase, Allocator.Persistent);
                m_DataBase = newData;
                m_BufferChunkSize = newSize;
            }
        }

        private void ResetFrame()
        {
            m_OperationCount = 0;
            m_DataWriteOffset = m_BufferChunkSize;
            if (m_CurrentFrameUploadSize > m_MaxUploadSize)
                m_MaxUploadSize = m_CurrentFrameUploadSize;
            m_CurrentFrameUploadSize = 0;
        }
    }

    /// <summary>
    /// Represents SparseUploader statistics.
    /// </summary>
    public struct SparseUploaderStats
    {
        public long BytesGPUMemoryUsed;
        public long BytesGPUMemoryUploadedCurr;
        public long BytesGPUMemoryUploadedMax;
    }
}
