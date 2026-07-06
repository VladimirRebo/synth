using Synth.Api.Agents;
using Synth.Api.Configuration;
using Synth.Api.Embeddings;
using Synth.Api.Storage;

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

// Embedding generator (Ollama-backed IEmbeddingGenerator<string, Embedding<float>>).
// The endpoint + model arrive via Aspire service discovery from the AppHost's Ollama
// resource; the client connects lazily, so no live Ollama is needed to start up.
builder.AddSynthEmbeddings();

// Vector store for code chunks. Uses Qdrant when the AppHost supplies a "qdrant"
// connection (endpoint + API key via service discovery), otherwise an in-memory Local
// store — the same fallback tests and Docker-less local dev run on. Registers ICodeChunkStore.
builder.AddSynthVectorStore();

// Microsoft Agent Framework: register one minimal offline example agent.
// Proof-of-wiring only — see SYNTH-5; does not replace the existing agent loop.
builder.Services.AddSynthAgents();

var app = builder.Build();

// Aspire default endpoints (liveness at /alive in development). Synth.Api keeps
// ownership of the readiness endpoint at /health below.
app.MapDefaultEndpoints();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();

// Exposed so the test project can bootstrap the API via WebApplicationFactory<Program>.
public partial class Program;
