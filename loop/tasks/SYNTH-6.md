---
id: SYNTH-6
summary: "CodeChunk model + Synth.Core project (computed EmbeddingText)"
status: open
acceptance_command: "dotnet build src/Synth.slnx --nologo -v q && dotnet test src/Synth.slnx --nologo -v q && grep -rq 'class CodeChunk' src/Synth.Core"
acceptance_criterion: ""
boundaries: "Only add the new Synth.Core class library project, the CodeChunk model + its computed EmbeddingText property, a Synth.Core.Tests project, and wiring into the solution (and a project reference from Synth.Api, even if unused yet). Do NOT add chunkers, embeddings, or vector store code — those are later tasks. No Qdrant/Ollama/Vue."
limits: "max_iterations=25; max_minutes=120"
labels: [backend, rag-core, domain-model]
---

# SYNTH-6: CodeChunk model + Synth.Core project

## Context
Starts phase 2 (RAG core, GitHub issue #3). Ported from Sonar's `CodeChunk`
model (see Jarvis wiki `sonar-shared` and `code-chunking-strategy`), adapted
for Synth. This task only introduces the domain project and the data model —
no chunking/embedding/storage logic yet, so later tasks (Roslyn chunker,
Ollama embeddings, Qdrant) have a stable model to build on.

## What to do
1. Add `src/Synth.Core` (class library, `net10.0`) — this will hold domain
   models, chunkers, embedding/search logic for the rest of phase 2.
2. Add a `CodeChunk` record/class with fields mirroring Sonar's model:
   `FilePath`, `RelativePath`, `Namespace`, `ClassName`, `MethodName`,
   `ChunkType` (enum: e.g. `Class`, `Interface`, `Method`, `Constructor`,
   `Property`, `MethodHead`, `MethodBody`, `Markdown`, ...), `Content`,
   `Summary`, `StartLine`, `EndLine`, `FileHash`.
3. Add a computed `EmbeddingText` property (not stored, computed on access)
   that assembles, in order: a `[code]`/`[docs]` prefix based on chunk type,
   the qualified name (`Namespace.ClassName.MethodName`, skipping empty
   parts), `Summary` if present, then `Content` (verbatim if under ~3000
   chars, otherwise the first ~40 lines), then the qualified name again if it
   still fits — all capped at a hard limit of 24000 characters total.
4. Add `src/Synth.Core.Tests` (xUnit) with unit tests for `EmbeddingText`
   covering: short content (verbatim), long content (head-truncated),
   missing summary, and the hard 24000-char cap.
5. Wire `Synth.Core` and `Synth.Core.Tests` into `src/Synth.slnx`, and add a
   project reference from `Synth.Api` to `Synth.Core` (no usage required yet
   — just prove it compiles together).
6. Keep all existing tests (`Synth.Api.Tests`) green and untouched.

## Acceptance
`dotnet build`/`dotnet test` on `src/Synth.slnx` stay green (existing +
new tests), and a `CodeChunk` class exists under `src/Synth.Core` (mirrors
the frontmatter `acceptance_command`).

## Out of scope
- File chunkers (Roslyn/regex) — `SYNTH-7`.
- Ollama embeddings — `SYNTH-8`.
- Qdrant / vector store — `SYNTH-9`.
- Indexing pipeline, search — later tasks.
- Vue client.
