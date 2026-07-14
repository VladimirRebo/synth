using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Synth.Application;
using Synth.Infrastructure.Configuration;
using Synth.Infrastructure.Embeddings;
using Synth.Api.Graph;
using Synth.Infrastructure.Health;
using Synth.Api.Indexing;
using Synth.Api.Mcp;
using Synth.Api.Search;
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

        // Same layered config store as Synth.Api (~/.synth/config.json over IConfiguration) so
        // settings written via the REST API (VCS tokens, embedding config) are picked up here too,
        // instead of this process only ever seeing appsettings/env defaults.
        builder.AddSynthConfigStore();

        // Same search stack as Synth.Api: Ollama-backed embeddings, Qdrant-or-Local vector
        // store, and the rerank/dedup CodeSearchService the tool resolves per invocation.
        builder.AddSynthEmbeddings();
        builder.AddSynthVectorStore();
        builder.AddSynthSearch();

        // Same VCS stack as Synth.Api: GitRepoService/IGitRepoService, VcsOptions binding, and the
        // SQLite-backed IRepositoryRegistry (shared ~/.synth/synth.db) — so list_collections and
        // delete_collection see exactly the repositories Synth.Api's own indexing has recorded,
        // instead of an empty in-memory registry private to this process.
        builder.AddSynthVcs();

        // Same call-graph store as Synth.Api (SQLite-backed, shared ~/.synth/synth.db) so the
        // find_callers/find_callees tools resolve ICodeGraphStore over stdio just as they do over
        // HTTP. AddSynthCodeGraph's SqliteConnectionFactory registration is idempotent with
        // AddSynthVcs's (TryAddSingleton on both sides), so call order doesn't matter.
        builder.AddSynthCodeGraph();

        // Indexing stack so the `index_code` tool (SYNTH-36) can resolve its dependencies over stdio
        // the same way it does over HTTP: the pipeline + the single job tracker come from
        // AddSynthIndexing; GitRepoService/IGitRepoService and the repository registry now come from
        // AddSynthVcs above, and the delete_collection command handler is registered there too.
        builder.AddSynthIndexing();

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
