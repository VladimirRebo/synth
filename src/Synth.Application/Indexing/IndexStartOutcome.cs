namespace Synth.Application.Indexing;

/// <summary>
/// Discriminated result of handling an <see cref="IndexRepositoryCommand"/> — the shared "try to
/// start an indexing job" flow used by both <c>POST /index</c> and the <c>index_code</c> MCP tool.
/// Callers map it to their own response type: <see cref="Kind.Started"/> carries the resolved
/// <see cref="Collection"/>, <see cref="Kind.ValidationError"/> and <see cref="Kind.AlreadyRunning"/>
/// carry a human-readable <see cref="Error"/>.
/// </summary>
public sealed record IndexStartOutcome(IndexStartOutcome.Kind Status, string? Collection, string? Error)
{
    public enum Kind { Started, ValidationError, AlreadyRunning }

    public static IndexStartOutcome Started(string collection) => new(Kind.Started, collection, null);

    public static IndexStartOutcome ValidationError(string message) => new(Kind.ValidationError, null, message);

    public static IndexStartOutcome AlreadyRunning() =>
        new(Kind.AlreadyRunning, null, "An indexing job is already running.");
}
