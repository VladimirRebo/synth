using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics;
using Serilog;
using Synth.Api.Agents;
using Synth.Api.Indexing;
using Synth.Infrastructure.Configuration;
using Synth.Infrastructure.Health;
using Synth.Infrastructure.Logging;
using Synth.Api.Mcp;
using Synth.Api.Search;
using Synth.Application.Vcs;
using Synth.Domain.Configuration;
using Synth.Domain.Logging;
using Synth.Domain.Vcs;
using Synth.Infrastructure.Embeddings;
using Synth.Infrastructure.Graph;
using Synth.Infrastructure.Storage;
using Synth.Infrastructure.Vcs;

var builder = WebApplication.CreateBuilder(args);

// Captures log events onto a bounded channel; a background writer drains it into the SQLite log
// store off the request hot path, so Emit itself never touches the database.
var logSink = new LogEntryStoreSink();

// Wired before any other builder call that might log. writeToProviders keeps the Aspire/OpenTelemetry
// pipeline (registered by AddServiceDefaults below) working alongside the console + sink.
builder.Host.UseSerilog((context, loggerConfig) => loggerConfig
    .ReadFrom.Configuration(context.Configuration)
    .WriteTo.Console()
    .WriteTo.Sink(logSink), writeToProviders: true);

builder.AddSynthLogging(logSink);
builder.AddServiceDefaults();

// Serialize enums as their string name (e.g. "Method", not the underlying int) so Controller and MCP
// tool responses agree on the wire shape.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddControllers()
    .AddJsonOptions(options =>
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// Config layering: appsettings.json (bootstrap) -> IConfigStore document (local ~/.synth/config.json,
// live-reloaded) -> environment variables (always win).
builder.AddSynthConfigStore();

builder.AddSynthEmbeddings();
builder.AddSynthVectorStore();
builder.AddSynthIndexing();
builder.AddSynthVcs();

// One-shot hosted service: GCs workspace-root checkouts with no matching registry entry.
builder.Services.AddHostedService<OrphanCheckoutSweeper>();

// Periodic background loop: checks every repoUrl-indexed collection's remote for a new commit and
// reindexes on change. Api-host-only (like the sweeper above) — not run by Synth.Mcp.Stdio, so only
// one process ever owns this loop. Registered as its own singleton first, then exposed as both
// IHostedService and IRepositoryPoller via forwarding factories, so POST /repositories/poll (which
// depends on IRepositoryPoller) triggers a tick on the exact same instance the background loop runs
// on — not a second, independently-scheduled one.
builder.Services.AddSingleton<RepositoryPollingService>();
builder.Services.AddHostedService<RepositoryPollingService>(sp => sp.GetRequiredService<RepositoryPollingService>());
builder.Services.AddSingleton<IRepositoryPoller>(sp => sp.GetRequiredService<RepositoryPollingService>());

builder.AddSynthCodeGraph();

// Depends on the QdrantClient + IEmbeddingGeneratorFactory registered above, so it must come after
// the vector store and embeddings.
builder.Services.AddSynthHealthChecks();

builder.AddSynthSearch();

// Proof-of-wiring for the Microsoft Agent Framework only; does not replace the existing agent loop.
builder.Services.AddSynthAgents();

builder.AddSynthMcp();

var app = builder.Build();

// Catch anything an endpoint doesn't handle itself (e.g. a SQLite failure propagating up from one
// of the stores, per their deliberate "let it propagate" design) and turn it into a clean JSON 500
// instead of ASP.NET's default HTML error page, which no API client here expects to parse.
app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    if (exception is not null)
        app.Logger.LogError(exception, "Unhandled exception while processing {Path}", context.Request.Path);

    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
    context.Response.ContentType = "application/json";
    await context.Response.WriteAsJsonAsync(new { error = "An unexpected error occurred." });
}));

// Aspire liveness endpoint (/alive in development). Synth.Api keeps ownership of the readiness
// endpoint at /health, served by HealthController below.
app.MapDefaultEndpoints();

app.MapControllers();

// MCP Streamable HTTP transport (the search_code/get_symbol/... tools are served here).
app.MapMcp("/mcp");

app.Run();

// Exposed so the test project can bootstrap the API via WebApplicationFactory<Program>.
public partial class Program;
