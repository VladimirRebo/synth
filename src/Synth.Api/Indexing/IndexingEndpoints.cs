using Synth.Api.Vcs;
using Synth.Core;
using Synth.Core.Vcs;

namespace Synth.Api.Indexing;

/// <summary>
/// Request body for <c>POST /index</c>. Exactly one of <see cref="Path"/> (a local directory,
/// indexed into <see cref="CollectionNames.Default"/>) or <see cref="RepoUrl"/> (a remote git
/// URL, cloned/fetched and indexed into its own per-repo collection) must be supplied.
/// <see cref="Branch"/> only applies to the <see cref="RepoUrl"/> case.
/// </summary>
public sealed record IndexRequest(string? Path = null, string? RepoUrl = null, string? Branch = null);

/// <summary>
/// Maps the manual indexing trigger. Before this endpoint existed, <see cref="IndexingPipeline"/>
/// was only ever invoked from tests — there was no way to actually index a real directory against
/// the running app. Intentionally minimal: no auth, no background job, just a direct call for
/// local/manual use. SYNTH-19 added the remote-repo branch and registry bookkeeping.
/// </summary>
public static class IndexingEndpoints
{
    public static IEndpointRouteBuilder MapIndexingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/index", async (
            IndexRequest request,
            IndexingPipeline pipeline,
            GitRepoService gitRepoService,
            IRepositoryRegistry registry,
            CancellationToken cancellationToken) =>
        {
            var hasPath = !string.IsNullOrWhiteSpace(request.Path);
            var hasRepoUrl = !string.IsNullOrWhiteSpace(request.RepoUrl);
            if (hasPath == hasRepoUrl)
                return Results.BadRequest(new { error = "Provide exactly one of 'path' or 'repoUrl'." });

            string collection;
            string indexRoot;
            RepositoryEntry entry;

            if (hasPath)
            {
                if (!Directory.Exists(request.Path))
                    return Results.BadRequest(new { error = $"Directory not found: {request.Path}" });

                // Local-path indexing lands in the default collection, unchanged from before SYNTH-19.
                collection = CollectionNames.Default;
                indexRoot = request.Path!;
                entry = new RepositoryEntry
                {
                    Collection = collection,
                    SourceType = "local",
                    Source = request.Path!,
                    Branch = null,
                    LastIndexedAt = DateTime.UtcNow,
                    ChunkCount = 0,
                };
            }
            else
            {
                RepoUrlInfo info;
                try
                {
                    info = RepoUrlInfo.Parse(request.RepoUrl!);
                }
                catch (FormatException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }

                try
                {
                    indexRoot = await gitRepoService.EnsureRepoAsync(request.RepoUrl!, request.Branch, cancellationToken);
                }
                catch (GitCommandException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }

                // Per-repo collection derived from the URL (SYNTH-18); the same URL always maps here.
                collection = info.Slug;
                entry = new RepositoryEntry
                {
                    Collection = collection,
                    SourceType = SourceTypeFor(info.Provider),
                    Source = request.RepoUrl!,
                    Branch = string.IsNullOrWhiteSpace(request.Branch) ? null : request.Branch,
                    LastIndexedAt = DateTime.UtcNow,
                    ChunkCount = 0,
                };
            }

            var summary = await pipeline.IndexDirectoryAsync(collection, indexRoot, cancellationToken);

            await registry.UpsertAsync(entry with { ChunkCount = summary.ChunksIndexed }, cancellationToken);

            return Results.Ok(summary);
        });

        return endpoints;
    }

    private static string SourceTypeFor(GitProvider provider) => provider switch
    {
        GitProvider.GitHub => "github",
        GitProvider.GitLab => "gitlab",
        _ => "other",
    };
}
