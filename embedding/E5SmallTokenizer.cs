using Microsoft.ML.Tokenizers;

public class E5SmallTokenizer
{
    private readonly BertTokenizer _tokenizer;
    private const int MAX_SEQUENCE_LENGTH = 256;

    public E5SmallTokenizer(string vocabPath)
    {
        _tokenizer = BertTokenizer.Create(vocabFilePath: vocabPath);
    }

    public (long[] inputIds, long[] tokenTypeIds, long[] attentionMasks, int batchMaxLen) EncodeBatchFlat(string[] texts, bool isQuery)
    {
        int count = texts.Length;
        var rawIdsBatch = new IReadOnlyList<int>[count];
        int batchMaxLen = 0;

        // Pre-calculate prefix IDs once for the whole batch
        string prefixStr = isQuery ? "query: " : "passage: ";
        var prefixIds = _tokenizer.EncodeToIds(prefixStr);

        // Pass 1: Parallel Tokenization
        Parallel.For(0, count, i =>
        {
            var text = texts[i] ?? string.Empty;
            var raw = _tokenizer.EncodeToIds(text);
            rawIdsBatch[i] = raw;

            // Debug: Log input text and token count
            Console.WriteLine($"[Tokenizer] Input[{i}]: '{text.Replace("\n", " ").Replace("\r", " ")}' | Tokens: {raw.Count}");

            // Total = CLS (1) + Prefix + Text + SEP (1)
            int totalLen = 1 + prefixIds.Count + raw.Count + 1;
            int cappedLen = Math.Min(totalLen, MAX_SEQUENCE_LENGTH);

            int initialMax;
            do
            {
                initialMax = batchMaxLen;
                if (initialMax >= cappedLen) break;
            } while (Interlocked.CompareExchange(ref batchMaxLen, cappedLen, initialMax) != initialMax);
        });

        Console.WriteLine($"[Tokenizer] Final batchMaxLen: {batchMaxLen}, count: {count}");

        if (batchMaxLen == 0)
        {
            Console.WriteLine("[Tokenizer] ERROR: batchMaxLen is zero! No valid input?");
        }

        long[] flatIds = new long[count * batchMaxLen];
        long[] flatMask = new long[count * batchMaxLen];
        long[] flatTypes = new long[count * batchMaxLen];

        // Pass 3: Fill arrays without ever creating "prefix + text" strings
        Parallel.For(0, count, i =>
        {
            var rawTextIds = rawIdsBatch[i];
            int rowOffset = i * batchMaxLen;
            int currentPos = rowOffset;

            // Debug: Log rowOffset and array bounds
            if (rowOffset >= flatIds.Length)
            {
                Console.WriteLine($"[Tokenizer] ERROR: rowOffset {rowOffset} >= flatIds.Length {flatIds.Length} (i={i})");
                return;
            }

            // 1. CLS
            if (currentPos - rowOffset < batchMaxLen)
            {
                if (currentPos < flatIds.Length)
                {
                    flatIds[currentPos] = 101L;
                    flatMask[currentPos] = 1L;
                }
                else
                {
                    Console.WriteLine($"[Tokenizer] ERROR: currentPos {currentPos} >= flatIds.Length {flatIds.Length} (CLS, i={i})");
                }
                currentPos++;
            }

            // 2. Prefix IDs
            foreach (var pId in prefixIds)
            {
                if (currentPos - rowOffset >= batchMaxLen)
                    break;
                if (currentPos < flatIds.Length)
                {
                    flatIds[currentPos] = (long)pId;
                    flatMask[currentPos] = 1L;
                }
                else
                {
                    Console.WriteLine($"[Tokenizer] ERROR: currentPos {currentPos} >= flatIds.Length {flatIds.Length} (Prefix, i={i})");
                }
                currentPos++;
            }

            // 3. Text IDs
            foreach (var tId in rawTextIds)
            {
                if (currentPos - rowOffset >= batchMaxLen)
                    break;
                if (currentPos < flatIds.Length)
                {
                    flatIds[currentPos] = (long)tId;
                    flatMask[currentPos] = 1L;
                }
                else
                {
                    Console.WriteLine($"[Tokenizer] ERROR: currentPos {currentPos} >= flatIds.Length {flatIds.Length} (Text, i={i})");
                }
                currentPos++;
            }

            // 4. SEP
            if (currentPos - rowOffset < batchMaxLen)
            {
                if (currentPos < flatIds.Length)
                {
                    flatIds[currentPos] = 102L;
                    flatMask[currentPos] = 1L;
                }
                else
                {
                    Console.WriteLine($"[Tokenizer] ERROR: currentPos {currentPos} >= flatIds.Length {flatIds.Length} (SEP, i={i})");
                }
                currentPos++;
            }

            // Debug: Log final currentPos for this row
            Console.WriteLine($"[Tokenizer] Row {i}: rowOffset={rowOffset}, final currentPos={currentPos}");
        });

        return (flatIds, flatTypes, flatMask, batchMaxLen);
    }
}