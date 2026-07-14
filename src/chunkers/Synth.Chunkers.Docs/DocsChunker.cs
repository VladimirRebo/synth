using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Synth.Domain;

namespace Synth.Chunkers.Docs;

/// <summary>
/// Chunks Markdown (<c>.md</c>), YAML (<c>.yml</c>/<c>.yaml</c>) and JSON (<c>.json</c>) files —
/// three non-code, whole-document formats bundled into one chunker the same way
/// <c>Synth.Chunkers.TsVue</c>'s <c>TsVueChunker</c> bundles TS/TSX/Vue: each format gets its own
/// internal strategy, dispatched on <see cref="CanHandle"/>'s extension.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Markdown is split at ATX heading boundaries (<c>#</c> through <c>######</c>) — the same
/// "declaration boundary" scan the code chunkers use, just with a heading standing in for a
/// class/function start. Chunks carry <see cref="ChunkType.Markdown"/> so <see cref="CodeChunk.EmbeddingText"/>
/// gets the <c>[docs]</c> prefix instead of <c>[code]</c>.</item>
/// <item>YAML is split at top-level (column 0, unindented) <c>key:</c> lines — config's rough
/// equivalent of a declaration boundary. Each chunk carries <see cref="ChunkType.Property"/> with the
/// key as <see cref="CodeChunk.MethodName"/>.</item>
/// <item>JSON has no reliable regex-splittable top-level boundary (its keys are indented inside
/// <c>{ }</c>, not anchored to column 0 like YAML's), so a <c>.json</c> file is always indexed as one
/// whole-file chunk rather than risking a fragile brace-aware split.</item>
/// </list>
/// This is a chunker only: it does not implement <see cref="Graph.ICallSiteExtractor"/>.
/// </remarks>
public sealed partial class DocsChunker : IFileChunker
{
    private static readonly string[] SupportedExtensions = [".md", ".yml", ".yaml", ".json"];

    /// <inheritdoc />
    public bool CanHandle(string filePath) =>
        !string.IsNullOrEmpty(filePath) &&
        SupportedExtensions.Any(ext => filePath.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc />
    public IReadOnlyList<CodeChunk> Chunk(string filePath, string relativePath, string content)
    {
        content = (content ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n");
        filePath ??= string.Empty;
        relativePath ??= string.Empty;

        if (filePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return [WholeFileChunk(content, filePath, relativePath, ChunkType.Method)];

        var isMarkdown = filePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
        var chunks = isMarkdown
            ? ScanBoundaries(content, filePath, relativePath, MarkdownHeadingRegex(), ChunkType.Markdown, useHeadingAsMethodName: false)
            : ScanBoundaries(content, filePath, relativePath, YamlTopLevelKeyRegex(), ChunkType.Property, useHeadingAsMethodName: true);

        if (chunks.Count == 0)
            chunks.Add(WholeFileChunk(content, filePath, relativePath, isMarkdown ? ChunkType.Markdown : ChunkType.Property));

        return chunks;
    }

    // Shared declaration-boundary scan: slice the file between consecutive regex matches (the next
    // match — or EOF — ends the current chunk), same pattern TsVueChunker/PythonChunker/GoChunker use.
    private static List<CodeChunk> ScanBoundaries(
        string content, string filePath, string relativePath, Regex boundaryRegex, ChunkType chunkType,
        bool useHeadingAsMethodName)
    {
        var fileHash = ComputeFileHash(content);
        var newlineOffsets = NewlineOffsets(content);
        var matches = boundaryRegex.Matches(content);
        var chunks = new List<CodeChunk>(matches.Count);

        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : content.Length;

            var slice = content.Substring(start, end - start).TrimEnd();
            if (slice.Length == 0)
                continue;

            chunks.Add(new CodeChunk
            {
                FilePath = filePath,
                RelativePath = relativePath,
                MethodName = useHeadingAsMethodName ? matches[i].Groups["key"].Value : string.Empty,
                ChunkType = chunkType,
                Content = slice,
                StartLine = LineAt(newlineOffsets, start),
                EndLine = LineAt(newlineOffsets, start + slice.Length - 1),
                FileHash = fileHash,
            });
        }

        return chunks;
    }

    private static CodeChunk WholeFileChunk(string content, string filePath, string relativePath, ChunkType chunkType)
    {
        var body = content.TrimEnd();
        if (body.Length == 0)
            body = content;

        var newlineOffsets = NewlineOffsets(content);
        return new CodeChunk
        {
            FilePath = filePath,
            RelativePath = relativePath,
            ChunkType = chunkType,
            Content = body,
            StartLine = 1,
            EndLine = body.Length > 0 ? LineAt(newlineOffsets, body.Length - 1) : 1,
            FileHash = ComputeFileHash(content),
        };
    }

    private static int[] NewlineOffsets(string content)
    {
        var offsets = new List<int>();
        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] == '\n')
                offsets.Add(i);
        }

        return offsets.ToArray();
    }

    private static int LineAt(int[] newlineOffsets, int charOffset)
    {
        int lo = 0, hi = newlineOffsets.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (newlineOffsets[mid] < charOffset)
                lo = mid + 1;
            else
                hi = mid;
        }

        return lo + 1;
    }

    // Same SHA256 hex-lower approach the other chunkers use, so change-detection matches in spirit.
    private static string ComputeFileHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(bytes);
    }

    // An ATX heading line (1-6 '#', a space, then text), anchored to column 0.
    [GeneratedRegex(@"^#{1,6}[ \t]+.+$", RegexOptions.Multiline)]
    private static partial Regex MarkdownHeadingRegex();

    // A top-level (unindented) YAML mapping key: bare or single/double-quoted, followed by ':' and
    // either a value or end-of-line (a nested-mapping key). Anchored to column 0 so an indented key
    // one level down inside a mapping/list doesn't create a spurious boundary.
    [GeneratedRegex(@"^(?<key>[\w.\-]+|""[^""\n]+""|'[^'\n]+')\s*:", RegexOptions.Multiline)]
    private static partial Regex YamlTopLevelKeyRegex();
}
