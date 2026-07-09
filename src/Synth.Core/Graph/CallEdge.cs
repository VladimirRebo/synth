namespace Synth.Core.Graph;

/// <summary>
/// One directed call-graph edge within a single indexed source: <see cref="Caller"/> calls
/// <see cref="Callee"/>, observed at <see cref="SourceFile"/>:<see cref="Line"/>. Scoped by
/// <see cref="Collection"/> (the vector-store collection of the source it was extracted from) so a
/// graph query never leaks edges between indexed repos. <see cref="Caller"/>/<see cref="Callee"/>
/// are qualified symbol names; their exact format (e.g. <c>Namespace.ClassName.MethodName</c>) is
/// the extractor's concern (SYNTH-26) — storage only needs a string field to index on.
/// </summary>
public sealed record CallEdge(string Collection, string Caller, string Callee, string SourceFile, int Line);
