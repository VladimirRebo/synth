var builder = WebApplication.CreateBuilder(args);

// Aspire service defaults: OpenTelemetry, health checks, and service discovery.
builder.AddServiceDefaults();

var app = builder.Build();

// Aspire default endpoints (liveness at /alive in development). Synth.Api keeps
// ownership of the readiness endpoint at /health below.
app.MapDefaultEndpoints();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();

// Exposed so the test project can bootstrap the API via WebApplicationFactory<Program>.
public partial class Program;
