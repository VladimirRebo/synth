using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Synth.Application;
using Synth.Application.Cqrs;
using Synth.Application.Indexing;
using Synth.Core;
using Synth.Domain;

namespace Synth.Api.Indexing;

/// <summary>
/// DI wiring for the indexing pipeline: registers the available <see cref="IFileChunker"/>s
/// and the <see cref="IndexingPipeline"/> that walks a directory, chunks + embeds each file
/// and upserts the chunks into the registered <see cref="ICodeChunkStore"/>. Depends on the
/// embedding generator, vector store and call-graph store registered by <c>AddSynthEmbeddings</c> /
/// <c>AddSynthVectorStore</c> / <c>AddSynthCodeGraph</c> — the pipeline constructor takes the
/// <c>ICodeGraphStore</c> by DI, so no extra wiring is needed here beyond those registrations.
/// </summary>
public static class IndexingServiceExtensions
{
    public static IHostApplicationBuilder AddSynthIndexing(this IHostApplicationBuilder builder)
    {
        // The pipeline picks the first chunker whose CanHandle matches; each owns disjoint
        // extensions (.cs vs .ts/.tsx/.vue), so registration order doesn't matter here.
        builder.Services.AddSingleton<IFileChunker, CSharpRoslynChunker>();
        builder.Services.AddSingleton<IFileChunker, TsVueChunker>();
        builder.Services.AddSingleton<IndexingPipeline>();

        // Single, process-lifetime job tracker so a client can poll indexing progress (issue #39).
        builder.Services.AddSingleton<IIndexJobTracker, InMemoryIndexJobTracker>();

        // CQRS command handler for the indexing flow (SYNTH-61, issue #82). Explicit one-line
        // registration, no scanning — POST /index and the index_code MCP tool resolve it by its
        // ICommandHandler<IndexRepositoryCommand, IndexStartOutcome> interface.
        builder.Services.AddSingleton<
            ICommandHandler<IndexRepositoryCommand, IndexStartOutcome>, IndexRepositoryCommandHandler>();

        return builder;
    }
}
