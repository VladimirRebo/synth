using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog.Events;
using Serilog.Parsing;
using Synth.Api.Logging;

namespace Synth.Api.Tests;

// Drives GET /logs over HTTP. A RingBufferLogSink is pre-populated with a known set of entries and
// swapped in as the DI singleton the endpoint injects, so the buffer contents are fully
// deterministic (no live Serilog pipeline, no timing) and every filter can be asserted precisely.
public class LogsEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly DateTimeOffset Base = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly MessageTemplateParser Parser = new();

    private readonly WebApplicationFactory<Program> _factory;

    public LogsEndpointTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private HttpClient CreateClient(RingBufferLogSink sink) =>
        _factory
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.RemoveAll<RingBufferLogSink>();
                services.AddSingleton(sink);
            }))
            .CreateClient();

    // Feeds one event into the sink at an explicit timestamp/level/message (no template args needed
    // since RenderMessage on a literal template just returns the text verbatim).
    private static void Emit(RingBufferLogSink sink, int minute, LogEventLevel level, string message) =>
        sink.Emit(new LogEvent(
            Base.AddMinutes(minute),
            level,
            exception: null,
            Parser.Parse(message),
            properties: []));

    // A fixed four-entry buffer spanning multiple levels and ascending timestamps.
    private static RingBufferLogSink SeededSink()
    {
        var sink = new RingBufferLogSink(capacity: 100);
        Emit(sink, 0, LogEventLevel.Debug, "debug details");
        Emit(sink, 1, LogEventLevel.Information, "indexing started");
        Emit(sink, 2, LogEventLevel.Warning, "slow query detected");
        Emit(sink, 3, LogEventLevel.Error, "indexing failed");
        return sink;
    }

    private static async Task<LogEntryDto[]> GetLogs(HttpClient client, string query)
    {
        var response = await client.GetAsync($"/logs{query}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LogEntryDto[]>())!;
    }

    [Fact]
    public async Task No_params_returns_everything_buffered_oldest_first()
    {
        var client = CreateClient(SeededSink());

        var entries = await GetLogs(client, "");

        Assert.Equal(
            new[] { "debug details", "indexing started", "slow query detected", "indexing failed" },
            entries.Select(e => e.Message));
    }

    [Fact]
    public async Task Level_filter_excludes_less_severe_entries()
    {
        var client = CreateClient(SeededSink());

        var entries = await GetLogs(client, "?level=Warning");

        // Warning + Error survive; Debug + Information are dropped.
        Assert.Equal(
            new[] { "slow query detected", "indexing failed" },
            entries.Select(e => e.Message));
    }

    [Fact]
    public async Task Since_filter_excludes_entries_at_or_before_the_timestamp()
    {
        var client = CreateClient(SeededSink());

        // Strictly after minute 1 -> only minutes 2 and 3 remain.
        var since = Uri.EscapeDataString(Base.AddMinutes(1).ToString("o"));
        var entries = await GetLogs(client, $"?since={since}");

        Assert.Equal(
            new[] { "slow query detected", "indexing failed" },
            entries.Select(e => e.Message));
    }

    [Fact]
    public async Task Search_filter_matches_message_substring_case_insensitively()
    {
        var client = CreateClient(SeededSink());

        var entries = await GetLogs(client, "?search=INDEXING");

        Assert.Equal(
            new[] { "indexing started", "indexing failed" },
            entries.Select(e => e.Message));
    }

    [Fact]
    public async Task Level_and_search_combine_with_and()
    {
        var client = CreateClient(SeededSink());

        // "indexing" matches two entries; level=Warning narrows to just the Error one.
        var entries = await GetLogs(client, "?level=Warning&search=indexing");

        Assert.Equal(new[] { "indexing failed" }, entries.Select(e => e.Message));
    }

    [Fact]
    public async Task Unknown_level_is_rejected()
    {
        var client = CreateClient(SeededSink());

        var response = await client.GetAsync("/logs?level=Nope");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private sealed record LogEntryDto(DateTimeOffset Timestamp, string Level, string Message, string? Exception);
}
