---
id: SYNTH-50
summary: "Settings: Ollama model picker with pull progress"
status: open
acceptance_command: "dotnet build src/Synth.slnx --nologo -v q && dotnet test src/Synth.slnx --nologo -v q && grep -rq 'OllamaModelsEndpoints\\|OllamaModelEndpoints' src/Synth.Api/"
acceptance_criterion: ""
boundaries: "New backend endpoints live under /settings/embedding/ollama/... (bare, no /api prefix). Reuse this project's established fire-and-forget + polling pattern (IIndexJobTracker) for the pull job — do not build a streaming/SSE mechanism, this project's standing convention is REST polling. Do not touch the OpenAI settings path at all — this is Ollama-specific."
limits: "max_iterations=30; max_minutes=150"
labels: [backend, client, settings]
---

# SYNTH-50: Ollama model picker with pull progress

## Context
Part of issue #59. Switching the embedding model today means manually running `ollama pull <model>`
in a terminal, then typing the model name into `SettingsPanel.vue`'s free-text `ollamaModel` field
— no visibility into what models are already available locally, no way to trigger a pull from the
UI. Ollama's own HTTP API already provides what's needed: `GET {endpoint}/api/tags` (list locally
available models) and `POST {endpoint}/api/pull` (streaming newline-delimited-JSON pull progress).
The `{endpoint}` is whatever `EmbeddingOptions.Ollama.Endpoint` currently resolves to (the Settings
override if set, otherwise the Aspire-supplied connection — check `ConfigurableEmbeddingGenerator`/
`IEmbeddingGeneratorFactory` for how the effective endpoint is currently resolved, reuse that same
resolution rather than re-deriving it).

This project's established convention for a long-running operation is fire-and-forget + client
polling (see `IIndexJobTracker`/`POST /index`/`GET /index/status`, SYNTH-30/31) — not
SSE/streaming, even though Ollama's own `/api/pull` streams. Adapt: the backend endpoint kicks off
the pull as a detached background task (consuming Ollama's streamed response internally, updating a
tracker), and the client polls a status endpoint, mirroring the indexing job pattern.

## What to do
1. `GET /settings/embedding/ollama/models` (bare route): proxies `GET {endpoint}/api/tags` via
   `IHttpClientFactory` (register `AddHttpClient()` if not already present — check, SYNTH-37 may
   have added one for VCS token probing already, reuse it) and returns the list of locally available
   model names. Resolve `{endpoint}` the same way the live embedding generator does (current
   effective `EmbeddingOptions.Ollama.Endpoint`, falling back to the Aspire connection string if
   unset — check how that fallback currently works before reimplementing it).
2. A small tracker for the pull job — `IOllamaPullTracker`/`InMemoryOllamaPullTracker` (a single
   global pull at a time is fine, mirror `IIndexJobTracker`'s shape: `TryStart`/`ReportProgress`/
   `Complete`/`Fail`, a status record with at least `State` (Idle/Running/Done/Failed), `Model`,
   some progress indicator (Ollama's pull stream reports a `status` string and byte counts — surface
   whatever's simplest to parse, a human-readable status string is enough, doesn't need to be a
   precise percentage), `Error`).
3. `POST /settings/embedding/ollama/pull` (bare route, body `{ model: string }`): reserves the pull
   slot via the tracker (409 if one's already running), dispatches a detached background task
   (`CancellationToken.None`, same reasoning as `POST /index`'s background dispatch — the request's
   own token gets cancelled when the near-instant response completes) that streams
   `POST {endpoint}/api/pull` (Ollama's API takes `{ "name": model, "stream": true }` and returns
   newline-delimited JSON objects, each with a `status` field and sometimes `completed`/`total` byte
   counts — read the response stream line-by-line, updating the tracker after each line), calling
   `tracker.Complete()`/`Fail(...)` at the end. Returns `202 Accepted` immediately.
4. `GET /settings/embedding/ollama/pull/status` (bare route): returns the tracker's current snapshot.
5. Client: in `SettingsPanel.vue`'s Ollama section, replace the free-text `ollamaModel` input with a
   picker — fetch `GET /settings/embedding/ollama/models` on mount/endpoint-change, show them as
   selectable options, plus a text field for typing a new model name with a "Pull" button that calls
   the new `POST .../pull` and starts polling `GET .../pull/status` (same 1s-interval polling pattern
   `IndexPanel.vue` already established, including the request-sequencing guard fix from earlier this
   session — reuse that pattern, don't reintroduce the stale-response race it fixed), showing the
   pull's status text while running and refreshing the model list on completion.
6. Tests: backend tests for the models-list proxy (mock the HTTP call, don't hit real Ollama) and the
   pull tracker's start/progress/complete/fail transitions + the 409-when-already-running case
   (mirror `IndexJobTrackerTests.cs`'s style); a client test for the picker rendering fetched models,
   triggering a pull, and polling status to completion (mirror `IndexPanel.test.ts`'s polling tests,
   including a fake-timers test).

## Acceptance
`dotnet build`/`dotnet test` stay green, `npm test`/`npm run build` stay green. Settings' Ollama
section shows locally-available models fetched from the live Ollama instance, and can trigger a
pull for a new model with visible in-progress status polled from the backend, without blocking the
request or using streaming/SSE on the wire between client and Synth's own API.

## Out of scope
- An OpenAI model picker — OpenAI's model list isn't meaningfully "pull"-able the same way, free-text
  stays for that provider.
- Cancelling an in-flight pull.
- Persisting pull history — only the current/most-recent pull's status, same scope `IIndexJobTracker`
  itself has for indexing jobs.
