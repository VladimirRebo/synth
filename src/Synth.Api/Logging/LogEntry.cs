namespace Synth.Api.Logging;

/// <summary>
/// An immutable snapshot of a single log event, holding only what a log viewer needs to render.
/// Captured by <see cref="RingBufferLogSink"/> and later surfaced (SYNTH-24) over REST.
/// </summary>
/// <param name="Timestamp">When the event was raised, in UTC.</param>
/// <param name="Level">The severity name, e.g. "Information"/"Warning"/"Error".</param>
/// <param name="Message">The rendered message (template already expanded), not the raw template.</param>
/// <param name="Exception">The exception's <c>ToString()</c> when present, otherwise <c>null</c>.</param>
public sealed record LogEntry(
    DateTimeOffset Timestamp,
    string Level,
    string Message,
    string? Exception);
