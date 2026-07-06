---
id: SYNTH-5
summary: "Wire up Microsoft Agent Framework (minimal proof-of-wiring, no loop replacement)"
status: done
acceptance_command: "dotnet build src/Synth.slnx --nologo -v q && dotnet test src/Synth.slnx --nologo -v q && grep -rqi 'Microsoft.Agents.AI\|Microsoft.Extensions.AI.Agents\|AgentFramework' src/Synth.Api/Synth.Api.csproj"
acceptance_criterion: ""
boundaries: "Only add the Microsoft Agent Framework package(s) and ONE minimal example agent/workflow to prove it builds and runs in this repo. Do NOT replace, touch, or reimplement scripts/loop.sh, the maker/checker subagent scaffold, or any of the existing agent-loop plumbing — that is a separate, deliberately-scoped future decision. No Qdrant/real embeddings/Vue. The example must not require live LLM credentials (Anthropic/OpenAI/Azure API keys) to build or to pass its test — use a mock/stub chat client or a deterministic function-based agent if the framework supports one."
limits: "max_iterations=30; max_minutes=150"
labels: [backend, orchestration, microsoft-agent-framework]
---

# SYNTH-5: Wire up Microsoft Agent Framework (minimal proof-of-wiring)

## Context
Decided 2026-07-06 (see Jarvis wiki `microsoft-agent-framework`, GitHub issue #2):
Synth's agent orchestration will use **Microsoft Agent Framework** (MAF,
https://github.com/microsoft/agent-framework, MIT, .NET + Python). This task
only proves MAF is correctly referenced and buildable in `Synth.Api` with one
trivial working example — it is explicitly NOT the task that redesigns how
the existing hand-rolled agent loop (`scripts/loop.sh` + maker/checker
subagents) works. That redesign is a separate, bigger decision for later.

## What to do
1. Find and add the correct .NET NuGet package(s) for Microsoft Agent
   Framework (check nuget.org / the GitHub repo's docs for current package
   names — the framework has been iterating, so confirm the actual package
   id rather than guessing from memory).
2. Add ONE minimal example: a simple agent/workflow (e.g. an "echo" or
   deterministic function-based agent — whatever MAF's API calls its
   simplest primitive) that takes a string input and returns a string output,
   without calling out to a real LLM provider. If MAF requires an
   `IChatClient`/model abstraction even for the simplest agent, use a
   fake/mock implementation that returns a canned response — the point is to
   prove the package wiring and API surface work, not to exercise a real
   model.
3. Expose or invoke this example in a way that's testable — e.g. a small
   internal service registered in DI, exercised directly from a unit test
   (an HTTP endpoint is optional, not required).
4. Add a test that runs the example agent/workflow end-to-end (against the
   mock/stub, no network) and asserts on its output.
5. Keep the existing `/health` contract, Mongo wiring, and config-store tests
   from SYNTH-1 through SYNTH-4 untouched and green.

## Acceptance
`dotnet build`/`dotnet test` on `src/Synth.slnx` stay green, and
`Synth.Api.csproj` references an MAF package (mirrors the frontmatter
`acceptance_command`'s grep, adjust the pattern in your own test if you find
the real package name differs — the important thing is a real MAF package
reference exists and the example runs offline).

## Out of scope
- Replacing/modifying `scripts/loop.sh`, `.claude/agents/{maker,checker}.md`, or any part of the existing agent-loop scaffold.
- Real LLM calls (Anthropic/OpenAI/Azure) — everything must run offline/mocked.
- MCP integration with MAF (separate open question, tracked in issue #4).
- Qdrant, real embeddings, Vue client.
