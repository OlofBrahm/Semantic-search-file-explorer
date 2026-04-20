using System;
using System.Collections.Generic;
using VectorDataBase.Models;

namespace VectorDataBase.Interfaces;

public interface IHnswIndex
{
    Dictionary<int, HnswNode> Nodes { get; set; }
    /// <summary>
    /// Insert a new node into the index
    /// </summary>
    /// <param name="newNode"></param>
    /// <param name="random"></param>
    void Insert(HnswNode newNode, Random random);

    /// <summary>
    /// Find nearest neighbors for a given query vector
    /// </summary>
    /// <param name="queryVector"></param>
    /// <param name="k"></param>
    /// <param name="efSearch"></param>
    /// <returns></returns>
    List<HnswNode> FindNearestNeighbors(float[] queryVector, int k, int? efSearch = null);
}