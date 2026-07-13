---
id: SYNTH-67
summary: "Convert RepositoryEndpoints to a Controller + DeleteCollectionCommand (issue #82, slice 12/many)"
status: open
acceptance_command: "test -f src/Synth.Api/Vcs/RepositoriesController.cs && ! test -f src/Synth.Api/Vcs/RepositoryEndpoints.cs && grep -rq 'ICommandHandler<DeleteCollectionCommand' src/Synth.Application/"
acceptance_criterion: ""
boundaries: "Only convert RepositoryEndpoints.cs and the DeleteCollectionAsync sequence it currently owns. Do not touch the Settings files, CallGraphEndpoints.cs, or LogsEndpoints.cs. GET /repositories stays a thin Controller action calling IRepositoryRegistry directly (no Query wrapper needed — simple read, same judgment call as SearchController) — only DELETE gets the Command treatment, since it has real multi-step logic worth encapsulating (explicitly called out in issue #82's own text)."
limits: "max_iterations=25; max_minutes=120"
labels: [backend, refactor, architecture]
---

# SYNTH-67: RepositoryEndpoints -> Controller + DeleteCollectionCommand (issue #82, slice 12)

## Context
Continuing the Controllers conversion (slices 10-11, `SearchController`/`IndexingController`, both
merged). `src/Synth.Api/Vcs/RepositoryEndpoints.cs` maps two routes plus a shared helper:
- `GET /repositories?limit=&offset=` — simple read over `IRepositoryRegistry`, ordered + paginated.
  Stays a thin Controller action, same as `SearchController`'s reads.
- `DELETE /repositories/{collection}` — a real multi-step sequence (delete the vector-store
  collection, clear call-graph edges, remove the registry entry, clean up an on-disk checkout if the
  source was a cloned remote) currently factored into the public static
  `RepositoryEndpoints.DeleteCollectionAsync` helper, reused as-is by the `delete_collection` MCP
  tool (`src/Synth.Api/Mcp/DeleteCollectionTool.cs`). Issue #82 explicitly calls this sequence out as
  the kind of logic that belongs in a Command — this is that migration, following the exact pattern
  `SYNTH-61` established for `IndexRepositoryCommand`.

## What to do
1. Create `DeleteCollectionCommand`/`DeleteCollectionResult` (or reuse a `bool`/existing shape if
   that's cleaner — your call) and `DeleteCollectionCommandHandler : ICommandHandler<DeleteCollectionCommand, ...>`
   in `Synth.Application` (e.g. `src/Synth.Application/Vcs/DeleteCollectionCommandHandler.cs`),
   constructor-injecting `ICodeChunkStore`, `ICodeGraphStore`, `IRepositoryRegistry`, and
   `IGitRepoService` (the port from `SYNTH-61` — not the concrete `Synth.Infrastructure.Vcs.GitRepoService`,
   Application must not depend on concrete Infrastructure). Move the existing sequence's body
   (read the entry first to know `SourceType`, delete chunk-store collection + clear graph edges +
   remove registry entry, clean up the on-disk checkout for a cloned remote) into `HandleAsync`
   essentially unchanged. The `IsClonedRemote` helper and `GitRepoService.DeleteCheckout`/
   `ResolveCheckoutPath` calls move/adapt accordingly — check whether `IGitRepoService` already
   exposes what's needed or needs a small addition (`ResolveCheckoutPath`, a static-ish
   `DeleteCheckout` helper) to support this without the Application layer seeing the concrete type.
2. Register the handler in DI (wherever `AddSynthVcs`/similar already lives, one explicit line, no
   scanning).
3. Create `src/Synth.Api/Vcs/RepositoriesController.cs`: a `[ApiController]` with:
   - `[HttpGet("/repositories")]` — same pagination/validation as today, calling
     `IRepositoryRegistry` directly (no Command/Query wrapper).
   - `[HttpDelete("/repositories/{collection}")]` — constructor-injects the new
     `ICommandHandler<DeleteCollectionCommand, ...>`, dispatches it, maps the result to
     `NoContent()`/`NotFound()` exactly as today.
4. Delete `src/Synth.Api/Vcs/RepositoryEndpoints.cs` and remove its `app.MapRepositoryEndpoints();`
   call from `Program.cs`.
5. Update `src/Synth.Api/Mcp/DeleteCollectionTool.cs` to resolve and call the new
   `ICommandHandler<DeleteCollectionCommand, ...>` via DI instead of calling
   `RepositoryEndpoints.DeleteCollectionAsync` directly (mirroring how `IndexCodeTool.cs` already
   does this for `IndexRepositoryCommand`, per `SYNTH-61`).
6. Tests: move/adapt whatever currently tests `RepositoryEndpoints.DeleteCollectionAsync`/
   `IsClonedRemote` directly into a new test file for `DeleteCollectionCommandHandler` in
   `tests/Synth.Application.Tests/`; keep the HTTP-level tests for the two routes in
   `tests/Synth.Api.Tests/` (rename `RepositoryEndpointsTests.cs` to
   `RepositoriesControllerTests.cs` if that reads better). Update
   `tests/Synth.Api.Tests/DeleteCollectionMcpToolTests.cs` for the new indirection if needed.

## Acceptance
`dotnet build`/`dotnet test` on `Synth.slnx` stay green. `RepositoriesController.cs` exists;
`RepositoryEndpoints.cs` no longer exists. `ICommandHandler<DeleteCollectionCommand, ...>` is
implemented in `Synth.Application`. `GET /repositories` and `DELETE /repositories/{collection}`
behave identically to before, and the `delete_collection` MCP tool still works via the shared
command handler.

## Out of scope
- The three Settings files, `CallGraphEndpoints.cs`, `LogsEndpoints.cs` — separate later slices.
- Wrapping `GET /repositories` in a Query type.
- Any change to `ICodeChunkStore`/`ICodeGraphStore`/`IRepositoryRegistry` themselves.
