using System;
using System.Collections.Generic;

namespace VectorDataBase.Utils;

public static class SimpleTextChunker
{
    public static string[] Chunk(string text, int maxChunkSize = 1500)
    {
        var chunks = new List<string>();

        string[] paragraphs = text.Split(new[] { "\n\n", "\r\n\r\n" }, StringSplitOptions.RemoveEmptyEntries);
        string currentChunk = "";

        foreach (var paragraph in paragraphs)
        {
            if (!string.IsNullOrEmpty(currentChunk) && (currentChunk.Length + paragraph.Length + 2) > maxChunkSize)
            {
                chunks.Add(currentChunk.Trim());
                currentChunk = paragraph;
            }
            else
            {
                currentChunk += (string.IsNullOrEmpty(currentChunk) ? "" : "\n\n") + paragraph;
            }
        }

        if (!string.IsNullOrWhiteSpace(currentChunk))
        {
            chunks.Add(currentChunk.Trim());
        }

        return chunks.ToArray();
    }
}