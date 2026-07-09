---
id: SYNTH-29
summary: "GET/PUT /settings/raw — whole config document as unmasked JSON"
status: done
acceptance_command: "dotnet build src/Synth.slnx --nologo -v q && dotnet test src/Synth.slnx --nologo -v q && grep -riq '\"/settings/raw\"' src/Synth.Api/"
acceptance_criterion: ""
boundaries: "Only add the raw whole-document GET/PUT endpoint. Do not touch the section-specific endpoints (GET/PUT /settings/vcs, /settings/embedding) or their masking behavior — those stay exactly as they are, this is an additional, separate escape hatch. Do not add probe-before-persist to the raw endpoint (unlike /settings/embedding) — a raw JSON editor is a deliberate power-user path that trusts the caller, validating only that the body is well-formed JSON. Do not touch the Vue client (done directly after, not via loop)."
limits: "max_iterations=20; max_minutes=100"
labels: [backend, settings]
---

# SYNTH-29: GET/PUT /settings/raw — whole config document as unmasked JSON

## Context
`SYNTH-20`/`SYNTH-22` added masked, per-section Settings endpoints (`/settings/vcs`,
`/settings/embedding`) that never echo secrets back raw. Vladimir wants an additional, separate
"advanced" capability: view and edit the **entire** stored config document as plain JSON in the
client, secrets included unmasked (2026-07-09 decision — Synth is a single local user with no auth,
and the values already sit in plaintext in Mongo/the config file; masking only in the section
endpoints was a UX choice for the common case, not a security boundary that this new escape hatch
would be breaking).

This is a whole-document read/replace, not a section merge — reuse `ConfigSectionUpdater`
(`src/Synth.Api/Configuration/ConfigSectionUpdater.cs`, added in `SYNTH-20`) for its existing
`SemaphoreSlim` gate rather than calling `IConfigStore.SaveAsync` directly from the endpoint, so a
raw whole-document write can't race with a concurrent section-specific PUT and clobber each other.

## What to do
1. Extend `ConfigSectionUpdater` with a new method, e.g.
   `Task<string> LoadDocumentAsync(CancellationToken ct = default)` (returns the current document,
   `"{}"` when nothing is stored — reuse whatever default-empty-object logic
   `UpdateSectionAsync` already has) and
   `Task ReplaceDocumentAsync(string json, CancellationToken ct = default)` (validates `json` parses
   as a JSON object — reject invalid JSON by throwing something the endpoint can turn into a 400,
   don't let malformed input reach `IConfigStore.SaveAsync` — then persists it under the same
   `_gate` semaphore `UpdateSectionAsync` uses, replacing the whole document).
2. Add `GET /settings/raw` and `PUT /settings/raw` to a new or existing endpoints file under
   `Synth.Api/Configuration/` (mapped bare, no `/api` prefix — matching every other endpoint in this
   app). `GET` returns the raw document as-is (unmasked — this is the point of the endpoint).
   `PUT` accepts a raw JSON body (the whole document, as a string — read it as `JsonElement`/plain
   text and re-stringify, or accept the raw request body text directly, whichever is simpler given
   how minimal-API endpoints in this repo already read bodies), rejects non-object/malformed JSON
   with 400, otherwise persists via `ReplaceDocumentAsync` and returns the persisted document (200).
3. Map the endpoint in `Program.cs` next to the other `Map*Endpoints()` calls.
4. Tests: `GET` returns `{}` (or whatever default) when nothing is stored; after a `PUT`, a
   subsequent `GET` returns exactly what was written, unmasked (including any secret-shaped value —
   assert this explicitly, since it's the one behavior that's the opposite of every other Settings
   endpoint in this repo, easy to accidentally "fix" into masking by copy-pasting from
   `VcsSettingsEndpoints`); malformed JSON body → 400, nothing persisted; a `PUT` to `/settings/raw`
   is observable through `IOptionsMonitor<VcsOptions>`/`IOptionsMonitor<EmbeddingOptions>` afterward
   (same reload guarantee as the section endpoints), proving it isn't a separate, disconnected
   storage path.

## Acceptance
`dotnet build`/`dotnet test` on `src/Synth.slnx` stay green; `GET`/`PUT /settings/raw` exist, round-
trip the whole document unmasked, reject malformed JSON with 400 without persisting, and a write is
observable through the existing `IOptionsMonitor<T>` reload path.

## Out of scope
- Vue client — done directly after the backend lands.
- Any change to the existing masked section endpoints.
