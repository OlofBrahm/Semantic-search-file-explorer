using System;
using System.Buffers;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using VectorDataBase.Models;
using VectorDataBase.Utils;

namespace VectorDataBase.Indices;

public sealed class HnswIndexV3
{
    private HnswNodeV3[] _nodes;
    private int _nodeCount;

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

    public HnswIndexV3(int initialCapacity = 65536)
    {
        _nodes = new HnswNodeV3[initialCapacity];
        _vectorPool = Array.Empty<float>();
        _neighborPool = Array.Empty<int>();
        _levelOffsetsPool = Array.Empty<int>();
        _levelCountsPool = Array.Empty<int>();
    }

    public List<int> GetOriginalDocumentIds(float[] queryEmbedding, int k)
    {
        var nodeIds = FindNearestUniqueDocs(queryEmbedding, k);
        var documentIds = new List<int>(nodeIds.Count);
        foreach (var nodeId in nodeIds)
        {
            if (nodeId >= 0 && nodeId < _nodeCount)
            {
                documentIds.Add(_nodes[nodeId].OriginalDocumentId);
            }
        }
        return documentIds;
    }

    public int Insert(float[] vector, int originalDocumentId, Random random)
    {
        if (vector.Length == 0) throw new ArgumentException("Vector cannot be empty.", nameof(vector));

        lock (_writeLock)
        {
            if (_vectorDim == 0) _vectorDim = vector.Length;
            else if (_vectorDim != vector.Length) throw new ArgumentException("Dimensionality mismatch.");

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
                return nodeId;
            }

            int entry = EntryPointId;
            entry = SearchTopLayers(node, entry);
            ConnectLayers(node, entry);
            UpdateMaxState(node);

            return nodeId;
        }
    }

    /// <summary>
    /// Searches for K unique documents. If a single document dominates the top results, 
    /// the search depth (ef) expands to find other files.
    /// </summary>
    public List<int> FindNearestUniqueDocs(float[] queryVector, int k, int? efSearch = null)
    {
        if (_nodeCount == 0) return new List<int>();

        float[] normalized = NormalizedCopy(queryVector);
        int entry = EntryPointId;

        // Navigate down to layer 0
        for (int lev = MaxLevel; lev >= 1; lev--)
        {
            List<int> candidates = SearchLayer(normalized, entry, lev, ef: 1);
            if (candidates.Count > 0) entry = candidates[0];
        }

        var uniqueResults = new List<int>(k);
        var seenDocs = new HashSet<int>();

        // Start with a standard ef, but allow it to grow if we don't find enough unique files
        int currentEf = efSearch ?? Math.Max(EfConstruction, k * 2);
        int maxAllowedEf = Math.Min(_nodeCount, 2048);

        currentEf = Math.Max(1, Math.Min(currentEf, maxAllowedEf));

        while (uniqueResults.Count < k && currentEf <= maxAllowedEf)
        {
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

            if (uniqueResults.Count >= k) break;
            currentEf *= 2; // Expand the net
        }

        return uniqueResults;
    }

    private int SearchTopLayers(HnswNodeV3 node, int entryId)
    {
        float[] vec = GetVector(node);
        for (int lev = MaxLevel; lev > node.Level; lev--)
        {
            List<int> candidates = SearchLayer(vec, entryId, lev, ef: 1);
            if (candidates.Count > 0) entryId = candidates[0];
        }
        return entryId;
    }

    private void ConnectLayers(HnswNodeV3 node, int entryId)
    {
        float[] vec = GetVector(node);
        for (int lev = Math.Min(node.Level, MaxLevel); lev >= 0; lev--)
        {
            // SearchLayer returns a List<int>
            List<int> candidates = SearchLayer(vec, entryId, lev, EfConstruction);

            int[] selectedArray = ArrayPool<int>.Shared.Rent(MaxNeighbours);
            try
            {
                var selected = selectedArray.AsSpan(0, MaxNeighbours);

                // USE CollectionsMarshal.AsSpan to pass the list efficiently
                ReadOnlySpan<int> candidateSpan = CollectionsMarshal.AsSpan(candidates);

                int selCount = SelectNeighbors(vec, candidateSpan, selected, node.OriginalDocumentId);

                for (int i = 0; i < selCount; i++)
                {
                    AddNeighbor(node.Id, lev, selected[i]);
                    AddNeighbor(selected[i], lev, node.Id, allowShrink: true);
                }
            }
            finally
            {
                ArrayPool<int>.Shared.Return(selectedArray);
            }

            if (candidates.Count > 0) entryId = candidates[0];
        }
    }

    /// <summary>
    /// Diversity-aware neighbor selection. Prevents a node from filling its 
    /// neighbor list exclusively with chunks from its own document.
    /// </summary>
    private int SelectNeighbors(float[] queryVector, ReadOnlySpan<int> candidates, Span<int> output, int sourceDocId)
    {
        int candidateCount = candidates.Length;
        if (candidateCount == 0) return 0;

        Span<(float dist, int id)> sorted = candidateCount <= 256
            ? stackalloc (float, int)[candidateCount]
            : new (float, int)[candidateCount];

        for (int i = 0; i < candidateCount; i++)
        {
            sorted[i] = (Distance(queryVector, candidates[i]), candidates[i]);
        }
        MemoryExtensions.Sort(sorted, static (a, b) => a.dist.CompareTo(b.dist));

        int selCount = 0;
        int sameDocLimit = Math.Max(1, MaxNeighbours / 4); // Only 25% can be from the same file

        foreach (var (dist, id) in sorted)
        {
            if (selCount >= output.Length) break;

            bool diverse = true;
            int sameDocCount = 0;
            int candDocId = _nodes[id].OriginalDocumentId;

            for (int j = 0; j < selCount; j++)
            {
                // HNSW Heuristic: Is this node closer to an existing neighbor than to the query?
                if (Distance(id, output[j]) < dist) { diverse = false; break; }

                // Diversity check: Count same-document neighbors already added
                if (_nodes[output[j]].OriginalDocumentId == candDocId) sameDocCount++;
            }

            // If it's a sibling chunk, check the quota
            if (candDocId == sourceDocId && sameDocCount >= sameDocLimit) diverse = false;

            if (diverse) output[selCount++] = id;
        }

        // Fill remaining slots if the diversity check was too strict
        if (selCount < output.Length)
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
        if (entryId < 0 || entryId >= _nodeCount) return new List<int>();

        int[] visitedGen = ArrayPool<int>.Shared.Rent(_nodeCount);
        int generation = Environment.TickCount;

        var candidateQueue = new PriorityQueue<int, float>();
        var bestResults = new PriorityQueue<int, float>();

        float entryDist = Distance(queryVector, entryId);
        candidateQueue.Enqueue(entryId, entryDist);
        bestResults.Enqueue(entryId, -entryDist);
        visitedGen[entryId] = generation;

        while (candidateQueue.Count > 0)
        {
            candidateQueue.TryPeek(out int currentId, out float currentDist);
            candidateQueue.Dequeue();

            bestResults.TryPeek(out _, out float worstDistNeg);
            if (currentDist > -worstDistNeg && bestResults.Count >= ef) break;

            var (neighborOffset, neighborCount) = GetNeighborBlock(currentId, layer);
            for (int i = 0; i < neighborCount; i++)
            {
                int neighborId = _neighborPool[neighborOffset + i];
                if (visitedGen[neighborId] == generation) continue;

                visitedGen[neighborId] = generation;
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

    // --- REMAINDER OF UTILITIES (AddNeighbor, Pool Mgmt, SIMD) ---
    private void AddNeighbor(int nodeId, int layer, int neighborId, bool allowShrink = false)
    {
        int levelIndex = _nodes[nodeId].NeighborOffset + layer;
        int neighborOffset = _levelOffsetsPool[levelIndex];
        int count = _levelCountsPool[levelIndex];

        if (count < MaxNeighbours)
        {
            _neighborPool[neighborOffset + count] = neighborId;
            _levelCountsPool[levelIndex] = count + 1;
        }
        else if (allowShrink) ShrinkConnections(nodeId, layer, neighborId);
    }

    private void ShrinkConnections(int nodeId, int layer, int newCandidateId)
    {
        int levelIndex = _nodes[nodeId].NeighborOffset + layer;
        int neighborOffset = _levelOffsetsPool[levelIndex];
        int count = _levelCountsPool[levelIndex];

        int total = count + 1;
        Span<int> candidates = total <= 128 ? stackalloc int[total] : new int[total];
        for (int i = 0; i < count; i++) candidates[i] = _neighborPool[neighborOffset + i];
        candidates[count] = newCandidateId;

        int[] selectedArray = ArrayPool<int>.Shared.Rent(MaxNeighbours);
        try
        {
            var selected = selectedArray.AsSpan(0, MaxNeighbours);
            int selCount = SelectNeighbors(GetVector(nodeId), candidates, selected, _nodes[nodeId].OriginalDocumentId);
            for (int i = 0; i < selCount; i++) _neighborPool[neighborOffset + i] = selected[i];
            _levelCountsPool[levelIndex] = selCount;
        }
        finally { ArrayPool<int>.Shared.Return(selectedArray); }
    }

    private void UpdateMaxState(HnswNodeV3 node)
    {
        if (node.Level > MaxLevel) { MaxLevel = node.Level; EntryPointId = node.Id; }
    }

    private int AllocateLevelMetadata(int levelCount)
    {
        int start = _levelPoolCount;
        int required = _levelPoolCount + levelCount;
        if (required > _levelOffsetsPool.Length)
        {
            int newSize = Math.Max(required, _levelOffsetsPool.Length * 2 + 128);
            Array.Resize(ref _levelOffsetsPool, newSize);
            Array.Resize(ref _levelCountsPool, newSize);
        }
        _levelPoolCount = required;
        return start;
    }

    private int AllocateNeighborBlock()
    {
        int start = _neighborPoolCount;
        int required = _neighborPoolCount + MaxNeighbours + 1;
        if (required > _neighborPool.Length)
        {
            Array.Resize(ref _neighborPool, Math.Max(required, _neighborPool.Length * 2 + 1024));
        }
        _neighborPoolCount = required;
        return start;
    }

    private int AppendVector(float[] vector)
    {
        float[] normalized = NormalizedCopy(vector);
        int start = _vectorPoolCount;
        if (_vectorPoolCount + _vectorDim > _vectorPool.Length)
        {
            Array.Resize(ref _vectorPool, Math.Max(_vectorPoolCount + _vectorDim, _vectorPool.Length * 2 + _vectorDim));
        }
        Array.Copy(normalized, 0, _vectorPool, start, _vectorDim);
        _vectorPoolCount += _vectorDim;
        return start;
    }

    private void EnsureNodeCapacity(int required)
    {
        if (required > _nodes.Length) Array.Resize(ref _nodes, _nodes.Length * 2);
    }

    private float[] GetVector(HnswNodeV3 node) => GetVector(node.Id);
    private float[] GetVector(int nodeId)
    {
        var v = new float[_vectorDim];
        Array.Copy(_vectorPool, _nodes[nodeId].VectorOffset, v, 0, _vectorDim);
        return v;
    }

    private (int offset, int count) GetNeighborBlock(int nodeId, int level)
    {
        int idx = _nodes[nodeId].NeighborOffset + level;
        return (_levelOffsetsPool[idx], _levelCountsPool[idx]);
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
        int i = 0;
        int step = Vector<float>.Count;
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