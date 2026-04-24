using Alphaleonis.Win32.Filesystem;
using SimiliVec_Explorer.DocumentStorer;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using VectorDataBase.Indices;
using VectorDataBase.Interfaces;
using System.Linq;

public class SemanticIndexerService
{
    private readonly IEmbeddingModel _model;
    private readonly HnswIndexV3 _index;
    private readonly DocumentStore _documentStore;
    private readonly object _indexLock = new();
    private int idGenerator = 0;
    private readonly Random _random = new Random();

    // Priority extensions for "immediate" indexing
    private static readonly HashSet<string> HighPriorityExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".md", ".txt", ".docx" };

    private static readonly HashSet<string> SearchableExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".txt", ".md", ".docx", ".html", ".json", ".py", ".cs", ".xml" };

    public SemanticIndexerService(IEmbeddingModel model, HnswIndexV3 index, DocumentStore documentStore)
    {
        _model = model;
        _index = index;
        _documentStore = documentStore;
    }

    public async Task RunFullIndexAsync(string rootPath)
    {
        using var pathQueue = new BlockingCollection<string>(50000);
        using var contentQueue = new BlockingCollection<FileContent>(1000);

        var totalWatch = Stopwatch.StartNew();
        Console.WriteLine("Starting Rapid Discovery...");

        // 1. FAST DISCOVERY: Get all paths and prioritize them
        var discoveryTask = Task.Run(() => {
            var sw = Stopwatch.StartNew();
            DiscoverFilesFast(rootPath, pathQueue);
            sw.Stop();
            Console.WriteLine($"[Timing] DiscoverFilesFast took {sw.Elapsed.TotalMilliseconds} ms");
        });

        // 2. PARALLEL EXTRACTION: Multiple threads reading file contents
        var extractionTask = Task.Run(() => {
            var sw = Stopwatch.StartNew();
            ParallelExtractContent(pathQueue, contentQueue);
            sw.Stop();
            Console.WriteLine($"[Timing] ParallelExtractContent took {sw.Elapsed.TotalMilliseconds} ms");
        });

        // 3. GPU PROCESSING: Consumer
        var gpuWatch = Stopwatch.StartNew();
        await ProcessEmbeddingsOnGpu(contentQueue);
        gpuWatch.Stop();
        Console.WriteLine($"[Timing] ProcessEmbeddingsOnGpu took {gpuWatch.Elapsed.TotalMilliseconds} ms");

        await Task.WhenAll(discoveryTask, extractionTask);
        totalWatch.Stop();
        Console.WriteLine($"Full Indexing Complete in {totalWatch.Elapsed.TotalSeconds}s. Nodes: {_index.NodeCount}");
    }

    private void DiscoverFilesFast(string root, BlockingCollection<string> output)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            // Commercial Strategy: Get metadata first to allow sorting by Recency
            // Using AlphaLeonis to get FileSystemEntryInfo objects (much faster than File.GetFiles)
            var options = DirectoryEnumerationOptions.Recursive |
                          DirectoryEnumerationOptions.ContinueOnException |
                          DirectoryEnumerationOptions.BasicSearch;

            var allEntries = Alphaleonis.Win32.Filesystem.Directory.EnumerateFileSystemEntryInfos<FileSystemEntryInfo>(root, "*.*", options)
                .Where(e => !e.IsDirectory && IsSupported(e.LongFullPath))
                .OrderByDescending(e => e.LastWriteTime) // INDEX RECENT FILES FIRST
                .ToList();

            Console.WriteLine($"Discovery finished. Found {allEntries.Count} candidate files.");

            foreach (var entry in allEntries)
            {
                output.Add(entry.LongFullPath);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Discovery Error: {ex.Message}");
        }
        finally
        {
            output.CompleteAdding();
            sw.Stop();
            Console.WriteLine($"[Timing] DiscoverFilesFast (internal) took {sw.Elapsed.TotalMilliseconds} ms");
        }
    }

    private void ParallelExtractContent(BlockingCollection<string> input, BlockingCollection<FileContent> output)
    {
        var sw = Stopwatch.StartNew();
        // Use 4-8 threads for extraction to saturate Disk IO while GPU works
        Parallel.ForEach(input.GetConsumingEnumerable(), new ParallelOptions { MaxDegreeOfParallelism = 8 }, path =>
        {
            var swItem = Stopwatch.StartNew();
            try
            {
                // Commercial Logic: Don't read huge files entirely to start
                var fileInfo = new System.IO.FileInfo(path);
                if (fileInfo.Length > 10 * 1024 * 1024) return; // Skip files > 10MB for first-pass

                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);

                string content = reader.ReadToEnd();
                if (string.IsNullOrWhiteSpace(content) || IsBinaryContent(content)) return;

                int id = Interlocked.Increment(ref idGenerator);
                _documentStore.AddDocument(id, path);

                output.Add(new FileContent { Id = id, Content = content });
            }
            catch (Exception ex) { Console.WriteLine($"Error processing file: {ex.Message}"); }
            finally { swItem.Stop(); }
        });

        output.CompleteAdding();
        sw.Stop();
        Console.WriteLine($"[Timing] ParallelExtractContent (internal) took {sw.Elapsed.TotalMilliseconds} ms");
    }

    private async Task ProcessEmbeddingsOnGpu(BlockingCollection<FileContent> input)
    {
        const int GPU_BATCH_SIZE = 128;
        var gpuModel = _model.Factory();
        var localBatch = new List<FileContent>(GPU_BATCH_SIZE);

        await Task.Run(() =>
        {
            Stopwatch sw = Stopwatch.StartNew();
            foreach (var document in input.GetConsumingEnumerable())
            {
                var swDoc = Stopwatch.StartNew();
                document.Content = SanitizeUnicode(document.Content);
                localBatch.Add(document);

                if (localBatch.Count == GPU_BATCH_SIZE)
                {
                    RunBatchToIndex(localBatch, gpuModel);
                    localBatch.Clear();
                }
            }

            if (localBatch.Count > 0)
            {
                var swBatch = Stopwatch.StartNew();
                RunBatchToIndex(localBatch, gpuModel);
                Console.WriteLine($"Processed final GPU Batch of {localBatch.Count}");
            }
            Console.WriteLine($"[Timing] ProcessEmbeddingsOnGpu (internal) total GPU processing time: {sw.Elapsed.TotalMilliseconds} ms");
        });
    }

    private void RunBatchToIndex(List<FileContent> batch, IEmbeddingModel model)
    {
        var sw = Stopwatch.StartNew();
        string[] texts = batch.Select(b => b.Content).ToArray();
        float[][] vectors = model.GetEmbeddings(texts, isQuery: false);
        Console.WriteLine($"Generated embeddings for batch of {batch.Count} documents in {sw.Elapsed.TotalMilliseconds} ms");

        lock (_indexLock)
        {
            for (int i = 0; i < batch.Count; i++)
            {
                _index.Insert(vectors[i], batch[i].Id, _random);
            }
        }
        sw.Stop();
        Console.WriteLine($"[Timing] RunBatchToIndex for {batch.Count} docs took {sw.Elapsed.TotalMilliseconds} ms");
    }

    private bool IsSupported(string path) => SearchableExtensions.Contains(Alphaleonis.Win32.Filesystem.Path.GetExtension(path));

    private string SanitizeUnicode(string input)
    {
        try
        {
            var encoding = Encoding.GetEncoding(Encoding.UTF8.CodePage, new EncoderReplacementFallback(""), new DecoderReplacementFallback(""));
            return encoding.GetString(encoding.GetBytes(input));
        }
        catch { return input; }
    }

    private bool IsBinaryContent(string content)
    {
        int nonPrintable = content.Count(c => char.IsControl(c) && c != '\r' && c != '\n' && c != '\t');
        return (double)nonPrintable / Math.Max(content.Length, 1) > 0.1;
    }
}

public class FileContent
{
    public int Id { get; set; }
    public string Content { get; set; }
}