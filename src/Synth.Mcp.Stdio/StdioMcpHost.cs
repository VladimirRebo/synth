using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Synth.Api.Embeddings;
using Synth.Api.Graph;
using Synth.Api.Mcp;
using Synth.Api.Search;
using Synth.Api.Storage;

namespace Synth.Mcp.Stdio;

/// <summary>
/// Builds the generic host for Synth's stdio MCP server. Reuses exactly the search-layer
/// registrations Synth.Api uses (<see cref="EmbeddingServiceExtensions.AddSynthEmbeddings"/>,
/// <see cref="VectorStoreServiceExtensions.AddSynthVectorStore"/>,
/// <see cref="SearchServiceExtensions.AddSynthSearch"/>) and the same transport-agnostic
/// <see cref="CodeSearchTool"/>, but serves it over the MCP <c>stdio</c> transport instead of
/// HTTP — the transport most local MCP clients (editors, CLI agents) expect for a process they
/// spawn directly. The host wiring is factored out here (rather than inline in Program.cs) so a
/// test can build the container and assert the tool is registered without spawning the process.
/// </summary>
public static class StdioMcpHost
{
    /// <summary>
    /// Creates and configures the host builder. Callers <c>Build().Run()</c> it; tests
    /// <c>Build()</c> it and inspect DI. Like Synth.Api, the embedding generator and vector
    /// store clients are constructed lazily, so this needs no live Ollama/Qdrant to build.
    /// </summary>
    public static HostApplicationBuilder CreateBuilder(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        // stdout is the MCP protocol channel on the stdio transport — anything written there
        // that isn't a protocol message corrupts the stream. Route all logs to stderr instead.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

        // Same search stack as Synth.Api: Ollama-backed embeddings, Qdrant-or-Local vector
        // store, and the rerank/dedup CodeSearchService the tool resolves per invocation.
        builder.AddSynthEmbeddings();
        builder.AddSynthVectorStore();
        builder.AddSynthSearch();

        // Same call-graph store as Synth.Api (Mongo-or-in-memory) so the find_callers/find_callees
        // tools resolve ICodeGraphStore over stdio just as they do over HTTP.
        builder.AddSynthCodeGraph();

        // MCP server over stdio (not HTTP): the same `search_code` + call-graph tools, spawned by
        // the client on stdin/stdout. No HTTP endpoints are mapped in this process.
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<CodeSearchTool>()
            .WithTools<CallGraphTool>();

        return builder;
    }
}
