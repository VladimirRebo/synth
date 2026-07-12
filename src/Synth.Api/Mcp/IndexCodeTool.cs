using System.ComponentModel;
using ModelContextProtocol.Server;
using Synth.Api.Indexing;
using Synth.Application.Indexing;
using Synth.Application;
using Synth.Domain.Vcs;
using Synth.Infrastructure.Vcs;
using Synth.Domain;

namespace Synth.Api.Mcp;

/// <summary>
/// Transport-agnostic MCP tool that lets an agent trigger (re)indexing of a repository — the MCP
/// equivalent of <c>POST /index</c> (SYNTH-36, part of issue #48). Mirrors
/// <see cref="CodeSearchTool"/>'s shape (the wrapping lives here; the actual flow stays in
/// <see cref="IndexingEndpoints.StartIndexing"/>, shared with the REST endpoint) and its
/// fire-and-forget contract: the tool validates + reserves the job slot synchronously and returns
/// immediately with an <see cref="IndexCodeResult"/>, while the clone/index work runs in the
/// background. Callers observe progress via the status surface, not this tool's return value.
/// Registered via <c>AddMcpServer().WithTools&lt;IndexCodeTool&gt;()</c> over both the HTTP and stdio
/// transports, the same way <c>search_code</c> already is.
/// </summary>
[McpServerToolType]
public sealed class IndexCodeTool
{
    /// <summary>
    /// Starts an indexing job. The service dependencies are injected from DI per invocation; the
    /// <paramref name="path"/>/<paramref name="repoUrl"/>/<paramref name="branch"/> arguments mirror
    /// <c>POST /index</c>'s request body exactly.
    /// </summary>
    [McpServerTool(Name = "index_code")]
    [Description(
        "Trigger (re)indexing of a repository so its code becomes searchable via search_code and " +
        "the call-graph tools. Fire-and-forget: returns immediately once the job has started (it " +
        "does NOT wait for indexing to finish). Provide exactly one of 'path' or 'repoUrl'.")]
    public static IndexCodeResult IndexCode(
        IndexingPipeline pipeline,
        GitRepoService gitRepoService,
        IRepositoryRegistry registry,
        IIndexJobTracker tracker,
        ILoggerFactory loggerFactory,
        [Description(
            "Absolute path to a local directory to index into the main codebase collection. " +
            "Provide this OR repoUrl, not both.")]
        string? path = null,
        [Description(
            "Remote git URL to clone/fetch and index into its own per-repo collection. " +
            "Provide this OR path, not both.")]
        string? repoUrl = null,
        [Description(
            "Branch to index; only applies to the repoUrl case. Leave unset for the repository's " +
            "default branch.")]
        string? branch = null)
    {
        var request = new IndexRequest(path, repoUrl, branch);
        var outcome = IndexingEndpoints.StartIndexing(
            request, pipeline, gitRepoService, registry, tracker, loggerFactory);
        return IndexCodeResult.From(outcome);
    }
}
