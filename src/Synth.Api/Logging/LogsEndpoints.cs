using System.Globalization;
using Serilog.Events;
using Synth.Domain.Logging;

namespace Synth.Api.Logging;

/// <summary>
/// Maps <c>GET /logs</c>: a filterable, read-only view over the active <see cref="ILogEntryStore"/>
/// (SYNTH-28: Mongo-backed and durable when configured, in-memory otherwise). The Vue client polls
/// this — an initial call with no parameters loads the recent buffer, then subsequent calls pass
/// <c>since</c> to fetch only newer entries. Registered bare as <c>/logs</c> (no <c>/api</c> prefix):
/// the Vite dev proxy strips <c>/api</c> before forwarding, matching every other endpoint in this app.
/// </summary>
public static class LogsEndpoints
{
    public static IEndpointRouteBuilder MapLogsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // GET /logs?level=&since=&search=&limit=&offset= — all optional, combined with AND, oldest
        // first. limit/offset paginate the already-filtered set; omitting both returns everything.
        endpoints.MapGet("/logs", async (
            ILogEntryStore store,
            string? level,
            string? since,
            string? search,
            int? limit,
            int? offset) =>
        {
            LogEventLevel? minLevel = null;
            if (!string.IsNullOrWhiteSpace(level))
            {
                // Reuse Serilog's own level names/ordering rather than inventing a parallel scale.
                if (!Enum.TryParse<LogEventLevel>(level, ignoreCase: true, out var parsedLevel))
                    return Results.BadRequest(new { error = $"Unknown log level: {level}" });
                minLevel = parsedLevel;
            }

            DateTimeOffset? sinceTimestamp = null;
            if (!string.IsNullOrWhiteSpace(since))
            {
                if (!DateTimeOffset.TryParse(
                        since,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var parsedSince))
                    return Results.BadRequest(new { error = $"Invalid 'since' timestamp: {since}" });
                sinceTimestamp = parsedSince;
            }

            if (offset is < 0)
                return Results.BadRequest(new { error = $"'offset' must be non-negative: {offset}" });
            if (limit is < 0)
                return Results.BadRequest(new { error = $"'limit' must be non-negative: {limit}" });

            IEnumerable<LogEntry> entries = await store.SnapshotAsync();

            if (minLevel is { } floor)
            {
                // entry.Level is LogEventLevel.ToString(); parse it back so we compare on Serilog's
                // severity ordering (Verbose < Debug < Information < Warning < Error < Fatal).
                entries = entries.Where(e =>
                    Enum.TryParse<LogEventLevel>(e.Level, ignoreCase: true, out var entryLevel) &&
                    entryLevel >= floor);
            }

            if (sinceTimestamp is { } after)
                entries = entries.Where(e => e.Timestamp > after);

            if (!string.IsNullOrWhiteSpace(search))
                entries = entries.Where(e =>
                    e.Message.Contains(search, StringComparison.OrdinalIgnoreCase));

            // SnapshotAsync() already yields oldest-first; the filters preserve that order, and
            // Skip/Take below page over the already-filtered set without disturbing it.
            if (offset is { } skip)
                entries = entries.Skip(skip);
            if (limit is { } take)
                entries = entries.Take(take);

            return Results.Ok(entries.ToArray());
        });

        return endpoints;
    }
}
