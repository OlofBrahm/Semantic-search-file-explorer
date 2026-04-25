# Semantic Search File Explorer

USE AT YOUR OWN RISK

**Semantic Search File Explorer** is a .NET 8.0 console application that performs semantic (vector-based) search on documents within a filesystem directory. It leverages vector embeddings and the HNSW (Hierarchical Navigable Small World) index to efficiently find and retrieve text documents relevant to a user's query—even if those documents don't contain exact keyword matches.

## Features

- **Semantic Search**: Utilizes machine learning embeddings to understand the meaning of queries and documents, returning the most relevant results.
- **File System Indexing**: Recursively scans a user-specified directory, indexing a wide variety of text-based documents.
- **Supported File Types**: Out-of-the-box support for `.txt`, `.md`, `.log`, `.json`, `.cs`, `.py`, `.html`, `.xml`, `.docx`, and others.
- **Efficient Nearest Neighbor Search**: Uses a custom HNSW (Hierarchical Navigable Small World) index for fast approximate nearest neighbor searches.
- **Console Interface**: Simple interactive prompt for entering queries and viewing results.

## TODOs & Ideas
- Solve extreme initial load time
- Display what chunk of the file is the hit.
- Add batching for embedding (performance improvement)
- Support for multi-threaded indexing
- Cross-platform support
- Index substring spans for highlighting exact match context
