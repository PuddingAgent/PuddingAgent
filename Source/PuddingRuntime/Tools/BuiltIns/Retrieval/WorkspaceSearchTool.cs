using System.Text.Json;
using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingCodeIndex.Contracts.Retrieval;
using PuddingCodeIndex.Services.Retrieval;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// <c>workspace_search</c>：统一检索入口（ADR-089 R6 B1）。
/// <para>
/// <b>它是薄壳</b>：参数校验与展示在这里，路由/融合/去重/分页/空原因裁决全在
/// <see cref="IRetrievalPort"/> 里。工具层不得自己再判一次"有没有结果"，
/// 否则同一件事会有两个裁决点。
/// </para>
/// <para>
/// <b>为什么把 emptyReason / degraded / nextSteps 都带回给调用方</b>：
/// "没搜到"有六种不同的原因（未索引 / 能力不支持 / 过滤太紧 / 确实没有 …），
/// 只回一个空数组等于把它们压成一种，调用方只能瞎猜。
/// </para>
/// </summary>
[Tool(
    id: "workspace_search",
    name: "Search workspace (unified)",
    description: "统一检索入口：按 intent 在代码索引 + 文本面上检索，返回带证据、可解释排序、去重与分页的结果。intent 取值：auto/file/namespace/type/member/comment/implementation/references/callers/callees/any。结果包含 emptyReason（没搜到的真实原因）、nextSteps（下一步怎么办）、degraded（结果可能不完整）。scope 缺省取当前工作区。",
    category: ToolCategory.Query,
    permission: ToolPermissionLevel.Low,
    SortOrder = 40)]
public sealed class WorkspaceSearchTool(IRetrievalPort port) : PuddingToolBase<WorkspaceSearchArgs>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    private readonly IRetrievalPort _port = port ?? throw new ArgumentNullException(nameof(port));

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        WorkspaceSearchArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var query = args.Query?.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return ToolExecutionResult.Fail(
                "query is required — provide the text, symbol name, file path fragment or comment fragment to search for. " +
                "Example: query='RetrievalPortV1', intent='type'",
                status: ToolResultStatuses.ContractError);
        }

        if (!TryParseIntent(args.Intent, out var intent, out var intentError))
        {
            return ToolExecutionResult.Fail(intentError!, status: ToolResultStatuses.ContractError);
        }

        var scopeId = string.IsNullOrWhiteSpace(args.Scope) ? context.WorkspaceId : args.Scope!.Trim();
        if (string.IsNullOrWhiteSpace(scopeId))
        {
            return ToolExecutionResult.Fail(
                "scope is required — pass scope explicitly when the execution context carries no workspace id.",
                status: ToolResultStatuses.ContractError);
        }

        var pageSize = args.PageSize ?? RetrievalBudget.DefaultPageSize;
        if (pageSize < 1 || pageSize > RetrievalBudget.MaxPageSize)
        {
            return ToolExecutionResult.Fail(
                $"pageSize must be within 1..{RetrievalBudget.MaxPageSize} (got {pageSize}).",
                status: ToolResultStatuses.ContractError);
        }

        RetrievalRequest request;
        try
        {
            request = new RetrievalRequest(
                query: query,
                scopeId: scopeId,
                intent: intent,
                pageSize: pageSize,
                cursor: args.Cursor);
        }
        catch (ArgumentException ex)
        {
            return ToolExecutionResult.Fail(ex.Message, status: ToolResultStatuses.ContractError);
        }

        RetrievalResult result;
        try
        {
            result = await _port.RetrieveAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (RetrievalContractViolationException ex)
        {
            // 契约违约是缺陷，不是"没搜到"：显式报错，绝不降级成空结果。
            return ToolExecutionResult.Fail($"retrieval contract violation: {ex.Message}");
        }

        return ToolExecutionResult.Ok(Serialize(result));
    }

    private static string Serialize(RetrievalResult result)
    {
        var hits = result.Hits.Select(h => new
        {
            name = h.SymbolName,
            kind = h.HitKind.ToString(),
            symbolKind = h.SymbolKind.ToString(),
            relation = h.Relation.ToString(),
            confidence = h.Confidence.ToString(),
            score = h.Score,
            why = h.Why,
            matchTargets = h.MatchTargets.ToString(),
            file = h.Evidence.FilePath,
            line = h.Evidence.Line,
            container = h.Container,
        });

        var nextSteps = result.NextSteps.Select(s => new
        {
            kind = s.Kind.ToString(),
            text = s.Text,
            alternative = s.Alternative,
            argument = s.Argument,
        });

        var payload = new
        {
            query = result.Request.Query,
            scope = result.Request.ScopeId,
            intent = result.Request.Intent.ToString(),
            indexVersion = result.IndexVersion,
            totalCount = result.TotalCount,
            returned = result.Hits.Count,
            emptyReason = result.EmptyReason?.ToString(),
            degraded = result.Degraded,
            degradedReason = result.DegradedReason,
            nextCursor = result.NextCursor,
            overflowPath = result.Overflow?.Path,
            nextSteps,
            hits,
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    /// <summary>
    /// intent 解析：非法值必须报错并列出合法值，不能静默退化成 auto ——
    /// 静默退化会让"我明确要 callers"变成"给了我一堆别的"。
    /// </summary>
    private static bool TryParseIntent(string? raw, out RetrievalIntent intent, out string? error)
    {
        intent = RetrievalIntent.Auto;
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
            return true;

        var value = raw.Trim();
        if (Enum.TryParse<RetrievalIntent>(value, ignoreCase: true, out var parsed)
            && Enum.IsDefined(parsed))
        {
            intent = parsed;
            return true;
        }

        error =
            $"unknown intent '{value}'. Valid values: " +
            string.Join(", ", Enum.GetNames<RetrievalIntent>().Select(n => n.ToLowerInvariant()));
        return false;
    }
}

/// <summary><c>workspace_search</c> 的参数。</summary>
public sealed record WorkspaceSearchArgs
{
    [ToolParam("Text, symbol name, file-path fragment or comment fragment to search for. Required.")]
    public string? Query { get; init; }

    [ToolParam("Retrieval intent: auto (default) / file / namespace / type / member / comment / implementation / references / callers / callees / impact / any. Unknown values are rejected, never silently downgraded to auto.")]
    public string? Intent { get; init; }

    [ToolParam("Retrieval scope id. Defaults to the current workspace id from the execution context.")]
    public string? Scope { get; init; }

    [ToolParam("Page size (1..MaxPageSize). Defaults to the platform default page size.")]
    public int? PageSize { get; init; }

    [ToolParam("Opaque page cursor returned as nextCursor by a previous call. Omit for the first page.")]
    public string? Cursor { get; init; }
}
