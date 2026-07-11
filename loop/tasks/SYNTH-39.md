---
id: SYNTH-39
summary: "Paginate GET /repositories and GET /logs"
status: done
acceptance_command: "dotnet build src/Synth.slnx --nologo -v q && dotnet test src/Synth.slnx --nologo -v q && grep -rq 'offset' src/Synth.Api/Vcs/RepositoryEndpoints.cs"
acceptance_criterion: ""
boundaries: "Touch only src/Synth.Api/Vcs/RepositoryEndpoints.cs, src/Synth.Api/Logging/LogsEndpoints.cs, and their tests. Both new params are optional with backward-compatible defaults (omitting them must return the same full result either endpoint returns today) — do not change existing callers' behavior when they don't pass the new params. No client changes in this task (see Out of scope)."
limits: "max_iterations=20; max_minutes=90"
labels: [backend, api, operability]
---

# SYNTH-39: Paginate GET /repositories and GET /logs

## Context
Part of issue #52. Both endpoints currently return their full result set unbounded:
- `RepositoryEndpoints.cs`: `GET /repositories` → `Results.Ok(await registry.ListAsync(cancellationToken))`.
- `LogsEndpoints.cs`: `GET /logs` → filters `ILogEntryStore.SnapshotAsync()` in memory (level/since/search,
  combined with AND) and returns the full filtered set as `entries.ToArray()`.

Fine at today's personal scale (a handful of indexed repos, a capped 20k-document Mongo log
collection), but worth adding simple limit/offset pagination now rather than after it's a real
problem. Sonar's own `CollectionBrowser` uses cursor pagination for chunk browsing, but that's
overkill here — a plain `limit`/`offset` pair is enough given the realistic data volumes involved.

## What to do
1. `GET /repositories`: add optional `int? limit` and `int? offset` query parameters. When both
   are omitted, behavior is unchanged (returns everything, as today) — this preserves backward
   compatibility for the existing client call. When provided, apply `.Skip(offset ?? 0).Take(limit ?? int.MaxValue)`
   (or equivalent) over the list `registry.ListAsync()` returns, after whatever ordering it already
   has (check whether `ListAsync`'s current order is stable/meaningful — if not, pick a sensible
   default order, e.g. by `LastIndexedAt` descending, most-recently-indexed first, so pagination is
   at least deterministic across calls).
2. `GET /logs`: add the same optional `limit`/`offset` pair, applied after the existing
   level/since/search filters (so pagination operates on the already-filtered result set, not the
   raw unfiltered store), preserving the existing oldest-first order when applying `Skip`/`Take`.
   Omitting both params again preserves today's behavior exactly.
3. Validate `limit`/`offset` are non-negative when provided; a negative value is a 400
   (`Results.BadRequest`), matching this endpoint's existing validation style for `level`/`since`.
4. Tests: for each endpoint, a test with no pagination params still returns everything (regression
   guard for backward compatibility); a test with `limit`/`offset` set returns the correctly-sliced
   subset; a test with a negative `limit` or `offset` returns 400.

## Acceptance
`dotnet build`/`dotnet test` stay green. `GET /repositories` and `GET /logs` both accept optional
`limit`/`offset` query params that slice their (already-filtered, for `/logs`) result set;
omitting either param preserves each endpoint's exact current behavior.

## Out of scope
- Any client change to actually use the new params — `IndexPanel.vue`'s repository list and
  `LogsPanel.vue` keep calling both endpoints exactly as they do today (no params), which continues
  to return everything per the backward-compatible default. Wiring the client to page through
  results is a separate follow-up once there's an actual need (many indexed repos / a long log
  history) to justify it.
- Cursor-based pagination — plain limit/offset is enough for the realistic data volumes here.
