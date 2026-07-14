using Synth.Chunkers.Go;
using Synth.Domain;

namespace Synth.Chunkers.Go.Tests;

public class GoChunkerTests
{
    private static readonly GoChunker Chunker = new();

    private static IReadOnlyList<CodeChunk> ChunkGo(string source) =>
        Chunker.Chunk("/repo/sample.go", "sample.go", source);

    [Theory]
    [InlineData("/repo/main.go", true)]
    [InlineData("/repo/main.GO", true)]
    [InlineData("/repo/main.py", false)]
    [InlineData("", false)]
    public void CanHandle_AcceptsOnlyGoFiles(string path, bool expected) =>
        Assert.Equal(expected, Chunker.CanHandle(path));

    [Fact]
    public void TopLevelFunctions_EmitOneChunkPerFunction()
    {
        const string source = """
            package main

            func alpha() int {
                return 1
            }

            func beta(x int) int {
                return x * 2
            }
            """;

        var chunks = ChunkGo(source);

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
    public void MethodWithReceiver_CapturesReceiverTypeAsClassName()
    {
        const string source = """
            package main

            func (s *Service) Handle() {
                s.doWork()
            }
            """;

        var chunk = Assert.Single(ChunkGo(source));
        Assert.Equal(ChunkType.Method, chunk.ChunkType);
        Assert.Equal("Service", chunk.ClassName);
        Assert.Equal("Handle", chunk.MethodName);
    }

    [Theory]
    [InlineData("type Shape struct {\n\tArea float64\n}", ChunkType.Struct, "Shape")]
    [InlineData("type Greeter interface {\n\tGreet() string\n}", ChunkType.Interface, "Greeter")]
    public void TypeDeclarations_MapToMatchingChunkType(string decl, ChunkType expected, string name)
    {
        var chunk = Assert.Single(ChunkGo($"package main\n\n{decl}"));
        Assert.Equal(expected, chunk.ChunkType);
        Assert.Equal(name, chunk.ClassName);
    }

    [Fact]
    public void FileWithNoRecognizedDeclarations_FallsBackToOneWholeFileChunk()
    {
        const string source = """
            package main

            var x = 1
            const y = 2
            """;

        var chunk = Assert.Single(ChunkGo(source));
        Assert.Equal(ChunkType.Method, chunk.ChunkType);
        Assert.Contains("const y = 2", chunk.Content);
        Assert.Equal(1, chunk.StartLine);
    }
}
