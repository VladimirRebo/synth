---
id: SYNTH-8
summary: "Ollama as an Aspire resource + embedding generator wiring"
status: done
acceptance_command: "dotnet build src/Synth.slnx --nologo -v q && dotnet test src/Synth.slnx --nologo -v q && grep -riq 'ollama' src/Synth.AppHost/AppHost.cs && grep -riq 'ollama' src/Synth.Api/Synth.Api.csproj"
acceptance_criterion: ""
boundaries: "Only wire up Ollama as an Aspire resource and an IEmbeddingGenerator<string, Embedding<float>> in Synth.Api/Synth.Core. Do not add Qdrant, the indexing pipeline, or search yet. No Vue client. Tests must not require a live Ollama server/Docker to pass."
limits: "max_iterations=30; max_minutes=150"
labels: [backend, rag-core, ollama, embeddings, aspire]
---

# SYNTH-8: Ollama as an Aspire resource + embedding generator

## Context
Decided stack: **Ollama** is Synth's embedding provider (see Jarvis wiki
`overview`/`synth`/`sonar-infrastructure`), matching one of Sonar's supported
providers. This task adds Ollama to the Aspire AppHost (from `SYNTH-2`) and
wires an embedding generator into the API, so the indexing pipeline
(`SYNTH-10`) has something to call.

## What to do
1. Add Ollama to `Synth.AppHost`: look for the current Aspire hosting
   integration for Ollama (e.g. an Aspire Community Toolkit package —
   confirm the actual package name/version on nuget.org rather than
   guessing) and register a resource with a persistent volume + a default
   embedding model pulled on startup (e.g. `nomic-embed-text`, or the model
   Sonar used if you find it referenced — check the Jarvis wiki
   `sonar-infrastructure` page for hints, otherwise `nomic-embed-text` is a
   sane default). Reference it from the `api` resource.
2. In `Synth.Core` or `Synth.Api`, wire an
   `IEmbeddingGenerator<string, Embedding<float>>` (`Microsoft.Extensions.AI`)
   backed by Ollama. Mirror Sonar's approach if useful: Ollama exposes an
   OpenAI-compatible endpoint, so an OpenAI-compatible embedding client
   pointed at the Ollama resource's endpoint (with a dummy API key like
   `"ollama"`) works, via `.AsIEmbeddingGenerator(dimensions)` or whatever the
   current `Microsoft.Extensions.AI`/Aspire client package calls it. Use
   Aspire service discovery for the endpoint, not a hardcoded URL.
3. Register the generator in DI so it's available for later tasks.
4. Add tests that verify the DI registration/wiring compiles and resolves
   correctly using a fake/mock `IEmbeddingGenerator` or a stubbed HTTP
   handler — do NOT require a real Ollama server or Docker to be running for
   the automated test suite to pass (mirror the lazy-connection pattern used
   for Mongo in `SYNTH-3`).
5. Keep all existing tests green.

## Acceptance
`dotnet build`/`dotnet test` on `src/Synth.slnx` stay green, `AppHost.cs`
references Ollama, and `Synth.Api.csproj` references an Ollama-related
package (mirrors the frontmatter `acceptance_command`'s two greps). No live
Ollama/Docker connection required for tests to pass.

## Out of scope
- Qdrant / vector store — `SYNTH-9`.
- Indexing pipeline — `SYNTH-10`.
- Search/reranking — `SYNTH-11`.
- Vue client.
