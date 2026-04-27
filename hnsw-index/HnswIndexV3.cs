using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using VectorDataBase.Models;
using VectorDataBase.Persistence;
using VectorDataBase.Utils;

namespace VectorDataBase.Indices;

public sealed class HnswIndexV3
{
    private HnswNodeV3[] _nodes;
    private int _nodeCount;
    private readonly HnswStorage _storage;

    private float[] _vectorPool;
    private int _vectorPoolCount;
    private int _vectorDim;

    private int[] _neighborPool;
    private int _neighborPoolCount;

    private int[] _levelOffsetsPool;
    private int[] _levelCountsPool;
    private int _levelPoolCount;


    private readonly object _writeLock = new();

    public int EntryPointId { get; private set; } = -1;
    public int MaxLevel { get; private set; } = -1;
    public int MaxNeighbours { get; init; }
    public int EfConstruction { get; init; }
    public float InverseLogM { get; init; }

    public int NodeCount => _nodeCount;

    public HnswIndexV3(HnswStorage storage, int initialCapacity = 65536, bool loadFromStorage = false)
    {
        _storage = storage;
        if (loadFromStorage)
        {
            var header = _storage.ReadHeader();
            if (header.MagicNumber != unchecked((int)0xDEADBEEF))
                throw new InvalidDataException("Invalid or corrupt HNSW index file.");
            if (header.Version != 1)
                throw new NotSupportedException("Unsupported index version.");

            _nodes = new HnswNodeV3[header.TotalNodes];
            _vectorPool = new float[header.TotalNodes * header.VectorDimension];
            _neighborPool = new int[header.TotalNodes * MaxNeighbours];
            _levelOffsetsPool = new int[header.TotalNodes];
            _levelCountsPool = new int[header.TotalNodes];
        }
        else
        {
            _nodes = new HnswNodeV3[initialCapacity];
            _vectorPool = new float[initialCapacity * 128];
            _neighborPool = new int[initialCapacity * 16];
            _levelOffsetsPool = new int[initialCapacity];
            _levelCountsPool = new int[initialCapacity];
        }
    }

    public List<int> GetOriginalDocumentIds(float[] queryEmbedding, int k)
    {
        // Fix: Added null/empty check
        if (queryEmbedding == null || queryEmbedding.Length == 0) return new List<int>();

        var nodeIds = FindNearestUniqueDocs(queryEmbedding, k);
        var documentIds = new List<int>(nodeIds.Count);

        foreach (var nodeId in nodeIds)
        {
            documentIds.Add(_nodes[nodeId].OriginalDocumentId);
        }
        return documentIds;
    }

    public int Insert(float[] vector, int originalDocumentId, Random random)
    {
        if (vector == null || vector.Length == 0) throw new ArgumentException("Vector empty.");

        lock (_writeLock)
        {
            if (_vectorDim == 0) _vectorDim = vector.Length;

            int nodeId = _nodeCount;
            EnsureNodeCapacity(nodeId + 1);

            int level = HNSWUtils.GetRandomLevel(InverseLogM, random);
            int vectorOffset = AppendVector(vector);
            int levelOffset = AllocateLevelMetadata(level + 1);

            for (int l = 0; l <= level; l++)
            {
                int neighborOffset = AllocateNeighborBlock();
                _levelOffsetsPool[levelOffset + l] = neighborOffset;
                _levelCountsPool[levelOffset + l] = 0;
            }

            var node = new HnswNodeV3
            {
                Id = nodeId,
                OriginalDocumentId = originalDocumentId,
                Level = level,
                VectorOffset = vectorOffset,
                NeighborOffset = levelOffset
            };

            _nodes[nodeId] = node;
            _nodeCount++;

            if (_nodeCount == 1)
            {
                EntryPointId = nodeId;
                MaxLevel = level;
            }
            else
            {
                int currEntryPoint = EntryPointId;
                // Fix: Pass the actual vector to avoid redundant GetVector calls inside search
                float[] normalized = NormalizedCopy(vector);

                currEntryPoint = SearchTopLayers(normalized, currEntryPoint, level);
                ConnectLayers(node, normalized, currEntryPoint);
                UpdateMaxState(node);
            }

            _storage?.SaveNode(nodeId, node);
            _storage?.SaveVector(nodeId, vector);
            _storage.WriteHeader(new HnswHeader
            {
                MagicNumber = 0xDEADBEEF,
                Version = 1,
                TotalNodes = _nodeCount,
                VectorDimension = _vectorDim,
                EntryPointId = EntryPointId
            });
            _storage?.Commit();
            return nodeId;
        }
    }

    public List<int> FindNearestUniqueDocs(float[] queryVector, int k, int? efSearch = null)
    {
        // Thread-safe snapshots of entry state
        int entry = EntryPointId;
        int maxLvl = MaxLevel;
        int count = _nodeCount;

        if (count == 0 || entry == -1) return new List<int>();

        float[] normalized = NormalizedCopy(queryVector);

        // Fix: Downward traversal ensures 'entry' is always the best found so far
        for (int lev = maxLvl; lev >= 1; lev--)
        {
            List<int> candidates = SearchLayer(normalized, entry, lev, ef: 1);
            if (candidates.Count > 0) entry = candidates[0];
        }

        var uniqueResults = new List<int>(k);
        var seenDocs = new HashSet<int>();
        int currentEf = efSearch ?? Math.Max(EfConstruction, k);
        int maxAllowedEf = Math.Min(count, 2048);

        // Final Search at Level 0
        List<int> finalCandidates = SearchLayer(normalized, entry, 0, currentEf);

        foreach (var nodeId in finalCandidates)
        {
            int docId = _nodes[nodeId].OriginalDocumentId;
            if (seenDocs.Add(docId))
            {
                uniqueResults.Add(nodeId);
            }
            if (uniqueResults.Count >= k) break;
        }

        return uniqueResults;
    }

    private int SearchTopLayers(float[] queryVec, int entryId, int targetLevel)
    {
        // Fix: Use the snapshot of MaxLevel
        for (int lev = MaxLevel; lev > targetLevel; lev--)
        {
            List<int> candidates = SearchLayer(queryVec, entryId, lev, ef: 1);
            if (candidates.Count > 0) entryId = candidates[0];
        }
        return entryId;
    }

    private void ConnectLayers(HnswNodeV3 node, float[] queryVec, int entryId)
    {
        for (int lev = Math.Min(node.Level, MaxLevel); lev >= 0; lev--)
        {
            List<int> candidates = SearchLayer(queryVec, entryId, lev, EfConstruction);

            int[] selectedArray = ArrayPool<int>.Shared.Rent(MaxNeighbours);
            try
            {
                var selected = selectedArray.AsSpan(0, MaxNeighbours);
                int selCount = SelectNeighbors(queryVec, CollectionsMarshal.AsSpan(candidates), selected, node.OriginalDocumentId);

                for (int i = 0; i < selCount; i++)
                {
                    int neighborId = selected[i];
                    AddNeighbor(node.Id, lev, neighborId);
                    AddNeighbor(neighborId, lev, node.Id, allowShrink: true);
                }
            }
            finally { ArrayPool<int>.Shared.Return(selectedArray); }

            if (candidates.Count > 0) entryId = candidates[0];
        }
    }

    private int SelectNeighbors(float[] queryVector, ReadOnlySpan<int> candidates, Span<int> output, int sourceDocId)
    {
        int candidateCount = candidates.Length;
        if (candidateCount == 0) return 0;

        Span<(float dist, int id)> sorted = candidateCount <= 256 ? stackalloc (float, int)[candidateCount] : new (float, int)[candidateCount];
        for (int i = 0; i < candidateCount; i++)
            sorted[i] = (Distance(queryVector, candidates[i]), candidates[i]);

        MemoryExtensions.Sort(sorted, static (a, b) => a.dist.CompareTo(b.dist));

        int selCount = 0;
        // Heuristic: allow some same-doc neighbors to maintain connectivity, but prioritize diversity
        int sameDocLimit = Math.Max(1, MaxNeighbours / 4);

        foreach (var (dist, id) in sorted)
        {
            if (selCount >= output.Length) break;

            bool diverse = true;
            int sameDocCount = 0;
            int candDocId = _nodes[id].OriginalDocumentId;

            for (int j = 0; j < selCount; j++)
            {
                if (Distance(id, output[j]) < dist) { diverse = false; break; }
                if (_nodes[output[j]].OriginalDocumentId == candDocId) sameDocCount++;
            }

            if (candDocId == sourceDocId && sameDocCount >= sameDocLimit) diverse = false;

            if (diverse) output[selCount++] = id;
        }

        // Fallback: fill remaining slots with closest candidates if diversity pruning was too aggressive
        if (selCount < output.Length && selCount < candidateCount)
        {
            foreach (var (_, id) in sorted)
            {
                if (selCount >= output.Length) break;
                bool exists = false;
                for (int i = 0; i < selCount; i++) if (output[i] == id) { exists = true; break; }
                if (!exists) output[selCount++] = id;
            }
        }
        return selCount;
    }

    private List<int> SearchLayer(float[] queryVector, int entryId, int layer, int ef)
    {
        ef = Math.Max(1, ef);
        int currentCount = _nodeCount;
        if (entryId < 0 || entryId >= currentCount) return new List<int>();

        // Visited set using a simple generation check to avoid clearing the whole array
        int[] visitedGen = ArrayPool<int>.Shared.Rent(currentCount);
        Array.Clear(visitedGen, 0, currentCount);

        var candidateQueue = new PriorityQueue<int, float>(); // Min-heap (closest first)
        var bestResults = new PriorityQueue<int, float>();    // Max-heap (furthest of the best first)

        float entryDist = Distance(queryVector, entryId);
        candidateQueue.Enqueue(entryId, entryDist);
        bestResults.Enqueue(entryId, -entryDist);
        visitedGen[entryId] = 1;

        while (candidateQueue.TryDequeue(out int currentId, out float currentDist))
        {
            bestResults.TryPeek(out _, out float worstDistNeg);
            if (currentDist > -worstDistNeg && bestResults.Count >= ef) break;

            var (neighborOffset, neighborCount) = GetNeighborBlock(currentId, layer);
            for (int i = 0; i < neighborCount; i++)
            {
                int neighborId = _neighborPool[neighborOffset + i];
                if (neighborId < 0 || neighborId >= currentCount || visitedGen[neighborId] == 1) continue;

                visitedGen[neighborId] = 1;
                float neighborDist = Distance(queryVector, neighborId);

                if (bestResults.Count < ef || neighborDist < -worstDistNeg)
                {
                    candidateQueue.Enqueue(neighborId, neighborDist);
                    bestResults.Enqueue(neighborId, -neighborDist);
                    if (bestResults.Count > ef) bestResults.Dequeue();
                }
            }
        }

        var results = new List<int>(bestResults.Count);
        while (bestResults.Count > 0) results.Add(bestResults.Dequeue());
        results.Reverse();

        ArrayPool<int>.Shared.Return(visitedGen);
        return results;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float Distance(float[] queryVector, int nodeId) => 1f - DotProductSIMD(queryVector, nodeId);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float Distance(int nodeIdA, int nodeIdB) => 1f - DotProductSIMD(nodeIdA, nodeIdB);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float DotProductSIMD(float[] q, int nodeId)
    {
        int offset = _nodes[nodeId].VectorOffset;
        var acc = Vector<float>.Zero;
        int i = 0, step = Vector<float>.Count;
        for (; i <= _vectorDim - step; i += step)
            acc += new Vector<float>(q, i) * new Vector<float>(_vectorPool, offset + i);
        float dot = Vector.Dot(acc, Vector<float>.One);
        for (; i < _vectorDim; i++) dot += q[i] * _vectorPool[offset + i];
        return dot;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float DotProductSIMD(int a, int b)
    {
        int offA = _nodes[a].VectorOffset;
        int offB = _nodes[b].VectorOffset;
        var acc = Vector<float>.Zero;
        int i = 0, step = Vector<float>.Count;
        for (; i <= _vectorDim - step; i += step)
            acc += new Vector<float>(_vectorPool, offA + i) * new Vector<float>(_vectorPool, offB + i);
        float dot = Vector.Dot(acc, Vector<float>.One);
        for (; i < _vectorDim; i++) dot += _vectorPool[offA + i] * _vectorPool[offB + i];
        return dot;
    }

    private int AppendVector(float[] vector)
    {
        float[] normalized = NormalizedCopy(vector);
        int start = _vectorPoolCount;
        if (_vectorPoolCount + _vectorDim > _vectorPool.Length)
            Array.Resize(ref _vectorPool, Math.Max(_vectorPoolCount + _vectorDim, _vectorPool.Length * 2));
        Array.Copy(normalized, 0, _vectorPool, start, _vectorDim);
        _vectorPoolCount += _vectorDim;
        return start;
    }

    private float[] GetVector(int nodeId)
    {
        var v = new float[_vectorDim];
        Array.Copy(_vectorPool, _nodes[nodeId].VectorOffset, v, 0, _vectorDim);
        return v;
    }

    private void EnsureNodeCapacity(int required)
    {
        if (required > _nodes.Length) Array.Resize(ref _nodes, _nodes.Length * 2);
    }

    private void AddNeighbor(int nodeId, int layer, int neighborId, bool allowShrink = false)
    {
        int levelIndex = _nodes[nodeId].NeighborOffset + layer;
        int offset = _levelOffsetsPool[levelIndex];
        int count = _levelCountsPool[levelIndex];

        // Fix: Prevent self-looping
        if (nodeId == neighborId) return;

        if (count < MaxNeighbours)
        {
            // Check for duplicates before adding
            for (int i = 0; i < count; i++) if (_neighborPool[offset + i] == neighborId) return;

            _neighborPool[offset + count] = neighborId;
            _levelCountsPool[levelIndex] = count + 1;
        }
        else if (allowShrink)
        {
            ShrinkConnections(nodeId, layer, neighborId);
        }
    }

    private void ShrinkConnections(int nodeId, int layer, int newCandidateId)
    {
        int levelIndex = _nodes[nodeId].NeighborOffset + layer;
        int offset = _levelOffsetsPool[levelIndex];
        int count = _levelCountsPool[levelIndex];

        // Fix: Check if candidate already exists in the full block
        for (int i = 0; i < count; i++) if (_neighborPool[offset + i] == newCandidateId) return;

        Span<int> candidates = stackalloc int[count + 1];
        for (int i = 0; i < count; i++) candidates[i] = _neighborPool[offset + i];
        candidates[count] = newCandidateId;

        int[] selectedArray = ArrayPool<int>.Shared.Rent(MaxNeighbours);
        try
        {
            var selected = selectedArray.AsSpan(0, MaxNeighbours);
            int selCount = SelectNeighbors(GetVector(nodeId), candidates, selected, _nodes[nodeId].OriginalDocumentId);
            for (int i = 0; i < selCount; i++) _neighborPool[offset + i] = selected[i];
            _levelCountsPool[levelIndex] = selCount;
        }
        finally { ArrayPool<int>.Shared.Return(selectedArray); }
    }

    private int AllocateLevelMetadata(int levelCount)
    {
        int start = _levelPoolCount;
        _levelPoolCount += levelCount;
        if (_levelPoolCount > _levelOffsetsPool.Length)
        {
            Array.Resize(ref _levelOffsetsPool, _levelPoolCount * 2);
            Array.Resize(ref _levelCountsPool, _levelPoolCount * 2);
        }
        return start;
    }

    private int AllocateNeighborBlock()
    {
        int start = _neighborPoolCount;
        _neighborPoolCount += MaxNeighbours;
        if (_neighborPoolCount > _neighborPool.Length)
            Array.Resize(ref _neighborPool, _neighborPoolCount * 2);
        return start;
    }

    private (int offset, int count) GetNeighborBlock(int nodeId, int level)
    {
        int idx = _nodes[nodeId].NeighborOffset + level;
        return (_levelOffsetsPool[idx], _levelCountsPool[idx]);
    }

    private void UpdateMaxState(HnswNodeV3 node)
    {
        if (node.Level > MaxLevel)
        {
            MaxLevel = node.Level;
            EntryPointId = node.Id;
        }
    }

    private static float[] NormalizedCopy(float[] v) { float[] c = (float[])v.Clone(); NormalizeInPlace(c); return c; }
    private static void NormalizeInPlace(float[] v)
    {
        float sq = 0; for (int i = 0; i < v.Length; i++) sq += v[i] * v[i];
        float mag = MathF.Sqrt(sq);
        if (mag < 1e-9f) return;
        float inv = 1f / mag;
        for (int i = 0; i < v.Length; i++) v[i] *= inv;
    }
}