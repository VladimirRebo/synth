---
id: SYNTH-24
summary: "GET /api/logs — filterable read of the ring buffer (level, since, search)"
status: done
acceptance_command: "dotnet build src/Synth.slnx --nologo -v q && dotnet test src/Synth.slnx --nologo -v q && grep -riq '\"/logs\"' src/Synth.Api/"
acceptance_criterion: ""
boundaries: "Only add the GET /api/logs endpoint on top of SYNTH-23's RingBufferLogSink. Do not touch the Vue client (later, direct). Register the route bare (\"/logs\"), matching every other endpoint in this app (index, search, repositories, settings) — do NOT bake an \"/api\" prefix into the route string, that exact mistake was already made and fixed once for the Settings endpoints (see the fix commit for VcsSettingsEndpoints/EmbeddingSettingsEndpoints) and must not be repeated here."
limits: "max_iterations=20; max_minutes=100"
labels: [backend, logging]
---

# SYNTH-24: GET /api/logs — filterable read of the ring buffer

## Context
`SYNTH-23` added `RingBufferLogSink` (registered as a DI singleton) holding the most recent log
entries. This task exposes it over HTTP so the Vue client can poll it. Since this is REST polling
(not SignalR push, per Vladimir's decision), the endpoint needs to support "give me only what's new
since I last asked" as well as a plain "give me the recent log" for the initial load — both via the
same `since` query parameter.

**Route naming pitfall, read before writing the route string:** every other endpoint in this app
(`/index`, `/search`, `/repositories`, `/settings/vcs`, `/settings/embedding`) is mapped **without**
an `/api` prefix — the Vue client always calls `/api/...`, and Vite's dev proxy
(`src/client/vite.config.ts`) strips the `/api` prefix before forwarding to the backend. A recent
task (the Settings endpoints) accidentally registered routes as literal `/api/settings/...` and had
to be fixed after it 404'd through the client. Map this endpoint as bare `/logs`, not `/api/logs`.

## What to do
1. Add `Synth.Api/Logging/LogsEndpoints.cs`, mapping `GET /logs` with these optional query
   parameters:
   - `level` — a minimum level name (e.g. `"Warning"`); when present, only entries at that level or
     more severe are returned (Serilog's own level ordering: Verbose < Debug < Information <
     Warning < Error < Fatal — reuse `Serilog.Events.LogEventLevel`'s ordering/parsing rather than
     inventing a second one).
   - `since` — an ISO-8601 UTC timestamp; when present, only entries strictly after it are
     returned (this is what the client's polling loop uses to fetch just the new entries).
   - `search` — a case-insensitive substring match against the entry's `Message`.
   - No parameters: return the most recent entries as currently buffered (whatever `Snapshot()`
     currently holds — no separate "limit" parameter needed since the buffer itself is already
     capped by `SYNTH-23`'s capacity).
   Combine filters with AND when more than one is given. Return entries in chronological order
   (oldest first) so the client can append straightforwardly.
2. Map the endpoint in `Program.cs` next to the other `Map*Endpoints()` calls.
3. Tests: no live Serilog pipeline needed — construct a `RingBufferLogSink`, feed it a handful of
   `LogEvent`s (or entries via whatever the sink's public surface allows) spanning multiple levels
   and timestamps, then hit the endpoint through `WebApplicationFactory` (matching this repo's
   existing endpoint-test style, e.g. `IndexingEndpointTests`/`VcsSettingsEndpointTests`) and assert:
   no params returns everything currently buffered; `level=Warning` excludes Information/Debug
   entries; `since=<timestamp>` excludes entries at or before it; `search=<text>` matches only
   entries whose message contains it (case-insensitively); combining `level` and `search` narrows
   to the intersection.

## Acceptance
`dotnet build`/`dotnet test` on `src/Synth.slnx` stay green, `GET /logs` (bare, no `/api` prefix)
exists and correctly filters by level/since/search individually and combined.

## Out of scope
- Vue client — done directly after the backend lands.
- Any endpoint beyond the one filterable GET.
