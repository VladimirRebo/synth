using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Synth.Core;
using Synth.Core.Graph;

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

        public int CallCount { get; private set; }

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
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
}
