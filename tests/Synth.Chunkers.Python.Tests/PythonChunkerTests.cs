using Synth.Chunkers.Python;
using Synth.Domain;

namespace Synth.Chunkers.Python.Tests;

public class PythonChunkerTests
{
    private static readonly PythonChunker Chunker = new();

    private static IReadOnlyList<CodeChunk> ChunkPy(string source) =>
        Chunker.Chunk("/repo/src/sample.py", "src/sample.py", source);

    [Theory]
    [InlineData("/repo/src/foo.py", true)]
    [InlineData("/repo/src/foo.PY", true)]
    [InlineData("/repo/src/foo.pyi", false)]
    [InlineData("/repo/src/foo.ts", false)]
    [InlineData("", false)]
    public void CanHandle_AcceptsOnlyPyFiles(string path, bool expected) =>
        Assert.Equal(expected, Chunker.CanHandle(path));

    [Fact]
    public void TopLevelFunctions_EmitOneChunkPerFunction()
    {
        const string source = """
            def alpha():
                return 1


            def beta(x):
                return x * 2
            """;

        var chunks = ChunkPy(source);

        Assert.Equal(2, chunks.Count);
        Assert.All(chunks, c => Assert.Equal(ChunkType.Method, c.ChunkType));

        var alpha = Assert.Single(chunks, c => c.MethodName == "alpha");
        Assert.Contains("return 1", alpha.Content);
        Assert.DoesNotContain("return x * 2", alpha.Content);

        var beta = Assert.Single(chunks, c => c.MethodName == "beta");
        Assert.Contains("return x * 2", beta.Content);

        Assert.All(chunks, c => Assert.NotEmpty(c.FileHash));
        Assert.Single(chunks.Select(c => c.FileHash).Distinct());
    }

    [Fact]
    public void AsyncDef_IsChunkedLikeARegularFunction()
    {
        const string source = """
            async def fetch(url):
                return await get(url)
            """;

        var chunk = Assert.Single(ChunkPy(source));
        Assert.Equal(ChunkType.Method, chunk.ChunkType);
        Assert.Equal("fetch", chunk.MethodName);
    }

    [Fact]
    public void ClassDeclaration_MapsToClassChunkType()
    {
        const string source = """
            class Greeter:
                def greet(self, name):
                    return f"Hello, {name}"
            """;

        var chunk = Assert.Single(ChunkPy(source));
        Assert.Equal(ChunkType.Class, chunk.ChunkType);
        Assert.Equal("Greeter", chunk.ClassName);
        // The nested method stays embedded in the class chunk — only column-0 declarations split.
        Assert.Contains("def greet", chunk.Content);
    }

    [Fact]
    public void DecoratorLines_StayAttachedToTheDeclarationTheyDecorate()
    {
        const string source = """
            def alpha():
                return 1


            @staticmethod
            @cached
            def beta():
                return 2
            """;

        var chunks = ChunkPy(source);

        var alpha = Assert.Single(chunks, c => c.MethodName == "alpha");
        Assert.DoesNotContain("@staticmethod", alpha.Content);

        var beta = Assert.Single(chunks, c => c.MethodName == "beta");
        Assert.Contains("@staticmethod", beta.Content);
        Assert.Contains("@cached", beta.Content);
    }

    [Fact]
    public void LeadingDocstring_IsExtractedIntoSummary()
    {
        const string source = """"
            def greet(name):
                """Greets a person by name."""
                return f"Hello, {name}"
            """";

        var chunk = Assert.Single(ChunkPy(source));
        Assert.Equal("Greets a person by name.", chunk.Summary);
    }

    [Fact]
    public void NoDocstring_HasEmptySummary()
    {
        const string source = """
            def greet(name):
                return name
            """;

        var chunk = Assert.Single(ChunkPy(source));
        Assert.Empty(chunk.Summary);
    }

    [Fact]
    public void FileWithNoTopLevelDeclarations_FallsBackToOneWholeFileChunk()
    {
        const string source = """
            x = 1
            y = 2
            print(x + y)
            """;

        var chunk = Assert.Single(ChunkPy(source));
        Assert.Equal(ChunkType.Method, chunk.ChunkType);
        Assert.Contains("print(x + y)", chunk.Content);
        Assert.Equal(1, chunk.StartLine);
    }
}
