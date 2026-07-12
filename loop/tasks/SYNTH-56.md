---
id: SYNTH-56
summary: "Synth.Infrastructure: move Configuration (FileConfigStore + wiring) (issue #82, slice 4/many)"
status: open
acceptance_command: "test -f src/Synth.Infrastructure/Configuration/FileConfigStore.cs && ! test -f src/Synth.Api/Configuration/FileConfigStore.cs"
acceptance_criterion: ""
boundaries: "Slice 4 of issue #82 (slices 1-3 merged: Synth.Domain, Synth.Application, Synth.Infrastructure/Storage+Graph). Only move the 5 files listed below. Do not move RawSettingsEndpoints.cs — it's a Minimal-API endpoint file (Api-layer), stays in Synth.Api/Configuration/. Do not touch Embeddings/, Vcs/, Logging/, Health/ files — separate later slices."
limits: "max_iterations=25; max_minutes=120"
labels: [backend, refactor, architecture]
---

# SYNTH-56: Synth.Infrastructure — Configuration (issue #82, slice 4)

## Context
Continuing issue #82; slices 1-3 are merged (`Synth.Domain`, `Synth.Application`,
`Synth.Infrastructure` with Storage+Graph). This slice adds the config-store implementation to
`Synth.Infrastructure`. `src/Synth.Api/Configuration/` currently has 6 files; 5 are concrete
implementation/wiring (moving), 1 is an Api-layer endpoint file (`RawSettingsEndpoints.cs`, stays).

**Namespace convention** (same as prior slices): moved types get `Synth.Infrastructure.*`
namespace (e.g. `Synth.Api.Configuration.FileConfigStore` → `Synth.Infrastructure.Configuration.FileConfigStore`).

## What to do
1. Move these files from `src/Synth.Api/Configuration/` into
   `src/Synth.Infrastructure/Configuration/` (namespace `Synth.Api.Configuration` →
   `Synth.Infrastructure.Configuration`):
   - `FileConfigStore.cs`
   - `ConfigStoreExtensions.cs`
   - `ConfigStoreConfigurationProvider.cs`
   - `ConfigStoreConfigurationSource.cs`
   - `ConfigSectionUpdater.cs`
2. Leave `RawSettingsEndpoints.cs` in `src/Synth.Api/Configuration/` — it's a Minimal-API endpoint
   file (`Map*` route registration), Api-layer, not moving. Update its `using` if it referenced any
   of the moved types by their old namespace.
3. `Synth.Api.csproj` already references `Synth.Infrastructure` (from slice 3) — no new
   `ProjectReference` needed unless something else changed. `Program.cs` calls
   `builder.AddSynthConfigStore()` — fix its `using` if needed.
4. Fix every `using Synth.Api.Configuration` across the whole solution that now needs
   `using Synth.Infrastructure.Configuration` for the moved types (note: `RawSettingsEndpoints.cs`
   and anything else legitimately in `Synth.Api.Configuration` namespace keeps that namespace — only
   fix `using`s that resolved to the *moved* types).
5. Move each moved type's test file(s) from `tests/Synth.Api.Tests/` into
   `tests/Synth.Infrastructure.Tests/` (the project already exists from slice 3 — add files to it,
   don't create a new project). Check current names: `FileConfigStoreTests.cs`,
   `ConfigSectionUpdaterTests.cs`, `ConfigStoreConfigurationSourceTests.cs`, and any test for
   `ConfigStoreExtensions`/`ConfigStoreConfigurationProvider` if one exists.

## Acceptance
`dotnet build`/`dotnet test` on `Synth.slnx` stay green — full solution.
`Synth.Infrastructure/Configuration/FileConfigStore.cs` exists;
`Synth.Api/Configuration/FileConfigStore.cs` no longer exists.
`Synth.Api/Configuration/RawSettingsEndpoints.cs` still exists (correctly left behind).

## Out of scope
- Embeddings, Vcs (GitRepoService/registries), Logging, Health — separate later Infrastructure
  slices.
- `RawSettingsEndpoints.cs` itself — stays in Synth.Api.
- Introducing CQRS, Controllers, or any other Api-layer change.
