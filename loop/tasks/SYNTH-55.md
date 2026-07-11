---
id: SYNTH-55
summary: "Create Synth.Infrastructure, move Storage + Graph store implementations into it (issue #82, slice 3/many)"
status: open
acceptance_command: "grep -q 'Synth.Infrastructure/Synth.Infrastructure.csproj' Synth.slnx && test -f src/Synth.Infrastructure/Storage/QdrantCodeChunkStore.cs && ! test -f src/Synth.Core/LocalCodeChunkStore.cs"
acceptance_criterion: ""
boundaries: "This is slice 3 of issue #82 (slices 1-2, Synth.Domain and Synth.Application, already merged). Only move the files listed below. Do not touch Configuration/, Embeddings/, Vcs/ (registry/GitRepoService), or Logging/Health files in Synth.Api or Synth.Core — those are separate later slices. Do NOT remove Aspire.Qdrant.Client or Aspire.MongoDB.Driver package references from Synth.Api.csproj — Health (Qdrant) and Logging/Vcs (Mongo) still reference them directly until their own later slices land. Do not touch CallGraphTool.cs or CallGraphEndpoints.cs in src/Synth.Api/Graph/ — those are Api-layer (MCP tool + Minimal API endpoint), they stay put."
limits: "max_iterations=30; max_minutes=150"
labels: [backend, refactor, architecture]
---

# SYNTH-55: Create Synth.Infrastructure — Storage + Graph (issue #82, slice 3)

## Context
Continuing issue #82. Slices 1-2 (`SYNTH-52` Synth.Domain, `SYNTH-54` Synth.Application) are merged.
This slice creates `Synth.Infrastructure` — every concrete implementation of a `Synth.Domain`
interface, owning the external package dependencies those implementations need — and moves in the
first batch: the two `ICodeChunkStore` implementations (Qdrant + local in-memory fallback) and the
two `ICodeGraphStore` implementations (Mongo + in-memory fallback), plus their DI-wiring extension
methods. Infrastructure will be built up over several more slices (Configuration, Embeddings, Vcs,
Logging+Health) after this one.

The DI extension methods (`AddSynthVectorStore`, `AddSynthCodeGraph`) are typed as
`this IHostApplicationBuilder builder` — that's a plain `Microsoft.Extensions.Hosting.Abstractions`
type, not `WebApplicationBuilder`, so they work fine from a plain `Microsoft.NET.Sdk` class library
(no need for `Microsoft.NET.Sdk.Web` or an ASP.NET Core framework reference). Per issue #82's own
open question ("whether `AddSynth*` DI composition extension methods live in Infrastructure or stay
in Api — pick whichever reads cleaner"): this slice moves them into `Synth.Infrastructure`,
co-located next to what they register, since `Program.cs` in `Synth.Api` just calls
`builder.AddSynthVectorStore()`/`builder.AddSynthCodeGraph()` either way.

**Namespace convention** (same as prior slices): moved types get `Synth.Infrastructure.*`
namespace, keeping the same sub-namespace shape (e.g. `Synth.Api.Storage.QdrantCodeChunkStore` →
`Synth.Infrastructure.Storage.QdrantCodeChunkStore`, `Synth.Core.LocalCodeChunkStore` →
`Synth.Infrastructure.Storage.LocalCodeChunkStore` — note it moves into the `Storage`
sub-namespace/folder even though it wasn't there before, to sit next to `QdrantCodeChunkStore` as
the other `ICodeChunkStore` implementation).

## What to do
1. Create `src/Synth.Infrastructure/Synth.Infrastructure.csproj` (net10.0, same
   `Nullable`/`ImplicitUsings` style as the other projects), referencing `Synth.Domain`. Add
   `Aspire.Qdrant.Client` and `Aspire.MongoDB.Driver` package references (copy the exact versions
   currently pinned in `Synth.Api.csproj`) — these are needed by the moved store implementations.
2. Move from `src/Synth.Api/Storage/` into `src/Synth.Infrastructure/Storage/`:
   - `QdrantCodeChunkStore.cs`
   - `VectorStoreServiceExtensions.cs`
3. Move `src/Synth.Core/LocalCodeChunkStore.cs` into `src/Synth.Infrastructure/Storage/LocalCodeChunkStore.cs`.
4. Move from `src/Synth.Api/Graph/` into `src/Synth.Infrastructure/Graph/`:
   - `InMemoryCodeGraphStore.cs`
   - `MongoCodeGraphStore.cs`
   - `CodeGraphServiceExtensions.cs`
   (leave `CallGraphTool.cs` and `CallGraphEndpoints.cs` in `src/Synth.Api/Graph/` — they're
   Api-layer, not moving).
5. `Synth.Api.csproj` adds a `<ProjectReference>` to `Synth.Infrastructure.csproj`. `Synth.Core.csproj`
   loses nothing (it never had these files as its primary content beyond `LocalCodeChunkStore.cs`,
   which is now gone from it). Check `Synth.Mcp.Stdio.csproj` and the test projects for direct
   references needed.
6. Fix every `using Synth.Api.Storage`/`using Synth.Api.Graph` (for the moved Storage/Graph-store
   files specifically — not `CallGraphTool`/`CallGraphEndpoints`, which keep their existing
   namespace) and `using Synth.Core` (for `LocalCodeChunkStore`) across the whole solution that now
   needs `using Synth.Infrastructure.Storage`/`using Synth.Infrastructure.Graph`.
7. Add `src/Synth.Infrastructure/Synth.Infrastructure.csproj` to `Synth.slnx`.
8. Move each moved type's test file(s) into a new
   `tests/Synth.Infrastructure.Tests/Synth.Infrastructure.Tests.csproj` (same xunit/test-sdk package
   set as the other test projects, plus whatever Qdrant/Mongo test-fixture packages the moved tests
   currently need — check `tests/Synth.Api.Tests/Storage/QdrantCodeChunkStoreTests.cs` and
   `tests/Synth.Api.Tests/CodeGraphStoreTests.cs` and any `LocalCodeChunkStore`-specific test in
   `tests/Synth.Core.Tests/`). Add the new test project to `Synth.slnx` under `/Tests/`.

## Acceptance
`dotnet build`/`dotnet test` on `Synth.slnx` stay green — full solution. `Synth.Infrastructure.csproj`
exists, is registered in `Synth.slnx`, contains `Storage/QdrantCodeChunkStore.cs`.
`LocalCodeChunkStore.cs` no longer exists under `src/Synth.Core/`. `Synth.Api.csproj` still has its
`Aspire.Qdrant.Client`/`Aspire.MongoDB.Driver` package references (needed by Health/Logging/Vcs
until their own slices land).

## Out of scope
- Configuration, Embeddings, Vcs (GitRepoService/registries), Logging, Health — all separate later
  Infrastructure slices.
- Removing package references from `Synth.Api.csproj`.
- `CallGraphTool.cs`/`CallGraphEndpoints.cs` — Api-layer, not moving.
- Introducing CQRS, Controllers, or any other Api-layer change.
