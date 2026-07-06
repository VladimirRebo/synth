---
id: SYNTH-13
summary: "Stdio MCP transport host for code search"
status: open
acceptance_command: "dotnet build src/Synth.slnx --nologo -v q && dotnet test src/Synth.slnx --nologo -v q && grep -rq 'WithStdioServerTransport' src/Synth.Mcp.Stdio"
acceptance_criterion: ""
boundaries: "Only add a new console host project (e.g. Synth.Mcp.Stdio) that registers the existing, already transport-agnostic CodeSearchTool (src/Synth.Api/Mcp/CodeSearchTool.cs, SYNTH-12) over ModelContextProtocol's stdio transport, reusing AddSynthEmbeddings/AddSynthVectorStore/AddSynthSearch as-is. Do not change CodeSearchTool, CodeSearchService, the HTTP transport wiring in Synth.Api, the indexing pipeline, or the Vue client. No live Ollama/Qdrant/Docker required for the test."
limits: "max_iterations=30; max_minutes=150"
labels: [backend, mcp, phase-3]
---

# SYNTH-13: Stdio MCP transport host for code search

## Context
Follow-up to SYNTH-12 (PR #18), which added HTTP transport for the
`search_code` MCP tool in `Synth.Api`. `CodeSearchTool` was deliberately
written transport-agnostic (see its doc comment) so it can back a stdio host
without changes — this task adds that host, completing issue #4's first
checklist item ("MCP tools as a transport-agnostic library (stdio + HTTP)").
Stdio is the transport most local MCP clients (editors, CLI agents) expect
for locally-spawned servers, as opposed to the HTTP transport meant for
networked/Aspire-orchestrated deployment.

## What to do
1. Add a new console project (suggested name `Synth.Mcp.Stdio`) under `src/`,
   added to `src/Synth.slnx`. It should reference `Synth.Api` (to reuse
   `AddSynthEmbeddings`, `AddSynthVectorStore`, `AddSynthSearch`, and the
   existing `Synth.Api.Mcp.CodeSearchTool` directly — no duplication) and
   `Synth.ServiceDefaults` if needed for consistent config binding.
2. `Program.cs`: build a generic host (`Host.CreateApplicationBuilder(args)`),
   wire the same search-layer registrations Synth.Api uses, then
   `.AddMcpServer().WithStdioServerTransport().WithTools<CodeSearchTool>()`
   and run the host. This process is meant to be spawned directly by an MCP
   client (stdin/stdout), not served over HTTP — do not map any HTTP
   endpoints here.
3. Confirm it still works offline/buildable without live Ollama/Qdrant: the
   embedding generator and vector store client construction must stay lazy
   (same guarantee `AddSynthEmbeddings`/`AddSynthVectorStore` already give
   Synth.Api), so `dotnet build` and any DI-registration test don't need a
   live backend.
4. Add a test (in `Synth.Api.Tests` or a new small test project for the stdio
   host, whichever fits better) that builds the host's DI container and
   asserts `search_code` is registered as an `McpServerTool`, mirroring the
   registration-check style already used in SYNTH-12's
   `CodeSearchMcpToolTests`. Don't attempt to spawn the process and speak the
   stdio protocol end-to-end — a DI-level check is sufficient here.
5. Keep all prior tests (SYNTH-1..SYNTH-12) green.

## Acceptance
`dotnet build`/`dotnet test` on `src/Synth.slnx` stay green, and the new
`Synth.Mcp.Stdio` project references `WithStdioServerTransport` (mirrors the
frontmatter `acceptance_command`'s grep).

## Out of scope
- Changing `CodeSearchTool`, `CodeSearchService`, or the HTTP transport.
- Spawning the stdio host as a subprocess and driving the real MCP wire
  protocol end-to-end (out of scope for automated tests; manual smoke test
  is fine but not required for acceptance).
- The "connector layer for the agent loop itself" checklist item on issue #4
  (maturity-matrix step 4) — that is a separate, larger architectural
  question about whether/how `scripts/loop.sh`'s maker/checker subagents
  should consume Synth's own MCP tools, not decided yet.
- Vue client, VCS automations (issue #5, on hold).
