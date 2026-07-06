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
    // Single collection for all code chunks; cosine distance matches the Local store.
    internal const string CollectionName = "code_chunks";

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

    public async Task UpsertAsync(IEnumerable<CodeChunk> chunks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);

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

        await EnsureCollectionAsync((ulong)vectorSize, cancellationToken).ConfigureAwait(false);

        await _client.UpsertAsync(CollectionName, points, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<(CodeChunk Chunk, float Score)>> SearchAsync(
        ReadOnlyMemory<float> queryVector,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0 || !await CollectionExistsAsync(cancellationToken).ConfigureAwait(false))
            return [];

        var hits = await _client.SearchAsync(
            CollectionName,
            queryVector,
            limit: (ulong)limit,
            payloadSelector: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return hits.Select(hit => (ToChunk(hit.Payload), hit.Score)).ToList();
    }

    public async Task<IReadOnlyList<CodeChunk>> GetByFileAsync(
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        if (!await CollectionExistsAsync(cancellationToken).ConfigureAwait(false))
            return [];

        var response = await _client.ScrollAsync(
            CollectionName,
            filter: Conditions.MatchKeyword(RelativePathKey, relativePath),
            limit: uint.MaxValue,
            payloadSelector: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return response.Result
            .Select(point => ToChunk(point.Payload))
            .OrderBy(chunk => chunk.StartLine)
            .ToList();
    }

    private Task<bool> CollectionExistsAsync(CancellationToken cancellationToken) =>
        _client.CollectionExistsAsync(CollectionName, cancellationToken);

    private async Task EnsureCollectionAsync(ulong vectorSize, CancellationToken cancellationToken)
    {
        if (await CollectionExistsAsync(cancellationToken).ConfigureAwait(false))
            return;

        await _client.CreateCollectionAsync(
            CollectionName,
            new VectorParams { Size = vectorSize, Distance = Distance.Cosine },
            cancellationToken: cancellationToken).ConfigureAwait(false);
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
