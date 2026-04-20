namespace VectorDataBase.Models;

public struct HnswNodeV3
{
    public int Id;
    public int OriginalDocumentId;
    public int Level;
    public int VectorOffset;
    public int NeighborOffset;
}
