---
id: SYNTH-40
summary: "SourceUrl on CodeChunk + link in search results (GitHub/GitLab)"
status: done
acceptance_command: "dotnet build src/Synth.slnx --nologo -v q && dotnet test src/Synth.slnx --nologo -v q && grep -rq 'SourceUrl' src/Synth.Core/CodeChunk.cs"
acceptance_criterion: ""
boundaries: "Touch: src/Synth.Core/CodeChunk.cs, a new src/Synth.Core/Vcs/SourceUrlBuilder.cs (or similar name), src/Synth.Core/IndexingPipeline.cs (new optional parameter, additive — do not change its existing required-parameter behavior for callers that omit it), src/Synth.Api/Indexing/IndexingEndpoints.cs (pass RepoUrlInfo/branch through for the repoUrl case only), src/Synth.Api/Mcp/CodeSearchResult.cs, src/Synth.api/Storage/QdrantCodeChunkStore.cs (persist the new field), src/Synth.Core/LocalCodeChunkStore.cs (already stores full CodeChunk, likely no change needed there — verify), client SearchResultItem.vue, and tests. Local-path-indexed collections simply have a null SourceUrl on every chunk — do not error or warn for that case, it's the normal/expected state for local indexing."
limits: "max_iterations=30; max_minutes=150"
labels: [backend, client, search]
---

# SYNTH-40: SourceUrl on CodeChunk + link in search results

## Context
Part of issue #56. `CodeChunk` has no `SourceUrl` field at all today. For repositories indexed by
`repoUrl` (GitHub/GitLab, via `POST /index`'s repo-URL branch in `IndexingEndpoints.cs`), a search
result only shows a bare relative path — there's no way to jump from a result straight to the file
on GitHub/GitLab at the matched line, even though Synth already knows the remote URL (it's what
`GitRepoService` cloned from) and the exact line range of every chunk.

`RepoUrlInfo` (`src/Synth.Core/Vcs/RepoUrlInfo.cs`) already parses a repo URL into `Host`, `Owner`,
`Name`, `Provider`, `Slug` — enough to build a blob URL without needing the raw URL string (avoids
trailing-`.git` edge cases). Blob URL shapes:
- GitHub: `https://{Host}/{Owner}/{Name}/blob/{branch}/{relativePath}#L{start}-L{end}`
- GitLab: `https://{Host}/{Owner}/{Name}/-/blob/{branch}/{relativePath}#L{start}-{end}`

When the indexed branch is unspecified (`IndexRequest.Branch` is null — the repo's default branch
was used, per `GitRepoService.EnsureRepoAsync`'s `origin/HEAD` fallback), use the literal segment
`HEAD` in the blob URL — both GitHub and GitLab resolve `/blob/HEAD/...` to the default branch.

`IndexingEndpoints.MapIndexingEndpoints`'s `POST /index` handler already has everything needed to
build this per-run: the parsed `RepoUrlInfo info` (repo-URL branch only) and `branch` variable are
already in scope there, just not currently passed into `pipeline.IndexDirectoryAsync(...)`.

## What to do
1. Add `public string? SourceUrl { get; init; }` to `CodeChunk` (nullable — local-path-indexed
   repos have no meaningful source URL; leave it null for those, don't invent a fake one).
2. Add a small static helper, e.g. `Synth.Core.Vcs.SourceUrlBuilder.Build(RepoUrlInfo info, string? branch, string relativePath, int startLine, int endLine) -> string?` implementing the two blob-URL shapes above by `info.Provider` (GitHub/GitLab); return `null` for `GitProvider.Other` (no known blob-URL shape to build). Unit-test this helper directly (a few cases: GitHub with an explicit branch, GitHub with null branch → `HEAD`, GitLab's `/-/blob/` shape, `Other` provider → null) — it's pure and easy to test in isolation.
3. Give `IndexingPipeline.IndexDirectoryAsync` a new optional parameter, added last (after the
   existing `progress` parameter, matching how `progress` itself was added additively in SYNTH-30
   so existing callers/tests that omit it are unaffected): something like
   `(RepoUrlInfo? repoInfo = null, string? branch = null)`. When `repoInfo` is provided, populate
   `SourceUrl` on each chunk via the new builder before embedding/upserting; when it's null (the
   local-path case, and every existing caller that doesn't pass it), `SourceUrl` stays null exactly
   as `CodeChunk`'s default.
4. In `IndexingEndpoints.cs`'s detached background task, pass `info`/`branch` into the
   `pipeline.IndexDirectoryAsync(...)` call only in the `repoUrl` branch (the `hasPath` branch has
   no `RepoUrlInfo` and should keep omitting these params, defaulting to null as today).
5. Thread `SourceUrl` through to callers: `CodeSearchResult` (`src/Synth.Api/Mcp/CodeSearchResult.cs`)
   gains a `string? SourceUrl` field, populated in `CodeSearchResult.From` from
   `scored.Chunk.SourceUrl` — this flows to both `GET /search` (REST) and `search_code` (MCP)
   automatically, same pattern the `Score` field already follows.
6. Persist the new field in `QdrantCodeChunkStore` (a new payload key, written in `UpsertAsync`,
   read back in `ToChunk`) — verify `LocalCodeChunkStore` needs no change since it likely stores the
   full `CodeChunk` object as-is already (check before assuming).
7. Client: in `SearchResultItem.vue`, when a result has a `sourceUrl`, render it as a link (open in
   a new tab, `target="_blank" rel="noopener noreferrer"`) instead of (or alongside) the plain
   relative-path text currently shown; when `sourceUrl` is absent (local-path-indexed results),
   render the path exactly as today, unchanged.
8. Tests: the `SourceUrlBuilder` unit tests from step 2; an `IndexingPipelineTests.cs` test proving
   a chunk indexed with a `repoInfo` argument gets the expected `SourceUrl`, and one proving the
   existing no-`repoInfo` behavior is unchanged (`SourceUrl` stays null); a `CodeSearchResult`/search
   endpoint test asserting the field round-trips; a `SearchResultItem.test.ts` (or wherever client
   search-result tests live) case for the link rendering vs. the no-URL fallback.

## Acceptance
`dotnet build`/`dotnet test` stay green. Indexing a `repoUrl`-sourced collection populates each
chunk's `SourceUrl` with a correct GitHub/GitLab blob-URL-with-line-range; local-path-indexed
chunks have `SourceUrl: null`. `GET /search`/`search_code` include the field. The client renders a
clickable link when present, the plain path when not.

## Out of scope
- Bitbucket or any provider beyond GitHub/GitLab (`GitProvider.Other` → `SourceUrl` stays null,
  matching this project's existing GitHub/GitLab-only VCS scope).
- The separate local-editor deep-link feature (issue #57) — that's a different mechanism
  (`jetbrains://`/`vscode://` protocol links for local-path-indexed results), not part of this task.
