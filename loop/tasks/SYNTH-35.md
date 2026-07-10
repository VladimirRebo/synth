---
id: SYNTH-35
summary: "Real health checks (Qdrant + embedding reachability)"
status: open
acceptance_command: "dotnet build src/Synth.slnx --nologo -v q && dotnet test src/Synth.slnx --nologo -v q && grep -rq 'HealthCheck' src/Synth.Api/ src/Synth.Core/"
acceptance_criterion: ""
boundaries: "Route stays /health (bare, unchanged path), but its handler must actually check dependencies instead of returning a static object. Touch: Program.cs (the /health mapping), a new health-check service/class, and tests. Do NOT add disk-space checks or GitLab/VCS-provider reachability checks (out of scope, see below). Do NOT change the route path or break existing callers of GET /health that only check for a 200 status — the response body can grow richer, but a healthy system must still return 200."
limits: "max_iterations=25; max_minutes=120"
labels: [backend, api, operability]
---

# SYNTH-35: Real health checks (Qdrant + embedding reachability)

# Context
Part of issue #43. `GET /health` (`Program.cs`) is currently:
```csharp
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
```
It doesn't check anything — it's always `200 {"status":"ok"}` regardless of whether Qdrant or
the embedding provider is actually reachable. This session hit two real incidents where the
underlying dependency was down (native Ollama.app had stopped running; a Qdrant collection had a
dimension mismatch) and the only way to discover it was starting an indexing job and watching it
fail — `/health` gave no advance warning either time.

`EmbeddingSettingsEndpoints.ProbeAsync` (`src/Synth.Api/Embeddings/EmbeddingSettingsEndpoints.cs`)
already has the exact pattern needed for an embedding-reachability check: build a generator from
the current `EmbeddingOptions` via `IEmbeddingGeneratorFactory.Create(...)`, call `GenerateAsync`
with a short fixed probe string under a timeout, and turn any exception/timeout/empty-result into
a clear string reason. Reuse this pattern (don't necessarily reuse the exact private method — it's
private to that file — but mirror its approach: short timeout, probe text, catch broadly).

For Qdrant reachability: the DI-registered `QdrantClient` (used by `QdrantCodeChunkStore`) has
methods like `GetCollectionInfoAsync`/collection listing that will throw if Qdrant is unreachable —
a lightweight call like listing collections (or checking a well-known collection's existence) is
enough to prove connectivity without needing a dedicated ping endpoint.

# What to do
1. Add a small `IHealthCheckService`/`HealthCheckService` (naming your choice, keep it simple) in
   whichever project makes sense given its dependencies (likely `Synth.Api`, since it needs
   `QdrantClient` and `IEmbeddingGeneratorFactory`, both already Api-layer concerns) with a method
   like `Task<HealthReport> CheckAsync(CancellationToken ct)` returning at minimum: overall
   `Healthy: bool`, and per-component results for `qdrant` and `embedding` (each with its own
   healthy/unhealthy + an error message when unhealthy).
2. Cache the result briefly (a few seconds — check `EmbeddingSettingsEndpoints.ProbeTimeout`'s
   general timeout-value style for consistency) so hitting `/health` repeatedly (e.g. a client
   polling it) doesn't hammer Ollama/Qdrant on every single call — same reasoning Sonar's own
   `IHealthService` uses (cached probe result).
3. Wire `GET /health` in `Program.cs` to call this service and return its report as the JSON body,
   with an overall `200` when healthy and (your choice, but pick one and be consistent) either
   still `200` with `"status":"degraded"` in the body, or a `503` when a component is unhealthy —
   whichever you pick, make sure a truly healthy system still returns `200` so nothing that only
   checks status-code-200 today regresses.
4. Tests: unit-test the health-check service directly with fake/mock Qdrant and embedding
   dependencies (a reachable-vs-unreachable case each) rather than requiring live infrastructure —
   follow this project's established "fake when no live service is configured" pattern used
   throughout (e.g. `LocalCodeChunkStore` for tests, `NotConfiguredEmbeddingGenerator` sentinel).
   Assert the endpoint's JSON shape includes both component results.

# Acceptance
`dotnet build`/`dotnet test` stay green. `GET /health` performs real reachability checks against
Qdrant and the configured embedding provider (cached briefly, not hammering either on every poll)
and reports per-component status in its JSON body; a fully healthy system still returns 200.

# Out of scope
- Disk-space checks.
- GitLab/GitHub reachability checks — Synth's VCS layer is just the `git` CLI + optional tokens,
  not a persistent connection worth health-checking the same way.
- Client-side surfacing of this (a sidebar status indicator, etc.) — backend-only for this task;
  a follow-up can wire it into the UI once this data exists to consume.
