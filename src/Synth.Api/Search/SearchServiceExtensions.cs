using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Synth.Core;
using Synth.Domain;

namespace Synth.Api.Search;

/// <summary>
/// DI wiring for Synth's search layer: registers the <see cref="QueryExpander"/> and the
/// <see cref="CodeSearchService"/> that reranks/dedups results on top of the vector store.
/// Depends on the embedding generator (<c>AddSynthEmbeddings</c>) and vector store
/// (<c>AddSynthVectorStore</c>) registered earlier in the pipeline.
/// </summary>
public static class SearchServiceExtensions
{
    public static IHostApplicationBuilder AddSynthSearch(this IHostApplicationBuilder builder)
    {
        builder.Services.AddSingleton<QueryExpander>();
        builder.Services.AddSingleton<CodeSearchService>();

        return builder;
    }
}
