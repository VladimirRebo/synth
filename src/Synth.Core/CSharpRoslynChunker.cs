using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Synth.Core;

/// <summary>
/// Chunks C# source files using a real Roslyn syntax-tree walk (namespace → type →
/// member), rather than line windows or regex. Ported from Sonar's chunking strategy.
/// </summary>
/// <remarks>
/// Emits one chunk per type (class/interface/record/struct) covering its whole body,
/// and one chunk per method/constructor. Methods longer than
/// <see cref="LongMethodLineThreshold"/> lines are split into a
/// <see cref="ChunkType.MethodHead"/> chunk (first <see cref="MethodHeadLines"/> lines)
/// and a <see cref="ChunkType.MethodBody"/> chunk (the remainder).
/// </remarks>
public sealed class CSharpRoslynChunker : IFileChunker
{
    /// <summary>Methods with more source lines than this are split into head/body chunks.</summary>
    public const int LongMethodLineThreshold = 300;

    /// <summary>Number of leading lines kept in the <see cref="ChunkType.MethodHead"/> chunk.</summary>
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

        chunks.Add(new CodeChunk
        {
            FilePath = context.FilePath,
            RelativePath = context.RelativePath,
            Namespace = ns,
            ClassName = className,
            ChunkType = chunkType.Value,
            Content = typeDecl.ToString(),
            Summary = ExtractSummary(typeDecl),
            StartLine = startLine,
            EndLine = endLine,
            FileHash = context.FileHash,
        });

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
