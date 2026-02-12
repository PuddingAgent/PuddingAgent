using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// Roslyn 语法树遍历器，提取文件顶层结构。
/// 仅访问类型/成员声明，不进入方法体、Lambda、局部变量等实现细节。
/// </summary>
public sealed class OutlineSyntaxVisitor : CSharpSyntaxWalker
{
    private readonly Stack<OutlineNode> _stack = new();
    private readonly List<OutlineNode> _rootNodes = new();

    public IReadOnlyList<OutlineNode> RootNodes => _rootNodes;
    public bool HasSyntaxErrors { get; private set; }

    public OutlineSyntaxVisitor() : base(SyntaxWalkerDepth.Node) { }

    public override void Visit(SyntaxNode node)
    {
        if (node.ContainsDiagnostics)
        {
            var errors = node.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error);
            if (errors.Any()) HasSyntaxErrors = true;
        }
        base.Visit(node);
    }

    // ── 文件/命名空间 ──

    public override void VisitNamespaceDeclaration(NamespaceDeclarationSyntax node)
        => PushContainer("namespace", node.Name.ToString(), node.Modifiers, null, null, node,
            () => { foreach (var m in node.Members) Visit(m); });

    public override void VisitFileScopedNamespaceDeclaration(FileScopedNamespaceDeclarationSyntax node)
        => PushContainer("namespace", node.Name.ToString(), node.Modifiers, null, null, node,
            () => { foreach (var m in node.Members) Visit(m); });

    // ── 类型声明 ──

    public override void VisitClassDeclaration(ClassDeclarationSyntax node)
        => VisitType("class", node.Identifier.Text, node.BaseList, null, node);

    public override void VisitStructDeclaration(StructDeclarationSyntax node)
        => VisitType("struct", node.Identifier.Text, node.BaseList, null, node);

    public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node)
        => VisitType("interface", node.Identifier.Text, node.BaseList, null, node);

    public override void VisitEnumDeclaration(EnumDeclarationSyntax node)
    {
        var sig = node.BaseList?.Types.Count > 0
            ? ": " + string.Join(", ", node.BaseList.Types.Select(t => t.Type.ToString()))
            : null;
        PushContainer("enum", node.Identifier.Text, node.Modifiers, sig, null, node,
            () => { foreach (var m in node.Members) Visit(m); });
    }

    public override void VisitRecordDeclaration(RecordDeclarationSyntax node)
    {
        var kind = node.ClassOrStructKeyword.Kind() == SyntaxKind.StructKeyword ? "record struct" : "record";
        VisitType(kind, node.Identifier.Text, node.BaseList, node.ParameterList, node);
    }

    private void VisitType(string kind, string name, BaseListSyntax? baseList,
        ParameterListSyntax? primaryCtorParams, TypeDeclarationSyntax node)
    {
        var sig = BuildTypeSignature(baseList, primaryCtorParams);
        PushContainer(kind, name, node.Modifiers, sig, null, node, () =>
        {
            foreach (var m in node.Members) Visit(m);
        });
    }

    // ── 叶子成员 ──

    public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
        => PushLeaf("method", node.Identifier.Text, node.Modifiers, null,
            node.ReturnType.ToString(), node.ParameterList, null, node);

    public override void VisitPropertyDeclaration(PropertyDeclarationSyntax node)
        => PushLeaf("property", node.Identifier.Text, node.Modifiers, null,
            node.Type.ToString(), null, null, node);

    public override void VisitFieldDeclaration(FieldDeclarationSyntax node)
    {
        foreach (var v in node.Declaration.Variables)
            PushLeaf("field", v.Identifier.Text, node.Modifiers, null,
                node.Declaration.Type.ToString(), null, null, node);
    }

    public override void VisitEventDeclaration(EventDeclarationSyntax node)
        => PushLeaf("event", node.Identifier.Text, node.Modifiers, null,
            node.Type.ToString(), null, null, node);

    public override void VisitEventFieldDeclaration(EventFieldDeclarationSyntax node)
    {
        foreach (var v in node.Declaration.Variables)
            PushLeaf("event", v.Identifier.Text, node.Modifiers, null,
                node.Declaration.Type.ToString(), null, null, node);
    }

    public override void VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
        => PushLeaf("constructor", node.Identifier.Text, node.Modifiers, null,
            null, node.ParameterList, null, node);

    public override void VisitIndexerDeclaration(IndexerDeclarationSyntax node)
    {
        var sig = $"this[{FormatBracketedParams(node.ParameterList)}]";
        var n = MakeNode("indexer", null, node.Modifiers, sig, node.Type.ToString(), node);
        AddToParent(n);
    }

    public override void VisitDelegateDeclaration(DelegateDeclarationSyntax node)
        => PushLeaf("delegate", node.Identifier.Text, node.Modifiers, null,
            node.ReturnType.ToString(), node.ParameterList, null, node);

    public override void VisitEnumMemberDeclaration(EnumMemberDeclarationSyntax node)
        => PushLeaf("enum_member", node.Identifier.Text, default,
            node.EqualsValue is not null ? $" = {node.EqualsValue.Value}" : null,
            null, null, null, node);

    // ── 跳过实现细节 ──

    public override void VisitBlock(BlockSyntax node) { }
    public override void VisitArrowExpressionClause(ArrowExpressionClauseSyntax node) { }
    public override void VisitAccessorDeclaration(AccessorDeclarationSyntax node) { }
    public override void VisitEqualsValueClause(EqualsValueClauseSyntax node) { }

    // ── 私有构建方法 ──

    private void PushContainer(string kind, string name, SyntaxTokenList modifiers,
        string? signature, string? returnType, SyntaxNode node, Action visitChildren)
    {
        var n = MakeNode(kind, name, modifiers, signature, returnType, node);
        AddToParent(n);
        _stack.Push(n);
        visitChildren();
        _stack.Pop();
    }

    private void PushLeaf(string kind, string? name, SyntaxTokenList modifiers,
        string? signature, string? returnType, ParameterListSyntax? paramList,
        BaseListSyntax? baseList, SyntaxNode node)
    {
        var sig = signature ?? FormatSig(paramList);
        if (baseList?.Types.Count > 0)
            sig = (sig is not null ? sig + " " : "") + ": " + string.Join(", ", baseList.Types.Select(t => t.Type.ToString()));
        AddToParent(MakeNode(kind, name, modifiers, sig, returnType, node));
    }

    private static OutlineNode MakeNode(string kind, string? name, SyntaxTokenList modifiers,
        string? signature, string? returnType, SyntaxNode node)
    {
        var ls = node.GetLocation().GetLineSpan();
        return new OutlineNode
        {
            Kind = kind,
            Name = name,
            Signature = signature,
            Line = ls.StartLinePosition.Line + 1,
            EndLine = ls.EndLinePosition.Line + 1,
            Modifiers = ModifiersShort(modifiers),
            ReturnType = returnType,
        };
    }

    private void AddToParent(OutlineNode node)
    {
        if (_stack.Count > 0)
            _stack.Peek().Children.Add(node);
        else
            _rootNodes.Add(node);
    }

    // ── 格式化 ──

    private static string? FormatSig(ParameterListSyntax? ps) =>
        ps is null ? null : $"({FormatParams(ps)})";

    private static string FormatParams(ParameterListSyntax ps) =>
        string.Join(", ", ps.Parameters.Select(p =>
        {
            var mod = p.Modifiers.Any() ? string.Join(" ", p.Modifiers) + " " : "";
            return $"{mod}{p.Type} {p.Identifier}";
        }));

    private static string FormatBracketedParams(BracketedParameterListSyntax ps) =>
        string.Join(", ", ps.Parameters.Select(p =>
        {
            var mod = p.Modifiers.Any() ? string.Join(" ", p.Modifiers) + " " : "";
            return $"{mod}{p.Type} {p.Identifier}";
        }));

    private static string? BuildTypeSignature(BaseListSyntax? baseList, ParameterListSyntax? primaryCtor)
    {
        string? sig = null;
        if (baseList?.Types.Count > 0)
            sig = ": " + string.Join(", ", baseList.Types.Select(t => t.Type.ToString()));
        if (primaryCtor is not null)
        {
            var ctorSig = $"({FormatParams(primaryCtor)})";
            sig = sig is not null ? ctorSig + " " + sig : ctorSig;
        }
        return sig;
    }

    private static string? ModifiersShort(SyntaxTokenList modifiers)
    {
        var parts = new List<string>();
        foreach (var m in modifiers)
        {
            switch (m.Kind())
            {
                case SyntaxKind.PublicKeyword: parts.Add("+"); break;
                case SyntaxKind.PrivateKeyword: parts.Add("-"); break;
                case SyntaxKind.ProtectedKeyword: parts.Add("#"); break;
                case SyntaxKind.InternalKeyword: parts.Add("~"); break;
                case SyntaxKind.StaticKeyword: parts.Add("*"); break;
                case SyntaxKind.AbstractKeyword: parts.Add("^"); break;
                case SyntaxKind.VirtualKeyword: parts.Add("v"); break;
                case SyntaxKind.OverrideKeyword: parts.Add("o"); break;
                case SyntaxKind.ReadOnlyKeyword: parts.Add("ro"); break;
                case SyntaxKind.ConstKeyword: parts.Add("c"); break;
                case SyntaxKind.SealedKeyword: parts.Add("se"); break;
                case SyntaxKind.PartialKeyword: parts.Add("p"); break;
                case SyntaxKind.AsyncKeyword: parts.Add("a"); break;
            }
        }
        return parts.Count > 0 ? string.Join(" ", parts) : null;
    }

    /// <summary>
    /// 从语法节点的 LeadingTrivia 中提取 XML 文档注释纯文本。
    /// 只提取 &lt;summary&gt; 标签内容。
    /// </summary>
    public static string? GetDocumentationText(SyntaxNode node)
    {
        var trivia = node.GetLeadingTrivia();
        var docTrivia = trivia
            .Where(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
                     || t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
            .ToList();

        if (docTrivia.Count == 0) return null;

        var lines = new List<string>();
        foreach (var triv in docTrivia)
        {
            var structure = triv.GetStructure();
            if (structure is DocumentationCommentTriviaSyntax doc)
            {
                foreach (var item in doc.Content)
                {
                    if (item is XmlElementSyntax elem
                        && elem.StartTag.Name.LocalName.Text.Equals(
                            "summary", StringComparison.OrdinalIgnoreCase))
                    {
                        var text = string.Join("",
                            elem.Content.Select(c => c.ToString()));
                        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
                        if (!string.IsNullOrWhiteSpace(text))
                            lines.Add(text);
                    }
                }
            }
        }

        return lines.Count > 0 ? string.Join(" ", lines) : null;
    }
}
