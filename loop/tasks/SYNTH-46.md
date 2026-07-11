---
id: SYNTH-46
summary: "Retry transient errors per-file during indexing"
status: done
acceptance_command: "dotnet build src/Synth.slnx --nologo -v q && dotnet test src/Synth.slnx --nologo -v q && grep -rq 'MaxRetries' src/Synth.Core/IndexingPipeline.cs"
acceptance_criterion: ""
boundaries: "Touch only src/Synth.Core/IndexingPipeline.cs and its tests. Read the current IndexDirectoryAsync body fresh before starting — other tasks (SYNTH-33 incremental indexing, SYNTH-40 SourceUrl, SYNTH-44 parallelization) may have landed first and changed its shape. Only retry the embed+upsert step for a single file — do not retry chunking (a parse failure isn't transient) and do not add job-level retry/resume."
limits: "max_iterations=20; max_minutes=90"
labels: [backend, indexing, reliability]
---

# SYNTH-46: Retry transient errors per-file during indexing

## Context
Part of issue #47. Currently a single transient failure (e.g. Ollama momentarily unreachable, a
Qdrant upsert timeout) anywhere during a file's embed+upsert step propagates up and fails the
entire indexing job via `IIndexJobTracker.Fail` — even if most files already succeeded. Given
incremental indexing (SYNTH-33) now skips unchanged files on a re-run, this is somewhat mitigated
already (a resubmitted job won't redo already-successful files), but a single flaky moment during a
long run still aborts the whole thing rather than just marking that one file as skipped and moving
on — mirroring the existing pattern where an unreadable file is skipped rather than aborting the run.

## What to do
1. Wrap the per-file embed+upsert step (whatever it looks like in the current code — read it fresh,
   see boundaries) in a small retry-with-backoff: 2-3 attempts total, a short exponential backoff
   between attempts (e.g. 200ms, then 800ms — small delays, this shouldn't meaningfully slow down
   the common all-succeeds case).
2. Only retry **transient** exception types — network errors (`HttpRequestException`), timeouts
   (`TaskCanceledException` when NOT caused by the caller's own `cancellationToken` — check
   `cancellationToken.IsCancellationRequested` to distinguish a genuine external cancellation from a
   timeout, same pattern `EmbeddingSettingsEndpoints.ProbeAsync` already uses), and gRPC transient
   status codes if `Grpc.Core.RpcException` is thrown by the Qdrant client (check its `StatusCode`
   for `Unavailable`/`DeadlineExceeded` specifically — don't blanket-retry every `RpcException`,
   e.g. a `DimensionMismatchException` from SYNTH-32 is NOT transient and must NOT be retried, it'll
   just fail the same way every time and burn the retry budget for nothing).
3. A file that still fails after exhausting retries is counted as skipped (same as an unreadable
   file today) — log the failure (there's an established logging pattern in this codebase via
   Serilog, check how other skip/failure paths log if they do) and continue to the next file. Do
   NOT let a file's exhausted retries abort the whole job.
4. Tests: inject a fake embedding generator or store that fails N times then succeeds — confirm the
   file is eventually indexed (retry worked); a fake that always fails — confirm the file is counted
   as skipped and the job completes rather than throwing; confirm a non-transient exception type
   (e.g. simulate a `DimensionMismatchException`-like case, or just any non-network/timeout
   exception) is NOT retried and still propagates/fails fast as it does today (don't swallow real
   bugs into a silent skip — only genuinely transient failures get the retry+skip treatment).

## Acceptance
`dotnet build`/`dotnet test` stay green. A transient failure during a file's embed+upsert step is
retried a few times with backoff; if it still fails, that file is counted as skipped and indexing
continues rather than aborting the whole job. Non-transient exceptions are not retried and still
propagate/fail the job as they do today.

## Out of scope
- Job-level retry/resume (re-running only the files that failed from a previous job) — that's a
  bigger feature; this is per-file resilience within a single run.
- Configurable retry count/backoff via Settings — hardcode reasonable defaults.
- Retrying the chunking step — a parse failure isn't transient, don't retry it.
