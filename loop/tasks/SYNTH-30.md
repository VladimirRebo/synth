---
id: SYNTH-30
summary: "IIndexJobTracker (in-memory) + progress-reporting hook in IndexingPipeline"
status: open
acceptance_command: "dotnet build src/Synth.slnx --nologo -v q && dotnet test src/Synth.slnx --nologo -v q && grep -riq 'interface IIndexJobTracker' src/Synth.Core/ src/Synth.Api/"
acceptance_criterion: ""
boundaries: "Only add the job-tracker abstraction and the progress-reporting hook on IndexingPipeline. Do not change POST /index or add GET /index/status (SYNTH-31). Do not touch the Vue client. Do not add per-collection job history or a job queue — one global current-or-last job only, per issue #39."
limits: "max_iterations=25; max_minutes=120"
labels: [backend, indexing, progress]
---

# SYNTH-30: IIndexJobTracker + progress hook in IndexingPipeline

## Context
Issue #39: `POST /index` is currently a single blocking call with no way to observe progress
except waiting for the whole HTTP response — a page reload mid-index loses all visibility even
though the server keeps working. This task adds the tracking primitive and the pipeline
instrumentation; wiring `POST /index` to actually use it (fire-and-forget + a status endpoint) is
`SYNTH-31`.

**Scope decision (2026-07-10): one global job, not per-collection.** Synth is a personal,
single-user tool; there is no existing concurrent-indexing story to preserve. Track exactly one
"current or most recently finished" job rather than a map keyed by collection.

## What to do
1. Add `Synth.Core/Indexing/IndexJobStatus.cs` (or similar): an immutable snapshot with at least
   `State` (an enum: `Idle`, `Running`, `Done`, `Failed`), `Collection`, `Source` (the path or repo
   URL being indexed), `FilesIndexed`, `FilesSkipped`, `TotalFiles` (nullable until counted),
   `ChunksIndexed`, `StartedAt`, `FinishedAt` (nullable while running), `Error` (nullable message,
   set only when `State == Failed`).
2. Add `Synth.Api/Indexing/IIndexJobTracker.cs` (in-memory only — no Mongo, this is ephemeral,
   process-lifetime state, not history) with something like:
   - `IndexJobStatus Current { get; }` — the current/most recent job; `Idle` with no other fields
     populated when nothing has ever run.
   - `bool TryStart(string collection, string source)` — atomically transitions to `Running` if not
     already running (returns `false`, leaving the existing job alone, if one is already in
     progress — this is what `SYNTH-31` uses to reject a concurrent `POST /index` with 409).
   - `void ReportProgress(int filesIndexed, int filesSkipped, int? totalFiles)` — updates the
     in-flight job's counters.
   - `void Complete(int filesIndexed, int filesSkipped, int chunksIndexed)` — transitions to `Done`.
   - `void Fail(string error)` — transitions to `Failed`.
   Guard concurrent access appropriately (a lock around reads/writes of the current status is
   enough — this doesn't need to be lock-free, updates are infrequent relative to embedding calls).
3. Implement it (`InMemoryIndexJobTracker` or similar), registered as a DI singleton.
4. Extend `IndexingPipeline.IndexDirectoryAsync` (`Synth.Core/IndexingPipeline.cs`) with an optional
   parameter for progress reporting — e.g. `IProgress<IndexingProgress>? progress = null`, where
   `IndexingProgress` is a small record `(int FilesIndexed, int FilesSkipped, int TotalFiles)`. Count
   the total matching files upfront (a cheap enumeration pass over `EnumerateSourceFiles` before the
   main loop — the existing method is already lazy, a `.Count()` call is a plain directory walk, not
   expensive relative to embedding) and report it once at the start; report updated counts as the
   existing per-file loop progresses (every file, or a small batch — don't over-engineer throttling
   for a personal tool). This parameter is optional and additive: existing callers that don't pass
   it (tests, any other caller) must keep working unchanged.
5. Tests: `InMemoryIndexJobTracker` — `TryStart` returns `false` when already running and leaves the
   existing job's fields untouched; `ReportProgress`/`Complete`/`Fail` transition state and populate
   fields correctly; `Current` reports `Idle` before anything has run. `IndexingPipeline` — passing
   an `IProgress<IndexingProgress>` (a simple test double capturing reported values) against a small
   fixture directory receives at least a final report matching the actual file/chunk counts, and the
   reported `TotalFiles` matches the number of matching files in the fixture.

## Acceptance
`dotnet build`/`dotnet test` on `src/Synth.slnx` stay green, `IIndexJobTracker` exists, and
`IndexingPipeline.IndexDirectoryAsync` accepts an optional progress callback that reports file
counts as indexing proceeds, without changing behavior for callers that omit it.

## Out of scope
- `POST /index`/`GET /index/status` — `SYNTH-31`.
- Vue client — done directly after the backend lands.
- Per-collection history, job queueing, cancellation.
