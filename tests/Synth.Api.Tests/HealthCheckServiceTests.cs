using Microsoft.Extensions.AI;
using Synth.Api.Health;
using Synth.Domain.Embeddings;
using Synth.Infrastructure.Embeddings;

namespace Synth.Api.Tests;

// Unit-tests HealthCheckService directly against fake Qdrant and embedding dependencies (a
// reachable-vs-unreachable case each), following the project's "fake when no live service is
// configured" pattern — no live Qdrant/Ollama required.
public class HealthCheckServiceTests
{
    private static HealthCheckService Create(
        IQdrantHealthProbe qdrant,
        IEmbeddingGeneratorFactory embedding,
        TimeProvider? time = null) =>
        new(qdrant, embedding, new MutableOptionsMonitor<EmbeddingOptions>(new EmbeddingOptions()), time);

    [Fact]
    public async Task Both_dependencies_reachable_reports_healthy()
    {
        var service = Create(new FakeQdrantProbe(), FakeEmbeddingFactory.Succeeding());

        var report = await service.CheckAsync(CancellationToken.None);

        Assert.True(report.Healthy);
        Assert.Equal("ok", report.Status);
        Assert.True(report.Qdrant.Healthy);
        Assert.Null(report.Qdrant.Error);
        Assert.True(report.Embedding.Healthy);
        Assert.Null(report.Embedding.Error);
    }

    [Fact]
    public async Task Unreachable_qdrant_reports_only_qdrant_unhealthy()
    {
        var service = Create(
            new FakeQdrantProbe(() => throw new InvalidOperationException("connection refused")),
            FakeEmbeddingFactory.Succeeding());

        var report = await service.CheckAsync(CancellationToken.None);

        Assert.False(report.Healthy);
        Assert.Equal("degraded", report.Status);
        Assert.False(report.Qdrant.Healthy);
        Assert.Contains("connection refused", report.Qdrant.Error);
        // The other component is independent and still healthy.
        Assert.True(report.Embedding.Healthy);
    }

    [Fact]
    public async Task Unreachable_embedding_provider_reports_only_embedding_unhealthy()
    {
        var service = Create(new FakeQdrantProbe(), FakeEmbeddingFactory.Failing());

        var report = await service.CheckAsync(CancellationToken.None);

        Assert.False(report.Healthy);
        Assert.False(report.Embedding.Healthy);
        Assert.Contains("provider unreachable", report.Embedding.Error);
        Assert.True(report.Qdrant.Healthy);
    }

    [Fact]
    public async Task Embedding_provider_returning_an_empty_vector_is_unhealthy()
    {
        var service = Create(new FakeQdrantProbe(), FakeEmbeddingFactory.ReturningEmptyVector());

        var report = await service.CheckAsync(CancellationToken.None);

        Assert.False(report.Healthy);
        Assert.False(report.Embedding.Healthy);
        Assert.Contains("empty vector", report.Embedding.Error);
    }

    [Fact]
    public async Task Repeated_calls_within_the_cache_window_probe_only_once()
    {
        var qdrant = new FakeQdrantProbe();
        var embedding = FakeEmbeddingFactory.Succeeding();
        var time = new FakeTimeProvider();
        var service = Create(qdrant, embedding, time);

        await service.CheckAsync(CancellationToken.None);
        await service.CheckAsync(CancellationToken.None);
        await service.CheckAsync(CancellationToken.None);

        // Three polls in quick succession must not hammer the dependencies: the cached report is reused.
        Assert.Equal(1, qdrant.Calls);
        Assert.Equal(1, embedding.Calls);

        // Once the cache expires, the next poll re-probes.
        time.Advance(TimeSpan.FromSeconds(30));
        await service.CheckAsync(CancellationToken.None);

        Assert.Equal(2, qdrant.Calls);
        Assert.Equal(2, embedding.Calls);
    }

    private sealed class FakeQdrantProbe(Action? onCheck = null) : IQdrantHealthProbe
    {
        public int Calls { get; private set; }

        public Task CheckAsync(CancellationToken cancellationToken)
        {
            Calls++;
            onCheck?.Invoke();
            return Task.CompletedTask;
        }
    }

    // A deterministic stand-in for IEmbeddingGeneratorFactory whose probe behavior (succeed / throw /
    // empty vector) is fixed per test, so no real provider is contacted. Counts how many probes ran.
    private sealed class FakeEmbeddingFactory(Func<GeneratedEmbeddings<Embedding<float>>> probe)
        : IEmbeddingGeneratorFactory
    {
        public int Calls { get; private set; }

        public static FakeEmbeddingFactory Succeeding() =>
            new(() => new GeneratedEmbeddings<Embedding<float>>(
                [new Embedding<float>(new float[] { 0.1f, 0.2f, 0.3f })]));

        public static FakeEmbeddingFactory Failing() =>
            new(() => throw new InvalidOperationException("provider unreachable"));

        public static FakeEmbeddingFactory ReturningEmptyVector() =>
            new(() => new GeneratedEmbeddings<Embedding<float>>(
                [new Embedding<float>(ReadOnlyMemory<float>.Empty)]));

        public IEmbeddingGenerator<string, Embedding<float>> Create(EmbeddingOptions options)
        {
            Calls++;
            return new StubGenerator(probe);
        }

        private sealed class StubGenerator(Func<GeneratedEmbeddings<Embedding<float>>> probe)
            : IEmbeddingGenerator<string, Embedding<float>>
        {
            public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
                IEnumerable<string> values,
                EmbeddingGenerationOptions? options = null,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(probe());

            public object? GetService(Type serviceType, object? serviceKey = null) => null;

            public void Dispose()
            {
            }
        }
    }

    // A controllable clock so the cache-expiry path can be exercised without real waiting.
    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
