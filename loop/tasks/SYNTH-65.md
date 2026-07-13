---
id: SYNTH-65
summary: "Convert SearchEndpoints to a Controller (issue #82, slice 10/many — first Controller, sets up the pattern)"
status: open
acceptance_command: "test -f src/Synth.Api/Search/SearchController.cs && ! test -f src/Synth.Api/Search/SearchEndpoints.cs && grep -q 'AddControllers' src/Synth.Api/Program.cs && grep -q 'MapControllers' src/Synth.Api/Program.cs"
acceptance_criterion: ""
boundaries: "This is the first Controller conversion — it also sets up Controllers support in Program.cs (AddControllers/MapControllers), which every later endpoint-file conversion will reuse, not re-add. Only convert SearchEndpoints.cs in this task. Do not touch IndexingEndpoints.cs, RepositoryEndpoints.cs, the three Settings endpoint files, CallGraphEndpoints.cs, or LogsEndpoints.cs — each converts in its own later task. Every route must stay bare (no /api prefix) and keep its exact current behavior (status codes, response shapes) — this is a mechanical Minimal-API-to-Controller move, not a behavior change."
limits: "max_iterations=25; max_minutes=120"
labels: [backend, refactor, architecture]
---

# SYNTH-65: Convert SearchEndpoints to a Controller (issue #82, slice 10)

## Context
Issue #82's layering (slices 1-9, all merged) and CQRS scaffolding are done. This starts the last
piece: converting the 10 Minimal-API endpoint-mapping files to Controllers, one file at a time. This
is the **first** conversion — besides converting `SearchEndpoints.cs` itself, it sets up ASP.NET
Core Controllers support in `Program.cs` (`AddControllers()`/`MapControllers()`), which every
subsequent endpoint-file conversion will just rely on already being there.

`src/Synth.Api/Search/SearchEndpoints.cs` maps two routes:
- `GET /search?q=...&limit=...&collection=...` — delegates to `CodeSearchService`
  (`SearchAsync`/`SearchAllCollectionsAsync` depending on whether `collection=*`).
- `GET /repositories/{collection}/files/{*relativePath}` — delegates to `ICodeChunkStore.GetByFileAsync`.

Both are simple reads with no meaningful business logic to extract into a Query type — per issue
#82's own open question ("exact command/query class names and boundaries... leaning explicit, but
worth confirming when scoped"), a thin Controller action calling the existing `CodeSearchService`/
`ICodeChunkStore` directly (constructor-injected) is the right call here, not a forced Query wrapper
for a single delegating line. Reserve the CQRS wrapper pattern (proven in `SYNTH-61`) for operations
with actual logic worth encapsulating — this task is about the Controllers half specifically.

## What to do
1. In `src/Synth.Api/Program.cs`: add `builder.Services.AddControllers()` (with the same
   `JsonStringEnumConverter` configured for the existing `ConfigureHttpJsonOptions` call — Controllers
   use `AddControllers().AddJsonOptions(...)` for the equivalent, or configure both from one shared
   options source, your call on the cleanest way to keep both Minimal API and Controller JSON output
   consistent while other endpoint files haven't converted yet) and `app.MapControllers()` (add it
   near the other `app.Map*Endpoints()` calls — exact position matters less than it running once).
2. Create `src/Synth.Api/Search/SearchController.cs`: a `[ApiController]` class (no `[Route]` prefix
   needed at the class level since every action route is already absolute and bare) with two actions:
   - `[HttpGet("/search")]` taking the same query parameters (`q`, `limit`, `collection`) and
     `CodeSearchService`/`IRepositoryRegistry` via constructor injection, same validation
     (400 if `q` missing) and same branching (`collection == CollectionNames.All` → fan-out).
   - `[HttpGet("/repositories/{collection}/files/{*relativePath}")]` taking `ICodeChunkStore` via
     constructor injection, same validation and 404-when-empty behavior.
   Copy the existing doc comments over (adapted for the Controller shape) rather than dropping them.
3. Delete `src/Synth.Api/Search/SearchEndpoints.cs` and remove its `app.MapSearchEndpoints();` call
   from `Program.cs` (Controllers auto-register their routes via `MapControllers()`, no manual
   per-file mapping call needed anymore for this one).
4. `SearchServiceExtensions.cs` (the `AddSynthSearch` DI wiring for `QueryExpander`/
   `CodeSearchService`) is unrelated to endpoint mapping — leave it untouched.
5. Update `tests/Synth.Api.Tests/SearchEndpointTests.cs` (or wherever the existing HTTP-level tests
   for these two routes live) — if they exercise the routes via `WebApplicationFactory<Program>`
   (hitting real HTTP), they should need no behavioral changes, just confirm they still pass; rename
   the test file to `SearchControllerTests.cs` if that reads better, your call.

## Acceptance
`dotnet build`/`dotnet test` on `Synth.slnx` stay green — full solution. `SearchController.cs`
exists; `SearchEndpoints.cs` no longer exists. `Program.cs` has both `AddControllers()` and
`MapControllers()`. `GET /search` and `GET /repositories/{collection}/files/{*relativePath}` behave
identically to before (same status codes, same response JSON shape) — verify via the existing
integration tests passing, not just a compile check.

## Out of scope
- Converting any other endpoint file (`IndexingEndpoints.cs`, `RepositoryEndpoints.cs`, the three
  Settings files, `CallGraphEndpoints.cs`, `LogsEndpoints.cs`) — each is its own later slice.
- Introducing new Command/Query wrapper types for these two read-only routes.
- Any change to `CodeSearchService`, `ICodeChunkStore`, or `SearchServiceExtensions.cs`.
