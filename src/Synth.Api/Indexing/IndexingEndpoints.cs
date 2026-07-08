using Synth.Core;

namespace Synth.Api.Indexing;

/// <summary>
/// Request body for <c>POST /index</c>: the absolute path of the directory to index.
/// </summary>
public sealed record IndexRequest(string Path);

/// <summary>
/// Maps the manual indexing trigger. Before this endpoint existed, <see cref="IndexingPipeline"/>
/// was only ever invoked from tests — there was no way to actually index a real directory against
/// the running app. Intentionally minimal: no auth, no background job, just a direct call for
/// local/manual use.
/// </summary>
public static class IndexingEndpoints
{
    public static IEndpointRouteBuilder MapIndexingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/index", async (IndexRequest request, IndexingPipeline pipeline, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Path) || !Directory.Exists(request.Path))
                return Results.BadRequest(new { error = $"Directory not found: {request.Path}" });

            // Local-path indexing lands in the default collection; per-repo collections arrive in SYNTH-19.
            var summary = await pipeline.IndexDirectoryAsync(CollectionNames.Default, request.Path, cancellationToken);
            return Results.Ok(summary);
        });

        return endpoints;
    }
}
