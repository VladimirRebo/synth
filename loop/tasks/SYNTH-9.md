---
id: SYNTH-9
summary: "Qdrant as an Aspire resource + VectorStore wiring (+ Local store for tests)"
status: open
acceptance_command: "dotnet build src/Synth.slnx --nologo -v q && dotnet test src/Synth.slnx --nologo -v q && grep -riq 'qdrant' src/Synth.AppHost/AppHost.cs"
acceptance_criterion: ""
boundaries: "Only wire up Qdrant as an Aspire resource, a VectorStore abstraction over CodeChunk with a Qdrant-backed implementation, and a simple in-memory 'Local' implementation used by tests. Do not add the indexing pipeline or search yet. No Vue client. Tests must not require a live Qdrant/Docker connection."
limits: "max_iterations=30; max_minutes=150"
labels: [backend, rag-core, qdrant, vector-store, aspire]
---

# SYNTH-9: Qdrant as an Aspire resource + VectorStore wiring

## Context
Decided stack: **Qdrant** is Synth's vector store (see Jarvis wiki
`overview`/`synth`/`sonar-infrastructure`). This task adds Qdrant to the
Aspire AppHost, defines a small `VectorStore`-style abstraction over
`CodeChunk` (upsert/search/get-by-file, roughly mirroring Sonar's shape but
don't over-engineer), a Qdrant-backed implementation, and a simple in-memory
"Local" implementation — the Local one is what tests and local dev-without-
Docker will use, mirroring Sonar's own Qdrant/Milvus/Local split.

## What to do
1. Add Qdrant to `Synth.AppHost` (there's an official Aspire Qdrant hosting
   integration — confirm the exact package name/version on nuget.org).
   Register a resource with a persistent volume, reference it from the `api`
   resource.
2. In `Synth.Core`, define a small store abstraction for `CodeChunk`, e.g.
   `ICodeChunkStore` with something like:
   `Task UpsertAsync(IEnumerable<CodeChunk> chunks)`,
   `Task<IReadOnlyList<(CodeChunk Chunk, float Score)>> SearchAsync(ReadOnlyMemory<float> queryVector, int limit)`,
   `Task<IReadOnlyList<CodeChunk>> GetByFileAsync(string relativePath)`.
   Keep it minimal — this is infrastructure plumbing, not the reranking logic
   (that's `SYNTH-11`).
3. Implement `QdrantCodeChunkStore : ICodeChunkStore` in `Synth.Api` (or
   `Synth.Core` if it can stay free of ASP.NET-specific concerns) using the
   official Qdrant client/vector-data integration, storing `EmbeddingText`'s
   vector plus the chunk's fields as payload.
4. Implement `LocalCodeChunkStore : ICodeChunkStore` — an in-memory
   dictionary + brute-force cosine similarity search (fine for tests/small
   local use, mirrors Sonar's `Local` store). No Docker/network needed.
5. Register whichever store is configured (Qdrant if a connection is
   present, else Local — mirror the same "connection string present → real
   backend, else fallback" decision used for Mongo in `SYNTH-3` and Ollama in
   `SYNTH-8`).
6. Add tests against `LocalCodeChunkStore` covering upsert + search
   (including that closer vectors rank higher) + get-by-file. No live Qdrant
   required.

## Acceptance
`dotnet build`/`dotnet test` on `src/Synth.slnx` stay green, and
`AppHost.cs` references Qdrant (mirrors the frontmatter `acceptance_command`).
No live Qdrant/Docker connection required for the automated tests to pass.

## Out of scope
- The indexing pipeline that actually chunks+embeds+upserts a real codebase — `SYNTH-10`.
- Reranking/query-expansion search logic — `SYNTH-11`.
- Vue client.
