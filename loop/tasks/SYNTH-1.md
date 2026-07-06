---
id: SYNTH-1
summary: "Bootstrap the .NET solution with a minimal Web API and a /health endpoint"
status: done
acceptance_command: "dotnet test src/Synth.slnx --nologo -v q"
acceptance_criterion: ""
boundaries: "Create only the solution skeleton under src/. Do not add Qdrant/Mongo integration yet, no Vue client yet."
limits: "max_iterations=25; max_minutes=60"
labels: [scaffold, backend, bootstrap]
---

# SYNTH-1: Bootstrap the .NET solution

## Context
Synth's backend is .NET. This is the very first task the loop runs against an
empty `src/` — it stands up the solution so later tasks have something to build on.

## What to do
1. Create `src/Synth.slnx` (new XML-based solution format, .NET 10's `dotnet new sln` default — not the classic `.sln`).
2. Add an ASP.NET Core Web API project `src/Synth.Api` (`net10.0`) exposing
   `GET /health` that returns `200` with body `{"status":"ok"}`.
3. Add a test project `src/Synth.Api.Tests` (xUnit) with a test that spins up the
   API (`WebApplicationFactory`) and asserts `GET /health` → `200` and `status == "ok"`.
4. Wire both projects into the solution.

## Acceptance
`dotnet test src/Synth.slnx` builds and passes (this is the frontmatter
`acceptance_command`). The health test proves the endpoint works end-to-end.

## Out of scope
- Qdrant / MongoDB / embeddings.
- Vue client.
- Auth, config layering, deployment.
