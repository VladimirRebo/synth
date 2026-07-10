using System.Security.Cryptography;
using System.Text;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using Synth.Core;

namespace Synth.Api.Storage;

/// <summary>
/// Qdrant-backed <see cref="ICodeChunkStore"/>. Stores each chunk's embedding vector as
/// a point plus the chunk's fields as payload, so search and get-by-file can rebuild the
/// full <see cref="CodeChunk"/>. The collection is created lazily on first upsert using the
/// incoming vector's dimensionality with cosine distance. Infrastructure plumbing only —
/// no reranking (that's a later task).
/// </summary>
public sealed class QdrantCodeChunkStore : ICodeChunkStore
{
    // Payload keys. Kept in one place so write (Upsert) and read (Search/GetByFile) agree.
    private const string FilePathKey = "filePath";
    private const string RelativePathKey = "relativePath";
    private const string NamespaceKey = "namespace";
    private const string ClassNameKey = "className";
    private const string MethodNameKey = "methodName";
    private const string ChunkTypeKey = "chunkType";
    private const string ContentKey = "content";
    private const string SummaryKey = "summary";
    private const string StartLineKey = "startLine";
    private const string EndLineKey = "endLine";
    private const string FileHashKey = "fileHash";

    private readonly QdrantClient _client;

    public QdrantCodeChunkStore(QdrantClient client) => _client = client;

    public async Task UpsertAsync(string collection, IEnumerable<CodeChunk> chunks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);

        var collectionName = SanitizeCollectionName(collection);

        var points = new List<PointStruct>();
        var vectorSize = 0;
        foreach (var chunk in chunks)
        {
            var vector = chunk.Embedding.ToArray();
            if (vector.Length == 0)
                continue; // Nothing to index without an embedding.

            vectorSize = vector.Length;

            var point = new PointStruct
            {
                Id = ToPointId(chunk.ChunkId),
                Vectors = vector,
                Payload =
                {
                    [FilePathKey] = chunk.FilePath,
                    [RelativePathKey] = chunk.RelativePath,
                    [NamespaceKey] = chunk.Namespace,
                    [ClassNameKey] = chunk.ClassName,
                    [MethodNameKey] = chunk.MethodName,
                    [ChunkTypeKey] = (long)chunk.ChunkType,
                    [ContentKey] = chunk.Content,
                    [SummaryKey] = chunk.Summary,
                    [StartLineKey] = (long)chunk.StartLine,
                    [EndLineKey] = (long)chunk.EndLine,
                    [FileHashKey] = chunk.FileHash,
                },
            };

            points.Add(point);
        }

        if (points.Count == 0)
            return;

        await EnsureCollectionAsync(collectionName, (ulong)vectorSize, cancellationToken).ConfigureAwait(false);

        await _client.UpsertAsync(collectionName, points, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<(CodeChunk Chunk, float Score)>> SearchAsync(
        string collection,
        ReadOnlyMemory<float> queryVector,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var collectionName = SanitizeCollectionName(collection);

        if (limit <= 0 || !await CollectionExistsAsync(collectionName, cancellationToken).ConfigureAwait(false))
            return [];

        var hits = await _client.SearchAsync(
            collectionName,
            queryVector,
            limit: (ulong)limit,
            payloadSelector: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return hits.Select(hit => (ToChunk(hit.Payload), hit.Score)).ToList();
    }

    public async Task<IReadOnlyList<CodeChunk>> GetByFileAsync(
        string collection,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        var collectionName = SanitizeCollectionName(collection);

        if (!await CollectionExistsAsync(collectionName, cancellationToken).ConfigureAwait(false))
            return [];

        var response = await _client.ScrollAsync(
            collectionName,
            filter: Conditions.MatchKeyword(RelativePathKey, relativePath),
            limit: uint.MaxValue,
            payloadSelector: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return response.Result
            .Select(point => ToChunk(point.Payload))
            .OrderBy(chunk => chunk.StartLine)
            .ToList();
    }

    public async Task<IReadOnlyList<string>> ListRelativePathsAsync(
        string collection,
        CancellationToken cancellationToken = default)
    {
        var collectionName = SanitizeCollectionName(collection);

        if (!await CollectionExistsAsync(collectionName, cancellationToken).ConfigureAwait(false))
            return [];

        // Scroll the whole collection pulling only the relativePath payload key (not the full
        // chunk payload) — this is a cheap projection used just to diff against on-disk files.
        var response = await _client.ScrollAsync(
            collectionName,
            filter: null,
            limit: uint.MaxValue,
            payloadSelector: new[] { RelativePathKey },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return response.Result
            .Select(point => GetString(point.Payload, RelativePathKey))
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public async Task DeleteByFileAsync(
        string collection,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        var collectionName = SanitizeCollectionName(collection);

        if (!await CollectionExistsAsync(collectionName, cancellationToken).ConfigureAwait(false))
            return;

        // Delete every point for this file by the same relativePath filter GetByFileAsync reads with.
        await _client.DeleteAsync(
            collectionName,
            Conditions.MatchKeyword(RelativePathKey, relativePath),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteCollectionAsync(string collection, CancellationToken cancellationToken = default)
    {
        var collectionName = SanitizeCollectionName(collection);

        // Guard on existence so deleting a collection that isn't there is a clean no-op rather than
        // a gRPC error, matching the store's "unknown collection yields nothing" stance elsewhere.
        if (!await CollectionExistsAsync(collectionName, cancellationToken).ConfigureAwait(false))
            return;

        await _client.DeleteCollectionAsync(collectionName, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private Task<bool> CollectionExistsAsync(string collectionName, CancellationToken cancellationToken) =>
        _client.CollectionExistsAsync(collectionName, cancellationToken);

    private async Task EnsureCollectionAsync(string collectionName, ulong vectorSize, CancellationToken cancellationToken)
    {
        if (await CollectionExistsAsync(collectionName, cancellationToken).ConfigureAwait(false))
        {
            // Collection already exists at a fixed dimension. Qdrant would otherwise reject the
            // upsert with a raw "expected dim: N, got M" gRPC error; surface a clear one up front.
            var info = await _client.GetCollectionInfoAsync(collectionName, cancellationToken).ConfigureAwait(false);
            var existingSize = info.Config.Params.VectorsConfig.Params.Size;
            if (existingSize != vectorSize)
                throw new DimensionMismatchException(collectionName, (int)existingSize, (int)vectorSize);

            return;
        }

        await _client.CreateCollectionAsync(
            collectionName,
            new VectorParams { Size = vectorSize, Distance = Distance.Cosine },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    // Qdrant collection names must be non-empty and are used verbatim in URLs/gRPC, so map the
    // caller's name to a safe form: lowercase, and anything outside [a-z0-9_-] becomes '-'. Falls
    // back to the default when the input reduces to nothing. This sanitization matters once
    // SYNTH-18 derives collection names from git URLs.
    private static string SanitizeCollectionName(string collection)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);

        var sanitized = new StringBuilder(collection.Length);
        foreach (var ch in collection.ToLowerInvariant())
        {
            sanitized.Append(ch is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-' ? ch : '-');
        }

        var result = sanitized.ToString();
        return string.IsNullOrEmpty(result) ? CollectionNames.Default : result;
    }

    private static CodeChunk ToChunk(IReadOnlyDictionary<string, Value> payload) => new()
    {
        FilePath = GetString(payload, FilePathKey),
        RelativePath = GetString(payload, RelativePathKey),
        Namespace = GetString(payload, NamespaceKey),
        ClassName = GetString(payload, ClassNameKey),
        MethodName = GetString(payload, MethodNameKey),
        ChunkType = (ChunkType)GetInt(payload, ChunkTypeKey),
        Content = GetString(payload, ContentKey),
        Summary = GetString(payload, SummaryKey),
        StartLine = (int)GetInt(payload, StartLineKey),
        EndLine = (int)GetInt(payload, EndLineKey),
        FileHash = GetString(payload, FileHashKey),
    };

    private static string GetString(IReadOnlyDictionary<string, Value> payload, string key) =>
        payload.TryGetValue(key, out var value) && value.HasStringValue ? value.StringValue : string.Empty;

    private static long GetInt(IReadOnlyDictionary<string, Value> payload, string key) =>
        payload.TryGetValue(key, out var value) && value.HasIntegerValue ? value.IntegerValue : 0L;

    // Qdrant point IDs are UUIDs or unsigned ints; derive a stable UUID from the chunk's
    // string identity so re-upserting the same chunk overwrites rather than duplicates.
    private static PointId ToPointId(string chunkId)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(chunkId));
        return new Guid(hash);
    }
}
