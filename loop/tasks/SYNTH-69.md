---
id: SYNTH-69
summary: "Convert EmbeddingSettingsEndpoints to a Controller + UpdateEmbeddingSettingsCommand (issue #82, slice 14/many)"
status: open
acceptance_command: "test -f src/Synth.Api/Embeddings/EmbeddingSettingsController.cs && ! test -f src/Synth.Api/Embeddings/EmbeddingSettingsEndpoints.cs && grep -rq 'ICommandHandler<UpdateEmbeddingSettingsCommand' src/Synth.Application/"
acceptance_criterion: ""
boundaries: "Only convert EmbeddingSettingsEndpoints.cs. Do not touch OllamaModelEndpoints.cs in this task — it converts in the next slice and merges its actions into the same EmbeddingSettingsController this task creates (both are under /settings/embedding/*). Do not touch RawSettingsEndpoints.cs, CallGraphEndpoints.cs, or LogsEndpoints.cs. GET stays a thin Controller read; PUT's probe-then-persist logic moves into a Command, same treatment as SYNTH-61/67/68."
limits: "max_iterations=25; max_minutes=120"
labels: [backend, refactor, architecture]
---

# SYNTH-69: EmbeddingSettingsEndpoints -> Controller + Command (issue #82, slice 14)

## Context
Continuing the Controllers conversion (slices 10-13 merged, most recently `VcsSettingsController`+
`UpdateVcsSettingsCommand` — this task follows the exact same shape for embedding settings).
`EmbeddingSettingsEndpoints.cs` maps `GET`/`PUT /settings/embedding`:
- `GET` — a simple read (mask `EmbeddingOptions`). Stays a thin Controller action.
- `PUT` — probes a candidate config (builds one real embedding for a fixed probe string via
  `IEmbeddingGeneratorFactory`) before persisting; rejects with 400 on failure. Real logic worth a
  Command, same treatment as `UpdateVcsSettingsCommand` (`SYNTH-68`).

`OllamaModelEndpoints.cs` (routes under `/settings/embedding/ollama/*`) is explicitly **not** part
of this task — it's large enough to warrant its own slice, and will merge its actions into the same
`EmbeddingSettingsController` class this task creates (both live under the same `/settings/embedding`
route family) in the next task.

## What to do
1. Create `UpdateEmbeddingSettingsCommand`/`UpdateEmbeddingSettingsResult` and
   `UpdateEmbeddingSettingsCommandHandler : ICommandHandler<UpdateEmbeddingSettingsCommand, ...>` in
   `Synth.Application` (e.g. `src/Synth.Application/Embeddings/UpdateEmbeddingSettingsCommandHandler.cs`),
   constructor-injecting `IEmbeddingGeneratorFactory`, `IConfigSectionUpdater` (the port from
   `SYNTH-68` — reuse it, don't create a second one), and `IOptionsMonitor<EmbeddingOptions>`. Move
   the PUT handler's entire body — `BuildCandidate`, `ProbeAsync`, the partial-update application
   (`ApplyOllama`/`ApplyOpenAI`/`GetOrAddObject`), and `Mask` — into the handler essentially
   unchanged. Same three-way "absent/null/value" distinction as `UpdateVcsSettingsCommand` needs to
   be preserved for the request body shape.
2. Register the handler in DI (wherever `AddSynthEmbeddings` or similar lives).
3. Create `src/Synth.Api/Embeddings/EmbeddingSettingsController.cs`: a `[ApiController]` with
   `[HttpGet("/settings/embedding")]` (thin read) and `[HttpPut("/settings/embedding")]`
   (dispatches the command, same 400/200 mapping as today). This class is the one
   `OllamaModelEndpoints`'s actions will be added to in the next slice — leave room for that (no
   need to over-engineer now, just don't preclude it).
4. Delete `src/Synth.Api/Embeddings/EmbeddingSettingsEndpoints.cs` and remove its
   `app.MapEmbeddingSettingsEndpoints();` call from `Program.cs`. `EmbeddingSettingsResponse`/
   `OllamaSettingsView`/`OpenAISettingsView` (the response record types) move to `Synth.Application`
   alongside the command/handler.
5. Update `tests/Synth.Api.Tests/EmbeddingSettingsEndpointTests.cs` — rename to
   `EmbeddingSettingsControllerTests.cs` if that reads better; move probe/partial-update unit tests
   into a new `tests/Synth.Application.Tests/Embeddings/UpdateEmbeddingSettingsCommandHandlerTests.cs`.

## Acceptance
`dotnet build`/`dotnet test` on `Synth.slnx` stay green. `EmbeddingSettingsController.cs` exists;
`EmbeddingSettingsEndpoints.cs` no longer exists. `ICommandHandler<UpdateEmbeddingSettingsCommand, ...>`
is implemented in `Synth.Application`. `GET`/`PUT /settings/embedding` behave identically to before,
including the probe-before-persist behavior.

## Out of scope
- `OllamaModelEndpoints.cs` — next slice, merges into the same `EmbeddingSettingsController`.
- `RawSettingsEndpoints.cs`, `CallGraphEndpoints.cs`, `LogsEndpoints.cs` — separate later slices.
- Wrapping `GET /settings/embedding` in a Query type.
- Any change to `EmbeddingOptions`, `IEmbeddingGeneratorFactory`, or the probe text/timeout.
