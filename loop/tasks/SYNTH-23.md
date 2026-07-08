---
id: SYNTH-23
summary: "Serilog + in-memory ring-buffer log sink"
status: done
acceptance_command: "dotnet build src/Synth.slnx --nologo -v q && dotnet test src/Synth.slnx --nologo -v q && grep -riq 'class RingBufferLogSink' src/Synth.Api/"
acceptance_criterion: ""
boundaries: "Only wire Serilog and the in-memory ring buffer that captures log events. Do not add the GET /api/logs endpoint (SYNTH-24) or touch the Vue client. Do not add SignalR/real-time push — Vladimir decided REST polling, no separate hub."
limits: "max_iterations=25; max_minutes=120"
labels: [backend, logging]
---

# SYNTH-23: Serilog + in-memory ring-buffer log sink

## Context
Issue #27 (Phase: Logging system + frontend) adds a way to see Synth's own logs from the UI.
Today `Synth.Api` has no logging system of its own — just the default ASP.NET Core
console logger wired through Aspire's `AddServiceDefaults` (`Synth.ServiceDefaults/Extensions.cs`,
which adds OpenTelemetry logging, not something a REST endpoint can read back). This task adds
Serilog (so structured log events exist to capture) plus a bounded in-memory sink that keeps the
most recent N entries queryable in-process — no database, no file, this is meant to be a live
"what's happening right now" view, not a durable audit log (that's a different concern, not this
task). Vladimir explicitly decided REST polling over SignalR for this feature (2026-07-08) — Synth
is a single local instance, a dedicated real-time hub is unwarranted complexity here.

## What to do
1. Add Serilog to `Synth.Api` (`Serilog.AspNetCore` covers `UseSerilog`/`WriteTo.Console` in one
   package; confirm the current version on nuget.org, matching this repo's habit of verifying exact
   package versions rather than guessing). Wire it in `Program.cs` via
   `builder.Host.UseSerilog((context, loggerConfig) => ...)`, called early (before other builder
   calls that might log), keeping console output so existing local-dev log visibility is unchanged.
2. Add `Synth.Api/Logging/LogEntry.cs`: an immutable record capturing what a log viewer needs —
   `Timestamp` (UTC), `Level` (string, e.g. `"Information"`/`"Warning"`/`"Error"`), `Message`
   (the rendered message, not the template), `Exception` (string? — the exception's `ToString()`
   when present, else null).
3. Add `Synth.Api/Logging/RingBufferLogSink.cs`: implements Serilog's `Serilog.Core.ILogEventSink`.
   Holds the most recent `capacity` entries (constructor parameter, default e.g. `1000`) in a
   thread-safe bounded structure (a lock around a `Queue<LogEntry>`/`LinkedList<LogEntry>`, evicting
   the oldest entry once at capacity — a `ConcurrentQueue` alone isn't quite enough since it has no
   built-in capacity cap, so pair it with a count check under a lock, or use a simple lock-guarded
   ring array; pick whichever is simplest to get right). Expose a read method (e.g.
   `IReadOnlyList<LogEntry> Snapshot()`) returning a stable copy so a concurrent reader never sees a
   half-mutated state.
4. Register `RingBufferLogSink` as a singleton in DI *and* as a Serilog sink for the same instance
   (construct it once, pass it both to `.WriteTo.Sink(instance)` in the Serilog config and to
   `builder.Services.AddSingleton(instance)`), so `SYNTH-24`'s endpoint can inject it directly rather
   than needing a second parallel logging path.
5. Tests (no live external log aggregator needed — this is entirely in-process): log a handful of
   entries at different levels through `ILogger<T>` (or directly through a `Serilog.ILogger` built
   around the sink in the test, whichever is easier to construct in isolation) and confirm they show
   up in `Snapshot()` with the right level/message; confirm capacity eviction (log more than
   `capacity` entries, confirm the oldest ones are gone and only the most recent `capacity` remain,
   in the right order); confirm thread-safety isn't trivially broken (a test that logs concurrently
   from multiple tasks and asserts the final count is sane, not a `Task.Delay`-based flakiness check).

## Acceptance
`dotnet build`/`dotnet test` on `src/Synth.slnx` stay green, `RingBufferLogSink` exists in
`Synth.Api`, console logging still works as before (Serilog wired, not just added as a dependency),
and the sink correctly caps at its configured capacity while remaining queryable via `Snapshot()`.

## Out of scope
- `GET /api/logs` HTTP endpoint — `SYNTH-24`.
- Vue client — done directly after the backend lands.
- SignalR/real-time push, any persistence beyond the in-memory buffer.
