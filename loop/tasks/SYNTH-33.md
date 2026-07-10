---
id: SYNTH-33
summary: "Incremental indexing: skip unchanged files, delete stale ones"
status: open
acceptance_command: "dotnet build src/Synth.slnx --nologo -v q && dotnet test src/Synth.slnx --nologo -v q && grep -rq 'ListRelativePathsAsync' src/Synth.Core/ICodeChunkStore.cs && grep -rq 'DeleteByFileAsync' src/Synth.Core/ICodeChunkStore.cs"
acceptance_criterion: ""
boundaries: "Touch: src/Synth.Core/ICodeChunkStore.cs (two new interface methods), src/Synth.Core/LocalCodeChunkStore.cs, src/Synth.Api/Storage/QdrantCodeChunkStore.cs, src/Synth.Core/IndexingPipeline.cs, and their tests. Do NOT skip the chunking/call-site-extraction step for unchanged files — only skip the embedding-generator call and the store upsert (see the correctness note in Context below about why this distinction matters for the call graph). Do NOT touch IndexingEndpoints.cs, IIndexJobTracker, or any client code — this is purely an internal pipeline optimization; the public IndexingSummary/IndexJobStatus shape and its FilesIndexed/FilesSkipped counters are unchanged in meaning, just cheaper to produce."
limits: "max_iterations=30; max_minutes=150"
labels: [backend, indexing, performance]
---

# SYNTH-33: Incremental indexing: skip unchanged files, delete stale ones

## Context
Part of issue #42. `IndexingPipeline.IndexDirectoryAsync` currently re-chunks AND re-embeds every
matching file on every run, even when nothing changed — every file's chunks are always sent
through the embedding generator and upserted. `CodeChunk.FileHash` is already computed per file
during chunking (`CSharpRoslynChunker.ChunkFile` → `ComputeFileHash(content)`) and stored on every
chunk, but nothing ever reads it back to decide whether a file's embedding can be skipped. For a
repo with hundreds of files this wastes time and (for cloud embedding providers) money re-embedding
identical content on every re-index.

**Critical correctness note — read before implementing.** Do NOT skip the per-file chunking step
itself for unchanged files, only the embedding+upsert step. Chunking also performs call-site
extraction for the call graph (`rawCallSites`/`knownSymbols` accumulated during the per-file walk,
resolved once at the end into `ICodeGraphStore` via `ReplaceEdgesAsync`). If you skip chunking
entirely for a file whose content hasn't changed, that file's calls silently disappear from the
freshly-resolved call graph on every subsequent (mostly-unchanged) re-index — a real regression.
Chunking itself is cheap, CPU-only, no network calls; the embedding generator call is the actual
expensive/costly step this task should skip for unchanged files. So: always chunk every on-disk
file (as today, unchanged), but before calling the embedding generator + `UpsertAsync` for a given
file's freshly-produced chunks, check whether that file's content hash matches what's already
stored — if so, skip straight to the next file without touching the embedding generator or the
store for this file at all.

## What to do
1. Add two methods to `ICodeChunkStore` (`src/Synth.Core/ICodeChunkStore.cs`):
   - `Task<IReadOnlyList<string>> ListRelativePathsAsync(string collection, CancellationToken cancellationToken = default);`
     — every distinct `RelativePath` currently stored in `collection` (used to detect files that
     were indexed before but no longer exist on disk).
   - `Task DeleteByFileAsync(string collection, string relativePath, CancellationToken cancellationToken = default);`
     — removes every chunk for `relativePath` in `collection`.
2. Implement both in `LocalCodeChunkStore` (`src/Synth.Core/LocalCodeChunkStore.cs`) — trivial
   dictionary operations over the existing `_collections` structure.
3. Implement both in `QdrantCodeChunkStore` (`src/Synth.Api/Storage/QdrantCodeChunkStore.cs`):
   - `ListRelativePathsAsync`: scroll the collection selecting only the `relativePath` payload key
     (mirror the existing `GetByFileAsync`'s scroll call, but without pulling full payloads — check
     `Qdrant.Client`'s scroll API for a payload-selector-by-key-list option; if none exists cleanly,
     it's acceptable to scroll with the full payload selector and just project `RelativePathKey`
     client-side, matching this method's existing style elsewhere in the file), return the distinct
     set.
   - `DeleteByFileAsync`: delete points matching `Conditions.MatchKeyword(RelativePathKey, relativePath)`
     (the same filter `GetByFileAsync` already uses to read) via the client's delete-by-filter API.
   - Both should return early (no-op) if the collection doesn't exist yet, same guard `GetByFileAsync`
     already uses.
4. In `IndexingPipeline.IndexDirectoryAsync`, restructure the per-file loop:
   - Keep the existing chunking + call-site-extraction step unchanged for every on-disk file (see
     the correctness note above — this must NOT be skipped).
   - Before embedding a file's freshly-produced chunks, look up the file's previously-stored hash —
     reuse the existing `_store.GetByFileAsync(collection, relativePath)` and take the first result's
     `FileHash` if any chunks exist. If it equals the freshly-computed `FileHash` on this file's new
     chunks, skip calling the embedding generator and skip `UpsertAsync` for this file entirely —
     count it the same way an already-skipped file is counted today (fold into the existing
     `filesSkipped`/`FilesSkipped` counter rather than adding a new public field, to avoid touching
     `IndexingSummary`/`IndexJobStatus`'s shape).
   - After the on-disk walk finishes (all files chunked, embeddings done for changed/new files),
     call `_store.ListRelativePathsAsync(collection)`, diff against the set of relative paths seen
     during this walk, and call `_store.DeleteByFileAsync(collection, path)` for every path that's
     in the store but no longer on disk.
5. Tests (extend `IndexingPipelineTests.cs`, follow its existing `FakeEmbeddingGenerator` +
   `LocalCodeChunkStore` fixture pattern):
   - Indexing the same unchanged directory twice in a row only calls the embedding generator on
     the first run — assert `generator.CallCount` after the second `IndexDirectoryAsync` call
     equals the count after the first (no new calls for unchanged files).
   - Changing one file's content between two runs causes exactly that file to be re-embedded
     (generator call count increases by the changed file's chunk-producing call), while an
     untouched sibling file is not.
   - Deleting a file from disk between two runs removes its chunks from the store on the second
     `IndexDirectoryAsync` call (assert `store.GetByFileAsync` returns empty for the deleted file's
     path afterward).
   - The call-graph regression this task must NOT introduce: re-indexing an *unchanged* directory
     twice still returns the same call-graph edges the second time (reuse the existing
     `IndexDirectoryAsync_populates_call_graph_across_files`-style fixture, call
     `IndexDirectoryAsync` twice back to back with no file changes in between, assert
     `FindCalleesAsync`/`FindCallersAsync` still return the expected edges after the second run —
     this is the test that would catch the "skipped chunking silently drops call-graph edges"
     mistake described above if the fix were implemented incorrectly).

## Acceptance
`dotnet build`/`dotnet test` on `src/Synth.slnx` stay green. Re-indexing an unchanged directory
does not call the embedding generator again for unchanged files. Changing a file re-embeds only
that file. Deleting a file from disk and re-indexing removes its chunks from the store. The call
graph remains fully correct across a no-op re-index (unchanged files are still chunked, so their
call sites are still extracted and resolved every run).

## Out of scope
- A local on-disk hash cache file (mirroring Sonar's `~/.sonar/hashes/...`) — reading `FileHash`
  back from the store via `GetByFileAsync` is the simpler mechanism for this pass; only add a
  separate cache later if that read-back proves too slow in practice.
- Batching the stale-file deletions (Sonar batches by 100) — a plain sequential loop over
  `DeleteByFileAsync` calls is fine at Synth's realistic personal-repo scale; batch later if it
  proves to matter.
- Any change to `IndexingSummary`/`IndexJobStatus`'s public shape, the REST/MCP surface, or the client.
