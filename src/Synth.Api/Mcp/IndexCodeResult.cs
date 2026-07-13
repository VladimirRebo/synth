using Synth.Application.Indexing;

namespace Synth.Api.Mcp;

/// <summary>
/// Flat, serializable result of the <c>index_code</c> MCP tool. Mirrors the REST <c>202</c> body's
/// <c>{ collection, status }</c> shape on success (<see cref="Status"/> = <c>"started"</c>,
/// <see cref="Collection"/> = the resolved collection name, <see cref="Error"/> = null), and reports
/// validation / already-running failures inline (<see cref="Status"/> = <c>"rejected"</c>,
/// <see cref="Error"/> = a human-readable reason) rather than throwing — the natural shape for a tool
/// result an agent reads back.
/// </summary>
public sealed record IndexCodeResult(string? Collection, string Status, string? Error)
{
    /// <summary>Projects the shared <see cref="IndexStartOutcome"/> into a tool result.</summary>
    public static IndexCodeResult From(IndexStartOutcome outcome) => outcome.Status switch
    {
        IndexStartOutcome.Kind.Started => new(outcome.Collection, "started", null),
        _ => new(null, "rejected", outcome.Error),
    };
}
