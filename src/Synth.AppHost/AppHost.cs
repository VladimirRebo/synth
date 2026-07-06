var builder = DistributedApplication.CreateBuilder(args);

// Synth.Api runs as an Aspire project resource. Follow-up tasks will add Qdrant,
// MongoDB, and Ollama resources and wire them in here.
builder.AddProject<Projects.Synth_Api>("api");

builder.Build().Run();
