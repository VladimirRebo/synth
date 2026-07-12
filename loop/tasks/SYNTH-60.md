---
id: SYNTH-60
summary: "Retire empty Synth.Core + Synth.Core.Tests (issue #82, slice 8/many)"
status: open
acceptance_command: "! test -d src/Synth.Core && ! test -d tests/Synth.Core.Tests && ! grep -q 'Synth.Core.csproj' Synth.slnx"
acceptance_criterion: ""
boundaries: "Slice 8 of issue #82. Synth.Core has held zero .cs files since SYNTH-58 (GitRepoService moved out) — every other slice already moved its content to Synth.Domain/Synth.Application/Synth.Infrastructure. This task is pure removal + one test-file relocation, not a code change. Do not touch any production code beyond removing dangling ProjectReferences."
limits: "max_iterations=15; max_minutes=90"
labels: [backend, refactor, architecture]
---

# SYNTH-60: Retire Synth.Core + Synth.Core.Tests (issue #82, slice 8)

## Context
`Synth.Core` has been empty (zero `.cs` files) since `SYNTH-58` moved `GitRepoService.cs` out to
`Synth.Infrastructure` — every other type that used to live in `Synth.Core` was already relocated to
`Synth.Domain` (slice 1) or `Synth.Application` (slice 2). The project directory and its `.csproj`
are just dead weight now.

`tests/Synth.Core.Tests` similarly has only one file left: `IndexingPipelineTests.cs`, which tests
`IndexingPipeline` — an `Synth.Application` type since slice 2. It belongs in
`tests/Synth.Application.Tests` alongside the rest of Application's tests, matching the pattern every
other slice already followed (tests live with the layer whose type they primarily exercise).

## What to do
1. Move `tests/Synth.Core.Tests/IndexingPipelineTests.cs` into
   `tests/Synth.Application.Tests/IndexingPipelineTests.cs` (no namespace change needed if it's
   already in a namespace unrelated to the physical Core/Application split — check and adjust only
   if needed).
2. Delete `tests/Synth.Core.Tests/` entirely (the `.csproj` and any remaining files/folders).
3. Delete `src/Synth.Core/` entirely (the `.csproj` — it has no `.cs` files to worry about losing).
4. Remove the `Synth.Core.csproj` `<Project Path>` entry from `Synth.slnx`, and the
   `Synth.Core.Tests.csproj` entry too.
5. Remove the dangling `<ProjectReference Include="../Synth.Core/Synth.Core.csproj" />` (or
   equivalent relative path) from `src/Synth.Api/Synth.Api.csproj` and from
   `tests/Synth.Application.Tests/Synth.Application.Tests.csproj` (needs a reference to
   `Synth.Application.csproj` itself already; add nothing new — `IndexingPipelineTests.cs`'s other
   dependencies, e.g. the chunker projects and `Synth.Infrastructure`, should already be referenced
   there from earlier slices, verify and add only what's actually missing to compile after the
   move).
6. Grep the whole solution (`src/`, `tests/`, `Synth.slnx`, every `.csproj`) for any remaining
   `Synth.Core` reference and remove/fix it.

## Acceptance
`dotnet build`/`dotnet test` on `Synth.slnx` stay green — full solution, same 300 tests just with
`IndexingPipelineTests.cs` now under `Synth.Application.Tests`. `src/Synth.Core/` and
`tests/Synth.Core.Tests/` no longer exist. `Synth.slnx` contains no `Synth.Core.csproj` reference.

## Out of scope
- Any other test-project reorganization — this is purely resolving the one dangling empty project,
  not a general test-layout redesign (that's flagged as an explicit open question in issue #82,
  not decided).
- CQRS scaffolding or Controllers — next slices after this one.
