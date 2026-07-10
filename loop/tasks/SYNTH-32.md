---
id: SYNTH-32
summary: "Dimension-mismatch guard on Qdrant collection upsert"
status: done
acceptance_command: "dotnet build src/Synth.slnx --nologo -v q && dotnet test src/Synth.slnx --nologo -v q && grep -rq 'DimensionMismatchException' src/Synth.Core/"
acceptance_criterion: ""
boundaries: "Only touch src/Synth.Core/ (new exception type) and src/Synth.Api/Storage/QdrantCodeChunkStore.cs. Do not touch LocalCodeChunkStore.cs — it's an in-memory dev/test double with no fixed per-collection dimension, unlike Qdrant which enforces one at the gRPC level; forcing the same check onto it would be artificial. Do not touch IndexingEndpoints.cs or IIndexJobTracker — an exception thrown out of IndexingPipeline.IndexDirectoryAsync already propagates up and is caught there, setting IndexJobStatus.Error to the exception's Message (already tested behavior from SYNTH-31) — no endpoint change needed, just throw a clear exception."
limits: "max_iterations=25; max_minutes=120"
labels: [backend, indexing, reliability]
---

# SYNTH-32: Dimension-mismatch guard on Qdrant collection upsert

## Context
Real incident this session (2026-07-10, part of issue #42): switching the embedding provider
to a model with a different output dimension while a Qdrant collection already existed at the
old dimension caused a raw `Grpc.Core.RpcException: expected dim: 768, got 2560` deep inside
`QdrantCodeChunkStore.UpsertAsync` → `_client.UpsertAsync`. Had to be diagnosed via Qdrant's own
REST API and fixed by manually deleting the stale collection.

`QdrantCodeChunkStore.EnsureCollectionAsync` (`src/Synth.Api/Storage/QdrantCodeChunkStore.cs`)
currently does this:
```csharp
private async Task EnsureCollectionAsync(string collectionName, ulong vectorSize, CancellationToken cancellationToken)
{
    if (await CollectionExistsAsync(collectionName, cancellationToken).ConfigureAwait(false))
        return;

    await _client.CreateCollectionAsync(
        collectionName,
        new VectorParams { Size = vectorSize, Distance = Distance.Cosine },
        cancellationToken: cancellationToken).ConfigureAwait(false);
}
```
If the collection already exists, it just returns — no check that the incoming `vectorSize`
(from the current embedding config) actually matches what the collection was created with. The
mismatch is only discovered when Qdrant itself rejects the upsert with its raw gRPC error.

Sonar guards against this proactively in its own `IndexingPipeline` (probe dimension → compare
to existing collection → throw a clear `DimensionMismatchException` before indexing starts).
Port the same idea, scoped to where Synth's equivalent check naturally belongs: inside
`EnsureCollectionAsync`, since it already has the collection name and the incoming vector size
right there.

`Qdrant.Client` (already a project dependency, v1.15.1) exposes
`QdrantClient.GetCollectionInfoAsync(string collectionName, CancellationToken)` returning a
`CollectionInfo`. The existing single-vector dimension is reachable via
`collectionInfo.Config.Params.VectorsConfig.Params.Size` (confirmed by decompiling the installed
package — this is the same shape `EnsureCollectionAsync` writes today via the implicit
`VectorParams` → `VectorsConfig` conversion passed to `CreateCollectionAsync`, i.e. the
single/unnamed-vector case, not the named multi-vector `ParamsMap` case, so `.Params.Size` is
the right path, not `.ParamsMap`).

## What to do
1. Add a new exception type to `Synth.Core` (e.g. `src/Synth.Core/DimensionMismatchException.cs`):
   a plain `Exception` subclass, constructor takes `(string collection, int expectedDimension, int actualDimension)`,
   builds a clear, actionable message, e.g.:
   `"Collection 'default' was indexed with 768-dimensional embeddings, but the current embedding configuration produces 2560-dimensional vectors. Re-index into a new collection, or switch the embedding provider/model back to a 768-dimensional one."`
   Expose the three values as public read-only properties too (`Collection`, `ExpectedDimension`, `ActualDimension`) for anything that wants to inspect them programmatically later, not just read `.Message`.
2. In `QdrantCodeChunkStore.EnsureCollectionAsync`, when the collection already exists: call
   `_client.GetCollectionInfoAsync(collectionName, cancellationToken)`, read the existing vector
   size off `Config.Params.VectorsConfig.Params.Size`, and compare it to the incoming `vectorSize`
   parameter (both are `ulong`/`int` — cast consistently, check the existing method signature's
   type for `vectorSize`). If they differ, throw `DimensionMismatchException` with the collection
   name and both dimensions. If they match, return as today (no-op, collection is fine to upsert into).
3. Tests: check whether `Synth.Api.Tests` already has a test fixture for `QdrantCodeChunkStore`
   (e.g. under a `Storage/` test folder) that runs against a real Qdrant instance when a
   connection string is configured, else skips gracefully — follow whatever pattern is already
   established there (check other tests in the same file/project for the live-vs-skip convention
   used, e.g. `Skip.If`/a conditional `[Fact]` attribute/environment check). Add a test that:
   upserts chunks with an N-dimensional embedding to create a collection, then attempts to upsert
   a second batch of chunks with a *different* dimension into the same collection name, and
   asserts `DimensionMismatchException` is thrown with the correct collection name and both
   dimensions. If no live Qdrant is configured in the test environment, this test should skip
   like its neighbors do — don't invent a new skip mechanism.

## Acceptance
`dotnet build`/`dotnet test` on `src/Synth.slnx` stay green. `DimensionMismatchException` exists
in `Synth.Core` and is thrown by `QdrantCodeChunkStore.EnsureCollectionAsync` when an upsert
targets an existing collection with a different vector dimension than the collection was created
with, with a clear message naming the collection and both dimensions.

## Out of scope
- `LocalCodeChunkStore` — no fixed per-collection dimension concept, don't force one onto it.
- Any UI/client change — the exception's `.Message` already flows through to `IndexJobStatus.Error`
  and renders in `IndexPanel.vue`'s existing error display (SYNTH-31), no new plumbing needed.
- Automatic migration/re-embedding on mismatch — surfacing a clear error is the goal here, not
  silently fixing the mismatch.
