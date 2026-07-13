using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Synth.Application.Cqrs;

namespace Synth.Application.Embeddings;

/// <summary>
/// Handles <see cref="PullOllamaModelCommand"/>: validate the request, reserve the single pull slot, and
/// dispatch the detached background pull, returning immediately. Validation (empty model → error, no
/// endpoint configured → error) and the <see cref="IOllamaPullTracker.TryStart"/> reservation run
/// synchronously; the actual pull is dispatched fire-and-forget on a detached task so this returns right
/// away. Returns a <see cref="PullOllamaModelResult"/> the controller maps to its own 400/409/202 shape.
/// <para>
/// SYNTH-70 lifted this out of <c>OllamaModelEndpoints</c>'s <c>POST .../pull</c> handler essentially
/// unchanged so it lives behind the CQRS seam (issue #82) — mirroring
/// <see cref="Indexing.IndexRepositoryCommandHandler"/>'s background-job pattern exactly: the dependencies
/// it used to take as endpoint parameters are now constructor-injected, and the Ollama endpoint is resolved
/// through the <see cref="IOllamaEndpointResolver"/> port so Application never references Infrastructure.
/// The detached run uses <see cref="CancellationToken.None"/> deliberately (same reasoning as
/// <c>POST /index</c>): the request token is cancelled when the near-instant response completes, which would
/// otherwise kill the pull. No streaming/SSE on the wire — the client polls the status endpoint.
/// </para>
/// </summary>
public sealed class PullOllamaModelCommandHandler
    : ICommandHandler<PullOllamaModelCommand, PullOllamaModelResult>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOllamaEndpointResolver _endpointResolver;
    private readonly IOllamaPullTracker _tracker;
    private readonly ILogger _logger;

    public PullOllamaModelCommandHandler(
        IHttpClientFactory httpClientFactory,
        IOllamaEndpointResolver endpointResolver,
        IOllamaPullTracker tracker,
        ILoggerFactory loggerFactory)
    {
        _httpClientFactory = httpClientFactory;
        _endpointResolver = endpointResolver;
        _tracker = tracker;
        _logger = loggerFactory.CreateLogger(typeof(PullOllamaModelCommandHandler).FullName!);
    }

    public Task<PullOllamaModelResult> HandleAsync(
        PullOllamaModelCommand command, CancellationToken cancellationToken = default) =>
        Task.FromResult(Start(command));

    private PullOllamaModelResult Start(PullOllamaModelCommand command)
    {
        var model = command.Model?.Trim();
        if (string.IsNullOrWhiteSpace(model))
            return PullOllamaModelResult.ValidationError("Provide a 'model' to pull.");

        var endpoint = _endpointResolver.Resolve();
        if (string.IsNullOrWhiteSpace(endpoint))
            return PullOllamaModelResult.ValidationError("No Ollama endpoint is configured.");

        // Reserve the single pull slot; a pull already in progress is rejected without dispatching.
        if (!_tracker.TryStart(model))
            return PullOllamaModelResult.AlreadyRunning();

        // Detached background run: the request's CancellationToken is deliberately NOT used — it is
        // cancelled when the (near-instant) 202 response completes, which would kill the pull. Use
        // CancellationToken.None, same reasoning as POST /index's background dispatch.
        var client = _httpClientFactory.CreateClient();
        _ = Task.Run(async () =>
        {
            try
            {
                await PullAsync(client, endpoint!, model!, _tracker, CancellationToken.None);
                _tracker.Complete();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ollama pull of model {Model} failed.", model);
                _tracker.Fail(ex.Message);
            }
        });

        return PullOllamaModelResult.Started(model!);
    }

    // Streams Ollama's POST {endpoint}/api/pull (newline-delimited JSON, one object per line) and feeds
    // each parsed line to the tracker. Throws on a non-success response or an error line in the stream so
    // the caller records the pull as Failed.
    private static async Task PullAsync(
        HttpClient client, string endpoint, string model, IOllamaPullTracker tracker, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildOllamaUri(endpoint, "api/pull"))
        {
            Content = JsonContent.Create(new { name = model, stream = true }),
        };

        using var response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var status = ParseProgressLine(line);
            if (status is not null)
                tracker.ReportProgress(status);
        }
    }

    // Parses one pull-stream line into a human-readable status. Ollama reports a `status` string and,
    // during a layer download, `completed`/`total` byte counts — surfaced here as a rough percentage
    // (the tracker only needs a readable indicator, not a precise figure). An `error` field means the
    // pull failed (e.g. unknown model), so we throw. A line that isn't valid JSON is ignored.
    private static string? ParseProgressLine(string line)
    {
        OllamaPullProgress? progress;
        try
        {
            progress = JsonSerializer.Deserialize<OllamaPullProgress>(line, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (progress is null)
            return null;

        if (!string.IsNullOrWhiteSpace(progress.Error))
            throw new InvalidOperationException(progress.Error);

        if (progress.Total is > 0 && progress.Completed is >= 0)
        {
            var percent = (int)(100L * progress.Completed.Value / progress.Total.Value);
            return $"{progress.Status} ({percent}%)";
        }

        return progress.Status;
    }

    // Combines the (possibly slash-terminated) Ollama base endpoint with an API relative path.
    private static Uri BuildOllamaUri(string endpoint, string relativePath)
    {
        var baseUri = new Uri(endpoint.EndsWith('/') ? endpoint : endpoint + "/");
        return new Uri(baseUri, relativePath);
    }

    // One line of the /api/pull stream: a status plus optional byte counts, or an error when the pull fails.
    private sealed record OllamaPullProgress(
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("completed")] long? Completed,
        [property: JsonPropertyName("total")] long? Total,
        [property: JsonPropertyName("error")] string? Error);
}
