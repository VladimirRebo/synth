using Microsoft.AspNetCore.Mvc;
using Synth.Domain.Graph;
using Synth.Domain;

namespace Synth.Api.Graph;

/// <summary>
/// Plain REST call-graph endpoints for the Vue client, mirroring the <c>search_code</c>/<c>GET
/// /search</c> pairing: the MCP tools <see cref="CallGraphTool"/> (served over <c>/mcp</c>) are for
/// AI-agent clients, browsers get bare <c>GET /callers?symbol=</c> / <c>GET /callees?symbol=</c>
/// instead of an MCP JSON-RPC handshake. Both sit on top of the same <see cref="ICodeGraphStore"/>,
/// so there's no duplicated lookup logic.
/// </summary>
/// <remarks>
/// Routes stay bare (no <c>/api</c> prefix, no class-level <c>[Route]</c>) — every action carries
/// its own absolute route, matching <c>GET /search</c>. Both are simple reads over
/// <see cref="ICodeGraphStore"/> with a one-line validation, so there's no Command/Query wrapper —
/// same judgment call as <c>SearchController</c>.
/// </remarks>
[ApiController]
public class CallGraphController : ControllerBase
{
    private readonly ICodeGraphStore _store;

    public CallGraphController(ICodeGraphStore store) => _store = store;

    /// <summary>
    /// Edges where <paramref name="symbol"/> is the callee — the callers of the symbol. Optional
    /// <paramref name="collection"/> scopes the query to one indexed repo, defaulting to the main
    /// codebase. 400 when <paramref name="symbol"/> is missing.
    /// </summary>
    [HttpGet("/callers")]
    public async Task<IActionResult> Callers(
        string? symbol,
        string? collection,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return BadRequest(new { error = "symbol is required" });

        var target = string.IsNullOrWhiteSpace(collection) ? CollectionNames.Default : collection;
        var edges = await _store.FindCallersAsync(target, symbol, cancellationToken);
        return Ok(edges);
    }

    /// <summary>
    /// Edges where <paramref name="symbol"/> is the caller — what the symbol calls. Optional
    /// <paramref name="collection"/> scopes the query to one indexed repo, defaulting to the main
    /// codebase. 400 when <paramref name="symbol"/> is missing.
    /// </summary>
    [HttpGet("/callees")]
    public async Task<IActionResult> Callees(
        string? symbol,
        string? collection,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return BadRequest(new { error = "symbol is required" });

        var target = string.IsNullOrWhiteSpace(collection) ? CollectionNames.Default : collection;
        var edges = await _store.FindCalleesAsync(target, symbol, cancellationToken);
        return Ok(edges);
    }
}
