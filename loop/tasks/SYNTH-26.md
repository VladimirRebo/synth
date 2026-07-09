---
id: SYNTH-26
summary: "Extract call edges from C# source and wire them into IndexingPipeline"
status: open
acceptance_command: "dotnet build src/Synth.slnx --nologo -v q && dotnet test src/Synth.slnx --nologo -v q && grep -riq 'ICallSiteExtractor\\|ExtractCallSites' src/Synth.Core/"
acceptance_criterion: ""
boundaries: "Only add call-site extraction (syntax-heuristic, no semantic/compilation model) and wire it into IndexingPipeline so ICodeGraphStore (SYNTH-25) gets populated on every index run. Do not add the MCP/REST query tools (SYNTH-27). Do not add type-hierarchy edges (inheritance/interface implementation) — call-graph only, per issue #33. Do not touch the Vue client."
limits: "max_iterations=30; max_minutes=150"
labels: [backend, call-graph, roslyn]
---

# SYNTH-26: Extract call edges from C# source, wire into IndexingPipeline

## Context
`SYNTH-25` added `ICodeGraphStore` (storage only, nothing populates it yet). This task extracts
call-graph edges from the same Roslyn syntax trees `CSharpRoslynChunker` already parses for
chunking, and feeds them into the store on every index run. Per issue #33's architecture decision:
**syntax-heuristic extraction, not semantic-model resolution** — `Microsoft.CodeAnalysis.CSharp`
(parsing) is the only Roslyn dependency this repo has; adding `MSBuildWorkspace`/compilation would
require a loadable, NuGet-restored project and break indexing of freshly-cloned repos that don't
build, which is the entire point of `SYNTH-18`'s git-repo indexing. So: match invocation call sites
to known method/constructor names **by simple name**, within the same collection. This is
approximate (two unrelated classes with a same-named method both become candidate callees) — that's
an accepted, documented trade-off, not a bug to work around.

**Two-stage design (read carefully before implementing — this determines what's per-file vs.
collection-wide):**

1. **Per-file, syntax-only:** for each method/constructor body Roslyn already walks in
   `CSharpRoslynChunker.EmitMember`, find every `InvocationExpressionSyntax` in that member's body
   and record a "raw call site": the *caller's* qualified name (known immediately — it's the
   enclosing method/constructor being walked), the **invoked name** as written (just the simple
   identifier — for `Foo()` that's `Foo`; for `this.Foo()`/`obj.Foo()`/`Namespace.Type.Foo()` take
   the last identifier segment via the `MemberAccessExpressionSyntax.Name`), and the call site's
   source file + line. The *callee* is NOT resolved yet at this stage — a raw invocation
   ("something named `Foo` was called here") isn't yet known to correspond to which qualified
   method, because that requires seeing every method in the whole collection, not just this file.
2. **Collection-wide, after all files are chunked:** `IndexingPipeline` already walks every file in
   a collection one at a time (chunk → embed → upsert, streaming, not holding everything in memory).
   Extend it to also accumulate, per file, the lightweight raw call sites from stage 1 plus every
   method/constructor's own qualified name (small strings — do NOT hold full `CodeChunk` content in
   memory for this, that would defeat the point of the existing streaming design). Once the whole
   directory has been walked, resolve: for each raw call site, look up its invoked simple name
   against the accumulated qualified-name index; every match becomes one `CallEdge` (an invoked name
   matching multiple qualified methods emits one edge per match — approximate, as decided). Finally
   call `ICodeGraphStore.ReplaceEdgesAsync(collection, edges)` once, after resolution.

## What to do
1. Add `Synth.Core/Graph/ICallSiteExtractor.cs` (or similar) with a raw call-site record (e.g.
   `RawCallSite(string CallerQualifiedName, string InvokedName, string SourceFile, int Line)`) and
   an extraction method taking the same `(filePath, relativePath, content)` shape
   `CSharpRoslynChunker.Chunk` already takes.
2. Implement it on `CSharpRoslynChunker` (it already walks every method/constructor in
   `EmitMember`/`EmitType` with `ns`/`className`/`methodName` in scope — reuse that structure rather
   than writing a second, separate Roslyn walk if it can reasonably share code with the existing
   chunking walk; don't force a merge if it makes the existing chunker harder to read, but do
   consider it before defaulting to a second full `CSharpSyntaxTree.ParseText` pass over the same
   file).
3. Qualified name format: `{Namespace}.{ClassName}.{MethodName}` (omit the namespace segment when
   empty, matching how chunk metadata already represents it) — this is also what `SYNTH-27`'s query
   tools will accept as input, so keep it exactly consistent with however chunk metadata would
   render the same method (no separate/parallel naming scheme).
4. Extend `IndexingPipeline` (`Synth.Core/IndexingPipeline.cs`) to take an `ICodeGraphStore` in its
   constructor, accumulate raw call sites + known qualified names while walking files (stage 1 above,
   per file), then resolve and call `ReplaceEdgesAsync` once at the end of `IndexDirectoryAsync`
   (stage 2 above) — after the existing per-file chunk/embed/upsert loop completes, before returning
   the `IndexingSummary`. A directory with zero `.cs` files (or the extractor interface not
   implemented by whichever chunker handled a file) should still complete normally with an empty
   edge set — don't make graph extraction mandatory for every chunker, only for ones that implement
   `ICallSiteExtractor`.
5. Update `EmbeddingServiceExtensions`/wherever `IndexingPipeline` is constructed in DI
   (`Synth.Api/Indexing/IndexingServiceExtensions.cs` or similar) to inject the `ICodeGraphStore`
   registered by `SYNTH-25`.
6. Update every existing test that constructs `IndexingPipeline` directly to pass a graph store
   (an in-memory fake/the real `InMemoryCodeGraphStore` from `SYNTH-25` both work — reuse whichever
   is less friction).
7. Tests: extraction correctness on small, hand-written C# snippets — a method calling another
   method in the same class; a method calling a method on `this`/an injected field; a constructor
   calling another method; a call to something with no matching known method (should simply produce
   no edge, not an error); two classes with same-named methods both being called (produces edges to
   both, since resolution is by simple name only — document this in the test as intended, not a bug).
   End-to-end test: run `IndexingPipeline.IndexDirectoryAsync` against a small multi-file fixture
   directory (a caller file + a callee file) and assert the resulting edges via
   `ICodeGraphStore.FindCallersAsync`/`FindCalleesAsync` on the in-memory store used in the test.

## Acceptance
`dotnet build`/`dotnet test` on `src/Synth.slnx` stay green, `ICallSiteExtractor`/
`ExtractCallSites` exist in `Synth.Core`, and an end-to-end indexing run populates
`ICodeGraphStore` with correctly-resolved call edges for a small fixture.

## Out of scope
- MCP tools / REST endpoints querying the graph — `SYNTH-27`.
- Type-hierarchy edges, semantic/compilation-based resolution, Vue client.
