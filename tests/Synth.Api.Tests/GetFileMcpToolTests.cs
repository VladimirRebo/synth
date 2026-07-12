using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Synth.Api.Mcp;
using Synth.Core;
using Synth.Domain.Vcs;
using Synth.Infrastructure.Vcs;
using Synth.Domain;

namespace Synth.Api.Tests;

// Proves SYNTH-42 (part 2): the `get_file` MCP tool reads a file by repository-relative path for a
// local-indexed collection, guards against path traversal, rejects an unknown collection, and
// enforces the 10 MB size limit. Uses a throwaway temp-directory fixture, not real repo files.
public sealed class GetFileMcpToolTests : IDisposable
{
    private readonly string _repoDir;
    private readonly InMemoryRepositoryRegistry _registry = new();
    private readonly GitRepoService _gitRepoService =
        new(new MutableOptionsMonitor<VcsOptions>(new VcsOptions()));

    private const string LocalCollection = "local-fixture";

    public GetFileMcpToolTests()
    {
        _repoDir = Directory.CreateTempSubdirectory("synth-get-file-tool-test-").FullName;
        _registry.UpsertAsync(new RepositoryEntry
        {
            Collection = LocalCollection,
            SourceType = "local",
            Source = _repoDir,
            LastIndexedAt = DateTime.UtcNow,
        }).GetAwaiter().GetResult();
    }

    public void Dispose() => Directory.Delete(_repoDir, recursive: true);

    private Task<string> Invoke(string relativePath, string? collection = LocalCollection) =>
        GetFileTool.GetFileAsync(_registry, _gitRepoService, relativePath, collection);

    [Fact]
    public async Task Get_file_reads_a_file_content_for_a_local_collection()
    {
        var nested = Path.Combine(_repoDir, "src");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(Path.Combine(nested, "Greeter.cs"), "public class Greeter { }");

        var content = await Invoke("src/Greeter.cs");

        Assert.Equal("public class Greeter { }", content);
    }

    [Fact]
    public async Task Get_file_rejects_a_relative_path_that_escapes_the_root()
    {
        // A parent-dir traversal that resolves outside the repo root must be rejected, even if the
        // target file exists on disk.
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            Invoke("../../../etc/passwd"));

        Assert.Contains("escapes", ex.Message);
    }

    [Fact]
    public async Task Get_file_rejects_an_unknown_collection()
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            Invoke("whatever.cs", collection: "no-such-collection"));

        Assert.Contains("Unknown collection", ex.Message);
    }

    [Fact]
    public async Task Get_file_rejects_a_missing_file()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() => Invoke("does-not-exist.cs"));
    }

    [Fact]
    public async Task Get_file_rejects_a_file_over_the_size_limit()
    {
        // Create a sparse file just past the 10 MB limit without writing 10 MB of data.
        var big = Path.Combine(_repoDir, "big.bin");
        using (var stream = File.Create(big))
            stream.SetLength(GetFileTool.MaxFileSizeBytes + 1);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke("big.bin"));

        Assert.Contains("too large", ex.Message);
    }

    [Fact]
    public void Get_file_tool_is_registered_on_the_mcp_server()
    {
        using var factory = new WebApplicationFactory<Program>();

        var tools = factory.Services.GetServices<McpServerTool>();

        Assert.Contains(tools, tool => tool.ProtocolTool.Name == "get_file");
    }
}
