using System.Linq;
using Google.Protobuf.WellKnownTypes;
using VectorDataBase.Interfaces;
using VectorDataBase.Models;
using VectorDataBase.Utils;
using VectorDataBase.Indices;
using SimiliVec_Explorer.DocumentStore;

namespace VectorDataBase.Services
{
    public class VectorService
    {
        public readonly HnswIndexV3 _dataIndex;
        private readonly IEmbeddingModel _embeddingModel;
        private readonly DocumentStore _documentStore;
        private int _nextId = 0;

        public VectorService(HnswIndexV3 dataIndex, IEmbeddingModel embeddingModel)
        {
            _dataIndex = dataIndex;
            _embeddingModel = embeddingModel;
            _documentStore = new DocumentStore();
        }

        public void AddDocument(string documentText, int documentId)
        {
            var chunks = SimpleTextChunker.Chunk(documentText);
            foreach (var chunk in chunks)
            {
                var embedding = _embeddingModel.GetEmbeddings(chunk);
                _dataIndex.Insert(embedding, documentId, new Random());
                _nextId++;
            }

            _documentStore.AddDocument(documentId, documentText);
        }

        public List<DocumentModel> Search(string query, int k)
        {
            Console.WriteLine($"Current node count in index: {_dataIndex.NodeCount}");
            var queryEmbedding = _embeddingModel.GetEmbeddings(query, isQuery: true);
            var documentIds = _dataIndex.GetOriginalDocumentIds(queryEmbedding, k);
            return _documentStore.GetDocumentByids(documentIds);
        }
    }
}
