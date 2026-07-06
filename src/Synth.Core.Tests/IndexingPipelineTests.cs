using Microsoft.Extensions.AI;
using Synth.Core;

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

    private static IndexingPipeline PipelineFor(ICodeChunkStore store, FakeEmbeddingGenerator generator) =>
        new([new CSharpRoslynChunker()], generator, store);

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

        var summary = await pipeline.IndexDirectoryAsync(_root);

        Assert.Equal(2, summary.FilesIndexed);
        Assert.Equal(0, summary.FilesSkipped);
        Assert.Equal(5, summary.ChunksIndexed);

        var fooChunks = await store.GetByFileAsync("Foo.cs");
        Assert.Equal(3, fooChunks.Count);

        var barChunks = await store.GetByFileAsync("nested/Bar.cs");
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

        var summary = await pipeline.IndexDirectoryAsync(_root);

        // Only Real.cs is indexed; the build-output/VCS files are never enumerated.
        Assert.Equal(1, summary.FilesIndexed);
        Assert.Empty(await store.GetByFileAsync("bin/Generated.cs"));
        Assert.Empty(await store.GetByFileAsync("obj/Generated.cs"));
        Assert.Empty(await store.GetByFileAsync(".git/Hook.cs"));
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

        var summary = await pipeline.IndexDirectoryAsync(_root);

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

        await pipeline.IndexDirectoryAsync(_root);

        Assert.Equal(2, generator.CallCount);
    }

    [Fact]
    public async Task IndexDirectoryAsync_throws_when_root_missing()
    {
        var pipeline = PipelineFor(new LocalCodeChunkStore(), new FakeEmbeddingGenerator());

        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => pipeline.IndexDirectoryAsync(Path.Combine(_root, "does-not-exist")));
    }
}
