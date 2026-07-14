using Synth.Chunkers.Docs;
using Synth.Domain;

namespace Synth.Chunkers.Docs.Tests;

public class DocsChunkerTests
{
    private static readonly DocsChunker Chunker = new();

    private static IReadOnlyList<CodeChunk> ChunkMd(string source) =>
        Chunker.Chunk("/repo/docs/guide.md", "docs/guide.md", source);

    private static IReadOnlyList<CodeChunk> ChunkYaml(string source) =>
        Chunker.Chunk("/repo/config.yml", "config.yml", source);

    private static IReadOnlyList<CodeChunk> ChunkJson(string source) =>
        Chunker.Chunk("/repo/package.json", "package.json", source);

    [Theory]
    [InlineData("/repo/README.md", true)]
    [InlineData("/repo/config.yml", true)]
    [InlineData("/repo/config.yaml", true)]
    [InlineData("/repo/package.json", true)]
    [InlineData("/repo/README.MD", true)]
    [InlineData("/repo/src/Foo.cs", false)]
    [InlineData("", false)]
    public void CanHandle_AcceptsMarkdownYamlAndJson(string path, bool expected) =>
        Assert.Equal(expected, Chunker.CanHandle(path));

    [Fact]
    public void Markdown_SplitsAtHeadingBoundaries()
    {
        const string source = """
            # Title

            Intro text.

            ## Section One

            Body one.

            ## Section Two

            Body two.
            """;

        var chunks = ChunkMd(source);

        Assert.Equal(3, chunks.Count);
        Assert.All(chunks, c => Assert.Equal(ChunkType.Markdown, c.ChunkType));

        var sectionOne = Assert.Single(chunks, c => c.Content.Contains("Body one."));
        Assert.DoesNotContain("Body two.", sectionOne.Content);
        Assert.StartsWith("## Section One", sectionOne.Content);
    }

    [Fact]
    public void Markdown_WithNoHeadings_FallsBackToOneWholeFileChunk()
    {
        const string source = "Just a paragraph, no headings at all.";

        var chunk = Assert.Single(ChunkMd(source));
        Assert.Equal(ChunkType.Markdown, chunk.ChunkType);
        Assert.Equal(source, chunk.Content);
    }

    [Fact]
    public void Yaml_SplitsAtTopLevelKeys()
    {
        const string source = """
            name: synth
            settings:
              nested: true
              other: false
            version: 1.0
            """;

        var chunks = ChunkYaml(source);

        Assert.Equal(3, chunks.Count);
        Assert.All(chunks, c => Assert.Equal(ChunkType.Property, c.ChunkType));

        var settings = Assert.Single(chunks, c => c.MethodName == "settings");
        Assert.Contains("nested: true", settings.Content);
        Assert.Contains("other: false", settings.Content);
        // The nested keys stay embedded in the parent chunk — only column-0 keys split.
        Assert.DoesNotContain("version:", settings.Content);

        Assert.Contains(chunks, c => c.MethodName == "name");
        Assert.Contains(chunks, c => c.MethodName == "version");
    }

    [Fact]
    public void Json_IsAlwaysOneWholeFileChunk()
    {
        const string source = """
            {
              "name": "synth",
              "version": "1.0.0"
            }
            """;

        var chunk = Assert.Single(ChunkJson(source));
        Assert.Equal(ChunkType.Method, chunk.ChunkType);
        Assert.Contains("\"version\": \"1.0.0\"", chunk.Content);
        Assert.Equal(1, chunk.StartLine);
    }
}
