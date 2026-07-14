using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Synth.Domain;

namespace Synth.Chunkers.Go;

/// <summary>
/// Chunks Go (<c>.go</c>) files with a regex declaration-boundary scan, rather than a real parser —
/// same approach as <c>Synth.Chunkers.TsVue</c>'s <c>TsVueChunker</c>: scan for top-level declaration
/// starts, slice the file between consecutive starts (the next start — or EOF — ends the current
/// chunk), and fall back to the whole file as one chunk when nothing matches.
/// </summary>
/// <remarks>
/// Recognizes top-level (column 0) <c>func</c> declarations — both free functions and methods with a
/// receiver (<c>func (r *Type) Name(...)</c>, whose receiver type becomes the chunk's
/// <see cref="CodeChunk.ClassName"/>, mirroring a C# method's enclosing class) — plus
/// <c>type Name struct</c> and <c>type Name interface</c> declarations. Other top-level forms (plain
/// <c>type Name = ...</c> aliases, package-level <c>var</c>/<c>const</c> blocks) are out of scope, the
/// same "declaration boundary" trade-off TsVueChunker makes for TS/Vue. This is a chunker only: it
/// does not implement <see cref="Graph.ICallSiteExtractor"/> (call-graph extraction stays C#-only for
/// now).
/// </remarks>
public sealed partial class GoChunker : IFileChunker
{
    /// <inheritdoc />
    public bool CanHandle(string filePath) =>
        !string.IsNullOrEmpty(filePath) && filePath.EndsWith(".go", StringComparison.OrdinalIgnoreCase);

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
                StartLine = LineAt(newlineOffsets, start),
                EndLine = LineAt(newlineOffsets, start + slice.Length - 1),
                FileHash = fileHash,
            });
        }

        // Nothing recognized: index the whole file as a single chunk rather than silently dropping it.
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
        if (match.Groups["fn"].Success)
            return (ChunkType.Method, match.Groups["recv"].Value, match.Groups["fn"].Value);
        if (match.Groups["structName"].Success)
            return (ChunkType.Struct, match.Groups["structName"].Value, string.Empty);
        if (match.Groups["ifaceName"].Success)
            return (ChunkType.Interface, match.Groups["ifaceName"].Value, string.Empty);

        return (ChunkType.Method, string.Empty, string.Empty);
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

    // Top-level declaration starts, anchored to column 0 (Multiline ^) so a type embedded inside a
    // function body (a local type declaration) doesn't create a spurious boundary:
    //   - func (recv *Type)? Name(...)      — recv group holds the receiver's type name, if present
    //   - type Name struct
    //   - type Name interface
    [GeneratedRegex(
        @"^func\s+(?:\(\s*\w+\s+\*?(?<recv>[A-Za-z_]\w*)\s*\)\s+)?(?<fn>[A-Za-z_]\w*)\s*\(" +
        @"|^type\s+(?<structName>[A-Za-z_]\w*)\s+struct\b" +
        @"|^type\s+(?<ifaceName>[A-Za-z_]\w*)\s+interface\b",
        RegexOptions.Multiline)]
    private static partial Regex DeclarationRegex();
}
