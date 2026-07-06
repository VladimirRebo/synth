var builder = DistributedApplication.CreateBuilder(args);

// MongoDB is Synth's config store. Run it as an Aspire-managed container with a
// persistent data volume so config survives restarts. Follow-up tasks will add
// Qdrant and Ollama resources here.
var mongo = builder.AddMongoDB("mongo")
    .WithDataVolume();

var configDb = mongo.AddDatabase("synthconfig");

// Synth.Api runs as an Aspire project resource. It gets a MongoDB connection
// string via service discovery from the referenced database resource.
builder.AddProject<Projects.Synth_Api>("api")
    .WithReference(configDb)
    .WaitFor(configDb);

builder.Build().Run();
