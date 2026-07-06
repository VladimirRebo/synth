---
id: SYNTH-2
summary: "Add .NET Aspire AppHost for local orchestration"
status: open
acceptance_command: "dotnet build src/Synth.slnx --nologo -v q && test -f src/Synth.AppHost/Synth.AppHost.csproj && test -f src/Synth.ServiceDefaults/Synth.ServiceDefaults.csproj"
acceptance_criterion: ""
boundaries: "Only add Aspire scaffolding (AppHost + ServiceDefaults) and wire Synth.Api to it. Do not add Qdrant/Mongo/Ollama resources yet — those are separate follow-up tasks. No Vue client."
limits: "max_iterations=25; max_minutes=120"
labels: [scaffold, backend, aspire, local-env]
---

# SYNTH-2: Add .NET Aspire AppHost for local orchestration

## Context
Decided 2026-07-06: Synth's local environment is orchestrated with **.NET Aspire**
(same tool Sonar used, see the Jarvis wiki entity `sonar-shared`). Later tasks will
add Qdrant, MongoDB, and Ollama as Aspire resources; this task only lays the
groundwork so `Synth.Api` runs under Aspire.

## What to do
1. Add `src/Synth.AppHost` (Aspire App Host project, `net10.0`) that references and
   registers `Synth.Api` as a project resource.
2. Add `src/Synth.ServiceDefaults` (standard Aspire service-defaults project:
   OpenTelemetry, health checks, service discovery defaults) and reference it from
   `Synth.Api`.
3. Wire both new projects into `src/Synth.slnx`.
4. Keep `Synth.Api`'s existing `/health` endpoint and its test passing unchanged.

## Acceptance
`dotnet build src/Synth.slnx` succeeds with both new projects in the solution, and
`Synth.AppHost`/`Synth.ServiceDefaults` project files exist (mirrors the frontmatter
`acceptance_command`). Manually running `dotnet run --project src/Synth.AppHost`
should start the Aspire dashboard and the API — this manual check is not part of
automated acceptance since it's a long-running process, but do a quick smoke check
that it launches without an immediate crash if convenient.

## Out of scope
- Qdrant / MongoDB / Ollama Aspire resources (separate follow-up tasks).
- Vue client.
- Config layering, auth, deployment.
