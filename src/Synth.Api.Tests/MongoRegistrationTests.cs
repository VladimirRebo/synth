using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace Synth.Api.Tests;

// Proves SYNTH-3 wiring: the Aspire MongoDB client integration registers
// IMongoClient/IMongoDatabase in DI, resolved from the Aspire-supplied connection
// string (no hardcoded value in the app). A MongoClient is created lazily and does
// not open a socket, so this runs without a live Mongo/Docker.
public class MongoRegistrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string TestConnectionString = "mongodb://localhost:27017/synthconfig";

    private readonly WebApplicationFactory<Program> _factory;

    public MongoRegistrationTests(WebApplicationFactory<Program> factory) =>
        _factory = factory.WithWebHostBuilder(builder =>
            // Stand in for the connection string Aspire injects via service discovery.
            builder.UseSetting("ConnectionStrings:synthconfig", TestConnectionString));

    [Fact]
    public void IMongoClient_is_registered()
    {
        using var scope = _factory.Services.CreateScope();

        var client = scope.ServiceProvider.GetService<IMongoClient>();

        Assert.NotNull(client);
    }

    [Fact]
    public void IMongoDatabase_resolves_to_the_configured_database()
    {
        using var scope = _factory.Services.CreateScope();

        var database = scope.ServiceProvider.GetService<IMongoDatabase>();

        Assert.NotNull(database);
        Assert.Equal("synthconfig", database.DatabaseNamespace.DatabaseName);
    }
}
