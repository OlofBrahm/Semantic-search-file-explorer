using System.Text.Json.Serialization;

namespace VectorDataBase.Models;

/// <summary>
/// Represents a document in the vector database
/// </summary>
public class DocumentModel
{
    /// <summary>
    /// Unique user-provided ID for the original document
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// The full content of the document
    /// </summary>
    public string FilePath = string.Empty;

    /// <summary>
    /// Optional metadata (author, date, source)
    /// </summary>
    public Dictionary<string, string> MetaData { get; set; } = new Dictionary<string, string>();

}
