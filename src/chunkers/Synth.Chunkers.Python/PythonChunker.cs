using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Synth.Domain;
using Synth.Domain.Graph;

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
/// comment extraction. <see cref="ExtractCallSites"/> implements <see cref="ICallSiteExtractor"/> at
/// the same granularity <see cref="Chunk"/> emits chunks at — a call made anywhere inside a top-level
/// class's body (including its nested methods, which do not get their own chunk) is attributed to
/// that class as a whole, matching the caller identity <c>IndexingPipeline</c> can actually resolve
/// (a nested method's qualified name is never a known chunk, so it could never be resolved as a
/// callee either — attributing calls to a finer-grained name that can never match anything would be
/// misleading, not just imprecise).
/// </remarks>
public sealed partial class PythonChunker : IFileChunker, ICallSiteExtractor
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
            var (_, className, methodName) = Classify(matches[i]);
            var callerQualifiedName = QualifiedNameOf(className, methodName);
            if (callerQualifiedName.Length == 0)
                continue;

            var start = matches[i].Index;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : content.Length;

            CollectInvocations(content, start, end, callerQualifiedName, relativePath, newlineOffsets, sites);
        }

        return sites;
    }

    private static string QualifiedNameOf(string className, string methodName)
    {
        if (className.Length > 0 && methodName.Length > 0)
            return $"{className}.{methodName}";
        return className.Length > 0 ? className : methodName;
    }

    // Scans [spanStart, spanEnd) for invocations (an identifier, optionally dotted, followed by '(') and
    // records one RawCallSite per match. Two kinds of false positive are filtered:
    //   - A declaration's own name (def foo( / class Foo(Base): both look exactly like foo/Foo calling
    //     itself) — InvocationRegex's own leading negative lookbehind excludes anything immediately
    //     preceded by "def "/"class ", which also covers a *nested* def inside a class body (nested defs
    //     don't get their own chunk here, but the whole class span — including their signature lines —
    //     is still scanned, so without this they'd read as the class calling each of its own methods).
    //   - Keywords that can precede '(' without being a call ("except (Foo, Bar):", "return (x)"),
    //     filtered by name, not position, so a real call nested anywhere in the span — including one in
    //     a default-argument expression on a signature line — is still found.
    private static void CollectInvocations(
        string content, int spanStart, int spanEnd, string callerQualifiedName,
        string sourceFile, int[] newlineOffsets, List<RawCallSite> sites)
    {
        var region = content.Substring(spanStart, spanEnd - spanStart);
        foreach (Match invocation in InvocationRegex().Matches(region))
        {
            var nameGroup = invocation.Groups["name"];
            var invokedName = nameGroup.Value;
            if (PythonKeywords.Contains(invokedName))
                continue;

            var absoluteStart = spanStart + nameGroup.Index;
            sites.Add(new RawCallSite(callerQualifiedName, invokedName, sourceFile, LineAt(newlineOffsets, absoluteStart)));
        }
    }

    // Keywords that can be immediately followed by '(' without being a call (e.g. "except (Foo, Bar):",
    // "return (x)"), so they never appear as a real invocation's name.
    private static readonly HashSet<string> PythonKeywords =
    [
        "if", "elif", "else", "while", "for", "in", "not", "and", "or", "is", "return", "yield", "assert",
        "del", "raise", "except", "with", "as", "lambda", "global", "nonlocal", "pass", "break", "continue",
        "import", "from", "class", "def", "async", "await", "try", "finally",
    ];

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

    // An invocation: an identifier — optionally dotted (self.foo(), a.b.foo()) — immediately followed
    // by '('. Only the last segment is captured, so a dotted call resolves to its bare method name, the
    // same "last segment" convention CSharpRoslynChunker's InvokedSimpleName uses. The leading negative
    // lookbehind excludes any "def "/"class " immediately before — a declaration's own signature, not a
    // call to it — whether it's the span's own outer declaration or a nested def inside a class body.
    [GeneratedRegex(@"(?<!\b(?:def|class)\s+)\b(?:[A-Za-z_]\w*\.)*(?<name>[A-Za-z_]\w*)\s*\(")]
    private static partial Regex InvocationRegex();
}
