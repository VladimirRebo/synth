---
id: SYNTH-4
summary: "Config layering: IConfigStore (File/Mongo) with live-reload + env override"
status: done
acceptance_command: "dotnet build src/Synth.slnx --nologo -v q && dotnet test src/Synth.slnx --nologo -v q && grep -rq IConfigStore src/Synth.Api"
acceptance_criterion: ""
boundaries: "Only add the config-store abstraction, its two implementations (File/Mongo), the configuration-source glue, and tests. Do not add any real domain settings yet (no VectorStore/Embedding/etc. config sections — those belong to later RAG-core tasks). No Qdrant/Ollama wiring. No Vue client."
limits: "max_iterations=25; max_minutes=150"
labels: [backend, configuration, mongodb]
---

# SYNTH-4: Config layering (File/Mongo config store, live-reload, env override)

## Context
Adapted from Sonar's config-layering pattern (see Jarvis wiki
`config-layering-cicd`), simplified for a personal project (no CI/CD secret
scraping, no Kubernetes). Three layers, increasing priority:

1. `appsettings.json` — bootstrap/hosting only.
2. `IConfigStore` document, loaded into `IConfiguration` via a custom
   `IConfigurationSource`/`IConfigurationProvider` with live-reload.
3. Environment variables using the standard .NET `__` → `:` convention —
   always wins, no extra code needed (`AddEnvironmentVariables()` added last
   in the configuration builder).

## What to do
1. Define `IConfigStore` with roughly: `Task<string?> LoadAsync()`,
   `Task SaveAsync(string json)`, and a `Changed` event/notification for
   live-reload (mirror Sonar's shape, adapted — don't over-engineer).
2. Implement `FileConfigStore` — reads/writes a single JSON document under a
   local path (e.g. `~/.synth/config.json`), used whenever no Mongo connection
   is configured. This is the default for local dev without Docker running.
3. Implement `MongoConfigStore` using the `IMongoDatabase` registered in
   `Synth.Api` since `SYNTH-3` — store the config document as a single
   document (raw JSON string field, same reasoning as Sonar: Mongo forbids
   dots in field names). Select Mongo vs File based on whether the Mongo
   resource/connection is actually configured (mirror Sonar's `ConnectionString
   present → Mongo, else → File` decision) — don't hard fail if Mongo is
   absent.
4. Wire the chosen store into an `IConfigurationSource` that flattens the
   JSON document into `key:subkey` pairs and feeds `IConfiguration`, with
   reload-on-change so `IOptionsMonitor<T>` picks up updates without a
   restart.
5. Register `AddEnvironmentVariables()` after the config-store source so env
   vars always take precedence.
6. Tests: cover `FileConfigStore` round-trip (save → load → matches) and that
   the configuration source correctly flattens a sample JSON document and
   that env vars override it. Use an in-memory/temp-file store for tests —
   do not require a live Mongo/Docker connection to pass.

## Acceptance
`dotnet build`/`dotnet test` on `src/Synth.slnx` stay green (existing tests
untouched/still passing), and `IConfigStore` exists and is used somewhere
under `src/Synth.Api` (mirrors the frontmatter `acceptance_command`, which
greps for it). No live Mongo connection required for the automated tests to
pass.

## Out of scope
- Actual domain config sections (VectorStore, Embedding/Ollama, Qdrant, etc.) — later RAG-core tasks.
- CI/CD secret scraping / Kubernetes / Helm — not applicable to a personal project.
- Vue client, auth, deployment.
