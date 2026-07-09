---
id: SYNTH-28
summary: "Persist logs to a capped Mongo collection, with in-memory fallback"
status: open
acceptance_command: "dotnet build src/Synth.slnx --nologo -v q && dotnet test src/Synth.slnx --nologo -v q && grep -riq 'class MongoLogEntryStore' src/Synth.Api/"
acceptance_criterion: ""
boundaries: "Only add Mongo-backed log persistence and the abstraction it sits behind. Do not change GET /logs's query parameters/filter semantics (level/since/search stay exactly as SYNTH-24 defined them) or its route. Do not touch the Vue LogsPanel — it already calls GET /logs and needs no changes regardless of what's behind that endpoint."
limits: "max_iterations=25; max_minutes=120"
labels: [backend, logging, mongo]
---

# SYNTH-28: Persist logs to Mongo (capped collection), in-memory fallback

## Context
`SYNTH-23`/`SYNTH-24` (issue #27, already shipped) gave Synth a `RingBufferLogSink` — purely
in-memory, capacity 1000, lost on every restart. Vladimir wants logs to actually persist in Mongo
(2026-07-09), matching the "Mongo when a connection is configured, in-memory fallback otherwise"
duality every other store in this repo already follows (`ICodeChunkStore`, `IConfigStore`,
`IRepositoryRegistry`, and `SYNTH-25`'s `ICodeGraphStore`). Precedent for the storage shape: Sonar's
own audit log (documented in the Jarvis wiki, concept `rbac-audit-pattern`) uses a **capped Mongo
collection** — fixed size by document count and bytes, oldest entries self-evict, no separate
retention job needed, sorted by `$natural` (insertion order) rather than an index. Reuse that same
shape here instead of inventing a different persistence scheme.

**Write-volume consideration, read before implementing:** a busy Synth instance emits many log
lines per request (a single `/health` call alone produced ~8 Information-level lines in a live
test). Writing each one *synchronously* to Mongo from inside `Serilog.Core.ILogEventSink.Emit`
(a synchronous method) would add I/O latency to the hot path of every request. Keep `Emit` itself
fast and non-blocking: hand entries off to a bounded background queue
(`System.Threading.Channels.Channel<LogEntry>` is the natural fit) and drain it from a background
`IHostedService`/`BackgroundService` that calls into whichever store is active. This applies
uniformly whether the active store is Mongo or in-memory — one code path, not two.

## What to do
1. Add `Synth.Api/Logging/ILogEntryStore.cs`:
   - `Task RecordAsync(LogEntry entry, CancellationToken ct = default)`
   - `Task<IReadOnlyList<LogEntry>> SnapshotAsync(CancellationToken ct = default)` — most recent
     entries, oldest first (same ordering `RingBufferLogSink.Snapshot()` already returns, since
     `LogsEndpoints`'s existing level/since/search filtering logic expects that shape and must keep
     working unchanged).
2. Add `Synth.Api/Logging/InMemoryLogEntryStore.cs`: move `RingBufferLogSink`'s existing
   `Queue<LogEntry>` + lock + capacity-eviction logic here (capacity 1000, same default). This
   becomes the fallback when no Mongo connection is configured.
3. Add `Synth.Api/Logging/MongoLogEntryStore.cs`: a **capped** Mongo collection (e.g. `logs`,
   `CreateCollectionOptions { Capped = true, MaxSize = 16 * 1024 * 1024, MaxDocuments = 20_000 }` —
   matching Sonar's documented `auth_audit` sizing) in the same database `SYNTH-25`/this task's
   sibling rename work uses (check what that database is now called before hardcoding the old
   `"synthconfig"` name — see the note below). `RecordAsync` inserts one document per entry;
   `SnapshotAsync` reads with `.Sort(Builders<BsonDocument>.Sort.Ascending("$natural"))` (or however
   this repo's Mongo driver version expresses natural-order sort) capped to a reasonable read size
   (matching `InMemoryLogEntryStore`'s capacity, e.g. the most recent 1000, so client-facing
   behavior doesn't change even though far more history is retained on disk). Both methods swallow
   connection failures and degrade gracefully — same "no live Mongo required in tests/dev" guarantee
   every other Mongo-backed piece in this repo has.
4. Add `Synth.Api/Logging/LogEntryStoreSink.cs`: a thin `Serilog.Core.ILogEventSink` that converts
   `LogEvent` → `LogEntry` (reuse the exact conversion `RingBufferLogSink.Emit` already does) and
   writes it to a bounded channel (drop-oldest or drop-newest on overflow — pick one, document which,
   this is a live tail not a guaranteed-delivery log shipper).
5. Add a `BackgroundService` that reads from that channel and calls `ILogEntryStore.RecordAsync` per
   entry (or in small batches if that's meaningfully simpler — don't over-engineer batching for a
   personal local tool).
6. Wire DI selection (new or extended `Synth.Api/Logging/LoggingServiceExtensions.cs`): Mongo-backed
   store when a connection string is present, `InMemoryLogEntryStore` otherwise — copy the exact
   selection pattern `VcsServiceExtensions.CreateRegistry`/`SYNTH-25`'s graph-store wiring already
   use. Update `Program.cs`'s Serilog setup to use `LogEntryStoreSink` instead of constructing
   `RingBufferLogSink` directly, and update `LogsEndpoints` (`SYNTH-24`) to depend on
   `ILogEntryStore` instead of the concrete `RingBufferLogSink` — the query parameters/filtering
   logic (`level`/`since`/`search`) stay exactly as they are, only the data source changes.
7. **Database naming note:** a sibling piece of work (outside this task) may have just renamed the
   `synthconfig` Aspire database resource to something else, since it was about to accumulate a
   third unrelated collection (call-graph edges) beyond its original config-only purpose. Check
   `src/Synth.AppHost/AppHost.cs` and `ConfigStoreExtensions.cs`'s `GetConnectionString(...)` call for
   the *current* database resource name before writing `MongoLogEntryStore` — don't assume
   `"synthconfig"` without checking, and don't re-introduce the old name if it's been renamed.
8. Tests: `InMemoryLogEntryStore` round-trip + capacity eviction (mirror `RingBufferLogSinkTests`,
   which this supersedes — update or remove those tests to match wherever this logic now lives, don't
   leave duplicate/dead tests behind). `MongoLogEntryStore`: follow whatever convention this repo
   already uses for testing Mongo-backed stores without a live connection (check `SYNTH-25`'s tests
   for the freshest precedent, since it was written just before this task). `LogsEndpoints` tests
   (`SYNTH-24`'s existing `LogsEndpointTests.cs`) should keep passing with only their store
   substitution changed (inject a fake/in-memory `ILogEntryStore` instead of a raw
   `RingBufferLogSink`) — filter behavior itself must not regress.

## Acceptance
`dotnet build`/`dotnet test` on `src/Synth.slnx` stay green, `MongoLogEntryStore` exists in
`Synth.Api`, `GET /logs`'s existing filter behavior (level/since/search) is unchanged, `Emit` stays
non-blocking (writes go through a background channel, not inline Mongo I/O), and logs persist across
a store re-creation when Mongo is configured (a `SnapshotAsync` after a fresh `MongoLogEntryStore`
instance still sees previously recorded entries, proving durability, not just in-process capture).

## Out of scope
- Changing `GET /logs`'s route or query parameters.
- The Vue `LogsPanel` — no client changes needed.
- Type-hierarchy/call-graph work (unrelated, see `SYNTH-25`..`SYNTH-27`).
