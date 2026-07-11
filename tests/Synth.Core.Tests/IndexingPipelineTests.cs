using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Synth.Application;
using Synth.Core;
using Synth.Domain.Graph;
using Synth.Core.Vcs;
using Synth.Domain.Vcs;
using Synth.Domain;

namespace Synth.Core.Tests;

// Proves SYNTH-10: IndexingPipeline walks a directory, chunks each *.cs file with the
// CSharpRoslynChunker, embeds every chunk via a deterministic fake generator (no live
// Ollama), and upserts the results into the in-memory LocalCodeChunkStore from SYNTH-9.
public class IndexingPipelineTests : IDisposable
{
    // A deterministic, offline IEmbeddingGenerator: each text maps to a fixed-length
    // vector derived from its hash. No network, no Ollama — same text => same vector.
    private sealed class FakeEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public const int Dimensions = 8;

        private int _callCount;

        // Thread-safe: the pipeline embeds files concurrently (SYNTH-44), so several files may call
        // GenerateAsync at the same time. A plain field++ would lose increments and make the
        // call-count assertions flaky; Interlocked keeps the tally exact under concurrency.
        public int CallCount => Volatile.Read(ref _callCount);

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            var embeddings = new GeneratedEmbeddings<Embedding<float>>(
                values.Select(text => new Embedding<float>(Vectorize(text))));
            return Task.FromResult(embeddings);
        }

        private static float[] Vectorize(string text)
        {
            var vector = new float[Dimensions];
            unchecked
            {
                var hash = 17;
                foreach (var ch in text)
                    hash = hash * 31 + ch;

                for (var i = 0; i < Dimensions; i++)
                    vector[i] = ((hash >> i) & 1) == 0 ? -1f : 1f;
            }

            return vector;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    // A generator that throws a caller-supplied exception on its first N GenerateAsync calls, then
    // (if N is finite) succeeds — used to exercise the per-file embed+upsert retry (SYNTH-46). N < 0
    // means "always fail". CallCount is Interlocked-tracked so a single file's sequential retries can
    // be asserted exactly.
    private sealed class FlakyEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        private readonly Func<Exception> _exceptionFactory;
        private readonly int _failuresBeforeSuccess;
        private int _callCount;

        public FlakyEmbeddingGenerator(Func<Exception> exceptionFactory, int failuresBeforeSuccess)
        {
            _exceptionFactory = exceptionFactory;
            _failuresBeforeSuccess = failuresBeforeSuccess;
        }

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _callCount);
            if (_failuresBeforeSuccess < 0 || call <= _failuresBeforeSuccess)
                throw _exceptionFactory();

            var embeddings = new GeneratedEmbeddings<Embedding<float>>(
                values.Select(_ => new Embedding<float>(new float[FakeEmbeddingGenerator.Dimensions])));
            return Task.FromResult(embeddings);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    // Minimal in-memory ICodeGraphStore for the pipeline's stage-2 output. The real fallback store
    // (InMemoryCodeGraphStore) lives in Synth.Api, which Synth.Core.Tests does not reference, so this
    // stands in — same delete-then-insert / by-collection lookup contract the pipeline relies on.
    private sealed class FakeCodeGraphStore : ICodeGraphStore
    {
        private readonly ConcurrentDictionary<string, List<CallEdge>> _byCollection = new();

        public Task ReplaceEdgesAsync(string collection, IReadOnlyList<CallEdge> edges, CancellationToken ct = default)
        {
            _byCollection[collection] = [.. edges];
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CallEdge>> FindCallersAsync(string collection, string symbol, CancellationToken ct = default) =>
            Task.FromResult(Query(collection, edge => edge.Callee == symbol));

        public Task<IReadOnlyList<CallEdge>> FindCalleesAsync(string collection, string symbol, CancellationToken ct = default) =>
            Task.FromResult(Query(collection, edge => edge.Caller == symbol));

        private IReadOnlyList<CallEdge> Query(string collection, Func<CallEdge, bool> predicate) =>
            _byCollection.TryGetValue(collection, out var edges) ? edges.Where(predicate).ToList() : [];
    }

    private readonly string _root;

    public IndexingPipelineTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "synth-indexing-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private void WriteFile(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static IndexingPipeline PipelineFor(
        ICodeChunkStore store,
        FakeEmbeddingGenerator generator,
        ICodeGraphStore? graphStore = null) =>
        new([new CSharpRoslynChunker()], generator, store, graphStore ?? new FakeCodeGraphStore());

    [Fact]
    public async Task IndexDirectoryAsync_chunks_embeds_and_stores_supported_files()
    {
        // Foo: one class + two methods => 3 chunks.
        WriteFile("Foo.cs", """
            namespace Sample;

            public class Foo
            {
                public int A() => 1;
                public int B() => 2;
            }
            """);

        // Bar: one class + one method => 2 chunks.
        WriteFile("nested/Bar.cs", """
            namespace Sample.Nested;

            public class Bar
            {
                public void Run() { }
            }
            """);

        var store = new LocalCodeChunkStore();
        var generator = new FakeEmbeddingGenerator();
        var pipeline = PipelineFor(store, generator);

        var summary = await pipeline.IndexDirectoryAsync(CollectionNames.Default, _root);

        Assert.Equal(2, summary.FilesIndexed);
        Assert.Equal(0, summary.FilesSkipped);
        Assert.Equal(5, summary.ChunksIndexed);

        var fooChunks = await store.GetByFileAsync(CollectionNames.Default, "Foo.cs");
        Assert.Equal(3, fooChunks.Count);

        var barChunks = await store.GetByFileAsync(CollectionNames.Default, "nested/Bar.cs");
        Assert.Equal(2, barChunks.Count);

        // Every stored chunk carries an embedding of the fake generator's dimension.
        foreach (var chunk in fooChunks.Concat(barChunks))
            Assert.Equal(FakeEmbeddingGenerator.Dimensions, chunk.Embedding.Length);
    }

    [Fact]
    public async Task IndexDirectoryAsync_chunks_cs_and_ts_vue_files_with_their_respective_chunkers()
    {
        // One .cs file (Roslyn chunker) and one .ts + one .vue file (TsVueChunker) in the same run.
        WriteFile("Backend.cs", """
            namespace Sample;

            public class Backend
            {
                public void Serve() { }
            }
            """);
        WriteFile("client/widget.ts", """
            export function render(): string {
                return "hi";
            }
            """);
        WriteFile("client/App.vue", """
            <template><div /></template>

            <script setup lang="ts">
            const title = () => "Synth";
            </script>
            """);

        var store = new LocalCodeChunkStore();
        var pipeline = new IndexingPipeline(
            [new CSharpRoslynChunker(), new TsVueChunker()],
            new FakeEmbeddingGenerator(),
            store,
            new FakeCodeGraphStore());

        var summary = await pipeline.IndexDirectoryAsync(CollectionNames.Default, _root);

        Assert.Equal(3, summary.FilesIndexed);
        Assert.Equal(0, summary.FilesSkipped);

        // The C# file is chunked by the Roslyn chunker (class + method).
        var csChunks = await store.GetByFileAsync(CollectionNames.Default, "Backend.cs");
        Assert.Contains(csChunks, c => c.ChunkType == ChunkType.Class && c.ClassName == "Backend");

        // The .ts and .vue files are chunked by the TsVueChunker.
        var tsChunks = await store.GetByFileAsync(CollectionNames.Default, "client/widget.ts");
        Assert.Contains(tsChunks, c => c.ChunkType == ChunkType.Method && c.MethodName == "render");

        var vueChunks = await store.GetByFileAsync(CollectionNames.Default, "client/App.vue");
        Assert.Contains(vueChunks, c => c.ChunkType == ChunkType.Method && c.MethodName == "title");
    }

    [Fact]
    public async Task IndexDirectoryAsync_with_repoInfo_stamps_source_url_on_every_chunk()
    {
        // Foo: class + method => 2 chunks, each getting a GitHub blob URL for its own line range.
        WriteFile("Foo.cs", """
            namespace Sample;
            public class Foo { public void M() { } }
            """);

        var store = new LocalCodeChunkStore();
        var pipeline = PipelineFor(store, new FakeEmbeddingGenerator());
        var repoInfo = RepoUrlInfo.Parse("https://github.com/owner/repo.git");

        await pipeline.IndexDirectoryAsync(
            CollectionNames.Default, _root, repoInfo: repoInfo, branch: "main");

        var chunks = await store.GetByFileAsync(CollectionNames.Default, "Foo.cs");
        Assert.NotEmpty(chunks);
        Assert.All(chunks, chunk =>
        {
            Assert.NotNull(chunk.SourceUrl);
            Assert.Equal(
                $"https://github.com/owner/repo/blob/main/Foo.cs#L{chunk.StartLine}-L{chunk.EndLine}",
                chunk.SourceUrl);
        });
    }

    [Fact]
    public async Task IndexDirectoryAsync_without_repoInfo_leaves_source_url_null()
    {
        WriteFile("Foo.cs", """
            namespace Sample;
            public class Foo { public void M() { } }
            """);

        var store = new LocalCodeChunkStore();
        var pipeline = PipelineFor(store, new FakeEmbeddingGenerator());

        // The local-path case (no repoInfo argument): SourceUrl stays null on every chunk.
        await pipeline.IndexDirectoryAsync(CollectionNames.Default, _root);

        var chunks = await store.GetByFileAsync(CollectionNames.Default, "Foo.cs");
        Assert.NotEmpty(chunks);
        Assert.All(chunks, chunk => Assert.Null(chunk.SourceUrl));
    }

    [Fact]
    public async Task IndexDirectoryAsync_skips_bin_obj_and_git_directories()
    {
        WriteFile("Real.cs", """
            namespace Sample;
            public class Real { public void M() { } }
            """);
        WriteFile("bin/Generated.cs", "public class GenBin { }");
        WriteFile("obj/Generated.cs", "public class GenObj { }");
        WriteFile(".git/Hook.cs", "public class GitHook { }");

        var store = new LocalCodeChunkStore();
        var pipeline = PipelineFor(store, new FakeEmbeddingGenerator());

        var summary = await pipeline.IndexDirectoryAsync(CollectionNames.Default, _root);

        // Only Real.cs is indexed; the build-output/VCS files are never enumerated.
        Assert.Equal(1, summary.FilesIndexed);
        Assert.Empty(await store.GetByFileAsync(CollectionNames.Default, "bin/Generated.cs"));
        Assert.Empty(await store.GetByFileAsync(CollectionNames.Default, "obj/Generated.cs"));
        Assert.Empty(await store.GetByFileAsync(CollectionNames.Default, ".git/Hook.cs"));
    }

    [Fact]
    public async Task IndexDirectoryAsync_skips_empty_files_without_aborting()
    {
        WriteFile("Empty.cs", "");
        WriteFile("Whitespace.cs", "   \n\t  ");
        WriteFile("Good.cs", """
            namespace Sample;
            public class Good { public void M() { } }
            """);

        var store = new LocalCodeChunkStore();
        var pipeline = PipelineFor(store, new FakeEmbeddingGenerator());

        var summary = await pipeline.IndexDirectoryAsync(CollectionNames.Default, _root);

        Assert.Equal(1, summary.FilesIndexed);
        Assert.Equal(2, summary.FilesSkipped);
        Assert.Equal(2, summary.ChunksIndexed); // Good: class + method.
    }

    [Fact]
    public async Task IndexDirectoryAsync_batches_one_generator_call_per_indexed_file()
    {
        WriteFile("One.cs", "namespace S; public class One { public void M() { } }");
        WriteFile("Two.cs", "namespace S; public class Two { public void M() { } }");

        var generator = new FakeEmbeddingGenerator();
        var pipeline = PipelineFor(new LocalCodeChunkStore(), generator);

        await pipeline.IndexDirectoryAsync(CollectionNames.Default, _root);

        Assert.Equal(2, generator.CallCount);
    }

    // A test double that captures every IndexingProgress the pipeline reports (SYNTH-30).
    private sealed class ProgressCollector : IProgress<IndexingProgress>
    {
        public List<IndexingProgress> Reports { get; } = [];

        public void Report(IndexingProgress value) => Reports.Add(value);
    }

    [Fact]
    public async Task IndexDirectoryAsync_reports_progress_matching_the_final_counts()
    {
        // Foo: class + two methods => 3 chunks. Bar: class + one method => 2 chunks. Both are matching
        // *.cs files; Notes.txt is not enumerated (only *.cs), so TotalFiles must be 2, not 3.
        WriteFile("Foo.cs", """
            namespace Sample;

            public class Foo
            {
                public int A() => 1;
                public int B() => 2;
            }
            """);
        WriteFile("nested/Bar.cs", """
            namespace Sample.Nested;

            public class Bar
            {
                public void Run() { }
            }
            """);
        WriteFile("Notes.txt", "not a source file");

        var pipeline = PipelineFor(new LocalCodeChunkStore(), new FakeEmbeddingGenerator());
        var progress = new ProgressCollector();

        var summary = await pipeline.IndexDirectoryAsync(CollectionNames.Default, _root, progress: progress);

        Assert.NotEmpty(progress.Reports);

        // TotalFiles is the upfront count of matching *.cs files and is constant across reports.
        Assert.All(progress.Reports, report => Assert.Equal(2, report.TotalFiles));

        // The final report matches the actual file counts the run produced.
        var final = progress.Reports[^1];
        Assert.Equal(summary.FilesIndexed, final.FilesIndexed);
        Assert.Equal(summary.FilesSkipped, final.FilesSkipped);
        Assert.Equal(2, final.FilesIndexed);
        Assert.Equal(0, final.FilesSkipped);
    }

    [Fact]
    public async Task IndexDirectoryAsync_without_progress_behaves_unchanged()
    {
        WriteFile("Good.cs", """
            namespace Sample;
            public class Good { public void M() { } }
            """);

        // Omitting the optional progress sink must leave the existing contract intact.
        var pipeline = PipelineFor(new LocalCodeChunkStore(), new FakeEmbeddingGenerator());

        var summary = await pipeline.IndexDirectoryAsync(CollectionNames.Default, _root);

        Assert.Equal(1, summary.FilesIndexed);
        Assert.Equal(2, summary.ChunksIndexed);
    }

    [Fact]
    public async Task IndexDirectoryAsync_skips_unreadable_subdirectory_without_aborting()
    {
        // Regression test: EnumerateSourceFiles used to be a single lazy Directory.EnumerateFiles
        // walk with no exception guard, so one permission-denied subdirectory threw out of the
        // pipeline and failed the whole run instead of being skipped like any other unreadable path.
        if (OperatingSystem.IsWindows())
            return; // chmod-based restriction below doesn't apply; the fix is platform-agnostic.

        WriteFile("Readable.cs", "namespace S; public class Readable { public void M() { } }");
        var lockedDir = Path.Combine(_root, "locked");
        Directory.CreateDirectory(lockedDir);
        File.WriteAllText(Path.Combine(lockedDir, "Hidden.cs"), "namespace S; public class Hidden { public void M() { } }");
        File.SetUnixFileMode(lockedDir, UnixFileMode.None);

        try
        {
            var pipeline = PipelineFor(new LocalCodeChunkStore(), new FakeEmbeddingGenerator());

            var summary = await pipeline.IndexDirectoryAsync(CollectionNames.Default, _root);

            Assert.Equal(1, summary.FilesIndexed); // only Readable.cs; locked/Hidden.cs is unreachable.
        }
        finally
        {
            // Restore permissions so Dispose()'s recursive delete of _root can actually remove it.
            File.SetUnixFileMode(lockedDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task IndexDirectoryAsync_throws_when_root_missing()
    {
        var pipeline = PipelineFor(new LocalCodeChunkStore(), new FakeEmbeddingGenerator());

        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => pipeline.IndexDirectoryAsync(CollectionNames.Default, Path.Combine(_root, "does-not-exist")));
    }

    [Fact]
    public async Task IndexDirectoryAsync_populates_call_graph_across_files()
    {
        // Caller file: Service.Handle() calls a method declared in the other file.
        WriteFile("Service.cs", """
            namespace Sample;

            public class Service
            {
                private readonly Repository _repo = new();

                public void Handle()
                {
                    _repo.Load();
                }
            }
            """);

        // Callee file: Repository.Load lives in a separate file — resolution is collection-wide.
        WriteFile("Repository.cs", """
            namespace Sample;

            public class Repository
            {
                public void Load() { }
            }
            """);

        var graphStore = new FakeCodeGraphStore();
        var pipeline = PipelineFor(new LocalCodeChunkStore(), new FakeEmbeddingGenerator(), graphStore);

        await pipeline.IndexDirectoryAsync(CollectionNames.Default, _root);

        // The edge is resolved by simple name ("Load") across the two files.
        var callees = await graphStore.FindCalleesAsync(CollectionNames.Default, "Sample.Service.Handle");
        var edge = Assert.Single(callees);
        Assert.Equal("Sample.Repository.Load", edge.Callee);
        Assert.Equal("Service.cs", edge.SourceFile);

        var callers = await graphStore.FindCallersAsync(CollectionNames.Default, "Sample.Repository.Load");
        Assert.Equal("Sample.Service.Handle", Assert.Single(callers).Caller);
    }

    [Fact]
    public async Task IndexDirectoryAsync_leaves_empty_graph_when_no_calls_resolve()
    {
        // Handle() calls Missing(), which no file declares — no edge, no error.
        WriteFile("Lonely.cs", """
            namespace Sample;

            public class Lonely
            {
                public void Handle() { Missing(); }
            }
            """);

        var graphStore = new FakeCodeGraphStore();
        var pipeline = PipelineFor(new LocalCodeChunkStore(), new FakeEmbeddingGenerator(), graphStore);

        await pipeline.IndexDirectoryAsync(CollectionNames.Default, _root);

        Assert.Empty(await graphStore.FindCalleesAsync(CollectionNames.Default, "Sample.Lonely.Handle"));
    }

    [Fact]
    public async Task IndexDirectoryAsync_same_named_method_resolves_to_every_match()
    {
        // Two unrelated classes both declare Save(). Resolution is by simple name only, so a single
        // `Save()` call produces an edge to BOTH — this is the documented, accepted approximation
        // (issue #33), not a bug: no semantic model exists to disambiguate the receiver's type.
        WriteFile("Callers.cs", """
            namespace Sample;

            public class Caller
            {
                public void Run() { Save(); }
            }
            """);
        WriteFile("Targets.cs", """
            namespace Sample;

            public class RepoA { public void Save() { } }
            public class RepoB { public void Save() { } }
            """);

        var graphStore = new FakeCodeGraphStore();
        var pipeline = PipelineFor(new LocalCodeChunkStore(), new FakeEmbeddingGenerator(), graphStore);

        await pipeline.IndexDirectoryAsync(CollectionNames.Default, _root);

        var callees = await graphStore.FindCalleesAsync(CollectionNames.Default, "Sample.Caller.Run");
        Assert.Equal(2, callees.Count);
        Assert.Contains(callees, e => e.Callee == "Sample.RepoA.Save");
        Assert.Contains(callees, e => e.Callee == "Sample.RepoB.Save");
    }

    [Fact]
    public async Task IndexDirectoryAsync_re_index_of_unchanged_files_skips_the_embedding_generator()
    {
        WriteFile("Foo.cs", "namespace S; public class Foo { public void A() { } }");
        WriteFile("Bar.cs", "namespace S; public class Bar { public void B() { } }");

        var store = new LocalCodeChunkStore();
        var generator = new FakeEmbeddingGenerator();
        var pipeline = PipelineFor(store, generator);

        await pipeline.IndexDirectoryAsync(CollectionNames.Default, _root);
        var callsAfterFirstRun = generator.CallCount; // one embed call per indexed file.
        Assert.Equal(2, callsAfterFirstRun);

        // Nothing changed on disk: the second run must not embed either file again.
        var summary = await pipeline.IndexDirectoryAsync(CollectionNames.Default, _root);

        Assert.Equal(callsAfterFirstRun, generator.CallCount);
        Assert.Equal(0, summary.FilesIndexed);
        Assert.Equal(2, summary.FilesSkipped); // both unchanged files folded into the skipped counter.
    }

    [Fact]
    public async Task IndexDirectoryAsync_re_embeds_only_the_changed_file()
    {
        WriteFile("Changing.cs", "namespace S; public class Changing { public void A() { } }");
        WriteFile("Stable.cs", "namespace S; public class Stable { public void B() { } }");

        var store = new LocalCodeChunkStore();
        var generator = new FakeEmbeddingGenerator();
        var pipeline = PipelineFor(store, generator);

        await pipeline.IndexDirectoryAsync(CollectionNames.Default, _root);
        Assert.Equal(2, generator.CallCount);

        // Edit only Changing.cs; Stable.cs keeps identical content (same hash).
        WriteFile("Changing.cs", "namespace S; public class Changing { public void A() { return; } }");

        await pipeline.IndexDirectoryAsync(CollectionNames.Default, _root);

        // Exactly one more embed call — the changed file. The untouched sibling was skipped.
        Assert.Equal(3, generator.CallCount);
    }

    [Fact]
    public async Task IndexDirectoryAsync_deletes_chunks_of_files_removed_from_disk()
    {
        WriteFile("Keep.cs", "namespace S; public class Keep { public void A() { } }");
        WriteFile("Gone.cs", "namespace S; public class Gone { public void B() { } }");

        var store = new LocalCodeChunkStore();
        var pipeline = PipelineFor(store, new FakeEmbeddingGenerator());

        await pipeline.IndexDirectoryAsync(CollectionNames.Default, _root);
        Assert.NotEmpty(await store.GetByFileAsync(CollectionNames.Default, "Gone.cs"));

        // Remove Gone.cs from disk, then re-index: its stale chunks must be pruned from the store.
        File.Delete(Path.Combine(_root, "Gone.cs"));

        await pipeline.IndexDirectoryAsync(CollectionNames.Default, _root);

        Assert.Empty(await store.GetByFileAsync(CollectionNames.Default, "Gone.cs"));
        Assert.NotEmpty(await store.GetByFileAsync(CollectionNames.Default, "Keep.cs"));
    }

    [Fact]
    public async Task IndexDirectoryAsync_keeps_call_graph_correct_across_a_no_op_re_index()
    {
        // Same cross-file fixture as IndexDirectoryAsync_populates_call_graph_across_files, but indexed
        // twice with no file changes. Because unchanged files are still chunked (only embedding is
        // skipped), their call sites are re-extracted and the graph is rebuilt correctly every run —
        // this test would fail if the skip incorrectly dropped chunking for unchanged files.
        WriteFile("Service.cs", """
            namespace Sample;

            public class Service
            {
                private readonly Repository _repo = new();

                public void Handle()
                {
                    _repo.Load();
                }
            }
            """);
        WriteFile("Repository.cs", """
            namespace Sample;

            public class Repository
            {
                public void Load() { }
            }
            """);

        var graphStore = new FakeCodeGraphStore();
        var pipeline = PipelineFor(new LocalCodeChunkStore(), new FakeEmbeddingGenerator(), graphStore);

        await pipeline.IndexDirectoryAsync(CollectionNames.Default, _root);
        await pipeline.IndexDirectoryAsync(CollectionNames.Default, _root);

        // The edge survives the second, embedding-skipped run.
        var callees = await graphStore.FindCalleesAsync(CollectionNames.Default, "Sample.Service.Handle");
        var edge = Assert.Single(callees);
        Assert.Equal("Sample.Repository.Load", edge.Callee);

        var callers = await graphStore.FindCallersAsync(CollectionNames.Default, "Sample.Repository.Load");
        Assert.Equal("Sample.Service.Handle", Assert.Single(callers).Caller);
    }

    [Fact]
    public async Task IndexDirectoryAsync_re_index_replaces_stale_edges()
    {
        WriteFile("Pair.cs", """
            namespace Sample;

            public class Pair
            {
                public void A() { B(); }
                public void B() { }
            }
            """);

        var graphStore = new FakeCodeGraphStore();
        var pipeline = PipelineFor(new LocalCodeChunkStore(), new FakeEmbeddingGenerator(), graphStore);
        await pipeline.IndexDirectoryAsync(CollectionNames.Default, _root);
        Assert.Single(await graphStore.FindCalleesAsync(CollectionNames.Default, "Sample.Pair.A"));

        // Rewrite the file so A no longer calls B, then re-index: the old edge must be gone.
        WriteFile("Pair.cs", """
            namespace Sample;

            public class Pair
            {
                public void A() { }
                public void B() { }
            }
            """);
        await pipeline.IndexDirectoryAsync(CollectionNames.Default, _root);

        Assert.Empty(await graphStore.FindCalleesAsync(CollectionNames.Default, "Sample.Pair.A"));
    }

    [Fact]
    public async Task IndexDirectoryAsync_indexes_many_files_with_race_free_counts()
    {
        // SYNTH-44: with the per-file loop now parallelized, many files are chunked/embedded/upserted
        // concurrently. Enough files here that they genuinely overlap; every file is one class + one
        // method (2 chunks). A wrong total would betray a lost Interlocked increment (a sync bug).
        const int fileCount = 40;
        for (var i = 0; i < fileCount; i++)
        {
            // Multi-line so the class and method chunks get distinct line ranges (and thus distinct
            // ChunkIds — see CodeChunk.ChunkId), giving a clean 2 chunks per file.
            WriteFile($"File{i}.cs", $$"""
                namespace S;

                public class File{{i}}
                {
                    public void M{{i}}() { }
                }
                """);
        }

        var store = new LocalCodeChunkStore();
        var generator = new FakeEmbeddingGenerator();
        var pipeline = PipelineFor(store, generator);

        var summary = await pipeline.IndexDirectoryAsync(CollectionNames.Default, _root);

        Assert.Equal(fileCount, summary.FilesIndexed);
        Assert.Equal(0, summary.FilesSkipped);
        Assert.Equal(fileCount * 2, summary.ChunksIndexed); // class + method per file.
        Assert.Equal(fileCount, generator.CallCount);       // exactly one batched embed call per file.

        // Every file's chunks actually landed in the store — the concurrent upserts didn't drop any.
        for (var i = 0; i < fileCount; i++)
        {
            Assert.Equal(2, (await store.GetByFileAsync(CollectionNames.Default, $"File{i}.cs")).Count);
        }
    }

    [Fact]
    public async Task IndexDirectoryAsync_resolves_call_graph_across_many_files_concurrently()
    {
        // SYNTH-44: the call-graph accumulators (knownSymbols, rawCallSites) are written from every
        // concurrent file. With many caller/callee pairs indexed at once, a dropped symbol or call site
        // would leave an edge unresolved. Each Caller{i}.Run() calls Target{i}.Do{i}() in a sibling file.
        const int pairCount = 30;
        for (var i = 0; i < pairCount; i++)
        {
            WriteFile($"Caller{i}.cs", $$"""
                namespace Sample;

                public class Caller{{i}}
                {
                    private readonly Target{{i}} _t = new();
                    public void Run() { _t.Do{{i}}(); }
                }
                """);
            WriteFile($"Target{i}.cs", $$"""
                namespace Sample;

                public class Target{{i}}
                {
                    public void Do{{i}}() { }
                }
                """);
        }

        var graphStore = new FakeCodeGraphStore();
        var pipeline = PipelineFor(new LocalCodeChunkStore(), new FakeEmbeddingGenerator(), graphStore);

        await pipeline.IndexDirectoryAsync(CollectionNames.Default, _root);

        // Every one of the 30 cross-file edges resolved, despite the files being indexed concurrently.
        for (var i = 0; i < pairCount; i++)
        {
            var callees = await graphStore.FindCalleesAsync(CollectionNames.Default, $"Sample.Caller{i}.Run");
            var edge = Assert.Single(callees);
            Assert.Equal($"Sample.Target{i}.Do{i}", edge.Callee);
            Assert.Equal($"Caller{i}.cs", edge.SourceFile);
        }
    }

    [Fact]
    public async Task IndexDirectoryAsync_retries_a_transient_embed_failure_and_still_indexes_the_file()
    {
        WriteFile("Flaky.cs", "namespace S; public class Flaky { public void M() { } }");

        var store = new LocalCodeChunkStore();
        // Two transient network failures, then success on the third attempt: the retry must recover
        // and the file must end up indexed, not skipped (SYNTH-46).
        var generator = new FlakyEmbeddingGenerator(
            () => new HttpRequestException("ollama momentarily unreachable"), failuresBeforeSuccess: 2);
        var pipeline = new IndexingPipeline(
            [new CSharpRoslynChunker()], generator, store, new FakeCodeGraphStore());

        var summary = await pipeline.IndexDirectoryAsync(CollectionNames.Default, _root);

        Assert.Equal(1, summary.FilesIndexed);
        Assert.Equal(0, summary.FilesSkipped);
        Assert.Equal(3, generator.CallCount); // two failed attempts + the successful third.
        Assert.NotEmpty(await store.GetByFileAsync(CollectionNames.Default, "Flaky.cs"));
    }

    [Fact]
    public async Task IndexDirectoryAsync_counts_a_permanently_transient_file_as_skipped_without_aborting()
    {
        WriteFile("Doomed.cs", "namespace S; public class Doomed { public void M() { } }");

        var store = new LocalCodeChunkStore();
        // Every attempt times out (a TaskCanceledException NOT caused by the caller's own token, i.e. a
        // genuine transient timeout). After exhausting the retries the file is skipped and the run still
        // completes with a summary — it must not throw or abort the whole job.
        var generator = new FlakyEmbeddingGenerator(
            () => new TaskCanceledException("embed timed out"), failuresBeforeSuccess: -1);
        var pipeline = new IndexingPipeline(
            [new CSharpRoslynChunker()], generator, store, new FakeCodeGraphStore());

        var summary = await pipeline.IndexDirectoryAsync(CollectionNames.Default, _root);

        Assert.Equal(0, summary.FilesIndexed);
        Assert.Equal(1, summary.FilesSkipped);
        Assert.Equal(3, generator.CallCount); // MaxRetries attempts, all failing, then give up.
        Assert.Empty(await store.GetByFileAsync(CollectionNames.Default, "Doomed.cs"));
    }

    [Fact]
    public async Task IndexDirectoryAsync_does_not_retry_a_non_transient_failure_and_fails_fast()
    {
        WriteFile("Bug.cs", "namespace S; public class Bug { public void M() { } }");

        var store = new LocalCodeChunkStore();
        // A dimension mismatch is a real, permanent error — not a flaky hiccup. It must NOT be retried
        // (retrying would only fail identically) and must still propagate out to fail the job, exactly
        // as it did before per-file retry existed. Swallowing it into a silent skip would hide real bugs.
        var generator = new FlakyEmbeddingGenerator(
            () => new DimensionMismatchException(CollectionNames.Default, 8, 16), failuresBeforeSuccess: -1);
        var pipeline = new IndexingPipeline(
            [new CSharpRoslynChunker()], generator, store, new FakeCodeGraphStore());

        await Assert.ThrowsAsync<DimensionMismatchException>(
            () => pipeline.IndexDirectoryAsync(CollectionNames.Default, _root));

        Assert.Equal(1, generator.CallCount); // tried exactly once — no retry for a non-transient error.
    }
}
