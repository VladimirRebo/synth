var builder = DistributedApplication.CreateBuilder(args);

// Ollama is Synth's embedding provider. Run it as an Aspire-managed container with
// a persistent data volume so pulled models survive restarts, and pull a default
// embedding model on startup. The referenced model resource hands the API an
// endpoint + model name via service discovery (no hardcoded URL).
var ollama = builder.AddOllama("ollama")
    .WithDataVolume();

// nomic-embed-text, not the larger qwen3-embedding:4b: Ollama runs CPU-only in this Docker
// setup (no GPU passthrough), and the 4b model's embed requests routinely exceeded the
// default 100s HttpClient timeout, stalling indexing almost entirely. Not worth the
// complexity of raising timeouts/retries for better embedding quality during local dev.
var embeddings = ollama.AddModel("embeddings", "nomic-embed-text");

// Qdrant is Synth's vector store. Run it as an Aspire-managed container with a
// persistent data volume so the index survives restarts. The referenced resource
// hands the API its gRPC endpoint + API key via service discovery (no hardcoded URL).
var qdrant = builder.AddQdrant("qdrant")
    .WithDataVolume();

// Synth.Api runs as an Aspire project resource. It gets the Ollama embedding endpoint and
// the Qdrant vector store via service discovery from the referenced resources. Config,
// repository registry, call-graph, and logs are all local SQLite/file storage now (issue
// #80) — no database resource/connection string needed here.
//
// IsProxied=false pins the externally-reachable port to the app's own launchSettings.json
// port (5042) instead of a DCP-assigned proxy port that changes every run — otherwise the
// MCP connect panel's copy-paste snippets, `make index`, and anyone hitting the API directly
// have to re-discover the port each time. Same discipline Sonar's AppHost uses.
var api = builder.AddProject<Projects.Synth_Api>("api")
    .WithEndpoint("http", e => e.IsProxied = false)
    .WithReference(embeddings)
    .WaitFor(embeddings)
    .WithReference(qdrant)
    .WaitFor(qdrant);

// The Vue client (src/client) runs as an Aspire-managed Vite dev server. AddViteApp
// wires Aspire's assigned HTTP endpoint straight into `vite --port <n>`, so no
// vite.config.ts changes are needed. Was deliberately left out of the AppHost by
// SYNTH-14 ("needs real research") — this is that follow-up.
//
// Pinned to the conventional Vite dev port (5173, IsProxied=false) for the same reason as
// the API above: a stable URL instead of a new random port every `dotnet run`.
builder.AddViteApp("client", "../client")
    .WithReference(api)
    .WaitFor(api)
    .WithEndpoint("http", e =>
    {
        e.Port = 5173;
        e.TargetPort = 5173;
        e.IsProxied = false;
    })
    .WithExternalHttpEndpoints();

builder.Build().Run();
