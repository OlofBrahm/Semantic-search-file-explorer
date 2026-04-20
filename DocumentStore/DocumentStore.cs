using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VectorDataBase.Models;

namespace SimiliVec_Explorer.DocumentStore
{
    public class DocumentStore
    {
        private readonly Dictionary<int, DocumentModel> _documentStore = new Dictionary<int, DocumentModel>();


        public List<DocumentModel> GetDocumentsByHnswNode(List<HnswNode> hnswNodes)
        {
            
            var results = new List<DocumentModel>();
            foreach (var node in hnswNodes)
            {
                if (_documentStore.TryGetValue(node.OriginalDocumentId, out var doc))
                {
                    results.Add(doc);
                }
            }
            return results;
        }

        public List<DocumentModel> GetDocumentByids(List<int> documentIds)
        {
            var results = new List<DocumentModel>();
            foreach (var id in documentIds)
            {
                if (_documentStore.TryGetValue(id, out var doc))
                {
                    results.Add(doc);
                }
            }
            return results;
        }

        public void AddDocument(int id, string content)
        {
            try
            {
                _documentStore[id] = new DocumentModel
                {
                    Id = id.ToString(),
                    Content = content //this will get replaced with the file path to the corresponding file on the computer
                };
            }
            catch (Exception ex)
            {
                {
                    Console.WriteLine(ex.Message);
                }

            }
        }
    }
}
