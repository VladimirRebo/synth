---
id: SYNTH-11
summary: "Search with reranking + basic RU->EN query expansion"
status: open
acceptance_command: "dotnet build src/Synth.slnx --nologo -v q && dotnet test src/Synth.slnx --nologo -v q && grep -rq 'CodeSearchService' src/Synth.Core"
acceptance_criterion: ""
boundaries: "Only add the CodeSearchService (over-fetch + rerank + dedup) and a small query-expansion helper, plus tests. Do not touch the indexing pipeline, chunkers, or storage internals beyond calling ICodeChunkStore.SearchAsync. No HTTP endpoint required. No Vue client. Tests must not require live Ollama/Qdrant/Docker."
limits: "max_iterations=30; max_minutes=150"
labels: [backend, rag-core, search, reranking]
---

# SYNTH-11: Search with reranking (final phase-2 task)

## Context
Ported from Sonar's search pipeline (Jarvis wiki `rag-search-reranking`,
`sonar-core`). Closes out phase 2 (RAG core, GitHub issue #3) by giving the
indexed chunks (`SYNTH-6`..`SYNTH-10`) an actual search API with better-than-
raw-cosine ranking.

## What to do
1. Add a small `QueryExpander` to `Synth.Core`: a hardcoded RU→EN dictionary
   (a modest set of common code/programming terms is enough — don't try to
   be exhaustive) that, when the query contains Cyrillic characters, appends
   English translations for matched terms to the query text before
   embedding. Keep it simple.
2. Add `CodeSearchService` to `Synth.Core` with
   `Task<IReadOnlyList<CodeChunk>> SearchAsync(string query, int limit, CancellationToken ct = default)`
   implementing:
   - Expand the query via `QueryExpander`.
   - Embed the expanded query via `IEmbeddingGenerator`.
   - Over-fetch: call `ICodeChunkStore.SearchAsync` with `limit * 4` candidates.
   - Rerank each candidate: `score = vectorScore * chunkTypeWeight * keywordBoost`.
     - `chunkTypeWeight`: classes/interfaces/records/structs ≈ 1.15, methods/
       constructors ≈ 1.10, properties ≈ 0.95, method-body ≈ 0.90, markdown/
       other non-code ≈ 0.85-0.90 (use judgment for chunk types that don't
       exist yet — just don't make weights identical across all types, that
       defeats the point).
     - `keywordBoost`: token overlap between the (non-expanded) query and
       `ClassName`+`MethodName`, with **camelCase-aware tokenization** (e.g.
       `GetUserById` → `[Get, User, By, Id]`) — this is the part worth
       getting right, mirror Sonar's approach rather than a naive substring
       match.
   - Dedup: at most 2 hits per method name, at most 1 per
     `RelativePath::ClassName.MethodName`.
   - Sort by final score, truncate to `limit`.
3. Add tests using a fake `ICodeChunkStore` (or `LocalCodeChunkStore` seeded
   with known chunks + a deterministic fake embedding generator) that verify:
   the chunk-type weighting changes ranking order for otherwise-equal vector
   scores, the camelCase keyword boost correctly matches
   `GetUserById`-style names, and the dedup limits are enforced. No live
   Ollama/Qdrant/Docker required.
4. Register `CodeSearchService`/`QueryExpander` in DI.

## Acceptance
`dotnet build`/`dotnet test` on `src/Synth.slnx` stay green, and
`CodeSearchService` exists under `src/Synth.Core` (mirrors the frontmatter
`acceptance_command`).

## Out of scope
- HTTP endpoint or MCP tool exposing search (future phase — MCP layer, issue #4).
- Cross-encoder reranking / LLM-based query expansion (noted as an open question, not required now).
- Vue client.
