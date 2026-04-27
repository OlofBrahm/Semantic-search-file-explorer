using VectorDataBase.Services;
using VectorDataBase.Models;
using VectorDataBase.Indices;
using VectorDataBase.Embedding;
using System.ComponentModel;
using VectorDataBase.Persistence;

namespace SimiliVec_Explorer
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            
            VectorService vectorService = new VectorService();
            await vectorService.Initialize();
            
            while (true)
            {
                Console.WriteLine("Add search query: ");
                string[] userInput = Console.ReadLine()?.Split(' ') ?? Array.Empty<string>();
                var results = vectorService.Search(userInput, 5);
                Console.WriteLine(results.Count);

                foreach (var result in results)
                {
                    Console.WriteLine($" :::::::: Found document with ID: {result.Id} ::::::::: Content: {result.FilePath}");
                }
            }
        }
    }
}
