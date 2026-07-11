using Synth.Api.Mcp;
using Synth.Core;
using Synth.Domain;

namespace Synth.Api.Tests;

// SYNTH-40: CodeSearchResult.From must carry the chunk's SourceUrl through to GET /search and the
// search_code MCP tool (both project via From), the same way Score already flows through.
public class CodeSearchResultTests
{
    [Fact]
    public void From_maps_source_url_when_present()
    {
        var chunk = new CodeChunk
        {
            RelativePath = "src/App.cs",
            StartLine = 10,
            EndLine = 20,
            SourceUrl = "https://github.com/owner/repo/blob/main/src/App.cs#L10-L20",
        };

        var result = CodeSearchResult.From(new ScoredCodeChunk(chunk, 0.5));

        Assert.Equal("https://github.com/owner/repo/blob/main/src/App.cs#L10-L20", result.SourceUrl);
    }

    [Fact]
    public void From_maps_null_source_url_for_local_path_chunks()
    {
        var chunk = new CodeChunk { RelativePath = "src/App.cs", StartLine = 1, EndLine = 2 };

        var result = CodeSearchResult.From(new ScoredCodeChunk(chunk, 0.5));

        Assert.Null(result.SourceUrl);
    }
}
