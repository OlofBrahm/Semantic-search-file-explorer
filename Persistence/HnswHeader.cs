using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace VectorDataBase.Persistence
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct HnswHeader
    {
        public uint MagicNumber;
        public int Version;
        public int TotalNodes;
        public int VectorDimension;
        public int EntryPointId;
    }
}
