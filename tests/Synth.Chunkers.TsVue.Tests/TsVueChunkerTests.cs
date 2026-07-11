using Synth.Core;

namespace Synth.Core.Tests;

public class TsVueChunkerTests
{
    private static readonly TsVueChunker Chunker = new();

    private static IReadOnlyList<CodeChunk> ChunkTs(string source) =>
        Chunker.Chunk("/repo/src/sample.ts", "src/sample.ts", source);

    private static IReadOnlyList<CodeChunk> ChunkVue(string source) =>
        Chunker.Chunk("/repo/src/App.vue", "src/App.vue", source);

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
}
