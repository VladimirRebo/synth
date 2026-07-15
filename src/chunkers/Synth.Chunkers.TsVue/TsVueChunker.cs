using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Synth.Domain;

namespace Synth.Chunkers.TsVue;

/// <summary>
/// Chunks TypeScript (<c>.ts</c>/<c>.tsx</c>) and Vue Single File Component (<c>.vue</c>) files with a
/// regex declaration-boundary scan, rather than a real parser. Ported from Sonar's shared regex-chunker
/// pattern: normalize line endings, scan for top-level declaration starts, slice the file between
/// consecutive starts (the next start — or EOF — ends the current chunk), and fall back to the whole
/// file as one chunk when nothing matches.
/// </summary>
/// <remarks>
/// For <c>.vue</c> files only the <c>&lt;script&gt;</c>/<c>&lt;script setup&gt;</c> block(s) are scanned —
/// a component's searchable code lives there; <c>&lt;template&gt;</c>/<c>&lt;style&gt;</c> are out of scope.
/// This is a chunker only: it deliberately does not implement <see cref="Graph.ICallSiteExtractor"/>
/// (unlike <c>CSharpRoslynChunker</c>/<c>PythonChunker</c>/<c>GoChunker</c>, which do — TS/Vue call-graph
/// extraction is a possible future addition, not a fundamental limitation). Recognized declarations reuse the existing
/// <see cref="ChunkType"/> values (<see cref="ChunkType.Class"/>/<see cref="ChunkType.Interface"/>/
/// <see cref="ChunkType.Method"/>) — a regex heuristic doesn't warrant growing the enum, and every
/// slice is code, so the only <see cref="ChunkType"/>-driven behavior that matters (the <c>[code]</c>
/// embedding prefix) is already correct.
/// </remarks>
public sealed partial class TsVueChunker : IFileChunker
{
    private static readonly string[] SupportedExtensions = [".ts", ".tsx", ".vue"];

    /// <inheritdoc />
    public bool CanHandle(string filePath) =>
        !string.IsNullOrEmpty(filePath) &&
        SupportedExtensions.Any(ext => filePath.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc />
    public IReadOnlyList<CodeChunk> Chunk(string filePath, string relativePath, string content)
    {
        // Normalize line endings first so char offsets and the regex scan are OS-independent.
        content = (content ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n");
        filePath ??= string.Empty;
        relativePath ??= string.Empty;

        var fileHash = ComputeFileHash(content);
        var newlineOffsets = NewlineOffsets(content);

        var isVue = filePath.EndsWith(".vue", StringComparison.OrdinalIgnoreCase);
        // A .vue file's code lives in its <script> block(s); a .ts/.tsx file is one region: the whole file.
        var regions = isVue ? ScriptRegions(content) : [(0, content.Length)];

        var chunks = new List<CodeChunk>();
        foreach (var (regionStart, regionEnd) in regions)
            AppendRegionChunks(content, filePath, relativePath, fileHash, newlineOffsets, regionStart, regionEnd, chunks);

        // Nothing recognized (no script block, or a script/file with no top-level declarations): index the
        // whole file as a single chunk rather than silently dropping it.
        if (chunks.Count == 0)
            chunks.Add(WholeFileChunk(content, filePath, relativePath, fileHash, newlineOffsets));

        return chunks;
    }

    // Scans one region [regionStart, regionEnd) of the file for top-level declaration starts and slices
    // between consecutive starts. Line numbers are computed against the whole file so a chunk carved out
    // of a .vue <script> block still reports its true 1-based location in the original file.
    private static void AppendRegionChunks(
        string content,
        string filePath,
        string relativePath,
        string fileHash,
        int[] newlineOffsets,
        int regionStart,
        int regionEnd,
        List<CodeChunk> chunks)
    {
        if (regionEnd <= regionStart)
            return;

        var regionText = content.Substring(regionStart, regionEnd - regionStart);
        var matches = DeclarationRegex().Matches(regionText);
        if (matches.Count == 0)
            return;

        for (var i = 0; i < matches.Count; i++)
        {
            var absoluteStart = regionStart + matches[i].Index;
            var absoluteEnd = i + 1 < matches.Count ? regionStart + matches[i + 1].Index : regionEnd;

            var slice = content.Substring(absoluteStart, absoluteEnd - absoluteStart).TrimEnd();
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
                StartLine = LineAt(newlineOffsets, absoluteStart),
                EndLine = LineAt(newlineOffsets, absoluteStart + slice.Length - 1),
                FileHash = fileHash,
            });
        }
    }

    private static CodeChunk WholeFileChunk(
        string content,
        string filePath,
        string relativePath,
        string fileHash,
        int[] newlineOffsets)
    {
        var body = content.TrimEnd();
        if (body.Length == 0)
            body = content;

        return new CodeChunk
        {
            FilePath = filePath,
            RelativePath = relativePath,
            ChunkType = ChunkType.Method,
            Content = body,
            StartLine = 1,
            EndLine = body.Length > 0 ? LineAt(newlineOffsets, body.Length - 1) : 1,
            FileHash = fileHash,
        };
    }

    // Maps a matched declaration to a (ChunkType, ClassName, MethodName) triple. Names are best-effort:
    // a class/interface name goes to ClassName; a function/const-arrow name goes to MethodName.
    private static (ChunkType ChunkType, string ClassName, string MethodName) Classify(Match match)
    {
        if (match.Groups["cls"].Success)
            return (ChunkType.Class, match.Groups["cls"].Value, string.Empty);
        if (match.Groups["iface"].Success)
            return (ChunkType.Interface, match.Groups["iface"].Value, string.Empty);
        if (match.Groups["fn"].Success)
            return (ChunkType.Method, string.Empty, match.Groups["fn"].Value);
        if (match.Groups["cst"].Success)
            return (ChunkType.Method, string.Empty, match.Groups["cst"].Value);

        return (ChunkType.Method, string.Empty, string.Empty);
    }

    private static IEnumerable<(int Start, int End)> ScriptRegions(string content)
    {
        foreach (Match match in ScriptBlockRegex().Matches(content))
        {
            var body = match.Groups["body"];
            yield return (body.Index, body.Index + body.Length);
        }
    }

    // Indices of every '\n' in the content, used to turn a char offset into a 1-based line number.
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

    // 1-based line number of a char offset: one more than the count of newlines strictly before it.
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

    // Same SHA256 hex-lower approach CSharpRoslynChunker.ComputeFileHash uses, so change-detection on a
    // TS/Vue file matches the C# chunker in spirit (a stable per-content hash).
    private static string ComputeFileHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(bytes);
    }

    // Top-level declaration starts, anchored to column 0 (Multiline ^, no leading indentation) so nested
    // declarations inside a function body don't create spurious boundaries. Captures the declared name in
    // one of four named groups (fn/cls/iface/cst) so Classify can pick the ChunkType and name:
    //   - export? default? (async)? function name(...)
    //   - export? default? (abstract)? class Name
    //   - export? interface Name
    //   - export? (const|let|var) Name ... = (async)? function | (...) => | x =>   (function-valued only)
    [GeneratedRegex(
        @"^(?:export\s+)?(?:default\s+)?(?:(?:async\s+)?function\*?\s+(?<fn>[A-Za-z_$][\w$]*)|(?:abstract\s+)?class\s+(?<cls>[A-Za-z_$][\w$]*)|interface\s+(?<iface>[A-Za-z_$][\w$]*)|(?:const|let|var)\s+(?<cst>[A-Za-z_$][\w$]*)\b[^=\n]*=\s*(?:async\s+)?(?:function\b|(?:\([^\n]*\)|[A-Za-z_$][\w$]*)\s*(?::[^=>\n]+?)?=>))",
        RegexOptions.Multiline)]
    private static partial Regex DeclarationRegex();

    // A <script ...>...</script> block's inner body (non-greedy, dot-matches-newline). Matches both
    // <script> and <script setup lang="ts"> forms; multiple blocks in one SFC each yield a region.
    [GeneratedRegex(@"<script\b[^>]*>(?<body>.*?)</script>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptBlockRegex();
}
