using Synth.Application.Cqrs;
using Synth.Domain;
using Synth.Domain.Graph;
using Synth.Domain.Vcs;

namespace Synth.Application.Vcs;

/// <summary>
/// Handles <see cref="DeleteCollectionCommand"/>: removes an indexed collection completely — its
/// vector-store collection, its call-graph edges (a full clear via
/// <see cref="ICodeGraphStore.ReplaceEdgesAsync"/> with an empty list), and its registry entry — and,
/// for a cloned remote (github/gitlab), the on-disk checkout under the workspace root. Reports whether
/// the registry actually held an entry to remove (<c>true</c> -> 204, <c>false</c> -> 404 at the REST
/// layer). The store and graph cleanup run even when the registry has no entry (the two could drift),
/// so a stale collection is still fully purged.
///
/// SYNTH-67 lifted this out of <c>RepositoryEndpoints.DeleteCollectionAsync</c> unchanged so it lives
/// behind the CQRS seam (issue #82), following the exact pattern <c>IndexRepositoryCommandHandler</c>
/// established: the dependencies it used to take as method parameters are now constructor-injected,
/// and it depends on the <see cref="IGitRepoService"/> port rather than the concrete
/// <c>GitRepoService</c> so Application never references Infrastructure.
/// </summary>
public sealed class DeleteCollectionCommandHandler
    : ICommandHandler<DeleteCollectionCommand, bool>
{
    private readonly ICodeChunkStore _chunkStore;
    private readonly ICodeGraphStore _graphStore;
    private readonly IRepositoryRegistry _registry;
    private readonly IGitRepoService _gitRepoService;

    public DeleteCollectionCommandHandler(
        ICodeChunkStore chunkStore,
        ICodeGraphStore graphStore,
        IRepositoryRegistry registry,
        IGitRepoService gitRepoService)
    {
        _chunkStore = chunkStore;
        _graphStore = graphStore;
        _registry = registry;
        _gitRepoService = gitRepoService;
    }

    public async Task<bool> HandleAsync(
        DeleteCollectionCommand command, CancellationToken cancellationToken = default)
    {
        var collection = command.Collection;

        // Read the entry before the delete sequence removes it, so we know its SourceType: only a
        // cloned remote (github/gitlab) has an on-disk checkout under the workspace root to remove;
        // a local source was indexed in place and never cloned.
        var entries = await _registry.ListAsync(cancellationToken);
        var entry = entries.FirstOrDefault(e =>
            string.Equals(e.Collection, collection, StringComparison.Ordinal));

        await _chunkStore.DeleteCollectionAsync(collection, cancellationToken);
        await _graphStore.ReplaceEdgesAsync(collection, [], cancellationToken);
        var removed = await _registry.DeleteAsync(collection, cancellationToken);

        if (removed && entry is not null && IsClonedRemote(entry.SourceType))
            _gitRepoService.RemoveCheckout(collection);

        return removed;
    }

    /// <summary>
    /// True for source types cloned into the workspace root (<c>github</c>/<c>gitlab</c>), i.e. those
    /// with an on-disk checkout to clean up. A <c>local</c> source is indexed in place and has none.
    /// </summary>
    private static bool IsClonedRemote(string sourceType) =>
        string.Equals(sourceType, "github", StringComparison.Ordinal) ||
        string.Equals(sourceType, "gitlab", StringComparison.Ordinal);
}
