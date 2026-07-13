---
id: SYNTH-66
summary: "Convert IndexingEndpoints to a Controller (issue #82, slice 11/many)"
status: open
acceptance_command: "test -f src/Synth.Api/Indexing/IndexingController.cs && ! test -f src/Synth.Api/Indexing/IndexingEndpoints.cs"
acceptance_criterion: ""
boundaries: "Only convert IndexingEndpoints.cs. Do not touch RepositoryEndpoints.cs, the Settings files, CallGraphEndpoints.cs, or LogsEndpoints.cs. Do not touch IndexRepositoryCommand/IndexRepositoryCommandHandler themselves (SYNTH-61) — this task only changes how the routes are mapped, not the command/handler logic. Routes stay bare (no /api prefix), same status codes."
limits: "max_iterations=20; max_minutes=100"
labels: [backend, refactor, architecture]
---

# SYNTH-66: Convert IndexingEndpoints to a Controller (issue #82, slice 11)

## Context
Continuing the Controllers conversion (slice 10, `SearchController`, established the pattern and
set up `AddControllers()`/`MapControllers()` in `Program.cs` — already there, don't re-add).

`src/Synth.Api/Indexing/IndexingEndpoints.cs` already went through the CQRS migration in `SYNTH-61`
— `POST /index` binds the request body as `IndexRepositoryCommand` and dispatches it through
`ICommandHandler<IndexRepositoryCommand, IndexStartOutcome>`, mapping the outcome to 400/409/202.
This conversion is now almost entirely mechanical: move the same two route handlers into a
`[ApiController]` class, constructor-injecting the command handler and `IIndexJobTracker`.

## What to do
1. Create `src/Synth.Api/Indexing/IndexingController.cs`: a `[ApiController]` class with:
   - `[HttpPost("/index")]` taking `IndexRepositoryCommand` from the body (`[FromBody]` if
     Controllers don't infer it automatically the way Minimal API does — check and add the
     attribute if needed) and `ICommandHandler<IndexRepositoryCommand, IndexStartOutcome>` via
     constructor injection. Same status-code mapping as today (400/409/202).
   - `[HttpGet("/index/status")]` taking `IIndexJobTracker` via constructor injection, returning
     `Ok(tracker.Current)`.
   Copy the existing doc comments over, adapted for the Controller shape.
2. Delete `src/Synth.Api/Indexing/IndexingEndpoints.cs` and remove its `app.MapIndexingEndpoints();`
   call from `Program.cs`.
3. Check `src/Synth.Api/Mcp/IndexCodeTool.cs` (the MCP tool that also dispatches
   `IndexRepositoryCommand` through the same handler, per `SYNTH-61`) — it shouldn't need any
   change since it doesn't go through the endpoint-mapping file, but confirm its `using` directives
   still resolve after this file move.
4. Update `tests/Synth.Api.Tests/IndexingEndpointTests.cs` — rename to `IndexingControllerTests.cs`
   if that reads better; the HTTP-level assertions (status codes, routes) shouldn't need behavioral
   changes.

## Acceptance
`dotnet build`/`dotnet test` on `Synth.slnx` stay green. `IndexingController.cs` exists;
`IndexingEndpoints.cs` no longer exists. `POST /index` and `GET /index/status` behave identically
to before.

## Out of scope
- `RepositoryEndpoints.cs`, the three Settings files, `CallGraphEndpoints.cs`, `LogsEndpoints.cs` —
  separate later slices.
- Any change to `IndexRepositoryCommand`/`IndexRepositoryCommandHandler`/`IIndexJobTracker` logic.
- The `index_code` MCP tool's own logic.
