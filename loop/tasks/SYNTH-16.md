---
id: SYNTH-16
summary: "POST /index endpoint to manually trigger IndexingPipeline"
status: done
commit: c00d49f211d35b2fcd44a9bd8221e466177dede7
acceptance_command: "dotnet build src/Synth.slnx --nologo -v q && dotnet test src/Synth.slnx --nologo -v q && grep -rq 'MapPost(\"/index\"' src/Synth.Api"
acceptance_criterion: ""
boundaries: "Only add a minimal HTTP endpoint to trigger the existing IndexingPipeline (SYNTH-10) against a real directory, plus tests. No search/reranking changes, no MCP-layer changes, no client changes. Tests must not require live Ollama/Qdrant/Docker (fake embedding generator)."
limits: "n/a — authored directly by Vladimir outside the agent loop, not run through maker/checker"
labels: [backend, rag-core, indexing]
---

# SYNTH-16: POST /index endpoint

## Context
`IndexingPipeline` (SYNTH-10) was only ever exercised by tests — SYNTH-10's
boundaries explicitly deferred adding an HTTP trigger ("that can be a tiny
follow-up, not required here"). This is that follow-up: a minimal manual
trigger endpoint so a real directory can be indexed against the running app,
not just in tests.

**Bookkeeping note:** this task was implemented directly by Vladimir as
commit `c00d49f` (2026-07-07), committed straight to `main` outside the
`scripts/loop.sh` maker/checker flow — no `fix/SYNTH-16` branch, no PR, no
validator run. This file is being filed retroactively (per Vladimir's
2026-07-07 decision) so the loop's task/state bookkeeping stays consistent
with what's actually in `main`.

## What was done
- `src/Synth.Api/Indexing/IndexingEndpoints.cs` — new `POST /index` endpoint
  wired into `IndexingPipeline`.
- `src/Synth.Api/Program.cs` — registers the new endpoint.
- `src/Synth.Api.Tests/IndexingEndpointTests.cs` — endpoint tests using a
  deterministic fake embedding generator (no live Ollama/Docker required).

## Acceptance
`dotnet build`/`dotnet test` green on `src/Synth.slnx`, and
`src/Synth.Api/Indexing/IndexingEndpoints.cs` maps `POST /index`.

## Out of scope
- Search/reranking changes (SYNTH-11 territory).
- MCP-layer or client changes.
- Any live Ollama/Qdrant/Docker dependency in tests.
