---
id: SYNTH-58
summary: "Synth.Infrastructure: move Vcs (GitRepoService + registries) (issue #82, slice 6/many)"
status: open
acceptance_command: "test -f src/Synth.Infrastructure/Vcs/GitRepoService.cs && ! test -f src/Synth.Core/Vcs/GitRepoService.cs"
acceptance_criterion: ""
boundaries: "Slice 6 of issue #82 (slices 1-5 merged). Only move the 5 files listed below. Do not move RepositoryEndpoints.cs or VcsSettingsEndpoints.cs — both are Minimal-API endpoint files (Api-layer), stay in Synth.Api/Vcs/. GitRepoService.cs moving out empties Synth.Core entirely — do not delete the Synth.Core project itself in this task, that's a separate follow-up slice once this is confirmed to have landed cleanly."
limits: "max_iterations=25; max_minutes=120"
labels: [backend, refactor, architecture]
---

# SYNTH-58: Synth.Infrastructure — Vcs (issue #82, slice 6)

## Context
Continuing issue #82; slices 1-5 are merged (`Synth.Domain`, `Synth.Application`,
`Synth.Infrastructure` with Storage+Graph+Configuration+Embeddings). This slice moves the VCS
concrete implementations. `src/Synth.Api/Vcs/` has 6 files; 4 are concrete implementation/wiring
(moving), 2 are Api-layer endpoint files (staying). `src/Synth.Core/Vcs/GitRepoService.cs` also
moves — it's the **last file remaining in `Synth.Core`**, per issue #82's own classification
("shells out to `git`" = infrastructure, not application). Once this lands, `Synth.Core` is an
empty project (no `.cs` files) — leave it in place for now, retiring the project itself is a
separate follow-up task once this is confirmed to have landed cleanly.

**Namespace convention** (same as prior slices): moved types get `Synth.Infrastructure.*`
namespace (e.g. `Synth.Api.Vcs.MongoRepositoryRegistry` → `Synth.Infrastructure.Vcs.MongoRepositoryRegistry`,
`Synth.Core.Vcs.GitRepoService` → `Synth.Infrastructure.Vcs.GitRepoService`).

## What to do
1. Move these files from `src/Synth.Api/Vcs/` into `src/Synth.Infrastructure/Vcs/` (namespace
   `Synth.Api.Vcs` → `Synth.Infrastructure.Vcs`):
   - `MongoRepositoryRegistry.cs`
   - `InMemoryRepositoryRegistry.cs`
   - `VcsServiceExtensions.cs`
   - `OrphanCheckoutSweeper.cs`
2. Move `src/Synth.Core/Vcs/GitRepoService.cs` into `src/Synth.Infrastructure/Vcs/GitRepoService.cs`
   (namespace `Synth.Core.Vcs` → `Synth.Infrastructure.Vcs`).
3. Leave `RepositoryEndpoints.cs` and `VcsSettingsEndpoints.cs` in `src/Synth.Api/Vcs/` — both are
   Minimal-API endpoint files, Api-layer, not moving. Update their `using` directives for the moved
   types' new namespace (they currently reference `Synth.Core.Vcs`/`Synth.Api.Vcs` for
   `GitRepoService`/the registries).
4. `Synth.Core.csproj` keeps existing (now with zero `.cs` files of its own, just its
   `ProjectReference` to `Synth.Domain` — leave the `.csproj` itself untouched, don't try to remove
   it or its reference from `Synth.slnx` in this task).
5. Fix every `using Synth.Api.Vcs` (for the moved registry/sweeper files specifically, not
   `RepositoryEndpoints`/`VcsSettingsEndpoints`, which keep their namespace) and
   `using Synth.Core.Vcs` (for `GitRepoService`) across the whole solution that now needs
   `using Synth.Infrastructure.Vcs`.
6. Move each moved type's test file(s) into `tests/Synth.Infrastructure.Tests/` (already exists —
   add to it). Check current names/locations: `RepositoryRegistryTests.cs`,
   `OrphanCheckoutSweeperTests.cs` (likely in `tests/Synth.Api.Tests/`), `GitRepoServiceTests.cs`
   (likely in `tests/Synth.Core.Tests/`), and `GitRepoFixture.cs` if it's a shared test fixture used
   only by `GitRepoServiceTests.cs`.

## Acceptance
`dotnet build`/`dotnet test` on `Synth.slnx` stay green — full solution.
`Synth.Infrastructure/Vcs/GitRepoService.cs` exists; `Synth.Core/Vcs/GitRepoService.cs` no longer
exists. `Synth.Api/Vcs/RepositoryEndpoints.cs` and `VcsSettingsEndpoints.cs` still exist (correctly
left behind).

## Out of scope
- Logging, Health — separate later Infrastructure slices.
- `RepositoryEndpoints.cs`/`VcsSettingsEndpoints.cs` themselves — stay in Synth.Api.
- Deleting/retiring the now-empty `Synth.Core` project — separate follow-up task.
- Introducing CQRS, Controllers, or any other Api-layer change.
