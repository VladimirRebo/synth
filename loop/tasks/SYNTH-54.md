---
id: SYNTH-54
summary: "Create Synth.Application, move use-case orchestration into it (issue #82, slice 2/many)"
status: open
acceptance_command: "grep -q 'Synth.Application/Synth.Application.csproj' Synth.slnx && test -f src/Synth.Application/IndexingPipeline.cs && ! test -f src/Synth.Core/IndexingPipeline.cs"
acceptance_criterion: ""
boundaries: "This is slice 2 of issue #82's multi-task restructuring (slice 1, Synth.Domain, already merged). Only move the files listed below. Do not move GitRepoService or LocalCodeChunkStore — both stay in Synth.Core for now, they are Infrastructure per the issue's own classification and move in a later slice. Do not add Controllers or CQRS yet. Do not delete Synth.Core — it still holds GitRepoService and LocalCodeChunkStore until the Infrastructure slice."
limits: "max_iterations=30; max_minutes=150"
labels: [backend, refactor, architecture]
---

# SYNTH-54: Create Synth.Application (issue #82, slice 2)

## Context
Continuing issue #82's layered restructuring. Slice 1 (`SYNTH-52`, merged) created `Synth.Domain`
with the pure domain types/interfaces. This slice creates `Synth.Application` — use-case
orchestration that depends only on `Synth.Domain`'s interfaces, never on concrete infrastructure —
and moves the application-layer services into it.

Two files currently in `Synth.Core` are explicitly **not** part of this move even though they're in
the same project: `Vcs/GitRepoService.cs` (shells out to `git`, a concrete infrastructure-ish
implementation per the issue's own text) and `LocalCodeChunkStore.cs` (a concrete `ICodeChunkStore`
implementation). Both are Infrastructure and move in a later slice — leave them in `Synth.Core`
for now.

**Namespace convention** (same as slice 1): moved types get `Synth.Application.*` namespace, keeping
the same sub-namespace shape (e.g. `Synth.Core.Indexing.IndexJobStatus` →
`Synth.Application.Indexing.IndexJobStatus`, `Synth.Api.Indexing.IIndexJobTracker` →
`Synth.Application.Indexing.IIndexJobTracker`). Update every consumer's `using` accordingly.

## What to do
1. Create `src/Synth.Application/Synth.Application.csproj` (net10.0, same
   `Nullable`/`ImplicitUsings` style as the other projects), referencing `Synth.Domain` only (no
   direct reference to `Synth.Core`, `Synth.Api`, or any concrete infrastructure package —
   `Microsoft.Extensions.AI.Abstractions`/`Microsoft.Extensions.Options` are fine if the moved code
   actually needs them, check each file).
2. Move these files from `src/Synth.Core/` into `src/Synth.Application/` (namespace `Synth.Core.*` →
   `Synth.Application.*`):
   - `CodeSearchService.cs`
   - `IdentifierTokenizer.cs`
   - `IndexingPipeline.cs`
   - `QueryExpander.cs`
   - `Vcs/SourceUrlBuilder.cs`
   - `Indexing/IndexJobStatus.cs`
3. Move these files from `src/Synth.Api/` into `src/Synth.Application/` (namespace `Synth.Api.*` →
   `Synth.Application.*`) — each is an interface bundled with its single in-memory implementation in
   the same file (job-tracking is ephemeral process-lifetime state, not persisted, so there's no
   separate "concrete implementation" to leave behind in Infrastructure — the in-memory impl *is*
   the only impl and belongs with its interface here):
   - `Indexing/IIndexJobTracker.cs` (contains both `IIndexJobTracker` and `InMemoryIndexJobTracker`)
   - `Embeddings/IOllamaPullTracker.cs` (contains both `IOllamaPullTracker` and
     `InMemoryOllamaPullTracker`)
4. `Synth.Core.csproj` keeps its `Synth.Domain` reference (still needed by `GitRepoService`/
   `LocalCodeChunkStore`). `Synth.Api.csproj` adds a `<ProjectReference>` to
   `Synth.Application.csproj`. Check `Synth.Mcp.Stdio.csproj` and both API/Core test projects for
   whether they need a direct `Synth.Application` reference too (anything using
   `CodeSearchService`/`IndexingPipeline`/the trackers directly, not just transitively).
5. Fix every `using Synth.Core.Indexing`/`using Synth.Core...` (for the moved Core files) and
   `using Synth.Api.Indexing`/`using Synth.Api.Embeddings` (for the moved tracker files) across the
   whole solution that now needs `using Synth.Application...`.
6. `IndexingServiceExtensions.cs` and any other DI-wiring file in `Synth.Api` that registers
   `IndexingPipeline`/`IIndexJobTracker`/`IOllamaPullTracker` — registration calls themselves don't
   change, just their `using` directives.
7. Add `src/Synth.Application/Synth.Application.csproj` to `Synth.slnx`.
8. Move each moved type's test file(s) into a new
   `tests/Synth.Application.Tests/Synth.Application.Tests.csproj` (same xunit/test-sdk package set
   as the other test projects) where a test file is purely about a moved type — e.g.
   `IndexingPipelineTests.cs`, `CodeSearchServiceTests.cs`, `IdentifierTokenizerTests.cs`,
   `SourceUrlBuilderTests.cs`, `IndexJobTrackerTests.cs`, `OllamaPullTrackerTests.cs` (check exact
   current names/locations in `tests/Synth.Core.Tests/` and `tests/Synth.Api.Tests/`). If a test
   mixes concerns with something not moving yet (e.g. `IndexingPipelineTests.cs` exercises the full
   pipeline including `GitRepoService`/store implementations), keep it in place if splitting would
   be awkward — matching the same judgment call already made for `Synth.Core.Tests` in slice 1 (it
   currently keeps tests that mix moved and not-yet-moved concerns). Add the new test project to
   `Synth.slnx` under the existing `/Tests/` folder.

## Acceptance
`dotnet build`/`dotnet test` on `Synth.slnx` stay green — full solution. `Synth.Application.csproj`
exists, is registered in `Synth.slnx`, references only `Synth.Domain`. `IndexingPipeline.cs` no
longer exists under `src/Synth.Core/` (moved). `GitRepoService.cs` and `LocalCodeChunkStore.cs`
still exist under `src/Synth.Core/` (correctly left behind).

## Out of scope
- Moving `GitRepoService` or `LocalCodeChunkStore` — Infrastructure, later slice.
- Moving any concrete store implementation (Qdrant/Mongo/InMemory config-store/registry/log-store),
  `ConfigurableEmbeddingGenerator`, health checks, or logging sinks — all Infrastructure, later
  slices.
- Introducing CQRS, Controllers, or any Api-layer change.
- Deleting `Synth.Core` — it still holds Infrastructure-destined code.
