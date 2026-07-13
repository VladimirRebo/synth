---
id: SYNTH-74
summary: "Convert Program.cs's inline GET /health to a Controller (issue #82, slice 19/many — last Controller conversion)"
status: open
acceptance_command: "test -f src/Synth.Api/HealthController.cs && ! grep -q 'app.MapGet(.\/health.' src/Synth.Api/Program.cs"
acceptance_criterion: ""
boundaries: "This is the last Controllers-conversion slice. Only convert the inline GET /health mapping. Do not touch app.MapDefaultEndpoints() (Aspire's own /alive liveness endpoint, unrelated) or anything else in Program.cs beyond removing the /health block and adding a using if needed."
limits: "max_iterations=15; max_minutes=90"
labels: [backend, refactor, architecture]
---

# SYNTH-74: Convert GET /health to a Controller (issue #82, slice 19, last Controller conversion)

## Context
This is the **last** of the 10 original Minimal-API endpoint-mapping surfaces to convert — after
this, every REST endpoint in Synth.Api is served through a Controller, completing issue #82's
Controllers half (the CQRS half was proven in `SYNTH-61` and used throughout the write-side
conversions). `Program.cs` currently maps `GET /health` inline (not in its own endpoint-mapping
file like every other route was) — a simple read over `IHealthCheckService`, no real logic beyond
mapping the report's `Healthy` flag to a status code.

## What to do
1. Create `src/Synth.Api/HealthController.cs` (top-level namespace `Synth.Api`, no sub-folder needed
   since there was never a `Health/` endpoint-mapping folder to begin with — the health *service*
   lives in `Synth.Infrastructure.Health`, this is just the thin route): a `[ApiController]` with
   `[HttpGet("/health")]` taking `IHealthCheckService` via constructor injection, same body as
   today (`Ok(report)` when healthy, `Json(report, statusCode: 503)` otherwise).
2. Remove the inline `app.MapGet("/health", ...)` block from `Program.cs`. Leave
   `app.MapDefaultEndpoints()` (Aspire's own `/alive` liveness endpoint) untouched — unrelated.
3. Update `tests/Synth.Api.Tests/HealthEndpointTests.cs` — rename to `HealthControllerTests.cs` if
   that reads better; no behavioral changes expected.

## Acceptance
`dotnet build`/`dotnet test` on `Synth.slnx` stay green. `HealthController.cs` exists; `Program.cs`
no longer has the inline `/health` mapping. `GET /health` behaves identically to before (200 when
healthy, 503 with the report body when not).

## Out of scope
- `app.MapDefaultEndpoints()`/`/alive` — Aspire's own endpoint, not part of this conversion.
- Any change to `IHealthCheckService`'s own logic.
- Wrapping the route in a Query type.
