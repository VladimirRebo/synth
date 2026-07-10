using System.Text.Json.Serialization;
using Serilog;
using Synth.Api.Agents;
using Synth.Api.Configuration;
using Synth.Api.Embeddings;
using Synth.Api.Graph;
using Synth.Api.Indexing;
using Synth.Api.Logging;
using Synth.Api.Mcp;
using Synth.Api.Search;
using Synth.Api.Storage;
using Synth.Api.Vcs;

var builder = WebApplication.CreateBuilder(args);

// Serilog sink that captures log events onto a bounded channel (SYNTH-28). Constructed once here
// so it can be attached to Serilog below; the actual persistence happens off the request hot path
// on LogEntryStoreWriter's background thread, into the ILogEntryStore chosen by AddSynthLogging
// (Mongo when configured, in-memory otherwise). Emit itself never touches Mongo.
var logSink = new LogEntryStoreSink();

// Serilog, wired early (before other builder calls that might log) so structured events exist to
// capture. Console output is preserved for local-dev visibility; writeToProviders keeps the
// Aspire/OpenTelemetry logging pipeline (registered by AddServiceDefaults) working alongside.
builder.Host.UseSerilog((context, loggerConfig) => loggerConfig
    .ReadFrom.Configuration(context.Configuration)
    .WriteTo.Console()
    .WriteTo.Sink(logSink), writeToProviders: true);

// Log persistence: registers the sink as a DI singleton, selects the ILogEntryStore (Mongo when
// the synthdata connection string is present, in-memory otherwise), and starts the background
// service that drains the sink's channel into that store. The store builds its own MongoClient from
// the connection string, like the config/registry/graph stores, so ordering here doesn't matter.
builder.AddSynthLogging(logSink);

// Aspire service defaults: OpenTelemetry, health checks, and service discovery.
builder.AddServiceDefaults();

// Serialize enums (e.g. CodeSearchResult.ChunkType) as their string name, not the underlying
// int, in Minimal API JSON responses — matches the MCP tool's own serialization (which already
// renders ChunkType as e.g. "Method"), so the two search transports agree on the wire shape.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// MongoDB — general-purpose data store (config, repository registry, call-graph, logs).
// The connection string is supplied by Aspire service discovery via the "synthdata"
// reference from the AppHost (no hardcoded value). This registers
// IMongoClient/IMongoDatabase in DI plus a Mongo health check that feeds the app's
// health-check pipeline.
builder.AddMongoDBClient("synthdata");

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

// Indexing pipeline: registers the file chunkers + IndexingPipeline that walks a
// directory, chunks/embeds each file and upserts the chunks into the vector store.
builder.AddSynthIndexing();

// VCS layer: GitRepoService (clone/fetch remote repos) + the repository registry that
// records what has been indexed. Uses Mongo when configured, an in-memory fallback otherwise.
builder.AddSynthVcs();

// Call-graph storage: registers ICodeGraphStore for structural "who calls X / what does X call"
// edges (issue #33). Mongo when configured, an in-memory fallback otherwise. Registration only —
// extraction (SYNTH-26) and query tools (SYNTH-27) build on top of it later.
builder.AddSynthCodeGraph();

// Search layer: registers QueryExpander + CodeSearchService (over-fetch, rerank, dedup)
// on top of the embedding generator and vector store registered above.
builder.AddSynthSearch();

// Microsoft Agent Framework: register one minimal offline example agent.
// Proof-of-wiring only — see SYNTH-5; does not replace the existing agent loop.
builder.Services.AddSynthAgents();

// MCP layer: register the MCP server with HTTP transport and the transport-agnostic
// `search_code` tool wrapping CodeSearchService. Endpoints are mapped via MapMcp below.
builder.AddSynthMcp();

var app = builder.Build();

// Aspire default endpoints (liveness at /alive in development). Synth.Api keeps
// ownership of the readiness endpoint at /health below.
app.MapDefaultEndpoints();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Manual indexing trigger (POST /index { "path": "..." }) plus its progress poll
// (GET /index/status). SYNTH-31: POST /index is fire-and-forget — it validates, returns 202
// immediately and runs the clone+indexing in the background; clients poll /index/status.
app.MapIndexingEndpoints();

// Lists the known collections and their metadata (GET /repositories) from the repository registry.
app.MapRepositoryEndpoints();

// Settings API for the Vcs config section (GET/PUT /settings/vcs): read/write the workspace
// root and provider tokens at runtime, masking secrets and live-reloading IOptionsMonitor<VcsOptions>.
app.MapVcsSettingsEndpoints();

// Settings API for the Embedding config section (GET/PUT /settings/embedding): read/write the
// provider/model/key at runtime, masking the OpenAI key and probing a candidate config before it is
// persisted so a broken provider is never saved.
app.MapEmbeddingSettingsEndpoints();

// Advanced Settings escape hatch (GET/PUT /settings/raw): read/replace the whole config document as
// plain JSON, secrets unmasked, no probe — a deliberate power-user path that trusts the caller and
// validates only that the body is a well-formed JSON object. Shares the section endpoints' reload path.
app.MapRawSettingsEndpoints();

// Plain REST search (GET /search?q=...&limit=...) for the Vue client — the MCP tool at
// /mcp is for AI-agent clients, this is the human-facing equivalent over CodeSearchService.
app.MapSearchEndpoints();

// Plain REST call-graph queries (GET /callers?symbol=..., GET /callees?symbol=...) — the
// human-facing equivalent of the find_callers/find_callees MCP tools over ICodeGraphStore.
app.MapCallGraphEndpoints();

// Filterable read of the in-memory log ring buffer (GET /logs?level=&since=&search=) so the Vue
// client can poll the live log — `since` returns only entries newer than the last poll.
app.MapLogsEndpoints();

// MCP Streamable HTTP transport endpoints (the `search_code` tool is served here).
app.MapMcp("/mcp");

app.Run();

// Exposed so the test project can bootstrap the API via WebApplicationFactory<Program>.
public partial class Program;
