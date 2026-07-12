using System.ComponentModel;
using ModelContextProtocol.Server;
using Synth.Core;
using Synth.Domain.Vcs;
using Synth.Infrastructure.Vcs;
using Synth.Domain;

namespace Synth.Api.Mcp;

/// <summary>
/// Transport-agnostic MCP tool that reads a file's full content by repository-relative path (part of
/// issue #44). Complements <see cref="CodeSearchTool"/>/<see cref="GetSymbolTool"/>: once an agent has
/// found a relevant chunk it can pull the whole file for context. Resolves the collection's on-disk
/// root via the <see cref="IRepositoryRegistry"/> — the indexed path directly for a <c>local</c>
/// source, or <see cref="GitRepoService.ResolveCheckoutPath"/> for a cloned remote — then guards
/// against path traversal and a size ceiling before reading. Registered via
/// <c>AddMcpServer().WithTools&lt;GetFileTool&gt;()</c> over both the HTTP and stdio transports.
/// </summary>
[McpServerToolType]
public sealed class GetFileTool
{
    /// <summary>Maximum file size served (10 MB), matching Sonar's own <c>get_file</c> limit.</summary>
    public const long MaxFileSizeBytes = 10L * 1024 * 1024;

    /// <summary>
    /// Reads the file at <paramref name="relativePath"/> within <paramref name="collection"/>'s root.
    /// <paramref name="registry"/>/<paramref name="gitRepoService"/> are injected from DI per invocation.
    /// </summary>
    [McpServerTool(Name = "get_file")]
    [Description(
        "Read the full content of a file by its repository-relative path within an indexed collection " +
        "— useful once search_code or get_symbol has pointed you at a file and you want its whole " +
        "context. Rejects paths that escape the repository root and files larger than 10 MB.")]
    public static async Task<string> GetFileAsync(
        IRepositoryRegistry registry,
        GitRepoService gitRepoService,
        [Description("Repository-relative path of the file to read, e.g. \"src/Foo/Bar.cs\".")]
        string relativePath,
        [Description(
            "Name of the indexed collection (repository) to read from. Leave unset to read from the " +
            "main indexed codebase.")]
        string? collection = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException("'relativePath' is required.");

        var target = string.IsNullOrWhiteSpace(collection) ? CollectionNames.Default : collection;

        var entries = await registry.ListAsync(cancellationToken);
        var entry = entries.FirstOrDefault(e => string.Equals(e.Collection, target, StringComparison.Ordinal))
            ?? throw new ArgumentException($"Unknown collection: '{target}'.");

        // Local sources are read from the indexed path directly; remote (github/gitlab) sources live
        // in the workspace checkout keyed by the collection slug (== collection name).
        var root = string.Equals(entry.SourceType, "local", StringComparison.Ordinal)
            ? entry.Source
            : gitRepoService.ResolveCheckoutPath(target);

        var rootFull = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(Path.Combine(rootFull, relativePath));

        // Path-traversal guard: the resolved path must stay inside the root. Anchoring the prefix
        // check on a trailing separator stops a sibling like "root-evil" from passing as "root".
        var rootWithSeparator = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.Ordinal))
            throw new ArgumentException($"Path escapes the repository root: '{relativePath}'.");

        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"File not found: '{relativePath}'.");

        // Check the size before reading so an oversized file is rejected without being pulled into
        // memory first.
        var length = new FileInfo(fullPath).Length;
        if (length > MaxFileSizeBytes)
            throw new InvalidOperationException(
                $"File is too large ({length} bytes); the limit is {MaxFileSizeBytes} bytes.");

        return await File.ReadAllTextAsync(fullPath, cancellationToken);
    }
}
