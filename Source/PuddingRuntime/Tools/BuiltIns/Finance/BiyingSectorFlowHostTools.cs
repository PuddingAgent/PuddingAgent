using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingFinance.Biying;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// 必盈板块/概念工具。全市场股票列表（250,566 字符）**必须带 keyword**，
/// 缺关键词直接拒答——先查再截断等于白烧一次配额。
/// </summary>
[Tool(
    id: "biying_sector",
    name: "必盈板块",
    description: "查询板块与成分：view=tree 指数/行业/概念树（配 keyword 定位）、constituents 按板块代码找成分股、membership 按股票代码找所属板块、stockList 全市场股票列表（**必须带 keyword**，如「平安」或「000001」）。【何时用】找同板块可比公司、看一只票被归到哪些概念、确认行业分类。【怎么用】tree/stockList 建议或必须给 keyword；constituents/membership 需给 code。【坑】stockList 不带 keyword 会被直接拒绝——全市场列表实测 250,566 字符，不能整表返回；tree 也有 178,716 字符，务必带关键词。返回里的 total/returned/truncated 是显式截断标记。",
    category: ToolCategory.Query,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe,
    SortOrder = 63)]
public sealed class BiyingSectorQueryTool(BiyingFinanceRuntime runtime) : PuddingToolBase<BiyingSectorArgs>
{
    private readonly BiyingFinanceRuntime _runtime = runtime;

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        BiyingSectorArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var state = _runtime.Resolve();
        if (!state.IsAvailable)
            return ToolExecutionResult.Fail(state.Error!);

        if (!TryParseView(args.View, out var view))
            return ToolExecutionResult.Fail($"未知的 view「{args.View}」。可用：tree、constituents、membership、stockList。");

        try
        {
            var result = await state.Sector!.QueryAsync(new BiyingSectorRequest
            {
                View = view,
                Code = args.Code,
                Keyword = args.Keyword,
                Limit = args.Limit ?? 20,
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

    private static bool TryParseView(string? value, out BiyingSectorView view)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "tree": view = BiyingSectorView.Tree; return true;
            case "constituents": view = BiyingSectorView.Constituents; return true;
            case "membership": view = BiyingSectorView.Membership; return true;
            case "stocklist": view = BiyingSectorView.StockList; return true;
            default: view = default; return false;
        }
    }
}

/// <summary>必盈板块参数。</summary>
public sealed record BiyingSectorArgs
{
    /// <summary>tree / constituents / membership / stockList。</summary>
    public string? View { get; init; }

    /// <summary>板块代码或股票代码。</summary>
    public string? Code { get; init; }

    /// <summary>关键词过滤（stockList 必填）。</summary>
    public string? Keyword { get; init; }

    public int? Limit { get; init; }
}

/// <summary>
/// 必盈资金/龙虎榜工具。回答的是"谁在买"，不是"值多少钱"——
/// 资金流入不等于基本面改善，两者必须分开看。
/// </summary>
[Tool(
    id: "biying_flow",
    name: "必盈资金",
    description: "查询资金与龙虎榜：view=lhbDaily 龙虎榜每日详情 / lhbStock 个股上榜统计（days 近 N 日）/ stockFlow 个股资金流向 / stockFlowHistory 指定日期资金流向（需 date=yyyyMMdd）。【何时用】判断涨跌是资金驱动还是基本面驱动、看席位结构、核查异动期间的筹码变化。【怎么用】stockFlow 与 stockFlowHistory 需 code；stockFlowHistory 还需 date；lhbStock 用 days 控制回溯天数。【坑】资金面只能说明交易结构，不能推断后续涨跌；日期必须 yyyyMMdd，写错格式会直接报错而不是猜。返回带 total/returned/truncated 截断标记与中文 legend。",
    category: ToolCategory.Query,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe,
    SortOrder = 64)]
public sealed class BiyingFlowQueryTool(BiyingFinanceRuntime runtime) : PuddingToolBase<BiyingFlowArgs>
{
    private readonly BiyingFinanceRuntime _runtime = runtime;

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        BiyingFlowArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var state = _runtime.Resolve();
        if (!state.IsAvailable)
            return ToolExecutionResult.Fail(state.Error!);

        if (!TryParseView(args.View, out var view))
        {
            return ToolExecutionResult.Fail(
                $"未知的 view「{args.View}」。可用：lhbDaily、lhbStock、stockFlow、stockFlowHistory。");
        }

        try
        {
            var result = await state.Flow!.QueryAsync(new BiyingFlowRequest
            {
                View = view,
                Code = args.Code,
                Date = args.Date,
                Days = args.Days ?? 5,
                Limit = args.Limit ?? 10,
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

    private static bool TryParseView(string? value, out BiyingFlowView view)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "lhbdaily": view = BiyingFlowView.LhbDaily; return true;
            case "lhbstock": view = BiyingFlowView.LhbStock; return true;
            case "stockflow": view = BiyingFlowView.StockFlow; return true;
            case "stockflowhistory": view = BiyingFlowView.StockFlowHistory; return true;
            default: view = default; return false;
        }
    }
}

/// <summary>必盈资金参数。</summary>
public sealed record BiyingFlowArgs
{
    /// <summary>lhbDaily / lhbStock / stockFlow / stockFlowHistory。</summary>
    public string? View { get; init; }

    public string? Code { get; init; }

    /// <summary>yyyyMMdd（stockFlowHistory 必填）。</summary>
    public string? Date { get; init; }

    /// <summary>近 N 日（lhbStock，默认 5）。</summary>
    public int? Days { get; init; }

    public int? Limit { get; init; }
}
