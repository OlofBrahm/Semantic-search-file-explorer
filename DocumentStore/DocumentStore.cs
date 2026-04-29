using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VectorDataBase.Models;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace SimiliVec_Explorer.DocumentStorer
{
    public class DocumentStore
    {
        private readonly string _connectionString;
        public DocumentStore(string dbPath)
        {
            _connectionString = $"Data Source={dbPath}";

            // Ensure the schema exists immediately
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE IF NOT EXISTS Documents (Id INTEGER PRIMARY KEY AUTOINCREMENT, DocumentId INTEGER, FilePath TEXT);";
            command.ExecuteNonQuery();
        }

        public void SaveDocument(DocumentModel doc)
        {
            Console.WriteLine($"[DocumentStore] SaveDocument: Id={doc.Id}, FilePath={doc.FilePath}");
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            var command = connection.CreateCommand();
            command.CommandText = @"INSERT OR REPLACE INTO Documents (DocumentId, FilePath) VALUES ($DocumentId, $FilePath)";
            command.Parameters.AddWithValue("$DocumentId", doc.Id);
            command.Parameters.AddWithValue("$FilePath", doc.FilePath);

            command.ExecuteNonQuery();
        }

        public DocumentModel? GetDocument(int id)
        {
            Console.WriteLine($"[DocumentStore] GetDocument: Id={id}");
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText = @"SELECT DocumentId, FilePath FROM Documents WHERE DocumentId = $DocumentId";
            command.Parameters.AddWithValue("$DocumentId", id);
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                var doc = new DocumentModel
                {
                    Id = reader.GetInt32(0),
                    FilePath = reader.GetString(1)
                };
                Console.WriteLine($"[DocumentStore] GetDocument FOUND: Id={doc.Id}, FilePath={doc.FilePath}");
                return doc;
            }
            Console.WriteLine($"[DocumentStore] GetDocument NOT FOUND: Id={id}");
            return null;
        }

        public bool IsPopulated()
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText = @"SELECT COUNT(*) FROM Documents";
            return Convert.ToInt32(command.ExecuteScalar()) > 0;
        }


    }
}
