---
id: SYNTH-63
summary: "Call-graph store: Mongo -> SQLite (issue #80, slice 3/many)"
status: open
acceptance_command: "test -f src/Synth.Infrastructure/Graph/SqliteCodeGraphStore.cs && ! test -f src/Synth.Infrastructure/Graph/MongoCodeGraphStore.cs"
acceptance_criterion: ""
boundaries: "Call-graph only — do not touch IRepositoryRegistry (already SQLite, SYNTH-62) or ILogEntryStore (separate later #80 slice). Do not remove MongoDB.Driver/Aspire.MongoDB.Driver package references — MongoLogEntryStore still needs them until its own slice. Reuse the existing SqliteConnectionFactory (src/Synth.Infrastructure/SqliteConnectionFactory.cs, from SYNTH-62) for the shared ~/.synth/synth.db file — do not create a second connection factory or a second db file."
limits: "max_iterations=25; max_minutes=120"
labels: [backend, refactor]
---

# SYNTH-63: Call-graph store — SQLite (issue #80, slice 3)

## Context
Continuing issue #80's Mongo removal. Slice 2 (`SYNTH-62`, merged) moved the repository registry to
SQLite and established `SqliteConnectionFactory` (`src/Synth.Infrastructure/SqliteConnectionFactory.cs`)
resolving a shared `~/.synth/synth.db` file — this slice reuses that same factory for the call-graph
store, not a second file.

This is explicitly the case issue #80 called out and Vladimir personally rejected an alternative for:
folding call-graph edges into Qdrant chunk payloads was considered and explicitly rejected ("мы не
строим приблизительные графы") — the graph stays a real, separately-indexed relational model with
proper `caller`/`callee` indexes. SQLite gives that directly.

`MongoCodeGraphStore` stores each `CallEdge` as its own document (unlike the registry's one-row-per-
collection shape) with compound indexes on `(Collection, Caller)` and `(Collection, Callee)` for both
query directions, and does a full delete-then-insert replace per collection on every re-index
(`ReplaceEdgesAsync`). As with the registry, SQLite is embedded — no "Mongo unreachable" case to
swallow, so the new implementation can let exceptions propagate instead of the old catch-and-degrade.

## What to do
1. Create `src/Synth.Infrastructure/Graph/SqliteCodeGraphStore.cs` implementing `ICodeGraphStore`
   (from `Synth.Domain.Graph`), using the existing `SqliteConnectionFactory` (constructor-injected,
   same as `SqliteRepositoryRegistry`). Table `call_edges`: `Id INTEGER PRIMARY KEY AUTOINCREMENT`,
   `Collection TEXT NOT NULL`, `Caller TEXT NOT NULL`, `Callee TEXT NOT NULL`,
   `SourceFile TEXT NOT NULL`, `Line INTEGER NOT NULL`, created with `CREATE TABLE IF NOT EXISTS` on
   first use (matching `SqliteRepositoryRegistry`'s pattern). Add two indexes:
   `CREATE INDEX IF NOT EXISTS idx_call_edges_caller ON call_edges(Collection, Callee)` (for
   `FindCallersAsync`, which filters by `Callee`) and
   `CREATE INDEX IF NOT EXISTS idx_call_edges_callee ON call_edges(Collection, Caller)` (for
   `FindCalleesAsync`, which filters by `Caller`) — matching Mongo's two compound indexes exactly.
2. `ReplaceEdgesAsync`: within a single transaction, `DELETE FROM call_edges WHERE Collection = ?`
   then bulk-insert the new edges (a full replace, same semantics as the Mongo version — never an
   incremental upsert).
3. `FindCallersAsync`/`FindCalleesAsync`: parameterized `SELECT` filtered by `(Collection, Callee)` /
   `(Collection, Caller)` respectively, mapped back to `CallEdge` records.
4. Delete `src/Synth.Infrastructure/Graph/MongoCodeGraphStore.cs`.
5. Update `CodeGraphServiceExtensions.cs` (or wherever `ICodeGraphStore` is currently selected — check
   whether it's Mongo-vs-InMemory branching like the old registry was) to always wire
   `SqliteCodeGraphStore`, dropping any connection-string branching, matching `VcsServiceExtensions`
   (SYNTH-62) and `ConfigStoreExtensions` (SYNTH-53). `InMemoryCodeGraphStore` stays in the codebase
   for tests, no longer wired into production DI.
6. Tests: `SqliteCodeGraphStoreTests.cs` in `tests/Synth.Infrastructure.Tests/` — mirror
   `SqliteRepositoryRegistryTests.cs`'s style (real temp-file SQLite db per test). Cover:
   `ReplaceEdgesAsync` then `FindCallersAsync`/`FindCalleesAsync` round-trip, a second
   `ReplaceEdgesAsync` call on the same collection fully replacing (not accumulating) prior edges,
   and collection-scoping (edges in one collection never returned for another).

## Acceptance
`dotnet build`/`dotnet test` on `Synth.slnx` stay green. `SqliteCodeGraphStore.cs` exists;
`MongoCodeGraphStore.cs` no longer exists. A real round-trip against an actual SQLite file (not
mocks) passes: replace edges for a collection, find callers/callees, replace again and confirm the
old edges are gone.

## Out of scope
- `ILogEntryStore` (logs) — the last #80 slice, separate task.
- Removing MongoDB package references — `MongoLogEntryStore` still needs them.
- Issue #82's Controllers conversion — unrelated to this task.
