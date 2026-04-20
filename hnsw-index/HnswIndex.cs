using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VectorDataBase.Interfaces;
using VectorDataBase.Models;
using VectorDataBase.Utils;

// Change to slightly different namespace to avoid class-namespace name collisions
namespace VectorDataBase.Indices;

/// <summary>
/// Class to hold indexed vectors, now relying on HSNWUtils for distance and level calculation.
/// </summary>
[Obsolete("Legacy version. Use HnswIndexV2.")]
public class HnswIndex : IHnswIndex
{
    public Dictionary<int, HnswNode> Nodes { get; set; } = new Dictionary<int, HnswNode>();

    public int EntryPointId { get; private set; } //id of the entry point node
    public int MaxLevel { get; private set; } //highest level in the graph
    public int MaxNeighbours { get; init; } //max number of neighbors per node
    public int EfConstruction { get; init; } //size of candidate list during construction
    public float InverseLogM { get; init; } //Controls the probability of a node being assigned to higher levels

    /// <summary>
    /// Insert a new node into the HSNW index
    /// </summary>
    /// <param name="newNode"></param>
    /// <param name="random"></param>
    public void Insert(HnswNode newNode, Random random)
    {
        if (Nodes.Count == 0)
        {
            InitializeFirstNode(newNode);
            return;
        }

        // Initialization: Set level and prepare structure
        int newNodeLevel = InitializeNewNode(newNode, random);
        int currentEntryId = EntryPointId;

        // Search Top Layers: Find the closest node in layers above the new node's level
        currentEntryId = SearchTopLayers(newNode, newNodeLevel, currentEntryId);

        // Layer Connection: Connect the new node in all relevant layers
        ConnectLayers(newNode, newNodeLevel, currentEntryId);

        // Update Global State
        UpdateMaxState(newNode);
    }

    /// <summary>
    /// Initialize the first node in the index
    /// </summary>
    /// <param name="newNode"></param>
    private void InitializeFirstNode(HnswNode newNode)
    {
        newNode.Level = 0;
        newNode.Neighbors = new List<int>[1];
        newNode.Neighbors[0] = new List<int>(MaxNeighbours);
        Nodes.Add(newNode.Id, newNode);
        EntryPointId = newNode.Id;
        MaxLevel = 0;
    }

    /// <summary>
    /// Initialize new node: assign level and prepare neighbor lists
    /// </summary>
    /// <param name="newNode"></param>
    /// <param name="random"></param>
    /// <returns></returns>
    private int InitializeNewNode(HnswNode newNode, Random random)
    {
        // Calculate and assign level
        int newNodeLevel = HNSWUtils.GetRandomLevel(InverseLogM, random);
        newNode.Level = newNodeLevel;

        // Initialize neighbor lists for each level up to the new node's level
        newNode.Neighbors = new List<int>[newNodeLevel + 1];
        for (int level = 0; level <= newNodeLevel; level++)
        {
            newNode.Neighbors[level] = new List<int>(MaxNeighbours);
        }
        Nodes.Add(newNode.Id, newNode);
        return newNodeLevel;
    }

    /// <summary>
    /// Search from the top layers down to the new node's level to find the closest entry point
    /// </summary>
    /// <param name="newNode"></param>
    /// <param name="newNodeLevel"></param>
    /// <param name="currentEntryId"></param>
    /// <returns></returns>
    private int SearchTopLayers(HnswNode newNode, int newNodeLevel, int currentEntryId)
    {
        // Search from MaxLevel down to newNodeLevel + 1
        for (int level = MaxLevel; level > newNodeLevel; level--)
        {
            if (level < Nodes[currentEntryId].Neighbors.Length)
            {
                // Search with ef=1 to quickly find the next best entry point
                List<int> candidates = SearchLayer(newNode.Vector, currentEntryId, level, ef: 1);

                if (candidates.Count > 0)
                {
                    currentEntryId = candidates[0];
                }
            }
        }
        return currentEntryId;
    }

    /// <summary>
    /// Connect the new node to neighbors in each layer down to level 0
    /// </summary>
    /// <param name="newNode"></param>
    /// <param name="newNodeLevel"></param>
    /// <param name="currentEntryId"></param>
    private void ConnectLayers(HnswNode newNode, int newNodeLevel, int currentEntryId)
    {
        // Connect from min(newNodeLevel, MaxLevel) down to 0
        for (int level = Math.Min(newNodeLevel, MaxLevel); level >= 0; level--)
        {
            // Search the layer to find potential neighbors
            List<int> candidates = SearchLayer(newNode.Vector, currentEntryId, level, EfConstruction);

            // Select neighbors using heuristic
            List<int> neighborsToConnect = SelectNeighbors(newNode.Vector, candidates, MaxNeighbours);

            // Establish connections
            newNode.Neighbors[level].AddRange(neighborsToConnect);

            // Connect neighbors back to the new node and manage their connections
            foreach (int neighborId in neighborsToConnect)
            {
                HnswNode neighborNode = Nodes[neighborId];
                neighborNode.Neighbors[level].Add(newNode.Id);

                if (neighborNode.Neighbors[level].Count > MaxNeighbours)
                {
                    ShrinkConnections(neighborId, level);
                }
            }

            // Update entry point for next level's search
            if (candidates.Count > 0)
            {
                currentEntryId = candidates[0];
            }
        }
    }

    /// <summary>
    /// Update global index state if the new node has the highest level
    /// </summary>
    /// <param name="newNode"></param>
    private void UpdateMaxState(HnswNode newNode)
    {
        if (newNode.Level > MaxLevel)
        {
            MaxLevel = newNode.Level;
            EntryPointId = newNode.Id;
        }
    }

    /// <summary>
    /// Find nearest neighbors for a given query vector
    /// </summary>
    /// <param name="queryVector"></param>
    /// <param name="k"></param>
    /// <param name="efSearch"></param>
    /// <returns></returns>
    public List<HnswNode> FindNearestNeighbors(float[] queryVector, int k, int? efSearch = null)
    {
        if (Nodes.Count == 0)
        {
            return new List<HnswNode>();
        }

        int ef = efSearch ?? EfConstruction;

        int currentEntryId = EntryPointId;

        for (int level = MaxLevel; level >= 1; level--)
        {
            List<int> candidates = SearchLayer(queryVector, currentEntryId, level, ef: 1);
            if (candidates.Count > 0)
            {
                currentEntryId = candidates.First();
            }
            // If no candidates found, continue with the currentEntryId
        }

        List<int> finalCandidatesIds = SearchLayer(queryVector, currentEntryId, 0, ef);
        List<HnswNode> finalCandidates = finalCandidatesIds.Select(id => Nodes[id])
            .OrderBy(node => GetDistance(queryVector, node.Vector))
            .ToList();
        return finalCandidates.Take(k).ToList();
    }

    /// <summary>
    /// Select neighbors ensuring diversity using heuristic
    /// </summary>
    /// <param name="queryVector"></param>
    /// <param name="candidates"></param>
    /// <param name="maxConnections"></param>
    /// <returns></returns>
    public List<int> SelectNeighbors(float[] queryVector, List<int> candidates, int maxConnections)
    {
        //Sort candidates by distance to query vector
        var sortedCandidates = candidates.Select(id => new
        {
            Id = id,
            Distance = GetDistance(queryVector, Nodes[id].Vector)
        })
        .OrderBy(x => x.Distance)
        .ToList();

        List<int> selectedNeighbors = new List<int>();
        HashSet<int> selectedSet = new HashSet<int>();

        // Select diverse neighbors
        foreach (var candidate in sortedCandidates)
        {
            if (selectedNeighbors.Count >= maxConnections)
            {
                break;
            }

            bool isDiverse = true;
            //Check diversity against already selected neighbors
            foreach (int selectedId in selectedNeighbors)
            {
                float distanceBetween = GetDistance(Nodes[candidate.Id].Vector, Nodes[selectedId].Vector);
                if (distanceBetween < candidate.Distance)
                {
                    isDiverse = false;
                    break;
                }
            }

            if (isDiverse)
            {
                selectedNeighbors.Add(candidate.Id);
                selectedSet.Add(candidate.Id);
            }
        }
        //Fill remaining slots
        if (selectedNeighbors.Count < maxConnections)
        {
            foreach (var candidate in sortedCandidates)
            {
                if (selectedNeighbors.Count >= maxConnections)
                    break;
                if (selectedSet.Add(candidate.Id))
                {
                    selectedNeighbors.Add(candidate.Id);
                }

            }
        }
        return selectedNeighbors;
    }

    /// <summary>
    /// Shrink connections of a node to maintain max neighbors using selection heuristic
    /// </summary>
    /// <param name="nodeId"></param>
    /// <param name="layer"></param>
    public void ShrinkConnections(int nodeId, int layer)
    {
        HnswNode node = Nodes[nodeId];
        List<int> currentNeighbors = node.Neighbors[layer];

        if (currentNeighbors.Count <= MaxNeighbours)
            return;

        float[] nodeVector = node.Vector;
        List<int> neighborids = currentNeighbors;

        List<int> selectedNeighbors = SelectNeighbors(nodeVector, neighborids, MaxNeighbours);
        node.Neighbors[layer] = selectedNeighbors;
    }



    /// <summary>
    /// Search for nearest neighbors in a given layer
    /// </summary>
    /// <param name="queryVector"></param>
    /// <param name="entryId"></param>
    /// <param name="layer"></param>
    /// <param name="ef"></param>
    /// <returns></returns>
    public List<int> SearchLayer(float[] queryVector, int entryId, int layer, int ef)
    {
        ef = Math.Max(1, ef);

        // Candidates: Min-priority queue to explore the nodes closest to the query first.
        var candidateQueue = new PriorityQueue<int, float>();

        // bestResults: A Max-prio queue to track the best 'ef' results found.
        // Stores distances in negative values because PriorityQueue is a min-heap.
        // This allows us to quickly Dequeue the furthest node when we find a better one.
        var bestResults = new PriorityQueue<int, float>();

        // Track nodes we have already visited to prevent infinite loops and redundant work.
        var visitedSet = new HashSet<int>();

        if (!Nodes.TryGetValue(entryId, out var entryNode))
        {
            return new List<int>();
        }

        // Initialize search with the entry point
        float entryDist = GetDistance(queryVector, entryNode.Vector);
        candidateQueue.Enqueue(entryId, entryDist);
        bestResults.Enqueue(entryId, -entryDist);
        visitedSet.Add(entryId);

        while (candidateQueue.Count > 0)
        {
            // Get the nearest candidate that hasn't been explored yet
            candidateQueue.TryPeek(out int currentId, out float currentDist);
            candidateQueue.Dequeue();

            // Get the current worst/furthest distance in our top-K results
            bestResults.TryPeek(out _, out float worstDistNeg);
            float worstDist = -worstDistNeg;

            // If the current closest candidate is already further than our worst result
            // and we have enough results, no better results can be found. 
            if (currentDist > worstDist && bestResults.Count >= ef) break;

            var currentNode = Nodes[currentId];

            // Ensure that the node has connections at this specific HNSW layer.
            if (layer >= currentNode.Neighbors.Length) continue;

            foreach (int neighborId in currentNode.Neighbors[layer])
            {
                // Only process neighbours we haven't seen in this search
                if (visitedSet.Add(neighborId))
                {
                    float neighborDist = GetDistance(queryVector, Nodes[neighborId].Vector);

                    // If this neighbor is closer than our worst result
                    // or if we haven't reached the required 'ef' count
                    if (bestResults.Count < ef || neighborDist < worstDist)
                    {
                        candidateQueue.Enqueue(neighborId, neighborDist);
                        bestResults.Enqueue(neighborId, -neighborDist);

                        // Maintain the size of the result set to exactly 'ef'
                        if (bestResults.Count > ef)
                        {
                            bestResults.Dequeue();
                        }

                        // Update worstDist for the next check
                        bestResults.TryPeek(out _, out worstDistNeg);
                        worstDist = -worstDistNeg;
                    }
                }
            }
        }

        return ExtractResults(bestResults);
    }

    private List<int> ExtractResults(PriorityQueue<int, float> queue)
    {
        var results = new List<int>(queue.Count);
        while (queue.Count > 0) results.Add(queue.Dequeue());
        results.Reverse();
        return results;
    }

    /// <summary>
    /// Calculates distance between two vectors. 1 - Cosine Similarity
    /// </summary>
    /// <param name="v1"></param>
    /// <param name="v2"></param>
    /// <returns></returns>
    private static float GetDistance(float[] v1, float[] v2) => 1.0f - HNSWUtils.CosineSimilarity(v1, v2);


    /// <summary>
    /// Generates a KnnMatrix from all nodes
    /// </summary>
    /// <param name="k"></param>
    /// <returns></returns>
    public (int[][], float[][]) KnnMatrix(int k)
    {
        var nodesList = Nodes.Values.ToList();
        var nodeCount = nodesList.Count;

        var idToInternalIndex = new Dictionary<int, int>();
        for (int i = 0; i < nodeCount; i++)
        {
            idToInternalIndex[nodesList[i].Id] = i;
        }

        int[][] allIndices = new int[nodeCount][];
        float[][] allDistances = new float[nodeCount][];

        Parallel.For(0, nodeCount, i =>
        {
            var queryVector = nodesList[i].Vector;

            //k + 1, because closest is always it 
            List<HnswNode> neighbors = FindNearestNeighbors(queryVector, k + 1);
            List<int> neighborIds = neighbors.Select(n => n.Id).ToList();

            int[] rowIndicies = new int[k];
            float[] rowDistances = new float[k];

            int found = 0;
            for (int j = 0; j < neighborIds.Count && found < k; j++)
            {
                int neighborId = neighborIds[j];
                if (neighborId == nodesList[i].Id) continue; // skip self

                rowIndicies[found] = idToInternalIndex[neighborId];
                rowDistances[found] = GetDistance(queryVector, Nodes[neighborId].Vector);
                found++;
            }

            allIndices[i] = rowIndicies;
            allDistances[i] = rowDistances;
        });

        return (allIndices, allDistances);
    }


}