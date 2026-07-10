---
id: SYNTH-34
summary: "DELETE /repositories/{collection} — remove indexed collection + registry entry + graph edges"
status: done
acceptance_command: "dotnet build src/Synth.slnx --nologo -v q && dotnet test src/Synth.slnx --nologo -v q && grep -rq 'MapDelete(\"/repositories' src/Synth.Api/"
acceptance_criterion: ""
boundaries: "Route must be bare (/repositories/{collection}, no /api prefix — the client's Vite dev proxy already strips /api; this exact mistake has bitten this project before on the Settings endpoints). Touch: src/Synth.Api/Vcs/RepositoryEndpoints.cs, IRepositoryRegistry.cs + its Mongo/InMemory implementations, ICodeChunkStore.cs (one new delete-collection method) + its Qdrant/Local implementations, and tests. Do NOT touch the git checkout under ~/.synth/workspaces — cleaning that up is a separate issue (#46) that depends on this one existing; leave the on-disk clone alone for now. Do NOT touch IndexingEndpoints.cs or the client."
limits: "max_iterations=25; max_minutes=120"
labels: [backend, api, operability]
---

# SYNTH-34: DELETE /repositories/{collection}

# Context
Part of issue #43. There is currently no way to remove an indexed repository/collection from
Synth at all — no `DELETE` endpoint exists anywhere in `src/Synth.Api/`, no UI button. Once
something is indexed, the only way to get rid of it is going around the app entirely (this
session, a stale/duplicate Qdrant collection had to be cleared by hand via `curl` against
Qdrant's own REST API).

Three things need to go away together when a collection is deleted, so the deletion is complete
rather than leaving orphaned state behind:
1. The vector-store collection itself (`ICodeChunkStore` has no delete-collection method today —
   only `UpsertAsync`/`SearchAsync`/`GetByFileAsync`; add one).
2. The `IRepositoryRegistry` entry for that collection (`GET /repositories` would otherwise keep
   listing a collection that no longer has any data).
3. The collection's call-graph edges in `ICodeGraphStore` — this interface already has exactly
   the right tool for this: `ReplaceEdgesAsync(collection, edges, ct)` is a full delete-then-insert,
   so calling it with an empty list (`ReplaceEdgesAsync(collection, [], ct)`) clears a collection's
   edges without needing a new method on that interface.

# What to do
1. Add `Task DeleteCollectionAsync(string collection, CancellationToken cancellationToken = default);`
   to `ICodeChunkStore` (`src/Synth.Core/ICodeChunkStore.cs`).
2. Implement in `LocalCodeChunkStore`: remove the collection's bucket from `_collections` (no-op if
   it doesn't exist).
3. Implement in `QdrantCodeChunkStore`: call the Qdrant client's `DeleteCollectionAsync(collectionName, ...)`
   (already available on `QdrantClient`, same client this file already holds) — guard with the
   existing `CollectionExistsAsync` check first so deleting a non-existent collection is a clean
   no-op, not an error.
4. Add `Task DeleteAsync(string collection, CancellationToken cancellationToken = default);` to
   `IRepositoryRegistry` (`src/Synth.Api/Vcs/IRepositoryRegistry.cs`), implement in both
   `MongoRepositoryRegistry` and `InMemoryRepositoryRegistry` (remove the entry keyed by
   `collection`; no-op if absent).
5. Add `DELETE /repositories/{collection}` to `RepositoryEndpoints.cs` (bare route, matching the
   existing `GET /repositories` in the same file): calls, in order, `ICodeChunkStore.DeleteCollectionAsync`,
   `ICodeGraphStore.ReplaceEdgesAsync(collection, [])`, and `IRepositoryRegistry.DeleteAsync` — if the
   collection wasn't in the registry to begin with, still attempt the store/graph cleanup (defensive:
   the registry and the actual store could theoretically drift) but return 404 if the registry had no
   entry for it, matching how a "delete something that doesn't exist" request should read to a caller.
6. Client: in `IndexPanel.vue`'s "Indexed repositories" list, add a delete action per row (a small
   button/icon) that calls the new endpoint via a new `deleteRepository(collection)` function in
   `api.ts`, with a plain `confirm()` guard before sending the request (matches this project's
   existing "single-user local tool" simplicity bar — no custom modal needed), then refreshes the
   list via the existing `useRepositories()` composable's `refresh()`.
7. Tests: `RepositoryEndpoints`/registry tests for the new delete path (successful delete removes
   the entry from a subsequent `ListAsync`/`GET /repositories`; deleting an unknown collection
   returns 404); a client test for `IndexPanel.vue`'s delete button (calls the API, refreshes the
   list, confirm() gate) following this project's existing `vi.mock('../api')` pattern.

# Acceptance
`dotnet build`/`dotnet test` stay green. `DELETE /repositories/{collection}` (bare route) removes
the Qdrant/Local collection, its registry entry, and its call-graph edges; returns 404 for an
unknown collection. Client can delete a row from the Indexed repositories list with a confirmation
step.

# Out of scope
- Cleaning up the git checkout directory under `~/.synth/workspaces` — separate issue (#46),
  intentionally deferred so that issue can build directly on this endpoint existing.
- Any bulk/multi-select delete — one collection at a time is enough for this pass.
