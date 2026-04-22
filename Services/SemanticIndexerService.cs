using Alphaleonis.Win32.Filesystem;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using VectorDataBase.Embedding;
using VectorDataBase.Indices;
using VectorDataBase.Interfaces;
using VectorDataBase.Models;
using VectorDataBase.Utils;
using SimiliVec_Explorer.DocumentStorer;
using static System.Net.Mime.MediaTypeNames;
using Directory = Alphaleonis.Win32.Filesystem.Directory;
using DirectoryEnumerationOptions = Alphaleonis.Win32.Filesystem.DirectoryEnumerationOptions;
using DirectoryInfo = Alphaleonis.Win32.Filesystem.DirectoryInfo;
using File = Alphaleonis.Win32.Filesystem.File;

    public class SemanticIndexerService
    {
        

        private readonly HashSet<string> _supportedExts = new() { ".log", ".txt", ".md" };
        private readonly IEmbeddingModel _model;
        private readonly HnswIndexV3 _index;
        private int idGenerator = 0;
        private Random _random;
        private DocumentStore _documentStore;
        public SemanticIndexerService(IEmbeddingModel model, HnswIndexV3 index, DocumentStore documentStore)
        {
            _model = model;
            _index = index;
            _random = new Random();
            _documentStore = documentStore;
        }

        public async Task RunFullIndexAsync(string rootPath)
        {

            using var pathQueue = new BlockingCollection<string>(1000);
            using var contentQueue = new BlockingCollection<FileContent>(100);


            var discoveryTask = Task.Run(() => DiscoverFiles(rootPath, pathQueue));

            var extractionTask = Task.Run(() => ExtractContent(pathQueue, contentQueue));

            await Task.Run(() => ProcessEmbeddings(contentQueue));

            await Task.WhenAll(discoveryTask, extractionTask);
            Console.WriteLine("Indexing Complete.");
        }

        private void DiscoverFiles(string root, BlockingCollection<string> output)
        {
            try
            {
                var options = DirectoryEnumerationOptions.Recursive |
                              DirectoryEnumerationOptions.ContinueOnException |
                              DirectoryEnumerationOptions.SkipReparsePoints;

                var entries = Alphaleonis.Win32.Filesystem.Directory.EnumerateFileSystemEntryInfos<FileSystemEntryInfo>(
                    root,
                    "*.*",
                    options
                );

                foreach (var entry in entries)
                {

                    if (!entry.IsDirectory && IsSupported(entry.LongFullPath))
                    {
                        // This call will BLOCK if the queue is full (Backpressure)
                        output.Add(entry.LongFullPath);
                        Console.WriteLine("Found files counter: " + output.Count());
                    }
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                // This happens if the 'root' itself is forbidden
                Debug.WriteLine($"Access denied to root path: {ex.Message}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Discovery failed: {ex.Message}");
            }
            finally
            {
                output.CompleteAdding();
            }
        }

        private void ExtractContent(BlockingCollection<string> input, BlockingCollection<FileContent> output)
        {
            try
            {
                foreach (var path in input.GetConsumingEnumerable())
                {
                    FileContent content = new FileContent
                    {
                        Id = Interlocked.Increment(ref idGenerator),
                        Content = File.ReadAllText(path)
                    };
                    output.Add(content);
                    Console.WriteLine("Extracted content counter: " + output.Count());

                _documentStore.AddDocument(content.Id, path);

                }
            }
            finally { output.CompleteAdding(); }
        }

        private void ProcessEmbeddings(BlockingCollection<FileContent> input)
        {
            foreach (var document in input.GetConsumingEnumerable())
            {
                var vector = _model.GetEmbeddings(document.Content);
            Console.WriteLine("Generated embedding for Document ID: " + document.Id);
            _index.Insert(vector, document.Id, _random);
            Console.WriteLine("Indexed Document ID: " + document.Id);
            }
        }

        private static readonly HashSet<string> SearchableExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".md", ".pdf", ".docx", ".html", ".log", ".json", ".py", ".cs", ".err", ".out", ".conf", ".cfg", ".json", ".xml"
        };

        private bool IsSupported(string path)
        {
            string ext = Alphaleonis.Win32.Filesystem.Path.GetExtension(path);

            return !string.IsNullOrEmpty(ext) && SearchableExtensions.Contains(ext);
        }

    }

    public class FileContent
    {
        public int Id { get; set; }
        public string Content { get; set; } = string.Empty;
    }
