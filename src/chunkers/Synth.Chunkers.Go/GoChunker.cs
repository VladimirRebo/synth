using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Synth.Domain;
using Synth.Domain.Graph;

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
/// same "declaration boundary" trade-off TsVueChunker makes for TS/Vue. <see cref="ExtractCallSites"/>
/// implements <see cref="ICallSiteExtractor"/> only for <c>func</c> spans (structs/interfaces have no
/// callable body to scan).
/// </remarks>
public sealed partial class GoChunker : IFileChunker, ICallSiteExtractor
{
    /// <summary>Chunks with more source lines than this are split into head/body chunks.</summary>
    public const int LongChunkLineThreshold = 300;

    /// <summary>Number of leading lines kept in the head chunk of an oversized declaration.</summary>
    public const int ChunkHeadLines = 50;

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

            // A struct/interface's slice is the only chunk covering its body (no separate per-member
            // chunk exists to fall back on), so bound it the same way an oversized C# type/method
            // chunk already is — an unbounded chunk dilutes its own embedding and bloats
            // get_symbol/search responses.
            AddChunkOrSplit(
                chunks, filePath, relativePath, className, methodName, chunkType, slice,
                LineAt(newlineOffsets, start), LineAt(newlineOffsets, start + slice.Length - 1), fileHash);
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

    // Adds one chunk for a declaration's slice, or — past LongChunkLineThreshold lines — a head/body
    // pair instead, mirroring CSharpRoslynChunker's split: a struct/interface's oversized slice
    // becomes TypeHead/TypeBody, a func's becomes MethodHead/MethodBody.
    private static void AddChunkOrSplit(
        List<CodeChunk> chunks, string filePath, string relativePath, string className, string methodName,
        ChunkType chunkType, string content, int startLine, int endLine, string fileHash)
    {
        var lines = content.Split('\n');
        if (lines.Length <= LongChunkLineThreshold)
        {
            chunks.Add(new CodeChunk
            {
                FilePath = filePath,
                RelativePath = relativePath,
                ClassName = className,
                MethodName = methodName,
                ChunkType = chunkType,
                Content = content,
                StartLine = startLine,
                EndLine = endLine,
                FileHash = fileHash,
            });
            return;
        }

        var (headType, bodyType) = chunkType is ChunkType.Struct or ChunkType.Interface
            ? (ChunkType.TypeHead, ChunkType.TypeBody)
            : (ChunkType.MethodHead, ChunkType.MethodBody);

        chunks.Add(new CodeChunk
        {
            FilePath = filePath,
            RelativePath = relativePath,
            ClassName = className,
            MethodName = methodName,
            ChunkType = headType,
            Content = string.Join('\n', lines.Take(ChunkHeadLines)),
            StartLine = startLine,
            EndLine = startLine + ChunkHeadLines - 1,
            FileHash = fileHash,
        });

        chunks.Add(new CodeChunk
        {
            FilePath = filePath,
            RelativePath = relativePath,
            ClassName = className,
            MethodName = methodName,
            ChunkType = bodyType,
            Content = string.Join('\n', lines.Skip(ChunkHeadLines)),
            StartLine = startLine + ChunkHeadLines,
            EndLine = endLine,
            FileHash = fileHash,
        });
    }

    /// <inheritdoc />
    public IReadOnlyList<RawCallSite> ExtractCallSites(string filePath, string relativePath, string content)
    {
        content = (content ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n");
        relativePath ??= string.Empty;

        var newlineOffsets = NewlineOffsets(content);
        var matches = DeclarationRegex().Matches(content);
        var sites = new List<RawCallSite>();

        for (var i = 0; i < matches.Count; i++)
        {
            // Only func spans have a callable body; struct/interface declarations have nothing to scan.
            if (!matches[i].Groups["fn"].Success)
                continue;

            var recv = matches[i].Groups["recv"].Value;
            var fn = matches[i].Groups["fn"].Value;
            var callerQualifiedName = recv.Length > 0 ? $"{recv}.{fn}" : fn;

            var start = matches[i].Index;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : content.Length;
            var declNameStart = matches[i].Groups["fn"].Index;

            CollectInvocations(content, start, end, declNameStart, callerQualifiedName, relativePath, newlineOffsets, sites);
        }

        return sites;
    }

    // Scans [spanStart, spanEnd) for invocations (an identifier, optionally dotted, followed by '(') and
    // records one RawCallSite per match — except the declaration's own name (declNameStart), which would
    // otherwise be misread as it calling itself (func Name( looks exactly like an invocation of Name).
    // Go keywords that can precede '(' without being a call (if a condition happened to be parenthesized,
    // a type switch, etc.) are filtered by name, not position, so a real call anywhere else in the span
    // is still found.
    private static void CollectInvocations(
        string content, int spanStart, int spanEnd, int declNameStart, string callerQualifiedName,
        string sourceFile, int[] newlineOffsets, List<RawCallSite> sites)
    {
        var region = content.Substring(spanStart, spanEnd - spanStart);
        foreach (Match invocation in InvocationRegex().Matches(region))
        {
            var nameGroup = invocation.Groups["name"];
            var absoluteStart = spanStart + nameGroup.Index;
            if (absoluteStart == declNameStart)
                continue;

            var invokedName = nameGroup.Value;
            if (GoKeywords.Contains(invokedName))
                continue;

            sites.Add(new RawCallSite(callerQualifiedName, invokedName, sourceFile, LineAt(newlineOffsets, absoluteStart)));
        }
    }

    // Keywords that could precede '(' without being a call, so they never appear as a real invocation's
    // name (Go rarely parenthesizes these — no parens around if/for conditions — but the guard is cheap).
    private static readonly HashSet<string> GoKeywords =
    [
        "if", "for", "switch", "select", "case", "default", "func", "go", "defer", "return", "range",
        "chan", "map", "struct", "interface", "type", "var", "const", "package", "import", "else",
    ];

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

    // An invocation: an identifier — optionally dotted (s.Handle(), pkg.Foo()) — immediately followed by
    // '('. Only the last segment is captured, so a dotted call resolves to its bare name, the same "last
    // segment" convention CSharpRoslynChunker's InvokedSimpleName uses.
    [GeneratedRegex(@"(?:[A-Za-z_]\w*\.)*(?<name>[A-Za-z_]\w*)\s*\(")]
    private static partial Regex InvocationRegex();
}
