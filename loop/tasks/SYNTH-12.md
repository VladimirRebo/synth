---
id: SYNTH-12
summary: "MCP tool library exposing code search (HTTP transport)"
status: done
acceptance_command: "dotnet build src/Synth.slnx --nologo -v q && dotnet test src/Synth.slnx --nologo -v q && grep -rq 'ModelContextProtocol' src/Synth.Api/Synth.Api.csproj"
acceptance_criterion: ""
boundaries: "Only add the MCP tool wiring (new Synth.Mcp project or Synth.Api/Mcp folder, whichever is simpler given the SDK's actual API) and register/expose CodeSearchService.SearchAsync (from SYNTH-11) as one MCP tool over HTTP transport in Synth.Api. Do not add a stdio host in this task (separate follow-up) and do not touch the indexing pipeline, chunkers, storage, or Vue client. No live Ollama/Qdrant/Docker required for the test — use the existing Local fallback store + a fake/deterministic embedding generator, same as SYNTH-11's tests."
limits: "max_iterations=30; max_minutes=150"
labels: [backend, mcp, phase-3]
---

# SYNTH-12: MCP tool library exposing code search (HTTP transport)

## Context
Phase 3 (MCP layer, GitHub issue #4) kickoff. Confirmed via research
(2026-07-06): Microsoft Agent Framework (`Microsoft.Agents.AI`, already
referenced in `Synth.Api` since SYNTH-5) has documented MCP support, and the
official `ModelContextProtocol` C# SDK (NuGet, maintained by
Microsoft+Anthropic+the MCP org, package `ModelContextProtocol` /
`ModelContextProtocol.AspNetCore`) is the standard way to expose .NET code as
MCP tools over stdio or HTTP transport — this is well-documented, not an open
design question, so this task proceeds without blocking on issue #4.

This task is the first slice of issue #4's first checklist item ("MCP tools
as a transport-agnostic library (stdio + HTTP)"). It covers the HTTP half and
the transport-agnostic tool definition; stdio transport is a separate
follow-up task so this one stays small.

## What to do
1. Add the `ModelContextProtocol` and `ModelContextProtocol.AspNetCore`
   NuGet packages (confirm current package IDs/versions on nuget.org rather
   than guessing) to `Synth.Api` (or a new small `Synth.Mcp` class library
   referenced by `Synth.Api`, if that gives a cleaner separation between the
   transport-agnostic tool definitions and the ASP.NET Core hosting — use
   judgment on project layout, but keep it minimal).
2. Define one MCP tool (using the SDK's tool attributes, e.g.
   `[McpServerToolType]` / `[McpServerTool]`) that wraps
   `Synth.Core.CodeSearchService.SearchAsync(string query, int limit, ...)`
   from SYNTH-11 — name it something like `search_code`, with a query and
   limit parameter, returning the matched chunks (path, class/method name,
   snippet) as the tool result.
3. Wire MCP server registration into `Synth.Api`'s `Program.cs`
   (`AddMcpServer()` + tool registration + HTTP transport mapping, following
   whatever the SDK's current API surface actually is), alongside the
   existing `AddSynthSearch()`/`AddSynthAgents()` wiring. Keep the existing
   `/health` endpoint and all prior wiring untouched.
4. Add a test that exercises the tool end-to-end without live external
   services: seed a `LocalCodeChunkStore` (or equivalent fake, mirroring
   SYNTH-11's tests) with known chunks, a deterministic fake embedding
   generator, invoke the MCP tool (either by calling the tool method directly
   through DI, or via an in-process MCP client against
   `WebApplicationFactory<Program>` if that's not excessively complex), and
   assert it returns the expected search results.
5. Keep all prior tests (SYNTH-1..SYNTH-11) green.

## Acceptance
`dotnet build`/`dotnet test` on `src/Synth.slnx` stay green, and
`Synth.Api.csproj` references the `ModelContextProtocol` package (mirrors
the frontmatter `acceptance_command`'s grep).

## Out of scope
- Stdio MCP transport (follow-up task).
- Exposing indexing, config, or any tool besides code search.
- MCP client-side usage (e.g. wiring MAF agents to *consume* MCP tools) —
  that's a separate open item from issue #4.
- Vue client, VCS automations (issue #5, on hold).
