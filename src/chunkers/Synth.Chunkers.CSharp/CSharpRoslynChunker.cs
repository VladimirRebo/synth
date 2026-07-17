using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Synth.Domain.Graph;
using Synth.Domain;

namespace Synth.Chunkers.CSharp;

/// <summary>
/// Chunks C# source files using a real Roslyn syntax-tree walk (namespace → type →
/// member), rather than line windows or regex. Ported from Sonar's chunking strategy.
/// </summary>
/// <remarks>
/// Emits one chunk per type (class/interface/record/struct) covering its whole body,
/// and one chunk per method/constructor. Anything longer than
/// <see cref="LongMethodLineThreshold"/> lines — a method/constructor, or a type's own whole-body
/// chunk (its members are already covered by their own chunks, so re-embedding all of them again
/// unbounded inside the type chunk would only dilute it) — is split into a head chunk (first
/// <see cref="MethodHeadLines"/> lines: <see cref="ChunkType.MethodHead"/> or
/// <see cref="ChunkType.TypeHead"/>) and a body chunk covering the remainder
/// (<see cref="ChunkType.MethodBody"/> or <see cref="ChunkType.TypeBody"/>).
/// </remarks>
public sealed class CSharpRoslynChunker : IFileChunker, ICallSiteExtractor
{
    /// <summary>Methods and type bodies with more source lines than this are split into head/body chunks.</summary>
    public const int LongMethodLineThreshold = 300;

    /// <summary>Number of leading lines kept in the head chunk of an oversized method or type.</summary>
    public const int MethodHeadLines = 50;

    /// <inheritdoc />
    public bool CanHandle(string filePath) =>
        !string.IsNullOrEmpty(filePath) &&
        filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public IReadOnlyList<CodeChunk> Chunk(string filePath, string relativePath, string content)
    {
        content ??= string.Empty;

        var fileHash = ComputeFileHash(content);
        var tree = CSharpSyntaxTree.ParseText(content);
        var root = tree.GetCompilationUnitRoot();

        var context = new ChunkContext(filePath, relativePath, fileHash);
        var chunks = new List<CodeChunk>();

        WalkMembers(root.Members, ns: string.Empty, context, chunks);

        return chunks;
    }

    /// <inheritdoc />
    /// <remarks>
    /// A dedicated syntax walk, not folded into <see cref="Chunk"/>: the two walks share the
    /// namespace → type → member skeleton but diverge in what they collect (chunk head/body
    /// splitting and summaries here would only be noise, and there call sites would). Re-parsing the
    /// same text is cheap next to the embedding round-trip the pipeline runs per file, so keeping the
    /// two entry points independently readable wins over threading both through one walk.
    /// </remarks>
    public IReadOnlyList<RawCallSite> ExtractCallSites(string filePath, string relativePath, string content)
    {
        content ??= string.Empty;

        var tree = CSharpSyntaxTree.ParseText(content);
        var root = tree.GetCompilationUnitRoot();

        var sites = new List<RawCallSite>();
        WalkForCallSites(root.Members, ns: string.Empty, relativePath ?? string.Empty, sites);

        return sites;
    }

    private static void WalkForCallSites(
        IEnumerable<MemberDeclarationSyntax> members,
        string ns,
        string sourceFile,
        List<RawCallSite> sites)
    {
        foreach (var member in members)
        {
            switch (member)
            {
                case BaseNamespaceDeclarationSyntax nsDecl:
                    WalkForCallSites(nsDecl.Members, Combine(ns, nsDecl.Name.ToString()), sourceFile, sites);
                    break;

                case TypeDeclarationSyntax typeDecl:
                    EmitTypeCallSites(typeDecl, ns, sourceFile, sites);
                    break;
            }
        }
    }

    private static void EmitTypeCallSites(
        TypeDeclarationSyntax typeDecl,
        string ns,
        string sourceFile,
        List<RawCallSite> sites)
    {
        var className = typeDecl.Identifier.Text;

        foreach (var member in typeDecl.Members)
        {
            switch (member)
            {
                case MethodDeclarationSyntax method:
                    CollectInvocations(method, QualifyMember(ns, className, method.Identifier.Text), sourceFile, sites);
                    break;

                case ConstructorDeclarationSyntax ctor:
                    CollectInvocations(ctor, QualifyMember(ns, className, ctor.Identifier.Text), sourceFile, sites);
                    break;

                case TypeDeclarationSyntax nested:
                    // Nested types are qualified under the same namespace as EmitType does (not under the
                    // outer type), so the caller name here stays identical to that member's chunk metadata.
                    EmitTypeCallSites(nested, ns, sourceFile, sites);
                    break;
            }
        }
    }

    private static void CollectInvocations(
        SyntaxNode member,
        string callerQualifiedName,
        string sourceFile,
        List<RawCallSite> sites)
    {
        foreach (var invocation in member.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var invokedName = InvokedSimpleName(invocation.Expression);
            if (invokedName is null)
                continue;

            var line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            sites.Add(new RawCallSite(callerQualifiedName, invokedName, sourceFile, line));
        }
    }

    // The simple identifier of the invoked callee, taking the last segment of a qualified call
    // (this.Foo() / obj.Foo() / Ns.Type.Foo() all resolve to "Foo"). Null when the callee is not a
    // plain name (e.g. an invoked delegate expression) — such sites can never match a known method.
    private static string? InvokedSimpleName(ExpressionSyntax expression) => expression switch
    {
        MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.Text,
        MemberBindingExpressionSyntax memberBinding => memberBinding.Name.Identifier.Text,
        SimpleNameSyntax simpleName => simpleName.Identifier.Text,
        _ => null,
    };

    // {Namespace}.{ClassName}.{MethodName}, empty parts skipped — identical to CodeChunk.QualifiedName,
    // so a caller/callee name here is exactly what chunk metadata renders for the same member.
    private static string QualifyMember(string ns, string className, string methodName) =>
        string.Join(
            '.',
            new[] { ns, className, methodName }.Where(part => !string.IsNullOrWhiteSpace(part)));

    private static void WalkMembers(
        IEnumerable<MemberDeclarationSyntax> members,
        string ns,
        ChunkContext context,
        List<CodeChunk> chunks)
    {
        foreach (var member in members)
        {
            switch (member)
            {
                case BaseNamespaceDeclarationSyntax nsDecl:
                    // Both block (namespace X { }) and file-scoped (namespace X;) forms.
                    var childNs = Combine(ns, nsDecl.Name.ToString());
                    WalkMembers(nsDecl.Members, childNs, context, chunks);
                    break;

                case TypeDeclarationSyntax typeDecl:
                    EmitType(typeDecl, ns, context, chunks);
                    break;
            }
        }
    }

    private static void EmitType(
        TypeDeclarationSyntax typeDecl,
        string ns,
        ChunkContext context,
        List<CodeChunk> chunks)
    {
        var chunkType = TypeKindOf(typeDecl);
        if (chunkType is null)
            return;

        var className = typeDecl.Identifier.Text;
        var (startLine, endLine) = LineSpanOf(typeDecl);
        var content = typeDecl.ToString();
        var summary = ExtractSummary(typeDecl);

        // A whole-type chunk's Content is the type's full source, members included verbatim — but
        // every member already gets its own chunk below, so for a large type this duplicated nearly
        // the whole file into one unbounded blob (diluting its embedding and bloating get_symbol/
        // search results with content already present elsewhere). Split the same way an oversized
        // method already does, keeping only the head (declaration/doc-comment/fields — the part
        // that's actually useful standalone) at full weight and demoting the redundant tail.
        var lines = content.Split('\n');
        if (lines.Length > LongMethodLineThreshold)
        {
            var head = string.Join('\n', lines.Take(MethodHeadLines));
            var body = string.Join('\n', lines.Skip(MethodHeadLines));

            chunks.Add(new CodeChunk
            {
                FilePath = context.FilePath,
                RelativePath = context.RelativePath,
                Namespace = ns,
                ClassName = className,
                ChunkType = ChunkType.TypeHead,
                Content = head,
                Summary = summary,
                StartLine = startLine,
                EndLine = startLine + MethodHeadLines - 1,
                FileHash = context.FileHash,
            });

            chunks.Add(new CodeChunk
            {
                FilePath = context.FilePath,
                RelativePath = context.RelativePath,
                Namespace = ns,
                ClassName = className,
                ChunkType = ChunkType.TypeBody,
                Content = body,
                Summary = summary,
                StartLine = startLine + MethodHeadLines,
                EndLine = endLine,
                FileHash = context.FileHash,
            });
        }
        else
        {
            chunks.Add(new CodeChunk
            {
                FilePath = context.FilePath,
                RelativePath = context.RelativePath,
                Namespace = ns,
                ClassName = className,
                ChunkType = chunkType.Value,
                Content = content,
                Summary = summary,
                StartLine = startLine,
                EndLine = endLine,
                FileHash = context.FileHash,
            });
        }

        foreach (var member in typeDecl.Members)
        {
            switch (member)
            {
                case MethodDeclarationSyntax method:
                    EmitMember(method, method.Identifier.Text, ChunkType.Method, ns, className, context, chunks);
                    break;

                case ConstructorDeclarationSyntax ctor:
                    EmitMember(ctor, ctor.Identifier.Text, ChunkType.Constructor, ns, className, context, chunks);
                    break;

                case TypeDeclarationSyntax nested:
                    // Nested type: qualify its name under the outer type.
                    EmitType(nested, ns, context, chunks);
                    break;
            }
        }
    }

    private static void EmitMember(
        MemberDeclarationSyntax member,
        string methodName,
        ChunkType chunkType,
        string ns,
        string className,
        ChunkContext context,
        List<CodeChunk> chunks)
    {
        var content = member.ToString();
        var summary = ExtractSummary(member);
        var (startLine, endLine) = LineSpanOf(member);

        var lines = content.Split('\n');
        if (lines.Length > LongMethodLineThreshold)
        {
            var head = string.Join('\n', lines.Take(MethodHeadLines));
            var body = string.Join('\n', lines.Skip(MethodHeadLines));

            chunks.Add(new CodeChunk
            {
                FilePath = context.FilePath,
                RelativePath = context.RelativePath,
                Namespace = ns,
                ClassName = className,
                MethodName = methodName,
                ChunkType = ChunkType.MethodHead,
                Content = head,
                Summary = summary,
                StartLine = startLine,
                EndLine = startLine + MethodHeadLines - 1,
                FileHash = context.FileHash,
            });

            chunks.Add(new CodeChunk
            {
                FilePath = context.FilePath,
                RelativePath = context.RelativePath,
                Namespace = ns,
                ClassName = className,
                MethodName = methodName,
                ChunkType = ChunkType.MethodBody,
                Content = body,
                Summary = summary,
                StartLine = startLine + MethodHeadLines,
                EndLine = endLine,
                FileHash = context.FileHash,
            });

            return;
        }

        chunks.Add(new CodeChunk
        {
            FilePath = context.FilePath,
            RelativePath = context.RelativePath,
            Namespace = ns,
            ClassName = className,
            MethodName = methodName,
            ChunkType = chunkType,
            Content = content,
            Summary = summary,
            StartLine = startLine,
            EndLine = endLine,
            FileHash = context.FileHash,
        });
    }

    private static ChunkType? TypeKindOf(TypeDeclarationSyntax typeDecl) => typeDecl switch
    {
        // RecordDeclarationSyntax also derives from TypeDeclarationSyntax, so test it first.
        RecordDeclarationSyntax => ChunkType.Record,
        ClassDeclarationSyntax => ChunkType.Class,
        InterfaceDeclarationSyntax => ChunkType.Interface,
        StructDeclarationSyntax => ChunkType.Struct,
        _ => null,
    };

    private static (int StartLine, int EndLine) LineSpanOf(SyntaxNode node)
    {
        var span = node.GetLocation().GetLineSpan();
        // Roslyn line positions are 0-based; CodeChunk uses 1-based lines.
        return (span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1);
    }

    /// <summary>
    /// Extracts the text of the leading <c>&lt;summary&gt;</c> XML doc element, if any.
    /// Returns an empty string when the member has no XML doc comment.
    /// </summary>
    private static string ExtractSummary(SyntaxNode node)
    {
        var docComment = node.GetLeadingTrivia()
            .Select(t => t.GetStructure())
            .OfType<DocumentationCommentTriviaSyntax>()
            .FirstOrDefault();

        if (docComment is null)
            return string.Empty;

        var summaryElement = docComment.Content
            .OfType<XmlElementSyntax>()
            .FirstOrDefault(e => e.StartTag.Name.LocalName.Text == "summary");

        if (summaryElement is null)
            return string.Empty;

        var sb = new StringBuilder();
        foreach (var token in summaryElement.Content.SelectMany(node => node.DescendantTokens()))
        {
            if (token.IsKind(SyntaxKind.XmlTextLiteralToken) ||
                token.IsKind(SyntaxKind.XmlTextLiteralNewLineToken))
            {
                sb.Append(token.ValueText);
            }
        }

        // Collapse the per-line indentation/whitespace left by the doc-comment layout.
        var normalized = string.Join(
            ' ',
            sb.ToString()
                .Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0));

        return normalized;
    }

    private static string ComputeFileHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(bytes);
    }

    private static string Combine(string outer, string inner) =>
        string.IsNullOrEmpty(outer) ? inner : $"{outer}.{inner}";

    private readonly record struct ChunkContext(string FilePath, string RelativePath, string FileHash);
}
