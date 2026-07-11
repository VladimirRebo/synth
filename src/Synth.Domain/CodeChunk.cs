using System.Text;

namespace Synth.Domain;

/// <summary>
/// A single unit of source code (or documentation) extracted from a repository,
/// ready to be embedded and stored in the vector index.
/// Ported from Sonar's <c>CodeChunk</c> model and adapted for Synth.
/// </summary>
/// <remarks>
/// This is a pure data model: chunking, embedding and storage live in later
/// phase-2 tasks. Only <see cref="EmbeddingText"/> carries logic, and it is
/// computed on access rather than stored.
/// </remarks>
public sealed class CodeChunk
{
    /// <summary>Maximum length (characters) of the assembled <see cref="EmbeddingText"/>.</summary>
    public const int MaxEmbeddingTextLength = 24_000;

    /// <summary>Content shorter than this is embedded verbatim; longer content is head-truncated.</summary>
    public const int VerbatimContentThreshold = 3_000;

    /// <summary>Number of leading lines kept when content is head-truncated.</summary>
    public const int HeadTruncationLines = 40;

    /// <summary>Absolute path of the source file this chunk came from.</summary>
    public string FilePath { get; init; } = string.Empty;

    /// <summary>Repository-relative path of the source file.</summary>
    public string RelativePath { get; init; } = string.Empty;

    /// <summary>Enclosing namespace, if any.</summary>
    public string Namespace { get; init; } = string.Empty;

    /// <summary>Enclosing class (or interface) name, if any.</summary>
    public string ClassName { get; init; } = string.Empty;

    /// <summary>Method (or member) name, if any.</summary>
    public string MethodName { get; init; } = string.Empty;

    /// <summary>Kind of construct this chunk represents.</summary>
    public ChunkType ChunkType { get; init; }

    /// <summary>Verbatim source text of the chunk.</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>Optional human/LLM-authored summary of the chunk.</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>1-based first line of the chunk in the source file.</summary>
    public int StartLine { get; init; }

    /// <summary>1-based last line of the chunk in the source file.</summary>
    public int EndLine { get; init; }

    /// <summary>Hash of the whole source file, used for change detection.</summary>
    public string FileHash { get; init; } = string.Empty;

    /// <summary>
    /// Provider blob URL (GitHub/GitLab) pointing at this chunk's line range in the remote
    /// repository, e.g. <c>https://github.com/owner/repo/blob/HEAD/path#L10-L20</c>. Populated only
    /// for repositories indexed by remote URL; <c>null</c> for local-path-indexed chunks (there is no
    /// meaningful source URL for those) and for providers with no known blob-URL shape.
    /// </summary>
    public string? SourceUrl { get; init; }

    /// <summary>
    /// Embedding vector for <see cref="EmbeddingText"/>. Empty until the chunk has
    /// been run through the embedding model; vector stores read it on upsert and the
    /// search side compares query vectors against it. Not part of the embedding input.
    /// </summary>
    public ReadOnlyMemory<float> Embedding { get; init; }

    /// <summary>
    /// Stable identity of this chunk within the index: repository-relative path plus
    /// its line span. Used as the upsert key so re-indexing a file replaces its chunks
    /// rather than duplicating them.
    /// </summary>
    public string ChunkId => $"{RelativePath}:{StartLine}-{EndLine}";

    /// <summary>
    /// Dot-joined qualified name (<c>Namespace.ClassName.MethodName</c>),
    /// skipping empty parts. Empty when no naming parts are set.
    /// </summary>
    public string QualifiedName
    {
        get
        {
            var parts = new List<string>(3);
            if (!string.IsNullOrWhiteSpace(Namespace)) parts.Add(Namespace);
            if (!string.IsNullOrWhiteSpace(ClassName)) parts.Add(ClassName);
            if (!string.IsNullOrWhiteSpace(MethodName)) parts.Add(MethodName);
            return string.Join('.', parts);
        }
    }

    /// <summary>
    /// Text fed to the embedding model. Assembled on access (never stored) in
    /// this order: a <c>[code]</c>/<c>[docs]</c> prefix, the qualified name,
    /// the summary (if present), the content (verbatim when short, otherwise
    /// head-truncated), then the qualified name once more if it still fits —
    /// the whole thing capped at <see cref="MaxEmbeddingTextLength"/> characters.
    /// </summary>
    public string EmbeddingText
    {
        get
        {
            var sb = new StringBuilder();

            void Append(string? section)
            {
                if (string.IsNullOrEmpty(section)) return;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(section);
            }

            var prefix = ChunkType == ChunkType.Markdown ? "[docs]" : "[code]";
            var qualifiedName = QualifiedName;

            Append(prefix);

            if (qualifiedName.Length > 0)
                Append(qualifiedName);

            if (!string.IsNullOrWhiteSpace(Summary))
                Append(Summary.Trim());

            Append(TruncateContent(Content));

            // Repeat the qualified name at the end when there is still room for it.
            if (qualifiedName.Length > 0)
            {
                var separatorAndName = (sb.Length > 0 ? 1 : 0) + qualifiedName.Length;
                if (sb.Length + separatorAndName <= MaxEmbeddingTextLength)
                    Append(qualifiedName);
            }

            var result = sb.ToString();
            return result.Length > MaxEmbeddingTextLength
                ? result[..MaxEmbeddingTextLength]
                : result;
        }
    }

    /// <summary>
    /// Returns a copy of this chunk carrying <paramref name="embedding"/> as its
    /// <see cref="Embedding"/>, leaving every other field unchanged. Used by the
    /// indexing pipeline to attach a computed vector without mutating the original
    /// (the model is otherwise immutable via <c>init</c>-only setters).
    /// </summary>
    public CodeChunk WithEmbedding(ReadOnlyMemory<float> embedding) => new()
    {
        FilePath = FilePath,
        RelativePath = RelativePath,
        Namespace = Namespace,
        ClassName = ClassName,
        MethodName = MethodName,
        ChunkType = ChunkType,
        Content = Content,
        Summary = Summary,
        StartLine = StartLine,
        EndLine = EndLine,
        FileHash = FileHash,
        SourceUrl = SourceUrl,
        Embedding = embedding,
    };

    /// <summary>
    /// Returns a copy of this chunk carrying <paramref name="sourceUrl"/> as its
    /// <see cref="SourceUrl"/>, leaving every other field unchanged. Used by the indexing pipeline to
    /// stamp a remote blob URL onto a chunk without mutating the original (init-only model).
    /// </summary>
    public CodeChunk WithSourceUrl(string? sourceUrl) => new()
    {
        FilePath = FilePath,
        RelativePath = RelativePath,
        Namespace = Namespace,
        ClassName = ClassName,
        MethodName = MethodName,
        ChunkType = ChunkType,
        Content = Content,
        Summary = Summary,
        StartLine = StartLine,
        EndLine = EndLine,
        FileHash = FileHash,
        SourceUrl = sourceUrl,
        Embedding = Embedding,
    };

    private static string TruncateContent(string content)
    {
        if (string.IsNullOrEmpty(content) || content.Length <= VerbatimContentThreshold)
            return content;

        var lines = content.Split('\n');
        if (lines.Length <= HeadTruncationLines)
            return content;

        return string.Join('\n', lines.Take(HeadTruncationLines));
    }
}
