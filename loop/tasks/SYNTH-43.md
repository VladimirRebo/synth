---
id: SYNTH-43
summary: "MCP tools: list_collections, delete_collection, health_check"
status: open
acceptance_command: "dotnet build src/Synth.slnx --nologo -v q && dotnet test src/Synth.slnx --nologo -v q && grep -rq 'list_collections' src/Synth.Api/Mcp/ && grep -rq 'delete_collection' src/Synth.Api/Mcp/"
acceptance_criterion: ""
boundaries: "These three tools are thin wrappers around logic that already exists (IRepositoryRegistry.ListAsync, the DeleteCollectionAsync/ReplaceEdgesAsync/DeleteAsync sequence from RepositoryEndpoints.cs's DELETE handler, and IHealthCheckService from SYNTH-35) — do not reimplement any of that logic, call the same services/methods the REST endpoints already call. New files under src/Synth.Api/Mcp/, registration in Program.cs + StdioMcpHost.cs, and tests."
limits: "max_iterations=25; max_minutes=120"
labels: [backend, mcp, operability]
---

# SYNTH-43: MCP tools list_collections, delete_collection, health_check

## Context
Part of issue #44 (last of the 5 MCP-parity tools). These three are the cheapest to add since the
underlying logic already exists via REST — `GET /repositories` (`RepositoryEndpoints.cs`),
`DELETE /repositories/{collection}` (same file, SYNTH-34), and `GET /health` (`Program.cs` +
`IHealthCheckService`, SYNTH-35). This task is pure MCP-surface wrapping, not new business logic.

## What to do
1. `src/Synth.Api/Mcp/ListCollectionsTool.cs`: `[McpServerToolType]`, static
   `[McpServerTool(Name = "list_collections")]` method taking `IRepositoryRegistry registry` (DI
   parameter, same pattern every other tool in this directory uses) and returning
   `IReadOnlyList<RepositoryEntry>` (or a thin projection of it, your call — `RepositoryEntry` is
   already a clean serializable record, reusing it directly is probably simplest) from
   `registry.ListAsync()`.
2. `src/Synth.Api/Mcp/DeleteCollectionTool.cs`: static `[McpServerTool(Name = "delete_collection")]`
   taking a required `collection` string parameter (`[Description]` warning this is destructive —
   removes the vector-store collection, call-graph edges, and registry entry). Call the exact same
   three-step sequence `RepositoryEndpoints.cs`'s `DELETE /repositories/{collection}` handler already
   does (`ICodeChunkStore.DeleteCollectionAsync`, `ICodeGraphStore.ReplaceEdgesAsync(collection, [])`,
   `IRepositoryRegistry.DeleteAsync`) — if that logic isn't already factored into a small reusable
   method, extract it the same way SYNTH-36 extracted `POST /index`'s logic into `StartIndexing`, so
   REST and MCP call the same code rather than duplicating the three-call sequence. Return a simple
   result indicating success or "collection not found" (mirroring the REST endpoint's 204/404 split).
3. `src/Synth.Api/Mcp/HealthCheckTool.cs`: static `[McpServerTool(Name = "health_check")]` taking
   `IHealthCheckService` and returning its `HealthReport` (or projecting it) from `CheckAsync(...)` —
   same per-component detail `GET /health` already returns.
4. Register all three in both transports (`Program.cs`'s HTTP host chain, `StdioMcpHost.cs`'s chain)
   — `HealthCheckTool`'s stdio registration needs `IHealthCheckService` available there; check how
   its dependencies (`QdrantClient`/embedding factory) are wired for stdio today, or whether stdio
   needs its own simplified health story (e.g. `NotConfiguredQdrantHealthProbe`-style fallback,
   matching SYNTH-35's own pattern for "no live Qdrant configured") if a full DI graph isn't
   available in the stdio host.
5. Tests: one test per tool confirming it calls through to the right underlying service and returns
   the expected shape (reuse whatever fakes/mocks the corresponding REST endpoint's own tests
   already use for `IRepositoryRegistry`/`ICodeChunkStore`/`ICodeGraphStore`/`IHealthCheckService`).

## Acceptance
`dotnet build`/`dotnet test` stay green. `list_collections`, `delete_collection`, `health_check`
all exist on both MCP transports, each delegating to the same logic its REST counterpart already
uses (no duplicated business logic).

## Out of scope
- Any new REST endpoint — these three already have REST equivalents; this task only adds the MCP
  surface on top.
- Client changes.
