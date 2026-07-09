---
id: SYNTH-25
summary: "ICodeGraphStore: Mongo-backed call-graph edge storage + in-memory fallback"
status: open
acceptance_command: "dotnet build src/Synth.slnx --nologo -v q && dotnet test src/Synth.slnx --nologo -v q && grep -riq 'interface ICodeGraphStore' src/Synth.Core/"
acceptance_criterion: ""
boundaries: "Only add the storage abstraction and its two implementations (Mongo + in-memory). Do not add call-edge extraction (SYNTH-26) or the MCP/REST tools (SYNTH-27). Do not touch the Vue client. Do not add type-hierarchy edges — call-graph only, per issue #33."
limits: "max_iterations=25; max_minutes=120"
labels: [backend, call-graph, storage]
---

# SYNTH-25: ICodeGraphStore — Mongo-backed call-graph storage

## Context
Issue #33 adds a structural call-graph alongside Synth's existing vector search: "who calls this
method / what does this method call", answered precisely instead of approximated by embeddings.
This task is pure storage plumbing — no extraction logic yet (that's `SYNTH-26`), no query tools
yet (that's `SYNTH-27`). Storage decision (2026-07-09, see issue #33): **reuse the existing Mongo
connection** (`synthconfig`, already used by `IConfigStore` and `IRepositoryRegistry` — see
`src/Synth.Api/Vcs/MongoRepositoryRegistry.cs`/`InMemoryRepositoryRegistry.cs`) rather than
introducing a new storage technology. Unlike those two stores, which persist one JSON blob per key,
the graph needs genuinely indexed queries in *both* directions (find callers of X, find callees of
X), so edges must be stored as real Mongo documents with real fields, not opaque JSON strings.

The graph is scoped per `collection`, matching every other piece of Synth's storage since `SYNTH-17`
(one Qdrant/local vector-store collection per indexed repo) — a call-graph query must never leak
edges from one indexed repo into another.

## What to do
1. Add `Synth.Core/Graph/CallEdge.cs`: `public sealed record CallEdge(string Collection, string
   Caller, string Callee, string SourceFile, int Line)`. `Caller`/`Callee` are qualified symbol
   names (the exact format — e.g. `Namespace.ClassName.MethodName` — is `SYNTH-26`'s concern; this
   task just needs a string field to store and index on).
2. Add `Synth.Core/Graph/ICodeGraphStore.cs`:
   - `Task ReplaceEdgesAsync(string collection, IReadOnlyList<CallEdge> edges, CancellationToken ct = default)`
     — replaces **all** edges for `collection` with the given set (delete-then-insert, or an
     equivalent atomic-enough swap). A full replace per index run, not an incremental upsert, so a
     re-index never leaves stale edges from methods/calls that no longer exist — simpler and more
     correct than trying to diff old vs. new edges.
   - `Task<IReadOnlyList<CallEdge>> FindCallersAsync(string collection, string symbol, CancellationToken ct = default)`
     — edges where `Callee == symbol` within `collection`.
   - `Task<IReadOnlyList<CallEdge>> FindCalleesAsync(string collection, string symbol, CancellationToken ct = default)`
     — edges where `Caller == symbol` within `collection`.
3. Add `Synth.Api/Graph/MongoCodeGraphStore.cs` (`Synth.Api` — mirrors where `MongoRepositoryRegistry`
   lives, since it needs the Mongo driver): a real Mongo collection (e.g. `call_edges`) with actual
   BSON fields (`Collection`, `Caller`, `Callee`, `SourceFile`, `Line`), **not** the JSON-blob-per-
   document pattern used by `MongoConfigStore`/`MongoRepositoryRegistry` — that pattern only works
   because those stores are single-document-per-key; this one needs to filter/index on `Caller`/
   `Callee` directly. Create compound indexes on `(Collection, Caller)` and `(Collection, Callee)`
   (`IMongoCollection<T>.Indexes.CreateManyAsync`, idempotent — safe to call every time the store is
   constructed). Reads/writes swallow connection failures and degrade gracefully (empty list / no-op)
   — same "no live Mongo required in tests/dev" guarantee as every other Mongo-backed piece in this
   repo.
4. Add `Synth.Api/Graph/InMemoryCodeGraphStore.cs`: process-local fallback (a
   `ConcurrentDictionary<string, List<CallEdge>>` keyed by collection, or similar), used when no
   Mongo connection is configured — mirrors `InMemoryRepositoryRegistry`.
5. Add `Synth.Api/Graph/CodeGraphServiceExtensions.cs`: `AddSynthCodeGraph(this WebApplicationBuilder
   builder)` registering `ICodeGraphStore` — Mongo when `GetConnectionString("synthconfig")` is
   present, in-memory otherwise (copy `VcsServiceExtensions.CreateRegistry`'s exact selection logic).
   Wire it into `Program.cs` next to `AddSynthVcs`/etc. (registration only — nothing consumes it yet
   until `SYNTH-26`/`SYNTH-27`).
6. Tests: cover both implementations. `InMemoryCodeGraphStore`: replace/find-callers/find-callees
   round-trip, collection isolation (two collections' edges never leak into each other's queries).
   `MongoCodeGraphStore`: if this repo already has a pattern for testing Mongo-backed stores without
   a live Mongo (check `MongoRepositoryRegistry`'s/`MongoConfigStore`'s existing tests first), follow
   it; if Mongo-backed store tests in this repo are skipped/require a live connection, match that same
   convention rather than inventing a new one.

## Acceptance
`dotnet build`/`dotnet test` on `src/Synth.slnx` stay green, `ICodeGraphStore` exists in
`Synth.Core`, both implementations round-trip correctly and stay isolated per collection, and
`ReplaceEdgesAsync` genuinely replaces (a second call with a different edge set leaves none of the
first set behind).

## Out of scope
- Call-edge extraction from source — `SYNTH-26`.
- MCP tools / REST endpoints querying the graph — `SYNTH-27`.
- Type-hierarchy edges, Vue client.
