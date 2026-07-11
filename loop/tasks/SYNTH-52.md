---
id: SYNTH-52
summary: "Create Synth.Domain, move pure domain types/interfaces into it (issue #82, slice 1/many)"
status: open
acceptance_command: "grep -q 'Synth.Domain/Synth.Domain.csproj' Synth.slnx && test -d src/Synth.Domain && ! grep -q 'MongoDB\|Qdrant\|Grpc' src/Synth.Domain/Synth.Domain.csproj"
acceptance_criterion: ""
boundaries: "This is slice 1 of a multi-task restructuring (issue #82: Domain/Application/Infrastructure/Api layering). Only move the files listed below — do not touch IndexingPipeline, CodeSearchService, GitRepoService, or any concrete store implementation (Qdrant/Mongo/InMemory/File) in this task, those move in later slices. Do not add Controllers or CQRS yet. Do not delete Synth.Core or Synth.Api — they keep existing, just gain a ProjectReference to the new Synth.Domain and lose the files that moved out."
limits: "max_iterations=30; max_minutes=150"
labels: [backend, refactor, architecture]
---

# SYNTH-52: Create Synth.Domain (issue #82, slice 1)

## Context
Issue #82 restructures the solution into `Synth.Domain`/`Synth.Application`/`Synth.Infrastructure`/
`Synth.Api` layers. This is far too large for one task — it touches nearly every file in the
solution — so it's broken into a sequence of small, independently buildable slices. This is slice 1:
create `Synth.Domain` and move into it every pure domain type/interface that exists today, whether
it currently lives in `Synth.Core` or `Synth.Api`. Nothing else changes — `IndexingPipeline`,
`CodeSearchService`, `GitRepoService`, and every concrete store implementation stay exactly where
they are for now; they move in later slices (Application and Infrastructure respectively).

`Synth.Domain` must end up with **no external package dependency beyond what the domain types
themselves strictly need** — `Microsoft.Extensions.AI.Abstractions` (for the embedding vector type
used by `CodeChunk`) and `Microsoft.Extensions.Options` (for `IOptionsMonitor<T>` used by option
records, if actually needed by a moved type) are fine. No MongoDB driver, no Qdrant client, no HTTP
client packages — those belong to Infrastructure once it exists.

**Namespace convention for this whole multi-slice restructuring**: each moved type's namespace
becomes `Synth.Domain.*` (matching its new project), not `Synth.Core.*`/`Synth.Api.*`. Keep the same
sub-namespace shape it had before (e.g. `Synth.Core.Graph.CallEdge` → `Synth.Domain.Graph.CallEdge`,
`Synth.Api.Vcs.IRepositoryRegistry` → `Synth.Domain.Vcs.IRepositoryRegistry`). Every consumer's
`using` directive needs updating accordingly — this is the bulk of the diff, there's no way around
it for a namespace rename, but each individual change is mechanical (find/replace the old
fully-qualified namespace with the new one, project-wide).

## What to do
1. Create `src/Synth.Domain/Synth.Domain.csproj` (net10.0, `Nullable`/`ImplicitUsings` enabled,
   matching `Synth.Core.csproj`'s style). Add only the package references actually needed by the
   moved types (check each type's current file for what it uses — most need nothing beyond the SDK).
2. Move these files from `src/Synth.Core/` into `src/Synth.Domain/` (same relative sub-path,
   namespace renamed `Synth.Core.*` → `Synth.Domain.*`):
   - `ChunkType.cs`
   - `CodeChunk.cs`
   - `CollectionNames.cs`
   - `DimensionMismatchException.cs`
   - `Graph/CallEdge.cs`
   - `Graph/ICallSiteExtractor.cs`
   - `Graph/ICodeGraphStore.cs`
   - `ICodeChunkStore.cs`
   - `IFileChunker.cs`
   - `Vcs/GitProvider.cs`
   - `Vcs/RepoUrlInfo.cs`
   - `Vcs/VcsOptions.cs`
   - `Embeddings/EmbeddingOptions.cs`
3. Move these files from `src/Synth.Api/` into `src/Synth.Domain/` (same relative sub-path minus
   the `Synth.Api/` prefix, namespace renamed `Synth.Api.*` → `Synth.Domain.*`) — each is a pure
   interface file (verified: no concrete implementation bundled in the same file):
   - `Vcs/IRepositoryRegistry.cs` (also contains the `RepositoryEntry` record — moves with it)
   - `Logging/ILogEntryStore.cs`
   - `Configuration/IConfigStore.cs`
4. `Synth.Core.csproj` and `Synth.Api.csproj` each add a `<ProjectReference>` to the new
   `Synth.Domain.csproj`. `Synth.Chunkers.CSharp.csproj`/`Synth.Chunkers.TsVue.csproj` currently
   reference `Synth.Core` for `IFileChunker`/`ICallSiteExtractor` — since those interfaces now live
   in `Synth.Domain`, point the chunker projects' `ProjectReference` at `Synth.Domain` directly
   instead (cleaner than relying on transitive project references, and matches what they actually
   use). Same check for `Synth.Mcp.Stdio.csproj` and both test projects — add a direct
   `Synth.Domain` reference wherever a moved type is used directly (not just transitively through
   `Synth.Core`/`Synth.Api`).
5. Fix every `using Synth.Core...`/`using Synth.Api...` (and fully-qualified references) across the
   whole solution — `src/`, `tests/` — that now needs to become `using Synth.Domain...` for the
   moved types. This touches many files; that's expected for a namespace-rename slice.
6. Add `src/Synth.Domain/Synth.Domain.csproj` to `Synth.slnx` (top-level project entry, no folder
   grouping needed — or match whatever grouping convention already exists, your call).
7. Move each moved type's corresponding test file(s) if any exist and are purely about the moved
   type in isolation (e.g. a test file that only tests `RepoUrlInfo`'s parsing, with no dependency
   on `IndexingPipeline`/stores/etc.) into a new `tests/Synth.Domain.Tests/Synth.Domain.Tests.csproj`
   (same xunit/test-sdk package set as `Synth.Core.Tests.csproj`). If a test file mixes domain-type
   tests with tests of something not moving yet (e.g. `IndexingPipelineTests.cs` uses `CodeChunk`
   but is fundamentally testing `IndexingPipeline`, which stays put), leave it where it is — don't
   split a single test file's assertions across two projects. Add the new test project to
   `Synth.slnx` under the existing `/Tests/` folder.

## Acceptance
`dotnet build`/`dotnet test` on `Synth.slnx` stay green — full solution, not just the new project.
`Synth.Domain.csproj` exists, is registered in `Synth.slnx`, and references neither MongoDB nor
Qdrant nor gRPC packages (grep-checked). Indexing pipeline `IndexingPipeline` compiles and works —
if the maker's changes broke `IndexingPipeline`'s ability to use the moved types (`CodeChunk`,
`ICodeChunkStore`, etc.), it did the namespace migration incorrectly.

## Out of scope
- Moving `IndexingPipeline`, `CodeSearchService`, `QueryExpander`, `IdentifierTokenizer`,
  `SourceUrlBuilder`, `IIndexJobTracker`, `IOllamaPullTracker`, `IndexJobStatus` — these are
  Application-layer per the issue's own classification, they move in the next slice.
- Moving `GitRepoService` or any concrete store implementation (Qdrant/Mongo/InMemory/File) — these
  are Infrastructure, they move in a later slice.
- Introducing CQRS, Controllers, or any Api-layer change.
- Renaming `Synth.Core`/`Synth.Api` themselves, or removing `Synth.Core` even if it ends up smaller
  — it still holds Application-layer code until the next slice moves that out too.
