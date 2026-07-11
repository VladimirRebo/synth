using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Synth.Api.Storage;
using Synth.Core;
using Synth.Mcp.Stdio;
using Synth.Domain;

namespace Synth.Api.Tests;

// Proves SYNTH-13: the stdio MCP host wires the same transport-agnostic `search_code` tool over
// the stdio transport. Mirrors SYNTH-12's registration check (CodeSearchMcpToolTests) but at the
// DI level — we build the host's container and inspect it rather than spawning the process and
// speaking the wire protocol. Runs fully offline: AddSynthEmbeddings/AddSynthVectorStore build
// their clients lazily, so no live Ollama/Qdrant/Docker is needed.
public class StdioMcpHostTests
{
    [Fact]
    public void Search_code_tool_is_registered_on_the_stdio_mcp_server()
    {
        using var host = StdioMcpHost.CreateBuilder([]).Build();

        var tools = host.Services.GetServices<McpServerTool>();

        Assert.Contains(tools, tool => tool.ProtocolTool.Name == "search_code");
    }

    [Fact]
    public void Index_code_tool_is_registered_on_the_stdio_mcp_server()
    {
        using var host = StdioMcpHost.CreateBuilder([]).Build();

        var tools = host.Services.GetServices<McpServerTool>();

        Assert.Contains(tools, tool => tool.ProtocolTool.Name == "index_code");
    }

    [Fact]
    public void Host_wires_the_indexing_layer_the_index_code_tool_depends_on()
    {
        // The index_code tool (SYNTH-36) resolves the indexing pipeline, git service, repository
        // registry and job tracker per invocation, so all must be registered over stdio too.
        var services = StdioMcpHost.CreateBuilder([]).Services;

        Assert.Contains(services, d => d.ServiceType == typeof(Synth.Core.IndexingPipeline));
        Assert.Contains(services, d => d.ServiceType == typeof(Synth.Core.Vcs.GitRepoService));
        Assert.Contains(services, d => d.ServiceType == typeof(Synth.Domain.Vcs.IRepositoryRegistry));
        Assert.Contains(services, d => d.ServiceType == typeof(Synth.Api.Indexing.IIndexJobTracker));
    }

    [Theory]
    [InlineData("list_collections")]
    [InlineData("delete_collection")]
    [InlineData("health_check")]
    public void Repository_and_health_tools_are_registered_on_the_stdio_mcp_server(string toolName)
    {
        // SYNTH-43: the three operability tools must be present over stdio as well as HTTP.
        using var host = StdioMcpHost.CreateBuilder([]).Build();

        var tools = host.Services.GetServices<McpServerTool>();

        Assert.Contains(tools, tool => tool.ProtocolTool.Name == toolName);
    }

    [Fact]
    public void Host_wires_the_health_layer_the_health_check_tool_depends_on()
    {
        // health_check (SYNTH-43) resolves IHealthCheckService per invocation, so AddSynthHealthChecks
        // must run over stdio too. With no live Qdrant configured it falls back to the always-healthy
        // probe, so the service resolves without a backend — mirroring SYNTH-35's "not configured" path.
        using var host = StdioMcpHost.CreateBuilder([]).Build();

        var health = host.Services.GetService<Synth.Api.Health.IHealthCheckService>();

        Assert.NotNull(health);
    }

    [Fact]
    public void Host_wires_the_search_layer_the_tool_depends_on()
    {
        // The tool resolves CodeSearchService (over the vector store) per invocation, so the
        // search layer must be registered. We assert on the service descriptors rather than
        // resolving CodeSearchService, since — like Synth.Api — the Ollama embedding generator
        // only fails when actually resolved without a connection string (registration is lazy).
        var services = StdioMcpHost.CreateBuilder([]).Services;

        Assert.Contains(services, d => d.ServiceType == typeof(CodeSearchService));
        Assert.Contains(services, d => d.ServiceType == typeof(ICodeChunkStore));
    }

    [Fact]
    public void Vector_store_falls_back_to_Local_without_a_qdrant_connection()
    {
        // Confirms the offline/Docker-less guarantee the host relies on: with no "qdrant"
        // connection configured, the in-memory store is used and resolves without a live backend.
        using var host = StdioMcpHost.CreateBuilder([]).Build();
        using var scope = host.Services.CreateScope();

        var store = scope.ServiceProvider.GetService<ICodeChunkStore>();

        Assert.IsType<LocalCodeChunkStore>(store);
    }
}
