using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using VectorDataBase.Interfaces;
using VectorDataBase.Models;
using VectorDataBase.Utils;

namespace VectorDataBase.Indices;

/// <summary>
/// HNSW index optimized for large node counts.
/// </summary>
public sealed class HnswIndexV2 : IHnswIndex
{
    private HnswNode[] _nodes;
    private int _nodeCount;
    private readonly object _writeLock = new();

    private int[][][] _neighbors;
    private int[][] _neighborCounts;

    public int EntryPointId { get; private set; } = -1;
    public int MaxLevel { get; private set; } = -1;
    public int MaxNeighbours { get; init; }
    public int EfConstruction { get; init; }
    public float InverseLogM { get; init; }

    public int NodeCount => _nodeCount;

    public HnswIndexV2(int initialCapacity = 65536)
    {
        _nodes = new HnswNode[initialCapacity];
        _neighbors = new int[initialCapacity][][];
        _neighborCounts = new int[initialCapacity][];
    }

    public void Insert(HnswNode newNode, Random random)
    {
        NormalizeInPlace(newNode.Vector);

        lock (_writeLock)
        {
            EnsureCapacity(newNode.Id + 1);

            if (_nodeCount == 0)
            {
                InitializeFirstNode(newNode);
                return;
            }

            int level = InitializeNewNode(newNode, random);
            int entry = EntryPointId;

            entry = SearchTopLayers(newNode, level, entry);
            ConnectLayers(newNode, level, entry);
            UpdateMaxState(newNode);
        }
    }

    private void InitializeFirstNode(HnswNode node)
    {
        node.Level = 0;
        AllocNeighbors(node.Id, 0);
        _nodes[node.Id] = node;
        _nodeCount++;
        EntryPointId = node.Id;
        MaxLevel = 0;
    }

    private int InitializeNewNode(HnswNode node, Random random)
    {
        int level = HNSWUtils.GetRandomLevel(InverseLogM, random);
        node.Level = level;
        AllocNeighbors(node.Id, level);
        _nodes[node.Id] = node;
        _nodeCount++;
        return level;
    }

    private int SearchTopLayers(HnswNode node, int nodeLevel, int entryId)
    {
        for (int lev = MaxLevel; lev > nodeLevel; lev--)
        {
            if (lev < _neighbors[entryId].Length)
            {
                using var lease = SearchLayer(node.Vector, entryId, lev, ef: 1);
                if (lease.Count > 0)
                {
                    entryId = lease.Results[0];
                }
            }
        }
        return entryId;
    }

    private void ConnectLayers(HnswNode node, int nodeLevel, int entryId)
    {
        for (int lev = Math.Min(nodeLevel, MaxLevel); lev >= 0; lev--)
        {
            using var lease = SearchLayer(node.Vector, entryId, lev, EfConstruction);

            int selCount;
            if (MaxNeighbours <= 256)
            {
                Span<int> selected = stackalloc int[MaxNeighbours];
                selCount = SelectNeighbors(node.Vector, lease.Results.AsSpan(0, lease.Count), selected);

                ref int[] newNbrs = ref _neighbors[node.Id][lev];
                ref int newCnt = ref _neighborCounts[node.Id][lev];
                for (int i = 0; i < selCount; i++)
                {
                    newNbrs[newCnt++] = selected[i];
                }

                for (int i = 0; i < selCount; i++)
                {
                    int nbId = selected[i];
                    ref int[] nbArr = ref _neighbors[nbId][lev];
                    ref int nbCnt = ref _neighborCounts[nbId][lev];

                    if (nbCnt < MaxNeighbours)
                    {
                        nbArr[nbCnt++] = node.Id;
                    }
                    else
                    {
                        ShrinkConnections(nbId, lev, node.Id);
                    }
                }
            }
            else
            {
                int[] selectedArray = ArrayPool<int>.Shared.Rent(MaxNeighbours);
                try
                {
                    var selected = selectedArray.AsSpan(0, MaxNeighbours);
                    selCount = SelectNeighbors(node.Vector, lease.Results.AsSpan(0, lease.Count), selected);

                    ref int[] newNbrs = ref _neighbors[node.Id][lev];
                    ref int newCnt = ref _neighborCounts[node.Id][lev];
                    for (int i = 0; i < selCount; i++)
                    {
                        newNbrs[newCnt++] = selected[i];
                    }

                    for (int i = 0; i < selCount; i++)
                    {
                        int nbId = selected[i];
                        ref int[] nbArr = ref _neighbors[nbId][lev];
                        ref int nbCnt = ref _neighborCounts[nbId][lev];

                        if (nbCnt < MaxNeighbours)
                        {
                            nbArr[nbCnt++] = node.Id;
                        }
                        else
                        {
                            ShrinkConnections(nbId, lev, node.Id);
                        }
                    }
                }
                finally
                {
                    ArrayPool<int>.Shared.Return(selectedArray, clearArray: false);
                }
            }

            if (lease.Count > 0)
            {
                entryId = lease.Results[0];
            }
        }
    }

    private void UpdateMaxState(HnswNode node)
    {
        if (node.Level > MaxLevel)
        {
            MaxLevel = node.Level;
            EntryPointId = node.Id;
        }
    }

    public List<HnswNode> FindNearestNeighbors(float[] queryVector, int k, int? efSearch = null)
    {
        if (_nodeCount == 0)
        {
            return new List<HnswNode>();
        }

        float[] q = NormalizedCopy(queryVector);
        int ef = efSearch ?? EfConstruction;
        int entry = EntryPointId;

        for (int lev = MaxLevel; lev >= 1; lev--)
        {
            using var lease = SearchLayer(q, entry, lev, ef: 1);
            if (lease.Count > 0)
            {
                entry = lease.Results[0];
            }
        }

        using var final = SearchLayer(q, entry, 0, ef);

        var results = new (int id, float dist)[final.Count];
        for (int i = 0; i < final.Count; i++)
        {
            int id = final.Results[i];
            results[i] = (id, Distance(q, _nodes[id].Vector));
        }

        Array.Sort(results, (a, b) => a.dist.CompareTo(b.dist));

        int takeCount = Math.Min(k, results.Length);
        var list = new List<HnswNode>(takeCount);
        for (int i = 0; i < takeCount; i++)
        {
            list.Add(_nodes[results[i].id]);
        }

        return list;
    }

    private struct SearchLease : IDisposable
    {
        internal int[] Results;
        internal int Count;
        private int[] _candidateIds;
        private float[] _candidatePris;
        private int[] _bestIds;
        private float[] _bestPris;
        private int[] _visited;
        private bool _returnResults;

        internal static SearchLease Create(int candidateCapacity, int bestCapacity, int visitedCapacity)
        {
            var lease = new SearchLease
            {
                _candidateIds = ArrayPool<int>.Shared.Rent(candidateCapacity),
                _candidatePris = ArrayPool<float>.Shared.Rent(candidateCapacity),
                _bestIds = ArrayPool<int>.Shared.Rent(bestCapacity),
                _bestPris = ArrayPool<float>.Shared.Rent(bestCapacity),
                _visited = ArrayPool<int>.Shared.Rent(visitedCapacity),
                Results = Array.Empty<int>(),
                Count = 0,
                _returnResults = false
            };

            return lease;
        }

        internal void SetResults(int[] results, int count, bool returnResults)
        {
            Results = results;
            Count = count;
            _returnResults = returnResults;
        }

        internal PooledHeap CreateCandidateHeap() => new(_candidateIds, _candidatePris);

        internal PooledHeap CreateBestHeap() => new(_bestIds, _bestPris);

        internal int[] Visited => _visited;

        public void Dispose()
        {
            if (_candidateIds != null)
            {
                ArrayPool<int>.Shared.Return(_candidateIds, clearArray: false);
            }
            if (_candidatePris != null)
            {
                ArrayPool<float>.Shared.Return(_candidatePris, clearArray: false);
            }
            if (_bestIds != null)
            {
                ArrayPool<int>.Shared.Return(_bestIds, clearArray: false);
            }
            if (_bestPris != null)
            {
                ArrayPool<float>.Shared.Return(_bestPris, clearArray: false);
            }
            if (_visited != null)
            {
                ArrayPool<int>.Shared.Return(_visited, clearArray: false);
            }
            if (_returnResults && Results.Length > 0)
            {
                ArrayPool<int>.Shared.Return(Results, clearArray: false);
            }
        }
    }

    private sealed class PooledHeap
    {
        private readonly int[] _ids;
        private readonly float[] _pri;
        public int Count { get; private set; }

        public PooledHeap(int[] ids, float[] pri)
        {
            _ids = ids;
            _pri = pri;
        }

        public void Push(int id, float p)
        {
            if (Count >= _ids.Length)
            {
                return;
            }

            int i = Count++;
            _ids[i] = id;
            _pri[i] = p;
            SiftUp(i);
        }

        public (int id, float pri) Pop()
        {
            var top = (_ids[0], _pri[0]);
            int last = --Count;
            _ids[0] = _ids[last];
            _pri[0] = _pri[last];
            SiftDown(0);
            return top;
        }

        public (int id, float pri) Peek() => (_ids[0], _pri[0]);

        public void Reset() => Count = 0;

        private void SiftUp(int i)
        {
            while (i > 0)
            {
                int p = (i - 1) >> 1;
                if (_pri[p] <= _pri[i])
                {
                    break;
                }
                Swap(i, p);
                i = p;
            }
        }

        private void SiftDown(int i)
        {
            while (true)
            {
                int l = (i << 1) + 1;
                int r = l + 1;
                int s = i;
                if (l < Count && _pri[l] < _pri[s])
                {
                    s = l;
                }
                if (r < Count && _pri[r] < _pri[s])
                {
                    s = r;
                }
                if (s == i)
                {
                    break;
                }
                Swap(i, s);
                i = s;
            }
        }

        private void Swap(int a, int b)
        {
            (_ids[a], _ids[b]) = (_ids[b], _ids[a]);
            (_pri[a], _pri[b]) = (_pri[b], _pri[a]);
        }
    }

    private SearchLease SearchLayer(float[] q, int entryId, int layer, int ef)
    {
        ef = Math.Max(1, ef);

        int visitedSize = _nodeCount;
        int bufSize = Math.Max(ef * 4, 256);
        int bestSize = ef + 1;

        var lease = SearchLease.Create(bufSize, bestSize, visitedSize);
        var candidates = lease.CreateCandidateHeap();
        var best = lease.CreateBestHeap();
        var visitedGen = lease.Visited;
        int gen = Environment.TickCount;

        if (entryId >= visitedSize || _nodes[entryId] == null)
        {
            lease.SetResults(Array.Empty<int>(), 0, returnResults: false);
            return lease;
        }

        float entryDist = Distance(q, _nodes[entryId].Vector);
        candidates.Push(entryId, entryDist);
        best.Push(entryId, -entryDist);
        visitedGen[entryId] = gen;

        while (candidates.Count > 0)
        {
            var (curId, curDist) = candidates.Pop();
            float worstDist = -best.Peek().pri;

            if (curDist > worstDist && best.Count >= ef)
            {
                break;
            }

            int[][] nodeNbrs = _neighbors[curId];
            if (layer >= nodeNbrs.Length)
            {
                continue;
            }

            int[] nbArr = nodeNbrs[layer];
            int nbCnt = _neighborCounts[curId][layer];

            for (int ni = 0; ni < nbCnt; ni++)
            {
                int nbId = nbArr[ni];
                if (nbId >= visitedSize)
                {
                    continue;
                }
                if (visitedGen[nbId] == gen)
                {
                    continue;
                }
                visitedGen[nbId] = gen;

                float nbDist = Distance(q, _nodes[nbId].Vector);
                if (best.Count < ef || nbDist < worstDist)
                {
                    candidates.Push(nbId, nbDist);
                    best.Push(nbId, -nbDist);

                    if (best.Count > ef)
                    {
                        best.Pop();
                    }

                    worstDist = -best.Peek().pri;
                }
            }
        }

        int count = best.Count;
        int[] resultBuf = ArrayPool<int>.Shared.Rent(count);
        for (int i = count - 1; i >= 0; i--)
        {
            resultBuf[i] = best.Pop().id;
        }

        lease.SetResults(resultBuf, count, returnResults: true);
        return lease;
    }

    public int SelectNeighbors(float[] queryVec, ReadOnlySpan<int> candidates, Span<int> output)
    {
        int candidateCount = candidates.Length;
        Span<(float dist, int id)> sorted = candidateCount <= 256
            ? stackalloc (float, int)[candidateCount]
            : new (float, int)[candidateCount];

        for (int i = 0; i < candidateCount; i++)
        {
            int id = candidates[i];
            sorted[i] = (Distance(queryVec, _nodes[id].Vector), id);
        }

        MemoryExtensions.Sort(sorted, static (a, b) => a.dist.CompareTo(b.dist));

        int selCount = 0;
        foreach (var (dist, id) in sorted)
        {
            if (selCount >= output.Length)
            {
                break;
            }

            bool diverse = true;
            for (int j = 0; j < selCount; j++)
            {
                if (Distance(_nodes[id].Vector, _nodes[output[j]].Vector) < dist)
                {
                    diverse = false;
                    break;
                }
            }

            if (diverse)
            {
                output[selCount++] = id;
            }
        }

        if (selCount < output.Length)
        {
            var selectedSet = new HashSet<int>(selCount);
            for (int i = 0; i < selCount; i++)
            {
                selectedSet.Add(output[i]);
            }

            foreach (var (_, id) in sorted)
            {
                if (selCount >= output.Length)
                {
                    break;
                }
                if (selectedSet.Add(id))
                {
                    output[selCount++] = id;
                }
            }
        }

        return selCount;
    }

    private void ShrinkConnections(int nodeId, int layer, int newCandidateId)
    {
        int[] nbArr = _neighbors[nodeId][layer];
        int nbCnt = _neighborCounts[nodeId][layer];
        float[] qVec = _nodes[nodeId].Vector;

        int totalCandidates = nbCnt + 1;
        Span<int> allCands = totalCandidates <= 128
            ? stackalloc int[totalCandidates]
            : new int[totalCandidates];

        nbArr.AsSpan(0, nbCnt).CopyTo(allCands);
        allCands[nbCnt] = newCandidateId;

        Span<int> selected = stackalloc int[MaxNeighbours];
        int selCount = SelectNeighbors(qVec, allCands, selected);

        for (int i = 0; i < selCount; i++)
        {
            nbArr[i] = selected[i];
        }
        _neighborCounts[nodeId][layer] = selCount;
    }

   

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Distance(float[] a, float[] b) => 1f - DotProductSIMD(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float DotProductSIMD(float[] a, float[] b)
    {
        int len = a.Length;
        int step = Vector<float>.Count;
        var acc = Vector<float>.Zero;
        int i;

        for (i = 0; i <= len - step; i += step)
        {
            acc += new Vector<float>(a, i) * new Vector<float>(b, i);
        }

        float dot = Vector.Dot(acc, Vector<float>.One);
        for (; i < len; i++)
        {
            dot += a[i] * b[i];
        }

        return dot;
    }

    private static void NormalizeInPlace(float[] v)
    {
        float mag = MathF.Sqrt(DotProductSIMD(v, v));
        if (mag < 1e-9f)
        {
            return;
        }
        float inv = 1f / mag;
        for (int i = 0; i < v.Length; i++)
        {
            v[i] *= inv;
        }
    }

    private static float[] NormalizedCopy(float[] v)
    {
        float[] c = (float[])v.Clone();
        NormalizeInPlace(c);
        return c;
    }

    private void AllocNeighbors(int nodeId, int level)
    {
        var nbrs = new int[level + 1][];
        var cnts = new int[level + 1];
        for (int l = 0; l <= level; l++)
        {
            nbrs[l] = new int[MaxNeighbours + 1];
        }
        _neighbors[nodeId] = nbrs;
        _neighborCounts[nodeId] = cnts;
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _nodes.Length)
        {
            return;
        }
        int newSize = Math.Max(required, _nodes.Length * 2);
        Array.Resize(ref _nodes, newSize);
        Array.Resize(ref _neighbors, newSize);
        Array.Resize(ref _neighborCounts, newSize);
    }

    Dictionary<int, HnswNode> IHnswIndex.Nodes
    {
        get => throw new NotSupportedException("Use flat array access for HnswIndexV2.");
        set => throw new NotSupportedException("Use flat array access for HnswIndexV2.");
    }
}
