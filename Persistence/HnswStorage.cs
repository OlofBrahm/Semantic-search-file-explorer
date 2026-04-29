using System;
using System.Collections.Generic;
using System.Text;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using VectorDataBase.Models;
using SimiliVec_Explorer.DocumentStorer;
using VectorDataBase.Indices;

namespace VectorDataBase.Persistence
{
    public class HnswStorage : IDisposable
    {
        private MemoryMappedFile _mmf;
        private MemoryMappedViewAccessor _headerAccessor;
        private MemoryMappedViewAccessor _nodeAccessor;
        private MemoryMappedViewAccessor _vectorAccessor;
        private MemoryMappedViewAccessor _neighborAccessor;
        private MemoryMappedViewAccessor _levelOffsetsAccessor;
        private MemoryMappedViewAccessor _levelCountsAccessor;


        private readonly long _nodeOffset;
        private readonly long _vectorOffset;
        private readonly long _nodeSize = Marshal.SizeOf<HnswNodeV3>();
        private readonly int _vectorSize;
        private readonly long _neighborOffset;
        private readonly long _levelOffsetsOffset;
        private readonly long _levelCountsOffset;

        public HnswStorage(string filePath, int maxNodes, int dimensions)
        {
            _vectorSize = dimensions * sizeof(float);
            // Calculate offsets
            _nodeOffset = 1024;
            _vectorOffset = _nodeOffset + ((long)maxNodes * _nodeSize);
            _neighborOffset = _vectorOffset + ((long)maxNodes * _vectorSize);
            _levelOffsetsOffset = _neighborOffset + ((long)maxNodes * 16 * sizeof(int));
            _levelCountsOffset = _levelOffsetsOffset + ((long)maxNodes * sizeof(int));
            long totalFileSize = _levelCountsOffset + ((long)maxNodes * sizeof(int));

            _mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.OpenOrCreate, "HnswMapping", totalFileSize);

            _headerAccessor = _mmf.CreateViewAccessor(0, _nodeOffset);
            _nodeAccessor = _mmf.CreateViewAccessor(_nodeOffset, (long)maxNodes * _nodeSize);
            _vectorAccessor = _mmf.CreateViewAccessor(_vectorOffset, (long)maxNodes * _vectorSize);
            _neighborAccessor = _mmf.CreateViewAccessor(_neighborOffset, (long)maxNodes * 16 * sizeof(int));
            _levelOffsetsAccessor = _mmf.CreateViewAccessor(_levelOffsetsOffset, (long)maxNodes * sizeof(int));
            _levelCountsAccessor = _mmf.CreateViewAccessor(_levelCountsOffset, (long)maxNodes * sizeof(int));
        }

        // Save/load neighbor pool
        public void SaveNeighborPool(int[] neighborPool, int count)
        {
            _neighborAccessor.WriteArray(0, neighborPool, 0, count);
        }
        public void LoadNeighborPool(int[] neighborPool, int count)
        {
            _neighborAccessor.ReadArray(0, neighborPool, 0, count);
        }

        // Save/load level offsets
        public void SaveLevelOffsets(int[] levelOffsets, int count)
        {
            _levelOffsetsAccessor.WriteArray(0, levelOffsets, 0, count);
        }
        public void LoadLevelOffsets(int[] levelOffsets, int count)
        {
            _levelOffsetsAccessor.ReadArray(0, levelOffsets, 0, count);
        }

        // Save/load level counts
        public void SaveLevelCounts(int[] levelCounts, int count)
        {
            _levelCountsAccessor.WriteArray(0, levelCounts, 0, count);
        }
        public void LoadLevelCounts(int[] levelCounts, int count)
        {
            _levelCountsAccessor.ReadArray(0, levelCounts, 0, count);
        }

        public (int maxNodes, int dimensions) GetheaderInfo()
        {
            int maxNodes = _headerAccessor.ReadInt32(0);
            int dimensions = _headerAccessor.ReadInt32(sizeof(int));
            return (maxNodes, dimensions);
        }

        public void WriteHeader(HnswHeader header)
        {
            int size = Marshal.SizeOf<HnswHeader>();
            byte[] headerBytes = new byte[size];
            GCHandle handle = GCHandle.Alloc(headerBytes, GCHandleType.Pinned);
            try
            {
                Marshal.StructureToPtr(header, handle.AddrOfPinnedObject(), false);
                _headerAccessor.WriteArray(0, headerBytes, 0, size);
            }
            finally
            {
                handle.Free();
            }
        }



        public HnswHeader ReadHeader()
        {
            int size = Marshal.SizeOf<HnswHeader>();
            byte[] headerBytes = new byte[size];
            _headerAccessor.ReadArray(0, headerBytes, 0, size);
            GCHandle handle = GCHandle.Alloc(headerBytes, GCHandleType.Pinned);
            try
            {
                return Marshal.PtrToStructure<HnswHeader>(handle.AddrOfPinnedObject());
            }
            finally
            {
                handle.Free();
            }
        }

        /// <summary>
        /// Saves a single HnswNode to the memory-mapped file at the specified index.
        /// </summary>
        /// <param name="index"></param>
        /// <param name="node"></param>
        public void SaveNode(int index, HnswNodeV3 node)
        {
            long pos = (long)index * _nodeSize;
            _nodeAccessor.Write(pos, ref node);
        }

        /// <summary>
        /// Saves the vector to the memory-mapped file at the specified index.
        /// </summary>
        /// <param name="index"></param>
        /// <param name="vector"></param>
        public void SaveVector(int index, float[] vector)
        {
            long pos = _vectorOffset + ((long)index * _vectorSize);
            using var vectorAccessor = _mmf.CreateViewAccessor(pos, _vectorSize);
            vectorAccessor.WriteArray(0, vector, 0, vector.Length);
        }

        /// <summary>
        /// Forces the os to physically write the RAM buffer to disk.
        /// </summary>
        public void Commit()
        {
            _headerAccessor.Flush();
            _nodeAccessor.Flush();
        }

        public HnswNodeV3 GetNode(int index)
        {
            long pos = (long)index * _nodeSize;
            _nodeAccessor.Read(pos, out HnswNodeV3 node);
            return node;
        }

        public unsafe ReadOnlySpan<float> GetVectorSpan(int nodeId)
        {
            long offset = _vectorOffset + ((long)nodeId * _vectorSize);
            byte* ptr = null;
            _vectorAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
            return new ReadOnlySpan<float>(ptr + offset, 384);
        }


        public void Dispose()
        {
            _headerAccessor?.Dispose();
            _nodeAccessor?.Dispose();
            _vectorAccessor?.Dispose();
            _neighborAccessor?.Dispose();
            _levelOffsetsAccessor?.Dispose();
            _levelCountsAccessor?.Dispose();
            _mmf?.Dispose();
        }

    }
}
