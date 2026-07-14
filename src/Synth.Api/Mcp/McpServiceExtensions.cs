using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Synth.Api.Graph;

namespace Synth.Api.Mcp;

/// <summary>
/// DI wiring for Synth's MCP layer: registers an MCP server with the HTTP (streamable)
/// transport and the transport-agnostic tools — <see cref="CodeSearchTool"/> (vector search) and
/// <see cref="CallGraphTool"/> (structural call graph). Depends on the search and call-graph layers
/// (<c>AddSynthSearch</c>/<c>AddSynthCodeGraph</c>) registered earlier, since the tools resolve
/// <see cref="Synth.Application.CodeSearchService"/> / <see cref="Synth.Domain.Graph.ICodeGraphStore"/> from
/// DI. The HTTP endpoints are mapped by <c>app.MapMcp()</c> in Program.cs.
/// </summary>
public static class McpServiceExtensions
{
    public static IHostApplicationBuilder AddSynthMcp(this IHostApplicationBuilder builder)
    {
        builder.Services
            .AddMcpServer()
            .WithHttpTransport()
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
