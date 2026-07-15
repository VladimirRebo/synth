using Synth.Chunkers.Python;
using Synth.Domain;
using Synth.Domain.Graph;

namespace Synth.Chunkers.Python.Tests;

public class PythonChunkerTests
{
    private static readonly PythonChunker Chunker = new();

    private static IReadOnlyList<CodeChunk> ChunkPy(string source) =>
        Chunker.Chunk("/repo/src/sample.py", "src/sample.py", source);

    private static IReadOnlyList<RawCallSite> ExtractCallSites(string source) =>
        Chunker.ExtractCallSites("/repo/src/sample.py", "src/sample.py", source);

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

    // --- Call-site extraction: raw, unresolved invocations per top-level function/class. ---

    [Fact]
    public void ExtractCallSites_RecordsCallerNameInvokedNameAndLine()
    {
        const string source = """
            def handle():
                work()
            """;

        var site = Assert.Single(ExtractCallSites(source), s => s.InvokedName == "work");
        Assert.Equal("handle", site.CallerQualifiedName);
        Assert.Equal("src/sample.py", site.SourceFile);
        Assert.Equal(2, site.Line);
    }

    [Fact]
    public void ExtractCallSites_TakesLastSegmentOfADottedCall()
    {
        const string source = """
            def handle(repo):
                self.local()
                repo.load()
                a.b.c.deep()
            """;

        var invoked = ExtractCallSites(source).Select(s => s.InvokedName).ToList();
        Assert.Contains("local", invoked);
        Assert.Contains("load", invoked);
        Assert.Contains("deep", invoked);
    }

    [Fact]
    public void ExtractCallSites_AttributesCallsInsideAClassToTheClassAsAWhole()
    {
        // Nested methods never get their own chunk, so they can never be resolved as a callee either —
        // attributing their calls to the enclosing class (its actual QualifiedName) is the only caller
        // identity IndexingPipeline could ever match. This also guards a regression: since the whole
        // class span (including both nested "def handle("/"def work(" signature lines) is scanned for
        // invocations, a naive scan would misread each nested method's own declaration as the class
        // calling a method of the same name — the assert below on the full site list (not just
        // Contains) locks in that only the real self.work() call is ever reported, nothing named
        // "handle" or "work" from either signature line.
        const string source = """
            class Service:
                def handle(self):
                    self.work()

                def work(self):
                    pass
            """;

        var site = Assert.Single(ExtractCallSites(source));
        Assert.Equal("Service", site.CallerQualifiedName);
        Assert.Equal("work", site.InvokedName);
    }

    [Fact]
    public void ExtractCallSites_DoesNotTreatTheDeclarationsOwnNameAsACallToItself()
    {
        const string source = """
            def standalone():
                pass
            """;

        Assert.Empty(ExtractCallSites(source));
    }

    [Fact]
    public void ExtractCallSites_DoesNotTreatKeywordsFollowedByParensAsInvocations()
    {
        const string source = """
            def handle():
                try:
                    work()
                except (TypeError, ValueError):
                    return (None)
            """;

        var invoked = ExtractCallSites(source).Select(s => s.InvokedName).ToList();
        Assert.Contains("work", invoked);
        Assert.DoesNotContain("except", invoked);
        Assert.DoesNotContain("return", invoked);
    }

    [Fact]
    public void ExtractCallSites_EmitsNothingForFileWithoutInvocations()
    {
        const string source = """
            def compute():
                x = 1
                return x
            """;

        Assert.Empty(ExtractCallSites(source));
    }
}
