# Semantic Search File Explorer

USE AT YOUR OWN RISK

**Semantic Search File Explorer** is a .NET 8.0 console application that performs semantic (vector-based) search on documents within a filesystem directory. It leverages vector embeddings and the HNSW (Hierarchical Navigable Small World) index to efficiently find and retrieve text documents relevant to a user's query—even if those documents don't contain exact keyword matches.

## Features

- **Semantic Search**: Utilizes machine learning embeddings to understand the meaning of queries and documents, returning the most relevant results.
- **File System Indexing**: Recursively scans a user-specified directory, indexing a wide variety of text-based documents.
- **Supported File Types**: Out-of-the-box support for `.txt`, `.md`, `.log`, `.json`, `.cs`, `.py`, `.html`, `.xml`, `.docx`, and others.
- **Efficient Nearest Neighbor Search**: Uses a custom HNSW (Hierarchical Navigable Small World) index for fast approximate nearest neighbor searches.

## TODOs & Ideas
- Keyword support for the hit rankings, required for shorter file lengths.
- Display what chunk of the file is the hit. Preview button.
- Cross-platform support
- Trying to minimize GC calls, using array pools and reuse already allocated memory.
