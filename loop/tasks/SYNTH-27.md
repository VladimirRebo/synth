---
id: SYNTH-27
summary: "MCP tools find_callers/find_callees + REST equivalents over ICodeGraphStore"
status: done
acceptance_command: "dotnet build src/Synth.slnx --nologo -v q && dotnet test src/Synth.slnx --nologo -v q && grep -riq 'find_callers' src/Synth.Api/"
acceptance_criterion: ""
boundaries: "Only add query tools (MCP + REST) over SYNTH-25/26's call-graph. Do not add type-hierarchy queries. Do not touch the Vue client (optional/stretch for this phase per issue #33 — skip it, don't build it speculatively). Map REST routes bare (no /api prefix) — the exact mistake made once for the Settings endpoints must not be repeated (see the fix commit for VcsSettingsEndpoints/EmbeddingSettingsEndpoints)."
limits: "max_iterations=25; max_minutes=120"
labels: [backend, call-graph, mcp]
---

# SYNTH-27: find_callers/find_callees — MCP tools + REST equivalents

## Context
`SYNTH-25` (storage) and `SYNTH-26` (extraction) make `ICodeGraphStore` a real, populated
call-graph per collection. This task exposes it to callers: MCP tools for AI agents (the primary
consumer, per issue #33 — this whole phase exists so an agent can ask "who calls this" precisely
instead of guessing from vector search), and REST equivalents for parity with the existing
`search_code`/`GET /search` pairing (`Synth.Api/Mcp/CodeSearchTool.cs` +
`Synth.Api/Search/SearchEndpoints.cs` — same split of "MCP tool wraps a shared service; REST
endpoint wraps the same service" applies here). A human-facing client UI is explicitly optional for
this phase — don't build one speculatively; the REST endpoints existing is enough to not block a
future client task.

## What to do
1. Add `Synth.Api/Graph/CallGraphTool.cs` (mirrors `CodeSearchTool.cs`'s shape): an
   `[McpServerToolType]` class with two `[McpServerTool]`-attributed static methods:
   - `find_callers` — `Task<IReadOnlyList<...>> FindCallersAsync(ICodeGraphStore store, [Description]
     string symbol, [Description] string collection = CollectionNames.Default, CancellationToken ct)`
     — returns the edges where `symbol` is the callee (who calls it).
   - `find_callees` — same shape, edges where `symbol` is the caller (what it calls).
   Give both a `[Description]` explaining the expected `symbol` format (the same qualified-name
   shape `SYNTH-26` produces, e.g. `Namespace.ClassName.MethodName`) and that `collection` defaults
   to the main indexed codebase — mirror `CodeSearchTool`'s existing `collection` parameter
   description exactly for consistency.
   Project `CallEdge` into a small response DTO if the raw record isn't already a clean wire shape
   (check whether `CallEdge` as-is serializes sensibly, e.g. field naming, before adding a
   projection layer that isn't needed).
2. Register the tool type alongside `CodeSearchTool` wherever MCP tools are registered
   (`AddSynthMcp`/`.WithTools<CallGraphTool>()` — find the existing `.WithTools<CodeSearchTool>()`
   call and add this next to it, both transports pick it up automatically the same way
   `search_code` already does for HTTP + stdio).
3. Add `Synth.Api/Graph/CallGraphEndpoints.cs`: `GET /callers?symbol=&collection=` and
   `GET /callees?symbol=&collection=`, mapped **bare** (no `/api` prefix — see boundaries), wrapping
   the same `ICodeGraphStore` calls as the MCP tool. `symbol` required (400 if missing/blank),
   `collection` optional (defaults to `CollectionNames.Default`, same convention as `GET /search`).
4. Map the new endpoints in `Program.cs` next to the other `Map*Endpoints()` calls.
5. Tests: MCP tool tests (mirror however `CodeSearchTool`/`CodeSearchMcpToolTests.cs` are tested —
   likely calling the static method directly against a fake/in-memory `ICodeGraphStore`) and REST
   endpoint tests via `WebApplicationFactory` (mirror `IndexingEndpointTests`/`VcsSettingsEndpointTests`'
   style) covering: a symbol with known callers/callees returns them; an unknown symbol returns an
   empty list (not an error); missing `symbol` query param returns 400; `collection` scoping is
   respected (a symbol only returns edges from the requested collection).

## Acceptance
`dotnet build`/`dotnet test` on `src/Synth.slnx` stay green; `find_callers`/`find_callees` exist as
MCP tools (both transports) and as bare REST endpoints (`/callers`, `/callees`), correctly scoped by
collection.

## Out of scope
- Vue client — optional for this phase, not built here.
- Type-hierarchy queries.
