var builder = DistributedApplication.CreateBuilder(args);

// MongoDB backs Synth's general-purpose data (config, repository registry, call-graph
// edges, logs) — one database, several collections. Run it as an Aspire-managed
// container with a persistent data volume so data survives restarts.
//
// Pin the admin credentials instead of letting Aspire generate a new random
// password per run: AddMongoDB otherwise regenerates the password on every
// `dotnet run`, but WithDataVolume keeps the *previous* run's admin user (with
// the *old* password) on disk — the new random password then fails to
// authenticate against it ("SCRAM authentication failed, storedKey mismatch"),
// the resource never reports healthy, and `WaitFor` blocks the API from
// starting forever. A fixed local-dev-only password keeps the volume and the
// credentials in sync across restarts. Not meant to protect real secrets.
var mongoUser = builder.AddParameter("mongo-username", "synth", publishValueAsDefault: true);
var mongoPassword = builder.AddParameter("mongo-password", "synth-local-dev-only", secret: true);

// Pinned to the conventional Mongo port (27017, IsProxied=false) for the same reason as the
// API/client below: a stable connection string instead of a new random host port every
// `dotnet run` — external tools (the mongodb-synth MCP server, mongosh, Compass) would
// otherwise need reconfiguring after every restart.
var mongo = builder.AddMongoDB("mongo", userName: mongoUser, password: mongoPassword)
    .WithDataVolume()
    .WithEndpoint("tcp", e =>
    {
        e.Port = 27017;
        e.IsProxied = false;
    });

var dataDb = mongo.AddDatabase("synthdata");

// Ollama is Synth's embedding provider. Run it as an Aspire-managed container with
// a persistent data volume so pulled models survive restarts, and pull a default
// embedding model on startup. The referenced model resource hands the API an
// endpoint + model name via service discovery (no hardcoded URL).
var ollama = builder.AddOllama("ollama")
    .WithDataVolume();

var embeddings = ollama.AddModel("embeddings", "nomic-embed-text");

// Qdrant is Synth's vector store. Run it as an Aspire-managed container with a
// persistent data volume so the index survives restarts. The referenced resource
// hands the API its gRPC endpoint + API key via service discovery (no hardcoded URL).
var qdrant = builder.AddQdrant("qdrant")
    .WithDataVolume();

// Synth.Api runs as an Aspire project resource. It gets a MongoDB connection
// string, the Ollama embedding endpoint, and the Qdrant vector store via service
// discovery from the referenced resources.
//
// IsProxied=false pins the externally-reachable port to the app's own launchSettings.json
// port (5042) instead of a DCP-assigned proxy port that changes every run — otherwise the
// MCP connect panel's copy-paste snippets, `make index`, and anyone hitting the API directly
// have to re-discover the port each time. Same discipline Sonar's AppHost uses.
var api = builder.AddProject<Projects.Synth_Api>("api")
    .WithEndpoint("http", e => e.IsProxied = false)
    .WithReference(dataDb)
    .WaitFor(dataDb)
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
