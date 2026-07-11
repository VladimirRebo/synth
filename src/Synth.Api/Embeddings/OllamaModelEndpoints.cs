using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Synth.Domain.Embeddings;

namespace Synth.Api.Embeddings;

/// <summary>
/// Maps the Ollama model-picker endpoints under <c>/settings/embedding/ollama/*</c> (bare routes, no
/// <c>/api</c> prefix, like every other endpoint in this app). These let Settings list the models
/// available on the live Ollama instance and trigger a pull for a new one — replacing the old free-text
/// model field (issue #59, SYNTH-50). The target Ollama server is whatever the live embedding generator
/// resolves to (<see cref="ConfigurableEmbeddingGenerator.ResolveOllamaEndpoint"/>), so the picker always
/// talks to the same instance embeddings actually use.
/// <para>
/// A pull is long-running, so it follows this project's fire-and-forget + polling convention
/// (<c>IIndexJobTracker</c>/<c>POST /index</c>), <b>not</b> streaming/SSE on the wire to the client: the
/// pull endpoint kicks off a detached background task that consumes Ollama's own newline-delimited-JSON
/// pull stream internally and updates <see cref="IOllamaPullTracker"/>, and the client polls the status
/// endpoint — mirroring the indexing job pattern.
/// </para>
/// </summary>
public static class OllamaModelEndpoints
{
    // Short timeout for the models-list proxy so an unreachable/hung Ollama fails fast. The pull itself
    // is deliberately NOT bounded this way — it runs detached and can legitimately take minutes.
    private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapOllamaModelEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // GET /settings/embedding/ollama/models — proxies Ollama's GET {endpoint}/api/tags and returns
        // just the locally-available model names.
        endpoints.MapGet("/settings/embedding/ollama/models", async (
            IHttpClientFactory httpClientFactory,
            IOptionsMonitor<EmbeddingOptions> options,
            OllamaConnection aspireDefault,
            CancellationToken cancellationToken) =>
        {
            var endpoint = ConfigurableEmbeddingGenerator.ResolveOllamaEndpoint(options.CurrentValue, aspireDefault);
            if (string.IsNullOrWhiteSpace(endpoint))
                return Results.BadRequest(new { error = "No Ollama endpoint is configured." });

            var client = httpClientFactory.CreateClient();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(ListTimeout);

                var tags = await client.GetFromJsonAsync<OllamaTagsResponse>(
                    BuildOllamaUri(endpoint, "api/tags"), JsonOptions, timeout.Token);

                var models = tags?.Models?
                    .Select(m => m.Name)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .ToList() ?? [];
                return Results.Ok(models);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Results.Json(
                    new { error = $"listing Ollama models timed out after {ListTimeout.TotalSeconds:0}s." },
                    statusCode: StatusCodes.Status502BadGateway);
            }
            catch (Exception ex)
            {
                return Results.Json(
                    new { error = $"could not reach Ollama: {ex.Message}" },
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // POST /settings/embedding/ollama/pull { "model": "..." } — reserves the single pull slot (409 if
        // one's already running) and dispatches a detached background pull, returning 202 immediately.
        endpoints.MapPost("/settings/embedding/ollama/pull", (
            OllamaPullRequest request,
            IHttpClientFactory httpClientFactory,
            IOptionsMonitor<EmbeddingOptions> options,
            OllamaConnection aspireDefault,
            IOllamaPullTracker tracker,
            ILoggerFactory loggerFactory) =>
        {
            var model = request.Model?.Trim();
            if (string.IsNullOrWhiteSpace(model))
                return Results.BadRequest(new { error = "Provide a 'model' to pull." });

            var endpoint = ConfigurableEmbeddingGenerator.ResolveOllamaEndpoint(options.CurrentValue, aspireDefault);
            if (string.IsNullOrWhiteSpace(endpoint))
                return Results.BadRequest(new { error = "No Ollama endpoint is configured." });

            // Reserve the single pull slot; a pull already in progress is rejected without dispatching.
            if (!tracker.TryStart(model))
                return Results.Conflict(new { error = "A model pull is already running." });

            // Detached background run: the request's CancellationToken is deliberately NOT used — it is
            // cancelled when the (near-instant) 202 response completes, which would kill the pull. Use
            // CancellationToken.None, same reasoning as POST /index's background dispatch.
            var logger = loggerFactory.CreateLogger(typeof(OllamaModelEndpoints).FullName!);
            var client = httpClientFactory.CreateClient();
            _ = Task.Run(async () =>
            {
                try
                {
                    await PullAsync(client, endpoint!, model!, tracker, CancellationToken.None);
                    tracker.Complete();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Ollama pull of model {Model} failed.", model);
                    tracker.Fail(ex.Message);
                }
            });

            return Results.Accepted(value: new { model, status = "started" });
        });

        // GET /settings/embedding/ollama/pull/status — the poll target for the fire-and-forget pull above.
        endpoints.MapGet("/settings/embedding/ollama/pull/status",
            (IOllamaPullTracker tracker) => Results.Ok(tracker.Current));

        return endpoints;
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

    // GET /api/tags response: { "models": [ { "name": "llama3:latest", ... }, ... ] }.
    private sealed record OllamaTagsResponse(
        [property: JsonPropertyName("models")] List<OllamaTag>? Models);

    private sealed record OllamaTag(
        [property: JsonPropertyName("name")] string? Name);

    // One line of the /api/pull stream: a status plus optional byte counts, or an error when the pull fails.
    private sealed record OllamaPullProgress(
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("completed")] long? Completed,
        [property: JsonPropertyName("total")] long? Total,
        [property: JsonPropertyName("error")] string? Error);
}

/// <summary>Request body for <c>POST /settings/embedding/ollama/pull</c>.</summary>
public sealed record OllamaPullRequest(string? Model);
