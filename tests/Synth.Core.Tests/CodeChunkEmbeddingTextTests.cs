using Synth.Core;

namespace Synth.Core.Tests;

public class CodeChunkEmbeddingTextTests
{
    [Fact]
    public void ShortContent_IsEmbeddedVerbatim()
    {
        var chunk = new CodeChunk
        {
            Namespace = "Synth.Core",
            ClassName = "CodeChunk",
            MethodName = "DoWork",
            ChunkType = ChunkType.Method,
            Summary = "Does the work.",
            Content = "public void DoWork() { }",
        };

        var text = chunk.EmbeddingText;

        Assert.StartsWith("[code]", text);
        Assert.Contains("Synth.Core.CodeChunk.DoWork", text);
        Assert.Contains("Does the work.", text);
        Assert.Contains("public void DoWork() { }", text);
        // Qualified name appears at the start and, since it fits, again at the end.
        Assert.EndsWith("Synth.Core.CodeChunk.DoWork", text);
    }

    [Fact]
    public void MarkdownChunk_UsesDocsPrefix()
    {
        var chunk = new CodeChunk
        {
            ChunkType = ChunkType.Markdown,
            Content = "# Title\nSome docs.",
        };

        Assert.StartsWith("[docs]", chunk.EmbeddingText);
    }

    [Fact]
    public void LongContent_IsHeadTruncatedToFortyLines()
    {
        // Build content that exceeds the verbatim threshold and the line cap.
        var lines = Enumerable.Range(1, 200).Select(i => $"line {i} with some padding text here");
        var content = string.Join('\n', lines);
        Assert.True(content.Length > CodeChunk.VerbatimContentThreshold);

        var chunk = new CodeChunk
        {
            ClassName = "Big",
            ChunkType = ChunkType.Class,
            Content = content,
        };

        var text = chunk.EmbeddingText;

        Assert.Contains("line 1 with", text);
        Assert.Contains($"line {CodeChunk.HeadTruncationLines} with", text);
        Assert.DoesNotContain($"line {CodeChunk.HeadTruncationLines + 1} with", text);
        Assert.DoesNotContain("line 200 with", text);
    }

    [Fact]
    public void MissingSummary_IsOmitted()
    {
        var chunk = new CodeChunk
        {
            ClassName = "NoSummary",
            ChunkType = ChunkType.Class,
            Content = "class NoSummary { }",
            // Summary left at its default empty value.
        };

        var text = chunk.EmbeddingText;

        var expected = "[code]\nNoSummary\nclass NoSummary { }\nNoSummary";
        Assert.Equal(expected, text);
    }

    [Fact]
    public void EmbeddingText_IsCappedAtHardLimit()
    {
        // Content under the verbatim threshold would be huge if we removed the cap,
        // so build something that stays verbatim yet blows past 24000 chars overall.
        var content = new string('x', CodeChunk.VerbatimContentThreshold - 1);
        var chunk = new CodeChunk
        {
            Namespace = new string('N', 30_000),
            ChunkType = ChunkType.Class,
            Content = content,
        };

        var text = chunk.EmbeddingText;

        Assert.Equal(CodeChunk.MaxEmbeddingTextLength, text.Length);
    }

    [Fact]
    public void QualifiedName_SkipsEmptyParts()
    {
        var chunk = new CodeChunk
        {
            Namespace = "Ns",
            // ClassName intentionally empty.
            MethodName = "M",
            ChunkType = ChunkType.Method,
        };

        Assert.Equal("Ns.M", chunk.QualifiedName);
    }
}
