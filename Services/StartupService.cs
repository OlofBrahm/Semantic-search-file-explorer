using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VectorDataBase.Embedding;
using VectorDataBase.Indices;
using SimiliVec_Explorer.DocumentStorer;

namespace SimiliVec_Explorer.Services
{
    public class StartupService
    {
        private readonly SemanticIndexerService _semanticIndexerService;
        private readonly EmbeddingModel _embeddingModel;
        private readonly HnswIndexV3 _hnswIndex;
        private readonly DocumentStore _documentstore;

        public StartupService(DocumentStore documentStore, HnswIndexV3 hnswIndex)
        {

            _embeddingModel = new EmbeddingModel();
            _hnswIndex = hnswIndex;
            _semanticIndexerService = new SemanticIndexerService(_embeddingModel, _hnswIndex, documentStore);
            _documentstore = documentStore;
        }

        public async Task InitializeAsync(string rootPath)
        {
            Console.WriteLine("Starting indexing process...");
            await _semanticIndexerService.RunFullIndexAsync(rootPath);
        }
    }
}
