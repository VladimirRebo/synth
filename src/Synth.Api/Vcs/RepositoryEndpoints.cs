namespace Synth.Api.Vcs;

/// <summary>
/// Maps <c>GET /repositories</c>, exposing the <see cref="IRepositoryRegistry"/> so clients can
/// discover the known collections and their metadata (the Vue collection picker in SYNTH-20, and
/// callers that need a valid collection name to pass to search).
/// </summary>
public static class RepositoryEndpoints
{
    public static IEndpointRouteBuilder MapRepositoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/repositories", async (IRepositoryRegistry registry, CancellationToken cancellationToken) =>
            Results.Ok(await registry.ListAsync(cancellationToken)));

        return endpoints;
    }
}
