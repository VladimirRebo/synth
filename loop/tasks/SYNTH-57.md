---
id: SYNTH-57
summary: "Synth.Infrastructure: move Embeddings (ConfigurableEmbeddingGenerator + wiring) (issue #82, slice 5/many)"
status: open
acceptance_command: "test -f src/Synth.Infrastructure/Embeddings/ConfigurableEmbeddingGenerator.cs && ! test -f src/Synth.Api/Embeddings/ConfigurableEmbeddingGenerator.cs"
acceptance_criterion: ""
boundaries: "Slice 5 of issue #82 (slices 1-4 merged). Only move the 3 files listed below. Do not move OllamaModelEndpoints.cs or EmbeddingSettingsEndpoints.cs — both are Minimal-API endpoint files (Api-layer), stay in Synth.Api/Embeddings/. Do not touch Vcs/, Logging/, Health/ files — separate later slices."
limits: "max_iterations=25; max_minutes=120"
labels: [backend, refactor, architecture]
---

# SYNTH-57: Synth.Infrastructure — Embeddings (issue #82, slice 5)

## Context
Continuing issue #82; slices 1-4 are merged (`Synth.Domain`, `Synth.Application`,
`Synth.Infrastructure` with Storage+Graph+Configuration). `src/Synth.Api/Embeddings/` has 5 files;
3 are concrete implementation/wiring (moving), 2 are Api-layer endpoint files (staying).

**Namespace convention** (same as prior slices): moved types get `Synth.Infrastructure.*`
namespace (e.g. `Synth.Api.Embeddings.ConfigurableEmbeddingGenerator` →
`Synth.Infrastructure.Embeddings.ConfigurableEmbeddingGenerator`).

## What to do
1. Move these files from `src/Synth.Api/Embeddings/` into `src/Synth.Infrastructure/Embeddings/`
   (namespace `Synth.Api.Embeddings` → `Synth.Infrastructure.Embeddings`):
   - `ConfigurableEmbeddingGenerator.cs`
   - `EmbeddingGeneratorFactory.cs`
   - `EmbeddingServiceExtensions.cs`
2. Leave `OllamaModelEndpoints.cs` and `EmbeddingSettingsEndpoints.cs` in
   `src/Synth.Api/Embeddings/` — both are Minimal-API endpoint files, Api-layer, not moving. Update
   their `using` directives for the moved types' new namespace.
3. Fix every `using Synth.Api.Embeddings` across the whole solution that now needs
   `using Synth.Infrastructure.Embeddings` for the moved types (the two endpoint files staying in
   `Synth.Api.Embeddings` namespace don't need a namespace change themselves, just possibly a new
   `using` to reach the moved types).
4. `Program.cs` calls `builder.AddSynthEmbeddings()` (or similarly named) — fix its `using` if
   needed. No new `ProjectReference` needed (`Synth.Api.csproj` already references
   `Synth.Infrastructure` from slice 3).
5. Move each moved type's test file(s) from `tests/Synth.Api.Tests/` into
   `tests/Synth.Infrastructure.Tests/` (already exists — add to it). Check current names:
   `ConfigurableEmbeddingGeneratorTests.cs`, `EmbeddingGeneratorRegistrationTests.cs`, and any test
   for `EmbeddingServiceExtensions` if one exists separately.

## Acceptance
`dotnet build`/`dotnet test` on `Synth.slnx` stay green — full solution.
`Synth.Infrastructure/Embeddings/ConfigurableEmbeddingGenerator.cs` exists;
`Synth.Api/Embeddings/ConfigurableEmbeddingGenerator.cs` no longer exists.
`Synth.Api/Embeddings/OllamaModelEndpoints.cs` and `EmbeddingSettingsEndpoints.cs` still exist
(correctly left behind).

## Out of scope
- Vcs (GitRepoService/registries), Logging, Health — separate later Infrastructure slices.
- `OllamaModelEndpoints.cs`/`EmbeddingSettingsEndpoints.cs` themselves — stay in Synth.Api.
- Introducing CQRS, Controllers, or any other Api-layer change.
