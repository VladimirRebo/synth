---
id: SYNTH-19
summary: "Wire remote repo indexing into POST /index + a Mongo-backed repository registry"
status: done
acceptance_command: "dotnet build src/Synth.slnx --nologo -v q && dotnet test src/Synth.slnx --nologo -v q && grep -riq 'RepoUrl' src/Synth.Api/Indexing/IndexingEndpoints.cs && grep -riq 'RepositoryRegistry' src/Synth.Api -r"
acceptance_criterion: ""
boundaries: "Only wire GitRepoService (SYNTH-18) into the indexing endpoint and add the repository registry + its listing endpoint. Do not touch the Vue client. Do not add webhooks, AI review, or issue automation (backlog, issue #22). Existing local-path indexing behavior (POST /index with just \"path\") must be unchanged, still using CollectionNames.Default."
limits: "max_iterations=30; max_minutes=150"
labels: [backend, vcs, api]
---

# SYNTH-19: Wire remote repo indexing into POST /index + a repository registry

## Context
SYNTH-17 made the store/pipeline/search collection-aware; SYNTH-18 added `GitRepoService` +
`RepoUrlInfo` to clone/fetch a repo and derive a stable collection name from its URL. This task
connects the two: let `POST /index` accept a repository URL instead of a local path, and keep
track of what's been indexed so it can be listed (the client will need this in SYNTH-20 to offer
a collection picker, and `search_code`/`GET /search` callers need to know valid collection names
to pass).

Existing local-path indexing must keep behaving exactly as it does today (single implicit
`CollectionNames.Default` collection) — this task is additive.

## What to do
1. Extend `IndexRequest` in `src/Synth.Api/Indexing/IndexingEndpoints.cs` with optional
   `string? RepoUrl` and `string? Branch` fields alongside the existing `Path`. Validation: exactly
   one of `Path`/`RepoUrl` must be provided (`BadRequest` otherwise, mirroring the existing
   "directory not found" `BadRequest` style).
   - When `Path` is given: index it into `CollectionNames.Default`, unchanged from today.
   - When `RepoUrl` is given: call `GitRepoService.EnsureRepoAsync(repoUrl, branch)` to get a local
     checkout path, derive the collection name via `RepoUrlInfo` (from SYNTH-18), then run
     `IndexingPipeline.IndexDirectoryAsync` against that path/collection.
2. Add a small repository registry so indexed sources are discoverable:
   - `src/Synth.Api/Vcs/IRepositoryRegistry.cs` — something like
     `Task UpsertAsync(RepositoryEntry entry, CancellationToken ct)` and
     `Task<IReadOnlyList<RepositoryEntry>> ListAsync(CancellationToken ct)`, where
     `RepositoryEntry` carries at least `{ Collection, SourceType (local|github|gitlab), Source
     (the path or URL), Branch, LastIndexedAt, ChunkCount }`.
   - `MongoRepositoryRegistry : IRepositoryRegistry` storing one document per collection in a
     Mongo collection (e.g. `repositories`), reusing the `IMongoDatabase` already registered via
     `AddMongoDBClient("synthconfig")` in `Program.cs` — mirror `MongoConfigStore`'s
     "Mongo unreachable → degrade gracefully instead of throwing" pattern
     (`src/Synth.Api/Configuration/MongoConfigStore.cs`) for the same reasons (no live Mongo
     required in tests/dev).
   - After a successful index run (both the `Path` and `RepoUrl` branches), upsert a
     `RepositoryEntry` with the resulting `IndexingSummary.ChunksIndexed` and current UTC time.
3. Add `GET /repositories` mapping to `IRepositoryRegistry.ListAsync`, returning the list of known
   collections and their metadata. Map it in `Program.cs` next to the other `Map*Endpoints()` calls.
4. Tests:
   - `IndexingEndpointTests`: a case using a local `file://` fixture repo (same kind of fixture as
     SYNTH-18's tests, no live GitHub/GitLab network call) exercising the `RepoUrl` branch
     end-to-end — asserts the response is a 200 with a non-zero `ChunksIndexed`, and that a
     registry entry now exists for the derived collection.
   - A registry test (in-memory/fake Mongo or a lightweight fake `IRepositoryRegistry`
     implementation, matching however this repo already tests Mongo-backed pieces) covering
     upsert + list round-tripping.

## Acceptance
`dotnet build`/`dotnet test` on `src/Synth.slnx` stay green; `POST /index` accepts either `path`
or `repoUrl`(+`branch`); a successful index run of either kind is reflected in
`GET /repositories`; existing local-path-only indexing behavior is unchanged.

## Out of scope
- Vue client changes (repo-URL input, collection picker) — done directly after this lands.
- Webhooks, AI review, issue auto-resolution — backlog, issue #22.
