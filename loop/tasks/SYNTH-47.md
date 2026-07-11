---
id: SYNTH-47
summary: "Collection browse/preview: inspect a file's chunks + embedding text"
status: open
acceptance_command: "dotnet build src/Synth.slnx --nologo -v q && dotnet test src/Synth.slnx --nologo -v q && grep -rq 'GetByFileAsync' src/Synth.Api/Search/SearchEndpoints.cs"
acceptance_criterion: ""
boundaries: "Backend: touch only src/Synth.Api/Search/SearchEndpoints.cs (or a new small file in the same directory) and tests. This reuses ICodeChunkStore.GetByFileAsync, which already exists — do not add a new store method. Client: a new small component, wired into the existing routed pages (Sidebar.vue navigation), not merged into SearchPanel.vue."
limits: "max_iterations=25; max_minutes=120"
labels: [backend, client, search]
---

# SYNTH-47: Collection browse/preview

## Context
Part of issue #49. There's currently no way to inspect *how* Synth chunked a given file — no way to
see the chunk boundaries, chunk types, or the exact text that was embedded for it. When chunking or
search quality looks off, the only debugging tool today is reading the chunker's source directly.

`ICodeChunkStore.GetByFileAsync(collection, relativePath, cancellationToken)` already exists and
returns every chunk for a file, ordered by `StartLine` — this task is mostly plumbing a thin
endpoint + a small client panel on top of it, not new store logic.

## What to do
1. Add `GET /repositories/{collection}/files/{*relativePath}` (bare route; `{*relativePath}` catches
   the rest of the path including any `/` segments, since relative paths are nested) to
   `SearchEndpoints.cs` (or a small new file in the same directory if that reads cleaner) — calls
   `ICodeChunkStore.GetByFileAsync(collection, relativePath, cancellationToken)` and returns the
   chunks. Project each chunk to a small response shape carrying at least: `chunkType`, `className`,
   `methodName`, `startLine`, `endLine`, `content`, `summary`, and the assembled `EmbeddingText`
   (already a computed property on `CodeChunk` — expose it here since that's the actual point of
   this endpoint, seeing what was embedded, not just the raw content). Return `404`/empty array (your
   call, pick one and be consistent) when the file has no chunks (not indexed, or a genuinely empty
   file).
2. Client: a new small component (e.g. `src/client/src/components/BrowsePanel.vue`) with its own
   routed page (check `router.ts` and `Sidebar.vue` for how existing pages — Search/Index/MCP/
   Settings/Logs — are wired in, follow the same pattern for a new "Browse" entry): a collection
   picker (reuse `useRepositories()`), a plain text input for a relative path (per this project's
   established "typing a path is fine for a personal tool" bar — no file-tree picker), and a list of
   the returned chunks showing type/line-range/embedding-text (a `<pre>` block or similar is fine,
   no syntax highlighting required though reusing the existing `highlight.ts` helper from
   `SearchResultItem.vue` is a reasonable nice-to-have if it's a small addition).
3. Add a corresponding `getFileChunks(collection, relativePath)` function to `api.ts`, typed against
   the new endpoint's response shape.
4. Tests: a backend test for the new endpoint (file with several chunks returns them in line order;
   an unindexed/unknown file returns the empty/404 case); a client test for `BrowsePanel.vue`
   (renders returned chunks, handles the empty case) following this project's `vi.mock('../api')`
   pattern.

## Acceptance
`dotnet build`/`dotnet test` stay green, `npm test`/`npm run build` stay green. A new endpoint
returns a file's chunks (including embedding text) for a given collection + relative path. A new
routed client page lets you pick a collection, type a path, and see the resulting chunks.

## Out of scope
- A file-tree browser for picking the path — plain text input is enough.
- Any change to `ICodeChunkStore` or the chunking pipeline itself — this is read-only inspection of
  already-indexed data.
