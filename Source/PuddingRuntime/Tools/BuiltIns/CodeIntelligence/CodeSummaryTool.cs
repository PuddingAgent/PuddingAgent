using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingCodeIntelligence.Contracts;
using PuddingCodeIntelligence.Services;

namespace PuddingRuntime.Services.Tools;

[Tool(
    id: "code_summary",
    name: "Code symbol summary",
    description: "Get a quick summary of a code symbol: purpose, location, signature, documentation, and key call relationships. Combines symbol search with doc extraction and call graph.",
    category: ToolCategory.Query,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe,
    SortOrder = 216)]
public sealed class CodeSummaryTool : PuddingToolBase<CodeSummaryArgs>
{
    private const int MaxCallers = 5;
    private const int MaxCallees = 5;
    private static readonly JsonSerializerOptions JsonOptions = 
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly ICodeQueryService? _queryService;
    private readonly ICodeIndexScopeResolver? _resolver;

    public CodeSummaryTool(
        ICodeQueryService? queryService = null,
        ICodeIndexScopeResolver? resolver = null)
    {
        _queryService = queryService;
        _resolver = resolver;
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        CodeSummaryArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        if (_queryService is null)
            return Fail("ICodeQueryService is not registered.");

        if (string.IsNullOrWhiteSpace(args.SymbolName))
            return Fail("symbol_name is required.");

        var symbolName = args.SymbolName.Trim();

        var projectId = await CodeQueryToolHelper.ResolveAndEnsureProjectIdAsync(
            _resolver, context.WorkspaceId, args.ProjectId, null, null, ct);

        var searchResults = await _queryService.SearchSymbolsAsync(
            new CodeSymbolSearchRequest(
                WorkspaceId: context.WorkspaceId,
                Query: symbolName,
                ProjectId: projectId,
                Kind: null,
                Limit: 10,
                Skip: 0),
            ct);

        if (searchResults.Count == 0)
            return Fail($"(未找到符号: {symbolName})");

        var match = searchResults[0];
        var sym = match.Symbol;

        string? docComment = null;
        try
        {
            if (File.Exists(sym.FilePath))
            {
                // Fast path: line-scan for doc comment without full AST parse
                docComment = ExtractDocCommentFast(sym.FilePath, sym.StartLine);
                if (docComment == null)
                {
                    // Fallback: full AST parse — skip for large files (>25KB ≈ 500 lines)
                    var fileInfo = new FileInfo(sym.FilePath);
                    if (fileInfo.Length > 25_000)
                    {
                        docComment = "(file too large for AST parse — use file_read to inspect)";
                    }
                    else
                    {
                        var source = await File.ReadAllTextAsync(sym.FilePath, ct);
                        var tree = CSharpSyntaxTree.ParseText(source);
                        var root = await tree.GetRootAsync(ct);
                        docComment = ExtractDocComment(root, sym);
                    }
                }
            }
        }
        catch { }

        List<CodeRelationRecord> callers = [];
        List<CodeRelationRecord> callees = [];
        try
        {
            var effectiveProjectId = sym.ProjectId ?? projectId;
            var callersTask = _queryService.GetCallersAsync(
                context.WorkspaceId, effectiveProjectId, sym.SymbolId, ct);
            var calleesTask = _queryService.GetCalleesAsync(
                context.WorkspaceId, effectiveProjectId, sym.SymbolId, ct);
            await Task.WhenAll(callersTask, calleesTask);
            callers = callersTask.Result.Take(MaxCallers).ToList();
            callees = calleesTask.Result.Take(MaxCallees).ToList();
        }
        catch { }

        var text = BuildSummaryText(sym, match.DisplayName, docComment, callers, callees);
        var json = JsonSerializer.Serialize(new
        {
            symbol_id = sym.SymbolId,
            name = match.DisplayName ?? sym.Name,
            kind = sym.Kind.ToString(),
            file_path = sym.FilePath,
            start_line = sym.StartLine,
            end_line = sym.EndLine,
            signature = sym.Signature ?? sym.Name,
            documentation = docComment,
            container = sym.Container,
            callers = callers.Select(c => new { symbol_id = c.SourceSymbolId, file = c.SourceFilePath, line = c.SourceLine }),
            callees = callees.Select(c => new { symbol_id = c.TargetSymbolId, file = c.SourceFilePath, line = c.SourceLine }),
        }, JsonOptions);

        return Ok($"{text}\n\n---\n{json}");
    }

    /// <summary>
    /// Fast line-scanning doc comment extractor. Reads the file lines, scans backward
    /// from the symbol's start line for /// comments. Much cheaper than full AST parse.
    /// Returns null if the doc comment can't be reliably extracted (falls through to AST).
    /// </summary>
    private static string? ExtractDocCommentFast(string filePath, int startLine)
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

    private static string? ExtractDocComment(SyntaxNode root, CodeSymbolRecord sym)
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

    private static bool IsDeclarationNode(SyntaxNode node) =>
        node is TypeDeclarationSyntax or MethodDeclarationSyntax or PropertyDeclarationSyntax
            or FieldDeclarationSyntax or EventDeclarationSyntax or ConstructorDeclarationSyntax
            or DelegateDeclarationSyntax or EnumMemberDeclarationSyntax;

    private static bool TryGetDeclarationName(SyntaxNode node, out string name)
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

    private static string BuildSummaryText(CodeSymbolRecord sym, string? displayName,
        string? doc, List<CodeRelationRecord> callers, List<CodeRelationRecord> callees)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"符号: {displayName ?? sym.Name}");
        sb.AppendLine($"类型: {sym.Kind}");
        sb.AppendLine($"位置: {sym.FilePath}:{sym.StartLine}");
        sb.AppendLine($"签名: {sym.Signature ?? sym.Name}");
        if (!string.IsNullOrWhiteSpace(doc))
        {
            sb.AppendLine();
            sb.Append($"文档: {doc}");
        }
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine($"调用者 ({callers.Count}):");
        if (callers.Count == 0) sb.AppendLine("  (无)");
        else for (int i = 0; i < callers.Count; i++)
            sb.AppendLine($"  {i + 1}. {callers[i].SourceSymbolId}");
        sb.AppendLine();
        sb.Append($"被调用者 ({callees.Count}):");
        if (callees.Count == 0) sb.Append("  (无内部方法调用)");
        else
        {
            sb.AppendLine();
            for (int i = 0; i < callees.Count; i++)
                sb.AppendLine($"  {i + 1}. {callees[i].TargetSymbolId}");
        }
        return sb.ToString().TrimEnd();
    }

    private static ToolExecutionResult Ok(string output) => ToolExecutionResult.Ok(output);
    private static ToolExecutionResult Fail(string error) => ToolExecutionResult.Fail(error);
}

public sealed record CodeSummaryArgs
{
    [ToolParam("Symbol name to summarize. Supports fuzzy matching.")]
    public required string SymbolName { get; init; }

    [ToolParam("Optional project identifier to limit search scope.")]
    public string? ProjectId { get; init; }
}
