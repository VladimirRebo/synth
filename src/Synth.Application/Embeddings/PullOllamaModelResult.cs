namespace Synth.Application.Embeddings;

/// <summary>
/// Discriminated result of handling a <see cref="PullOllamaModelCommand"/>. The controller maps it to the
/// same status codes the old <c>OllamaModelEndpoints</c> returned: <see cref="Kind.Started"/> carries the
/// pulled <see cref="Model"/> (→ <c>202 Accepted</c>), <see cref="Kind.ValidationError"/> a human-readable
/// <see cref="Error"/> (→ <c>400</c>, e.g. a blank model or no endpoint configured), and
/// <see cref="Kind.AlreadyRunning"/> the busy-slot rejection (→ <c>409 Conflict</c>). Mirrors
/// <see cref="Indexing.IndexStartOutcome"/>.
/// </summary>
public sealed record PullOllamaModelResult(PullOllamaModelResult.Kind Status, string? Model, string? Error)
{
    public enum Kind { Started, ValidationError, AlreadyRunning }

    public static PullOllamaModelResult Started(string model) => new(Kind.Started, model, null);

    public static PullOllamaModelResult ValidationError(string message) => new(Kind.ValidationError, null, message);

    public static PullOllamaModelResult AlreadyRunning() =>
        new(Kind.AlreadyRunning, null, "A model pull is already running.");
}
