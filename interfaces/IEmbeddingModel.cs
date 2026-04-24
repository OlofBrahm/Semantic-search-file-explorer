using VectorDataBase.Embedding;

namespace VectorDataBase.Interfaces;

public interface IEmbeddingModel
{
    /// <summary>
    /// Gets embeddings for the given text
    /// </summary>
    /// <param name="text"></param>
    /// <returns></returns>
    float[][] GetEmbeddings(string[] text, bool isQuery);

    /// <summary>
    /// Creates a new instance of the embedding model
    /// </summary>
    /// <returns></returns>
    EmbeddingModel Factory();

}