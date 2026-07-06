var builder = DistributedApplication.CreateBuilder(args);

// MongoDB is Synth's config store. Run it as an Aspire-managed container with a
// persistent data volume so config survives restarts. Follow-up tasks will add
// the Qdrant vector store here.
var mongo = builder.AddMongoDB("mongo")
    .WithDataVolume();

var configDb = mongo.AddDatabase("synthconfig");

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
builder.AddProject<Projects.Synth_Api>("api")
    .WithReference(configDb)
    .WaitFor(configDb)
    .WithReference(embeddings)
    .WaitFor(embeddings)
    .WithReference(qdrant)
    .WaitFor(qdrant);

builder.Build().Run();
