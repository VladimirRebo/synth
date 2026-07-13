using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Synth.Application;
using Synth.Application.Cqrs;
using Synth.Application.Vcs;
using Synth.Infrastructure.Embeddings;
using Synth.Api.Graph;
using Synth.Infrastructure.Health;
using Synth.Api.Indexing;
using Synth.Api.Mcp;
using Synth.Api.Search;
using Synth.Domain.Vcs;
using Synth.Infrastructure.Graph;
using Synth.Infrastructure.Storage;
using Synth.Infrastructure.Vcs;

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

        // Same call-graph store as Synth.Api (SQLite-backed, shared ~/.synth/synth.db) so the
        // find_callers/find_callees tools resolve ICodeGraphStore over stdio just as they do over
        // HTTP; AddSynthCodeGraph registers its own SqliteConnectionFactory (this host does not call
        // AddSynthVcs).
        builder.AddSynthCodeGraph();

        // Indexing stack so the `index_code` tool (SYNTH-36) can resolve its dependencies over stdio
        // the same way it does over HTTP: the pipeline + the single job tracker come from
        // AddSynthIndexing; GitRepoService and the repository registry are wired inline here rather
        // than via AddSynthVcs so the stdio host always pins the in-memory registry. No Mongo is
        // configured for the stdio process, so the in-memory registry is the right fallback.
        builder.AddSynthIndexing();
        builder.Services.Configure<VcsOptions>(builder.Configuration.GetSection(VcsOptions.SectionName));
        builder.Services.AddSingleton<GitRepoService>();
        // Same singleton behind the Application-layer port the index_code command handler depends on.
        builder.Services.AddSingleton<IGitRepoService>(sp => sp.GetRequiredService<GitRepoService>());
        builder.Services.AddSingleton<IRepositoryRegistry, InMemoryRepositoryRegistry>();
        // Same delete-collection command handler AddSynthVcs registers for Synth.Api, wired inline
        // here (this host doesn't call AddSynthVcs) so the delete_collection tool resolves it over
        // stdio just as it does over HTTP.
        builder.Services.AddSingleton<
            ICommandHandler<DeleteCollectionCommand, bool>, DeleteCollectionCommandHandler>();

        // Health checks so the `health_check` tool (SYNTH-43) resolves IHealthCheckService over stdio
        // just as it does over HTTP. Its Qdrant probe seam resolves QdrantClient lazily via GetService
        // (from AddSynthVectorStore), so with no live Qdrant configured for the stdio process it falls
        // back to the always-healthy NotConfiguredQdrantHealthProbe — SYNTH-35's own "not configured"
        // pattern; the embedding factory it also needs comes from AddSynthEmbeddings above.
        builder.Services.AddSynthHealthChecks();

        // MCP server over stdio (not HTTP): the same search_code + call-graph + index_code +
        // get_symbol + get_file + list_collections + delete_collection + health_check tools, spawned by
        // the client on stdin/stdout. list_collections/delete_collection resolve IRepositoryRegistry
        // (wired just above) + the chunk/graph stores; health_check resolves IHealthCheckService (wired
        // just above). No HTTP endpoints are mapped in this process.
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<CodeSearchTool>()
            .WithTools<CallGraphTool>()
            .WithTools<IndexCodeTool>()
            .WithTools<GetSymbolTool>()
            .WithTools<GetFileTool>()
            .WithTools<ListCollectionsTool>()
            .WithTools<DeleteCollectionTool>()
            .WithTools<HealthCheckTool>();

        return builder;
    }
}
