---
id: SYNTH-22
summary: "Settings CRUD for embedding config (GET/PUT /api/settings/embedding), probe-before-persist"
status: done
acceptance_command: "dotnet build src/Synth.slnx --nologo -v q && dotnet test src/Synth.slnx --nologo -v q && grep -riq 'settings/embedding' src/Synth.Api/"
acceptance_criterion: ""
boundaries: "Only add the embedding Settings HTTP endpoints on top of SYNTH-21's ConfigurableEmbeddingGenerator/EmbeddingOptions and SYNTH-20's config-section-update helper. Do not touch the Vue client (later, direct) or Qdrant (out of scope for this phase)."
limits: "max_iterations=25; max_minutes=120"
labels: [backend, settings, embeddings]
---

# SYNTH-22: Settings CRUD for embedding config, probe-before-persist

## Context
`SYNTH-21` added `EmbeddingOptions`/`ConfigurableEmbeddingGenerator` (provider-switchable,
hot-reloadable) but no way to actually change them except editing the config store's JSON by hand.
`SYNTH-20` added a reusable "update one section of the config document, thread-safe, triggers a
reload" helper for `Vcs` — reuse the same helper for the `Embedding` section here rather than
duplicating the read-merge-write logic.

Unlike `VcsOptions` (SYNTH-20, no probe — a token's validity isn't provable without a live clone),
an embedding config **is** cheaply provable: generate one real embedding with the candidate config
before persisting it. This is the same reasoning Sonar uses for its own settings ("persist
неработающего провайдера отравляет валидацию для всех последующих запросов" — a saved broken
provider poisons every subsequent request until someone notices and fixes it by hand). A `PUT` that
fails the probe must be rejected (400) and must **not** be persisted.

## What to do
1. `GET /api/settings/embedding` — returns the current `EmbeddingOptions` as JSON, with the OpenAI
   API key masked the same way `SYNTH-20` masks VCS tokens (`{ apiKeySet: bool }`, never the raw
   value). Shape: `{ provider: "Ollama" | "OpenAI" | null, ollama: { endpoint?, model? }, openai: {
   apiKeySet, model? } }`.
2. `PUT /api/settings/embedding` — accepts the same shape (with a real `apiKey` field instead of
   `apiKeySet` when the caller wants to set/change it; omitted means "leave the currently-stored key
   unchanged", matching `SYNTH-20`'s partial-update convention). Before persisting:
   - Build a temporary embedding generator from the candidate config (reuse whatever internal
     construction logic `ConfigurableEmbeddingGenerator` uses to build its inner generator from an
     `EmbeddingOptions` snapshot — refactor that into a small reusable method if it's currently
     private/inline, rather than duplicating provider-construction logic here).
   - Call it with a short fixed probe string (e.g. `"dimension probe"`, matching Sonar's own probe
     text) with a short timeout.
   - On success, persist via `SYNTH-20`'s section-update helper (section `"Embedding"`) and return
     the masked shape (200).
   - On failure (exception, timeout, or an empty/zero-length vector back), return 400 with a
     message describing what failed, and do **not** persist anything.
3. Map the endpoints (new `Synth.Api/Embeddings/EmbeddingSettingsEndpoints.cs`), registered in
   `Program.cs` near the other `Map*Endpoints()` calls.
4. Tests: no live Ollama/OpenAI required — inject/mock the piece that talks to the network (a fake
   `IEmbeddingGenerator` or an injectable factory function that the endpoint calls, so the test can
   make the probe succeed or fail deterministically). Cover: a successful PUT persists and is
   reflected in a subsequent GET (masked); a probe failure returns 400 and a subsequent GET shows
   the config is unchanged from before the failed PUT; partial update (e.g. only `model` for an
   already-configured provider) leaves other fields alone; API key masking never leaks the raw
   value in any GET response.

## Acceptance
`dotnet build`/`dotnet test` on `src/Synth.slnx` stay green; `GET`/`PUT /api/settings/embedding`
exist, round-trip correctly, mask the API key, support partial updates, and a `PUT` with a config
that can't actually produce an embedding is rejected without being persisted.

## Out of scope
- Vue client — done directly after the backend lands.
- Qdrant settings — explicitly not part of this phase.
