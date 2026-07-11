---
id: SYNTH-48
summary: "Search across all collections at once"
status: open
acceptance_command: "dotnet build src/Synth.slnx --nologo -v q && dotnet test src/Synth.slnx --nologo -v q && grep -rq 'SearchAllCollectionsAsync' src/Synth.Core/CodeSearchService.cs"
acceptance_criterion: ""
boundaries: "Touch: src/Synth.Core/CodeSearchService.cs, src/Synth.Api/Search/SearchEndpoints.cs, src/Synth.Api/Mcp/CodeSearchTool.cs, src/Synth.Api/Mcp/CodeSearchResult.cs (or ScoredCodeChunk in Synth.Core if adding a Collection field there is cleaner — your call), client SearchPanel.vue/SearchResultItem.vue/api.ts, and tests. Do NOT re-embed the query once per collection — embed it exactly once and reuse the vector across every collection's store search, for both cost and latency reasons. Requires IRepositoryRegistry to enumerate known collections, so CodeSearchService (currently Synth.Core, no registry dependency) needs that injected — check whether this pulls Synth.Api-only types into Synth.Core (IRepositoryRegistry currently lives in Synth.Api.Vcs) and resolve the layering cleanly (e.g. take the collection list as a parameter from the caller, which already has registry access, rather than injecting the registry into Synth.Core)."
limits: "max_iterations=30; max_minutes=150"
labels: [backend, client, search]
---

# SYNTH-48: Search across all collections at once

## Context
Part of issue #58. `SearchPanel.vue`'s collection picker requires choosing a single collection (or
the default) before searching. In practice, the common case for a personal multi-repo tool is not
remembering *which* indexed repo a piece of code lives in — searching everywhere at once and seeing
which collection each result came from is more useful than Sonar's side-by-side "compare mode"
(separate result columns per collection) — this task is about **merging into one ranked list**, not
a compare view.

`CodeSearchService.SearchAsync(collection, query, limit, ct)` (`src/Synth.Core/CodeSearchService.cs`)
today: expands the query, embeds it once, over-fetches from `ICodeChunkStore.SearchAsync` for the
*one* given collection, then reranks (chunk-type weight × keyword boost) and deduplicates before
truncating to `limit`. The reranking/dedup logic itself doesn't care which collection a candidate
came from — it operates on `ScoredCodeChunk` values.

`IRepositoryRegistry` (`src/Synth.Api/Vcs/IRepositoryRegistry.cs`) is the source of "what collections
exist" — but it lives in `Synth.Api`, while `CodeSearchService` lives in `Synth.Core` (no
`Synth.Api` dependency today, check the `.csproj` references before assuming one can be added
casually). Resolve this the simple way: don't inject the registry into `Synth.Core` — have the
*caller* (the REST endpoint / MCP tool, both already in `Synth.Api` with registry access) pass in
the list of collection names to search, and give `CodeSearchService` a method that takes an
`IReadOnlyList<string> collections` parameter instead of a single `string collection`.

## What to do
1. Add `Task<IReadOnlyList<ScoredCodeChunk>> SearchAllCollectionsAsync(IReadOnlyList<string> collections, string query, int limit, CancellationToken cancellationToken = default)`
   to `CodeSearchService`: embed the query exactly once (reuse the existing expand+embed logic —
   extract it into a small private helper shared with `SearchAsync` if it isn't already factored out,
   to avoid duplicating that step), then for each collection call `_store.SearchAsync(collection, queryVector, limit * OverFetchFactor, cancellationToken)`,
   tag each resulting chunk with which collection it came from, merge every collection's candidates
   into one set, and run them through the *same* rerank/dedup/take(limit) pipeline `SearchAsync`
   already uses (extract that shared tail into a private helper too, rather than duplicating the
   scoring/dedup code a second time).
2. `ScoredCodeChunk` (or wherever makes most sense — your call) needs to carry which collection each
   result came from for this mode, since the whole point is showing "found in collection X". Add a
   `Collection` field somewhere in the chain from `ScoredCodeChunk` through to `CodeSearchResult`
   (nullable/empty-string default is fine for the existing single-collection `SearchAsync` path,
   which doesn't need to populate it — only the new all-collections path does).
3. REST: extend `GET /search`'s `collection` query param — when it's a specific sentinel value (e.g.
   omit it entirely AND pass a new explicit flag like `?allCollections=true`, or reserve a value like
   `collection=*` — pick whichever reads more clearly as an explicit API, don't silently overload
   "collection omitted" since that already means "use the default collection" today) fan out via
   `IRepositoryRegistry.ListAsync()` to get every known collection name and call the new method.
4. MCP: give `search_code` (`CodeSearchTool.cs`) the same all-collections option via its `collection`
   parameter (same sentinel convention as REST, keep them consistent).
5. Client: add an "All collections" option to `SearchPanel.vue`'s collection picker (reasonable to
   make it the default selection, since it's the most generally useful case for someone who doesn't
   remember which repo has what) — each result row in `SearchResultItem.vue` shows which collection
   it came from when in this mode (omit/hide that label in the existing single-collection mode to
   avoid visual noise there).
6. Tests: `CodeSearchService` test proving a query against two populated collections returns results
   from both, ranked together, with only one embedding-generator call total (assert call count, same
   style `IndexingPipelineTests.cs` already uses via `FakeEmbeddingGenerator.CallCount`); REST/MCP
   tests for the new all-collections parameter; a client test for the "All collections" picker option
   and the per-result collection label.

## Acceptance
`dotnet build`/`dotnet test` stay green, `npm test`/`npm run build` stay green. Searching in
all-collections mode queries every known collection, embeds the query exactly once, merges and
reranks results from every collection into a single ranked list, and each result indicates which
collection it came from. The existing single-collection search path is unchanged.

## Out of scope
- Sonar's side-by-side compare mode (separate columns per collection) — this task merges into one
  ranked list instead; compare mode can be a separate future item if it turns out to matter.
- Any change to per-collection ranking weights/dedup rules — same scoring logic, just applied across
  a merged candidate set.
