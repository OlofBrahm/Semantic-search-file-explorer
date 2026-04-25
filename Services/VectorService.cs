using System.Linq;
using Google.Protobuf.WellKnownTypes;
using VectorDataBase.Interfaces;
using VectorDataBase.Models;
using VectorDataBase.Utils;
using VectorDataBase.Indices;
using SimiliVec_Explorer.DocumentStorer;
using SimiliVec_Explorer.Services;
using System.Data.SqlTypes;

namespace VectorDataBase.Services
{
    public class VectorService
    {
        public readonly HnswIndexV3 _dataIndex;
        private readonly IEmbeddingModel _embeddingModel;
        private int _nextId = 0;
        private DocumentStore _documentStore;
        private readonly StartupService _startupService;
        private string _rootPath = @"C:\Users\olleb\";

        public VectorService(HnswIndexV3 dataIndex, IEmbeddingModel embeddingModel)
        {
            _dataIndex = dataIndex;
            _embeddingModel = embeddingModel;
            _documentStore = new DocumentStore();
            _startupService = new StartupService(_documentStore, dataIndex);

        }

        public async Task Initialize()
        {
           await _startupService.InitializeAsync(_rootPath);
        }


        public List<DocumentModel> Search(string[] query, int k)
        {
            Console.WriteLine($"Current node count in index: {_dataIndex.NodeCount}");
            var queryText = string.Join(" ", query);
            Console.WriteLine($"[VectorService] Search called with query: '{queryText}' and k={k}");
            var queryEmbedding = _embeddingModel.GetEmbeddings(new[] { queryText }, isQuery: true);
            float[] primaryQueryVector = queryEmbedding[0];
            var documentIds = _dataIndex.GetOriginalDocumentIds(primaryQueryVector, k);
            return _documentStore.GetDocumentByids(documentIds);
        }
    }
}
