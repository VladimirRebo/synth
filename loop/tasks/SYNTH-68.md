---
id: SYNTH-68
summary: "Convert VcsSettingsEndpoints to a Controller + UpdateVcsSettingsCommand (issue #82, slice 13/many)"
status: open
acceptance_command: "test -f src/Synth.Api/Vcs/VcsSettingsController.cs && ! test -f src/Synth.Api/Vcs/VcsSettingsEndpoints.cs && grep -rq 'ICommandHandler<UpdateVcsSettingsCommand' src/Synth.Application/"
acceptance_criterion: ""
boundaries: "Only convert VcsSettingsEndpoints.cs. Do not touch EmbeddingSettingsEndpoints.cs, OllamaModelEndpoints.cs, or RawSettingsEndpoints.cs — separate later slices. GET stays a thin Controller read; PUT's probe-then-persist logic (the real business logic here) moves into a Command, same treatment as SYNTH-61/SYNTH-67."
limits: "max_iterations=25; max_minutes=120"
labels: [backend, refactor, architecture]
---

# SYNTH-68: VcsSettingsEndpoints -> Controller + UpdateVcsSettingsCommand (issue #82, slice 13)

## Context
Continuing the Controllers conversion (slices 10-12 merged: `SearchController`,
`IndexingController`, `RepositoriesController`+`DeleteCollectionCommand`). `VcsSettingsEndpoints.cs`
maps `GET`/`PUT /settings/vcs`:
- `GET` — a simple read (mask `VcsOptions` from `IOptionsMonitor<VcsOptions>`). Stays a thin
  Controller action, same judgment call as every prior GET conversion.
- `PUT` — real logic worth a Command: probes any newly-set, non-empty GitHub/GitLab token against
  the provider's API before persisting (rejecting with 400 if the token doesn't authenticate),
  applies a partial update through `ConfigSectionUpdater`, and returns the masked result.

## What to do
1. Create `UpdateVcsSettingsCommand`/`UpdateVcsSettingsResult` (or reuse `VcsSettingsResponse` as
   the result type — your call) and `UpdateVcsSettingsCommandHandler : ICommandHandler<UpdateVcsSettingsCommand, ...>`
   in `Synth.Application` (e.g. `src/Synth.Application/Vcs/UpdateVcsSettingsCommandHandler.cs`),
   constructor-injecting `ConfigSectionUpdater`, `IOptionsMonitor<VcsOptions>`, and
   `IHttpClientFactory`. Move the PUT handler's entire body — the probe-before-persist logic
   (`TryGetNewToken`/`ProbeGitHubAsync`/`ProbeGitLabAsync`/`ProbeAsync`), the partial-update
   application (`ApplyTokenUpdate`, `TryGetPropertyIgnoreCase`, `ToStringValueOrNull`), and the
   masking (`Mask`) — into the handler essentially unchanged. The command's input needs to carry the
   raw request body shape (a `JsonElement`, or a more structured DTO if that reads cleaner — your
   call, but the probe/partial-update logic depends on distinguishing "field absent" from "field
   present and null" from "field present and a string", so whatever shape you choose must preserve
   that three-way distinction).
2. Register the handler in DI (wherever `AddSynthVcs` or similar already lives).
3. Create `src/Synth.Api/Vcs/VcsSettingsController.cs`: a `[ApiController]` with:
   - `[HttpGet("/settings/vcs")]` — same as today, reading `IOptionsMonitor<VcsOptions>` directly
     and masking.
   - `[HttpPut("/settings/vcs")]` — dispatches the new command, maps a validation failure to 400
     (same error shape as today) and success to `Ok(...)` with the masked result.
4. Delete `src/Synth.Api/Vcs/VcsSettingsEndpoints.cs` and remove its
   `app.MapVcsSettingsEndpoints();` call from `Program.cs`. `VcsSettingsResponse`/
   `ProviderTokenStatus` (the response record types) move wherever makes sense — likely alongside
   the command/handler in `Synth.Application`, since they're the shape the command produces.
5. Update `tests/Synth.Api.Tests/VcsSettingsEndpointTests.cs` — rename to
   `VcsSettingsControllerTests.cs` if that reads better; move the probe/partial-update-logic unit
   tests (if any exist independent of the HTTP-level tests) into a new
   `tests/Synth.Application.Tests/Vcs/UpdateVcsSettingsCommandHandlerTests.cs`.

## Acceptance
`dotnet build`/`dotnet test` on `Synth.slnx` stay green. `VcsSettingsController.cs` exists;
`VcsSettingsEndpoints.cs` no longer exists. `ICommandHandler<UpdateVcsSettingsCommand, ...>` is
implemented in `Synth.Application`. `GET`/`PUT /settings/vcs` behave identically to before —
including the probe-before-persist behavior (a bad token still gets rejected with 400 and nothing
persisted).

## Out of scope
- `EmbeddingSettingsEndpoints.cs`, `OllamaModelEndpoints.cs`, `RawSettingsEndpoints.cs` — separate
  later slices.
- Wrapping `GET /settings/vcs` in a Query type.
- Any change to `VcsOptions`, `ConfigSectionUpdater`, or the actual probe URLs/timeout.
