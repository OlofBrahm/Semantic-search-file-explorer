using System;
using System.Collections.Generic;

namespace VectorDataBase.Models;

public class HnswNode
{
    public int Id { get; set; }
    public float[] Vector { get; init; } = Array.Empty<float>();
    public int Level { get; set; }
    public List<int>[] Neighbors { get; set; } = Array.Empty<List<int>>();
    public int OriginalDocumentId { get; set; }

}