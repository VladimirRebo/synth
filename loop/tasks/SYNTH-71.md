---
id: SYNTH-71
summary: "Convert RawSettingsEndpoints to a Controller + ReplaceRawSettingsCommand (issue #82, slice 16/many)"
status: open
acceptance_command: "test -f src/Synth.Api/Configuration/RawSettingsController.cs && ! test -f src/Synth.Api/Configuration/RawSettingsEndpoints.cs && grep -rq 'ICommandHandler<ReplaceRawSettingsCommand' src/Synth.Application/"
acceptance_criterion: ""
boundaries: "Only convert RawSettingsEndpoints.cs. Do not touch CallGraphEndpoints.cs or LogsEndpoints.cs. GET stays a thin Controller read; PUT's replace-and-warn logic moves into a Command, same treatment as the prior Settings conversions."
limits: "max_iterations=20; max_minutes=100"
labels: [backend, refactor, architecture]
---

# SYNTH-71: RawSettingsEndpoints -> Controller + Command (issue #82, slice 16)

## Context
Continuing the Controllers conversion (slices 10-15 merged, most recently the three Settings
endpoint files). `RawSettingsEndpoints.cs` maps `GET`/`PUT /settings/raw`:
- `GET` — returns the whole stored config document as-is (unmasked). Thin Controller read.
- `PUT` — replaces the whole document (validated as well-formed JSON, no probe), then scans the
  persisted document for top-level keys that don't match a known config section and surfaces them
  via a non-fatal `X-Settings-Warnings` response header (the write already happened by the time this
  runs — it's purely informational). Real logic worth a Command, same treatment as the other three
  Settings conversions.

## What to do
1. Create `ReplaceRawSettingsCommand`/`ReplaceRawSettingsResult` (the result needs to carry both the
   persisted document text and the list of warning strings, since the Controller needs both — the
   document for the response body, the warnings for the header) and
   `ReplaceRawSettingsCommandHandler : ICommandHandler<ReplaceRawSettingsCommand, ReplaceRawSettingsResult>`
   in `Synth.Application` (e.g. `src/Synth.Application/Configuration/ReplaceRawSettingsCommandHandler.cs`),
   constructor-injecting `IConfigSectionUpdater` (the port from `SYNTH-68`, reuse it). Move
   `ReplaceDocumentAsync` call + `CollectUnknownKeyWarnings` into the handler essentially unchanged;
   `KnownSectionNames` moves with it.
2. Register the handler in DI.
3. Create `src/Synth.Api/Configuration/RawSettingsController.cs`: a `[ApiController]` with:
   - `[HttpGet("/settings/raw")]` — reads `IConfigSectionUpdater.LoadDocumentAsync` directly,
     returns `Content(document, "application/json")`.
   - `[HttpPut("/settings/raw")]` — reads the raw request body verbatim (same reasoning as today:
     echo exactly what was sent, not a re-serialized `JsonElement`), dispatches the command, maps a
     `FormatException`-equivalent failure to 400, sets the `X-Settings-Warnings` header from the
     result when non-empty, and returns the persisted document as the body.
4. Delete `src/Synth.Api/Configuration/RawSettingsEndpoints.cs` and remove its
   `app.MapRawSettingsEndpoints();` call from `Program.cs`. `WarningsHeader` constant moves wherever
   makes most sense (the Controller, since it's a wire-format detail, or `Synth.Application` next to
   the result type — your call).
5. Update `tests/Synth.Api.Tests/RawSettingsEndpointTests.cs` — rename to
   `RawSettingsControllerTests.cs` if that reads better; move the warning-detection unit tests into a
   new `tests/Synth.Application.Tests/Configuration/ReplaceRawSettingsCommandHandlerTests.cs`.

## Acceptance
`dotnet build`/`dotnet test` on `Synth.slnx` stay green. `RawSettingsController.cs` exists;
`RawSettingsEndpoints.cs` no longer exists. `ICommandHandler<ReplaceRawSettingsCommand, ...>` is
implemented in `Synth.Application`. `GET`/`PUT /settings/raw` behave identically to before,
including the `X-Settings-Warnings` header on an unknown top-level key.

## Out of scope
- `CallGraphEndpoints.cs`, `LogsEndpoints.cs` — separate later slices.
- Wrapping `GET /settings/raw` in a Query type.
- Any change to `IConfigSectionUpdater`'s own contract.
