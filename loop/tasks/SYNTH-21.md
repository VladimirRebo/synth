---
id: SYNTH-21
summary: "ConfigurableEmbeddingGenerator supporting Ollama and OpenAI, hot-swappable"
status: done
acceptance_command: "dotnet build src/Synth.slnx --nologo -v q && dotnet test src/Synth.slnx --nologo -v q && grep -riq 'class ConfigurableEmbeddingGenerator' src/Synth.Api/"
acceptance_criterion: ""
boundaries: "Only add the provider-selectable, hot-swappable embedding generator and its config binding. Do not add the Settings HTTP endpoints yet (SYNTH-22) or touch the Vue client. Do not touch Qdrant/vector-store wiring — Vladimir decided Qdrant stays the only, Aspire-managed vector store, no provider abstraction needed there."
limits: "max_iterations=30; max_minutes=150"
labels: [backend, settings, embeddings]
---

# SYNTH-21: ConfigurableEmbeddingGenerator supporting Ollama and OpenAI

## Context
Today `EmbeddingServiceExtensions.AddSynthEmbeddings` (`src/Synth.Api/Embeddings/`) registers
`IEmbeddingGenerator<string, Embedding<float>>` as a **static, Ollama-only** client built once at
startup from an Aspire-supplied connection string (`builder.AddOllamaApiClient("embeddings").AddEmbeddingGenerator()`)
— there is no way to switch provider or change the model without restarting the whole Aspire host
and editing `AppHost.cs`. Issue #26 needs embeddings to support **both Ollama and OpenAI**,
switchable at runtime through Settings (the actual HTTP endpoint is `SYNTH-22`; this task only adds
the underlying capability + config).

This mirrors Sonar's `ConfigurableEmbeddingGenerator` (documented in the Jarvis wiki, entity
`sonar-infrastructure`): a wrapper implementing `IEmbeddingGenerator<string, Embedding<float>>`
that holds an inner generator, rebuilt from `IOptionsMonitor<EmbeddingOptions>` whenever the config
changes (`OnChange` + a snapshot key so it only rebuilds when something actually changed, double-
checked locking around the swap so in-flight calls aren't disrupted). Sonar also has a
`NotConfigured` sentinel that returns a generator which only throws when actually *used* (not at
DI-resolution time), so the app can start even with an invalid/incomplete config — worth
reproducing here too, since Aspire already gives Synth a "start clean, no live dependency required"
guarantee that this change must not break.

**Critical constraint: zero-config local dev must keep working exactly as today.** Most of the time
nobody will have touched Settings — in that case, embeddings must keep using Ollama via the
Aspire-supplied connection string, unchanged. The config override (Settings) only kicks in once
something has actually been saved.

## What to do
1. Add `Synth.Core/Embeddings/EmbeddingOptions.cs` (or under `Synth.Api/Embeddings/` if it needs to
   reference API-only types — prefer `Synth.Core` if it can stay dependency-free, matching where
   `VcsOptions` lives): a config-bound options class, section name `"Embedding"`, shaped roughly
   like:
   ```
   Provider: "Ollama" | "OpenAI" | null   // null/empty = use the Aspire-default (today's behavior)
   Ollama: { Endpoint?: string, Model?: string }   // overrides; null falls back to the Aspire connection
   OpenAI: { ApiKey?: string, Model?: string }
   ```
2. Confirm the right NuGet package for an OpenAI-backed `IEmbeddingGenerator` (the official `OpenAI`
   SDK's `EmbeddingClient` has an `.AsIEmbeddingGenerator()` extension via a
   `Microsoft.Extensions.AI.OpenAI`-family package — verify the exact current package name/version
   on nuget.org, the way `SYNTH-9` did for the Qdrant package) and add it to `Synth.Api.csproj`.
3. Add `ConfigurableEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>` in
   `Synth.Api/Embeddings/`: constructed with the Aspire-default Ollama connection info (as a
   fallback) plus `IOptionsMonitor<EmbeddingOptions>`. On each call, if the current options snapshot
   differs from the last-built one, rebuild the inner generator (Ollama-from-override, OpenAI, or
   Ollama-from-Aspire-default when `Provider` is unset) under a lock; otherwise reuse it. Delegate
   `GenerateAsync`/`GetService` to the inner instance.
4. Update `EmbeddingServiceExtensions.AddSynthEmbeddings` to register `ConfigurableEmbeddingGenerator`
   instead of the bare Ollama client, binding `EmbeddingOptions` from the layered config (same
   `services.Configure<EmbeddingOptions>(configuration.GetSection(EmbeddingOptions.SectionName))`
   pattern as `VcsServiceExtensions`), and passing through the Aspire Ollama connection details as
   the fallback.
5. Tests: no live Ollama/OpenAI required (mirror the existing "fallback/fake" test convention used
   throughout this repo). Cover: default config (no override) behaves like today; a config change
   observed through `IOptionsMonitor` triggers a rebuild (use a fake/test `IOptionsMonitor` you can
   push updates through, same pattern as `StaticOptionsMonitor` added in `SYNTH-18`'s tests for
   `GitRepoService`); an incomplete/invalid config (e.g. `Provider: "OpenAI"` with no API key)
   doesn't crash at construction/DI-resolution time, only throws (or degrades) when a search is
   actually attempted.

## Acceptance
`dotnet build`/`dotnet test` on `src/Synth.slnx` stay green, `ConfigurableEmbeddingGenerator` exists
in `Synth.Api`, default (no-override) behavior is unchanged from before this task, and a config
change is observably picked up without an app restart.

## Out of scope
- `GET/PUT /api/settings/embedding` HTTP endpoints — `SYNTH-22`.
- Vue client — done directly after the backend lands.
- Qdrant/vector-store settings — explicitly not part of this phase.
