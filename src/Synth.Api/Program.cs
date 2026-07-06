using Synth.Api.Configuration;

var builder = WebApplication.CreateBuilder(args);

// Aspire service defaults: OpenTelemetry, health checks, and service discovery.
builder.AddServiceDefaults();

// MongoDB config store. The connection string is supplied by Aspire service
// discovery via the "synthconfig" reference from the AppHost (no hardcoded value).
// This registers IMongoClient/IMongoDatabase in DI plus a Mongo health check that
// feeds the app's health-check pipeline.
builder.AddMongoDBClient("synthconfig");

// Config layering: appsettings.json (bootstrap) -> IConfigStore document
// (File/Mongo, live-reloaded) -> environment variables (always win).
builder.AddSynthConfigStore();

var app = builder.Build();

// Aspire default endpoints (liveness at /alive in development). Synth.Api keeps
// ownership of the readiness endpoint at /health below.
app.MapDefaultEndpoints();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();

// Exposed so the test project can bootstrap the API via WebApplicationFactory<Program>.
public partial class Program;
