using Synth.Chunkers.Go;
using Synth.Domain;
using Synth.Domain.Graph;

namespace Synth.Chunkers.Go.Tests;

public class GoChunkerTests
{
    private static readonly GoChunker Chunker = new();

    private static IReadOnlyList<CodeChunk> ChunkGo(string source) =>
        Chunker.Chunk("/repo/sample.go", "sample.go", source);

    private static IReadOnlyList<RawCallSite> ExtractCallSites(string source) =>
        Chunker.ExtractCallSites("/repo/sample.go", "sample.go", source);

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

    // --- Call-site extraction: raw, unresolved invocations per top-level func. ---

    [Fact]
    public void ExtractCallSites_RecordsCallerNameInvokedNameAndLine()
    {
        const string source = """
            package main

            func handle() {
                work()
            }
            """;

        var site = Assert.Single(ExtractCallSites(source), s => s.InvokedName == "work");
        Assert.Equal("handle", site.CallerQualifiedName);
        Assert.Equal("sample.go", site.SourceFile);
        Assert.Equal(4, site.Line);
    }

    [Fact]
    public void ExtractCallSites_MethodWithReceiver_QualifiesCallerByReceiverType()
    {
        const string source = """
            package main

            func (s *Service) Handle() {
                s.doWork()
            }
            """;

        var site = Assert.Single(ExtractCallSites(source), s => s.InvokedName == "doWork");
        Assert.Equal("Service.Handle", site.CallerQualifiedName);
    }

    [Fact]
    public void ExtractCallSites_TakesLastSegmentOfADottedCall()
    {
        const string source = """
            package main

            func handle(repo *Repo) {
                repo.Load()
                pkg.sub.Deep()
            }
            """;

        var invoked = ExtractCallSites(source).Select(s => s.InvokedName).ToList();
        Assert.Contains("Load", invoked);
        Assert.Contains("Deep", invoked);
    }

    [Fact]
    public void ExtractCallSites_DoesNotTreatTheFunctionsOwnNameAsACallToItself()
    {
        const string source = """
            package main

            func standalone() {
            }
            """;

        Assert.Empty(ExtractCallSites(source));
    }

    [Fact]
    public void ExtractCallSites_SkipsStructAndInterfaceDeclarations()
    {
        // Neither has a callable body, so nothing should be attributed to them as a caller.
        const string source = """
            package main

            type Shape struct {
                Area float64
            }

            type Greeter interface {
                Greet() string
            }
            """;

        Assert.Empty(ExtractCallSites(source));
    }

    [Fact]
    public void ExtractCallSites_EmitsNothingForFileWithoutInvocations()
    {
        const string source = """
            package main

            func compute() int {
                x := 1
                return x
            }
            """;

        Assert.Empty(ExtractCallSites(source));
    }

    [Fact]
    public void LongStruct_IsSplitIntoHeadAndBody()
    {
        var fields = string.Join('\n', Enumerable.Range(1, 350).Select(i => $"\tField{i} int"));
        var source = $"package main\n\ntype Huge struct {{\n{fields}\n}}\n";

        var chunks = ChunkGo(source);

        Assert.DoesNotContain(chunks, c => c.ChunkType == ChunkType.Struct);
        var head = Assert.Single(chunks, c => c.ChunkType == ChunkType.TypeHead);
        var body = Assert.Single(chunks, c => c.ChunkType == ChunkType.TypeBody);
        Assert.Equal("Huge", head.ClassName);
        Assert.Equal("Huge", body.ClassName);
        Assert.Equal(GoChunker.ChunkHeadLines, head.Content.Split('\n').Length);
        Assert.Contains("Field1 int", head.Content);
        Assert.DoesNotContain("Field350 int", head.Content);
        Assert.Contains("Field350 int", body.Content);
        Assert.Equal(head.EndLine + 1, body.StartLine);
    }

    [Fact]
    public void LongFunction_IsSplitIntoHeadAndBody()
    {
        var body = string.Join('\n', Enumerable.Range(1, 350).Select(i => $"\tx{i} := {i}"));
        var source = $"package main\n\nfunc huge() {{\n{body}\n}}\n";

        var chunks = ChunkGo(source);

        Assert.DoesNotContain(chunks, c => c.ChunkType == ChunkType.Method);
        var head = Assert.Single(chunks, c => c.ChunkType == ChunkType.MethodHead);
        var tail = Assert.Single(chunks, c => c.ChunkType == ChunkType.MethodBody);
        Assert.Equal("huge", head.MethodName);
        Assert.Equal("huge", tail.MethodName);
        Assert.Equal(head.EndLine + 1, tail.StartLine);
    }
}
