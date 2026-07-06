using Synth.Core;

namespace Synth.Core.Tests;

public class CSharpRoslynChunkerTests
{
    private static readonly CSharpRoslynChunker Chunker = new();

    private static IReadOnlyList<CodeChunk> ChunkSource(string source) =>
        Chunker.Chunk("/repo/src/Sample.cs", "src/Sample.cs", source);

    [Theory]
    [InlineData("/repo/src/Foo.cs", true)]
    [InlineData("/repo/src/Foo.CS", true)]
    [InlineData("/repo/README.md", false)]
    [InlineData("/repo/src/foo.ts", false)]
    [InlineData("", false)]
    public void CanHandle_AcceptsOnlyCSharpFiles(string path, bool expected) =>
        Assert.Equal(expected, Chunker.CanHandle(path));

    [Fact]
    public void SimpleClassWithShortMethod_EmitsClassAndMethodChunks()
    {
        const string source = """
            namespace Acme.Widgets;

            public class Greeter
            {
                public string Greet(string name)
                {
                    return $"Hello, {name}";
                }
            }
            """;

        var chunks = ChunkSource(source);

        var classChunk = Assert.Single(chunks, c => c.ChunkType == ChunkType.Class);
        Assert.Equal("Acme.Widgets", classChunk.Namespace);
        Assert.Equal("Greeter", classChunk.ClassName);
        Assert.Contains("public string Greet", classChunk.Content);
        Assert.True(classChunk.StartLine >= 1);
        Assert.True(classChunk.EndLine >= classChunk.StartLine);

        var methodChunk = Assert.Single(chunks, c => c.ChunkType == ChunkType.Method);
        Assert.Equal("Acme.Widgets", methodChunk.Namespace);
        Assert.Equal("Greeter", methodChunk.ClassName);
        Assert.Equal("Greet", methodChunk.MethodName);
        Assert.Contains("Hello", methodChunk.Content);

        // Every chunk from a file shares the same content hash.
        Assert.All(chunks, c => Assert.NotEmpty(c.FileHash));
        Assert.Single(chunks.Select(c => c.FileHash).Distinct());
    }

    [Fact]
    public void Constructor_IsEmittedAsConstructorChunk()
    {
        const string source = """
            namespace Acme;

            public class Service
            {
                public Service(int seed) { _seed = seed; }
                private readonly int _seed;
            }
            """;

        var chunks = ChunkSource(source);

        var ctor = Assert.Single(chunks, c => c.ChunkType == ChunkType.Constructor);
        Assert.Equal("Service", ctor.MethodName);
        Assert.Equal("Service", ctor.ClassName);
    }

    [Theory]
    [InlineData("interface IThing { }", ChunkType.Interface)]
    [InlineData("record Point(int X, int Y);", ChunkType.Record)]
    [InlineData("struct Vec { public int X; }", ChunkType.Struct)]
    [InlineData("class Plain { }", ChunkType.Class)]
    public void TypeDeclarations_MapToMatchingChunkType(string decl, ChunkType expected)
    {
        var chunks = ChunkSource($"namespace N;\n\npublic {decl}");

        var typeChunk = Assert.Single(chunks, c => c.ChunkType == expected);
        Assert.Equal("N", typeChunk.Namespace);
    }

    [Fact]
    public void LongMethod_IsSplitIntoHeadAndBody()
    {
        var body = string.Join('\n', Enumerable.Range(1, 350).Select(i => $"        var x{i} = {i};"));
        var source = $$"""
            namespace Big;

            public class Huge
            {
                public void DoLots()
                {
            {{body}}
                }
            }
            """;

        var chunks = ChunkSource(source);

        // No plain Method chunk for a long method — it is split instead.
        Assert.DoesNotContain(chunks, c => c.ChunkType == ChunkType.Method);

        var head = Assert.Single(chunks, c => c.ChunkType == ChunkType.MethodHead);
        var tail = Assert.Single(chunks, c => c.ChunkType == ChunkType.MethodBody);

        Assert.Equal("DoLots", head.MethodName);
        Assert.Equal("DoLots", tail.MethodName);

        // Head keeps the first MethodHeadLines lines; body carries the remainder.
        Assert.Equal(CSharpRoslynChunker.MethodHeadLines, head.Content.Split('\n').Length);
        Assert.Contains("var x1 =", head.Content);
        Assert.DoesNotContain("var x350 =", head.Content);
        Assert.Contains("var x350 =", tail.Content);

        // Line ranges are contiguous across the split.
        Assert.Equal(head.EndLine + 1, tail.StartLine);
    }

    [Fact]
    public void XmlDocComment_IsExtractedIntoSummary()
    {
        const string source = """
            namespace Docs;

            public class Calculator
            {
                /// <summary>
                /// Adds two numbers together.
                /// </summary>
                public int Add(int a, int b) => a + b;
            }
            """;

        var chunks = ChunkSource(source);

        var method = Assert.Single(chunks, c => c.ChunkType == ChunkType.Method);
        Assert.Equal("Adds two numbers together.", method.Summary);
    }

    [Fact]
    public void MethodWithoutDocComment_HasEmptySummary()
    {
        const string source = """
            namespace Docs;

            public class Calculator
            {
                public int Sub(int a, int b) => a - b;
            }
            """;

        var method = Assert.Single(ChunkSource(source), c => c.ChunkType == ChunkType.Method);
        Assert.Empty(method.Summary);
    }

    [Fact]
    public void FileScopedAndBlockNamespaces_AreBothHandled()
    {
        const string blockNs = """
            namespace Block
            {
                public class Inside { public void M() { } }
            }
            """;

        var classChunk = Assert.Single(ChunkSource(blockNs), c => c.ChunkType == ChunkType.Class);
        Assert.Equal("Block", classChunk.Namespace);
    }
}
