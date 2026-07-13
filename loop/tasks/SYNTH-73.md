---
id: SYNTH-73
summary: "Convert LogsEndpoints to a Controller (issue #82, slice 18/many)"
status: open
acceptance_command: "test -f src/Synth.Api/Logging/LogsController.cs && ! test -f src/Synth.Api/Logging/LogsEndpoints.cs"
acceptance_criterion: ""
boundaries: "Only convert LogsEndpoints.cs. Do not touch Program.cs's inline GET /health mapping (separate, final slice). This is a read-only filtered query over ILogEntryStore — no Command needed; a thin Controller action is the right call, same judgment as SearchController/CallGraphController."
limits: "max_iterations=15; max_minutes=90"
labels: [backend, refactor, architecture]
---

# SYNTH-73: Convert LogsEndpoints to a Controller (issue #82, slice 18)

## Context
Continuing the Controllers conversion (slices 10-17 merged). `LogsEndpoints.cs` maps one route:
`GET /logs?level=&since=&search=&limit=&offset=` — reads `ILogEntryStore.SnapshotAsync()` and
applies level/timestamp/substring filtering plus pagination, all in-memory over the already-fetched
snapshot. All optional, combined with AND, same validation as today (400 for an unparseable level or
`since`, or a negative limit/offset).

This is a read with real filtering logic, but it's straightforward LINQ over an in-memory
`IEnumerable` with no external side effects or reusable business rule — a thin Controller action
calling `ILogEntryStore` directly is the right call here (same precedent as `SearchController`'s
reads), not a forced Query wrapper.

## What to do
1. Create `src/Synth.Api/Logging/LogsController.cs`: a `[ApiController]` with
   `[HttpGet("/logs")]` taking the same query parameters (`level`, `since`, `search`, `limit`,
   `offset`) and `ILogEntryStore` via constructor injection. Move the existing validation +
   filtering + pagination logic in as-is.
2. Delete `src/Synth.Api/Logging/LogsEndpoints.cs` and remove its `app.MapLogsEndpoints();` call
   from `Program.cs`.
3. Update `tests/Synth.Api.Tests/LogsEndpointTests.cs` — rename to `LogsControllerTests.cs` if that
   reads better; no behavioral changes expected.

## Acceptance
`dotnet build`/`dotnet test` on `Synth.slnx` stay green. `LogsController.cs` exists;
`LogsEndpoints.cs` no longer exists. `GET /logs` behaves identically to before across every
filter/pagination combination.

## Out of scope
- `Program.cs`'s inline `GET /health` mapping — the final Controllers slice, separate task.
- Wrapping the route in a Query type.
- Any change to `ILogEntryStore` itself.
