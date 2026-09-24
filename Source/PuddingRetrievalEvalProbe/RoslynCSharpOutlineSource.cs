using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using PuddingIndexChunking;

namespace PuddingRetrievalEvalProbe;

/// <summary>
/// Adapter that feeds a C# outline into the chunking component through the <see cref="IOutlineSource"/>
/// port (U4-1a).
/// <para>
/// This file is the whole reason the port exists: extracting a real outline needs Roslyn, which lives
/// above the chunking component, so the adapter sits here — outside the component — and the component
/// stays a leaf whose tests can drive it with a stand-in outline.
/// </para>
/// <para>
/// <b>Semantics mirror the production C# outline path</b>
/// (<c>Source/PuddingRuntime/Tools/BuiltIns/CodeIntelligence/OutlineSyntaxVisitor.cs</c>): only
/// declarations are visited — method bodies, accessors and lambdas are never descended into — and each
/// symbol reports kind, name, modifiers, signature, line range, containing type, plus the text of an
/// XML <c>&lt;summary&gt;</c> doc comment when present. The production code cannot be referenced from
/// this probe (it lives in PuddingRuntime), so the visitor is re-stated here at the same level of
/// detail rather than approximated more coarsely; the report records this as a known duplication.
/// </para>
/// </summary>
internal sealed class RoslynCSharpOutlineSource : IOutlineSource
{
    public Task<OutlineSymbolSet> GetOutlineAsync(
        string filePath,
        string sourceText,
        SourceLanguage language,
        CancellationToken cancellationToken = default)
    {
        if (language != SourceLanguage.CSharp)
            return Task.FromResult(OutlineSymbolSet.Empty(filePath));

        try
        {
            var tree = CSharpSyntaxTree.ParseText(sourceText, path: filePath, cancellationToken: cancellationToken);
            var visitor = new OutlineVisitor();
            visitor.Visit(tree.GetRoot(cancellationToken));

            return Task.FromResult(new OutlineSymbolSet(filePath, visitor.Symbols));
        }
        catch (Exception ex)
        {
            // An outline failure is data, not a crash: the component falls back to its neutral tiers.
            return Task.FromResult(new OutlineSymbolSet(filePath, [], $"{ex.GetType().Name}: {ex.Message}"));
        }
    }

    private sealed class OutlineVisitor : CSharpSyntaxWalker
    {
        private readonly List<OutlineSymbol> _symbols = [];
        private readonly Stack<string> _containers = new();

        public OutlineVisitor()
            : base(SyntaxWalkerDepth.Node)
        {
        }

        public IReadOnlyList<OutlineSymbol> Symbols => _symbols;

        public override void VisitNamespaceDeclaration(NamespaceDeclarationSyntax node)
        {
            _containers.Push(node.Name.ToString());
            try
            {
                foreach (var member in node.Members)
                    Visit(member);
            }
            finally
            {
                _containers.Pop();
            }
        }

        public override void VisitFileScopedNamespaceDeclaration(FileScopedNamespaceDeclarationSyntax node)
        {
            _containers.Push(node.Name.ToString());
            try
            {
                foreach (var member in node.Members)
                    Visit(member);
            }
            finally
            {
                _containers.Pop();
            }
        }

        public override void VisitClassDeclaration(ClassDeclarationSyntax node) => VisitType(node);

        public override void VisitStructDeclaration(StructDeclarationSyntax node) => VisitType(node);

        public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node) => VisitType(node);

        public override void VisitRecordDeclaration(RecordDeclarationSyntax node) => VisitType(node);

        public override void VisitEnumDeclaration(EnumDeclarationSyntax node)
        {
            Add("enum", node.Identifier.Text, node.Modifiers, node.BaseList?.ToString(), OutlineSymbolKind.Type, node);
        }

        public override void VisitMethodDeclaration(MethodDeclarationSyntax node) =>
            Add("method", node.Identifier.Text, node.Modifiers,
                $"{node.ParameterList} -> {node.ReturnType}", OutlineSymbolKind.Method, node);

        public override void VisitConstructorDeclaration(ConstructorDeclarationSyntax node) =>
            Add("constructor", node.Identifier.Text, node.Modifiers,
                node.ParameterList.ToString(), OutlineSymbolKind.Constructor, node);

        public override void VisitPropertyDeclaration(PropertyDeclarationSyntax node) =>
            Add("property", node.Identifier.Text, node.Modifiers,
                $"{node.Type} {{ {Accessors(node)} }}", OutlineSymbolKind.Property, node);

        public override void VisitIndexerDeclaration(IndexerDeclarationSyntax node) =>
            Add("indexer", "this", node.Modifiers,
                $"{node.ParameterList} -> {node.Type}", OutlineSymbolKind.Indexer, node);

        public override void VisitEventDeclaration(EventDeclarationSyntax node) =>
            Add("event", node.Identifier.Text, node.Modifiers, node.Type.ToString(), OutlineSymbolKind.Event, node);

        public override void VisitDelegateDeclaration(DelegateDeclarationSyntax node) =>
            Add("delegate", node.Identifier.Text, node.Modifiers,
                $"{node.ParameterList} -> {node.ReturnType}", OutlineSymbolKind.Delegate, node);

        public override void VisitEnumMemberDeclaration(EnumMemberDeclarationSyntax node) =>
            Add("enum member", node.Identifier.Text, null, null, OutlineSymbolKind.EnumMember, node);

        public override void VisitFieldDeclaration(FieldDeclarationSyntax node)
        {
            foreach (var variable in node.Declaration.Variables)
            {
                _symbols.Add(new OutlineSymbol(
                    variable.Identifier.Text,
                    OutlineSymbolKind.Field,
                    LineOf(node),
                    LineOf(node, end: true),
                    node.Declaration.Type.ToString(),
                    node.Modifiers.ToString(),
                    Container(),
                    null));
            }
        }

        public override void VisitEventFieldDeclaration(EventFieldDeclarationSyntax node)
        {
            foreach (var variable in node.Declaration.Variables)
            {
                _symbols.Add(new OutlineSymbol(
                    variable.Identifier.Text,
                    OutlineSymbolKind.Event,
                    LineOf(node),
                    LineOf(node, end: true),
                    node.Declaration.Type.ToString(),
                    node.Modifiers.ToString(),
                    Container(),
                    null));
            }
        }

        private void VisitType(TypeDeclarationSyntax node)
        {
            Add("type", node.Identifier.Text, node.Modifiers, TypeSignature(node), OutlineSymbolKind.Type, node);

            _containers.Push(node.Identifier.Text);
            try
            {
                foreach (var member in node.Members)
                    Visit(member);
            }
            finally
            {
                _containers.Pop();
            }
        }

        private void Add(
            string kindText,
            string name,
            SyntaxTokenList? modifiers,
            string? signature,
            OutlineSymbolKind kind,
            SyntaxNode node)
        {
            _symbols.Add(new OutlineSymbol(
                name,
                kind,
                LineOf(node),
                LineOf(node, end: true),
                Clean(signature),
                Clean(modifiers?.ToString()) ?? kindText,
                Container(),
                DocumentationText(node)));
        }

        private string? Container() => _containers.Count > 0 ? _containers.Peek() : null;

        private static string? TypeSignature(TypeDeclarationSyntax node)
        {
            var parameters = node switch
            {
                RecordDeclarationSyntax record => record.ParameterList?.ToString(),
                _ => null,
            };

            var bases = node.BaseList?.ToString();
            return string.IsNullOrWhiteSpace(parameters) ? Clean(bases) : Clean($"{parameters} {bases}");
        }

        private static string Accessors(PropertyDeclarationSyntax node)
        {
            var names = node.AccessorList?.Accessors
                .Select(accessor => accessor.Keyword.Text)
                .Where(text => text.Length > 0)
                .ToArray() ?? [];

            return names.Length == 0 ? string.Empty : string.Join(" ", names);
        }

        private static string? Clean(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            return string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }

        private static int LineOf(SyntaxNode node, bool end = false)
        {
            var location = end
                ? node.GetLocation().GetLineSpan().EndLinePosition.Line
                : node.GetLocation().GetLineSpan().StartLinePosition.Line;

            return location + 1; // Roslyn lines are 0-based; OutlineSymbol lines are 1-based.
        }

        /// <summary>Text of an XML doc comment's <c>&lt;summary&gt;</c> element, flattened to one line.</summary>
        private static string? DocumentationText(SyntaxNode node)
        {
            var documentation = node.GetLeadingTrivia()
                .Select(trivia => trivia.GetStructure())
                .OfType<DocumentationCommentTriviaSyntax>()
                .ToArray();

            if (documentation.Length == 0)
                return null;

            var summaries = new List<string>();
            foreach (var trivia in documentation)
            {
                foreach (var element in trivia.Content.OfType<XmlElementSyntax>())
                {
                    if (!element.StartTag.Name.LocalName.Text.Equals("summary", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var text = string.Join(string.Empty, element.Content.Select(content => content.ToString()));
                    text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
                    if (text.Length > 0)
                        summaries.Add(text);
                }
            }

            return summaries.Count == 0 ? null : string.Join(' ', summaries);
        }
    }
}
