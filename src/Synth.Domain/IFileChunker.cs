namespace Synth.Domain;

/// <summary>
/// Splits a single source file into <see cref="CodeChunk"/>s ready for embedding.
/// Implementations are language-specific; the indexing pipeline (later task) picks
/// the first chunker whose <see cref="CanHandle"/> returns <c>true</c> for a file.
/// </summary>
public interface IFileChunker
{
    /// <summary>Whether this chunker understands the given file (typically by extension).</summary>
    bool CanHandle(string filePath);

    /// <summary>
    /// Parses <paramref name="content"/> and returns its chunks. <paramref name="filePath"/> is the
    /// absolute path (stored on each chunk); <paramref name="relativePath"/> is the repository-relative
    /// path. The <paramref name="content"/> is passed in so callers can read the file once.
    /// </summary>
    IReadOnlyList<CodeChunk> Chunk(string filePath, string relativePath, string content);
}
