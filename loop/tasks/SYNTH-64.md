---
id: SYNTH-64
summary: "Log store: Mongo -> SQLite, last #80 slice — Mongo fully removed from Synth"
status: open
acceptance_command: "test -f src/Synth.Infrastructure/Logging/SqliteLogEntryStore.cs && ! test -f src/Synth.Infrastructure/Logging/MongoLogEntryStore.cs && ! grep -q 'MongoDB' src/Synth.Infrastructure/Synth.Infrastructure.csproj && ! grep -q 'MongoDB' src/Synth.Api/Synth.Api.csproj"
acceptance_criterion: ""
boundaries: "This is the last #80 slice — once it lands, remove the MongoDB.Driver/Aspire.MongoDB.Driver package references from both Synth.Infrastructure.csproj and Synth.Api.csproj entirely (nothing else in the solution uses Mongo after this). Reuse the existing SqliteConnectionFactory (src/Synth.Infrastructure/SqliteConnectionFactory.cs) for the shared ~/.synth/synth.db file — do not create a second connection factory or db file. Do not touch issue #82's Controllers/CQRS work."
limits: "max_iterations=25; max_minutes=120"
labels: [backend, refactor]
---

# SYNTH-64: Log store — SQLite, last #80 slice (Mongo fully removed)

## Context
Final slice of issue #80. Slices 1-3 (config, registry, call-graph — all merged) moved everything
except logs off Mongo. This slice moves `ILogEntryStore`'s implementation to SQLite, reusing the
existing `SqliteConnectionFactory` (from `SYNTH-62`) for the shared `~/.synth/synth.db` file. Once
this lands, **nothing in Synth depends on Mongo at all** — remove the package references and this
is the actual completion of the "drop Mongo" architecture direction from issue #80.

`MongoLogEntryStore` uses a Mongo *capped collection*: bounded by both document count (20,000) and
byte size (16 MiB), oldest entries self-evict automatically, no separate retention job. SQLite has
no native capped-collection equivalent — emulate the document-count bound (byte-size capping is not
worth replicating exactly; nothing in `ILogEntryStore`'s contract promises a byte-size guarantee to
callers) by periodically deleting the oldest rows once the table exceeds `MaxDocuments` (20,000).
Doing a full eviction check on every single insert would be wasteful for a hot logging path — batch
it (e.g. only run the evict-oldest query every Nth insert, your call on N, or check row count cheaply
first and only delete when actually over budget) rather than an unconditional query per write.

## What to do
1. Create `src/Synth.Infrastructure/Logging/SqliteLogEntryStore.cs` implementing `ILogEntryStore`
   (from `Synth.Domain.Logging`), constructor-injected with the existing `SqliteConnectionFactory`.
   Table `logs`: `Id INTEGER PRIMARY KEY AUTOINCREMENT` (insertion order, the SQLite equivalent of
   Mongo's `$natural`), `Timestamp TEXT NOT NULL` (ISO-8601, `DateTimeOffset` round-trips via
   `"o"`/round-trip format string), `Level TEXT NOT NULL`, `Message TEXT NOT NULL`,
   `Exception TEXT NULL`. `CREATE TABLE IF NOT EXISTS` on first use, matching the other SQLite
   stores' pattern.
2. `RecordAsync`: `INSERT` the new row; then the bounded-eviction check described above (keep the
   `logs` table from growing past `MaxDocuments` = 20,000 rows — same constant Mongo used).
3. `SnapshotAsync`: `SELECT ... ORDER BY Id DESC LIMIT ReadLimit` (same `ReadLimit` constant as
   before, `InMemoryLogEntryStore.DefaultCapacity`), then reverse to oldest-first — exactly matching
   the existing ordering contract `LogsEndpoints`' filtering depends on.
4. Delete `src/Synth.Infrastructure/Logging/MongoLogEntryStore.cs`.
5. Update `LoggingServiceExtensions.cs` to always wire `SqliteLogEntryStore` — drop any
   connection-string branching, matching every prior SQLite slice. `InMemoryLogEntryStore` stays for
   tests, no longer wired into production DI.
6. **Remove Mongo entirely**: delete the `Aspire.MongoDB.Driver`/`MongoDB.Driver` package references
   from `Synth.Infrastructure.csproj` and `Synth.Api.csproj` (grep the whole solution first to
   confirm nothing else still references `MongoDB.*` — after slices 1-4, nothing should).
7. Tests: `SqliteLogEntryStoreTests.cs` in `tests/Synth.Infrastructure.Tests/` mirroring the style of
   `SqliteRepositoryRegistryTests.cs`/`SqliteCodeGraphStoreTests.cs` — cover record+snapshot
   round-trip, oldest-first ordering, and the eviction bound (insert more than `MaxDocuments` isn't
   practical in a fast unit test — instead test the eviction logic directly against a small
   configurable threshold, e.g. an internal/testable constructor overload or a much smaller constant
   exposed for testing, your call on the cleanest way to make this testable without slowing down the
   real test suite).

## Acceptance
`dotnet build`/`dotnet test` on `Synth.slnx` stay green. `SqliteLogEntryStore.cs` exists;
`MongoLogEntryStore.cs` no longer exists. Neither `Synth.Infrastructure.csproj` nor
`Synth.Api.csproj` references `MongoDB` anymore. A real round-trip against an actual SQLite file
passes: record several entries, snapshot returns them oldest-first.

## Out of scope
- Issue #82's Controllers/CQRS conversion — unrelated to this task.
- Any migration path for existing Mongo-backed log data — issue #80 explicitly says start fresh.
- Removing the Aspire AppHost's Mongo resource registration (`AddMongoDB(...)` in
  `Synth.AppHost`) — flag it as now-dead but leave the actual AppHost change for a quick separate
  follow-up once this is confirmed working, since removing local dev infrastructure wiring is a
  slightly different concern from the store implementations themselves.
