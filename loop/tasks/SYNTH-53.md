---
id: SYNTH-53
summary: "Drop MongoConfigStore, config is file/env only (issue #80, slice 1/many)"
status: open
acceptance_command: "! find src -iname 'MongoConfigStore.cs' | grep -q . && ! grep -q 'MongoConfigStore\|MongoUrl.Create\|MongoClientSettings' src/Synth.Api/Configuration/ConfigStoreExtensions.cs"
acceptance_criterion: ""
boundaries: "Config only. Do not touch MongoCodeGraphStore, MongoRepositoryRegistry, or MongoLogEntryStore — those are separate #80 slices, queued for after issue #82's Infrastructure project exists (per the issue's own sequencing note), not this task. Do not remove the MongoDB.Driver/Aspire.MongoDB.Driver package references from Synth.Api.csproj — the other three Mongo-backed stores still need them until their own slices land. This task only removes the Mongo *branch* for config specifically."
limits: "max_iterations=15; max_minutes=90"
labels: [backend, refactor]
---

# SYNTH-53: Drop MongoConfigStore (issue #80, slice 1)

## Context
Issue #80's target shape drops Mongo entirely from Synth, replacing its four current uses (config,
repository registry, call-graph, logs) with file/SQLite alternatives so a distributed product never
forces a client to run a Mongo server. Config is the one piece that's genuinely independent of
issue #82's layering work (it's a same-file logic change, not a project-structure move), so it can
land now rather than waiting for `Synth.Infrastructure` to exist. The other three Mongo uses
(registry/graph/logs → SQLite) do depend on the layering landing first and are separate,
later tasks.

`FileConfigStore` (`src/Synth.Api/Configuration/FileConfigStore.cs`) already exists and already
works — `ConfigStoreExtensions.CreateStore` currently prefers `MongoConfigStore` whenever a
`synthdata` connection string is present, falling back to `FileConfigStore` only when it's absent.
This task just removes that preference: config becomes file/env only, unconditionally.

## What to do
1. Delete `src/Synth.Api/Configuration/MongoConfigStore.cs`.
2. In `src/Synth.Api/Configuration/ConfigStoreExtensions.cs`, simplify `CreateStore` to always
   return `new FileConfigStore()` — remove the connection-string lookup, the `MongoUrl`/
   `MongoClientSettings` construction, and the `ConnectionName` constant entirely. `AddSynthConfigStore`
   itself (the DI wiring, `ConfigSectionUpdater` registration, `ConfigStoreConfigurationSource`,
   environment-variable layering) does not need to change — only `CreateStore`'s body.
3. Remove the now-unused `using MongoDB.Driver;` from `ConfigStoreExtensions.cs` if nothing else in
   the file needs it.
4. Optional/cosmetic: `MongoRepositoryRegistry.cs`, `MongoCodeGraphStore.cs`,
   `InMemoryRepositoryRegistry.cs` have doc-comments referencing `MongoConfigStore` by name as a
   "same pattern as" comparison — fine to leave as historical context, or update to reference
   `FileConfigStore` instead if it reads better; not required for acceptance.

## Acceptance
`dotnet build`/`dotnet test` stay green. `MongoConfigStore.cs` no longer exists.
`ConfigStoreExtensions.cs` no longer references Mongo at all for config. Config still works exactly
as before for the file/env path (which was already the fallback, now it's the only path) — verify
by running the existing config-related tests, no new test is required since this removes a code
path rather than adding one.

## Out of scope
- Repository registry, call-graph, or logs moving to SQLite — those are later #80 slices, queued
  for after `Synth.Infrastructure` exists (issue #82's foundational work).
- Removing the MongoDB driver package references — other stores still need them.
- Any change to `FileConfigStore` itself.
