---
id: SYNTH-10
summary: "Indexing pipeline (chunk -> embed -> upsert)"
status: open
acceptance_command: "dotnet build src/Synth.slnx --nologo -v q && dotnet test src/Synth.slnx --nologo -v q && grep -rq 'IndexingPipeline' src/Synth.Core"
acceptance_criterion: ""
boundaries: "Only add the IndexingPipeline that ties together IFileChunker + IEmbeddingGenerator + ICodeChunkStore for a directory of files, plus its tests. Do not add search/reranking (SYNTH-11) or any HTTP endpoint to trigger it (that can be a tiny follow-up, not required here). No Vue client. Tests must not require live Ollama/Qdrant/Docker."
limits: "max_iterations=30; max_minutes=150"
labels: [backend, rag-core, indexing]
---

# SYNTH-10: Indexing pipeline

## Context
Ties together everything from `SYNTH-6` through `SYNTH-9`: given a directory,
walk its files, chunk the C# ones with `CSharpRoslynChunker`, compute
embeddings for each chunk's `EmbeddingText`, and upsert into `ICodeChunkStore`.
Mirrors Sonar's `IndexingPipeline` (see Jarvis wiki `sonar-core`), simplified.

## What to do
1. Add `IndexingPipeline` to `Synth.Core` (or `Synth.Api` if it needs
   ASP.NET-specific services — prefer `Synth.Core` if the dependencies
   (`IFileChunker`, `IEmbeddingGenerator<string, Embedding<float>>`,
   `ICodeChunkStore`) are all injectable there) with roughly:
   `Task<int> IndexDirectoryAsync(string rootPath, CancellationToken ct = default)`
   returning the number of chunks indexed.
2. Implementation: enumerate files under `rootPath` (start with `*.cs` only,
   matching the one chunker that exists), skip `bin/`/`obj/`/`.git/`
   directories, run each file through the first `IFileChunker` whose
   `CanHandle` returns true, compute each resulting chunk's embedding via the
   registered `IEmbeddingGenerator`, set `CodeChunk.Embedding`, and batch
   `UpsertAsync` into `ICodeChunkStore`.
3. Handle empty/unreadable files gracefully (skip, don't throw and abort the
   whole run) — but do surface a count of files skipped vs. indexed for
   observability (a simple return type/summary object is fine, doesn't need
   to be fancy).
4. Register `IndexingPipeline` in DI.
5. Add tests that build a small temp directory with 2-3 sample `.cs` files
   (including one with a couple of classes/methods), run `IndexDirectoryAsync`
   against a fake/mock `IEmbeddingGenerator` (deterministic fake vectors,
   e.g. hash-based) and a `LocalCodeChunkStore` from `SYNTH-9`, and assert the
   expected number of chunks ends up in the store, retrievable via
   `GetByFileAsync`. No live Ollama/Qdrant/Docker.

## Acceptance
`dotnet build`/`dotnet test` on `src/Synth.slnx` stay green, and
`IndexingPipeline` exists under `src/Synth.Core` (mirrors the frontmatter
`acceptance_command`).

## Out of scope
- Search/reranking — `SYNTH-11`.
- Any HTTP endpoint/CLI to trigger indexing (nice-to-have, not required).
- Regex-based chunkers for other languages.
- Vue client.
