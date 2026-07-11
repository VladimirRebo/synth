---
id: SYNTH-42
summary: "MCP tools: get_symbol + get_file"
status: open
acceptance_command: "dotnet build src/Synth.slnx --nologo -v q && dotnet test src/Synth.slnx --nologo -v q && grep -rq 'get_symbol' src/Synth.Api/Mcp/ && grep -rq 'get_file' src/Synth.Api/Mcp/"
acceptance_criterion: ""
boundaries: "New files under src/Synth.Api/Mcp/, one new public method on GitRepoService (checkout-path resolution, reusing not duplicating its existing private ResolveWorkspaceRoot logic), registration in both Program.cs and src/Synth.Mcp.Stdio/StdioMcpHost.cs, and tests. Do NOT change ICodeChunkStore's public surface for get_symbol if GetByFileAsync-style filtering can be added as a new method following the same pattern already established (GetByFileAsync exists — add an analogous GetBySymbolAsync, don't repurpose an existing method's contract)."
limits: "max_iterations=30; max_minutes=150"
labels: [backend, mcp, search]
---

# SYNTH-42: MCP tools get_symbol + get_file

## Context
Part of issue #44. Synth's MCP surface today: `search_code` (vector search), `find_callers`/
`find_callees` (call graph), `index_code` (trigger indexing). Sonar has two more tools worth
porting first (highest value for an agent): `get_symbol` (exact class/method lookup, no embedding
call — cheap and precise when an agent already knows the name it wants, complementing
`search_code`'s fuzzy vector search) and `get_file` (read a file's full content by relative path,
with a path-traversal guard — useful once an agent has found a relevant chunk via search and wants
full file context).

## Part 1: get_symbol
1. Add `Task<IReadOnlyList<CodeChunk>> GetBySymbolAsync(string collection, string? className, string? methodName, CancellationToken cancellationToken = default);`
   to `ICodeChunkStore` (`src/Synth.Core/ICodeChunkStore.cs`) — at least one of `className`/
   `methodName` must be provided by the caller (validate this at the MCP tool layer, not the store);
   matches are case-insensitive exact matches against `CodeChunk.ClassName`/`CodeChunk.MethodName`
   (mirror `CodeChunk.QualifiedName`'s own `Namespace.ClassName.MethodName` convention — the same one
   `find_callers`/`find_callees` already key their `symbol` parameter on).
2. Implement in `LocalCodeChunkStore` (LINQ filter over the collection's chunks) and
   `QdrantCodeChunkStore` (scroll with `Conditions.MatchKeyword` on the `className`/`methodName`
   payload keys already used by `UpsertAsync`, combined with AND when both are given — mirror
   `GetByFileAsync`'s existing scroll-and-filter style in that file).
3. Add `src/Synth.Api/Mcp/GetSymbolTool.cs`: `[McpServerToolType]`, static
   `[McpServerTool(Name = "get_symbol")]` method taking `className`/`methodName` (both nullable,
   `[Description]`s explaining at least one is required) + the usual optional `collection` param
   (mirror `CodeSearchTool`'s `collection` handling — default to `CollectionNames.Default` when
   unset). Returns the matching chunks projected the same way `CodeSearchResult` does (reuse that
   type or a close sibling — no embedding score to report here since this bypasses vector search
   entirely, so either omit the score field or make it optional/omitted for this path, your call).

## Part 2: get_file
1. `GitRepoService` needs a way to resolve a collection's on-disk root without re-cloning: for a
   `repoUrl`-indexed collection, the checkout lives at `{WorkspaceRoot}/{slug}` (the same `slug`
   that equals the collection name — confirmed in `IndexingEndpoints.StartIndexing`, `collection = info.Slug`).
   Add a small public method to `GitRepoService`, e.g. `public string ResolveCheckoutPath(string slug)`,
   that reuses the existing private `ResolveWorkspaceRoot` logic (make it accessible from the new
   method rather than duplicating the default-path/env-expansion logic inline).
2. Add `src/Synth.Api/Mcp/GetFileTool.cs`: `[McpServerToolType]`, static
   `[McpServerTool(Name = "get_file")]` method taking `relativePath` (required) + `collection`
   (optional, default `CollectionNames.Default`). Resolve the collection's root: look it up via
   `IRepositoryRegistry.ListAsync()` to find the matching `RepositoryEntry`; if `SourceType == "local"`,
   the root is `entry.Source` directly; otherwise (github/gitlab), the root is
   `gitRepoService.ResolveCheckoutPath(collection)`. Combine root + `relativePath` into a full path,
   **guard against path traversal**: resolve to a full path (`Path.GetFullPath`) and verify it still
   starts with the resolved root (reject with a clear error otherwise — don't let `relativePath`
   contain `..` segments that escape the root). Enforce a size limit (10 MB, matching Sonar's own
   `get_file` limit) — reject files larger than that with a clear message rather than reading them
   fully into memory. Return the file's content as a string (or a clear error: file not found,
   unknown collection, path escapes root, file too large).
3. Register both new tools in both transports: `.WithTools<GetSymbolTool>().WithTools<GetFileTool>()`
   in `Program.cs`'s HTTP MCP host chain and `StdioMcpHost.cs`'s chain (stdio's DI setup will need
   `IRepositoryRegistry`/`GitRepoService` available — check how `IndexCodeTool`'s stdio registration
   from SYNTH-36 wired these up, since it needed the same dependencies, and follow the same pattern).

## Tests
- `GetBySymbolAsync` on both stores: matches by class only, by method only, by both, no match.
- `GetSymbolTool`: returns matches, rejects when neither className nor methodName is given.
- `GetFileTool`: reads a real file's content for a local-indexed collection fixture; rejects a
  `relativePath` containing `..` that would escape the root; rejects an unknown collection; rejects
  (or truncates, your call, but pick one and test it) a file over the size limit — use a small
  temp-directory fixture, not real repo files, for this.

## Acceptance
`dotnet build`/`dotnet test` stay green. `get_symbol` and `get_file` exist on both MCP transports;
`get_symbol` does exact class/method lookup with no embedding call; `get_file` reads a file by
relative path with a path-traversal guard and a 10 MB size limit, resolving the root correctly for
both local-path and repoUrl-indexed collections.

## Out of scope
- `search_by_file` (all chunks of a file, vs. `get_file`'s raw disk read) — lower priority, not part
  of this task.
- Any client UI for either tool — MCP-only surfaces for this pass.
