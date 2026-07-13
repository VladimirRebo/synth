using Synth.Application.Cqrs;

namespace Synth.Application.Vcs;

/// <summary>
/// Command to remove an indexed collection completely — the input to
/// <see cref="DeleteCollectionCommandHandler"/>, shared by <c>DELETE /repositories/{collection}</c>
/// and the <c>delete_collection</c> MCP tool. Its <see cref="bool"/> result mirrors the REST
/// endpoint's 204/404 split: <c>true</c> when the registry actually held an entry to remove,
/// <c>false</c> otherwise. SYNTH-67 lifted this multi-step sequence out of
/// <c>RepositoryEndpoints.DeleteCollectionAsync</c> so it lives behind the CQRS seam (issue #82),
/// following the pattern <c>IndexRepositoryCommand</c> established.
/// </summary>
public sealed record DeleteCollectionCommand(string Collection) : ICommand<bool>;
