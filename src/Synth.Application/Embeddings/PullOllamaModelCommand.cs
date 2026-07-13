using Synth.Application.Cqrs;

namespace Synth.Application.Embeddings;

/// <summary>
/// Command to pull an Ollama model — the input to <see cref="PullOllamaModelCommandHandler"/>, backing
/// <c>POST /settings/embedding/ollama/pull</c>. A pull is long-running, so the handler reserves the single
/// pull slot and dispatches a detached background task, returning immediately; the client polls
/// <c>GET /settings/embedding/ollama/pull/status</c> for progress. Same fire-and-forget shape as
/// <see cref="Indexing.IndexRepositoryCommand"/> (SYNTH-61). SYNTH-70 lifted the dispatch logic out of the
/// Minimal-API <c>OllamaModelEndpoints</c> so it lives behind the CQRS seam (issue #82).
/// </summary>
public sealed record PullOllamaModelCommand(string? Model) : ICommand<PullOllamaModelResult>;

/// <summary>
/// Request body for <c>POST /settings/embedding/ollama/pull</c>. The controller binds this and dispatches
/// a <see cref="PullOllamaModelCommand"/>; kept as its own DTO (rather than binding the command directly)
/// so the wire contract stays independent of the CQRS input.
/// </summary>
public sealed record OllamaPullRequest(string? Model);
