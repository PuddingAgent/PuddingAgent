using System.Text.Json;
using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingCodeIndex.Contracts;
using PuddingRetrieval.Contracts;
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
    description: "统一检索入口：按 intent 在代码索引 + 文本面上检索，返回带证据、可解释排序、去重与分页的结果。两种主体二选一：query（按词）；或 symbolRef（code_symbol_search 签发的 token，必须先通过内容指纹校验）—— 给 symbolRef 时 intent 必须是关系面（callers/callees/references/implementation/impact），引擎按符号身份而非名字定位目标。intent 取值：auto/file/namespace/type/member/comment/implementation/references/callers/callees/any。结果包含 emptyReason（没搜到的真实原因）、nextSteps（下一步怎么办）、degraded（结果可能不完整）。scope 缺省取当前工作区。",
    category: ToolCategory.Query,
    permission: ToolPermissionLevel.Low,
    SortOrder = 40)]
public sealed class WorkspaceSearchTool : PuddingToolBase<WorkspaceSearchArgs>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    private readonly IRetrievalPort _port;
    private readonly ISymbolRefSource? _refSource;

    /// <summary>
    /// 构造。
    /// <para>
    /// <b>为什么 refSource 带默认值</b>：工具被程序集扫描登记后，DI 会构造每个
    /// <c>IPuddingTool</c> ⇒ 多一个**无默认值**的依赖就是多一个启动期地雷
    /// （2026-10-29 的 exit -532462766 就是这个死法）。未注册时 ref 请求会得到
    /// 「未接线」的显式拒绝，而不是静默回落到名字检索。
    /// </para>
    /// </summary>
    public WorkspaceSearchTool(
        IRetrievalPort port,
        ISymbolRefSource? refSource = null,
        ICodeIndexStore? indexStore = null,
        ICodeSourceMaintenanceStore? maintenanceStore = null)
    {
        _port = port ?? throw new ArgumentNullException(nameof(port));

        // 与 workspace_open 同一套组合规则：宿主不登记 ISymbolRefSource，但有 indexStore。
        // 不自建就会让线上每一条 ref 都命中「not wired」—— 功能“已交付”但永远跑不到，
        // 正是 2026-10-30 早上那次 hollow completion 的翻版。
        _refSource = refSource
            ?? (indexStore is null ? null : new CodeIndexSymbolRefSource(indexStore, maintenanceStore));
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        WorkspaceSearchArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var query = args.Query?.Trim();
        var refText = args.SymbolRef?.Trim();

        if (string.IsNullOrWhiteSpace(query) && string.IsNullOrWhiteSpace(refText))
        {
            return ToolExecutionResult.Fail(
                "query or symbolRef is required — provide the text, symbol name, file path fragment, comment fragment " +
                "to search for, or a symbol_ref returned by code_symbol_search. " +
                "Example: query='RetrievalPortV1', intent='type'",
                status: ToolResultStatuses.ContractError);
        }

        // 两个都给 = “对谁做”有两个来源（§8.3 不猜）：不静默择优，直接拒。
        if (!string.IsNullOrWhiteSpace(query) && !string.IsNullOrWhiteSpace(refText))
        {
            return ToolExecutionResult.Fail(
                "query and symbolRef are mutually exclusive — a symbol_ref already names the subject; " +
                "passing both would leave two sources of truth for the same question.",
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

        SymbolAnchor? anchor = null;
        if (!string.IsNullOrWhiteSpace(refText))
        {
            var (resolved, resolveError) = await ResolveAnchorAsync(refText!, context.WorkspaceId, ct).ConfigureAwait(false);
            if (resolveError is not null)
            {
                return ToolExecutionResult.Fail(resolveError, status: ToolResultStatuses.ContractError);
            }

            anchor = resolved;
        }

        RetrievalRequest request;
        try
        {
            request = new RetrievalRequest(
                query: query ?? string.Empty,
                scopeId: scopeId,
                intent: intent,
                pageSize: pageSize,
                cursor: args.Cursor,
                anchor: anchor);
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
            claim = result.Claim is null
                ? null
                : new
                {
                    kind = result.Claim.Kind.ToString(),
                    searched = result.Claim.SearchedSurfaces,
                    unsearched = result.Claim.UnsearchedSurfaces,
                    requiredProviders = result.Claim.RequiredProviders,
                },
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
    /// 把 <c>symbol_ref</c> 解析成符号锚点（§8.3：不猜）。
    /// <para>
    /// 三道卡口依次是：①未接线 ⇒ 显式拒绝（不得静默改走名字检索）；
    /// ②token 格式不合法 ⇒ 报错；③**FRESH 校验不过** ⇒ 报陈旧 + 重定位候选。
    /// 只有三道都过，才允许把身份交给引擎。
    /// </para>
    /// </summary>
    private async Task<(SymbolAnchor? Anchor, string? Error)> ResolveAnchorAsync(
        string token,
        string workspaceId,
        CancellationToken cancellationToken)
    {
        if (_refSource is null)
        {
            return (null,
                "symbolRef is not wired in this build (no index-side ref source registered); use query instead.");
        }

        if (!SymbolRefV1.TryParse(token, out var reference, out var parseError) || reference is null)
        {
            return (null, $"symbolRef is malformed: {parseError}");
        }

        var contentHash = await _refSource
            .GetFileContentHashAsync(workspaceId, reference.ProjectId, reference.FilePath, cancellationToken)
            .ConfigureAwait(false);

        var candidates = await _refSource
            .FindSymbolCandidatesAsync(workspaceId, reference.ProjectId, reference.SymbolId, cancellationToken)
            .ConfigureAwait(false);

        var freshness = SymbolRefFreshness.Evaluate(reference, contentHash, candidates);
        if (freshness.State != SymbolRefState.Fresh)
        {
            // "找到候选"不等于"旧 ref 仍有效"：候选只作提示，绝不用它静默替换目标。
            var hint = freshness.CanRelocate
                ? $" Relocation candidate: {freshness.Relocations[0].FilePath}:{freshness.Relocations[0].StartLine}."
                : string.Empty;

            return (null, $"symbolRef is stale — {freshness.Reason}{hint} Re-search by name with query instead.");
        }

        return (new SymbolAnchor(reference.ProjectId, reference.SymbolId), null);
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
    [ToolParam("Text, symbol name, file-path fragment or comment fragment to search for. Required unless symbolRef is given.")]
    public string? Query { get; init; }

    [ToolParam("A symbol_ref token returned by code_symbol_search. Mutually exclusive with query. Requires a relation-face intent (callers/callees/references/implementation/impact); the engine resolves the target by identity instead of by name.")]
    public string? SymbolRef { get; init; }

    [ToolParam("Retrieval intent: auto (default) / file / namespace / type / member / comment / implementation / references / callers / callees / impact / any. Unknown values are rejected, never silently downgraded to auto.")]
    public string? Intent { get; init; }

    [ToolParam("Retrieval scope id. Defaults to the current workspace id from the execution context.")]
    public string? Scope { get; init; }

    [ToolParam("Page size (1..MaxPageSize). Defaults to the platform default page size.")]
    public int? PageSize { get; init; }

    [ToolParam("Opaque page cursor returned as nextCursor by a previous call. Omit for the first page.")]
    public string? Cursor { get; init; }
}
