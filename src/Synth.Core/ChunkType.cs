namespace Synth.Core;

/// <summary>
/// Kind of source construct a <see cref="CodeChunk"/> represents.
/// Mirrors Sonar's chunk taxonomy (see the <c>code-chunking-strategy</c> wiki).
/// </summary>
public enum ChunkType
{
    /// <summary>A whole class declaration.</summary>
    Class,

    /// <summary>A whole interface declaration.</summary>
    Interface,

    /// <summary>A whole method (signature + body).</summary>
    Method,

    /// <summary>A constructor.</summary>
    Constructor,

    /// <summary>A property.</summary>
    Property,

    /// <summary>The signature/declaration part of a method, without its body.</summary>
    MethodHead,

    /// <summary>The body of a method, split out from its head.</summary>
    MethodBody,

    /// <summary>Documentation prose (e.g. a Markdown file or section).</summary>
    Markdown,
}
