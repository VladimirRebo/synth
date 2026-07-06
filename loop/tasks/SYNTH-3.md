---
id: SYNTH-3
summary: "Add MongoDB as an Aspire resource for the config store"
status: done
acceptance_command: "dotnet build src/Synth.slnx --nologo -v q && dotnet test src/Synth.slnx --nologo -v q && grep -q AddMongoDB src/Synth.AppHost/AppHost.cs"
acceptance_criterion: ""
boundaries: "Only wire up the MongoDB Aspire resource and a basic connected client/health check in Synth.Api. Do not implement the actual config-layering read/write logic yet (that's SYNTH-4). No Qdrant/Ollama. No Vue client."
limits: "max_iterations=25; max_minutes=120"
labels: [backend, aspire, local-env, mongodb]
---

# SYNTH-3: Add MongoDB as an Aspire resource for the config store

## Context
Decided stack: MongoDB is Synth's config store (see Jarvis wiki `overview`/`synth`).
`SYNTH-2` added the Aspire AppHost; this task adds MongoDB as an Aspire-managed
container resource and wires a client into `Synth.Api`, so later tasks (config
layering) have a working connection to build on.

## What to do
1. Add the Aspire MongoDB hosting integration to `Synth.AppHost` (e.g.
   `Aspire.Hosting.MongoDB`), registering a `mongo` resource with a persistent
   volume so data survives restarts, and reference it from the `api` resource.
2. Add the corresponding Aspire client integration to `Synth.Api` (e.g.
   `Aspire.MongoDB.Driver`) so `IMongoClient`/`IMongoDatabase` is available via DI,
   configured through Aspire service discovery (no hardcoded connection string).
3. Add a lightweight health check (Aspire's built-in Mongo health check
   integration, or a minimal one) so `/health`-style checks reflect Mongo
   connectivity — but do NOT change the existing `GET /health` JSON contract or
   its test from SYNTH-1.
4. Keep everything building and the existing test suite green.

## Acceptance
`dotnet build` and `dotnet test` on `src/Synth.slnx` both succeed, and
`Synth.AppHost/AppHost.cs` registers the Mongo resource (mirrors the frontmatter
`acceptance_command`, which greps for `AddMongoDB`). A quick manual smoke check
(`dotnet run --project src/Synth.AppHost`, confirm the `mongo` resource comes up
under Docker) is welcome but not part of the automated contract.

## Out of scope
- The actual config-layering read/write pattern (env/DB precedence, hot-reload) — SYNTH-4.
- Qdrant / Ollama resources.
- Vue client.
