---
id: SYNTH-41
summary: "TS/Vue regex-based chunker"
status: open
acceptance_command: "dotnet build src/Synth.slnx --nologo -v q && dotnet test src/Synth.slnx --nologo -v q && grep -rq 'class.*Chunker' src/Synth.Core/TsVueChunker.cs"
acceptance_criterion: ""
boundaries: "New file(s) under src/Synth.Core/ for the chunker + its DI registration in src/Synth.Api/Indexing/IndexingServiceExtensions.cs (one more AddSingleton<IFileChunker, ...> line, additive). Do NOT touch CSharpRoslynChunker.cs or IndexingPipeline.cs's dispatch logic (FirstOrDefault(c => c.CanHandle(f)) already handles multiple chunkers correctly as long as CanHandle doesn't overlap .cs). Do NOT implement ICallSiteExtractor for this chunker — call-graph extraction stays C#-only for this pass, chunking only."
limits: "max_iterations=25; max_minutes=120"
labels: [backend, indexing, chunking]
---

# SYNTH-41: TS/Vue regex-based chunker

## Context
Part of issue #44. Synth only has one chunker, `CSharpRoslynChunker` (a real AST-based chunker for
C#) — every other file type is silently skipped during indexing. This means **Synth's own client**
(`src/client`, Vue 3 + TypeScript) is invisible to Synth's own search: indexing this repo only ever
covers the C# backend.

Sonar has 12 chunkers total: one AST-based (Roslyn/C#) and 11 regex-based (Java/Go/Python/Vue/etc.)
following a shared pattern — normalize line endings, scan for declaration boundaries via regex,
slice the file between them, fall back to the whole file as one chunk if nothing matches. This task
ports that pattern for TypeScript + Vue specifically (the two languages Synth's own client is
written in — closes the gap on Synth's own repo immediately, the most relevant target for this
project). Other languages can follow later, opportunistically, as separate tasks — don't try to
cover more than TS/Vue in this one.

`IFileChunker` (`src/Synth.Core/IFileChunker.cs`) is the abstraction: `CanHandle(filePath)` (by
extension) + `Chunk(filePath, relativePath, content) -> IReadOnlyList<CodeChunk>`. Chunkers are
registered as `IFileChunker` in DI (`IndexingServiceExtensions.cs:21` currently registers only
`CSharpRoslynChunker`); `IndexingPipeline` dispatches via `FirstOrDefault(c => c.CanHandle(filePath))`
— first match wins, so make sure this new chunker's `CanHandle` only claims `.ts`/`.tsx`/`.vue`
(never `.cs`, which the Roslyn chunker already owns).

## What to do
1. Create `src/Synth.Core/TsVueChunker.cs` implementing `IFileChunker`:
   - `CanHandle`: true for `.ts`, `.tsx`, `.vue` extensions.
   - `Chunk`: use `[GeneratedRegex]`-based patterns to find declaration boundaries — for TS/TSX:
     top-level `function`/`class`/`interface`/`export const ... = (...) =>`/`export function`
     declarations; for `.vue` Single File Components: the `<script setup>`/`<script>` block's
     top-level function/const declarations (treat the whole `<template>` and `<style>` blocks as
     out of scope for fine-grained chunking — a `.vue` file's searchable code is overwhelmingly in
     its script block). Slice content between consecutive declaration-start matches (the next
     match's start, or EOF, is the current chunk's end) — mirror Sonar's described approach:
     normalize line endings first, regex-scan for boundaries, slice between them, and if nothing
     matches at all, fall back to indexing the whole file as a single chunk (don't silently produce
     zero chunks for a file with no recognized top-level declarations).
   - Populate `CodeChunk` fields sensibly for this simpler (non-AST) extraction: `ChunkType` (add a
     value to the `ChunkType` enum if the existing values — check `ChunkType.cs` — don't fit a
     regex-derived function/class/component chunk; reuse existing values like `Method`/`Class` if
     they're a reasonable semantic fit instead of growing the enum, use your judgment), `ClassName`/
     `MethodName` extracted from the matched declaration name where discernible (best-effort — a
     regex chunker won't always know these precisely, empty string is fine when it can't tell),
     `FileHash` (same `SHA256`-based approach `CSharpRoslynChunker.ComputeFileHash` uses — check
     that method for the exact algorithm/format so hashes stay comparable in spirit, though this is
     a separate chunker's own hash of its own file so it doesn't need to be byte-identical to that
     method's implementation, just a stable per-content hash).
2. Register in `IndexingServiceExtensions.cs`: `builder.Services.AddSingleton<IFileChunker, TsVueChunker>();`
   (additive — one more line, doesn't touch the existing Roslyn registration).
3. Tests: a new `TsVueChunkerTests.cs` (mirror `CSharpRoslynChunkerTests.cs`'s structure/style) —
   a `.ts` file with a few top-level functions produces one chunk per function; a `.vue` SFC with a
   `<script setup>` block containing a couple of functions/consts produces chunks from the script
   block; a file with no recognizable declarations falls back to one whole-file chunk (not zero
   chunks); `CanHandle` returns true only for `.ts`/`.tsx`/`.vue`, false for `.cs`/other extensions.
   Also add an `IndexingPipelineTests.cs` case (or confirm an equivalent already covers this)
   proving a directory with both a `.cs` file and a `.ts`/`.vue` file gets both chunked by their
   respective chunkers in one `IndexDirectoryAsync` run.

## Acceptance
`dotnet build`/`dotnet test` stay green. A new `TsVueChunker` chunks `.ts`/`.tsx`/`.vue` files
(declaration-boundary regex scan, whole-file fallback), is registered alongside the existing
Roslyn chunker, and doesn't interfere with `.cs` file handling.

## Out of scope
- Any other language (Java/Go/Python/etc.) — TS/Vue only for this pass.
- Call-graph extraction (`ICallSiteExtractor`) for TS/Vue — chunking only.
- Fine-grained parsing of `<template>`/`<style>` blocks in `.vue` files — script block only.
