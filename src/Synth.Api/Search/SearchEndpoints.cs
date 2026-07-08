using Synth.Api.Mcp;
using Synth.Core;

namespace Synth.Api.Search;

/// <summary>
/// Plain REST search endpoint for the Vue client. The MCP tool <see cref="CodeSearchTool"/>
/// (served over <c>/mcp</c>) exists for AI-agent clients; browsers get a simple
/// <c>GET /search?q=...&amp;limit=...</c> instead of an MCP JSON-RPC handshake. Both sit on top
/// of the same <see cref="CodeSearchService"/> and share the same <see cref="CodeSearchResult"/>
/// projection, so there's no duplicated search logic.
/// </summary>
public static class SearchEndpoints
{
    public static IEndpointRouteBuilder MapSearchEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/search", async (string? q, int? limit, string? collection, CodeSearchService searchService, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(q))
                return Results.BadRequest(new { error = "q is required" });

            // Optional ?collection= scopes the search to one indexed repo; defaults to the main
            // codebase so existing callers are unaffected (ready for SYNTH-20's collection picker).
            var target = string.IsNullOrWhiteSpace(collection) ? CollectionNames.Default : collection;
            var chunks = await searchService.SearchAsync(target, q, limit ?? 10, cancellationToken);
            return Results.Ok(chunks.Select(CodeSearchResult.From).ToList());
        });

        return endpoints;
    }
}
