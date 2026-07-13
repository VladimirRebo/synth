using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Synth.Application.Cqrs;
using Synth.Application.Embeddings;
using Synth.Domain.Embeddings;

namespace Synth.Api.Controllers;

/// <summary>
/// The <c>Embedding</c> settings endpoints under <c>/settings/embedding/*</c>: <c>GET /settings/embedding</c>
/// (read the effective <see cref="EmbeddingOptions"/>, masking the OpenAI API key to a set/not-set flag
/// rather than echoing the secret), <c>PUT /settings/embedding</c> (partial write), and the Ollama
/// model-picker actions (list locally-available models, pull a new one with polled progress). The reads and
/// the model-list proxy stay thin actions over <see cref="IOptionsMonitor{TOptions}"/>/
/// <see cref="IHttpClientFactory"/> — no Query wrapper, same judgment call as
/// <see cref="VcsSettingsController"/>'s reads — while the two pieces with real orchestration
/// live behind the CQRS seam: the write's probe-before-persist logic in
/// <see cref="UpdateEmbeddingSettingsCommandHandler"/> (SYNTH-69) and the pull's fire-and-forget dispatch in
/// <see cref="PullOllamaModelCommandHandler"/> (SYNTH-70), both under issue #82. A candidate embedding
/// config is probed before it is saved; a broken provider is rejected with <c>400</c> and nothing is
/// persisted. SYNTH-70 merged the former Minimal-API <c>OllamaModelEndpoints</c> into this controller
/// (issue #82, slice 15). No auth/RBAC — Synth is a single local user.
/// </summary>
/// <remarks>
/// Routes stay bare (no <c>/api</c> prefix, no class-level <c>[Route]</c>) — each action carries its own
/// absolute route, and the client's Vite proxy strips <c>/api</c>. A pull follows this project's
/// fire-and-forget + polling convention (<c>IIndexJobTracker</c>/<c>POST /index</c>), <b>not</b>
/// streaming/SSE on the wire: <c>POST .../pull</c> kicks off a detached background pull that consumes
/// Ollama's own newline-delimited-JSON stream internally and updates <see cref="IOllamaPullTracker"/>, and
/// the client polls <c>GET .../pull/status</c>.
/// </remarks>
[ApiController]
public class EmbeddingSettingsController : ControllerBase
{
    // Short timeout for the models-list proxy so an unreachable/hung Ollama fails fast. The pull itself is
    // deliberately NOT bounded this way — it runs detached (in PullOllamaModelCommandHandler) and can
    // legitimately take minutes.
    private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ICommandHandler<UpdateEmbeddingSettingsCommand, UpdateEmbeddingSettingsResult> _updateHandler;
    private readonly ICommandHandler<PullOllamaModelCommand, PullOllamaModelResult> _pullHandler;

    public EmbeddingSettingsController(
        ICommandHandler<UpdateEmbeddingSettingsCommand, UpdateEmbeddingSettingsResult> updateHandler,
        ICommandHandler<PullOllamaModelCommand, PullOllamaModelResult> pullHandler)
    {
        _updateHandler = updateHandler;
        _pullHandler = pullHandler;
    }

    /// <summary>
    /// Current effective <see cref="EmbeddingOptions"/>, with the OpenAI key masked to a set/not-set
    /// flag. Reads <see cref="IOptionsMonitor{TOptions}"/> directly — a plain projection, no command
    /// dispatch.
    /// </summary>
    [HttpGet("/settings/embedding")]
    public IActionResult Get([FromServices] IOptionsMonitor<EmbeddingOptions> options)
        => Ok(EmbeddingSettingsResponse.Mask(options.CurrentValue));

    /// <summary>
    /// Partial update; returns the same masked shape as <see cref="Get"/>. Dispatches an
    /// <see cref="UpdateEmbeddingSettingsCommand"/> carrying the raw body (the probe/partial-update logic
    /// needs the three-way absent/null/string distinction a typed DTO would lose). A validation failure —
    /// a non-object body or a candidate config that failed its embedding probe — maps to <c>400</c> with
    /// the same <c>{ error }</c> shape as before, and nothing is persisted; success returns <c>200</c>
    /// with the masked result.
    /// </summary>
    [HttpPut("/settings/embedding")]
    public async Task<IActionResult> Update([FromBody] JsonElement body, CancellationToken cancellationToken)
    {
        var result = await _updateHandler.HandleAsync(new UpdateEmbeddingSettingsCommand(body), cancellationToken);
        return result.Success
            ? Ok(result.Response)
            : BadRequest(new { error = result.Error });
    }

    /// <summary>
    /// Proxies Ollama's <c>GET {endpoint}/api/tags</c> and returns just the locally-available model names,
    /// so Settings can offer a picker instead of a free-text model field (SYNTH-50). The endpoint is
    /// resolved through <see cref="IOllamaEndpointResolver"/> — the same server embeddings actually use. A
    /// thin action: the only logic here is the short-timeout proxy and mapping an unreachable Ollama to
    /// <c>502</c>.
    /// </summary>
    [HttpGet("/settings/embedding/ollama/models")]
    public async Task<IActionResult> GetOllamaModels(
        [FromServices] IHttpClientFactory httpClientFactory,
        [FromServices] IOllamaEndpointResolver endpointResolver,
        CancellationToken cancellationToken)
    {
        var endpoint = endpointResolver.Resolve();
        if (string.IsNullOrWhiteSpace(endpoint))
            return BadRequest(new { error = "No Ollama endpoint is configured." });

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
            return Ok(models);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return StatusCode(
                StatusCodes.Status502BadGateway,
                new { error = $"listing Ollama models timed out after {ListTimeout.TotalSeconds:0}s." });
        }
        catch (Exception ex)
        {
            return StatusCode(
                StatusCodes.Status502BadGateway,
                new { error = $"could not reach Ollama: {ex.Message}" });
        }
    }

    /// <summary>
    /// Reserves the single pull slot (<c>409</c> if one's already running) and dispatches a detached
    /// background pull, returning <c>202</c> immediately. All the orchestration lives in
    /// <see cref="PullOllamaModelCommandHandler"/>; this action is just the <c>400</c>/<c>409</c>/<c>202</c>
    /// mapping of its result — identical to the old <c>OllamaModelEndpoints</c> behavior.
    /// </summary>
    [HttpPost("/settings/embedding/ollama/pull")]
    public async Task<IActionResult> PullOllamaModel(
        [FromBody] OllamaPullRequest request, CancellationToken cancellationToken)
    {
        var result = await _pullHandler.HandleAsync(new PullOllamaModelCommand(request.Model), cancellationToken);
        return result.Status switch
        {
            PullOllamaModelResult.Kind.Started => Accepted(value: new { model = result.Model, status = "started" }),
            PullOllamaModelResult.Kind.AlreadyRunning => Conflict(new { error = result.Error }),
            _ => BadRequest(new { error = result.Error }),
        };
    }

    /// <summary>
    /// The poll target for the fire-and-forget pull above — the current/most-recent
    /// <see cref="IOllamaPullTracker.Current"/> snapshot.
    /// </summary>
    [HttpGet("/settings/embedding/ollama/pull/status")]
    public IActionResult GetOllamaPullStatus([FromServices] IOllamaPullTracker tracker)
        => Ok(tracker.Current);

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
}
