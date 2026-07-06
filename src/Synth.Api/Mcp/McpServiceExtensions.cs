using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Synth.Api.Mcp;

/// <summary>
/// DI wiring for Synth's MCP layer: registers an MCP server with the HTTP (streamable)
/// transport and the transport-agnostic <see cref="CodeSearchTool"/>. Depends on the search
/// layer (<c>AddSynthSearch</c>) registered earlier, since the tool resolves
/// <see cref="Synth.Core.CodeSearchService"/> from DI. The HTTP endpoints are mapped by
/// <c>app.MapMcp()</c> in Program.cs.
/// </summary>
public static class McpServiceExtensions
{
    public static IHostApplicationBuilder AddSynthMcp(this IHostApplicationBuilder builder)
    {
        builder.Services
            .AddMcpServer()
            .WithHttpTransport()
            .WithTools<CodeSearchTool>();

        return builder;
    }
}
