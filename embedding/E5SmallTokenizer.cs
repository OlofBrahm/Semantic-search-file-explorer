using Microsoft.ML.Tokenizers;
using System;
using System.Linq;
namespace VectorDataBase.Embedding;

/// <summary>
/// Tokenizer for E5-Small-V2 model
/// </summary>
public class E5SmallTokenizer
{
    private readonly BertTokenizer _tokenizer;

    private const string RelativeVocabPath = "MLModels/e5-small-v2/vocab.txt";
    private string _vocabPath => System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, RelativeVocabPath);
    private const int MAX_SEQUENCE_LENGTH = 512;

    public E5SmallTokenizer()
    {
        _tokenizer = BertTokenizer.Create(vocabFilePath: _vocabPath);
    }

    /// <summary>
    /// Encode input text to token IDs, token type IDs, and attention mask
    /// </summary>
    /// <param name="text"></param>
    /// <returns></returns>
    public (long[] inputIds, long[] TokenTypeIds, long[] AttentionMask) Encode(string text, bool isQuery = true)
    {
        var ids = _tokenizer.EncodeToIds(text, true, true)
        .Select(id => (long)id)
        .ToList();

        if (ids.Count > MAX_SEQUENCE_LENGTH)
        {
            ids = ids.Take(MAX_SEQUENCE_LENGTH).ToList();
        }

        long[] inputIds = ids.ToArray();
        long[] attentionMask = Enumerable.Repeat(1L, inputIds.Length).ToArray();
        long[] tokenTypeIds = new long[inputIds.Length];

        return (inputIds, tokenTypeIds, attentionMask);
    }

}