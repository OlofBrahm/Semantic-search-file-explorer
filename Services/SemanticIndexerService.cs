using Alphaleonis.Win32.Filesystem;
using Google.Protobuf;
using SimiliVec_Explorer.DocumentStorer;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection.Metadata;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using VectorDataBase.Indices;
using VectorDataBase.Interfaces;
using DirectoryEnumerationOptions = Alphaleonis.Win32.Filesystem.DirectoryEnumerationOptions;
using File = Alphaleonis.Win32.Filesystem.File;

/// <summary>
/// TODO: We need to make it faster and cheaper to run. We should multithread the embedding, just need to handle thread safety on the index and document store. 
/// We should also consider batching the embedding calls to the model, as it can be more efficient to embed multiple documents at once.
/// </summary>
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
            Console.WriteLine($"Discovery failed: {ex.Message}");
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
                try
                {
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var reader = new StreamReader(stream))
                    {
                        string content = reader.ReadToEnd();
                        string trimmed = content.Trim();
                        if(string.IsNullOrWhiteSpace(trimmed) || trimmed.Length == 0 || trimmed == "{}" || trimmed == "[]")
                        {
                            Console.WriteLine($"Skipping empty file: {path}");
                            continue;
                        }
                        if (IsBinaryContent(content))
                        {
                            Console.WriteLine($"Skipping likely binary file: {path}");
                            continue;
                        }
                        FileContent fileContent = new FileContent
                        {
                            Id = Interlocked.Increment(ref idGenerator),
                            Content = content
                        };
                        output.Add(fileContent);
                        Console.WriteLine("Extracted content counter: " + output.Count());
                        _documentStore.AddDocument(fileContent.Id, path);
                    }
                }
                catch (IOException ex)
                {
                    Console.WriteLine($"IO error while reading file: {ex.Message}");
                }
            }
        }
        catch(Exception ex)
        {
            Console.WriteLine($"Content extraction failed: {ex.Message}");
            Debug.WriteLine($"Content extraction failed: {ex.Message}");
        }
        finally { output.CompleteAdding(); }
    }
    private string temp = string.Empty;
    private void ProcessEmbeddings(BlockingCollection<FileContent> input)
    {
        try
        {
            foreach (var document in input.GetConsumingEnumerable())
            {
                document.Content = SanitizeUnicode(document.Content);
                temp = document.Content;
                var vector = _model.GetEmbeddings(document.Content);
                Console.WriteLine("Generated embedding for Document ID: " + document.Id);
                _index.Insert(vector, document.Id, _random);
                Console.WriteLine("Indexed Document ID: " + document.Id);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Embedding processing failed: {ex.Message}");
            Console.WriteLine($"Embedding processing failed: {ex.Message}");
            Console.WriteLine($"Failed document string: {temp}");

        }
    }

    private static readonly HashSet<string> SearchableExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".md", ".docx", ".html", ".log", ".json", ".py", ".cs", ".err", ".out", ".conf", ".cfg", ".json", ".xml", "dll"
        };

    private bool IsSupported(string path)
    {
        string ext = Alphaleonis.Win32.Filesystem.Path.GetExtension(path);

        return !string.IsNullOrEmpty(ext) && SearchableExtensions.Contains(ext);
    }

    private string SanitizeUnicode(string input)
    {
        var encoderSettings = new EncoderReplacementFallback("�");
        var decoderSettings = new DecoderReplacementFallback("�");
        try
        {
            var encoding = Encoding.GetEncoding(Encoding.UTF8.CodePage, encoderSettings, decoderSettings);
            byte[] bytes = encoding.GetBytes(input);
            return encoding.GetString(bytes);
        }catch(Exception ex)
        {
            Console.WriteLine($"Unicode sanitization failed: {ex.Message}");
            Console.WriteLine($"Broken file string: {input}");
            Debug.WriteLine($"Unicode sanitization failed: {ex.Message}");
            return input;
        }

    }

    private bool IsBinaryContent(string content)
    {
        // If more than 10% of the characters are non-printable, consider it binary
        int nonPrintable = content.Count(c => char.IsControl(c) && c != '\r' && c != '\n' && c != '\t');
        double ratio = (double)nonPrintable / Math.Max(content.Length, 1);
        return ratio > 0.1;
    }
}

public class FileContent
{
    public int Id { get; set; }
    public string Content { get; set; } = string.Empty;
}
