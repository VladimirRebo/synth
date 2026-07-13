---
id: SYNTH-70
summary: "Merge OllamaModelEndpoints into EmbeddingSettingsController + PullOllamaModelCommand (issue #82, slice 15/many)"
status: open
acceptance_command: "! test -f src/Synth.Api/Embeddings/OllamaModelEndpoints.cs && grep -q '/settings/embedding/ollama/models' src/Synth.Api/Embeddings/EmbeddingSettingsController.cs && grep -rq 'ICommandHandler<PullOllamaModelCommand' src/Synth.Application/"
acceptance_criterion: ""
boundaries: "Only convert OllamaModelEndpoints.cs, merging its three routes into the existing EmbeddingSettingsController.cs (from SYNTH-69) rather than creating a second controller class. Do not touch RawSettingsEndpoints.cs, CallGraphEndpoints.cs, or LogsEndpoints.cs. POST /pull's fire-and-forget dispatch becomes a Command (same treatment as IndexRepositoryCommand, SYNTH-61 — it's the same background-job pattern); the two GETs (models list, pull status) can stay thin Controller actions unless wrapping them as Queries reads cleaner, your call."
limits: "max_iterations=25; max_minutes=120"
labels: [backend, refactor, architecture]
---

# SYNTH-70: Merge OllamaModelEndpoints into EmbeddingSettingsController (issue #82, slice 15)

## Context
Continuing the Controllers conversion. `OllamaModelEndpoints.cs` maps three routes under
`/settings/embedding/ollama/*` — the same route family `EmbeddingSettingsController` (created in
`SYNTH-69`) already owns `/settings/embedding` for. This slice merges them into that same
Controller class rather than creating a separate one, and moves the pull-dispatch logic into the
CQRS layer, mirroring `IndexRepositoryCommand`'s fire-and-forget background-job pattern exactly
(reserve a tracker slot synchronously, dispatch a detached `Task.Run` with `CancellationToken.None`,
report progress/completion/failure onto the tracker).

- `GET /settings/embedding/ollama/models` — proxies Ollama's `GET {endpoint}/api/tags`, resolving
  the endpoint via `ConfigurableEmbeddingGenerator.ResolveOllamaEndpoint`.
- `POST /settings/embedding/ollama/pull` — reserves the single pull slot via `IOllamaPullTracker`
  (409 if busy), dispatches a detached background pull that streams Ollama's own
  newline-delimited-JSON `/api/pull` response and updates the tracker per line, returns 202
  immediately. This is the piece with real orchestration logic worth a Command.
- `GET /settings/embedding/ollama/pull/status` — reads `IOllamaPullTracker.Current`.

## What to do
1. Create `PullOllamaModelCommand`/result type and
   `PullOllamaModelCommandHandler : ICommandHandler<PullOllamaModelCommand, ...>` in
   `Synth.Application` (e.g. `src/Synth.Application/Embeddings/PullOllamaModelCommandHandler.cs`),
   constructor-injecting `IHttpClientFactory`, `IOptionsMonitor<EmbeddingOptions>`,
   `OllamaConnection` (check whether this Aspire-supplied type needs its own port or is already
   Application-safe — if it's a plain DTO/connection-string holder with no Infrastructure
   dependency, it can be used directly; if not, add a thin port), `IOllamaPullTracker`, and
   `ILoggerFactory`. Move `PullAsync`/`ParseProgressLine`/`BuildOllamaUri`/the tracker-reservation
   and background-dispatch logic into the handler essentially unchanged — same validation (empty
   model → error, no endpoint configured → error), same 409-via-`TryStart`-returning-false
   semantics, same detached-task/`CancellationToken.None` reasoning as `IndexRepositoryCommandHandler`.
2. Register the handler in DI.
3. Add to the existing `src/Synth.Api/Embeddings/EmbeddingSettingsController.cs` (from `SYNTH-69`,
   don't create a new file):
   - `[HttpGet("/settings/embedding/ollama/models")]` — proxies the Ollama tags endpoint (thin
     Controller action, or a small Query if that reads cleaner — your call).
   - `[HttpPost("/settings/embedding/ollama/pull")]` — dispatches the new command, same
     400/409/202 mapping as today.
   - `[HttpGet("/settings/embedding/ollama/pull/status")]` — reads `IOllamaPullTracker.Current`.
4. Delete `src/Synth.Api/Embeddings/OllamaModelEndpoints.cs` and remove its
   `app.MapOllamaModelEndpoints();` call from `Program.cs`. `OllamaPullRequest` (the request DTO)
   and any response record types move to `Synth.Application` alongside the new command.
5. Update `tests/Synth.Api.Tests/OllamaModelEndpointsTests.cs` — rename/fold into
   `EmbeddingSettingsControllerTests.cs` if that reads cleaner; move the pull-orchestration/
   progress-parsing unit tests into a new
   `tests/Synth.Application.Tests/Embeddings/PullOllamaModelCommandHandlerTests.cs`.

## Acceptance
`dotnet build`/`dotnet test` on `Synth.slnx` stay green. `OllamaModelEndpoints.cs` no longer exists;
its three routes are served from `EmbeddingSettingsController.cs`.
`ICommandHandler<PullOllamaModelCommand, ...>` is implemented in `Synth.Application`. All three
routes behave identically to before (same status codes, same fire-and-forget + polling semantics,
no streaming/SSE introduced anywhere).

## Out of scope
- `RawSettingsEndpoints.cs`, `CallGraphEndpoints.cs`, `LogsEndpoints.cs` — separate later slices.
- Any change to `IOllamaPullTracker`, `ConfigurableEmbeddingGenerator`, or Ollama's actual API
  contract.
