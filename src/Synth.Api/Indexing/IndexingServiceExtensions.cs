using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Synth.Core;

namespace Synth.Api.Indexing;

/// <summary>
/// DI wiring for the indexing pipeline: registers the available <see cref="IFileChunker"/>s
/// and the <see cref="IndexingPipeline"/> that walks a directory, chunks + embeds each file
/// and upserts the chunks into the registered <see cref="ICodeChunkStore"/>. Depends on the
/// embedding generator and vector store registered by <c>AddSynthEmbeddings</c> /
/// <c>AddSynthVectorStore</c>.
/// </summary>
public static class IndexingServiceExtensions
{
    public static IHostApplicationBuilder AddSynthIndexing(this IHostApplicationBuilder builder)
    {
        // Only the C# Roslyn chunker exists today; the pipeline picks the first whose
        // CanHandle matches, so more languages can be registered here later.
        builder.Services.AddSingleton<IFileChunker, CSharpRoslynChunker>();
        builder.Services.AddSingleton<IndexingPipeline>();

        return builder;
    }
}
