using Synth.Chunkers.TsVue;
using Synth.Domain;
using Synth.Domain.Graph;

namespace Synth.Chunkers.TsVue.Tests;

public class TsVueChunkerTests
{
    private static readonly TsVueChunker Chunker = new();

    private static IReadOnlyList<CodeChunk> ChunkTs(string source) =>
        Chunker.Chunk("/repo/src/sample.ts", "src/sample.ts", source);

    private static IReadOnlyList<CodeChunk> ChunkVue(string source) =>
        Chunker.Chunk("/repo/src/App.vue", "src/App.vue", source);

    private static IReadOnlyList<RawCallSite> ExtractCallSitesTs(string source) =>
        Chunker.ExtractCallSites("/repo/src/sample.ts", "src/sample.ts", source);

    private static IReadOnlyList<RawCallSite> ExtractCallSitesVue(string source) =>
        Chunker.ExtractCallSites("/repo/src/App.vue", "src/App.vue", source);

    [Theory]
    [InlineData("/repo/src/foo.ts", true)]
    [InlineData("/repo/src/foo.tsx", true)]
    [InlineData("/repo/src/App.vue", true)]
    [InlineData("/repo/src/App.VUE", true)]
    [InlineData("/repo/src/Foo.cs", false)]
    [InlineData("/repo/README.md", false)]
    [InlineData("/repo/src/foo.js", false)]
    [InlineData("", false)]
    public void CanHandle_AcceptsOnlyTsTsxVue(string path, bool expected) =>
        Assert.Equal(expected, Chunker.CanHandle(path));

    [Fact]
    public void TsFile_WithTopLevelFunctions_EmitsOneChunkPerFunction()
    {
        const string source = """
            export function alpha(): number {
                return 1;
            }

            export function beta(x: number): number {
                return x * 2;
            }
            """;

        var chunks = ChunkTs(source);

        Assert.Equal(2, chunks.Count);
        Assert.All(chunks, c => Assert.Equal(ChunkType.Method, c.ChunkType));

        var alpha = Assert.Single(chunks, c => c.MethodName == "alpha");
        Assert.Contains("return 1;", alpha.Content);
        Assert.DoesNotContain("return x * 2;", alpha.Content); // sliced at the next declaration.

        var beta = Assert.Single(chunks, c => c.MethodName == "beta");
        Assert.Contains("return x * 2;", beta.Content);

        // Every chunk from a file shares one content hash.
        Assert.All(chunks, c => Assert.NotEmpty(c.FileHash));
        Assert.Single(chunks.Select(c => c.FileHash).Distinct());
    }

    [Fact]
    public void TsFile_ClassAndInterface_MapToMatchingChunkTypes()
    {
        const string source = """
            export interface Shape {
                area(): number;
            }

            export class Circle implements Shape {
                area(): number { return 3.14; }
            }
            """;

        var chunks = ChunkTs(source);

        var iface = Assert.Single(chunks, c => c.ChunkType == ChunkType.Interface);
        Assert.Equal("Shape", iface.ClassName);

        var cls = Assert.Single(chunks, c => c.ChunkType == ChunkType.Class);
        Assert.Equal("Circle", cls.ClassName);
    }

    [Fact]
    public void TsFile_ExportConstArrow_IsChunkedAsAMethod()
    {
        const string source = """
            export const add = (a: number, b: number): number => a + b;

            export const shout = (msg: string) => msg.toUpperCase();
            """;

        var chunks = ChunkTs(source);

        Assert.Equal(2, chunks.Count);
        Assert.Contains(chunks, c => c.ChunkType == ChunkType.Method && c.MethodName == "add");
        Assert.Contains(chunks, c => c.ChunkType == ChunkType.Method && c.MethodName == "shout");
    }

    [Fact]
    public void VueSfc_ChunksScriptBlockDeclarations_IgnoringTemplateAndStyle()
    {
        const string source = """
            <template>
              <button @click="increment">{{ doubled }}</button>
            </template>

            <script setup lang="ts">
            import { ref } from 'vue';

            const count = ref(0);

            function increment(): void {
                count.value++;
            }

            const doubled = () => count.value * 2;
            </script>

            <style scoped>
            button { color: red; }
            </style>
            """;

        var chunks = ChunkVue(source);

        Assert.Contains(chunks, c => c.ChunkType == ChunkType.Method && c.MethodName == "increment");
        Assert.Contains(chunks, c => c.ChunkType == ChunkType.Method && c.MethodName == "doubled");

        // Template and style are out of scope: nothing from them leaks into any chunk.
        Assert.All(chunks, c => Assert.DoesNotContain("color: red", c.Content));
        Assert.All(chunks, c => Assert.DoesNotContain("<button", c.Content));

        // Line numbers are relative to the whole .vue file, not the script block.
        var increment = Assert.Single(chunks, c => c.MethodName == "increment");
        Assert.True(increment.StartLine > 5, "script-block chunk should report its true file line");
    }

    [Fact]
    public void FileWithNoRecognizableDeclarations_FallsBackToOneWholeFileChunk()
    {
        const string source = """
            const x = 1;
            const y = 2;
            export default x + y;
            """;

        var chunk = Assert.Single(ChunkTs(source));
        Assert.Equal(ChunkType.Method, chunk.ChunkType);
        Assert.Contains("const x = 1;", chunk.Content);
        Assert.Contains("export default", chunk.Content);
        Assert.Equal(1, chunk.StartLine);
    }

    [Fact]
    public void VueSfcWithoutScriptBlock_FallsBackToOneWholeFileChunk()
    {
        const string source = """
            <template>
              <p>static markup only</p>
            </template>
            """;

        var chunk = Assert.Single(ChunkVue(source));
        Assert.Equal(ChunkType.Method, chunk.ChunkType);
        Assert.Contains("static markup only", chunk.Content);
    }

    [Fact]
    public void LongClass_IsSplitIntoHeadAndBody()
    {
        var body = string.Join('\n', Enumerable.Range(1, 350).Select(i => $"  field{i} = {i};"));
        var source = $"export class Huge {{\n{body}\n}}\n";

        var chunks = ChunkTs(source);

        Assert.DoesNotContain(chunks, c => c.ChunkType == ChunkType.Class);
        var head = Assert.Single(chunks, c => c.ChunkType == ChunkType.TypeHead);
        var tail = Assert.Single(chunks, c => c.ChunkType == ChunkType.TypeBody);
        Assert.Equal("Huge", head.ClassName);
        Assert.Equal("Huge", tail.ClassName);
        Assert.Equal(TsVueChunker.ChunkHeadLines, head.Content.Split('\n').Length);
        Assert.Contains("field1 =", head.Content);
        Assert.DoesNotContain("field350 =", head.Content);
        Assert.Contains("field350 =", tail.Content);
        Assert.Equal(head.EndLine + 1, tail.StartLine);
    }

    [Fact]
    public void LongFunction_IsSplitIntoHeadAndBody()
    {
        var body = string.Join('\n', Enumerable.Range(1, 350).Select(i => $"  const x{i} = {i};"));
        var source = $"export function huge() {{\n{body}\n}}\n";

        var chunks = ChunkTs(source);

        Assert.DoesNotContain(chunks, c => c.ChunkType == ChunkType.Method);
        var head = Assert.Single(chunks, c => c.ChunkType == ChunkType.MethodHead);
        var tail = Assert.Single(chunks, c => c.ChunkType == ChunkType.MethodBody);
        Assert.Equal("huge", head.MethodName);
        Assert.Equal("huge", tail.MethodName);
        Assert.Equal(head.EndLine + 1, tail.StartLine);
    }

    // --- Call-site extraction: raw, unresolved invocations per top-level declaration. ---

    [Fact]
    public void ExtractCallSites_RecordsCallerNameInvokedNameAndLine()
    {
        const string source = """
            export function handle() {
              work();
            }
            """;

        var site = Assert.Single(ExtractCallSitesTs(source), s => s.InvokedName == "work");
        Assert.Equal("handle", site.CallerQualifiedName);
        Assert.Equal("src/sample.ts", site.SourceFile);
        Assert.Equal(2, site.Line);
    }

    [Fact]
    public void ExtractCallSites_TakesLastSegmentOfADottedCall()
    {
        const string source = """
            export function handle() {
              this.local();
              repo.load();
              a.b.c.deep();
            }
            """;

        var invoked = ExtractCallSitesTs(source).Select(s => s.InvokedName).ToList();
        Assert.Contains("local", invoked);
        Assert.Contains("load", invoked);
        Assert.Contains("deep", invoked);
    }

    [Fact]
    public void ExtractCallSites_AttributesConstArrowCallsToTheDeclaredName()
    {
        const string source = """
            const onSubmit = async () => {
              await search();
            };
            """;

        var site = Assert.Single(ExtractCallSitesTs(source), s => s.InvokedName == "search");
        Assert.Equal("onSubmit", site.CallerQualifiedName);
    }

    [Fact]
    public void ExtractCallSites_DoesNotTreatTheFunctionDeclarationsOwnNameAsACallToItself()
    {
        const string source = """
            export function standalone() {
              return 1;
            }
            """;

        Assert.Empty(ExtractCallSitesTs(source));
    }

    [Fact]
    public void ExtractCallSites_DoesNotTreatKeywordsFollowedByParensAsInvocations()
    {
        const string source = """
            export function handle(x: number) {
              if (x) {
                work();
              }
              return (x);
            }
            """;

        var invoked = ExtractCallSitesTs(source).Select(s => s.InvokedName).ToList();
        Assert.Contains("work", invoked);
        Assert.DoesNotContain("if", invoked);
        Assert.DoesNotContain("return", invoked);
    }

    [Fact]
    public void ExtractCallSites_NeverScansAnInterfaceBody()
    {
        // An interface body holds only method signature stubs ("load(): void") — never real
        // invocations. Scanning it would misread every signature as the interface "calling" that name.
        const string source = """
            export interface Repo {
              load(): void;
              save(item: string): void;
            }
            """;

        Assert.Empty(ExtractCallSitesTs(source));
    }

    [Fact]
    public void ExtractCallSites_EmitsNothingForFileWithoutInvocations()
    {
        const string source = """
            export function compute() {
              const x = 1;
              return x;
            }
            """;

        Assert.Empty(ExtractCallSitesTs(source));
    }

    [Fact]
    public void ExtractCallSites_OnlyScansScriptBlocksOfAVueFile()
    {
        const string source = """
            <template>
              <div>{{ greet() }}</div>
            </template>

            <script setup lang="ts">
            function onSubmit() {
              search();
            }
            </script>
            """;

        var site = Assert.Single(ExtractCallSitesVue(source));
        Assert.Equal("onSubmit", site.CallerQualifiedName);
        Assert.Equal("search", site.InvokedName);
    }
}
