---
id: SYNTH-36
summary: "MCP tool index_code — trigger indexing from an agent (HTTP + stdio)"
status: open
acceptance_command: "dotnet build src/Synth.slnx --nologo -v q && dotnet test src/Synth.slnx --nologo -v q && grep -rq 'index_code' src/Synth.Api/Mcp/"
acceptance_criterion: ""
boundaries: "Touch: src/Synth.Api/Indexing/IndexingEndpoints.cs (extract shared logic, don't duplicate it), a new src/Synth.Api/Mcp/IndexCodeTool.cs (or similar name), Program.cs (register the new tool type in the HTTP MCP host's WithTools<T>() chain), src/Synth.Mcp.Stdio/StdioMcpHost.cs (same registration for stdio), and tests. Do NOT make the MCP tool block until indexing completes — mirror POST /index's existing fire-and-forget contract exactly (this project's established convention: mutating operations return immediately, callers poll for status)."
limits: "max_iterations=25; max_minutes=120"
labels: [backend, mcp, indexing]
---

# SYNTH-36: MCP tool index_code

## Context
Part of issue #48. Synth's MCP server currently exposes `search_code`, `find_callers`,
`find_callees` — an agent can search and query the call graph, but cannot ask Synth to (re)index a
repository; that currently only happens via `POST /index` (REST) or the client UI. Sonar has
`index_code` as one of its 8 MCP tools for exactly this reason: letting an agent trigger indexing
directly is part of making Synth genuinely agent-usable, which is the whole point of the project.

`IndexingEndpoints.MapIndexingEndpoints`'s `POST /index` handler (`src/Synth.Api/Indexing/IndexingEndpoints.cs`)
already contains the full flow this tool needs: validate exactly one of path/repoUrl, build the
right `RepositoryEntry`, reserve the job slot via `IIndexJobTracker.TryStart` (409-equivalent if
already running), then dispatch the clone+index+registry-upsert+tracker-complete/fail work as a
detached `Task.Run(..., CancellationToken.None)`. Don't duplicate this ~80-line flow in the new
MCP tool — extract it into a shared, reusable method (e.g. a static or injectable method on
`IndexingEndpoints`, or a small new service) that both the REST endpoint and the new MCP tool call.
The extracted method's "did it start, or was there a validation/conflict error" outcome needs a
shape both callers can use to build their own response type (`Results.BadRequest`/`.Conflict`/`.Accepted`
for REST; whatever a Description-annotated return type looks like for the MCP tool — see
`CodeSearchTool`/`CallGraphTool` in the same directory for this project's established MCP tool
style: `[McpServerToolType]` class, static `[McpServerTool(Name = "...")]` method,
`[Description]` on the method and each parameter).

## What to do
1. Refactor `IndexingEndpoints.cs`: pull the validation + `TryStart` + detached dispatch logic out
   of the `MapPost("/index", ...)` lambda into a method callable from elsewhere (e.g.
   `public static IndexStartOutcome StartIndexing(IndexRequest request, IndexingPipeline pipeline, GitRepoService gitRepoService, IRepositoryRegistry registry, IIndexJobTracker tracker, ILoggerFactory loggerFactory)`
   — design the return shape yourself; it needs to distinguish "validation error" (with a message),
   "already running", and "started" (with the resolved collection name), so both the REST endpoint
   and the MCP tool can turn it into their own response. Update the REST endpoint to call this
   extracted method; confirm `POST /index`'s existing behavior and tests are unchanged (same 400s,
   same 409, same 202 body shape).
2. Add `src/Synth.Api/Mcp/IndexCodeTool.cs`: `[McpServerToolType]` class, static
   `[McpServerTool(Name = "index_code")]` method taking the same effective inputs `POST /index`
   does (`path`/`repoUrl`/`branch`, nullable, with `[Description]`s explaining exactly one of
   path/repoUrl is required — mirror `CodeSearchTool`'s doc-comment and attribute style), calling
   the extracted method from step 1, and returning a simple result object (e.g.
   `{ Collection, Status }` mirroring the REST `202`'s `{ collection, status }` shape, or an error
   message string on validation/conflict failure — pick whichever shape reads most naturally as an
   MCP tool result, consistent with how `CodeSearchResult`/`CallGraphTool`'s existing return types
   are shaped).
3. Register the new tool in both transports: add `.WithTools<IndexCodeTool>()` to the HTTP host's
   `AddMcpServer()...WithTools<...>()` chain in `Program.cs`, and the same to
   `src/Synth.Mcp.Stdio/StdioMcpHost.cs`'s chain (currently `.WithTools<CodeSearchTool>().WithTools<CallGraphTool>()`).
4. Tests: extend whatever test coverage already exists for `POST /index` (check
   `Synth.Api.Tests` for the existing indexing-endpoint test file) to also exercise the extracted
   method directly, or add a focused test for `IndexCodeTool` itself covering: starting a job
   successfully, rejecting when neither/both of path/repoUrl are given, and rejecting when a job
   is already running (`IIndexJobTracker.TryStart` returns false) — mirror however the existing
   `POST /index` tests fake/stub these dependencies (a real but fast fixture per this project's
   established indexing-test pattern, not live Ollama/Qdrant/git).

## Acceptance
`dotnet build`/`dotnet test` stay green. `POST /index`'s existing behavior and tests are
unchanged after the refactor. A new `index_code` MCP tool exists on both HTTP and stdio
transports, accepts the same path/repoUrl/branch inputs as `POST /index`, and returns immediately
(fire-and-forget, consistent with the REST endpoint) rather than blocking until indexing finishes.

## Out of scope
- Any new "check indexing status" MCP tool — that's `list_collections`/a status-check tool,
  tracked separately if it comes up; this task is only about triggering a start.
- Client changes — this is purely a new MCP surface, no UI involved.
