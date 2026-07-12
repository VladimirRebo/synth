---
id: SYNTH-61
summary: "CQRS scaffolding (no MediatR) + first command: IndexRepositoryCommand (issue #82, slice 9/many)"
status: open
acceptance_command: "test -f src/Synth.Application/Cqrs/ICommand.cs && grep -rq 'ICommandHandler<IndexRepositoryCommand' src/Synth.Application/"
acceptance_criterion: ""
boundaries: "Slice 9 of issue #82 (layering slices 1-8 all merged). This introduces ONE new pattern (CQRS interfaces) and migrates ONE existing flow (StartIndexing) to prove it end-to-end — do not touch any other endpoint file, do not start the Controllers conversion (that's the next slice, after this pattern is proven). No MediatR, no bus, no pipeline behaviors, no handler auto-discovery/scanning — handlers are resolved by explicit one-line DI registration and taken as a constructor parameter wherever they're used, matching this project's existing explicit-registration style everywhere else."
limits: "max_iterations=25; max_minutes=120"
labels: [backend, refactor, architecture]
---

# SYNTH-61: CQRS scaffolding + IndexRepositoryCommand (issue #82, slice 9)

## Context
Issue #82's remaining scope after the layering (slices 1-8, all merged) is CQRS + Controllers. This
slice does the CQRS half: introduce the hand-rolled command/query interfaces in `Synth.Application`,
and migrate one real flow to prove the pattern actually works end-to-end before touching anything
else. The next slice(s) convert the 10 Minimal-API endpoint-mapping files to Controllers, calling
into command/query handlers established here (and more added as each endpoint converts).

**Shape (from issue #82, decided, not open for reinterpretation):**
```csharp
public interface ICommand<TResult> { }
public interface ICommandHandler<TCommand, TResult> where TCommand : ICommand<TResult>
{
    Task<TResult> HandleAsync(TCommand command, CancellationToken cancellationToken = default);
}
public interface IQuery<TResult> { }
public interface IQueryHandler<TQuery, TResult> where TQuery : IQuery<TResult>
{
    Task<TResult> HandleAsync(TQuery query, CancellationToken cancellationToken = default);
}
```
(Exact member signatures — e.g. whether `CancellationToken` is required or defaulted — are your
call as long as the shape matches: one command type per use case, one handler type per command,
resolved directly from DI, no bus/mediator/pipeline.)

**The flow to migrate**: `IndexingEndpoints.StartIndexing(...)` (currently a `public static` method
in `src/Synth.Api/Indexing/IndexingEndpoints.cs`, shared by `POST /index` and the `index_code` MCP
tool) — validates the request, reserves the job slot via `IIndexJobTracker`, and dispatches the
detached background clone+index+registry-upsert work. It currently takes its dependencies
(`IndexingPipeline`, `GitRepoService`, `IRepositoryRegistry`, `IIndexJobTracker`, `ILoggerFactory`)
as method parameters — as a command handler, these become constructor-injected instead, and the
method body is otherwise unchanged. It returns `IndexStartOutcome`, which already exists as a
discriminated result type — reuse it as-is as the command's `TResult`.

## What to do
1. Create `src/Synth.Application/Cqrs/ICommand.cs` (or split into `ICommand.cs`/`ICommandHandler.cs`,
   your call) with the `ICommand<TResult>`/`ICommandHandler<TCommand, TResult>` interfaces described
   above, and `IQuery.cs`/`IQueryHandler.cs` likewise for `IQuery<TResult>`/`IQueryHandler<TQuery, TResult>`.
2. Move `IndexRequest` and `IndexStartOutcome` (currently `public sealed record`s at the top of
   `IndexingEndpoints.cs`) into `Synth.Application` — they're the command's input/output shape, not
   Api-layer concerns. `IndexRequest` may need renaming to something like `IndexRepositoryCommand`
   that itself implements `ICommand<IndexStartOutcome>` — your call whether `IndexRequest` becomes
   the command directly (simplest) or stays a separate DTO the endpoint maps into a new command
   record; simplest is best here.
3. Create `IndexRepositoryCommandHandler : ICommandHandler<IndexRepositoryCommand, IndexStartOutcome>`
   in `Synth.Application` (e.g. `src/Synth.Application/Indexing/IndexRepositoryCommandHandler.cs`),
   with `IndexingPipeline`, `GitRepoService`, `IRepositoryRegistry`, `IIndexJobTracker`,
   `ILoggerFactory` as constructor parameters. Move `StartIndexing`'s method body into
   `HandleAsync` essentially unchanged (it's already synchronous validation + detached
   `Task.Run` dispatch — keep that exact behavior, this is a structural move not a rewrite).
   `SourceTypeFor` (the private helper mapping `GitProvider` to a source-type string) moves with it.
4. Register the handler in DI (`IndexingServiceExtensions.cs` in `Synth.Api`, or wherever makes most
   sense — one explicit `AddSingleton<ICommandHandler<IndexRepositoryCommand, IndexStartOutcome>,
   IndexRepositoryCommandHandler>()`-style line, no scanning).
5. Update `IndexingEndpoints.cs`'s `POST /index` Minimal-API handler to take
   `ICommandHandler<IndexRepositoryCommand, IndexStartOutcome>` (instead of the individual services)
   and call `HandleAsync` — the route mapping itself, response-status mapping
   (`ValidationError`→400, `AlreadyRunning`→409, else 202), and `GET /index/status` are unchanged.
   This file stays Minimal API for now — do not convert it to a Controller in this task.
6. Update the `index_code` MCP tool (`src/Synth.Api/Mcp/IndexCodeTool.cs`, which currently also
   calls the shared `StartIndexing` static method) to resolve and call the same command handler via
   DI instead.
7. Move the relevant test coverage: whatever currently tests `IndexingEndpoints.StartIndexing`
   directly (check `tests/Synth.Api.Tests/IndexingEndpointTests.cs`) should now test
   `IndexRepositoryCommandHandler.HandleAsync` from `tests/Synth.Application.Tests/`; keep whatever
   endpoint-level tests exercise `POST /index`'s HTTP behavior (status codes, routing) in
   `tests/Synth.Api.Tests/` unchanged in spirit, just updated for the new indirection.

## Acceptance
`dotnet build`/`dotnet test` on `Synth.slnx` stay green — full solution, same behavior as before
(validation rules, 400/409/202 status codes, background dispatch semantics) just routed through the
new command/handler indirection. `Synth.Application/Cqrs/ICommand.cs` exists.
`ICommandHandler<IndexRepositoryCommand, ...>` is implemented somewhere in `Synth.Application`.

## Out of scope
- Converting any Minimal-API endpoint file to a Controller — that's the next slice(s), after this
  pattern is confirmed to work.
- Migrating any other endpoint's logic to a command/query (e.g. `DeleteCollectionAsync` in
  `RepositoryEndpoints.cs`) — one proof-of-concept flow per this task, more follow later.
- MediatR, a command/query bus, pipeline behaviors, or auto-discovery/scanning of handlers.
