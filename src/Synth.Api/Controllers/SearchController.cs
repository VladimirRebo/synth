using Microsoft.AspNetCore.Mvc;
using Synth.Api.Mcp;
using Synth.Application;
using Synth.Domain.Vcs;
using Synth.Domain;

namespace Synth.Api.Search;

/// <summary>
/// Plain REST search endpoints for the Vue client. The MCP tool <see cref="CodeSearchTool"/>
/// (served over <c>/mcp</c>) exists for AI-agent clients; browsers get a simple
/// <c>GET /search?q=...&amp;limit=...</c> instead of an MCP JSON-RPC handshake. Both sit on top
/// of the same <see cref="CodeSearchService"/> and share the same <see cref="CodeSearchResult"/>
/// projection, so there's no duplicated search logic.
/// </summary>
/// <remarks>
/// Routes stay bare (no <c>/api</c> prefix, no class-level <c>[Route]</c>) — every action carries
/// its own absolute route, and the client's Vite proxy strips <c>/api</c>.
/// </remarks>
[ApiController]
public class SearchController : ControllerBase
{
    /// <summary>
    /// Full-text-ish code search over one collection, or every known collection when
    /// <c>?collection=*</c> is passed (SYNTH-48). Delegates straight to
    /// <see cref="CodeSearchService"/> — no business logic worth wrapping in a Query type here.
    /// 400 when <paramref name="q"/> is missing.
    /// </summary>
    [HttpGet("/search")]
    public async Task<IActionResult> Search(
        string? q,
        int? limit,
        string? collection,
        [FromServices] CodeSearchService searchService,
        [FromServices] IRepositoryRegistry registry,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(q))
            return BadRequest(new { error = "q is required" });

        IReadOnlyList<ScoredCodeChunk> chunks;
        if (collection == CollectionNames.All)
        {
            // ?collection=* fans out over every known collection (SYNTH-48): merge into one ranked
            // list, each result tagged with the collection it came from. The query is embedded once
            // inside SearchAllCollectionsAsync and reused across every collection's store search.
            var collections = (await registry.ListAsync(cancellationToken)).Select(r => r.Collection).ToList();
            chunks = await searchService.SearchAllCollectionsAsync(collections, q, limit ?? 10, cancellationToken);
        }
        else
        {
            // Optional ?collection= scopes the search to one indexed repo; defaults to the main
            // codebase so existing callers are unaffected. Omitting it still means "the default
            // collection" — the all-collections mode is opt-in via the explicit '*' sentinel.
            var target = string.IsNullOrWhiteSpace(collection) ? CollectionNames.Default : collection;
            chunks = await searchService.SearchAsync(target, q, limit ?? 10, cancellationToken);
        }

        return Ok(chunks.Select(CodeSearchResult.From).ToList());
    }

    /// <summary>
    /// Browse a single file's chunks: everything the store holds for one relative path in one
    /// collection, in ascending line order, each projected to include its assembled
    /// EmbeddingText — the point of this endpoint is seeing exactly what was embedded for a file
    /// (chunk boundaries, types, the embedding input), not just re-reading the source. Reuses
    /// the existing <see cref="ICodeChunkStore.GetByFileAsync"/>; no new store logic.
    /// <c>{*relativePath}</c> is a catch-all so nested paths (src/Foo/Bar.cs) arrive intact rather
    /// than being cut at the first '/'. 404 when the file has no chunks (unindexed, unknown
    /// collection, or a genuinely empty file). Bare route (no /api prefix) — the client's Vite
    /// proxy strips /api.
    /// </summary>
    [HttpGet("/repositories/{collection}/files/{*relativePath}")]
    public async Task<IActionResult> BrowseFile(
        string collection,
        string relativePath,
        [FromServices] ICodeChunkStore chunkStore,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return BadRequest(new { error = "relativePath is required" });

        var chunks = await chunkStore.GetByFileAsync(collection, relativePath, cancellationToken);
        if (chunks.Count == 0)
            return NotFound();

        return Ok(chunks.Select(FileChunkResult.From).ToList());
    }
}
