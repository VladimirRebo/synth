---
id: SYNTH-62
summary: "Repository registry: Mongo -> SQLite (issue #80, slice 2/many)"
status: open
acceptance_command: "test -f src/Synth.Infrastructure/Vcs/SqliteRepositoryRegistry.cs && ! test -f src/Synth.Infrastructure/Vcs/MongoRepositoryRegistry.cs && grep -q 'Microsoft.Data.Sqlite' src/Synth.Infrastructure/Synth.Infrastructure.csproj"
acceptance_criterion: ""
boundaries: "Registry only — do not touch ICodeGraphStore or ILogEntryStore implementations, those are separate later #80 slices reusing the same connection-factory pattern this task establishes. Do not remove the MongoDB.Driver/Aspire.MongoDB.Driver package references from Synth.Infrastructure.csproj or Synth.Api.csproj yet — MongoCodeGraphStore and MongoLogEntryStore still need them until their own slices land. This is the second of three SQLite migrations (registry, then graph, then logs) sharing one SQLite file by design — see the connection-factory note below."
limits: "max_iterations=25; max_minutes=120"
labels: [backend, refactor]
---

# SYNTH-62: Repository registry — SQLite (issue #80, slice 2)

## Context
Issue #80's target shape drops Mongo entirely from Synth. Slice 1 (`SYNTH-53`, merged) handled
config (→ file/env). This slice handles the repository registry (`IRepositoryRegistry`) — replacing
`MongoRepositoryRegistry` with a SQLite-backed implementation, now that `Synth.Infrastructure`
exists as its own project (issue #82's layering, fully merged) so the new store lands directly in
the right place.

Per issue #80's explicit design note, the call-graph store and log store also move to SQLite in
later slices — **decision for this task**: all three (registry, call-graph, logs) share **one
SQLite database file**, not three separate files, each owning its own table(s). This slice
establishes the shared connection-factory piece so the later two slices reuse it rather than
reinventing db-path resolution.

`MongoRepositoryRegistry` currently degrades gracefully on every operation (a connection failure is
swallowed, methods return empty/no-op results) since Mongo might be absent in local dev. SQLite is
embedded — there's no "unreachable server" case to swallow, so the new implementation can let
exceptions propagate normally instead of catch-and-degrade (this is a real behavior simplification
issue #80 is explicitly going for: no more infrastructure-dependent degrade paths).

## What to do
1. Add a `Microsoft.Data.Sqlite` package reference to `Synth.Infrastructure.csproj` (latest stable
   10.x version matching the other `Microsoft.Extensions.*`/`Microsoft.Data.*` package versions
   already pinned in this solution — check what's available, use a real published version).
2. Create `src/Synth.Infrastructure/SqliteConnectionFactory.cs` (or similar name/location, your
   call): resolves the db file path (default `~/.synth/synth.db`, mirroring `FileConfigStore`'s
   `~/.synth/config.json` default-path convention — same `.synth` directory, `Directory.CreateDirectory`
   on first use), exposes something like `SqliteConnection OpenConnection()` (opens a connection to
   that file, creating it if it doesn't exist — SQLite does this automatically). No schema-versioning
   machinery — each store creates its own table(s) with `CREATE TABLE IF NOT EXISTS` on first use,
   idempotent and migration-free (explicitly out of scope for issue #80 to build real migrations).
3. Create `src/Synth.Infrastructure/Vcs/SqliteRepositoryRegistry.cs` implementing
   `IRepositoryRegistry` (from `Synth.Domain.Vcs`) using a `repositories` table: columns for
   `Collection` (primary key), `SourceType`, `Source`, `Branch` (nullable), `LastIndexedAt`,
   `ChunkCount` — a real relational row per `RepositoryEntry`, not a JSON blob (unlike the old Mongo
   version, which had to store JSON due to Mongo's dotted-field-name restriction — SQLite has no such
   restriction, use real columns). `UpsertAsync` = `INSERT ... ON CONFLICT(Collection) DO UPDATE`,
   `DeleteAsync` returns whether a row was actually removed, `ListAsync` returns every row mapped
   back to `RepositoryEntry`.
4. Delete `src/Synth.Infrastructure/Vcs/MongoRepositoryRegistry.cs`.
5. Update `VcsServiceExtensions.cs`'s `CreateRegistry` (or equivalent) to always return the new
   `SqliteRepositoryRegistry` — drop the connection-string-present branching entirely, matching how
   `ConfigStoreExtensions.CreateStore` already unconditionally returns `FileConfigStore` (SYNTH-53).
   `InMemoryRepositoryRegistry` stays in the codebase (useful for unit tests that don't want file
   I/O) but is no longer wired into production DI by this method.
6. Tests: a `SqliteRepositoryRegistryTests.cs` in `tests/Synth.Infrastructure.Tests/` covering
   upsert/list/delete against a real temp-file SQLite db (create a fresh temp path per test, clean up
   after) — mirror whatever test style `FileConfigStoreTests.cs` already uses for its own
   temp-file-based tests. Existing `RepositoryRegistryTests.cs` (if it currently tests
   `MongoRepositoryRegistry` directly) gets replaced/adapted for the new implementation;
   `InMemoryRepositoryRegistry`'s own test coverage (if separate) stays as-is.

## Acceptance
`dotnet build`/`dotnet test` on `Synth.slnx` stay green. `SqliteRepositoryRegistry.cs` exists;
`MongoRepositoryRegistry.cs` no longer exists. `Synth.Infrastructure.csproj` references
`Microsoft.Data.Sqlite`. A real end-to-end round-trip (upsert an entry, list it back, delete it, list
again and confirm it's gone) passes against a real SQLite file, not just mocks.

## Out of scope
- `ICodeGraphStore` (call-graph) or `ILogEntryStore` (logs) — separate later #80 slices, reusing the
  connection-factory piece established here.
- Removing MongoDB package references — still needed by the graph/logs Mongo implementations.
- Any migration path for existing Mongo-backed registry data — issue #80 explicitly says start fresh,
  no migration tooling.
- Issue #82's CQRS/Controllers work — unrelated to this task.
