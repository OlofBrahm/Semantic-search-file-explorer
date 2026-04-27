using Google.Protobuf.WellKnownTypes;
using SimiliVec_Explorer.DocumentStorer;
using SimiliVec_Explorer.Services;
using System.Data.SqlTypes;
using System.Linq;
using VectorDataBase.Indices;
using VectorDataBase.Interfaces;
using VectorDataBase.Models;
using VectorDataBase.Persistence;
using VectorDataBase.Embedding;
using VectorDataBase.Utils;

namespace VectorDataBase.Services
{
    public class VectorService
    {
        public readonly HnswIndexV3 _dataIndex;
        private readonly EmbeddingModel _embeddingModel;
        private int _nextId = 0;
        private DocumentStore _documentStore;
        private readonly StartupService _startupService;
        private string _rootPath = @"C:\Users\olleb\Documents";
        private readonly HnswStorage _storage;

        public VectorService()
        {
            _documentStore = new DocumentStore(@"C:\Users\olleb\source\repos\SimiliVec-Explorer\SimiliVec-Explorer\Storage\document_store.db");
            bool hasData = _documentStore.IsPopulated();
            _storage = new HnswStorage(@"C:\Users\olleb\Documents\TestSaves\my_index.bin", 2000000, 384);
            _dataIndex = new HnswIndexV3(_storage, 2000000, hasData)
            {
                MaxNeighbours = 16,
                EfConstruction = 200,
                InverseLogM = 1.0f / MathF.Log(16)
            };
            _embeddingModel = new EmbeddingModel();
            _startupService = new StartupService(_documentStore, _dataIndex);
        }


        public async Task Initialize()
        {
            if (_dataIndex.NodeCount > 0)
            {
                Console.WriteLine($"Index already has {_dataIndex.NodeCount} nodes in memory. Skipping initialization.");
                return;
            }
            if (_documentStore.IsPopulated())
            {
                Console.WriteLine("Existing data found. Loading index from storage...");
                return;
            }
            Console.WriteLine("No data found. Running full indexing...");
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
            Console.WriteLine($"[VectorService] Search found document IDs: {string.Join(", ", documentIds)}");
            List<DocumentModel> results = new List<DocumentModel>();
            foreach (var id in documentIds)
            {
                results.Add(_documentStore.GetDocument(id) ?? new DocumentModel { Id = id, FilePath = "Unknown" });
            }
            return results;
        }
    }
}
