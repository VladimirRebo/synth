using Microsoft.Extensions.Hosting;

namespace Synth.Mcp.Stdio;

// Local stdio MCP host for Synth's `search_code` tool. Meant to be spawned directly by an MCP
// client (editor/CLI agent) that speaks the protocol over this process's stdin/stdout. Uses an
// explicit namespaced entry class (not top-level statements) so it doesn't emit a global
// `Program` type that would clash with Synth.Api's `Program` when both are referenced in tests.
internal static class Program
{
    private static void Main(string[] args) =>
        StdioMcpHost.CreateBuilder(args).Build().Run();
}
