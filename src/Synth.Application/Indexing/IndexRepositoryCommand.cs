using Synth.Application.Cqrs;

namespace Synth.Application.Indexing;

/// <summary>
/// Command to (re)index a repository — the input to <see cref="IndexRepositoryCommandHandler"/>,
/// shared by <c>POST /index</c> and the <c>index_code</c> MCP tool. Exactly one of <see cref="Path"/>
/// (a local directory, indexed into <see cref="Synth.Domain.CollectionNames.Default"/>) or
/// <see cref="RepoUrl"/> (a remote git URL, cloned/fetched and indexed into its own per-repo
/// collection) must be supplied. <see cref="Branch"/> only applies to the <see cref="RepoUrl"/> case.
/// Doubles as the HTTP request body for <c>POST /index</c> (the endpoint binds it directly).
/// </summary>
public sealed record IndexRepositoryCommand(string? Path = null, string? RepoUrl = null, string? Branch = null)
    : ICommand<IndexStartOutcome>;
