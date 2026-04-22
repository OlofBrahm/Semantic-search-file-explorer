using VectorDataBase.Services;
using VectorDataBase.Models;
using VectorDataBase.Indices;
using VectorDataBase.Embedding;
using System.ComponentModel;

namespace SimiliVec_Explorer
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            VectorService vectorService = new VectorService(new HnswIndexV3
            {
                MaxNeighbours = 16,
                EfConstruction = 200,
                InverseLogM = 1.0f / MathF.Log(16)
            }, new EmbeddingModel());

            await vectorService.Initialize();
            var results = vectorService.Search("This is a test file", 5);
            Console.WriteLine(results.Count);

            foreach (var result in results)
            {
                Console.WriteLine($" :::::::: Found document with ID: {result.Id} ::::::::: Content: {result.FilePath}");
            }
        }
    }
}
