using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Synth.Domain;

namespace Synth.Chunkers.Python;

/// <summary>
/// Chunks Python (<c>.py</c>) files with a regex declaration-boundary scan, rather than a real
/// parser — same approach as <c>Synth.Chunkers.TsVue</c>'s <c>TsVueChunker</c>: scan for top-level
/// declaration starts, slice the file between consecutive starts (the next start — or EOF — ends
/// the current chunk), and fall back to the whole file as one chunk when nothing matches.
/// </summary>
/// <remarks>
/// Only column-0 (unindented) <c>def</c>/<c>class</c> statements are recognized, so a nested method
/// inside a class body stays embedded in its enclosing class's chunk rather than becoming its own —
/// the same "top-level only" scope TsVueChunker applies. Any decorator lines (<c>@foo</c>) directly
/// above a declaration are captured into the same chunk as the declaration they decorate. A leading
/// triple-quoted docstring, if the declaration's body opens with one, is extracted into
/// <see cref="CodeChunk.Summary"/> — the Python-idiomatic counterpart to the C# chunker's XML doc
/// comment extraction. This is a chunker only: it does not implement
/// <see cref="Graph.ICallSiteExtractor"/> (call-graph extraction stays C#-only for now).
/// </remarks>
public sealed partial class PythonChunker : IFileChunker
{
    /// <inheritdoc />
    public bool CanHandle(string filePath) =>
        !string.IsNullOrEmpty(filePath) && filePath.EndsWith(".py", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public IReadOnlyList<CodeChunk> Chunk(string filePath, string relativePath, string content)
    {
        content = (content ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n");
        filePath ??= string.Empty;
        relativePath ??= string.Empty;

        var fileHash = ComputeFileHash(content);
        var newlineOffsets = NewlineOffsets(content);

        var matches = DeclarationRegex().Matches(content);
        var chunks = new List<CodeChunk>();

        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : content.Length;

            var slice = content.Substring(start, end - start).TrimEnd();
            if (slice.Length == 0)
                continue;

            var (chunkType, className, methodName) = Classify(matches[i]);

            chunks.Add(new CodeChunk
            {
                FilePath = filePath,
                RelativePath = relativePath,
                ClassName = className,
                MethodName = methodName,
                ChunkType = chunkType,
                Content = slice,
                Summary = ExtractDocstring(slice),
                StartLine = LineAt(newlineOffsets, start),
                EndLine = LineAt(newlineOffsets, start + slice.Length - 1),
                FileHash = fileHash,
            });
        }

        // Nothing recognized (no top-level def/class): index the whole file as a single chunk
        // rather than silently dropping it.
        if (chunks.Count == 0)
        {
            var body = content.TrimEnd();
            if (body.Length == 0)
                body = content;

            chunks.Add(new CodeChunk
            {
                FilePath = filePath,
                RelativePath = relativePath,
                ChunkType = ChunkType.Method,
                Content = body,
                StartLine = 1,
                EndLine = body.Length > 0 ? LineAt(newlineOffsets, body.Length - 1) : 1,
                FileHash = fileHash,
            });
        }

        return chunks;
    }

    // Maps a matched declaration to a (ChunkType, ClassName, MethodName) triple.
    private static (ChunkType ChunkType, string ClassName, string MethodName) Classify(Match match)
    {
        if (match.Groups["cls"].Success)
            return (ChunkType.Class, match.Groups["cls"].Value, string.Empty);
        if (match.Groups["fn"].Success)
            return (ChunkType.Method, string.Empty, match.Groups["fn"].Value);

        return (ChunkType.Method, string.Empty, string.Empty);
    }

    // Best-effort: the first triple-quoted string anywhere in the slice, which for a well-formed
    // def/class is that declaration's own docstring (the first statement in its body). A rare false
    // positive — a triple-quoted string used as a default-argument value ahead of the real docstring
    // — is an accepted heuristic tradeoff, same spirit as TsVueChunker's regex-only scanning.
    private static string ExtractDocstring(string slice)
    {
        var match = DocstringRegex().Match(slice);
        if (!match.Success)
            return string.Empty;

        var doc = match.Groups["doc"].Success ? match.Groups["doc"].Value : match.Groups["doc2"].Value;
        return doc.Trim();
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

    // Top-level declaration starts, anchored to column 0 (Multiline ^, no leading indentation) so a
    // method nested inside a class body doesn't create a spurious boundary. Any decorator lines
    // (@foo, possibly with arguments, possibly stacked) directly above are captured as part of the
    // same match so they stay attached to the declaration they decorate:
    //   - (@decorator\n)*  (async)? def name(...)
    //   - (@decorator\n)*  class Name(...)?
    [GeneratedRegex(
        @"^(?:@[^\n]*\n)*(?:(?:async\s+)?def\s+(?<fn>[A-Za-z_]\w*)|class\s+(?<cls>[A-Za-z_]\w*))",
        RegexOptions.Multiline)]
    private static partial Regex DeclarationRegex();

    // First triple-quoted string (single- or double-quote flavor) anywhere in a chunk slice.
    [GeneratedRegex("\"\"\"(?<doc>.*?)\"\"\"|'''(?<doc2>.*?)'''", RegexOptions.Singleline)]
    private static partial Regex DocstringRegex();
}
