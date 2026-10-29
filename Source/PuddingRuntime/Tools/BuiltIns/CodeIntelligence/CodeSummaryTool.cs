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
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly ICodeSummaryService _summaryService;

    public CodeSummaryTool(
        ICodeQueryService? queryService = null,
        ICodeIndexScopeResolver? resolver = null)
    {
        // 服务在内部构造而不是构造注入：AddPuddingToolsFromAssembly 会解析**每个**工具，
        // 多一个构造参数就多一个「没注册就启动失败」的坑。DI 接线留到 S5。
        _summaryService = new CodeSummaryService(queryService, resolver);
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        CodeSummaryArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var outcome = await _summaryService.SummarizeAsync(args.SymbolName, args.ProjectId, context.WorkspaceId, ct);
        if (!outcome.Success)
            return Fail(outcome.Error!);

        // 排版留在工具层：摘要正文与 JSON 投影回答的是「怎么显示」，不是「是什么」。
        var sym = outcome.Symbol!;
        var text = BuildSummaryText(sym, outcome.DisplayName, outcome.DocComment, outcome.Callers.ToList(), outcome.Callees.ToList());
        var json = JsonSerializer.Serialize(new
        {
            symbol_id = sym.SymbolId,
            name = outcome.DisplayName ?? sym.Name,
            kind = sym.Kind.ToString(),
            file_path = sym.FilePath,
            start_line = sym.StartLine,
            end_line = sym.EndLine,
            signature = sym.Signature ?? sym.Name,
            documentation = outcome.DocComment,
            container = sym.Container,
            callers = outcome.Callers.Select(c => new { symbol_id = c.SourceSymbolId, file = c.SourceFilePath, line = c.SourceLine }),
            callees = outcome.Callees.Select(c => new { symbol_id = c.TargetSymbolId, file = c.SourceFilePath, line = c.SourceLine }),
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
