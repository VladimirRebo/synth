using Microsoft.Extensions.Options;
using Synth.Application.Embeddings;
using Synth.Application.Health;
using Synth.Domain.Embeddings;
using Synth.Infrastructure.Embeddings;

namespace Synth.Infrastructure.Health;

/// <summary>
/// Actually checks whether Qdrant and the configured embedding provider are reachable, instead of the
/// old always-<c>"ok"</c> stub. The embedding check mirrors <c>EmbeddingSettingsEndpoints.ProbeAsync</c>:
/// build a generator from the current <see cref="EmbeddingOptions"/> via <see cref="IEmbeddingGeneratorFactory"/>,
/// generate one embedding for a fixed probe string under a short timeout, and turn any exception / timeout /
/// empty result into a clear reason. The Qdrant check does a lightweight round trip via <see cref="IQdrantHealthProbe"/>.
/// <para>
/// The report is cached for a few seconds so a client polling <c>/health</c> doesn't hammer Ollama/Qdrant
/// on every call; the probe itself is serialized by a gate so concurrent pollers share one round trip.
/// </para>
/// </summary>
public sealed class HealthCheckService : IHealthCheckService
{
    // Fixed probe text (matches the embedding settings probe) and a short timeout so a hung/unreachable
    // dependency fails the check quickly rather than blocking the request.
    private const string ProbeText = "dimension probe";
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    // How long a computed report is reused before the next call re-probes. Short enough to stay fresh,
    // long enough that a tight polling loop doesn't turn into a probe-per-request storm.
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(5);

    private readonly IQdrantHealthProbe _qdrantProbe;
    private readonly IEmbeddingGeneratorFactory _embeddingFactory;
    private readonly IOptionsMonitor<EmbeddingOptions> _embeddingOptions;
    private readonly TimeProvider _timeProvider;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private HealthReport? _cached;
    private DateTimeOffset _cachedAt;

    public HealthCheckService(
        IQdrantHealthProbe qdrantProbe,
        IEmbeddingGeneratorFactory embeddingFactory,
        IOptionsMonitor<EmbeddingOptions> embeddingOptions,
        TimeProvider? timeProvider = null)
    {
        _qdrantProbe = qdrantProbe;
        _embeddingFactory = embeddingFactory;
        _embeddingOptions = embeddingOptions;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<HealthReport> CheckAsync(CancellationToken cancellationToken)
    {
        if (TryGetFresh(out var cached))
            return cached;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-check under the gate: a concurrent caller may have just refreshed the cache while we waited.
            if (TryGetFresh(out cached))
                return cached;

            var report = await ProbeAllAsync(cancellationToken).ConfigureAwait(false);
            _cached = report;
            _cachedAt = _timeProvider.GetUtcNow();
            return report;
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool TryGetFresh(out HealthReport report)
    {
        var cached = _cached;
        if (cached is not null && _timeProvider.GetUtcNow() - _cachedAt < CacheDuration)
        {
            report = cached;
            return true;
        }

        report = null!;
        return false;
    }

    private async Task<HealthReport> ProbeAllAsync(CancellationToken cancellationToken)
    {
        // Probe both dependencies concurrently — they're independent, so a slow one shouldn't serialize
        // behind the other (worst case is one ProbeTimeout, not two).
        var qdrant = ProbeQdrantAsync(cancellationToken);
        var embedding = ProbeEmbeddingAsync(cancellationToken);
        await Task.WhenAll(qdrant, embedding).ConfigureAwait(false);
        return HealthReport.From(qdrant.Result, embedding.Result);
    }

    private async Task<ComponentHealth> ProbeQdrantAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProbeTimeout);

            await _qdrantProbe.CheckAsync(timeout.Token).ConfigureAwait(false);
            return ComponentHealth.Ok;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ComponentHealth.Unhealthy($"the Qdrant probe timed out after {ProbeTimeout.TotalSeconds:0}s.");
        }
        catch (Exception ex)
        {
            return ComponentHealth.Unhealthy($"the Qdrant probe failed: {ex.Message}");
        }
    }

    private async Task<ComponentHealth> ProbeEmbeddingAsync(CancellationToken cancellationToken)
    {
        var generator = _embeddingFactory.Create(_embeddingOptions.CurrentValue);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProbeTimeout);

            var result = await generator.GenerateAsync([ProbeText], cancellationToken: timeout.Token)
                .ConfigureAwait(false);
            if (result.Count == 0 || result[0].Vector.Length == 0)
                return ComponentHealth.Unhealthy("the embedding provider returned an empty vector for the probe.");

            return ComponentHealth.Ok;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ComponentHealth.Unhealthy($"the embedding probe timed out after {ProbeTimeout.TotalSeconds:0}s.");
        }
        catch (Exception ex)
        {
            return ComponentHealth.Unhealthy($"the embedding probe failed: {ex.Message}");
        }
        finally
        {
            generator.Dispose();
        }
    }
}
