using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingCodeIntelligence.Contracts;
using PuddingCodeIntelligence.Services;
using PuddingCodeIndex.Contracts;

namespace PuddingRuntime.Services.Tools;

[Tool(
    id: "code_summary",
    name: "Code symbol summary",
    description: "获取代码符号（symbol）的快速摘要（summary）：用途、位置、签名、文档与关键调用关系，结合符号搜索、文档提取与调用图。【何时用】想快速了解某个符号是干什么的、无需通读源码时使用；适合作为深入阅读代码前的第一跳。【怎么用】传 symbol_name（符号名）即可；可选 project_id 限定范围，避免跨项目歧义。【坑】依赖项目已登记且索引完成；按名称模糊匹配取第一个结果，重名符号可能命中错误目标，必要时先用 code_symbol_search 精确定位。",
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
                docComment = CodeSummaryDocCommentReader.ExtractDocCommentFast(sym.FilePath, sym.StartLine);
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
                        docComment = CodeSummaryDocCommentReader.ExtractDocComment(root, sym);
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
            var results = await Task.WhenAll(callersTask, calleesTask);
            callers = results[0].Take(MaxCallers).ToList();
            callees = results[1].Take(MaxCallees).ToList();
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
