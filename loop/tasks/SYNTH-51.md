---
id: SYNTH-51
summary: "Extract CSharpRoslynChunker and TsVueChunker into their own .NET projects"
status: open
acceptance_command: "grep -q 'Synth.Chunkers.CSharp' Synth.slnx && grep -q 'Synth.Chunkers.TsVue' Synth.slnx && ! grep -q 'Microsoft.CodeAnalysis.CSharp' src/Synth.Core/Synth.Core.csproj"
acceptance_criterion: ""
boundaries: "Pure structural extraction — do not change IFileChunker/ICallSiteExtractor, do not change how IndexingPipeline dispatches chunkers (the `chunker is ICallSiteExtractor` pattern-match in IndexingPipeline.cs stays as-is, no new registration needed for call-graph extraction), do not add any new language chunker, do not build a runtime plugin-loading system. Solution file is now at repo root (Synth.slnx, moved in a prior task) — add the new projects there, not under a stale src/Synth.slnx path."
limits: "max_iterations=25; max_minutes=120"
labels: [backend, refactor]
---

# SYNTH-51: Extract chunkers into their own .NET projects

## Context
Issue #81. `src/Synth.Core/CSharpRoslynChunker.cs` and `src/Synth.Core/TsVueChunker.cs` currently
live directly inside `Synth.Core`, alongside the core abstractions (`IFileChunker`,
`ICallSiteExtractor`, `CodeChunk`, etc.). `CSharpRoslynChunker` additionally drags a direct
`<PackageReference Include="Microsoft.CodeAnalysis.CSharp">` onto `Synth.Core.csproj` itself, which
every consumer of `Synth.Core` (Synth.Api, both test projects, the stdio MCP host) pulls in
transitively whether or not it needs C# parsing. As more language chunkers get added over time
(Java, Go, Python, etc.), each should live in its own focused project with only the dependencies
that language's parsing needs — not pile into a shared "every chunker's dependencies" project. This
task retroactively extracts the two chunkers that exist today to establish that pattern, so the next
chunker added just follows it rather than inventing a new one.

The abstractions (`IFileChunker` in `Synth.Core/IFileChunker.cs`, `ICallSiteExtractor` in
`Synth.Core/Graph/ICallSiteExtractor.cs`) correctly belong in `Synth.Core` and stay there — only the
concrete chunker *implementations* move out.

`IndexingPipeline` (`Synth.Core/IndexingPipeline.cs:194`) dispatches call-graph extraction via
`if (chunker is ICallSiteExtractor extractor)` — a runtime pattern-match against whatever's
registered as `IFileChunker`, not a compile-time reference to `CSharpRoslynChunker` specifically.
Nothing about that dispatch needs to change.

The solution file `Synth.slnx` lives at the repo root now (moved out of `src/` in a prior task),
with virtual `/Api/` and `/Tests/` solution folders already established — follow that existing
grouping convention rather than inventing a new one.

## What to do
1. Create `src/Synth.Chunkers.CSharp/Synth.Chunkers.CSharp.csproj` (net10.0, same
   `Nullable`/`ImplicitUsings` settings as `Synth.Core.csproj`), referencing `Synth.Core` and
   carrying the `Microsoft.CodeAnalysis.CSharp` package reference. Move `CSharpRoslynChunker.cs`
   into it (namespace can stay `Synth.Core` or become `Synth.Chunkers.CSharp` — pick whichever
   keeps the diff smaller and update call sites accordingly; note `IndexingServiceExtensions.cs`
   references the type by name and will need its `using`/namespace updated either way).
2. Create `src/Synth.Chunkers.TsVue/Synth.Chunkers.TsVue.csproj` the same way, referencing only
   `Synth.Core` (no extra package deps needed). Move `TsVueChunker.cs` into it.
3. Remove the `Microsoft.CodeAnalysis.CSharp` `<PackageReference>` from `Synth.Core.csproj` — this
   is the acceptance check's core signal that the extraction actually happened, not just that new
   projects exist alongside the old code.
4. `Synth.Api.csproj` adds `<ProjectReference>`s to both new chunker projects (wherever DI
   composition happens today — check what `Synth.Api.csproj` already references). Update
   `IndexingServiceExtensions.cs`'s two `AddSingleton<IFileChunker, T>()` lines' `using` directives
   if the namespace changed; the registration calls themselves barely change.
5. Check `Synth.Mcp.Stdio.csproj` and `Synth.Api.Tests.csproj` (via its existing
   `Synth.Mcp.Stdio`/`Synth.Api` references) for whether they need a direct new `ProjectReference`
   too, or whether they only need the chunkers transitively through `Synth.Api`/`Synth.Mcp.Stdio` —
   add direct references only where actually needed to build.
6. Move `tests/Synth.Core.Tests/CSharpRoslynChunkerTests.cs` into a new
   `tests/Synth.Chunkers.CSharp.Tests/Synth.Chunkers.CSharp.Tests.csproj` (same xunit/test-sdk
   package set as `Synth.Core.Tests.csproj`, referencing `Synth.Chunkers.CSharp`), and
   `tests/Synth.Core.Tests/TsVueChunkerTests.cs` into
   `tests/Synth.Chunkers.TsVue.Tests/Synth.Chunkers.TsVue.Tests.csproj` likewise — each chunker's
   tests belong in that chunker's own test project, matching the split you're creating for the
   production code. `Synth.Core.Tests` keeps `IndexingPipelineTests.cs` and everything else
   unrelated to a specific chunker; if `IndexingPipelineTests.cs` itself instantiates
   `CSharpRoslynChunker`/`TsVueChunker` directly, add the needed `ProjectReference`(s) to
   `Synth.Core.Tests.csproj` rather than duplicating test fixtures.
7. Add all four new projects (2 production, 2 test) to `Synth.slnx` — production projects under the
   existing top-level project list (or a new folder grouping if that reads more clearly, your call),
   test projects under the existing `/Tests/` folder alongside `Synth.Core.Tests`/`Synth.Api.Tests`.

## Acceptance
`dotnet build`/`dotnet test` on `Synth.slnx` stay green (all existing chunker tests still pass, just
from their new project locations). `Synth.Core.csproj` no longer references
`Microsoft.CodeAnalysis.CSharp`. Both new chunker projects are registered in `Synth.slnx`. Indexing
a real directory still works end-to-end (chunker dispatch is unchanged, this is purely a project-
boundary move).

## Out of scope
- Adding any new language chunker (Java/Go/Python/etc.).
- Changing `IFileChunker`/`ICallSiteExtractor` or `IndexingPipeline`'s dispatch logic.
- A runtime plugin-loading system (loading chunker assemblies from a directory at runtime) — this
  stays compile-time `ProjectReference` composition.
