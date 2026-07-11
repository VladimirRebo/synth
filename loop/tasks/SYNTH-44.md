---
id: SYNTH-44
summary: "Parallelize indexing: concurrent per-file chunk+embed+upsert"
status: open
acceptance_command: "dotnet build src/Synth.slnx --nologo -v q && dotnet test src/Synth.slnx --nologo -v q && grep -rq 'ParallelOptions' src/Synth.Core/IndexingPipeline.cs"
acceptance_criterion: ""
boundaries: "Touch only src/Synth.Core/IndexingPipeline.cs and its tests. This runs after SYNTH-40 (SourceUrl) and SYNTH-33 (incremental indexing) have both already landed on main — read the current IndexDirectoryAsync body fresh before starting, don't assume the shape described here is exactly current if other tasks landed first. Preserve every existing behavior: incremental skip-if-unchanged (SYNTH-33), the call-graph two-stage resolution, progress reporting, stale-file cleanup, and (if already merged) SourceUrl population — this task is purely about concurrency, not changing what gets computed."
limits: "max_iterations=30; max_minutes=150"
labels: [backend, indexing, performance]
---

# SYNTH-44: Parallelize indexing

## Context
Part of issue #45. Measured this session: indexing Synth's own repo (103 files) took ~5 minutes.
`IndexingPipeline.IndexDirectoryAsync`'s per-file loop is strictly sequential — chunk, then embed,
then upsert, one file fully finishing before the next file starts. The embedding-generator call is
the actual bottleneck (a real HTTP round-trip to Ollama/OpenAI per file); chunking and upsert are
comparatively fast.

**Read `IndexDirectoryAsync`'s current body before starting** — by the time this task runs, SYNTH-33
(incremental indexing: skip-if-unchanged via `GetByFileAsync` hash comparison) and possibly SYNTH-40
(`SourceUrl` population) will already be merged, so the loop body has more steps than what's
described in older task history. The shape you need to preserve, whatever the exact current code
looks like:
1. Per-file: read content, chunk (+ call-site extraction — always runs, every file, for call-graph
   correctness), check the incremental-skip condition (stored hash vs. fresh hash), and if not
   skipped: embed + upsert.
2. Shared mutable state accumulated across the whole walk: `filesIndexed`/`filesSkipped`/
   `chunksIndexed` counters, `rawCallSites`/`knownSymbols` (call-graph accumulators), `seenPaths`
   (for stale-file cleanup at the end) — all of this is currently plain `int`/`List`/`HashSet`,
   which is **not thread-safe**. Parallelizing the per-file loop means these need proper
   synchronization (`Interlocked` for counters, a lock or concurrent collection for the
   lists/sets, or accumulate into per-task-local collections and merge afterward — your call on
   which is cleanest).

## What to do
1. Replace the sequential `foreach (var filePath in EnumerateSourceFiles(rootPath))` loop with a
   bounded-concurrency parallel version — `Parallel.ForEachAsync` (built into .NET, takes a
   `ParallelOptions.MaxDegreeOfParallelism`) is the natural fit here since each file's work is
   already `async`. Pick a reasonable default concurrency (e.g. 4-8) — don't make it configurable
   in this task, hardcode a sensible default.
2. Make every piece of shared state accumulated inside the loop body thread-safe:
   - `filesIndexed`/`filesSkipped`/`chunksIndexed`: `Interlocked.Increment`/`Interlocked.Add`, or
     switch to `int` fields wrapped appropriately — pick whichever reads cleanest.
   - `rawCallSites`: a thread-safe collection (`ConcurrentBag<RawCallSite>` or lock-guarded `List`).
   - `knownSymbols`: needs uniqueness (`HashSet` semantics) under concurrency — a lock-guarded
     `HashSet`, or a `ConcurrentDictionary<string, byte>` used as a set, whichever is idiomatic here.
   - `seenPaths`: same treatment as `knownSymbols`.
3. `progress?.Report(...)` is currently called after each file completes with the running totals —
   keep reporting progress from inside the parallel body (reading the now-thread-safe counters),
   accepting that report ordering across files may interleave (that's fine, the client only cares
   about the latest snapshot, not a strict per-file sequence — confirm nothing downstream assumes
   strict ordering before accepting this).
4. Everything *after* the per-file loop (stale-file cleanup via `ListRelativePathsAsync`/
   `DeleteByFileAsync`, call-graph resolution via `ResolveEdges`/`ReplaceEdgesAsync`, the final
   progress report) stays sequential, unchanged — only the per-file work itself is parallelized.
5. Tests: extend `IndexingPipelineTests.cs` — a test indexing a directory with several files still
   produces the correct total `FilesIndexed`/`ChunksIndexed` counts (proving the counters are race-free
   under concurrency — a flaky/wrong count here would indicate a synchronization bug); a test proving
   the call graph is still fully and correctly resolved across multiple files indexed concurrently
   (reuse the existing `IndexDirectoryAsync_populates_call_graph_across_files`-style fixture, now
   with enough files that they're genuinely likely to run concurrently); run the full existing
   `IndexingPipelineTests.cs` suite (all pre-existing tests, including SYNTH-33's incremental-index
   tests) to confirm nothing regressed under the new concurrent execution model.

## Acceptance
`dotnet build`/`dotnet test` stay green, including every pre-existing `IndexingPipelineTests.cs`
test (incremental skip, call-graph correctness, stale-file cleanup all still pass under
concurrency). Files are chunked+embedded+upserted with bounded concurrency instead of strictly
sequentially. File/chunk counts and call-graph resolution remain correct and race-free.

## Out of scope
- Configurable concurrency/batch-size via Settings — hardcode a reasonable default.
- Batching multiple files' chunks into a single embedding-generator call (a further optimization
  beyond this task's scope — this task is about concurrency between files, not batching within a
  single generator call).
- Measuring/reporting the actual speedup — the acceptance bar is correctness under concurrency, not
  a benchmark.
