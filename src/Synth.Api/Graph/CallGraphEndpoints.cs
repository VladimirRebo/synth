using Synth.Core;
using Synth.Core.Graph;

namespace Synth.Api.Graph;

/// <summary>
/// Plain REST call-graph endpoints for the Vue client, mirroring the <c>search_code</c>/<c>GET
/// /search</c> pairing: the MCP tools <see cref="CallGraphTool"/> (served over <c>/mcp</c>) are for
/// AI-agent clients, browsers get bare <c>GET /callers?symbol=</c> / <c>GET /callees?symbol=</c>
/// instead of an MCP JSON-RPC handshake. Both sit on top of the same <see cref="ICodeGraphStore"/>,
/// so there's no duplicated lookup logic. Routes are mapped bare (no <c>/api</c> prefix), matching
/// <c>GET /search</c>.
/// </summary>
public static class CallGraphEndpoints
{
    public static IEndpointRouteBuilder MapCallGraphEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/callers", async (string? symbol, string? collection, ICodeGraphStore store, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(symbol))
                return Results.BadRequest(new { error = "symbol is required" });

            var target = string.IsNullOrWhiteSpace(collection) ? CollectionNames.Default : collection;
            var edges = await store.FindCallersAsync(target, symbol, cancellationToken);
            return Results.Ok(edges);
        });

        endpoints.MapGet("/callees", async (string? symbol, string? collection, ICodeGraphStore store, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(symbol))
                return Results.BadRequest(new { error = "symbol is required" });

            var target = string.IsNullOrWhiteSpace(collection) ? CollectionNames.Default : collection;
            var edges = await store.FindCalleesAsync(target, symbol, cancellationToken);
            return Results.Ok(edges);
        });

        return endpoints;
    }
}
