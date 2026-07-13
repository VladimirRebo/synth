using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Serilog.Events;
using Synth.Domain.Logging;

namespace Synth.Api.Logging;

/// <summary>
/// Serves <c>GET /logs</c>: a filterable, read-only view over the active <see cref="ILogEntryStore"/>
/// (SYNTH-28: Mongo-backed and durable when configured, in-memory otherwise). The Vue client polls
/// this — an initial call with no parameters loads the recent buffer, then subsequent calls pass
/// <c>since</c> to fetch only newer entries.
/// </summary>
/// <remarks>
/// The route stays bare (no <c>/api</c> prefix, no class-level <c>[Route]</c>): the Vite dev proxy
/// strips <c>/api</c> before forwarding, matching every other endpoint in this app. This is a simple
/// filtered read over an already-fetched in-memory snapshot with no external side effects or reusable
/// business rule, so there's no Command/Query wrapper — same judgment call as <c>SearchController</c>
/// and <c>CallGraphController</c>.
/// </remarks>
[ApiController]
public class LogsController : ControllerBase
{
    private readonly ILogEntryStore _store;

    public LogsController(ILogEntryStore store) => _store = store;

    /// <summary>
    /// <c>GET /logs?level=&amp;since=&amp;search=&amp;limit=&amp;offset=</c> — all optional, combined
    /// with AND, oldest first. limit/offset paginate the already-filtered set; omitting both returns
    /// everything. 400 for an unparseable <paramref name="level"/> or <paramref name="since"/>, or a
    /// negative <paramref name="limit"/>/<paramref name="offset"/>.
    /// </summary>
    [HttpGet("/logs")]
    public async Task<IActionResult> Get(
        string? level,
        string? since,
        string? search,
        int? limit,
        int? offset)
    {
        LogEventLevel? minLevel = null;
        if (!string.IsNullOrWhiteSpace(level))
        {
            // Reuse Serilog's own level names/ordering rather than inventing a parallel scale.
            if (!Enum.TryParse<LogEventLevel>(level, ignoreCase: true, out var parsedLevel))
                return BadRequest(new { error = $"Unknown log level: {level}" });
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
                return BadRequest(new { error = $"Invalid 'since' timestamp: {since}" });
            sinceTimestamp = parsedSince;
        }

        if (offset is < 0)
            return BadRequest(new { error = $"'offset' must be non-negative: {offset}" });
        if (limit is < 0)
            return BadRequest(new { error = $"'limit' must be non-negative: {limit}" });

        IEnumerable<LogEntry> entries = await _store.SnapshotAsync();

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

        return Ok(entries.ToArray());
    }
}
