---
id: SYNTH-59
summary: "Synth.Infrastructure: move Logging + Health (issue #82, slice 7/many, last Infrastructure slice)"
status: open
acceptance_command: "test -f src/Synth.Infrastructure/Health/HealthCheckService.cs && ! test -f src/Synth.Api/Health/HealthCheckService.cs && test -f src/Synth.Infrastructure/Logging/MongoLogEntryStore.cs"
acceptance_criterion: ""
boundaries: "Slice 7 of issue #82, the last Infrastructure slice (slices 1-6 merged). Only move the files listed below. Do not move LogsEndpoints.cs — it's a Minimal-API endpoint file (Api-layer), stays in Synth.Api/Logging/. Program.cs's inline GET /health route mapping stays in Program.cs, it just resolves HealthCheckService via DI which is moving. Do not delete or retire Synth.Core (already empty since SYNTH-58) — that's a separate follow-up task."
limits: "max_iterations=25; max_minutes=120"
labels: [backend, refactor, architecture]
---

# SYNTH-59: Synth.Infrastructure — Logging + Health (issue #82, slice 7)

## Context
Continuing issue #82; slices 1-6 are merged (`Synth.Domain`, `Synth.Application`,
`Synth.Infrastructure` with Storage+Graph+Configuration+Embeddings+Vcs). This is the **last**
Infrastructure slice — after this, `Synth.Infrastructure` holds every concrete implementation and
the only work remaining on #82 is retiring the now-empty `Synth.Core` project, then CQRS +
Controllers.

`src/Synth.Api/Logging/` has 6 files; 5 are concrete implementation/wiring (moving), 1 is an
Api-layer endpoint file (staying). `src/Synth.Api/Health/` has 4 files, all Infrastructure — moving
as a whole unit (concrete health-probing implementation, DI wiring, and the report DTO it produces
all belong together).

**Namespace convention** (same as prior slices): moved types get `Synth.Infrastructure.*`
namespace (e.g. `Synth.Api.Logging.MongoLogEntryStore` → `Synth.Infrastructure.Logging.MongoLogEntryStore`,
`Synth.Api.Health.HealthCheckService` → `Synth.Infrastructure.Health.HealthCheckService`).

## What to do
1. Move these files from `src/Synth.Api/Logging/` into `src/Synth.Infrastructure/Logging/`
   (namespace `Synth.Api.Logging` → `Synth.Infrastructure.Logging`):
   - `LogEntryStoreSink.cs`
   - `LoggingServiceExtensions.cs`
   - `LogEntryStoreWriter.cs`
   - `MongoLogEntryStore.cs`
   - `InMemoryLogEntryStore.cs`
2. Leave `LogsEndpoints.cs` in `src/Synth.Api/Logging/` — Minimal-API endpoint file, Api-layer, not
   moving. Update its `using` directives for the moved types' new namespace.
3. Move all 4 files from `src/Synth.Api/Health/` into `src/Synth.Infrastructure/Health/` (namespace
   `Synth.Api.Health` → `Synth.Infrastructure.Health`):
   - `HealthCheckService.cs`
   - `HealthReport.cs`
   - `IQdrantHealthProbe.cs`
   - `HealthServiceExtensions.cs`
4. `Program.cs`'s inline `GET /health` route mapping stays in `Program.cs` (Api-layer) — it resolves
   `HealthCheckService` via DI, fix its `using` for the new namespace.
5. Fix every `using Synth.Api.Logging` (for the moved files specifically, not `LogsEndpoints`) and
   `using Synth.Api.Health` across the whole solution that now needs
   `using Synth.Infrastructure.Logging`/`using Synth.Infrastructure.Health`.
6. Move each moved type's test file(s) into `tests/Synth.Infrastructure.Tests/` (already exists —
   add to it). Check current names/locations: `LogEntryStoreSinkTests.cs`,
   `MongoLogEntryStoreTests.cs`, `InMemoryLogEntryStoreTests.cs`, `HealthCheckServiceTests.cs`, and
   any test for `LoggingServiceExtensions`/`HealthServiceExtensions` if one exists.

## Acceptance
`dotnet build`/`dotnet test` on `Synth.slnx` stay green — full solution.
`Synth.Infrastructure/Health/HealthCheckService.cs` and `Synth.Infrastructure/Logging/MongoLogEntryStore.cs`
exist; `Synth.Api/Health/HealthCheckService.cs` no longer exists. `Synth.Api/Logging/LogsEndpoints.cs`
still exists (correctly left behind).

## Out of scope
- Retiring the now-empty `Synth.Core` project — separate follow-up task, do it next once this
  merges cleanly.
- `LogsEndpoints.cs` itself — stays in Synth.Api.
- Introducing CQRS, Controllers, or any other Api-layer change.
