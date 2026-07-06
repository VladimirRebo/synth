---
id: SYNTH-7
summary: "Roslyn-based C# chunker (IFileChunker)"
status: open
acceptance_command: "dotnet build src/Synth.slnx --nologo -v q && dotnet test src/Synth.slnx --nologo -v q && grep -rq 'IFileChunker' src/Synth.Core"
acceptance_criterion: ""
boundaries: "Only add the IFileChunker abstraction and ONE implementation (Roslyn-based C# chunker) in Synth.Core, plus its tests. Do not add embeddings, vector store, or the indexing pipeline — those are later tasks. No regex fallback for other languages yet (C# only for now). No Qdrant/Ollama/Vue."
limits: "max_iterations=25; max_minutes=150"
labels: [backend, rag-core, chunking]
---

# SYNTH-7: Roslyn-based C# chunker

## Context
Ported from Sonar's chunking strategy (Jarvis wiki `code-chunking-strategy`,
`sonar-core`). Builds on the `CodeChunk` model from `SYNTH-6`. This task adds
the actual chunking logic for C# source using Roslyn — a real AST parse, not
line-window or regex splitting.

## What to do
1. Add `IFileChunker` to `Synth.Core`: something like
   `bool CanHandle(string filePath)` and
   `IReadOnlyList<CodeChunk> Chunk(string filePath, string relativePath, string content)`.
2. Add `CSharpRoslynChunker : IFileChunker` (using
   `Microsoft.CodeAnalysis.CSharp`) that:
   - Parses the file into a syntax tree and walks namespace → type → member.
   - Emits one `CodeChunk` per class/interface/record/struct (whole body,
     `ChunkType` = the matching type) and one per method/constructor
     (`ChunkType.Method`/`Constructor`), capturing `Namespace`, `ClassName`,
     `MethodName`, `StartLine`/`EndLine`, and `Summary` from the leading XML
     doc comment if present.
   - For methods longer than 300 lines, split into two chunks: the first ~50
     lines as `ChunkType.MethodHead` and the remainder as
     `ChunkType.MethodBody`, both carrying the same `MethodName`/`Summary`.
   - Computes a `FileHash` (e.g. SHA-256 of file content) stored on each
     chunk.
3. Add tests (in `Synth.Core.Tests`) using small inline C# source strings
   (no need for real files on disk beyond what a test fixture conveniently
   uses) covering: a simple class with one short method, a class with a long
   method (>300 lines, verify head/body split), and a method with an XML doc
   comment (verify `Summary` extraction).

## Acceptance
`dotnet build`/`dotnet test` on `src/Synth.slnx` stay green, and
`IFileChunker` exists under `src/Synth.Core` (mirrors the frontmatter
`acceptance_command`).

## Out of scope
- Regex-based chunkers for non-C# languages.
- Ollama embeddings — `SYNTH-8`.
- Qdrant / vector store — `SYNTH-9`.
- Indexing pipeline that walks a directory tree — `SYNTH-10`.
- Vue client.
