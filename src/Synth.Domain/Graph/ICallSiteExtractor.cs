namespace Synth.Domain.Graph;

/// <summary>
/// One unresolved invocation observed in source: method/constructor <see cref="CallerQualifiedName"/>
/// contains a call written as the simple name <see cref="InvokedName"/> at
/// <see cref="SourceFile"/>:<see cref="Line"/>. "Raw" because the callee is not resolved yet — a call
/// written as <c>Foo()</c> is only known to invoke <em>something named <c>Foo</c></em>; which
/// qualified method(s) that corresponds to needs the whole collection's declared names, so resolution
/// into <see cref="CallEdge"/>s happens collection-wide after every file is walked (SYNTH-26 / issue #33).
/// </summary>
public readonly record struct RawCallSite(string CallerQualifiedName, string InvokedName, string SourceFile, int Line);

/// <summary>
/// Extracts <see cref="RawCallSite"/>s from a single source file using a syntax-only heuristic (no
/// semantic/compilation model) — the deliberate trade-off from issue #33, so freshly-cloned repos that
/// do not build can still be indexed. A chunker opts into call-graph extraction by implementing this
/// alongside <see cref="IFileChunker"/>; <c>IndexingPipeline</c> resolves the raw sites into edges.
/// </summary>
public interface ICallSiteExtractor
{
    /// <summary>
    /// Returns every invocation call site in <paramref name="content"/>, using the same
    /// <c>(filePath, relativePath, content)</c> shape as <see cref="IFileChunker.Chunk"/>.
    /// <paramref name="relativePath"/> is recorded as each site's <see cref="RawCallSite.SourceFile"/>.
    /// </summary>
    IReadOnlyList<RawCallSite> ExtractCallSites(string filePath, string relativePath, string content);
}
