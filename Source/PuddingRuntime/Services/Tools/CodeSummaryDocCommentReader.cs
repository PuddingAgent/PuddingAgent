using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using PuddingCodeIndex.Contracts;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// <c>code_summary</c> 的文档注释提取：先走廉价的行扫描，失败再回退 Roslyn AST。
/// <para>
/// 从工具类里抽出来，是为了让 <c>ICodeSummaryService</c> 复用同一份语义 ——
/// 两处各写一份「怎么找 <c>///</c> 注释」，必然在空行、attribute、嵌套类型这些边界上漂移。
/// </para>
/// </summary>
internal static class CodeSummaryDocCommentReader
{
    /// <summary>
    /// Fast line-scanning doc comment extractor. Reads the file lines, scans backward
    /// from the symbol's start line for /// comments. Much cheaper than full AST parse.
    /// Returns null if the doc comment can't be reliably extracted (falls through to AST).
    /// </summary>
    internal static string? ExtractDocCommentFast(string filePath, int startLine)
    {
        try
        {
            var lines = File.ReadAllLines(filePath);
            if (startLine < 1 || startLine > lines.Length)
                return null;

            var comments = new List<string>();
            for (int i = startLine - 2; i >= 0; i--)
            {
                var trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith("///"))
                {
                    var text = trimmed.Length > 3 ? trimmed[3..].TrimStart() : "";
                    comments.Insert(0, text);
                }
                else if (trimmed.StartsWith("/**"))
                {
                    return null;
                }
                else if (trimmed.StartsWith('[') && comments.Count > 0)
                {
                    continue;
                }
                else if (string.IsNullOrWhiteSpace(trimmed) && comments.Count > 0)
                {
                    if (startLine - 2 - i > 20) break;
                    continue;
                }
                else
                {
                    break;
                }
            }

            return comments.Count > 0 ? string.Join("\n", comments) : null;
        }
        catch
        {
            return null;
        }
    }

    internal static string? ExtractDocComment(SyntaxNode root, CodeSymbolRecord sym)
    {
        Type? targetType = sym.Kind switch
        {
            CodeSymbolKind.Class      => typeof(ClassDeclarationSyntax),
            CodeSymbolKind.Struct     => typeof(StructDeclarationSyntax),
            CodeSymbolKind.Interface  => typeof(InterfaceDeclarationSyntax),
            CodeSymbolKind.Enum       => typeof(EnumDeclarationSyntax),
            CodeSymbolKind.Method     => typeof(MethodDeclarationSyntax),
            CodeSymbolKind.Constructor=> typeof(ConstructorDeclarationSyntax),
            CodeSymbolKind.Property   => typeof(PropertyDeclarationSyntax),
            CodeSymbolKind.Field      => typeof(FieldDeclarationSyntax),
            CodeSymbolKind.Event      => typeof(EventDeclarationSyntax),
            CodeSymbolKind.Delegate   => typeof(DelegateDeclarationSyntax),
            _ => null,
        };

        SyntaxNode? bestNode = null;
        int bestDistance = int.MaxValue;

        foreach (var node in root.DescendantNodes())
        {
            if (targetType is not null && !targetType.IsInstanceOfType(node))
                continue;
            if (targetType is null && !IsDeclarationNode(node))
                continue;

            int nodeStart = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            int distance = Math.Abs(nodeStart - sym.StartLine);
            bool nameMatch = TryGetDeclarationName(node, out var declName)
                          && string.Equals(declName, sym.Name, StringComparison.Ordinal);

            if (nameMatch && distance < bestDistance)
            {
                bestDistance = distance;
                bestNode = node;
            }
            else if (bestNode is null && distance < bestDistance)
            {
                bestDistance = distance;
                bestNode = node;
            }
        }

        if (bestNode is null) return null;
        return OutlineSyntaxVisitor.GetDocumentationText(bestNode);
    }

    internal static bool IsDeclarationNode(SyntaxNode node) =>
        node is TypeDeclarationSyntax or MethodDeclarationSyntax or PropertyDeclarationSyntax
            or FieldDeclarationSyntax or EventDeclarationSyntax or ConstructorDeclarationSyntax
            or DelegateDeclarationSyntax or EnumMemberDeclarationSyntax;

    internal static bool TryGetDeclarationName(SyntaxNode node, out string name)
    {
        name = node switch
        {
            TypeDeclarationSyntax t => t.Identifier.Text,
            MethodDeclarationSyntax m => m.Identifier.Text,
            PropertyDeclarationSyntax p => p.Identifier.Text,
            FieldDeclarationSyntax f => f.Declaration.Variables.FirstOrDefault()?.Identifier.Text ?? "",
            EventDeclarationSyntax e => e.Identifier.Text,
            ConstructorDeclarationSyntax c => c.Identifier.Text,
            DelegateDeclarationSyntax d => d.Identifier.Text,
            EnumMemberDeclarationSyntax em => em.Identifier.Text,
            _ => "",
        };
        return !string.IsNullOrEmpty(name);
    }
}
