---
id: SYNTH-31
summary: "POST /index becomes fire-and-forget (202) + GET /index/status"
status: done
acceptance_command: "dotnet build src/Synth.slnx --nologo -v q && dotnet test src/Synth.slnx --nologo -v q && grep -riq '\"/index/status\"' src/Synth.Api/"
acceptance_criterion: ""
boundaries: "Only change POST /index's response semantics (fire-and-forget) and add GET /index/status on top of SYNTH-30's IIndexJobTracker. Do not touch the Vue client. Map the new route bare (no /api prefix, matching every other endpoint — the Settings-endpoint mistake from earlier in this project must not be repeated). Keep the existing request validation (path exists / repoUrl parses) synchronous — only the actual clone+indexing work moves to the background."
limits: "max_iterations=25; max_minutes=120"
labels: [backend, indexing, progress]
---

# SYNTH-31: POST /index becomes fire-and-forget + GET /index/status

## Context
`SYNTH-30` added `IIndexJobTracker` and a progress-reporting hook on `IndexingPipeline` — nothing
uses them yet. This task rewires `POST /index` (`src/Synth.Api/Indexing/IndexingEndpoints.cs`) to
actually run the clone+indexing work as a detached background task instead of blocking the HTTP
response for the whole run, and adds the status endpoint the client will poll.

**Background-task lifetime, read carefully:** `IndexingPipeline`, `GitRepoService`,
`IRepositoryRegistry`, and `IIndexJobTracker` are all registered as DI singletons (check
`IndexingServiceExtensions`/`VcsServiceExtensions`/`SYNTH-30`'s registration to confirm before
assuming) — so the detached task can capture and reuse them directly with no
`IServiceScopeFactory`/scoped-service ceremony needed. What you must *not* do is use the incoming
HTTP request's `CancellationToken` for the detached work: that token is cancelled when the request
completes (which now happens almost immediately), so the background indexing would be killed
right after starting. Use `CancellationToken.None` (or a dedicated long-lived token if one already
exists in this codebase for background work — check `SYNTH-28`'s log-persistence
`BackgroundService` for precedent) for the detached task's own cancellation.

## What to do
1. In `IndexingEndpoints.MapIndexingEndpoints`, keep all existing synchronous validation exactly as
   it is today (exactly one of `path`/`repoUrl`; directory-exists check; `RepoUrlInfo.Parse`
   failures) — these still return `400` immediately, unchanged.
2. Before starting work, call `IIndexJobTracker.TryStart(collection, source)`. If it returns
   `false` (a job is already running), return `Results.Conflict(new { error = "An indexing job is
   already running." })` (409) without touching anything else.
3. On success, kick off the actual work — for the `repoUrl` case, `GitRepoService.EnsureRepoAsync`;
   then `IndexingPipeline.IndexDirectoryAsync(collection, indexRoot, progress, CancellationToken.None)`
   passing an `IProgress<IndexingProgress>` that forwards into
   `IIndexJobTracker.ReportProgress(...)` — as a detached `Task.Run(...)` (fire-and-forget from the
   endpoint's perspective; do not `await` it before responding). Inside that background task, on
   success call `tracker.Complete(...)` and update the `IRepositoryRegistry` entry (the same
   registry-upsert logic the endpoint does today, just moved into the continuation since it can no
   longer happen before the response is sent); on failure (git command error, pipeline exception)
   call `tracker.Fail(ex.Message)` and log it (don't let an unobserved task exception crash the
   process — wrap the whole background body in try/catch).
4. Return `Results.Accepted(value: new { collection, status = "started" })` (202) immediately after
   successfully calling `TryStart` and dispatching the background task — do not wait for it.
5. Add `GET /index/status` (bare route) returning `IIndexJobTracker.Current` as JSON.
6. Map the new endpoint in `Program.cs` next to the other `Map*Endpoints()` calls.
7. Tests: this is the trickiest part to test deterministically since the work is now detached —
   inject a fake/slow `IndexingPipeline` dependency isn't practical (it's a concrete class, not an
   interface) — instead, prefer testing through `IIndexJobTracker`'s observable state with a real
   but fast fixture (a tiny temp directory with 0-1 files) via `WebApplicationFactory`, polling
   `GET /index/status` a few times with a short delay until it reaches `Done`, rather than asserting
   on the synchronous response body containing a full summary (it won't — the response is now just
   `{collection, status}`). Cover: `POST /index` returns 202 immediately (not blocking); a second
   `POST /index` while one is running returns 409; `GET /index/status` eventually reports `Done`
   with correct file/chunk counts for the fixture; a failure case (e.g. a bad `repoUrl`) still
   returns its existing synchronous 400 — that path doesn't change since it fails before any
   background work starts.

## Acceptance
`dotnet build`/`dotnet test` on `src/Synth.slnx` stay green; `POST /index` returns 202 immediately
and runs indexing in the background; a concurrent second request gets 409; `GET /index/status`
(bare route) reports live progress and reaches `Done`/`Failed` with the same information the old
synchronous response used to carry.

## Out of scope
- Vue client — done directly after the backend lands.
- Cancelling an in-flight job, per-collection job history.
