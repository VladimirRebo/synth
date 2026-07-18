using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Synth.Domain.Graph;
using Synth.Application.Vcs;
using Synth.Domain.Vcs;
using Synth.Domain;

namespace Synth.Application;

/// <summary>
/// Ties chunking, embedding and storage together for a directory of source files:
/// walk the tree, chunk each supported file with the first matching
/// <see cref="IFileChunker"/>, embed every chunk's <see cref="CodeChunk.EmbeddingText"/>
/// via the registered <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/>, and upsert
/// the embedded chunks into <see cref="ICodeChunkStore"/>. Mirrors Sonar's
/// <c>IndexingPipeline</c>, simplified. Search/reranking live in a later task.
/// Chunkers that also implement <see cref="ICallSiteExtractor"/> additionally feed a syntax-heuristic
/// call graph into <see cref="ICodeGraphStore"/>, resolved collection-wide at the end of a run.
/// </summary>
public sealed class IndexingPipeline
{
    /// <summary>Directory names never descended into (build output, VCS metadata, JS deps).</summary>
    private static readonly string[] SkippedDirectorySegments = ["bin", "obj", ".git", "node_modules"];

    /// <summary>
    /// Bounded per-file concurrency for the indexing walk (SYNTH-44). Each file's cost is dominated by
    /// the embedding-generator HTTP round-trip, so overlapping several files' embed calls is the whole
    /// point; the cap keeps generator/store load and memory bounded. Hardcoded by design — making it
    /// configurable is explicitly out of scope for this task.
    /// </summary>
    private const int MaxIndexingConcurrency = 6;

    /// <summary>
    /// Per-file resilience for the embed+upsert step (SYNTH-46). A single transient hiccup — the
    /// embedding generator (Ollama) momentarily unreachable, a Qdrant upsert timeout — shouldn't take
    /// down a whole indexing run. Each file's embed+upsert is attempted up to <see cref="MaxRetries"/>
    /// times total with a short exponential backoff between attempts; only genuinely transient failures
    /// qualify (see <see cref="IsTransient"/>). Hardcoded by design — making these configurable via
    /// Settings is explicitly out of scope for this task.
    /// </summary>
    private const int MaxRetries = 3;

    /// <summary>
    /// Backoff waited before each retry: after attempt 1 fails, then after attempt 2. Its length is
    /// <see cref="MaxRetries"/> - 1 (the final attempt is never followed by a wait). Deliberately tiny
    /// so the common all-succeeds case pays nothing and a flaky moment costs about a second, not
    /// minutes — a slow retry storm would defeat the point of overlapping files at all.
    /// </summary>
    private static readonly TimeSpan[] RetryBackoffs =
        [TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(800)];

    private readonly IReadOnlyList<IFileChunker> _chunkers;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly ICodeChunkStore _store;
    private readonly ICodeGraphStore _graphStore;

    public IndexingPipeline(
        IEnumerable<IFileChunker> chunkers,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        ICodeChunkStore store,
        ICodeGraphStore graphStore)
    {
        ArgumentNullException.ThrowIfNull(chunkers);
        _chunkers = chunkers.ToList();
        _embeddingGenerator = embeddingGenerator ?? throw new ArgumentNullException(nameof(embeddingGenerator));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _graphStore = graphStore ?? throw new ArgumentNullException(nameof(graphStore));
    }

    /// <summary>
    /// Indexes every supported file under <paramref name="rootPath"/> (recursively,
    /// skipping <c>bin/</c>, <c>obj/</c> and <c>.git/</c>) into <paramref name="collection"/>.
    /// Files that no chunker handles, are empty, or cannot be read are skipped rather than
    /// aborting the run.
    /// </summary>
    /// <param name="progress">
    /// Optional, additive progress sink (issue #39): reported once at the start with the upfront
    /// total-file count, then after each indexed file, and once more with the final counts. Callers
    /// that omit it (tests, existing callers) are unaffected — behavior is otherwise identical.
    /// </param>
    /// <param name="repoInfo">
    /// Optional, additive (SYNTH-40): the parsed remote-repo URL when indexing a <c>repoUrl</c>-sourced
    /// collection. When provided, each chunk gets a provider blob URL (<see cref="CodeChunk.SourceUrl"/>)
    /// built from it and <paramref name="branch"/>. When null — the local-path case, and every existing
    /// caller that omits it — <see cref="CodeChunk.SourceUrl"/> stays null.
    /// </param>
    /// <param name="branch">
    /// Optional, additive (SYNTH-40): the indexed branch, or null when the repo's default branch was
    /// used (the blob URL then uses the literal <c>HEAD</c> segment). Only meaningful together with
    /// <paramref name="repoInfo"/>.
    /// </param>
    /// <returns>A summary of how many files were indexed vs. skipped and the chunk total.</returns>
    public async Task<IndexingSummary> IndexDirectoryAsync(
        string collection,
        string rootPath,
        CancellationToken cancellationToken = default,
        IProgress<IndexingProgress>? progress = null,
        RepoUrlInfo? repoInfo = null,
        string? branch = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        if (!Directory.Exists(rootPath))
            throw new DirectoryNotFoundException($"Index root not found: {rootPath}");

        var filesIndexed = 0;
        var filesSkipped = 0;
        var chunksIndexed = 0;

        // Count matching files upfront so progress carries a denominator. EnumerateSourceFiles is a
        // lazy directory walk, so this .Count() is a plain filesystem pass — cheap next to embedding.
        var totalFiles = EnumerateSourceFiles(rootPath).Count();
        progress?.Report(new IndexingProgress(filesIndexed, filesSkipped, totalFiles));

        // Call-graph accumulators (stage 1). Only lightweight strings are held across files — never the
        // full CodeChunk content — so this stays within the existing per-file streaming design. Because
        // the per-file loop runs concurrently (SYNTH-44), these are thread-safe collections: a
        // ConcurrentBag for the append-only call sites, and ConcurrentDictionary-as-set for the two
        // sets, whose keys feed the sequential stage-2 resolution below unchanged.
        var rawCallSites = new ConcurrentBag<RawCallSite>();
        // Qualified name -> the relative path it was declared in (used by ResolveEdges to derive a
        // coarse per-language bucket, so a call site is never matched against a same-named symbol from
        // an unrelated language — see ResolveEdges/LanguageOf).
        var knownSymbols = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        // Relative paths of every on-disk file seen this run. Diffed against the store at the end to
        // delete chunks of files that were indexed before but no longer exist on disk (SYNTH-33).
        var seenPaths = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

        // Parallelize the per-file work with bounded concurrency (SYNTH-44). Each file's dominant cost is
        // the embedding-generator round-trip, so overlapping several files' embed calls is where the time
        // is won; chunking, hashing and upsert are cheap by comparison. The counters and accumulators
        // touched inside the body are all thread-safe (Interlocked / concurrent collections), and the
        // ICodeChunkStore is safe for concurrent GetByFile/Upsert. Everything AFTER this loop stays
        // strictly sequential.
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = MaxIndexingConcurrency,
            CancellationToken = cancellationToken,
        };

        await Parallel.ForEachAsync(EnumerateSourceFiles(rootPath), parallelOptions, async (filePath, ct) =>
        {
            var chunker = _chunkers.FirstOrDefault(c => c.CanHandle(filePath));
            if (chunker is null)
            {
                Interlocked.Increment(ref filesSkipped);
                return;
            }

            string content;
            try
            {
                content = await File.ReadAllTextAsync(filePath, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable file: skip it, don't take down the whole run.
                Interlocked.Increment(ref filesSkipped);
                return;
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                Interlocked.Increment(ref filesSkipped);
                return;
            }

            var relativePath = NormalizeRelativePath(Path.GetRelativePath(rootPath, filePath));
            seenPaths.TryAdd(relativePath, 0);
            var chunks = chunker.Chunk(filePath, relativePath, content);
            if (chunks.Count == 0)
            {
                Interlocked.Increment(ref filesSkipped);
                return;
            }

            // Stamp a remote blob URL onto each chunk when indexing a repoUrl-sourced collection
            // (SYNTH-40). No-op for the local-path case (repoInfo == null): SourceUrl stays null.
            if (repoInfo is not null)
            {
                chunks = chunks
                    .Select(c => c.WithSourceUrl(
                        SourceUrlBuilder.Build(repoInfo, branch, c.RelativePath, c.StartLine, c.EndLine)))
                    .ToList();
            }

            // Stage 1: remember this file's declared method/constructor names (candidate callees) and,
            // if the chunker supports it, its raw call sites — for collection-wide resolution below.
            // This runs for EVERY on-disk file, even ones whose embedding is skipped below: chunking is
            // cheap and CPU-only, and skipping it would silently drop the file's call-graph edges on a
            // no-op re-index (see SYNTH-33). Only the embedding + upsert is skipped for unchanged files.
            foreach (var chunk in chunks)
            {
                if (IsCallableMember(chunk.ChunkType) && chunk.QualifiedName.Length > 0)
                    knownSymbols.TryAdd(chunk.QualifiedName, chunk.RelativePath);
            }

            if (chunker is ICallSiteExtractor extractor)
            {
                foreach (var site in extractor.ExtractCallSites(filePath, relativePath, content))
                    rawCallSites.Add(site);
            }

            // Incremental skip: if the file's content hash already stored matches this run's freshly
            // computed hash (every chunk of a file carries the same FileHash), the file is unchanged —
            // skip the expensive embedding-generator call and the upsert entirely, counting it the same
            // way as any other skipped file. The call graph above was already updated, so it stays correct.
            var freshHash = chunks[0].FileHash;
            var stored = await _store.GetByFileAsync(collection, relativePath, ct);
            if (stored.Count > 0 && string.Equals(stored[0].FileHash, freshHash, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref filesSkipped);
                return;
            }

            // The file changed since it was last indexed (or is new). Clear any chunks from its
            // previous chunking first: CodeChunk.ChunkId is "{RelativePath}:{StartLine}-{EndLine}", so
            // an edit that shifts line numbers anywhere before a file's last chunk gives every
            // following chunk a new ID — upserting only adds/overwrites points by ID, it never removes
            // ones whose ID no longer appears in the fresh set. Without this, the store would
            // accumulate an orphaned duplicate of every chunk after the edit point on essentially every
            // edit, not just a chunker-shape change. A no-op for a genuinely new file (stored.Count ==
            // 0 leaves nothing to delete). The brief window between this delete and the upsert below
            // means a file can end up with zero chunks if the embed+upsert step then exhausts its
            // retries — an acceptable tradeoff against silently-growing duplicate garbage, and the same
            // one the stale-file cleanup below already makes unconditionally.
            if (stored.Count > 0)
                await _store.DeleteByFileAsync(collection, relativePath, ct);

            var embedded = await TryEmbedAndUpsertAsync(collection, chunks, ct);
            if (embedded is null)
            {
                // The embed+upsert step failed transiently (generator/store momentarily unreachable, a
                // timeout) and stayed failed after exhausting its retries. Count this one file as skipped
                // — the same policy the unreadable-file branch above uses — and move on, rather than
                // letting one flaky moment abort the whole run. Non-transient failures (e.g. a
                // DimensionMismatchException) are never retried and have already propagated out to fail
                // the job, exactly as they did before this change.
                Interlocked.Increment(ref filesSkipped);
                return;
            }

            Interlocked.Increment(ref filesIndexed);
            Interlocked.Add(ref chunksIndexed, embedded.Count);

            // Report the latest snapshot of the (now thread-safe) counters. Reports may interleave across
            // concurrent files — that's fine, the client only cares about the newest snapshot, not a
            // strict per-file sequence — and the final post-loop report below always carries the exact
            // totals regardless of ordering.
            progress?.Report(new IndexingProgress(
                Volatile.Read(ref filesIndexed), Volatile.Read(ref filesSkipped), totalFiles));
        });

        // Delete chunks of files that were indexed on a previous run but are no longer on disk. Diff the
        // store's known relative paths against the ones seen during this walk, and drop the stragglers.
        var storedPaths = await _store.ListRelativePathsAsync(collection, cancellationToken);
        foreach (var stalePath in storedPaths)
        {
            if (!seenPaths.ContainsKey(stalePath))
                await _store.DeleteByFileAsync(collection, stalePath, cancellationToken);
        }

        // Stage 2: resolve every raw call site against the whole collection's known method names and
        // replace the graph in one shot. An empty edge set (no .cs files, or no resolved calls) still
        // replaces — clearing any stale edges from a previous index run.
        var edges = ResolveEdges(collection, rawCallSites, knownSymbols);
        await _graphStore.ReplaceEdgesAsync(collection, edges, cancellationToken);

        // Final report guarantees the last-seen counts match the summary even when the run ended on a
        // string of skipped files (which don't emit a per-file report of their own).
        progress?.Report(new IndexingProgress(filesIndexed, filesSkipped, totalFiles));

        return new IndexingSummary(filesIndexed, filesSkipped, chunksIndexed);
    }

    // Chunk kinds that represent a callable member (a candidate callee). Class/interface/property/etc.
    // chunks are never call targets. MethodHead/MethodBody are two halves of one long method, so they
    // fold to the same qualified name — the HashSet in stage 1 dedups them.
    private static bool IsCallableMember(ChunkType chunkType) =>
        chunkType is ChunkType.Method or ChunkType.Constructor or ChunkType.MethodHead or ChunkType.MethodBody;

    // Resolves raw call sites into edges by matching each invoked simple name against every known
    // qualified name sharing that last segment AND the same source language (see LanguageOf) — one
    // invoked name matching several methods emits one edge per match (approximate, by design — issue
    // #33); a name matching nothing (or only candidates from a different language) emits no edge.
    private static IReadOnlyList<CallEdge> ResolveEdges(
        string collection,
        IEnumerable<RawCallSite> callSites,
        IEnumerable<KeyValuePair<string, string>> knownSymbols)
    {
        // Live-verified bug: without the language key, a JS/TS "new Set(...)" (the built-in collection
        // type) resolved against a same-named C# test helper's Set() method, purely because both files
        // happened to be indexed into the same collection — the bare-name match had no notion of which
        // language a call site or a declared symbol actually came from. Each chunker owns disjoint file
        // extensions, so a call site can never legitimately target a symbol from a different chunker's
        // language; bucketing by (simple name, language) closes that entire cross-language false-
        // positive class in one place, rather than enumerating each language's built-ins one at a time.
        var bySimpleName = knownSymbols
            .GroupBy(kv => (Name: SimpleNameOf(kv.Key), Language: LanguageOf(kv.Value)))
            .ToDictionary(group => group.Key, group => group.Select(kv => kv.Key).ToList());

        var edges = new List<CallEdge>();
        foreach (var site in callSites)
        {
            var key = (Name: site.InvokedName, Language: LanguageOf(site.SourceFile));
            if (!bySimpleName.TryGetValue(key, out var callees))
                continue;

            foreach (var callee in callees)
                edges.Add(new CallEdge(collection, site.CallerQualifiedName, callee, site.SourceFile, site.Line));
        }

        return edges;
    }

    private static string SimpleNameOf(string qualifiedName)
    {
        var lastDot = qualifiedName.LastIndexOf('.');
        return lastDot < 0 ? qualifiedName : qualifiedName[(lastDot + 1)..];
    }

    // Coarse per-language bucket derived from a file's extension — exactly the boundary each chunker's
    // own CanHandle already draws (disjoint extensions, per AddSynthIndexing's registration comment),
    // so this mirrors chunker identity without needing to thread the chunker itself through.
    private static string LanguageOf(string relativePath)
    {
        var ext = Path.GetExtension(relativePath);
        return ext.ToLowerInvariant() switch
        {
            ".cs" => "csharp",
            ".py" => "python",
            ".go" => "go",
            ".ts" or ".tsx" or ".vue" => "tsvue",
            _ => "other",
        };
    }

    // Embeds a file's chunks and upserts them, retrying the whole step a few times with backoff when
    // it fails transiently (SYNTH-46). Returns the embedded chunks on success, or null when every
    // attempt hit a transient failure — the caller then counts the file as skipped, exactly as it does
    // for an unreadable file, instead of letting one flaky moment abort the entire run. A NON-transient
    // exception (e.g. DimensionMismatchException from SYNTH-32) is never caught here: it propagates out
    // to fail the job as it always has, since retrying it would only fail identically and burn the
    // budget for nothing. Only the embed+upsert is wrapped — chunking (a parse failure isn't transient)
    // ran earlier and is deliberately outside this retry.
    private async Task<IReadOnlyList<CodeChunk>?> TryEmbedAndUpsertAsync(
        string collection,
        IReadOnlyList<CodeChunk> chunks,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                var embedded = await EmbedAsync(chunks, cancellationToken);
                await _store.UpsertAsync(collection, embedded, cancellationToken);
                return embedded;
            }
            catch (Exception ex) when (IsTransient(ex, cancellationToken))
            {
                if (attempt == MaxRetries)
                    return null; // Exhausted the retry budget on transient failures — skip this file.

                await Task.Delay(RetryBackoffs[attempt - 1], cancellationToken);
            }
        }

        return null; // Unreachable: the loop above always returns a result or null.
    }

    // A transient failure is one worth retrying because it may clear on its own: a network error, a
    // timeout that isn't the caller's own cancellation, or a gRPC Unavailable/DeadlineExceeded from the
    // Qdrant client. Everything else (a real bug, a dimension mismatch, a genuine external cancellation)
    // is NOT transient and must not be retried. The timeout-vs-cancellation check mirrors
    // EmbeddingSettingsEndpoints.ProbeAsync: an OperationCanceledException (TaskCanceledException is one)
    // is a genuine cancellation only when the caller's own token actually asked to stop; otherwise it's
    // an internal timeout and is retryable.
    private static bool IsTransient(Exception ex, CancellationToken cancellationToken) => ex switch
    {
        HttpRequestException => true,
        OperationCanceledException => !cancellationToken.IsCancellationRequested,
        _ => IsTransientRpcException(ex),
    };

    // The Qdrant store lives in another assembly and pulls in Grpc.Core; Synth.Core doesn't reference
    // it, so recognize its RpcException by type name and read the StatusCode enum reflectively rather
    // than taking a whole gRPC dependency just to name the type. Only Unavailable and DeadlineExceeded
    // are transient — a schema/dimension error surfaces as a different status (or a different exception
    // type entirely) and must not be blanket-retried.
    private static bool IsTransientRpcException(Exception ex)
    {
        if (ex.GetType().FullName != "Grpc.Core.RpcException")
            return false;

        return ex.GetType().GetProperty("StatusCode")?.GetValue(ex) is Enum statusCode
            && statusCode.ToString() is "Unavailable" or "DeadlineExceeded";
    }

    // Embeds a whole file's chunks in one batched generator call, then attaches each
    // resulting vector to a copy of its chunk (the model is init-only / immutable).
    private async Task<IReadOnlyList<CodeChunk>> EmbedAsync(
        IReadOnlyList<CodeChunk> chunks,
        CancellationToken cancellationToken)
    {
        var texts = chunks.Select(chunk => chunk.EmbeddingText).ToList();
        var embeddings = await _embeddingGenerator.GenerateAsync(texts, cancellationToken: cancellationToken);

        var embedded = new List<CodeChunk>(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
            embedded.Add(chunks[i].WithEmbedding(embeddings[i].Vector));

        return embedded;
    }

    // Enumerate every file any registered chunker claims (CanHandle), not a hard-coded extension —
    // so newly registered chunkers (e.g. TsVueChunker for .ts/.tsx/.vue) are picked up here without
    // touching the dispatch logic below. Walks manually (rather than
    // Directory.EnumerateFiles(..., AllDirectories)) so a single unreadable subdirectory is skipped
    // like any other unreadable file, instead of throwing out of the lazy iterator and aborting the run.
    private IEnumerable<string> EnumerateSourceFiles(string rootPath) =>
        EnumerateFilesRecursive(rootPath).Where(file => _chunkers.Any(chunker => chunker.CanHandle(file)));

    private static IEnumerable<string> EnumerateFilesRecursive(string directory)
    {
        string[] files;
        string[] subdirectories;
        try
        {
            files = Directory.GetFiles(directory);
            subdirectories = Directory.GetDirectories(directory)
                .Where(d => !SkippedDirectorySegments.Contains(Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
                .ToArray();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Can't read this directory (permissions, broken symlink, etc.) — skip it rather than
            // aborting the whole run, same policy as an individual unreadable file.
            yield break;
        }

        foreach (var file in files)
            yield return file;

        foreach (var subdirectory in subdirectories)
        {
            foreach (var file in EnumerateFilesRecursive(subdirectory))
                yield return file;
        }
    }

    // Use forward slashes so RelativePath / ChunkId are stable across operating systems.
    private static string NormalizeRelativePath(string relativePath) =>
        relativePath.Replace('\\', '/');
}

/// <summary>
/// Observability summary of a single <see cref="IndexingPipeline.IndexDirectoryAsync"/> run:
/// how many files produced chunks, how many were skipped (unsupported/empty/unreadable),
/// and the total number of chunks upserted.
/// </summary>
public readonly record struct IndexingSummary(int FilesIndexed, int FilesSkipped, int ChunksIndexed);

/// <summary>
/// A single progress report emitted by <see cref="IndexingPipeline.IndexDirectoryAsync"/> when a
/// caller supplies an <see cref="IProgress{T}"/> sink (issue #39). <paramref name="TotalFiles"/> is
/// the upfront count of matching files (the denominator); <paramref name="FilesIndexed"/> and
/// <paramref name="FilesSkipped"/> are the running tallies.
/// </summary>
public readonly record struct IndexingProgress(int FilesIndexed, int FilesSkipped, int TotalFiles);
