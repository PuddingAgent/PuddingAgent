using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingFinance.Biying;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// 必盈任意端点直通，覆盖 market / financial / sector / flow 之外的约 170 个端点。
///
/// 安全边界全部由组件层保证（端点白名单、查询键白名单、强制整形），本类只做参数转换
/// 与错误翻译——不在这里重复实现校验，避免两套错误措辞随版本漂移。
/// </summary>
[Tool(
    id: "biying_raw",
    name: "必盈直通",
    description: "查询必盈注册表里任意端点（覆盖另外四个必盈工具没做的约 170 个：股东、分红、公告、行业财务、Pro 行情等）。【何时用】biying_market / biying_financial / biying_sector / biying_flow 都不覆盖时才用它——先看它们的 view 够不够。【怎么用】endpointId 必填（注册表端点编号，如 255）；pathArgs 按端点模板顺序给路径段（证书不用给）；query 只接受该端点声明的键。【坑】端点必须存在于注册表，随便写编号会当场被拒且不发请求——这是 SSRF 边界而非限制；响应形状不可预知，所以默认只返回 3 行，必须用 fields 投影或 keyword 过滤收窄，否则很快吃满预算。返回带 total/returned/truncated 与中文 legend。",
    category: ToolCategory.Query,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe,
    SortOrder = 65)]
public sealed class BiyingRawQueryTool(BiyingFinanceRuntime runtime) : PuddingToolBase<BiyingRawArgs>
{
    private readonly BiyingFinanceRuntime _runtime = runtime;

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        BiyingRawArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var state = _runtime.Resolve();
        if (!state.IsAvailable)
            return ToolExecutionResult.Fail(state.Error!);

        if (string.IsNullOrWhiteSpace(args.EndpointId))
            return ToolExecutionResult.Fail("必须提供 endpointId（注册表端点编号，如 255）。");

        try
        {
            var result = await state.Raw!.QueryAsync(new BiyingRawRequest
            {
                EndpointId = args.EndpointId.Trim(),
                PathArgs = args.PathArgs,
                Query = args.Query,
                Fields = args.Fields,
                Keyword = args.Keyword,
                Limit = args.Limit ?? 3,
            }, ct).ConfigureAwait(false);

            return ToolExecutionResult.Ok(BiyingMarketQueryTool.WithQuota(result));
        }
        catch (BiyingEndpointException exception)
        {
            return ToolExecutionResult.Fail(exception.Message);
        }
        catch (BiyingCallException exception)
        {
            return ToolExecutionResult.Fail(
                exception.IsRetryable ? $"{exception.Message}（可稍后重试）" : exception.Message);
        }
    }
}

/// <summary>必盈直通参数。</summary>
public sealed record BiyingRawArgs
{
    /// <summary>注册表端点编号（如 255）。</summary>
    public string? EndpointId { get; init; }

    /// <summary>路径参数，按端点模板顺序（证书位不需提供）。</summary>
    public List<string>? PathArgs { get; init; }

    /// <summary>查询串参数，键必须在端点声明集合内。</summary>
    public Dictionary<string, string>? Query { get; init; }

    /// <summary>只保留这些返回字段。</summary>
    public List<string>? Fields { get; init; }

    /// <summary>关键词过滤（超大静态表必需）。</summary>
    public string? Keyword { get; init; }

    /// <summary>返回行数上限，默认 3。</summary>
    public int? Limit { get; init; }
}