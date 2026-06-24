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
    /// Uploads data into a GPU GraphicsBuffer.
    /// Uses compute shader when available for efficient scattered writes.
    /// Falls back to direct GraphicsBuffer.SetData when shader is unavailable.
    /// </summary>
    public unsafe class SparseUploader : IDisposable
    {
        private GraphicsBuffer m_DestinationBuffer;
        private bool m_ShaderAvailable;

        // Compute shader path
        private ComputeShader m_Shader;
        private int m_CopyKernelIndex;

        // Staging for compute shader uploads
        private int m_BufferChunkSize;
        private int m_OperationCount;
        private int m_OperationCapacity;
        private Operation* m_Operations;
        private byte* m_DataBase;
        private long m_DataWriteOffset;

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
            m_ShaderAvailable = false;

            // Try to load compute shader
            var shader = Resources.Load<ComputeShader>("SparseUploader");
            if (shader != null)
            {
                try
                {
                    int copyKernel = shader.FindKernel("CopyKernel");
                    if (copyKernel >= 0)
                    {
                        m_Shader = shader;
                        m_CopyKernelIndex = copyKernel;
                        m_ShaderAvailable = true;

                        m_OperationCapacity = 4096;
                        m_Operations = (Operation*)UnsafeUtility.Malloc(
                            m_OperationCapacity * UnsafeUtility.SizeOf<Operation>(), 8, Allocator.Persistent);
                        m_OperationCount = 0;

                        m_DataBase = (byte*)UnsafeUtility.Malloc(bufferChunkSize, 64, Allocator.Persistent);
                        m_DataWriteOffset = bufferChunkSize;

                        m_SrcBufferID = Shader.PropertyToID("srcBuffer");
                        m_DstBufferID = Shader.PropertyToID("dstBuffer");
                        m_OperationsBaseID = Shader.PropertyToID("operationsBase");

                        Debug.Log("[SparseUploader] Compute shader loaded successfully.");
                        return;
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[SparseUploader] Failed to initialize compute shader: {e.Message}");
                }
            }

            // Fallback to direct uploads
            Debug.LogWarning("[SparseUploader] Compute shader unavailable. Using direct upload fallback.");
            m_ShaderAvailable = false;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (m_Disposed) return;
            m_Disposed = true;

            if (m_ShaderAvailable)
            {
                UnsafeUtility.Free(m_Operations, Allocator.Persistent);
                UnsafeUtility.Free(m_DataBase, Allocator.Persistent);
            }
        }

        /// <summary>
        /// Adds a new pending upload operation.
        /// </summary>
        public void AddUpload(void* src, int size, int offsetInBytes, int repeatCount = 1)
        {
            if (repeatCount <= 0) repeatCount = 1;

            if (m_ShaderAvailable)
            {
                AddUploadCompute(src, size, offsetInBytes, repeatCount);
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

        #region Compute Shader Path

        private void AddUploadCompute(void* src, int size, int offsetInBytes, int repeatCount)
        {
            EnsureOperationsSpace(1);
            EnsureDataSpace(size);

            int dataOffset = (int)(m_DataBase + m_BufferChunkSize - m_DataWriteOffset - size);
            UnsafeUtility.MemCpy(m_DataBase + dataOffset, src, size);

            var op = new Operation
            {
                type = 0, // Upload
                srcOffset = (uint)dataOffset,
                dstOffset = (uint)offsetInBytes,
                size = (uint)size,
                count = (uint)repeatCount
            };

            UnsafeUtility.MemCpy(m_Operations + m_OperationCount, &op, UnsafeUtility.SizeOf<Operation>());
            m_OperationCount++;
        }

        private void CommitCompute()
        {
            if (m_OperationCount == 0)
            {
                ResetComputeFrame();
                return;
            }

            const int k_MaxThreadGroups = 65535;
            int operationSize = UnsafeUtility.SizeOf<Operation>();
            int totalOpSize = m_OperationCount * operationSize;
            int bufferSize = m_BufferChunkSize;

            var uploadBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Raw, bufferSize / 4, 4);

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
            for (int iOp = 0; iOp < numOps; iOp += k_MaxThreadGroups)
            {
                int opsEnd = Mathf.Min(iOp + k_MaxThreadGroups, numOps);
                int numThreadGroups = opsEnd - iOp;

                m_Shader.SetBuffer(m_CopyKernelIndex, m_SrcBufferID, uploadBuffer);
                m_Shader.SetBuffer(m_CopyKernelIndex, m_DstBufferID, m_DestinationBuffer);
                m_Shader.SetInt(m_OperationsBaseID, iOp);
                m_Shader.Dispatch(m_CopyKernelIndex, numThreadGroups, 1, 1);
            }

            uploadBuffer.Dispose();
            ResetComputeFrame();
        }

        private void ResetComputeFrame()
        {
            m_OperationCount = 0;
            m_DataWriteOffset = m_BufferChunkSize;
        }

        private void EnsureOperationsSpace(int count)
        {
            if (m_OperationCount + count > m_OperationCapacity)
            {
                int newCapacity = Mathf.Max(m_OperationCapacity * 2, m_OperationCount + count);
                var newOps = (Operation*)UnsafeUtility.Malloc(
                    newCapacity * UnsafeUtility.SizeOf<Operation>(), 8, Allocator.Persistent);
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
                    UnsafeUtility.MemCpy(newData + newSize - usedDataSize, m_DataBase + oldSize - usedDataSize, usedDataSize);
                m_DataWriteOffset = newSize - usedDataSize;
                UnsafeUtility.Free(m_DataBase, Allocator.Persistent);
                m_DataBase = newData;
                m_BufferChunkSize = newSize;
            }
        }

        #endregion

        #region Direct Upload Fallback

        private void AddUploadDirect(void* src, int size, int offsetInBytes, int repeatCount)
        {
            // For direct uploads, stage data in a temp NativeArray and use SetData
            for (int r = 0; r < repeatCount; r++)
            {
                int destOffset = offsetInBytes + r * size;
                int elementOffset = destOffset / 4; // GraphicsBuffer is in ints
                int elemCount = (size + 3) / 4; // Round up to int boundary

                var temp = new NativeArray<int>(elemCount, Allocator.Temp);
                UnsafeUtility.MemCpy(temp.GetUnsafePtr(), src, size);
                m_DestinationBuffer.SetData(temp, 0, elementOffset, elemCount);
                temp.Dispose();
            }
        }

        private void CommitDirect()
        {
            // Direct uploads are applied immediately in AddUploadDirect, nothing to commit
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
