---
id: SYNTH-17
summary: "Multi-collection support across the store, indexing pipeline and search"
status: done
acceptance_command: "dotnet build src/Synth.slnx --nologo -v q && dotnet test src/Synth.slnx --nologo -v q"
acceptance_criterion: ""
boundaries: "Only thread a collection identifier through the existing store/pipeline/search/endpoint types. Do not add git cloning, a repository registry, or any client changes — those are later tasks (SYNTH-18, SYNTH-19). Do not change the chunking/reranking logic itself."
limits: "max_iterations=30; max_minutes=150"
labels: [backend, vcs, multi-collection]
---

# SYNTH-17: Multi-collection support across the store, indexing pipeline and search

## Context
Synth is about to gain the ability to index multiple external repositories (GitHub/GitLab)
in parallel — see GitHub issue #5. Right now `ICodeChunkStore` (`src/Synth.Core/ICodeChunkStore.cs`)
has no notion of a "collection": `QdrantCodeChunkStore` hardcodes a single Qdrant collection name
(`code_chunks`, `src/Synth.Api/Storage/QdrantCodeChunkStore.cs`), and `LocalCodeChunkStore`
(`src/Synth.Core/LocalCodeChunkStore.cs`) is a single flat in-memory store. Every indexed
directory's chunks land in the same bucket, and `CodeSearchService.SearchAsync`
(`src/Synth.Core/CodeSearchService.cs`) can't scope a search to one repo. This task lays the
foundation so later tasks (git cloning, a repository registry) can index N repos side by side
without their chunks colliding or search mixing results across repos.

Existing local-path indexing (`POST /index { "path": "..." }`) must keep working exactly as it
does today, using a well-known default collection name — this task is pure plumbing, not a
behavior change for current users.

## What to do
1. Add a small shared constant, e.g. `public static class CollectionNames { public const string Default = "default"; }`
   in `Synth.Core`, so every call site agrees on the name used when no explicit collection is given.
2. Extend `ICodeChunkStore` (`src/Synth.Core/ICodeChunkStore.cs`) so `UpsertAsync`, `SearchAsync`
   and `GetByFileAsync` each take a `string collection` parameter (chunks upserted/searched/fetched
   under one collection must never be visible from another).
3. Update `LocalCodeChunkStore` to key its storage by collection (e.g. an outer
   `ConcurrentDictionary<string, ConcurrentDictionary<string, CodeChunk>>`), so two different
   collection names are fully isolated from each other.
4. Update `QdrantCodeChunkStore` (`src/Synth.Api/Storage/QdrantCodeChunkStore.cs`) to use the
   passed-in `collection` as the actual Qdrant collection name instead of the hardcoded
   `CollectionName` const (keep collection creation lazy on first upsert, as today). Qdrant
   collection names must be non-empty; sanitize if needed (lowercase, replace anything that isn't
   `[a-z0-9_-]` with `-`) — this sanitization will matter once SYNTH-18 derives names from git URLs.
5. Update `IndexingPipeline.IndexDirectoryAsync` (`src/Synth.Core/IndexingPipeline.cs`) to take a
   `string collection` parameter and pass it through to the store.
6. Update `CodeSearchService.SearchAsync` (`src/Synth.Core/CodeSearchService.cs`) to take a
   `string collection` parameter and pass it through to the store.
7. Update call sites to pass `CollectionNames.Default` for now (real per-repo collection names
   arrive in SYNTH-19):
   - `src/Synth.Api/Indexing/IndexingEndpoints.cs` (`POST /index`)
   - `src/Synth.Api/Search/SearchEndpoints.cs` (`GET /search`) — also accept an optional
     `?collection=` query parameter, defaulting to `CollectionNames.Default`, so the REST API is
     ready for SYNTH-20's collection picker without another endpoint-shape change later.
   - `src/Synth.Api/Mcp/CodeSearchTool.cs` (`search_code` MCP tool) — likewise accept an optional
     `collection` parameter (with a `[Description]` explaining it defaults to the main indexed
     codebase), defaulting to `CollectionNames.Default`.
8. Update every existing test that calls these APIs (`CodeSearchServiceTests`,
   `IndexingPipelineTests`, store-level tests, `IndexingEndpointTests`, `SearchEndpointTests`,
   etc.) to pass `CollectionNames.Default` (or an equivalent literal) so the suite stays green.
9. Add at least one new test proving isolation: upsert distinct chunks into two different
   collections (e.g. `"repo-a"` and `"repo-b"`) via `LocalCodeChunkStore`, then assert a search in
   one collection never returns the other's chunks, and `GetByFileAsync` likewise stays scoped.

## Acceptance
`dotnet build`/`dotnet test` on `src/Synth.slnx` stay green, existing REST/MCP search and index
behavior is unchanged when no collection is specified (defaults to `"default"`), and the new
cross-collection isolation test passes.

## Out of scope
- Git clone/fetch, URL parsing, auth tokens — `SYNTH-18`.
- `POST /index` accepting a `repoUrl`, the repository registry, `GET /repositories` — `SYNTH-19`.
- Any Vue client change (repo-URL input, collection picker) — done directly after the backend
  lands, not as a loop task.
