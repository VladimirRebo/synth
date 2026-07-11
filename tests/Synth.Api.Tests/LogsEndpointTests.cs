using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Serilog.Events;
using Synth.Api.Logging;

namespace Synth.Api.Tests;

// Drives GET /logs over HTTP. An InMemoryLogEntryStore is pre-populated with a known set of entries
// and swapped in as the ILogEntryStore singleton the endpoint injects, so the buffer contents are
// fully deterministic (no live Serilog pipeline, no timing) and every filter can be asserted
// precisely. Only the store substitution changed from the pre-SYNTH-28 version (was a raw
// RingBufferLogSink); the filter behavior under test is unchanged.
public class LogsEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly DateTimeOffset Base = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly WebApplicationFactory<Program> _factory;

    public LogsEndpointTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private HttpClient CreateClient(ILogEntryStore store) =>
        _factory
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.RemoveAll<ILogEntryStore>();
                services.AddSingleton(store);

                // Drop the background drain so live Serilog events from app startup can't leak into
                // the seeded store — the filters are asserted against exactly the four seeded entries.
                var writer = services.SingleOrDefault(d =>
                    d.ServiceType == typeof(IHostedService) &&
                    d.ImplementationType == typeof(LogEntryStoreWriter));
                if (writer is not null)
                    services.Remove(writer);
            }))
            .CreateClient();

    // A fixed four-entry buffer spanning multiple levels and ascending timestamps. RecordAsync
    // appends and SnapshotAsync returns oldest-first, so entries come back in the order recorded.
    private static InMemoryLogEntryStore SeededStore()
    {
        var store = new InMemoryLogEntryStore(capacity: 100);
        Record(store, 0, LogEventLevel.Debug, "debug details");
        Record(store, 1, LogEventLevel.Information, "indexing started");
        Record(store, 2, LogEventLevel.Warning, "slow query detected");
        Record(store, 3, LogEventLevel.Error, "indexing failed");
        return store;
    }

    private static void Record(InMemoryLogEntryStore store, int minute, LogEventLevel level, string message) =>
        store.RecordAsync(new LogEntry(Base.AddMinutes(minute), level.ToString(), message, Exception: null))
            .GetAwaiter().GetResult();

    private static async Task<LogEntryDto[]> GetLogs(HttpClient client, string query)
    {
        var response = await client.GetAsync($"/logs{query}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LogEntryDto[]>())!;
    }

    [Fact]
    public async Task No_params_returns_everything_buffered_oldest_first()
    {
        var client = CreateClient(SeededStore());

        var entries = await GetLogs(client, "");

        Assert.Equal(
            new[] { "debug details", "indexing started", "slow query detected", "indexing failed" },
            entries.Select(e => e.Message));
    }

    [Fact]
    public async Task Level_filter_excludes_less_severe_entries()
    {
        var client = CreateClient(SeededStore());

        var entries = await GetLogs(client, "?level=Warning");

        // Warning + Error survive; Debug + Information are dropped.
        Assert.Equal(
            new[] { "slow query detected", "indexing failed" },
            entries.Select(e => e.Message));
    }

    [Fact]
    public async Task Since_filter_excludes_entries_at_or_before_the_timestamp()
    {
        var client = CreateClient(SeededStore());

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
        var client = CreateClient(SeededStore());

        var entries = await GetLogs(client, "?search=INDEXING");

        Assert.Equal(
            new[] { "indexing started", "indexing failed" },
            entries.Select(e => e.Message));
    }

    [Fact]
    public async Task Level_and_search_combine_with_and()
    {
        var client = CreateClient(SeededStore());

        // "indexing" matches two entries; level=Warning narrows to just the Error one.
        var entries = await GetLogs(client, "?level=Warning&search=indexing");

        Assert.Equal(new[] { "indexing failed" }, entries.Select(e => e.Message));
    }

    [Fact]
    public async Task Limit_and_offset_slice_the_result_preserving_oldest_first_order()
    {
        var client = CreateClient(SeededStore());

        // Four entries oldest-first; skip 1, take 2 -> minutes 1 and 2.
        var entries = await GetLogs(client, "?offset=1&limit=2");

        Assert.Equal(
            new[] { "indexing started", "slow query detected" },
            entries.Select(e => e.Message));
    }

    [Fact]
    public async Task Pagination_applies_after_the_filters()
    {
        var client = CreateClient(SeededStore());

        // search=indexing matches minutes 1 and 3; offset=1 drops the first -> only "indexing failed".
        var entries = await GetLogs(client, "?search=indexing&offset=1");

        Assert.Equal(new[] { "indexing failed" }, entries.Select(e => e.Message));
    }

    [Fact]
    public async Task Negative_offset_is_rejected()
    {
        var client = CreateClient(SeededStore());

        var response = await client.GetAsync("/logs?offset=-1");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_level_is_rejected()
    {
        var client = CreateClient(SeededStore());

        var response = await client.GetAsync("/logs?level=Nope");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private sealed record LogEntryDto(DateTimeOffset Timestamp, string Level, string Message, string? Exception);
}
