---
id: SYNTH-72
summary: "Convert CallGraphEndpoints to a Controller (issue #82, slice 17/many)"
status: open
acceptance_command: "test -f src/Synth.Api/Graph/CallGraphController.cs && ! test -f src/Synth.Api/Graph/CallGraphEndpoints.cs"
acceptance_criterion: ""
boundaries: "Only convert CallGraphEndpoints.cs. Do not touch LogsEndpoints.cs or Program.cs's inline GET /health mapping. Do not touch CallGraphTool.cs (the MCP tool) beyond fixing a using directive if needed — its own logic is unrelated to this endpoint file. Both routes are simple reads over ICodeGraphStore — no Command/Query wrapper needed, same judgment call as SearchController."
limits: "max_iterations=15; max_minutes=90"
labels: [backend, refactor, architecture]
---

# SYNTH-72: Convert CallGraphEndpoints to a Controller (issue #82, slice 17)

## Context
Continuing the Controllers conversion (slices 10-16 merged — all Search/Indexing/Repositories/
Settings endpoints done). `CallGraphEndpoints.cs` maps two simple reads:
- `GET /callers?symbol=&collection=` — `ICodeGraphStore.FindCallersAsync`.
- `GET /callees?symbol=&collection=` — `ICodeGraphStore.FindCalleesAsync`.

Both are thin delegations with a one-line validation (400 if `symbol` missing) — no real business
logic worth a Command/Query wrapper, matching `SearchController`'s precedent for simple reads.

Note: the current file has a stale `using Synth.Core;` at the top — `Synth.Core` was retired
entirely back in `SYNTH-60`; this using is dead and just hasn't errored because C# doesn't fail
compilation on an unused directive. Drop it during this conversion (it resolves to nothing useful
anymore).

## What to do
1. Create `src/Synth.Api/Graph/CallGraphController.cs`: a `[ApiController]` with
   `[HttpGet("/callers")]` and `[HttpGet("/callees")]`, each taking `symbol`/`collection` query
   parameters and `ICodeGraphStore` via constructor injection, same validation and response shape
   as today (400 if `symbol` missing, else `Ok(edges)`).
2. Delete `src/Synth.Api/Graph/CallGraphEndpoints.cs` and remove its
   `app.MapCallGraphEndpoints();` call from `Program.cs`.
3. Check `src/Synth.Api/Graph/CallGraphTool.cs` (the MCP tool) for any reference to
   `CallGraphEndpoints` — it shouldn't have one (it calls `ICodeGraphStore` directly per its own
   design), but confirm.
4. Update `tests/Synth.Api.Tests/CallGraphEndpointTests.cs` — rename to
   `CallGraphControllerTests.cs` if that reads better; no behavioral changes expected.

## Acceptance
`dotnet build`/`dotnet test` on `Synth.slnx` stay green. `CallGraphController.cs` exists;
`CallGraphEndpoints.cs` no longer exists. `GET /callers` and `GET /callees` behave identically to
before.

## Out of scope
- `LogsEndpoints.cs`, `Program.cs`'s inline `GET /health` mapping — separate later slices.
- `CallGraphTool.cs`'s own logic.
- Wrapping either route in a Query type.
